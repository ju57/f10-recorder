using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace F10Recorder;

// These are file/ownership fixtures, not playable media. No CaptureSession,
// RecorderHost, encoder, window, shortcut or dependency provisioning is started.
internal static class ReplayOwnershipChecks
{
    static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    static async Task<ReplaySaveResult> Finish(SaveSubmission save)
    {
        Require(save.Accepted, save.Rejection);
        return await save.Completion.WaitAsync(Deadline);
    }

    sealed class Source : IReplaySessionSource
    {
        internal readonly long Epoch;
        readonly ReplaySaveClock clock;
        public ReplaySessionInfo ReplayInfo { get; }
        public bool Stopped { get; private set; }
        public string Diagnostics => "controlled source";
        internal bool Disposed;
        internal int ExportCount;
        internal Action ExportAction;
        internal readonly TaskCompletionSource ExportEntered = Signal();
        internal readonly TaskCompletionSource StopEntered = Signal();
        internal readonly TaskCompletionSource DisposeEntered = Signal();
        internal TaskCompletionSource ExportRelease;
        internal string[] Arguments, Concat;
        internal string Contents;
        internal int OutputBytes = 2048;
        internal bool MissingOutput;

        internal Source(string directory, string codec = "h264_nvenc", bool audio = false, double age = 200)
        {
            Directory.CreateDirectory(directory);
            string extension = codec == "av1_nvenc" ? ".mkv" : ".ts";
            ReplayInfo = new(directory, Path.Combine(directory, "segments.csv"), 60, 5, 60, codec, extension, audio, audio);
            Epoch = Stopwatch.GetTimestamp() - (long)(Stopwatch.Frequency * age);
            clock = new(new MediaClock(Epoch, Stopwatch.Frequency, 0, 0));
        }
        internal long Trigger(double seconds) => Epoch + (long)(seconds * Stopwatch.Frequency);
        internal string Chunk(int number) => Path.Combine(ReplayInfo.Directory, "chunk-" + number.ToString("D9") + ReplayInfo.Extension);
        internal void Segments(int first, int count, string[] labels = null, bool finalNewline = true)
        {
            var lines = new List<string>();
            for (int i = 0; i < count; i++)
            {
                int n = first + i;
                File.WriteAllText(Chunk(n), labels == null ? "picture-" + n : labels[i]);
                lines.Add(Path.GetFileName(Chunk(n)) + "," + n.ToString(CultureInfo.InvariantCulture) + "," + (n + 1).ToString(CultureInfo.InvariantCulture));
            }
            File.WriteAllText(ReplayInfo.Manifest, string.Join("\n", lines) + (finalNewline ? "\n" : ""));
        }
        public double SaveCutoffAt(long timestamp) => clock.PositionAt(timestamp);
        public void FreezeAt(long timestamp) => clock.StopAt(timestamp);
        public void StopCapture() { Stopped = true; StopEntered.TrySetResult(); }
        public void CheckCaptureHealth(double end) { Require(!Disposed, "Maintenance reached a disposed source."); }
        public void Export(IReadOnlyList<string> arguments, string snapshot)
        {
            Require(!Disposed, "Export reached a disposed source.");
            ExportCount++;
            Arguments = arguments.ToArray();
            Concat = File.ReadAllLines(Path.Combine(snapshot, "concat.txt"));
            Contents = string.Join("|", Concat.Where(s => s.StartsWith("file '")).Select(s => File.ReadAllText(Path.Combine(snapshot, s[6..^1]))));
            ExportEntered.TrySetResult();
            ExportRelease?.Task.WaitAsync(Deadline).GetAwaiter().GetResult();
            Require(!Disposed && Directory.Exists(snapshot), "Retirement removed export resources.");
            ExportAction?.Invoke();
            if (!MissingOutput) File.WriteAllBytes(arguments[^1], new byte[OutputBytes]);
        }
        public void Dispose() { Require(Stopped, "Resources released before acquisition stopped."); Disposed = true; DisposeEntered.TrySetResult(); }
    }

    sealed class Files : ReplayFiles
    {
        internal Action<string, string> BeforeCopy;
        internal Action BeforeLink;
        internal Action Published;
        internal Action<string> DeletedFile;
        internal Action<string> DeletedDirectory;
        internal bool RealHardLinks, FailPublish, FailCleanup;
        internal int Copies, LinkAttempts, LinkError;
        internal readonly ConcurrentQueue<string> Cleaned = new();
        internal Func<string, ReplayFileExtent> Observation;
        internal Func<string, ReplayVolumeCapacity> CapacityObservation;
        internal override ReplayFileExtent Observe(string path) => Observation?.Invoke(path) ?? base.Observe(path);
        internal override ReplayVolumeCapacity ObserveCapacity(string path) => CapacityObservation == null ? base.ObserveCapacity(path) : CapacityObservation(path);
        internal override bool TryHardLink(string copy, string original)
        {
            BeforeLink?.Invoke();
            LinkAttempts++;
            if (!RealHardLinks) return false;
            bool linked = base.TryHardLink(copy, original);
            if (!linked) LinkError = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            return linked;
        }
        internal override void Copy(string original, string copy) { Copies++; BeforeCopy?.Invoke(original, copy); base.Copy(original, copy); }
        internal override void Publish(string partial, string final)
        {
            if (FailPublish) throw new IOException("controlled publication failure");
            base.Publish(partial, final);
            Published?.Invoke();
        }
        internal override void DeleteFile(string path)
        {
            Cleaned.Enqueue(path);
            if (FailCleanup) throw new IOException("controlled cleanup failure");
            base.DeleteFile(path);
            DeletedFile?.Invoke(path);
        }
        internal override void DeleteDirectory(string path)
        {
            Cleaned.Enqueue(path);
            if (FailCleanup) throw new UnauthorizedAccessException("controlled directory cleanup failure");
            base.DeleteDirectory(path);
            DeletedDirectory?.Invoke(path);
        }
    }

    sealed class ManualTime : TimeProvider
    {
        long ticks = 1;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => ticks;
        internal void Advance() => ticks += 1000;
    }

    sealed class Fixture : IDisposable
    {
        internal readonly string Root, Destination, Existing;
        internal readonly Files Files = new();
        internal readonly ConcurrentQueue<string> Diagnostics = new();
        internal readonly Source Source;
        internal readonly ReplayCache Cache;
        internal readonly ReplaySessionId Id;
        internal Fixture(string root, string codec = "h264_nvenc", bool audio = false,
            TimeProvider time = null, Func<Task> wait = null, long budget = 10_000_000_000)
        {
            Root = root; Destination = Path.Combine(root, "clips"); Directory.CreateDirectory(Destination);
            Existing = Path.Combine(Destination, "existing.mp4"); File.WriteAllText(Existing, "preserve this saved video");
            Source = new(Path.Combine(root, "buffer", "session-fixture"), codec, audio);
            Source.Segments(0, 3);
            Cache = new(Files, time, wait, Diagnostics.Enqueue, Path.Combine(root, "buffer"), budget); Id = Cache.RegisterSession(Source);
        }
        internal SaveSubmission Save(double end = 3) => Cache.TrySave(Id, Source.Trigger(end), Destination);
        public void Dispose()
        {
            Source.ExportRelease?.TrySetResult();
            Cache.Dispose();
            Require(File.ReadAllText(Existing) == "preserve this saved video", "Existing saved media changed.");
        }
    }

    internal static int Run(string root) => RunAsync(root).GetAwaiter().GetResult();
    static async Task<int> RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        Log.DirectoryPath = root;
        var results = new List<object>();
        int failed = 0;
        async Task Check(string name, Func<string, Task> check)
        {
            try { await check(Path.Combine(root, name)); results.Add(new { name, result = "passed" }); }
            catch (Exception ex) { failed++; results.Add(new { name, result = "failed", error = ex.ToString() }); }
        }

