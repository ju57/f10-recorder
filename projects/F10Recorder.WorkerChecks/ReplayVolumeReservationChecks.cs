using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace F10Recorder;

internal static class ReplayVolumeReservationChecks
{
    // Literal capacity/allocation observations at the existing filesystem seam;
    // ordinary path, identity, protected storage and lock operations remain native.
    sealed class CapacityFiles : ReplayFiles
    {
        internal long Free = 10000, Unit = 1;
        internal readonly Dictionary<string, (string Volume, long Free)> Volumes = new(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, long> Allocations = new(StringComparer.OrdinalIgnoreCase);
        internal string Gate;
        internal bool Unknown, MissingMetadata, MeasureHandles;
        internal int PeakHandles;
        string Volume(string path) => Volumes.FirstOrDefault(v => path.StartsWith(v.Key, StringComparison.OrdinalIgnoreCase)).Value.Volume ?? "volume-a";
        internal override ReplayFileExtent Observe(string path)
        {
            if (Unknown) throw new IOException("Controlled unavailable allocation metadata.");
            if (MissingMetadata) throw new FileNotFoundException("Controlled metadata-open disappearance.");
            var observed = base.Observe(path);
            return observed with { Volume = Volume(path), AllocationUnit = Unit, AllocatedBytes = Allocations.GetValueOrDefault(path, observed.AllocatedBytes) };
        }
        internal override ReplayVolumeCapacity ObserveCapacity(string directory)
        {
            if (MeasureHandles) { using var process = Process.GetCurrentProcess(); PeakHandles = Math.Max(PeakHandles, process.HandleCount); MeasureHandles = false; }
            if (Gate != null)
            {
                File.WriteAllText(Gate + ".ready", "locked");
                WaitFor(() => File.Exists(Gate + ".continue"), "Cross-process gate was not released.");
                Gate = null;
            }
            var entry = Volumes.FirstOrDefault(v => directory.StartsWith(v.Key, StringComparison.OrdinalIgnoreCase)).Value;
            return new(entry.Volume ?? "volume-a", Unit, entry.Volume == null ? Free : entry.Free);
        }
    }

