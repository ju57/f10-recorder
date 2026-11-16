using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace F10Recorder;

// Real default Cache/ledger/storage locks, controlled nonmedia exporter. No model
// is transferred and no codec is launched; this cannot qualify ordinary Save.
internal static class ReplayCacheGuardCosts
{
    sealed record CallbackInterval(long StartedQpc, long EndedQpc);
    // Match the qualification collector's native namespace semantics, but not
    // its Sampler/publication hooks. Its custom type disables Cache's native
    // sealed-snapshot reader, just as RetainFiles does. No fault interception.
    sealed class RetentionShapeFiles : ReplayFiles
    {
        internal override ReplayNamespaceIdentity ObserveNamespace(string path, ReplayFileExtent enumerated)
            => new(enumerated.Volume, enumerated.Identity);
    }
    static void Need(bool value, string message) { if (!value) throw new IOException(message); }
    static string Hash(string path) { using var input = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(input)); }
    sealed class Source(string directory, ReplayVolumeCapacity volume) : IReplaySessionSource
    {
        public ReplaySessionInfo ReplayInfo { get; } = new(directory, Path.Combine(directory, "segments.csv"), 300, 20, 60, "h264_nvenc", ".ts", false, false);
        public bool Stopped => false;
        // Frozen originals deny deletion through every alias. Preserve fixture
        // namespaces until their leases close; this is not ordinary retirement.
        public bool PreserveEvidence => true;
        internal bool UnknownMaintenance;
        public bool OutputProducerStopped => !UnknownMaintenance;
        public string OutputDemandIdentity => "controlled-cache-guard-cost-v1";
        public string Diagnostics => "Controlled metadata-only source; no media parsed.";
        public double SaveCutoffAt(long timestamp) => 300.5;
        public void FreezeAt(long timestamp) { }
        public void StopCapture() { }
        public void CheckCaptureHealth(double end) { }
        public void Dispose() { }
        public ReplayOutputDemand EstimateOutput(ReplayOutputSelection selection) => new(ReplayOutputEvidenceKind.CalibratedEstimate,
            "controlled-numeric-fixture-not-model", selection.Identity, selection.ExporterIdentity, volume.Volume, volume.AllocationUnit,
            16_777_216, true, null, ReplayReferenceModel.Round(1_100_000_000, volume.AllocationUnit));
        public void Export(IReadOnlyList<string> arguments, string snapshot) => throw new IOException("Controlled request overload required.");
        internal Action<ReplayExportRequest, Action> Work;
        public void Export(ReplayExportRequest request, string snapshot, Action<ReplayProducerProof> changed, Action check) => Work(request, check);
        public ReplayValidation ValidateOutput(ReplayExportRequest request, Action<ReplayProducerProof> changed, Action check)
        { check(); return new("ControlledMetadataFixture", 0, 0); }
    }
    internal static int Run(string planFile, string expectedHash)
    {
        byte[] raw = File.ReadAllBytes(planFile);
        Need(raw.Length <= 65536 && Convert.ToHexString(SHA256.HashData(raw)) == expectedHash, "Exact reviewed guard-cost plan required.");
        using var doc = JsonDocument.Parse(raw); var p = doc.RootElement;
        bool providersCompared = p.GetProperty("kind").GetString() == "issue83-cache-inventory-provider-cost-v1";
        int[] order = p.GetProperty("order").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        string[] providers = providersCompared ? p.GetProperty("providers").EnumerateArray().Select(x => x.GetString()).ToArray()
            : Enumerable.Repeat("default", 6).ToArray();
        Need((providersCompared || p.GetProperty("kind").GetString() == "issue83-cache-maintenance-cost-v1")
            && p.GetProperty("seconds").GetInt32() == 60 && p.GetProperty("callbacks").GetInt32() == 256
            && order.SequenceEqual(providersCompared ? new[] { 0, 0, 0, 0 } : new[] { 0, 100, 250, 250, 100, 0 })
            && (!providersCompared || providers.SequenceEqual(new[] { "default", "retention-shape", "retention-shape", "default" }))
            && DateTime.UtcNow.AddSeconds(80) < p.GetProperty("expiresUtc").GetDateTime(), "Finite component schedule changed/expired.");
        foreach (var file in p.GetProperty("files").EnumerateObject()) Need(Hash(file.Name) == file.Value.GetString(), "Guard-cost binding changed: " + file.Name);
        string root = Path.GetFullPath(p.GetProperty("output").GetString()), inputRoot = Path.GetFullPath(p.GetProperty("sourceDirectory").GetString());
        string prefix = Path.GetFullPath("artifacts") + Path.DirectorySeparatorChar;
        Need(root.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && inputRoot.StartsWith(prefix, StringComparison.OrdinalIgnoreCase), "Task-owned component scopes required.");
        Need(!Directory.Exists(root) && !File.Exists(root)
            && !root.StartsWith(inputRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !inputRoot.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !root.Equals(inputRoot, StringComparison.OrdinalIgnoreCase), "Fresh disjoint component output required.");
        using var owned = PilotStorageObservation.Create(root);
        var clock = Stopwatch.StartNew(); var rows = new List<object>(); var cleanupErrors = new List<string>(); Exception failure = null;
        using var parent = Process.GetCurrentProcess(); long metricAt = 0, storageAt = 0, peakNative = 0;
        double metricMilliseconds = 0, storageMilliseconds = 0; int metricQueries = 0, storageQueries = 0;
        var observedRoots = new List<string> { inputRoot };
        void Bound(bool force = false)
        {
            Need(clock.Elapsed.TotalSeconds < 60, "Whole guard-cost component deadline.");
            if (!force && metricAt != 0 && Stopwatch.GetElapsedTime(metricAt).TotalMilliseconds < 100) return;
            metricAt = Stopwatch.GetTimestamp(); parent.Refresh(); metricQueries++;
            Need(parent.WorkingSet64 <= 268_435_456 && parent.HandleCount <= 4096, "Guard-cost process envelope exceeded.");
            metricMilliseconds += Stopwatch.GetElapsedTime(metricAt).TotalMilliseconds;
            if (!force && storageAt != 0 && Stopwatch.GetElapsedTime(storageAt).TotalMilliseconds < 250) return;
            storageAt = Stopwatch.GetTimestamp(); storageQueries++;
            // Atomic private ledgers, directory metadata and bounded reports/logs
            // have a conservative32MiB reserve; native leaves are queried fresh.
            var inventory = ReplayStorage.Inventory(observedRoots, new ReplayFiles(), maximumFiles: 8192, maximumDirectories: 256);
            long native = checked(ReplayStorage.Bytes(inventory) + 33_554_432); peakNative = Math.Max(peakNative, native);
            Need(native <= 1_300_000_000 && ReplayStorage.ObserveCapacity(root).AvailableBytes >= 60_000_000_000,
                "Guard-cost native storage/free-space envelope exceeded.");
            storageMilliseconds += Stopwatch.GetElapsedTime(storageAt).TotalMilliseconds;
        }
        try
        {
            using var frozen = new FrozenCalibrationHistory([inputRoot], 2048, 64);
            Need(frozen.AllocatedBytes <= 1_200_000_000, "Retained component source exceeds1.2GB.");
            string[] originals = Directory.GetFiles(inputRoot, "chunk-*.ts").Order(StringComparer.OrdinalIgnoreCase).Take(301).ToArray();
            Need(originals.Length == 301, "Exactly301 protected original file identities required.");
            int ordinal = 0;
            foreach (int cadence in order)
            {
                Bound(); frozen.Check();
                string cell = Path.Combine(root, "trial-" + ++ordinal), buffer = Path.Combine(cell, "buffer"), session = Path.Combine(buffer, "session"), output = Path.Combine(cell, "output");
                string provider = providers[ordinal - 1];
                Directory.CreateDirectory(session); Directory.CreateDirectory(output);
                observedRoots.Add(buffer); observedRoots.Add(output);
                var lines = new List<string>();
                for (int n = 0; n < originals.Length; n++)
                {
                    string name = "chunk-" + n.ToString("D9") + ".ts", link = Path.Combine(session, name);
                    Need(Native.CreateHardLink(link, originals[n], IntPtr.Zero), "Native component hardlink failed.");
                    lines.Add(FormattableString.Invariant($"{name},{n},{n + 1}"));
                }
                File.WriteAllLines(Path.Combine(session, "segments.csv"), lines);
                var source = new Source(session, ReplayStorage.ObserveCapacity(output));
                var maintenance = new List<ReplayMaintenanceTiming>();
                ReplayFiles cacheFiles = provider == "default" ? new ReplayFiles(ReplayFileMode.Production) : new RetentionShapeFiles();
                var cache = new ReplayCache(cacheFiles, cacheDirectory: buffer, volumeReservations: new ReplayVolumeReservations(Path.Combine(cell, "authority"), 60_000_000_000));
                object rowGate = new(); var intervals = new List<CallbackInterval>();
                cache.MaintenanceObservedForChecks = row => { lock (rowGate) { Need(maintenance.Count < 1024, "Maintenance row bound."); maintenance.Add(row); } };
                var id = cache.RegisterSession(source);
                int completed = 0, attempted = 0; double callbackMs = 0; Exception maintenanceFailure = null;
                int metricsAtSave = metricQueries, storageAtSave = storageQueries;
                double metricMsAtSave = metricMilliseconds, storageMsAtSave = storageMilliseconds;
                parent.Refresh(); int handlesBeforeSave = parent.HandleCount, handlesAtExport = 0, handlesAfterCallbacks = 0;
                source.Work = (request, check) =>
                {
                    File.WriteAllBytes(request.Partial, new byte[4096]);
                    parent.Refresh(); handlesAtExport = parent.HandleCount;
                    using var stop = new ManualResetEventSlim(); using var maintenanceStarted = new ManualResetEventSlim();
                    Task maintainer = cadence == 0 ? Task.CompletedTask : Task.Run(() =>
                    {
                        try
                        {
                            while (!stop.IsSet)
                            {
                                maintenanceStarted.Set();
                                cache.Maintain(id);
                                // Match completion-then-sleep caller cadence.
                                if (stop.Wait(cadence)) break;
                            }
                        }
                        catch (Exception error) { maintenanceFailure = error; }
                    });
                    Exception primary = null;
                    try
                    {
                        if (cadence != 0) Need(maintenanceStarted.Wait(TimeSpan.FromSeconds(2)), "Maintenance arm never started.");
                        for (int n = 0; n < 256; n++)
                        {
                            Bound(); attempted++; long at = Stopwatch.GetTimestamp();
                            try { check(); completed++; }
                            finally
                            {
                                long ended = Stopwatch.GetTimestamp(); callbackMs += Stopwatch.GetElapsedTime(at, ended).TotalMilliseconds;
                                lock (rowGate) intervals.Add(new(at, ended));
                            }
                            if (maintenanceFailure != null) throw new IOException("Concurrent maintenance failed.", maintenanceFailure);
                        }
                        parent.Refresh(); handlesAfterCallbacks = parent.HandleCount;
                    }
                    catch (Exception error) { primary = error; throw; }
                    finally
                    {
                        stop.Set(); bool joined = maintainer.Wait(TimeSpan.FromSeconds(5)); source.UnknownMaintenance = !joined;
                        if (!joined)
                        {
                            try { PersistIncomplete("maintenance-task-unconfirmed", primary); }
                            catch (Exception error) { try { Console.Error.WriteLine("Incomplete diagnostic failed: " + error); } catch { } }
                            finally { AwaitOuterCutoff(); } // Never dispose live owners after a diagnostic write failure.
                        }
                        if (cadence != 0 && primary == null)
                            Need(maintenance.Any(m => intervals.Any(i => i.StartedQpc < m.CompletedQpc && i.EndedQpc > m.GateEnteredQpc)),
                                "No observed overlapping maintenance; cadence comparison is inconclusive.");
                    }
                };
                ReplaySaveResult result = null; Exception trialFailure = null; long started = Stopwatch.GetTimestamp();
                void PersistIncomplete(string reason, Exception primary)
                {
                    object[] frozenMaintenance, frozenIntervals;
                    lock (rowGate) { frozenMaintenance = maintenance.Cast<object>().ToArray(); frozenIntervals = intervals.Cast<object>().ToArray(); }
                    byte[] state = JsonSerializer.SerializeToUtf8Bytes(new { reason, ordinal, provider, cadence, attempted, completed, callbackMs,
                        primaryError = primary?.ToString(), maintenanceError = maintenanceFailure?.ToString(), frozenMaintenance, frozenIntervals,
                        seconds = clock.Elapsed.TotalSeconds, cleanup = "Owned outer cutoff is last resort; no cache/frozen-input disposal while accepted work may continue." },
                        new JsonSerializerOptions { IncludeFields = true });
                    Need(state.Length <= 2_097_152, "Incomplete component report bound.");
                    File.WriteAllBytes(Path.Combine(root, "incomplete-trial-" + ordinal + "-" + reason + ".json"), state);
                }
                static void AwaitOuterCutoff() { while (true) Thread.Sleep(100); }
                try
                {
                    var save = cache.TrySave(id, Stopwatch.GetTimestamp(), output); Need(save.Accepted, save.Rejection);
                    if (!save.Completion.Wait(TimeSpan.FromSeconds(55)))
                    {
                        try { PersistIncomplete("accepted-save-unconfirmed", null); }
                        catch (Exception error) { try { Console.Error.WriteLine("Incomplete diagnostic failed: " + error); } catch { } }
                        finally { AwaitOuterCutoff(); }
                    }
                    result = save.Completion.GetAwaiter().GetResult();
                    Need(result.Succeeded && completed == 256 && maintenanceFailure == null, "Controlled component save failed: " + result.Error);
                }
                catch (Exception error) { trialFailure = error; throw; }
                finally
                {
                    rows.Add(new { ordinal, provider, providerType = cacheFiles.GetType().FullName,
                        nativeSnapshotInventoryEligible = cacheFiles.Mode == ReplayFileMode.Production,
                        cadence, attempted, completed, callbackMs, handlesBeforeSave, handlesAtExport, handlesAfterCallbacks,
                        observerBeforeCleanup = new { metricQueries = metricQueries - metricsAtSave, metricMilliseconds = metricMilliseconds - metricMsAtSave,
                            storageQueries = storageQueries - storageAtSave, storageMilliseconds = storageMilliseconds - storageMsAtSave },
                        milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                        succeeded = result?.Succeeded, timing = result?.Timing, maintenance, intervals, maintenanceError = maintenanceFailure?.ToString(), error = trialFailure?.ToString() });
                    // Persist immutable completed-trial evidence before disposal.
                    byte[] trialBytes = JsonSerializer.SerializeToUtf8Bytes(rows[^1], new JsonSerializerOptions { IncludeFields = true });
                    Need(trialBytes.Length <= 2_097_152, "Completed trial report envelope.");
                    File.WriteAllBytes(Path.Combine(root, "trial-" + ordinal + "-before-cleanup.json"), trialBytes);
                    try { cache.Dispose(); }
                    catch (Exception error)
                    {
                        cleanupErrors.Add("trial" + ordinal + ":" + error);
                        if (trialFailure == null) throw;
                    }
                }
                frozen.Check(); Bound(true);
            }
        }
        catch (Exception error) { failure = error; }
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new { result = failure == null ? providersCompared ? "measured-cache-provider-cost" : "measured-cache-maintenance-cost" : "failed-no-retry", rows,
            seconds = clock.Elapsed.TotalSeconds, metricQueries, metricMilliseconds, storageQueries, storageMilliseconds, peakNative, cleanupErrors, error = failure?.ToString(),
            interpretation = "Real Cache/private ledger,controlled numeric nonmedia exporter;256callbacks per trial,identical linked inputs. Provider comparison changes only native file-provider type/namespace dispatch, not collector Sampler/publication hooks; retention-shaped type has no ObserveInventory override. Fixture PreserveEvidence retains aliases under frozen-original leases. No real catalog/livecapture/ordinary retirement or Saved/qualification claim; no-Maintain is diagnostic only. Maintain.Total excludes checks-only observer work; callbackMs excludes outer Bound observations; total trial wall retains both. Counterbalanced warm components do not establish OS-cold conditions." },
            new JsonSerializerOptions { WriteIndented = true, IncludeFields = true });
        Need(bytes.Length <= 2_097_152, "Guard-cost report envelope.");
        File.WriteAllBytes(Path.Combine(root, failure == null ? "result.json" : "failure.json"), bytes);
        if (failure != null) Console.Error.WriteLine(failure); return failure == null ? 0 : 1;
    }
}