        await Check("known-input-capacity-equality-and-rejection", async path =>
        {
            using var f = new Fixture(path);
            f.Source.Segments(0, 1);
            long available = 8191;
            f.Files.CapacityObservation = _ => new("target-volume", 4096, available);
            f.Files.Observation = p => Path.GetFileName(p).StartsWith("chunk-")
                ? new("source-volume", p, 1024, 1000, 1024)
                : new("target-volume", p, p.EndsWith(".ts") || p.EndsWith("concat.txt") ? 4096 : 0, new FileInfo(p).Length, 4096);
            var rejected = f.Save(1);
            if (rejected.Accepted) await Finish(rejected);
            Require(!rejected.Accepted && rejected.Rejection.Contains("capacity", StringComparison.OrdinalIgnoreCase)
                && f.Files.Copies == 0 && f.Source.ExportCount == 0 && !Directory.GetDirectories(f.Source.ReplayInfo.Directory, "save-*").Any(),
                "One 1000-byte input and concat need 8192 target bytes; 8191 must reject before responsibility or writes.");
            available = 8192;
            Require((await Finish(f.Save(1))).Succeeded && f.Files.Copies == 1 && f.Source.ExportCount == 1,
                "Literal known-input equality must admit an ordinary export; source allocation is already reflected in free space.");
        });

        await Check("known-input-target-unit-and-shared-pool", async path =>
        {
            using var f = new Fixture(path);
            f.Source.Segments(0, 2);
            File.WriteAllText(f.Source.Chunk(1), File.ReadAllText(f.Source.Chunk(0)));
            long unit = 4096, available = 12287;
            f.Files.CapacityObservation = _ => new("target-volume", unit, available);
            f.Files.Observation = p => new(Path.GetFileName(p).StartsWith("chunk-") ? "other-source-volume" : "target-volume", p,
                p.EndsWith(".ts") || p.EndsWith("concat.txt") ? unit : 0, 1000, unit);
            Require(!f.Save(2).Accepted, "Two copy names on one target need12288 total, not separate8192 allowances.");
            available = 12288;
            Require((await Finish(f.Save(2))).Succeeded, "Shared target finite equality failed.");
            f.Source.Segments(0, 1);
            unit = 65536; available = 131071;
            f.Files.Observation = p => Path.GetFileName(p).StartsWith("chunk-") ? new("source-volume", p, 4096, 1000, 4096)
                : new("target-volume", p, p.EndsWith(".ts") || p.EndsWith("concat.txt") ? 65536 : 0, 1000, 65536);
            Require(!f.Save(1).Accepted, "A source4096 unit cannot fund target65536 copy+concat demand131072.");
            available = 131072;
            Require((await Finish(f.Save(1))).Succeeded, "Target-unit equality failed.");
        });

        await Check("known-input-materialized-remainder-and-sampling", async path =>
        {
            foreach (bool enough in new[] { true, false })
            {
                using var f = new Fixture(Path.Combine(path, enough ? "equal637" : "short636"));
                f.Source.Segments(0, 1);
                long available = 1037;
                bool copying = false;
                f.Files.CapacityObservation = _ => new("volume-V", 1, available);
                f.Files.BeforeCopy = (_, _) => copying = true;
                f.Files.Observation = p =>
                {
                    if (Path.GetFileName(p) == "000000.ts")
                    {
                        Require(copying, "Snapshot was observed before copy setup.");
                        available = enough ? 637 : 636; // Post-materialization free, set by inventory BEFORE capacity.
                        return new("volume-V", p, 400, 1000, 1);
                    }
                    return new("volume-V", p, 0, Path.GetFileName(p).StartsWith("chunk-") ? 1000 : new FileInfo(p).Length, 1);
                };
                var result = await Finish(f.Save(1));
                Require(result.Succeeded == enough && f.Files.Copies == 1 && f.Source.ExportCount == (enough ? 1 : 0),
                    "Remaining600 + exact concat37 must fit637, reject636, without crediting materialization against pre-write free.");
                if (enough) Require(Encoding.UTF8.GetByteCount(string.Join(Environment.NewLine, f.Source.Concat) + Environment.NewLine) == 37,
                    "The prospectively specified Windows one-input concat fixture must be37 bytes.");
            }
        });

        await Check("known-input-link-discharge-and-fallback", async path =>
        {
            foreach (bool linked in new[] { true, false })
            {
                using var f = new Fixture(Path.Combine(path, linked ? "linked" : "fallback"));
                f.Source.Segments(0, 1);
                long available = 8192;
                f.Files.RealHardLinks = linked;
                f.Files.CapacityObservation = _ => new("volume-V", 4096, available);
                f.Files.Observation = p => new("volume-V", linked && Path.GetFileName(p) == "000000.ts" ? f.Source.Chunk(0) : p,
                    p.EndsWith(".ts") ? 4096 : 0, p.EndsWith(".ts") ? 1000 : 0, 4096);
                f.Files.BeforeLink = () => available = linked ? 4096 : 4095;
                var result = await Finish(f.Save(1));
                Require(result.Succeeded == linked && f.Files.Copies == 0 && f.Source.ExportCount == (linked ? 1 : 0),
                    "Successful hard link must discharge copy demand while reserving concat; failed fallback must recheck capacity before copying. Native link error=" + f.Files.LinkError);
            }
        });

        await Check("known-input-target-retargeting-cannot-credit-old-claim", async path =>
        {
            foreach (string changed in new[] { "capacity-volume", "capacity-unit", "extent-volume", "extent-unit" })
            {
                using var f = new Fixture(Path.Combine(path, changed));
                f.Source.Segments(0, 1);
                bool retargeted = false;
                f.Files.CapacityObservation = _ => new(retargeted && changed == "capacity-volume" ? "volume-W" : "volume-V",
                    retargeted && changed == "capacity-unit" ? 8192 : 4096, 8192);
                f.Files.Observation = p => new(retargeted && changed == "extent-volume" && Path.GetFileName(p) == "000000.ts" ? "volume-W" : "volume-V", p,
                    p.EndsWith(".ts") ? 4096 : 0, p.EndsWith(".ts") ? 1000 : 0,
                    retargeted && changed == "extent-unit" && Path.GetFileName(p) == "000000.ts" ? 8192 : 4096);
                f.Files.BeforeLink = () => retargeted = true;
                var failed = await Finish(f.Save(1));
                Require(!failed.Succeeded && failed.Error.Contains("target", StringComparison.OrdinalIgnoreCase)
                    && f.Source.ExportCount == 0 && !f.Cache.IsSaving && f.Files.Copies == (changed.StartsWith("extent-") ? 1 : 0),
                    "A changed target observation cannot discharge or migrate an accepted volume-V claim: " + changed);
                retargeted = false; f.Files.BeforeLink = null;
                Require((await Finish(f.Save(1))).Succeeded, "Retarget failure leaked a claim or poisoned the next valid request.");
            }
        });

