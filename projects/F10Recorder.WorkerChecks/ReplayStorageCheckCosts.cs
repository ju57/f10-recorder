using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace F10Recorder;

// Real native inventory and private ledger operations; no media is parsed.
internal static class ReplayStorageCheckCosts
{
    static void Require(bool value, string message) { if (!value) throw new IOException(message); }
    static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
    internal static int Run(string planFile, string expectedHash)
    {
        Require(new FileInfo(planFile).Length <= 65536 && Hash(planFile) == expectedHash, "Reviewed storage metadata plan changed.");
        using var document = JsonDocument.Parse(File.ReadAllBytes(planFile)); var p = document.RootElement;
        bool cadence = p.GetProperty("kind").GetString() == "issue83-storage-cadence-cost-v1";
        bool namespaceChecks = cadence && p.TryGetProperty("perCallbackNamespaceAndArtifacts", out var requiredNamespaces) && requiredNamespaces.GetBoolean();
        Require((cadence || p.GetProperty("kind").GetString() == "issue83-storage-check-cost-v1") && p.GetProperty("seconds").GetInt32() == 15
            && p.GetProperty("groups").GetInt32() == 4 && DateTime.UtcNow.AddSeconds(30) < p.GetProperty("expiresUtc").GetDateTime(), "Wrong scope or expired metadata plan.");
        if (cadence) Require(p.GetProperty("routineCallbacksPerTrial").GetInt32() == 64, "Exactly64 routine callbacks required.");
        foreach (var file in p.GetProperty("files").EnumerateObject()) Require(Hash(file.Name) == file.Value.GetString(), "Metadata binding changed: " + file.Name);
        string root = Path.GetFullPath(p.GetProperty("output").GetString()), source = Path.GetFullPath(p.GetProperty("sourceDirectory").GetString()),
            snapshot = Path.GetFullPath(p.GetProperty("snapshotDirectory").GetString());
        Require(root.StartsWith(Path.GetFullPath("artifacts") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && source.StartsWith(Path.GetFullPath("artifacts") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && snapshot.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Task-owned source/output scopes required.");
        using var owned = PilotStorageObservation.Create(root);
        var clock = Stopwatch.StartNew(); var inventoryRows = new List<object>(); var ledgerRows = new List<object>();
        var cadenceRows = new List<object>(); object sourceObservation = null;
        ReplayVolumeReservations reservations = null; string owner = null;
        Exception failure = null; object release = new { attempted = false };
        try
        {
            using var frozen = new FrozenCalibrationHistory([source], 2048, 64);
            Require(frozen.AllocatedBytes <= 1_200_000_000, "Source metadata allocation exceeds1.2GB.");
            sourceObservation = new { frozen.FileCount, frozen.DirectoryCount, frozen.AllocatedBytes };
            using var cached = new ReplaySnapshotInventory(); cached.Seal(snapshot);
            var ordinary = new ReplayFiles();
            Dictionary<string, ReplayFileExtent> baseline = null;
            for (int group = 0; group < 4; group++)
                foreach (bool useSnapshotInventory in new[] { false, true, true, false })
                {
                    Require(clock.Elapsed.TotalSeconds < 15, "Inventory attribution deadline."); frozen.Check();
                    long at = Stopwatch.GetTimestamp(); Dictionary<string, ReplayFileExtent> current;
                    if (useSnapshotInventory) cached.BeginScan();
                    try { current = ReplayStorage.Inventory([source], useSnapshotInventory ? cached : ordinary, maximumFiles: 2048, maximumDirectories: 64); }
                    finally { if (useSnapshotInventory) cached.EndScan(); }
                    double milliseconds = Stopwatch.GetElapsedTime(at).TotalMilliseconds;
                    baseline ??= current;
                    bool equal = current.Count == baseline.Count && current.All(e => baseline.TryGetValue(e.Key, out var old) && old == e.Value);
                    string difference = current.Keys.FirstOrDefault(k => !baseline.TryGetValue(k, out var old) || old != current[k])
                        ?? baseline.Keys.FirstOrDefault(k => !current.ContainsKey(k));
                    inventoryRows.Add(new { group, useSnapshotInventory, milliseconds, files = current.Count, expectedFiles = baseline.Count,
                        cachedHandles = cached.HandleCount, equal,
                        firstDifference = difference == null ? null : new { path = difference,
                            expected = baseline.GetValueOrDefault(difference), actual = current.GetValueOrDefault(difference) } });
                    Require(equal, "Native inventories differ.");
                }
            string authority = Path.Combine(root, "authority"), payload = Path.Combine(root, "dummy"); Directory.CreateDirectory(payload);
            var capacity = ReplayStorage.ObserveCapacity(payload); Require(capacity.AvailableBytes >= 60_000_000_000, "Native free-space floor.");
            var claims = new List<ReplayVolumeClaim>();
            foreach (string name in new[] { "output.dat", "assembly.dat", "boundary0.dat", "boundary1.dat" })
            {
                string path = Path.Combine(payload, name); File.WriteAllBytes(path, new byte[1024]);
                claims.Add(new(path, payload, capacity.Volume, capacity.AllocationUnit, ReplayReferenceModel.Round(262144, capacity.AllocationUnit)));
            }
            reservations = new ReplayVolumeReservations(authority, 60_000_000_000); owner = Guid.NewGuid().ToString("N");
            reservations.Update(owner, claims, ordinary);
            string ledger = Path.Combine(authority, "replay-volume-reservations.json");
            string partial = Path.Combine(payload, "candidate.partial.mp4");
            Dictionary<string, ReplayFileExtent> artifacts = null;
            if (namespaceChecks)
            {
                Directory.CreateDirectory(partial + ".work");
                foreach (string path in new[] { partial, ReplayOutputWorkspace.AssemblyPath(partial),
                    ReplayOutputWorkspace.BoundaryPath(partial, 0), ReplayOutputWorkspace.BoundaryPath(partial, 1) })
                    File.WriteAllBytes(path, new byte[1024]);
                artifacts = ReplayOutputWorkspace.Observe(partial, ordinary);
                artifacts.Add(partial, ReplayStorage.Observe(partial));
            }
            for (int n = 0; n < 8; n++)
            {
                Require(clock.Elapsed.TotalSeconds < 15, "Ledger attribution deadline.");
                string beforeHash = Hash(ledger); var before = ReplayStorage.Observe(ledger);
                long at = Stopwatch.GetTimestamp(); reservations.Update(owner, claims, ordinary);
                double milliseconds = Stopwatch.GetElapsedTime(at).TotalMilliseconds;
                string afterHash = Hash(ledger); var after = ReplayStorage.Observe(ledger);
                ledgerRows.Add(new { n, milliseconds, identicalBytes = beforeHash == afterHash, beforeHash, afterHash,
                    beforeIdentity = before.Identity, afterIdentity = after.Identity, namespaceObjectChanged = before.Identity != after.Identity });
                Require(beforeHash == afterHash, "Identical update changed ledger bytes.");
            }
            if (cadence)
                foreach (bool coalesced in new[] { false, true, true, false })
                {
                    int refreshes = 0, attemptedRefreshes = 0, completedCallbacks = 0, attemptedNamespaces = 0, completedNamespaces = 0;
                    bool finalForcedCompleted = false; Exception trialError = null; var timing = new ReplayStorageCheckTiming();
                    void ObserveNamespaceAndArtifacts()
                    {
                        if (!namespaceChecks) return;
                        attemptedNamespaces++;
                        long before = Stopwatch.GetTimestamp();
                        try
                        {
                            var names = ReplayStorage.InventoryNamespaces([source], ordinary, maximumFiles: 2048, maximumDirectories: 64);
                            Require(names.Count == baseline.Count && names.All(e => baseline.TryGetValue(e.Key, out var old)
                                && old.Volume == e.Value.Volume && old.Identity == e.Value.Identity), "Fresh namespace differs from immutable source.");
                            var currentArtifacts = ReplayOutputWorkspace.Observe(partial, ordinary);
                            currentArtifacts.Add(partial, ReplayStorage.Observe(partial));
                            Require(currentArtifacts.Count == artifacts.Count && currentArtifacts.All(e => artifacts.TryGetValue(e.Key, out var old)
                                && old == e.Value && !names.Values.Any(name => name.Volume == e.Value.Volume && name.Identity == e.Value.Identity)),
                                "Fresh native artifact identity/allocation or source-alias control failed.");
                            completedNamespaces++;
                        }
                        finally
                        {
                            double work = Stopwatch.GetElapsedTime(before).TotalMilliseconds;
                            timing.NamespaceMilliseconds += work;
                            timing.MaximumNamespaceMilliseconds = Math.Max(timing.MaximumNamespaceMilliseconds, work);
                        }
                    }
                    bool Observe()
                    {
                        attemptedRefreshes++;
                        Require(clock.Elapsed.TotalSeconds < 15, "Cadence component deadline."); frozen.Check();
                        var current = ReplayStorage.Inventory([source], ordinary, maximumFiles: 2048, maximumDirectories: 64);
                        Require(current.Count == baseline.Count && current.All(e => baseline.TryGetValue(e.Key, out var old) && old == e.Value), "Cadence inventory changed.");
                        reservations.Update(owner, claims, ordinary); refreshes++; return true;
                    }
                    long at = Stopwatch.GetTimestamp();
                    try
                    {
                        if (coalesced)
                        {
                            var gate = new ReplayStorageObservationGate(Observe, 100, 500, timing);
                            void Callback(bool final)
                            {
                                gate.RequireFreshness();
                                ObserveNamespaceAndArtifacts();
                                if (final) gate.CheckNow(); else gate.Check();
                            }
                            for (int n = 0; n < 64; n++) { Callback(false); completedCallbacks++; }
                            Callback(true); finalForcedCompleted = true;
                        }
                        else
                        {
                            for (int n = 0; n < 65; n++)
                            {
                                long before = Stopwatch.GetTimestamp(); ObserveNamespaceAndArtifacts();
                                long fullBefore = Stopwatch.GetTimestamp();
                                try { Observe(); }
                                finally
                                {
                                    double fullWork = Stopwatch.GetElapsedTime(fullBefore).TotalMilliseconds;
                                    timing.ObservationMilliseconds += fullWork;
                                    timing.MaximumObservationMilliseconds = Math.Max(timing.MaximumObservationMilliseconds, fullWork);
                                }
                                Require(Stopwatch.GetElapsedTime(before).TotalMilliseconds <= 500, "Baseline full observation exceeded500ms.");
                                if (n < 64) completedCallbacks++; else finalForcedCompleted = true;
                            }
                        }
                    }
                    catch (Exception error) { trialError = error; throw; }
                    finally
                    {
                        double milliseconds = Stopwatch.GetElapsedTime(at).TotalMilliseconds;
                        cadenceRows.Add(new { coalesced, routineCallbacks = 64, completedCallbacks, finalForcedCompleted,
                            namespaceChecks, attemptedNamespaces, completedNamespaces, attemptedRefreshes, refreshes, milliseconds, timing, error = trialError?.ToString() });
                    }
                }
            frozen.Check();
            Require(clock.Elapsed.TotalSeconds < 15, "Storage metadata attribution deadline.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        { failure = error; }
        finally
        {
            if (reservations != null && owner != null)
            {
                try { reservations.Release(owner); release = new { attempted = true, released = true }; }
                catch (Exception error)
                { release = new { attempted = true, released = false, error = error.ToString() }; failure = failure == null ? error : new AggregateException(failure, error); }
            }
        }
        if (clock.Elapsed.TotalSeconds >= 15) failure ??= new IOException("Complete metadata work/release exceeded15seconds.");
        Write(root, failure == null ? "result.json" : "failure.json", new { result = failure == null ? "measured-native-storage-check-costs" : "failed-no-retry",
            expectedHash, inventoryRows, ledgerRows, cadenceRows, sourceObservation, release, seconds = clock.Elapsed.TotalSeconds, error = failure?.ToString(),
            interpretation = cadence
                ? "Candidate gate component ABBA on real native inventory/private ledger,64 routine callbacks plus forced final per trial. Optional declared per-callback fresh native namespace and4-artifact observation uses real APIs but is not the complete Cache guard or actual save. Current product includes an unqualified candidate change. No capture/codec/publication, whole-save performance or exact-candidate qualification acceptance."
                : "ABBA native inventory on identical immutable source, then8 actual identical private-ledger updates. Dummy4KiB payload is nonplayable. No capture/codec/publication/model qualification acceptance." });
        if (failure != null) Console.Error.WriteLine(failure.Message);
        return failure == null ? 0 : 1;
    }
    static void Write(string root, string name, object value)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions { WriteIndented = true });
        Require(bytes.Length <= 1048576, "Metadata report exceeds1MiB.");
        using var output = new FileStream(Path.Combine(root, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read); output.Write(bytes);
    }
}
