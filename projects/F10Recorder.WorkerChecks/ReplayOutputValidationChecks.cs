using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace F10Recorder;

// Independent behavioral fixtures. No playable media or native producer exists.
internal static class ReplayOutputValidationChecks
{
    static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    static async Task<ReplaySaveResult> Finish(SaveSubmission save)
    {
        Require(save.Accepted, "Expected accepted request: " + save.Rejection);
        return await save.Completion.WaitAsync(Deadline);
    }

    sealed class Source : IReplaySessionSource
    {
        public ReplaySessionInfo ReplayInfo { get; }
        public bool Stopped { get; private set; }
        public string Diagnostics => "Independent nonmedia source";
        public string OutputDemandIdentity => Exporter;
        public bool OutputProducerStopped => ProducerAbsent;
        internal bool ProducerAbsent = true;
        internal string Exporter = "independent-exporter-v1";
        internal int Estimates, Exports;
        internal Action<IReadOnlyList<string>, string> ExportAction;
        internal Func<ReplayOutputSelection, int, ReplayOutputDemand> Estimate;
        internal Source(string directory)
        {
            Directory.CreateDirectory(directory);
            ReplayInfo = new(directory, Path.Combine(directory, "segments.csv"), 60, 5, 60, "h264_nvenc", ".ts", false, false);
            Segments(1);
        }
        internal string Chunk(int n) => Path.Combine(ReplayInfo.Directory, "chunk-" + n.ToString("D9") + ".ts");
        internal void Segments(int count)
        {
            var rows = new List<string>();
            for (int i = 0; i < count; i++)
            {
                File.WriteAllBytes(Chunk(i), new byte[2048]);
                rows.Add(Path.GetFileName(Chunk(i)) + "," + i.ToString(CultureInfo.InvariantCulture) + "," + (i + 1).ToString(CultureInfo.InvariantCulture));
            }
            File.WriteAllText(ReplayInfo.Manifest, string.Join("\n", rows) + "\n");
        }
        public ReplayOutputDemand EstimateOutput(ReplayOutputSelection selection) => Estimate(selection, ++Estimates);
        public double SaveCutoffAt(long triggerTimestamp) => triggerTimestamp / 1000.0;
        public void FreezeAt(long timestamp) { }
        public void StopCapture() => Stopped = true;
        public void CheckCaptureHealth(double completedEnd) { }
        public void Export(IReadOnlyList<string> arguments, string snapshot)
        {
            Exports++;
            ExportAction?.Invoke(arguments, snapshot);
            if (!File.Exists(arguments[^1])) File.WriteAllBytes(arguments[^1], new byte[2048]);
        }
        public void Dispose() { }
    }

    sealed class Files : ReplayFiles
    {
        internal readonly string Root, Destination;
        internal bool SameVolume, FailCleanup;
        internal int Copies, Publications;
        internal long CacheFree = 1_000_000, OutputFree = 1_000_000;
        internal string OutputVolume = "volume-B";
        internal string OutputIdentitySuffix = "";
        internal long OutputUnit = 4096;
        internal Action<string> Copied;
        internal readonly ConcurrentQueue<string> Observations = new();
        internal Files(string root, string destination) { Root = root; Destination = destination; }
        bool IsOutput(string path) => Path.GetFullPath(path).StartsWith(Destination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, Destination, StringComparison.OrdinalIgnoreCase);
        bool IsSnapshot(string path) => path.Contains(Path.DirectorySeparatorChar + "save-", StringComparison.OrdinalIgnoreCase);
        long Allocated(string path) => ((new FileInfo(path).Length + 4095) / 4096) * 4096;
        internal override ReplayFileExtent Observe(string path)
        {
            Observations.Enqueue("extent:" + path);
            return new(IsOutput(path) && !SameVolume ? OutputVolume : "volume-A", Path.GetFullPath(path) + (IsOutput(path) ? OutputIdentitySuffix : ""), Allocated(path), new FileInfo(path).Length,
                IsOutput(path) ? OutputUnit : 4096);
        }
        internal override ReplayFileExtent ObserveInventory(string path, ReplayFileExtent enumerated) => Observe(path);
        internal override ReplayVolumeCapacity ObserveCapacity(string directory)
        {
            Observations.Enqueue("capacity:" + directory);
            bool output = IsOutput(directory);
            long writtenCache = Directory.GetFiles(Root, "*", SearchOption.AllDirectories).Where(IsSnapshot).Sum(Allocated);
            long writtenOutput = Directory.GetFiles(Destination, "*.partial.mp4").Sum(Allocated);
            var observed = new ReplayVolumeCapacity(output && !SameVolume ? OutputVolume : "volume-A", output ? OutputUnit : 4096,
                SameVolume ? CacheFree - writtenCache - writtenOutput : output ? OutputFree - writtenOutput : CacheFree - writtenCache);
            return observed;
        }
        internal override bool TryHardLink(string copy, string original) => false;
        internal override void Copy(string original, string copy) { base.Copy(original, copy); Copies++; Copied?.Invoke(copy); }
        internal override void Publish(string partial, string final) { base.Publish(partial, final); Publications++; }
        internal override void DeleteDirectory(string path)
        {
            if (FailCleanup) throw new IOException("Independent cleanup failure");
            base.DeleteDirectory(path);
        }
        internal override void DeleteFile(string path)
        {
            if (FailCleanup) throw new IOException("Independent cleanup failure");
            base.DeleteFile(path);
        }
    }