        await Check("known-input-invalid-capacity-and-coherent-sampling", async path =>
        {
            using var f = new Fixture(path);
            f.Source.Segments(0, 1);
            f.Files.Observation = p => new("volume-V", p, p.EndsWith(".ts") ? 4096 : 0, p.EndsWith(".ts") ? 1000 : 0, 4096);
            foreach (var observe in new Func<string, ReplayVolumeCapacity>[] {
                _ => throw new UnauthorizedAccessException("controlled capacity denial"),
                _ => throw new System.ComponentModel.Win32Exception(5),
                _ => null,
                _ => new("", 4096, 8192), _ => new("volume-V", 0, 8192),
                _ => new("volume-V", 4096, -1), _ => new("volume-V", 4096, 0),
                _ => new("volume-V", long.MaxValue, long.MaxValue) })
            {
                f.Files.CapacityObservation = observe;
                var failed = f.Save(1);
                Require(!failed.Accepted && !string.IsNullOrEmpty(failed.Rejection) && !f.Cache.IsSaving
                    && f.Files.Copies == 0 && f.Source.ExportCount == 0 && !Directory.GetDirectories(f.Source.ReplayInfo.Directory, "save-*").Any(),
                    "Unknown/denied/invalid/overflow capacity must reject before responsibility, rather than use unlimited or zero-demand fallback.");
            }
            int observations = 0;
            f.Files.CapacityObservation = _ => new("volume-V", 4096, Interlocked.Increment(ref observations) == 1 ? 8192 : 8191);
            Require(!f.Save(1).Accepted, "Later lower same-volume availability cannot be maximized with an earlier sample.");
            f.Files.CapacityObservation = _ => new("volume-V", 4096, 8192);
            Require((await Finish(f.Save(1))).Succeeded, "Capacity failure poisoned a later valid ordinary export.");
        });

        await Check("known-input-late-selection-extension", async path =>
        {
            Fixture fixture = null;
            var time = new ManualTime();
            using var f = fixture = new Fixture(path, time: time, wait: () =>
            {
                fixture.Source.Segments(0, 2); time.Advance(); return Task.CompletedTask;
            });
            f.Source.Segments(0, 1);
            long available = 8192;
            f.Files.CapacityObservation = _ => new("volume-V", 4096, available);
            f.Files.Observation = p => new("volume-V", p, p.EndsWith(".ts") ? 4096 : 0, p.EndsWith(".ts") ? 1000 : 0, 4096);
            var accepted = f.Save(2);
            Require(accepted.Accepted, "Initially known single input should fit; no invented unfinished-tail bound.");
            var failed = await Finish(accepted);
            Require(!failed.Succeeded && failed.Error.Contains("capacity", StringComparison.OrdinalIgnoreCase)
                && f.Files.Copies == 0 && f.Source.ExportCount == 0 && !f.Cache.IsSaving,
                "Later two-input plan needs12288 and must fail explicitly before copies when only8192 is available.");
            available = 12288;
            Require((await Finish(f.Save(2))).Succeeded, "Failed late-plan extension leaked the accepted request's claim.");
        });

        await Check("known-input-preserves-budget-and-maintenance-seams", async path =>
        {
            using var f = new Fixture(path);
            f.Source.Segments(0, 1);
            bool denied = false, checkedBudget = false;
            int observations = 0;
            f.Files.CapacityObservation = _ => { observations++; return denied ? throw new UnauthorizedAccessException("controlled later capacity denial") : new("volume-V", 4096, 8192); };
            f.Files.Observation = p => new("volume-V", p, p.EndsWith(".ts") ? 4096 : 0, p.EndsWith(".ts") ? 1000 : 0, 4096);
            f.Files.BeforeCopy = (_, _) =>
            {
                denied = true;
                int before = observations;
                f.Cache.ValidateBudget(1_000_000);
                f.Cache.Maintain(f.Id);
                Require(observations == before && f.Cache.IsSaving, "Unrelated P05/maintenance consulted capacity or discarded the accepted claim.");
                checkedBudget = true;
            };
            var failed = await Finish(f.Save(1));
            Require(checkedBudget && !failed.Succeeded && failed.Error.Contains("capacity denial") && !f.Cache.IsSaving,
                "Only subsequent known-input growth should observe the capacity denial and fail the accepted request.");
            denied = false; f.Files.BeforeCopy = null;
            Require((await Finish(f.Save(1))).Succeeded, "Capacity denial poisoned an explicit later safe request.");
        });

        await Check("known-input-concurrent-rejection-and-retirement", async path =>
        {
            using var f = new Fixture(path);
            f.Source.Segments(0, 1);
            long available = 8192;
            var entered = Signal(); var release = Signal();
            f.Files.CapacityObservation = _ => new("volume-V", 4096, available);
            f.Files.Observation = p => new("volume-V", p, p.EndsWith(".ts") ? 4096 : 0, p.EndsWith(".ts") ? 1000 : 0, 4096);
            f.Files.BeforeCopy = (_, _) => { entered.TrySetResult(); release.Task.WaitAsync(Deadline).GetAwaiter().GetResult(); };
            var accepted = f.Save(1);
            Task retirement = null;
            try
            {
                await entered.Task.WaitAsync(Deadline);
                string otherDestination = Path.Combine(path, "different-destination");
                var replacement = new Source(Path.Combine(path, "buffer", "session-replacement")); replacement.Segments(0, 3);
                var id = f.Cache.RegisterSession(replacement);
                Require(!f.Cache.TrySave(id, replacement.Trigger(3), otherDestination).Accepted && f.Cache.IsSaving,
                    "A concurrent submission replaced or cleared an accepted request's claims.");
                retirement = f.Cache.RetireSessionAsync(f.Id);
                Require(!retirement.IsCompleted && !f.Source.Disposed, "Retirement reclaimed accepted input-copy resources.");
            }
            finally { release.TrySetResult(); }
            var saved = await Finish(accepted);
            if (retirement != null) await retirement.WaitAsync(Deadline);
            Require(saved.Succeeded && Path.GetDirectoryName(saved.File) == f.Destination && f.Source.Disposed
                && f.Source.Concat[1] == "duration 1.000000" && !f.Cache.IsSaving,
                "Concurrent/retired save lost its original destination, cutoff or completion lifetime.");
        });

        await Check("known-input-production-capacity-observer", path =>
        {
            Directory.CreateDirectory(path);
            string sentinel = Path.Combine(path, "ordinary-file");
            File.WriteAllText(sentinel, "task-owned capacity sentinel");
            var files = new ReplayFiles();
            var direct = files.ObserveCapacity(path);
            var alias = files.ObserveCapacity(Path.Combine(path, "."));
            string absent = Path.Combine(path, "not-created", "child");
            var future = files.ObserveCapacity(absent);
            var extent = files.Observe(sentinel);
            Require(!string.IsNullOrWhiteSpace(direct.Volume) && direct.AllocationUnit > 0 && direct.AvailableBytes >= 0
                && direct.Volume == alias.Volume && direct.Volume == future.Volume && direct.Volume == extent.Volume
                && direct.AllocationUnit == alias.AllocationUnit && direct.AllocationUnit == future.AllocationUnit
                && direct.AllocationUnit == extent.AllocationUnit && !Directory.Exists(Path.Combine(path, "not-created"))
                && File.ReadAllText(sentinel) == "task-owned capacity sentinel", "Native observer lost actual-volume identity or created/changed its inspected paths.");
            bool rejected = false;
            try { files.ObserveCapacity(Path.Combine(sentinel, "child")); } catch (IOException) { rejected = true; }
            Require(rejected, "A regular-file ancestor was mistaken for safely absent directory storage.");
            File.WriteAllText(Path.Combine(path, "capacity-smoke.json"), JsonSerializer.Serialize(new { direct, alias, future, extent }, WorkerProtocol.Json));
            return Task.CompletedTask;
        });