    static void Require(bool condition, string message) { if (!condition) throw new IOException(message); }
    static void Reject(Action action, string expectedReason = null)
    {
        try { action(); }
        catch (IOException ex) when (expectedReason == null || ex.Message.Contains(expectedReason, StringComparison.Ordinal)) { return; }
        catch (UnauthorizedAccessException) when (expectedReason == null) { return; }
        throw new Exception("Expected storage rejection was admitted.");
    }
    static void WaitFor(Func<bool> condition, string message)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition()) { if (elapsed.Elapsed > TimeSpan.FromSeconds(8)) throw new IOException(message); Thread.Sleep(20); }
    }
    static ReplayVolumeClaim Claim(string directory, string name, long peak, string volume = "volume-a") => new(Path.Combine(directory, name), directory, volume, 1, peak);
    static string Owner() => Guid.NewGuid().ToString("N");

    // The ordinary WorkerChecks entry point must route this flag before worker startup.
    internal static int Dispatch(string[] args)
    {
        if (args.Length != 5 || args[0] != "--replay-volume-reservation-child") return 2;
        string root = Path.GetFullPath(args[1]), owner = args[2], role = args[3], gate = Path.GetFullPath(args[4]);
        var files = new CapacityFiles { Gate = role == "first" ? gate : null };
        File.WriteAllText(gate + "." + role + ".started", "started");
        var elapsed = Stopwatch.StartNew();
        try
        {
            new ReplayVolumeReservations(Path.Combine(root, "private")).Update(owner, [Claim(root, role, role == "first" ? 6000 : 4001)], files);
            return 0;
        }
        catch (ReplayVolumeReservationBusyException ex) { File.WriteAllText(gate + ".busy-ms", elapsed.Elapsed.TotalMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)); Console.WriteLine(ex.Message); return 4; }
        catch (IOException ex) { Console.WriteLine(ex.Message); return 3; }
    }

    static Process StartChild(string root, string owner, string role, string gate)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetEntryAssembly().Location);
        foreach (string argument in new[] { "--replay-volume-reservation-child", root, owner, role, gate }) start.ArgumentList.Add(argument);
        return Process.Start(start);
    }

    internal static int Run(string root)
    {
        root = Path.GetFullPath(root);
        Directory.CreateDirectory(root);
        var results = new List<object>(); int failed = 0;
        void Check(string name, Action<string> action)
        {
            string directory = Path.Combine(root, name); Directory.CreateDirectory(directory);
            try { action(directory); results.Add(new { name, passed = true }); }
            catch (Exception ex) { failed++; results.Add(new { name, passed = false, error = ex.ToString() }); }
        }
        Check("literal-same-volume-exact-fit-and-one-over", directory =>
        {
            var files = new CapacityFiles(); var store = new ReplayVolumeReservations(Path.Combine(directory, "private"));
            store.Update(Owner(), [Claim(directory, "held", 6000)], files);
            string owner = Owner();
            Reject(() => store.Update(owner, [Claim(directory, "incoming", 4001)], files));
            store.Update(owner, [Claim(directory, "incoming", 4000)], files);
        });
        Check("different-volume-cannot-subsidize", directory =>
        {
            string other = Path.Combine(directory, "other"); Directory.CreateDirectory(other);
            var files = new CapacityFiles(); files.Volumes[other] = ("volume-b", 4000);
            var store = new ReplayVolumeReservations(Path.Combine(directory, "private"));
            store.Update(Owner(), [Claim(directory, "held", 6000)], files);
            string owner = Owner();
            Reject(() => store.Update(owner, [Claim(other, "incoming", 4001, "volume-b")], files));
            store.Update(owner, [Claim(other, "incoming", 4000, "volume-b")], files);
        });
        Check("materialized-prefix-charged-once", directory =>
        {
            var files = new CapacityFiles(); var store = new ReplayVolumeReservations(Path.Combine(directory, "private"));
            store.Update(Owner(), [Claim(directory, "held", 6000)], files);
            string held = Path.Combine(directory, "held"); File.WriteAllBytes(held, new byte[1]); files.Allocations[held] = 2000; files.Free = 8000;
            store.Update(Owner(), [Claim(directory, "incoming", 4000)], files);
            Reject(() => store.Update(Owner(), [Claim(directory, "over", 1)], files));
        });
        Check("second-instance-retains-orphan-obligation", directory =>
        {
            var files = new CapacityFiles(); string security = Path.Combine(directory, "private");
            new ReplayVolumeReservations(security).Update(Owner(), [Claim(directory, "orphan", 6000)], files);
            Reject(() => new ReplayVolumeReservations(security).Update(Owner(), [Claim(directory, "incoming", 4001)], files));
            new ReplayVolumeReservations(security).Update(Owner(), [Claim(directory, "fits", 4000)], files);
        });
        Check("materialized-disappearance-and-replacement-retain-obligation", directory =>
        {
            var files = new CapacityFiles(); var store = new ReplayVolumeReservations(Path.Combine(directory, "private"));
            string held = Path.Combine(directory, "held"), owner = Owner(); File.WriteAllBytes(held, new byte[1]); files.Allocations[held] = 2000;
            store.Update(owner, [Claim(directory, "held", 6000)], files);
            File.Move(held, held + ".old");
            Reject(() => store.Update(Owner(), [Claim(directory, "incoming", 1)], files));
            File.WriteAllBytes(held, new byte[1]);
            Reject(() => store.Update(Owner(), [Claim(directory, "incoming", 1)], files));
            store.Release(owner);
            store.Update(Owner(), [Claim(directory, "incoming", 10000)], files);
            Require(File.Exists(held) && File.Exists(held + ".old"), "Explicit release deleted payloads.");
        });
        Check("unknown-holder-metadata-never-erases-claims", directory =>
        {
            var files = new CapacityFiles(); var store = new ReplayVolumeReservations(Path.Combine(directory, "private"));
            string held = Path.Combine(directory, "held"); File.WriteAllBytes(held, new byte[1]); files.Allocations[held] = 0;
            store.Update(Owner(), [Claim(directory, "held", 6000)], files);
            files.Unknown = true;
            Reject(() => store.Update(Owner(), [Claim(directory, "incoming", 4000)], files));
            files.Unknown = false;
            Reject(() => store.Update(Owner(), [Claim(directory, "incoming", 4001)], files));
        });
        Check("rejected-proposal-retains-first-materialized-identity", directory =>
        {
            var files = new CapacityFiles(); var store = new ReplayVolumeReservations(Path.Combine(directory, "private"));
            store.Update(Owner(), [Claim(directory, "held", 6000)], files);
            string held = Path.Combine(directory, "held"); File.WriteAllBytes(held, new byte[1]); files.Allocations[held] = 2000; files.Free = 8000;
            Reject(() => store.Update(Owner(), [Claim(directory, "incoming", 4001)], files));
            File.Move(held, held + ".old");
            Reject(() => store.Update(Owner(), [Claim(directory, "next", 1)], files));
            File.WriteAllBytes(held, new byte[1]);
            Reject(() => store.Update(Owner(), [Claim(directory, "next", 1)], files));
        });
        Check("observed-leaf-metadata-disappearance-is-uncertainty", directory =>
        {
            var files = new CapacityFiles(); var store = new ReplayVolumeReservations(Path.Combine(directory, "private"));
            store.Update(Owner(), [Claim(directory, "held", 6000)], files);
            File.WriteAllBytes(Path.Combine(directory, "held"), new byte[1]);
            files.MissingMetadata = true;
            Reject(() => store.Update(Owner(), [Claim(directory, "incoming", 1)], files));
        });
        Check("failed-persist-preserves-prior-ledger", directory =>
        {
            var files = new CapacityFiles(); string security = Path.Combine(directory, "private"); var store = new ReplayVolumeReservations(security);
            store.Update(Owner(), [Claim(directory, "held", 6000)], files);
            using (var reader = File.Open(Path.Combine(security, "replay-volume-reservations.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
                Reject(() => store.Update(Owner(), [Claim(directory, "incoming", 4000)], files));
            var reopened = new ReplayVolumeReservations(security);
            Reject(() => reopened.Update(Owner(), [Claim(directory, "incoming", 4001)], files));
            reopened.Update(Owner(), [Claim(directory, "incoming", 4000)], files);
        });
        Check("release-only-named-owner", directory =>
        {
            var files = new CapacityFiles(); var store = new ReplayVolumeReservations(Path.Combine(directory, "private")); string first = Owner();
            store.Update(first, [Claim(directory, "first", 6000)], files);
            store.Update(Owner(), [Claim(directory, "second", 4000)], files);
            store.Release(first);
            Reject(() => store.Update(Owner(), [Claim(directory, "incoming", 6001)], files));
            store.Update(Owner(), [Claim(directory, "incoming", 6000)], files);
        });
        Check("same-path-overlap-rejected", directory =>
        {
            var files = new CapacityFiles(); var store = new ReplayVolumeReservations(Path.Combine(directory, "private"));
            store.Update(Owner(), [Claim(directory, "same", 100)], files);
            Reject(() => store.Update(Owner(), [Claim(directory, "same", 100)], files));
        });
        Check("native-hardlink-alias-growth-rejected", directory =>
        {
            var files = new CapacityFiles(); var store = new ReplayVolumeReservations(Path.Combine(directory, "private"));
            string first = Path.Combine(directory, "first"), second = Path.Combine(directory, "second"); File.WriteAllBytes(first, new byte[1]);
            Require(Native.CreateHardLink(second, first, IntPtr.Zero), "Hardlink fixture could not be established.");
            files.Allocations[first] = 2000; files.Allocations[second] = 2000;
            store.Update(Owner(), [Claim(directory, "first", 6000)], files);
            Reject(() => store.Update(Owner(), [Claim(directory, "second", 6000)], files));
        });
        Check("native-hardlink-fully-materialized-no-double-charge", directory =>
        {
            var files = new CapacityFiles { Free = 0 }; var store = new ReplayVolumeReservations(Path.Combine(directory, "private"));
            string first = Path.Combine(directory, "first"), second = Path.Combine(directory, "second"); File.WriteAllBytes(first, new byte[1]);
            Require(Native.CreateHardLink(second, first, IntPtr.Zero), "Hardlink fixture could not be established.");
            files.Allocations[first] = 2000; files.Allocations[second] = 2000;
            store.Update(Owner(), [Claim(directory, "first", 2000)], files);
            store.Update(Owner(), [Claim(directory, "second", 2000)], files);
        });
        Check("native-volume-allocation-and-protected-ledger", directory =>
        {
            string payload = Path.Combine(directory, "native"); File.WriteAllBytes(payload, new byte[8192]);
            var capacity = ReplayStorage.ObserveCapacity(directory); var extent = ReplayStorage.Observe(payload);
            var store = new ReplayVolumeReservations(Path.Combine(directory, "private"));
            string owner = Owner();
            store.Update(owner, [new(payload, directory, capacity.Volume, capacity.AllocationUnit, extent.AllocatedBytes)], new ReplayFiles());
            store.Release(owner);
            Require(File.ReadAllBytes(payload).Length == 8192, "Native release changed the payload.");
        });
        Check("wrong-volume-unit-and-nonordinary-leaf-rejected", directory =>
        {
            var files = new CapacityFiles(); var store = new ReplayVolumeReservations(Path.Combine(directory, "private"));
            Reject(() => store.Update(Owner(), [Claim(directory, "wrong-volume", 100, "volume-b")], files));
            Reject(() => store.Update(Owner(), [Claim(directory, "wrong-unit", 100) with { AllocationUnit = 2 }], files));
            Directory.CreateDirectory(Path.Combine(directory, "not-file"));
            Reject(() => store.Update(Owner(), [Claim(directory, "not-file", 100)], files));
            store.Update(Owner(), [Claim(directory, "valid", 10000)], files);
        });
        Check("same-volume-directories-combine-growth", directory =>
        {
            string other = Path.Combine(directory, "other"); Directory.CreateDirectory(other);
            var files = new CapacityFiles(); var store = new ReplayVolumeReservations(Path.Combine(directory, "private"));
            store.Update(Owner(), [Claim(directory, "held", 6000)], files);
            Reject(() => store.Update(Owner(), [Claim(other, "incoming", 4001)], files));
            store.Update(Owner(), [Claim(other, "incoming", 4000)], files);
        });
        Check("two-thirty-minute-profiles-at-realistic-path-length", directory =>
        {
            string first = Path.Combine(directory, "first-" + new string('a', Math.Max(1, 235 - directory.Length - 7)));
            string second = Path.Combine(directory, "second-" + new string('b', Math.Max(1, 235 - directory.Length - 8)));
            TrustedStorage.CreatePrivateDirectory(first); TrustedStorage.CreatePrivateDirectory(second);
            ReplayVolumeClaim[] Profile(string target) => Enumerable.Range(0, 1800)
                .Select(i => Claim(target, "chunk-" + i.ToString("D9") + ".ts", 1)).Append(Claim(target, "concat.txt", 1)).ToArray();
            var files = new CapacityFiles { Free = 3602, MeasureHandles = true };
            string security = Path.Combine(directory, "private"), owner = Owner();
            var store = new ReplayVolumeReservations(security);
            using var process = Process.GetCurrentProcess(); int handlesBefore = process.HandleCount;
            var firstClaims = Profile(first); var secondClaims = Profile(second);
            store.Update(Owner(), firstClaims, files);
            string ledger = Path.Combine(security, "replay-volume-reservations.json"); long oneProfileLedgerBytes = new FileInfo(ledger).Length;
            files.MeasureHandles = true;
            Reject(() => store.Update(owner, secondClaims.Select(c => Path.GetFileName(c.Path) == "concat.txt" ? c with { PeakAllocatedBytes = 2 } : c).ToArray(), files));
            files.MeasureHandles = true;
            new ReplayVolumeReservations(security).Update(owner, secondClaims, files);
            long twoProfileLedgerBytes = new FileInfo(ledger).Length;
            Require(!Directory.EnumerateFiles(first).Any() && !Directory.EnumerateFiles(second).Any(), "Future-claim profile fixture allocated payload files.");
            results.Add(new { name = "maximum-profile-cost", claims = 3602, oneProfileLedgerBytes, twoProfileLedgerBytes,
                shortestClaimPath = firstClaims.Min(c => c.Path.Length), longestClaimPath = firstClaims.Max(c => c.Path.Length),
                handlesBefore, peakHandles = files.PeakHandles, handleDelta = files.PeakHandles - handlesBefore });
        });
        Check("unreserved-literal-capacity-and-materialized-prefix", directory =>
        {
            var files = new CapacityFiles(); var store = new ReplayVolumeReservations(Path.Combine(directory, "private"));
            store.Update(Owner(), [Claim(directory, "held", 6000)], files);
            var before = store.ObserveUnreserved(directory, files);
            Require(before == ("volume-a", 1L, 4000L), "10000 free minus6000 remaining must report4000 unreserved.");
            string held = Path.Combine(directory, "held"); File.WriteAllBytes(held, new byte[1]); files.Allocations[held] = 2000; files.Free = 8000;
            var after = new ReplayVolumeReservations(Path.Combine(directory, "private")).ObserveUnreserved(directory, files);
            Require(after == ("volume-a", 1L, 4000L), "2000 materialized plus fresh8000 must leave4000 unreserved.");
            files.Free = 3999;
            Require(store.ObserveUnreserved(directory, files).Bytes == -1, "A deficit must remain negative, never clamp to zero.");
            File.Move(held, held + ".old");
            Reject(() => store.ObserveUnreserved(directory, files));
        });
        Check("unreserved-excludes-other-volume-growth", directory =>
        {
            string other = Path.Combine(directory, "other"); Directory.CreateDirectory(other);
            var files = new CapacityFiles(); files.Volumes[other] = ("volume-b", 5000);
            var store = new ReplayVolumeReservations(Path.Combine(directory, "private"));
            store.Update(Owner(), [Claim(directory, "held", 6000)], files);
            Require(store.ObserveUnreserved(other, files) == ("volume-b", 1L, 5000L), "Another volume's6000 promise reduced this volume's5000 free.");
        });
        Check("unreserved-unknown-metadata-and-identity-reject", directory =>
        {
            var files = new CapacityFiles(); var store = new ReplayVolumeReservations(Path.Combine(directory, "private"));
            string held = Path.Combine(directory, "held"); File.WriteAllBytes(held, new byte[1]); files.Allocations[held] = 2000;
            store.Update(Owner(), [Claim(directory, "held", 6000)], files);
            string other = Path.Combine(directory, "other"); Directory.CreateDirectory(other); files.Volumes[other] = ("volume-b", 5000);
            files.Unknown = true; Reject(() => store.ObserveUnreserved(other, files)); files.Unknown = false;
            File.Move(held, held + ".old"); File.WriteAllBytes(held, new byte[1]);
            Reject(() => store.ObserveUnreserved(directory, files));
        });
        Check("grouped-future-growth-updates-and-reopens", directory =>
        {
            var files = new CapacityFiles(); string security = Path.Combine(directory, "private"), owner = Owner();
            var store = new ReplayVolumeReservations(security);
            var group = Claim(directory, ".known-input-growth", 6000) with { Kind = ReplayVolumeClaimKind.UnmaterializedGrowth };
            store.Update(owner, [group], files);
            Require(store.ObserveUnreserved(directory, files).Bytes == 4000, "Scalar6000 future growth did not reserve6000.");
            files.Free = 8000;
            store.Update(owner, [group with { PeakAllocatedBytes = 4000 }], files);
            string incoming = Owner(); var reopened = new ReplayVolumeReservations(security);
            Reject(() => reopened.Update(incoming, [Claim(directory, "output", 4001)], files));
            reopened.Update(incoming, [Claim(directory, "output", 4000)], files);
        });
        Check("group-logical-key-file-never-offsets-future-growth", directory =>
        {
            var files = new CapacityFiles(); var store = new ReplayVolumeReservations(Path.Combine(directory, "private"));
            string logical = Path.Combine(directory, ".known-input-growth"); File.WriteAllBytes(logical, new byte[1]); files.Allocations[logical] = 2000;
            store.Update(Owner(), [Claim(directory, ".known-input-growth", 6000) with { Kind = ReplayVolumeClaimKind.UnmaterializedGrowth }], files);
            Require(store.ObserveUnreserved(directory, files).Bytes == 4000, "A physical file at a logical group key reduced its future commitment.");
            files.Unknown = true;
            Reject(() => store.Update(Owner(), [Claim(directory, "incoming", 4001)], files));
            store.Update(Owner(), [Claim(directory, "fits", 4000)], files);
        });
        Check("reservation-kind-changes-and-unknown-values-reject", directory =>
        {
            var files = new CapacityFiles(); var store = new ReplayVolumeReservations(Path.Combine(directory, "private")); string owner = Owner();
            var file = Claim(directory, "same", 6000);
            store.Update(owner, [file], files);
            Reject(() => store.Update(owner, [file with { Kind = ReplayVolumeClaimKind.UnmaterializedGrowth }], files));
            store.Release(owner);
            store.Update(owner, [file with { Kind = ReplayVolumeClaimKind.UnmaterializedGrowth }], files);
            Reject(() => store.Update(owner, [file], files));
            Reject(() => store.Update(Owner(), [Claim(directory, "unknown", 0) with { Kind = (ReplayVolumeClaimKind)999 }], files));
            Reject(() => store.Update(Owner(), [Claim(directory, "over", 4001)], files));
        });
        Check("group-volume-and-anchor-errors-preserve-obligation", directory =>
        {
            string target = Path.Combine(directory, "snapshot"); Directory.CreateDirectory(target);
            var files = new CapacityFiles(); string security = Path.Combine(directory, "private"); var store = new ReplayVolumeReservations(security);
            store.Update(Owner(), [Claim(target, ".known-input-growth", 6000) with { Kind = ReplayVolumeClaimKind.UnmaterializedGrowth }], files);
            files.Volumes[target] = ("changed-volume", 10000);
            Reject(() => new ReplayVolumeReservations(security).ObserveUnreserved(directory, files));
            files.Volumes.Clear();
            Require(store.ObserveUnreserved(directory, files).Bytes == 4000, "Volume failure erased the grouped obligation.");
            Directory.Move(target, target + ".old"); Directory.CreateDirectory(target);
            Reject(() => new ReplayVolumeReservations(security).ObserveUnreserved(directory, files));
        });
        Check("legacy-ledger-missing-kind-defaults-to-file-peak", directory =>
        {
            var files = new CapacityFiles(); string security = Path.Combine(directory, "private"); var store = new ReplayVolumeReservations(security);
            string held = Path.Combine(directory, "held"); File.WriteAllBytes(held, new byte[1]); files.Allocations[held] = 2000;
            store.Update(Owner(), [Claim(directory, "held", 6000)], files);
            string ledger = Path.Combine(security, "replay-volume-reservations.json");
            string text = File.ReadAllText(ledger); Require(text.Contains("\"Kind\":0"), "Default kind was not serialized in the new fixture.");
            File.WriteAllText(ledger, text.Replace(",\"Kind\":0", ""));
            Require(new ReplayVolumeReservations(security).ObserveUnreserved(directory, files).Bytes == 6000, "Legacy missingKind stopped using its2000 allocated prefix.");
        });
        Check("group-subunit-remaining-is-not-rounded-again", directory =>
        {
            // A4096-byte planned allocation with512 already materialized leaves
            // the independent literal3584 futurebytes, regardless ofunit4096.
            var files = new CapacityFiles { Free = 3584, Unit = 4096 };
            var store = new ReplayVolumeReservations(Path.Combine(directory, "private"));
            var group = Claim(directory, ".known-input-growth", 3584) with { AllocationUnit = 4096, Kind = ReplayVolumeClaimKind.UnmaterializedGrowth };
            Reject(() => store.Update(Owner(), [group with { Path = Path.Combine(directory, "file-peak"), Kind = ReplayVolumeClaimKind.FilePeak }], files));
            store.Update(Owner(), [group], files);
            Require(store.ObserveUnreserved(directory, files) == ("volume-a", 4096L, 0L), "Subunitremaining was rounded orchargedtwice instead ofexact3584.");
        });
        Check("volume-floor-once-across-two-owners", directory =>
        {
            var files = new CapacityFiles { Free = 6100 };
            var store = new ReplayVolumeReservations(Path.Combine(directory, "private"), 100);
            store.Update(Owner(), [Claim(directory, "first", 3000)], files);
            string owner = Owner();
            store.Update(owner, [Claim(directory, "second", 3000)], files);
            Require(store.ObserveUnreserved(directory, files).Bytes == 100, "The observation unexpectedly subtracted thefloor again.");
            Reject(() => store.Update(owner, [Claim(directory, "second", 3001)], files));
            Require(store.ObserveUnreserved(directory, files).Bytes == 100, "Rejectedflooroverrun changed acceptedgrowth.");
        });
        Check("exclude-only-named-owner-still-validates-its-identity", directory =>
        {
            var files = new CapacityFiles(); var store = new ReplayVolumeReservations(Path.Combine(directory, "private"));
            string first = Owner(), second = Owner(), held = Path.Combine(directory, "first");
            File.WriteAllBytes(held, new byte[1]); files.Allocations[held] = 2000;
            store.Update(first, [Claim(directory, "first", 6000)], files);
            store.Update(second, [Claim(directory, "second", 3000)], files);
            Require(store.ObserveUnreserved(directory, files, first).Bytes == 7000, "ExcludingA incorrectly retainedA or excludedB.");
            Require(store.ObserveUnreserved(directory, files, second).Bytes == 6000, "ExcludingB incorrectly retainedB or excludedA.");
            Require(store.ObserveUnreserved(directory, files, Owner()).Bytes == 3000, "Unknownvalidowner excluded anotherholder.");
            Reject(() => store.ObserveUnreserved(directory, files, "not-guid"));
            files.Unknown = true; Reject(() => store.ObserveUnreserved(directory, files, first)); files.Unknown = false;
            File.Move(held, held + ".old"); File.WriteAllBytes(held, new byte[1]);
            Reject(() => store.ObserveUnreserved(directory, files, first));
        });
        Check("owner-bound-preserves-ledger", directory =>
        {
            var files = new CapacityFiles(); string security = Path.Combine(directory, "private"); var store = new ReplayVolumeReservations(security);
            for (int i = 0; i < 128; i++) store.Update(Owner(), [Claim(directory, i.ToString(), 0)], files);
            Reject(() => store.Update(Owner(), [Claim(directory, "over", 0)], files));
            new ReplayVolumeReservations(security).Release(Owner());
        });
        Check("claim-bound-and-byte-bound-before-replacement", directory =>
        {
            var files = new CapacityFiles(); var store = new ReplayVolumeReservations(Path.Combine(directory, "private")); string owner = Owner();
            store.Update(owner, [Claim(directory, "held", 6000)], files);
            Reject(() => store.Update(owner, Enumerable.Range(0, 8193).Select(i => Claim(directory, i.ToString(), 0)).ToArray(), files), "exceeds its bound");
            string longMissing = directory;
            for (int i = 0; i < 12; i++) longMissing = Path.Combine(longMissing, new string('x', 80));
            Reject(() => store.Update(owner, Enumerable.Range(0, 8192).Select(i => Claim(longMissing, i.ToString(), 0)).ToArray(), files), "byte bound");
            Reject(() => store.Update(Owner(), [Claim(directory, "incoming", 4001)], files));
        });
        Check("cross-process-contention-rejects-then-fresh-call-combines", directory =>
        {
            string gate = Path.Combine(directory, "gate");
            using var first = StartChild(directory, Owner(), "first", gate);
            Process second = null;
            try
            {
                WaitFor(() => File.Exists(gate + ".ready"), "First process did not enter its locked capacity check.");
                second = StartChild(directory, Owner(), "second", gate);
                WaitFor(() => File.Exists(gate + ".second.started"), "Second process did not start.");
                Require(second.WaitForExit(1000) && second.ExitCode == 4, "Contending process did not promptly return the distinct busy outcome.");
                File.WriteAllText(gate + ".continue", "continue");
                Require(first.WaitForExit(8000) && first.ExitCode == 0, "First cross-process reservation failed: " + first.StandardError.ReadToEnd());
                var store = new ReplayVolumeReservations(Path.Combine(directory, "private")); var files = new CapacityFiles();
                Require(store.ObserveUnreserved(directory, files).Bytes == 4000, "Busy attempt changed the accepted ledger.");
                string owner = Owner();
                Reject(() => store.Update(owner, [Claim(directory, "fresh", 4001)], files));
                store.Update(owner, [Claim(directory, "fresh", 4000)], files);
                results.Add(new { name = "busy-lock-observed-cost", elapsedMilliseconds = double.Parse(File.ReadAllText(gate + ".busy-ms"), System.Globalization.CultureInfo.InvariantCulture) });
            }
            finally
            {
                File.WriteAllText(gate + ".continue", "continue");
                if (!first.HasExited) first.Kill();
                if (second != null) { if (!second.HasExited) second.Kill(); second.Dispose(); }
            }
        });
        string report = Path.Combine(root, "volume-reservation-checks.json");
        File.WriteAllText(report, JsonSerializer.Serialize(new { failed, results }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(report + ": " + failed + " failures"); return failed == 0 ? 0 : 1;
    }
}