    sealed class Fixture : IDisposable
    {
        internal readonly string Destination, Existing;
        internal readonly Source Source;
        internal readonly Files Files;
        internal readonly ReplayCache Cache;
        internal readonly ReplaySessionId Id;
        internal readonly ConcurrentQueue<string> Diagnostics = new();
        internal Fixture(string root, Func<Task> wait = null)
        {
            root = Path.GetFullPath(root);
            Destination = Path.Combine(root, "clips"); Directory.CreateDirectory(Destination);
            Existing = Path.Combine(Destination, "existing.mp4"); File.WriteAllText(Existing, "independent-preservation-sentinel");
            Files = new(root, Destination);
            Source = new(Path.Combine(root, "buffer", "session"));
            Source.Estimate = (s, _) => Demand(s);
            Cache = new(Files, waitForSegment: wait, diagnostic: Diagnostics.Enqueue, cacheDirectory: Path.Combine(root, "buffer"));
            Id = Cache.RegisterSession(Source);
        }
        internal ReplayOutputDemand Demand(ReplayOutputSelection s, long peak = 8192) => new(ReplayOutputEvidenceKind.CalibratedEstimate,
            "independent-fixture-r1", s.Identity, s.ExporterIdentity, Files.SameVolume ? "volume-A" : "volume-B", 4096, peak, true, null,
            s.Closed ? null : 12288);
        internal SaveSubmission Save(long end = 1000) => Cache.TrySave(Id, end, Destination);
        internal void Preserved()
        {
            Require(File.ReadAllText(Existing) == "independent-preservation-sentinel", "Pre-existing saved file changed.");
            Require(File.ReadAllBytes(Source.Chunk(0)).SequenceEqual(new byte[2048]), "Protected original changed or disappeared.");
        }
        public void Dispose() { Files.FailCleanup = false; Source.ProducerAbsent = true; Cache.Dispose(); }
    }

    internal static int Run(string root) => RunAsync(Path.GetFullPath(root)).GetAwaiter().GetResult();
    static async Task<int> RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        var results = new List<object>();
        int failed = 0;
        async Task Check(string name, Func<string, Task> action)
        {
            try { await action(Path.Combine(root, name)); results.Add(new { name, result = "passed" }); }
            catch (Exception ex) { failed++; results.Add(new { name, result = "failed", error = ex.ToString() }); }
        }
        foreach (bool same in new[] { false, true })
            foreach (bool sufficient in new[] { false, true })
                await Check((same ? "same" : "different") + "-volume-" + (sufficient ? "exact" : "one-unit-short"), async path =>
                {
                    using var f = new Fixture(path); f.Files.SameVolume = same;
                    // One 4 KiB copy + one 4 KiB concat + 8 KiB output on A; only 8 KiB output on B.
                    f.Files.CacheFree = same ? (sufficient ? 16384 : 12288) : 8192;
                    f.Files.OutputFree = sufficient ? 8192 : 4096;
                    var submission = f.Save();
                    if (sufficient)
                    {
                        var result = await Finish(submission);
                        Require(result.Succeeded && f.Files.Publications == 1, "Exact capacity failed: " + result.Error);
                    }
                    else Require(!submission.Accepted && f.Source.Exports == 0 && f.Files.Copies == 0, "Insufficient combined/target capacity admitted writes.");
                    f.Preserved();
                });