        await Check("budget-reduction-validates-without-changing-current-budget", async path =>
        {
            using var f = new Fixture(path, budget: 100_000_000);
            f.Files.CapacityObservation = _ => new("volume-C", 1, long.MaxValue);
            string retained = Path.Combine(path, "buffer", "session-retained", "retained.ts");
            Directory.CreateDirectory(Path.GetDirectoryName(retained)); File.WriteAllText(retained, "protected");
            f.Files.Observation = p => new("volume-C", p, p == f.Source.Chunk(0) ? 30_000_000 : p == retained ? 25_000_000 : 0,
                p == f.Source.Chunk(0) ? 30_000_000 : 0, 1);
            bool rejected = false;
            try { f.Cache.ValidateBudget(50_000_000); } catch (IOException) { rejected = true; }
            Require(rejected && f.Cache.Status.CacheBytes == 55_000_000 && File.ReadAllText(retained) == "protected",
                "B01: a 50M reduction must reject 55M retained demand without deleting it.");
            // The original 100M ceiling still admits 55M actual plus 30M copies.
            Require((await Finish(f.Save())).Succeeded, "Rejected preflight changed the current cache budget or poisoned save admission.");
        });

        await Check("budget-reduction-equality-identities-and-unknown", path =>
        {
            using var f = new Fixture(path, budget: 100_000_000);
            f.Files.Observation = p => new("volume-C", p, p == f.Source.Chunk(0) ? 30_000_000 : p == f.Source.Chunk(1) ? 20_000_000 : 0, 0, 1);
            f.Cache.ValidateBudget(50_000_000); // Literal known equality B02.
            f.Files.Observation = p => new("volume-C", p.EndsWith(".ts") ? "shared-A" : p, p.EndsWith(".ts") ? 45_000_000 : 0, 0, 1);
            f.Cache.ValidateBudget(50_000_000); // Several names, one allocation B03.
            File.WriteAllText(f.Source.Chunk(1), File.ReadAllText(f.Source.Chunk(0)));
            f.Files.Observation = p => new("volume-C", p, p == f.Source.Chunk(0) || p == f.Source.Chunk(1) ? 30_000_000 : 0, 0, 1);
            bool copiedRejected = false; try { f.Cache.ValidateBudget(50_000_000); } catch (IOException) { copiedRejected = true; }
            Require(copiedRejected, "B04: equal-content distinct copied allocations must reject a 50M reduction with60M demand.");
            foreach (var observation in new Func<string, ReplayFileExtent>[] {
                _ => throw new UnauthorizedAccessException("controlled denied observation"),
                p => new("", p, 1, 1, 1), p => new("volume-C", p, -1, 1, 1),
                p => new("volume-C", p, long.MaxValue, 1, 1),
                p => new("volume-C", "same-identity", p == f.Source.Chunk(0) ? 100 : 200, 0, 1) })
            {
                f.Files.Observation = observation; bool rejected = false;
                try { f.Cache.ValidateBudget(50_000_000); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException) { rejected = true; }
                Require(rejected, "B06: unknown/denied/inconsistent/overflow cannot imply empty demand.");
            }
            return Task.CompletedTask;
        });
        await Check("budget-reduction-keeps-pending-copy-obligations", async path =>
        {
            using var f = new Fixture(path, budget: 100_000_000);
            f.Files.CapacityObservation = _ => new("volume-C", 1, long.MaxValue);
            var entered = Signal(); var release = Signal();
            f.Files.Observation = p => new("volume-C", p, p == f.Source.Chunk(0) ? 30_000_000 : 0, p == f.Source.Chunk(0) ? 30_000_000 : 0, 1);
            f.Files.BeforeCopy = (_, _) => { entered.TrySetResult(); release.Task.WaitAsync(Deadline).GetAwaiter().GetResult(); };
            var accepted = f.Save();
            try
            {
                await entered.Task.WaitAsync(Deadline);
                bool rejected = false; try { f.Cache.ValidateBudget(50_000_000); } catch (IOException) { rejected = true; }
                Require(rejected && f.Cache.Status.Saving, "Pending copy demand was ignored or an accepted save was displaced.");
            }
            finally { release.TrySetResult(); }
            Require((await Finish(accepted)).Succeeded, "A rejected reduction harmed the accepted save.");
        });

        await Check("storage-shared-extent-status", path =>
        {
            using var f = new Fixture(path);
            // Three names model the same physical extent; manifests are separately modeled empty.
            f.Files.Observation = p => new("volume-C", Path.GetFileName(p).StartsWith("chunk-") ? "extent-A" : p,
                Path.GetFileName(p).StartsWith("chunk-") ? 1000 : 0, 1000, 1);
            Require(f.Cache.Status.CacheBytes == 1000, "E01: hard-link names and lifetime pins must charge one physical extent once.");
            return Task.CompletedTask;
        });
        await Check("storage-retained-input-copy-admission", async path =>
        {
            using var f = new Fixture(path, budget: 6500);
            f.Files.CapacityObservation = _ => new("volume-C", 1, long.MaxValue);
            f.Files.Observation = p => new("volume-C", p, Path.GetFileName(p).StartsWith("chunk-") ? 1000 : new FileInfo(p).Length,
                Path.GetFileName(p).StartsWith("chunk-") ? 1000 : new FileInfo(p).Length, 1);
            string retained = Path.Combine(path, "buffer", "session-retained", "chunk-old.ts");
            Directory.CreateDirectory(Path.GetDirectoryName(retained)); File.WriteAllText(retained, "protected retained input");
            var rejected = f.Save();
            Require(!rejected.Accepted && rejected.Rejection.Contains("budget", StringComparison.OrdinalIgnoreCase) && !f.Cache.Status.Saving && f.Files.Copies == 0,
                "E04/E06: retained old input plus worst-case fallback copies must reject before accepting or copying.");
            Require(File.ReadAllText(retained) == "protected retained input", "Rejected admission deleted protected old footage.");
            // Fixture capacity control: represent the old file as released, while keeping its bytes/sentinel untouched.
            f.Files.Observation = p => new("volume-C", p, p == retained ? 0 : Path.GetFileName(p).StartsWith("chunk-") ? 1000 : new FileInfo(p).Length,
                Path.GetFileName(p).StartsWith("chunk-") ? 1000 : new FileInfo(p).Length, 1);
            Require((await Finish(f.Save())).Succeeded, "Positive capacity control could not save the same footage.");
        });
        await Check("storage-fallback-rechecks-external-growth", async path =>
        {
            using var f = new Fixture(path, budget: 6500);
            f.Files.CapacityObservation = _ => new("volume-C", 1, long.MaxValue);
            f.Files.Observation = p => new("volume-C", p, p.EndsWith(".ts") ? 1000 : new FileInfo(p).Length,
                p.EndsWith(".ts") ? 1000 : new FileInfo(p).Length, 1);
            string retained = Path.Combine(path, "buffer", "external-retained.ts");
            f.Files.BeforeLink = () => File.WriteAllText(retained, "never delete to admit a save");
            var save = f.Save(); Require(save.Accepted, save.Rejection);
            var result = await Finish(save);
            Require(!result.Succeeded && result.Error.Contains("budget", StringComparison.OrdinalIgnoreCase) && f.Files.Copies == 0,
                "E06/T03: fallback must recheck external cache growth before copying, then report the accepted save's failure.");
            Require(File.ReadAllText(retained) == "never delete to admit a save" && !f.Cache.Status.Saving, "Failed save harmed retained data or left admission busy.");
        });
        await Check("storage-copies-retiring-session-and-cleanup", async path =>
        {
            using var f = new Fixture(path, budget: 6500);
            f.Files.CapacityObservation = _ => new("volume-C", 1, long.MaxValue);
            f.Files.Observation = p => new("volume-C", p, p.EndsWith(".ts") ? 1000 : 0, p.EndsWith(".ts") ? 1000 : 0, 1);
            f.Source.ExportRelease = Signal();
            var accepted = f.Save(); Require(accepted.Accepted, accepted.Rejection);
            await f.Source.ExportEntered.Task.WaitAsync(Deadline);
            Require(f.Cache.Status.CacheBytes == 6000, "E02: three originals plus three copied snapshot extents must count as 6,000 bytes.");
            var retirement = f.Cache.RetireSessionAsync(f.Id);
            var next = new Source(Path.Combine(path, "buffer", "session-new")); next.Segments(0, 3);
            var id = f.Cache.RegisterSession(next);
            Require(f.Cache.Status.CacheBytes == 9000, "E04: retiring accepted inputs/copies must still count alongside a new session.");
            bool blocked = false; try { f.Cache.Maintain(id); } catch (IOException ex) { blocked = ex.Message.Contains("budget", StringComparison.OrdinalIgnoreCase); }
            Require(blocked && !f.Source.Disposed, "Aggregate pressure must not dispose the accepted export's old source.");
            Require(!f.Cache.TrySave(id, next.Trigger(3), f.Destination).Accepted, "T01: a new request displaced an accepted old-session export.");
            f.Files.FailCleanup = true;
            f.Source.ExportRelease.SetResult();
            Require((await Finish(accepted)).Succeeded, "T02: cleanup failure reversed final publication success.");
            await retirement.WaitAsync(Deadline);
            Require(f.Cache.Status.CacheBytes == 9000, "E05: failed cleanup fabricated free cache capacity.");
            Require(!f.Cache.TrySave(id, next.Trigger(3), f.Destination).Accepted, "Failed-cleanup extents were omitted from subsequent admission.");
        });
        await Check("storage-distinct-identities-and-volumes", path =>
        {
            using var f = new Fixture(path);
            f.Files.Observation = p => new(p == f.Source.Chunk(2) ? "volume-D" : "volume-C", "same-file-id",
                p.EndsWith(".ts") ? 1000 : 0, p.EndsWith(".ts") ? 1000 : 0, 1);
            // The manifest has its own identity; otherwise zero allocation would contradict extent A.
            var observations = f.Files.Observation;
            f.Files.Observation = p => p.EndsWith(".ts") ? observations(p) : new("volume-C", p, 0, 0, 1);
            Require(f.Cache.Status.CacheBytes == 2000, "E03: equal file IDs on different volumes are two allocations; aliases on one volume are one.");
            f.Files.Observation = p => new("volume-C", p, p.EndsWith(".ts") ? 1000 : 0, p.EndsWith(".ts") ? 1000 : 0, 1);
            Require(f.Cache.Status.CacheBytes == 3000, "E03: different identities with equal length/content must count separately.");
            return Task.CompletedTask;
        });
        await Check("storage-pressure-before-first-completed-segment", path =>
        {
            using var f = new Fixture(path, budget: 2500);
            f.Source.Segments(0, 0);
            f.Files.Observation = p => new("volume-C", p, p.EndsWith(".ts") ? 1000 : 0, p.EndsWith(".ts") ? 1000 : 0, 1);
            bool blocked = false; try { f.Cache.Maintain(f.Id); } catch (IOException ex) { blocked = ex.Message.Contains("budget", StringComparison.OrdinalIgnoreCase); }
            Require(blocked, "Aggregate storage pressure must be checked even while no completed segment is yet available.");
            return Task.CompletedTask;
        });
        await Check("storage-observation-failures-reject-without-mutation", async path =>
        {
            using var f = new Fixture(path);
            foreach (var observation in new Func<string, ReplayFileExtent>[] {
                _ => throw new UnauthorizedAccessException("controlled inventory permission denial"),
                p => new("volume-C", p, -1, 1, 1),
                p => new("volume-C", p, 1, 1, 0),
                p => new("volume-C", p, 1, long.MaxValue, 2),
                p => new("volume-C", p, long.MaxValue, 1, 1) })
            {
                f.Files.Observation = observation;
                var rejected = f.Save();
                Require(!rejected.Accepted && !string.IsNullOrEmpty(rejected.Rejection) && !f.Cache.Status.Saving && f.Cache.Status.CacheBytes == null
                    && !string.IsNullOrEmpty(f.Cache.Status.StorageError) && f.Files.Copies == 0,
                    "V08: unknown/invalid/overflowed storage must reject with a reason, without inventing free capacity or accepting work.");
            }
            f.Files.Observation = null;
            Require((await Finish(f.Save())).Succeeded, "An earlier inventory failure poisoned later valid admission.");
        });
        await Check("storage-materializing-copy-not-double-counted", async path =>
        {
            using var f = new Fixture(path, budget: 6500);
            f.Files.CapacityObservation = _ => new("volume-C", 1, long.MaxValue);
            f.Files.Observation = p => new("volume-C", p, p.EndsWith(".ts") ? 1000 : 0, p.EndsWith(".ts") ? 1000 : 0, 1);
            f.Files.BeforeCopy = (_, copy) =>
            {
                if (f.Files.Copies != 1) return;
                File.WriteAllText(copy, "modeled partial copy");
                try { f.Cache.Maintain(f.Id); }
                finally { File.Delete(copy); }
            };
            Require((await Finish(f.Save())).Succeeded,
                "V06 cache analogue: materialized copy allocation must replace its future claim, not be charged alongside the full claim.");
        });
        await Check("storage-snapshot-cleanup-versus-maintenance", async path =>
        {
            using var f = new Fixture(path);
            using var observed = new ManualResetEventSlim();
            using var resume = new ManualResetEventSlim();
            using var deleted = new ManualResetEventSlim();
            var published = Signal();
            var cleanupRequested = Signal();
            int armed = 0, entered = 0, observing = 0, deletedDuringObservation = 0;
            f.Source.ExportRelease = Signal();
            f.Files.Published = () => published.TrySetResult();
            f.Files.DeletedFile = p => { if (p.EndsWith(".partial.mp4")) cleanupRequested.TrySetResult(); };
            f.Files.DeletedDirectory = p =>
            {
                if (!Path.GetFileName(p).StartsWith("save-")) return;
                if (Volatile.Read(ref observing) != 0) Volatile.Write(ref deletedDuringObservation, 1);
                deleted.Set();
            };
            f.Files.Observation = p =>
            {
                if (Volatile.Read(ref armed) == 0 || Path.GetFileName(p) != "000000.ts" || !Path.GetFileName(Path.GetDirectoryName(p)).StartsWith("save-") || Interlocked.Exchange(ref entered, 1) != 0) return null;
                long bytes = new FileInfo(p).Length;
                Volatile.Write(ref observing, 1);
                observed.Set();
                try { Require(resume.Wait(Deadline), "Inventory observation was not released."); }
                finally { Volatile.Write(ref observing, 0); }
                return new("controlled-volume", p, bytes, bytes, 1);
            };
            var save = f.Save();
            await f.Source.ExportEntered.Task.WaitAsync(Deadline);
            Volatile.Write(ref armed, 1);
            Exception maintenanceError = null;
            var maintenance = Task.Run(() => { try { f.Cache.Maintain(f.Id); } catch (Exception ex) { maintenanceError = ex; } });
            try
            {
                Require(observed.Wait(Deadline), "Inventory did not reach the protected snapshot.");
                f.Source.ExportRelease.SetResult();
                await published.Task.WaitAsync(Deadline);
                await cleanupRequested.Task.WaitAsync(Deadline);
                // Allow the old ungated deletion to finish, or the corrected
                // deletion to defer. Never require cleanup while inventory owns it.
                deleted.Wait(TimeSpan.FromSeconds(1));
            }
            finally { resume.Set(); }
            await maintenance.WaitAsync(Deadline);
            var result = await Finish(save);
            Require(result.Succeeded && maintenanceError == null && deletedDuringObservation == 0 && deleted.IsSet && f.Cache.Status.StorageError == null,
                "R01: snapshot deletion overlapped its held observation or broke continuing maintenance: " + maintenanceError);
        });
        await Check("storage-retirement-cleanup-versus-maintenance", async path =>
        {
            using var f = new Fixture(path);
            var next = new Source(Path.Combine(path, "buffer", "session-next")); next.Segments(0, 3);
            var nextId = f.Cache.RegisterSession(next);
            using var observed = new ManualResetEventSlim();
            using var resume = new ManualResetEventSlim();
            using var deleted = new ManualResetEventSlim();
            int entered = 0, observing = 0, deletedDuringObservation = 0;
            Task retirement = null;
            f.Files.DeletedDirectory = p =>
            {
                if (p != f.Source.ReplayInfo.Directory) return;
                if (Volatile.Read(ref observing) != 0) Volatile.Write(ref deletedDuringObservation, 1);
                deleted.Set();
            };
            f.Files.Observation = p =>
            {
                if (p != f.Source.Chunk(0) || Interlocked.Exchange(ref entered, 1) != 0) return null;
                long bytes = new FileInfo(p).Length;
                Volatile.Write(ref observing, 1);
                // Reentrant public retirement starts the independent cleanup
                // while this unrelated session owns a stable inventory scan.
                retirement = f.Cache.RetireSessionAsync(f.Id);
                observed.Set();
                try { Require(resume.Wait(Deadline), "Retirement inventory observation was not released."); }
                finally { Volatile.Write(ref observing, 0); }
                return new("controlled-volume", p, bytes, bytes, 1);
            };
            Exception maintenanceError = null;
            var maintenance = Task.Run(() => { try { f.Cache.Maintain(nextId); } catch (Exception ex) { maintenanceError = ex; } });
            try
            {
                Require(observed.Wait(Deadline), "Inventory did not reach the retiring session.");
                // Disposal must remain outside the inventory gate, even though
                // directory removal must wait until the observation is released.
                await f.Source.DisposeEntered.Task.WaitAsync(Deadline);
                deleted.Wait(TimeSpan.FromSeconds(1));
            }
            finally { resume.Set(); }
            await maintenance.WaitAsync(Deadline);
            Require(retirement != null, "Retirement was not requested.");
            await retirement.WaitAsync(Deadline);
            Require(maintenanceError == null && deletedDuringObservation == 0 && deleted.IsSet && f.Source.Disposed
                && f.Cache.GetStatus(nextId).BufferedSeconds > 0 && f.Cache.Status.StorageError == null,
                "R02: retirement deletion overlapped its held observation or broke unrelated maintenance: " + maintenanceError);
        });