        foreach (bool insufficient in new[] { false, true })
            await Check("partial-materialization-" + (insufficient ? "later-short" : "exact-remainder"), async path =>
            {
                using var f = new Fixture(path); f.Files.CacheFree = 8192; f.Files.OutputFree = 8192;
                f.Source.ExportAction = (args, _) =>
                {
                    File.WriteAllBytes(args[^1], new byte[4096]);
                    while (f.Files.Observations.TryDequeue(out _)) { }
                    if (insufficient) f.Files.OutputFree = 8191; // Fresh free 4095, remaining output 4096.
                    f.Cache.Maintain(f.Id);
                    var events = f.Files.Observations.ToArray();
                    int prefixIndex = Array.IndexOf(events, "extent:" + args[^1]);
                    int capacityIndex = Array.IndexOf(events, "capacity:" + f.Destination);
                    Require(prefixIndex >= 0 && capacityIndex > prefixIndex,
                        "Fresh capacity was sampled before observing partial allocation.");
                };
                var result = await Finish(f.Save());
                Require(result.Succeeded != insufficient && f.Files.Publications == (insufficient ? 0 : 1), "Remaining allocation capacity decision was wrong: " + result.Error);
                f.Preserved();
            });

        foreach (bool sufficient in new[] { false, true })
            await Check("tail-extension-" + (sufficient ? "exact" : "denied"), async path =>
            {
                var waiting = Signal(); var release = Signal();
                using var f = new Fixture(path, () => { waiting.TrySetResult(); return release.Task; });
                f.Files.CacheFree = 12288; f.Files.OutputFree = sufficient ? 16384 : 12288;
                f.Source.Estimate = (s, _) => f.Demand(s, s.Closed ? 16384 : 8192);
                var save = f.Save(2000);
                try
                {
                    Require(save.Accepted, save.Rejection); await waiting.Task.WaitAsync(Deadline);
                    Require(f.Files.Copies == 0, "Open-tail save wrote before closure.");
                    f.Source.Segments(2); release.TrySetResult();
                    var result = await Finish(save);
                    Require(result.Succeeded == sufficient && f.Source.Exports == (sufficient ? 1 : 0), "Tail extension decision was wrong: " + result.Error);
                    if (!sufficient) Require(f.Files.Copies == 0, "Rejected tail demand copied inputs before extension.");
                    f.Preserved();
                }
                finally { release.TrySetResult(); }
            });

        foreach (string change in new[] { "unmeasured", "revision", "exporter", "selection" })
            await Check("binding-change-" + change, async path =>
            {
                using var f = new Fixture(path);
                f.Source.Estimate = (s, call) =>
                {
                    var demand = f.Demand(s);
                    if (call == 1) return demand;
                    return change switch
                    {
                        "unmeasured" => ReplayOutputDemand.Unmeasured("Independent qualification withdrawal"),
                        "revision" => demand with { Revision = "independent-r2" },
                        "exporter" => demand with { ExporterIdentity = "other-exporter" },
                        _ => demand with { SelectionIdentity = "stale-selection" }
                    };
                };
                var result = await Finish(f.Save());
                Require(!result.Succeeded && f.Files.Copies == 0 && f.Source.Exports == 0 && f.Files.Publications == 0,
                    "Changed reservation evidence proceeded to writes."); f.Preserved();
            });

        foreach (string change in new[] { "volume", "unit", "capacity" })
            await Check("later-output-" + change, async path =>
            {
                using var f = new Fixture(path);
                f.Files.Copied = _ =>
                {
                    if (change == "volume") f.Files.OutputVolume = "volume-C";
                    else if (change == "unit") f.Files.OutputUnit = 8192;
                    else f.Files.OutputFree = 4096;
                };
                var result = await Finish(f.Save());
                Require(!result.Succeeded && f.Source.Exports == 0 && f.Files.Publications == 0, "Later target/capacity failure reached exporter.");
                f.Preserved();
            });

        await Check("publication-survives-cleanup", async path =>
        {
            using var f = new Fixture(path); f.Files.FailCleanup = true;
            var result = await Finish(f.Save());
            Require(result.Succeeded && File.Exists(result.File) && f.Files.Publications == 1, "Cleanup reversed publication success.");
            Require(f.Diagnostics.Any(s => s.Contains("cleanup failed", StringComparison.OrdinalIgnoreCase)), "Cleanup failure had no diagnostic.");
            f.Preserved();
        });