        await Check("pins-prune-retire", async path =>
        {
            using var f = new Fixture(path);
            var copying = Signal(); var resume = Signal();
            f.Source.ExportRelease = Signal();
            f.Files.BeforeCopy = (_, _) => { if (f.Files.Copies == 1) { copying.TrySetResult(); resume.Task.WaitAsync(Deadline).GetAwaiter().GetResult(); } };
            var save = f.Save();
            try
            {
                await copying.Task.WaitAsync(Deadline);
                f.Source.Segments(100, 3); f.Cache.Maintain(f.Id);
                Require(Enumerable.Range(0, 3).All(i => File.Exists(f.Source.Chunk(i))), "Pruning removed pinned originals during copy.");
                resume.SetResult(); await f.Source.ExportEntered.Task.WaitAsync(Deadline);
                f.Cache.Maintain(f.Id);
                Require(Enumerable.Range(0, 3).All(i => !File.Exists(f.Source.Chunk(i))), "Original pins leaked after snapshot creation.");
                var retirement = f.Cache.RetireSessionAsync(f.Id);
                await f.Source.StopEntered.Task.WaitAsync(Deadline);
                Require(!retirement.IsCompleted && !f.Source.Disposed && Directory.Exists(f.Source.ReplayInfo.Directory), "Retirement did not wait for export.");
                Require(!f.Save().Accepted, "Retiring session accepted another save.");
                f.Source.ExportRelease.SetResult(); var result = await Finish(save); await retirement.WaitAsync(Deadline);
                Require(result.Succeeded && File.Exists(result.File), "Protected export failed.");
                Require(f.Source.Contents == "picture-0|picture-1|picture-2", "Export lost its original snapshot.");
                Require(f.Source.Disposed && !Directory.Exists(f.Source.ReplayInfo.Directory), "Retirement did not reclaim the session.");
            }
            finally { resume.TrySetResult(); }
        });

        await Check("copy-failure-recovery", async path =>
        {
            using var f = new Fixture(path);
            f.Files.BeforeCopy = (_, _) => { if (f.Files.Copies == 2) throw new IOException("controlled second copy failure"); };
            var result = await Finish(f.Save());
            Require(!result.Succeeded && f.Source.ExportCount == 0 && !f.Cache.Status.Saving, "Copy failure left a save active or started export.");
            Require(!Directory.GetDirectories(f.Source.ReplayInfo.Directory, "save-*").Any(), "Failed snapshot was not cleaned.");
            f.Source.Segments(100, 3); f.Cache.Maintain(f.Id);
            Require(Enumerable.Range(0, 3).All(i => !File.Exists(f.Source.Chunk(i))), "Copy failure leaked pins.");
            f.Files.BeforeCopy = null;
            Require((await Finish(f.Save(103))).Succeeded, "A later save could not recover.");
        });

        foreach (string failure in new[] { "start", "timeout", "exit", "missing", "short", "publish" })
            await Check("export-failure-" + failure, async path =>
            {
                using var f = new Fixture(path);
                f.Source.ExportAction = () =>
                {
                    File.WriteAllBytes(f.Source.Arguments[^1], new byte[2048]);
                    if (failure == "start") throw new IOException("controlled start failure");
                    if (failure == "timeout") throw new TimeoutException("controlled export timeout");
                    if (failure == "exit") throw new IOException("controlled nonzero exit");
                    if (failure == "missing") File.Delete(f.Source.Arguments[^1]);
                };
                f.Source.MissingOutput = failure == "missing"; f.Source.OutputBytes = failure == "short" ? 10 : 2048;
                f.Files.FailPublish = failure == "publish";
                var result = await Finish(f.Save());
                Require(!result.Succeeded && !f.Cache.Status.Saving && f.Source.ExportCount == 1, "Failure was successful, busy, or automatically retried.");
                Require(Directory.GetFiles(f.Destination).SequenceEqual(new[] { f.Existing }), "Failure published a final file or left a partial output.");
                Require(!Directory.GetDirectories(f.Source.ReplayInfo.Directory, "save-*").Any(), "Export failure left a snapshot.");
                f.Source.ExportAction = null; f.Source.MissingOutput = false; f.Source.OutputBytes = 2048; f.Files.FailPublish = false;
                Require((await Finish(f.Save())).Succeeded, "Failure prevented a new save.");
            });

        await Check("cleanup-separate-from-result", async path =>
        {
            using var f = new Fixture(path);
            f.Files.FailCleanup = true;
            var result = await Finish(f.Save());
            Require(result.Succeeded && File.Exists(result.File), "Cleanup reversed publication success.");
            Require(f.Diagnostics.Any(s => s.Contains("cleanup failed")), "Cleanup failure was not diagnosed.");
            f.Source.ExportAction = () => throw new IOException("primary export error");
            var failure = await Finish(f.Save());
            Require(!failure.Succeeded && failure.Error == "primary export error", "Cleanup hid the primary failure.");
            f.Files.FailCleanup = false;
        });

        await Check("stop-during-segment-wait", async path =>
        {
            var waiting = Signal(); var resume = Signal();
            using var f = new Fixture(path, wait: () => { waiting.TrySetResult(); return resume.Task; });
            f.Source.Segments(0, 2);
            var save = f.Save(3.5);
            try
            {
                await waiting.Task.WaitAsync(Deadline);
                await f.Cache.StopAcquisitionAsync(f.Id).WaitAsync(Deadline);
                resume.SetResult(); var result = await Finish(save);
                Require(result.Succeeded && !f.Source.Disposed, "Source loss discarded retained footage.");
                int trim = Array.IndexOf(f.Source.Arguments, "-t");
                Require(f.Source.Arguments[trim + 1] == "2.000000", "Save exceeded completed footage after stop.");
                Require((await Finish(f.Save(20))).Succeeded, "Stopped completed footage was no longer savable.");
            }
            finally { resume.TrySetResult(); }
        });

        await Check("retire-during-wait", async path =>
        {
            var waiting = Signal(); var resume = Signal();
            using var f = new Fixture(path, wait: () => { waiting.TrySetResult(); return resume.Task; });
            f.Source.Segments(0, 2); var save = f.Save(4);
            try
            {
                await waiting.Task.WaitAsync(Deadline);
                var retirement = f.Cache.RetireSessionAsync(f.Id);
                await f.Source.StopEntered.Task.WaitAsync(Deadline);
                Require(!f.Source.Disposed && !retirement.IsCompleted, "Retirement removed waiting save inputs.");
                resume.SetResult(); Require((await Finish(save)).Succeeded, "Accepted waiting save did not finish.");
                await retirement.WaitAsync(Deadline);
            }
            finally { resume.TrySetResult(); }
        });

        await Check("admission-reserves-before-segment-wait", async path =>
        {
            var waiting = Signal(); var resume = Signal();
            using var f = new Fixture(path, wait: () => { waiting.TrySetResult(); return resume.Task; });
            var save = f.Save(4);
            try
            {
                await waiting.Task.WaitAsync(Deadline);
                // The current GOP closes while the save task remains suspended.
                f.Source.Segments(0, 4); f.Cache.Maintain(f.Id);
                // Manifest rotation must not erase metadata for the reserved inputs.
                f.Source.Segments(100, 3); f.Cache.Maintain(f.Id);
                Require(Enumerable.Range(0, 4).All(i => File.Exists(f.Source.Chunk(i))), "Accepted waiting save lost inputs to pruning.");
                resume.SetResult(); Require((await Finish(save)).Succeeded, "Reserved input metadata was lost after manifest rotation.");
                Require(f.Source.Contents == "picture-0|picture-1|picture-2|picture-3", "Waiting save changed its requested footage.");
            }
            finally { resume.TrySetResult(); }
        });

        await Check("retire-during-copy", async path =>
        {
            using var f = new Fixture(path);
            var copying = Signal(); var resume = Signal();
            f.Files.BeforeCopy = (_, _) => { copying.TrySetResult(); resume.Task.WaitAsync(Deadline).GetAwaiter().GetResult(); };
            var save = f.Save();
            try
            {
                await copying.Task.WaitAsync(Deadline); var retirement = f.Cache.RetireSessionAsync(f.Id);
                await f.Source.StopEntered.Task.WaitAsync(Deadline);
                Require(!retirement.IsCompleted && !f.Source.Disposed, "Retirement removed copy inputs.");
                resume.SetResult(); Require((await Finish(save)).Succeeded, "Accepted copying save did not finish.");
                await retirement.WaitAsync(Deadline);
            }
            finally { resume.TrySetResult(); }
        });

        await Check("busy-new-session-and-close", async path =>
        {
            using var f = new Fixture(path); f.Source.ExportRelease = Signal();
            var save = f.Save(); await f.Source.ExportEntered.Task.WaitAsync(Deadline);
            var replacement = new Source(Path.Combine(path, "buffer", "session-replacement"), age: 0); replacement.Segments(0, 3);
            var next = f.Cache.RegisterSession(replacement);
            Require(f.Cache.TrySave(next, replacement.Trigger(3), f.Destination).Rejection.Contains("already"), "Busy exclusion did not span sessions.");
            var close = f.Cache.CloseAsync();
            await f.Source.StopEntered.Task.WaitAsync(Deadline);
            Require(!close.IsCompleted && !f.Source.Disposed, "Close abandoned an accepted save.");
            Require(!f.Save().Accepted, "Close accepted another save.");
            f.Source.ExportRelease.SetResult(); Require((await Finish(save)).Succeeded, "Close interrupted save.");
            await close.WaitAsync(Deadline); Require(replacement.Disposed && f.Source.Disposed, "Close leaked source resources.");
        });

        await Check("admission-versus-retirement", async path =>
        {
            for (int i = 0; i < 20; i++)
            {
                using var f = new Fixture(Path.Combine(path, i.ToString()));
                var go = Signal();
                var saving = Task.Run(async () => { await go.Task; return f.Save(); });
                var retiring = Task.Run(async () => { await go.Task; await f.Cache.RetireSessionAsync(f.Id); });
                go.SetResult(); var submission = await saving.WaitAsync(Deadline);
                if (submission.Accepted) Require((await Finish(submission)).Succeeded, "Accepted save lost the admission race.");
                await retiring.WaitAsync(Deadline);
                Require(f.Source.Disposed && !f.Cache.Status.Saving, "Admission race leaked ownership.");
            }
        });

        await Check("stale-trigger-and-first-stop", async path =>
        {
            using var f = new Fixture(path);
            Require(!f.Cache.TrySave(f.Id, f.Source.Epoch - 1, f.Destination).Accepted && !f.Cache.Status.Saving, "Stale trigger borrowed session footage.");
            f.Source.FreezeAt(f.Source.Trigger(1.5));
            await f.Cache.StopAcquisitionAsync(f.Id);
            f.Source.FreezeAt(f.Source.Trigger(20));
            Require(f.Source.SaveCutoffAt(f.Source.Trigger(50)) == 1.5, "A later stop moved the first cutoff.");
            Require(f.Source.SaveCutoffAt(f.Source.Trigger(1)) == 1, "Stop changed an earlier trigger.");
            Require((await Finish(f.Save(20))).Succeeded, "First-stop save failed.");
            int trim = Array.IndexOf(f.Source.Arguments, "-t");
            Require(f.Source.Arguments[trim + 1] == "1.500000", "First-stop cutoff did not reach export.");
        });

        await Check("monitor-application-sequence", async path =>
        {
            using var f = new Fixture(path);
            f.Source.Segments(0, 3, ["fullscreen-game", "browser", "files"]);
            f.Source.ExportRelease = Signal(); var save = f.Save(); await f.Source.ExportEntered.Task.WaitAsync(Deadline);
            f.Source.Segments(0, 4, ["fullscreen-game", "browser", "files", "after-trigger"]);
            f.Source.ExportRelease.SetResult(); var result = await Finish(save);
            Require(result.Succeeded && Path.GetDirectoryName(result.File) == f.Destination, "Save destination changed.");
            Require(f.Source.Contents == "fullscreen-game|browser|files", "Application change filtered footage or extended the accepted request.");
        });