        await Check("unknown-producer-retains-claims-and-files", async path =>
        {
            using var f = new Fixture(path);
            string snapshot = null, partial = null;
            f.Source.ExportAction = (args, snap) =>
            {
                snapshot = snap; partial = args[^1]; File.WriteAllBytes(partial, new byte[2048]);
                f.Source.ProducerAbsent = false;
                throw new IOException("Independent producer absence unknown");
            };
            var result = await Finish(f.Save());
            Require(!result.Succeeded && f.Files.Publications == 0, "Unknown producer published success.");
            Require(File.Exists(partial) && Directory.Exists(snapshot), "Unknown producer lost owned output/snapshot material.");
            Require(!f.Save().Accepted, "Unknown producer released future claims and accepted another save.");
            await f.Cache.RetireSessionAsync(f.Id).WaitAsync(Deadline);
            Require(Directory.Exists(f.Source.ReplayInfo.Directory) && Directory.Exists(snapshot) && File.Exists(partial),
                "Retirement deleted resources while producer absence remained unknown.");
            f.Preserved();
        });

        await Check("observed-peak-overrun-retains-evidence", async path =>
        {
            using var f = new Fixture(path);
            f.Source.Estimate = (s, _) => f.Demand(s, 12288);
            string snapshot = null, partial = null;
            f.Source.ExportAction = (args, snap) =>
            {
                snapshot = snap; partial = args[^1];
                File.WriteAllBytes(partial, new byte[16384]);
            };
            var result = await Finish(f.Save());
            Require(!result.Succeeded && f.Files.Publications == 0, "Observed peak overrun published a qualified result.");
            Require(File.Exists(partial) && Directory.Exists(snapshot), "Observed peak overrun lost investigation evidence.");
            f.Preserved();
            f.Source.ExportAction = null;
            var next = await Finish(f.Save());
            Require(next.Succeeded, "Confirmed absent producer left a blanket admission lock after overrun: " + next.Error);
            Require(File.Exists(partial) && Directory.Exists(snapshot), "Later save deleted earlier rejected evidence.");
            await f.Cache.RetireSessionAsync(f.Id).WaitAsync(Deadline);
            Require(File.Exists(partial) && Directory.Exists(snapshot), "Retirement deleted earlier overrun evidence.");
            var retained = f.Cache.Status;
            Require(retained.StorageError == null && retained.CacheBytes > 0, "Retired overrun evidence became unobservable or uncharged.");
            f.Preserved();
        });

        await Check("materialized-output-identity-change", async path =>
        {
            using var f = new Fixture(path);
            string snapshot = null, partial = null;
            f.Source.ExportAction = (args, snap) =>
            {
                snapshot = snap; partial = args[^1];
                File.WriteAllBytes(partial, new byte[4096]);
                f.Cache.Maintain(f.Id); // First materialization establishes physical identity.
                f.Files.OutputIdentitySuffix = "-replacement";
                f.Cache.Maintain(f.Id); // Same pathname, volume, unit and bytes; different file.
            };
            var result = await Finish(f.Save());
            Require(!result.Succeeded && f.Files.Publications == 0, "Changed materialized physical identity was credited and published.");
            Require(File.Exists(partial) && Directory.Exists(snapshot), "Identity invalidation deleted investigation evidence.");
            f.Preserved();
            f.Source.ExportAction = null;
            Require((await Finish(f.Save())).Succeeded, "Confirmed absent producer left admission locked after identity invalidation.");
            Require(File.Exists(partial) && Directory.Exists(snapshot), "Later save deleted identity invalidation evidence.");
            await f.Cache.RetireSessionAsync(f.Id).WaitAsync(Deadline);
            Require(File.Exists(partial) && Directory.Exists(snapshot), "Retirement deleted earlier identity invalidation evidence.");
            var retained = f.Cache.Status;
            Require(retained.StorageError == null && retained.CacheBytes > 0, "Retired identity evidence became unobservable or uncharged.");
            f.Preserved();
        });

        File.WriteAllText(Path.Combine(root, "verification.json"), JsonSerializer.Serialize(new
        {
            result = failed == 0 ? "passed" : "failed", checks = results,
            attribution = "Initial capacity/binding/tail/preservation behaviors frozen before patch read. Producer-absence, overrun and materialized-identity cases added after contract clarification during integration; later retirement assertions follow independent Standards/Spec reviewer findings. Parent executes this check source.",
            effects = "Tiny nonmedia files and finite fake source only; no engine, capture, GPU, UI, worker, startup or installation.",
            limits = "Synthetic actual-volume observations validate integration, not physical filesystem qualification, calibrated bounds, producer control, queue/manual/global claims or live acceptance."
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Independent replay output validation: {results.Count - failed}/{results.Count} passed. {root}");
        return failed == 0 ? 0 : 1;
    }
}