        foreach (string codec in new[] { "h264_nvenc", "hevc_nvenc", "av1_nvenc" })
            await Check("manifest-and-export-" + codec, async path =>
            {
                using var f = new Fixture(path, codec, audio: true);
                f.Source.Segments(0, 3, finalNewline: false);
                File.WriteAllText(f.Source.Chunk(999999999), "new chunk absent from partial manifest");
                f.Cache.Maintain(f.Id);
                Require(f.Cache.GetStatus(f.Id).BufferedSeconds == 1, "Readiness included pre-roll or unfinished manifest entry.");
                var result = await Finish(f.Save(2)); Require(result.Succeeded, result.Error);
                Require(f.Source.Contents == "picture-1" && f.Source.Concat[1] == "duration 1.000000", "Pre-roll/incomplete manifest exclusion or authoritative duration changed.");
                Require(File.Exists(f.Source.Chunk(999999999)), "Pruning removed a newer unlisted chunk.");
                Require(f.Source.Arguments.Contains("0:a:0") && f.Source.Arguments.Contains("+faststart"), "Audio map or MP4 finalization missing.");
                Require(f.Source.Arguments.Contains("hvc1") == (codec == "hevc_nvenc"), "HEVC tag changed.");
                Require(f.Source.Arguments.Any(s => s.Contains("setts=time_base=1/48000")) == (codec == "av1_nvenc"), "AV1 clock restoration changed.");
                Require(!Directory.GetFiles(f.Destination, "*.partial.mp4").Any(), "Successful save left a partial output.");
            });

        await Check("hard-links-and-fallback", async path =>
        {
            using var f = new Fixture(path); f.Files.RealHardLinks = true;
            var result = await Finish(f.Save());
            Require(result.Succeeded && f.Files.Copies == 0 && f.Files.LinkAttempts == 3,
                $"Local hard-link snapshot failed: success={result.Succeeded}; copies={f.Files.Copies}; attempts={f.Files.LinkAttempts}; Win32={f.Files.LinkError}; {result.Error}");
            f.Files.RealHardLinks = false;
            Require((await Finish(f.Save())).Succeeded && f.Files.Copies == 3, "Copy fallback failed.");
        });

        await Check("bounded-segment-wait", async path =>
        {
            var time = new ManualTime();
            using var f = new Fixture(path, time: time, wait: () => { time.Advance(); return Task.CompletedTask; });
            f.Source.Segments(0, 1);
            var result = await Finish(f.Save(10));
            Require(!result.Succeeded && result.Error.Contains("finish the current segment") && !f.Cache.Status.Saving, "Segment timeout did not release admission.");
        });

        await Check("retention-byte-limit", path =>
        {
            using var f = new Fixture(path);
            long limit = (long)((60 + 30) * (5 * 1000000L + 192000) / 8 * 1.5);
            f.Files.Observation = p => p == f.Source.Chunk(2) ? new("volume-C", p, limit + 1, limit + 1, 1) : null;
            bool rejected = false;
            try { f.Cache.Maintain(f.Id); } catch (IOException ex) { rejected = ex.Message.Contains("byte limit"); }
            Require(rejected, "Retention stopped enforcing its byte limit.");
            return Task.CompletedTask;
        });

        await Check("fractional-manifest-durations", async path =>
        {
            using var f = new Fixture(path);
            File.WriteAllText(f.Source.ReplayInfo.Manifest,
                Path.GetFileName(f.Source.Chunk(0)) + ",0.25,1.5\n" + Path.GetFileName(f.Source.Chunk(1)) + ",1.5,2.75\n");
            Require((await Finish(f.Save(2.5))).Succeeded, "Fractional-boundary export failed.");
            Require(f.Source.Concat[1] == "duration 1.250000" && f.Source.Concat[3] == "duration 1.250000", "Concat used container probes instead of manifest boundaries.");
            int trim = Array.IndexOf(f.Source.Arguments, "-t");
            Require(f.Source.Arguments[trim + 1] == "2.250000", "Trigger trim ignored the selected keyframe start.");
        });

        await Check("keyframe-start-and-window", async path =>
        {
            using var f = new Fixture(path); f.Source.Segments(0, 64);
            Require((await Finish(f.Save(62.5))).Succeeded, "Keyframe selection failed.");
            Require(f.Source.Contents.StartsWith("picture-3|") && f.Source.Contents.EndsWith("|picture-62"), "Replay did not start on the first keyframe after the window start.");
            int trim = Array.IndexOf(f.Source.Arguments, "-t");
            Require(f.Source.Arguments[trim + 1] == "59.500000", "Replay length or frozen fractional end changed.");
        });

        await Check("throwing-notification-observers", async path =>
        {
            using var f = new Fixture(path);
            int started = 0, finished = 0;
            Action start = () => throw new IOException("controlled start observer failure"); start += () => started++;
            Action<string, bool> finish = (_, _) => throw new IOException("controlled finish observer failure");
            finish += (_, success) => { Require(success, "Observer saw an incorrect result."); finished++; };
            var save = f.Save();
            var observed = new ReplaySaveNotifications().Observe(save.Completion, start, finish);
            var result = await Finish(save); await observed.WaitAsync(Deadline);
            Require(result.Succeeded && started == 1 && finished == 1 && !f.Cache.Status.Saving, "Observer error altered save completion.");
            Require((await Finish(f.Save())).Succeeded, "Observer failure stranded admission.");
        });

        await Check("missing-cache-status", path =>
        {
            using var f = new Fixture(path);
            Directory.Delete(f.Source.ReplayInfo.Directory, true);
            var status = f.Cache.GetStatus(f.Id);
            Require(status.BufferedSeconds == 0 && status.CacheBytes == 0, "Missing cache reported available footage.");
            return Task.CompletedTask;
        });

        await Check("consecutive-save-notification-order", async path =>
        {
            using var f = new Fixture(path);
            var notifications = new ReplaySaveNotifications();
            var events = new ConcurrentQueue<string>();
            var firstDelivery = Signal(); var releaseFirst = Signal(); var secondStarted = Signal();
            var notices = new RecorderNotifications((title, _, _, _) => events.Enqueue(title));
            var first = f.Save();
            // Hold A's result delivery after its export has released admission.
            var delayedFirst = new TaskCompletionSource<ReplaySaveResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstObserved = notifications.Observe(delayedFirst.Task, notices.SaveStarted, (message, success) =>
            {
                firstDelivery.SetResult(); releaseFirst.Task.WaitAsync(Deadline).GetAwaiter().GetResult();
                notices.SaveFinished(message, success);
            });
            var firstResult = await Finish(first);
            try
            {
                delayedFirst.SetResult(firstResult);
                await firstDelivery.Task.WaitAsync(Deadline);
                f.Source.ExportRelease = Signal();
                var second = f.Save(); Require(second.Accepted, second.Rejection);
                var secondObserved = notifications.Observe(second.Completion,
                    () => { notices.SaveStarted(); secondStarted.SetResult(); }, notices.SaveFinished);
                Require(f.Cache.Status.Saving, "A notification handler blocked save admission or changed busy state.");
                releaseFirst.SetResult(); await firstObserved.WaitAsync(Deadline); await secondStarted.Task.WaitAsync(Deadline);
                Require(events.Last() == "Saving replay…", "A delayed completion replaced the newer save's active notice.");
                f.Source.ExportRelease.SetResult(); Require((await Finish(second)).Succeeded, "Second save failed.");
                await secondObserved.WaitAsync(Deadline);
                Require(events.SequenceEqual(new[] { "Saving replay…", "Replay saved", "Saving replay…", "Replay saved" }), "Save notices were reordered or duplicated.");
            }
            finally { releaseFirst.TrySetResult(); }
        });

        File.WriteAllText(Path.Combine(root, "verification.json"), JsonSerializer.Serialize(new
        {
            result = failed == 0 ? "passed" : "failed", checks = results,
            effects = new { ui = false, capture = false, gpu = false, engineLaunch = false, startupWrites = false },
            limits = "Controlled manifests, files and export outcomes; no playable media, live capture, A/V or hardware acceptance."
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Replay ownership: {results.Count - failed}/{results.Count} passed. {root}");
        return failed == 0 ? 0 : 1;
    }
}
