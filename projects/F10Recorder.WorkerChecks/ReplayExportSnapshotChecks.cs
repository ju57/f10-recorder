using System.Diagnostics;
using System.Text.Json;
using System.Security.Cryptography;

namespace F10Recorder;

internal static class ReplayExportSnapshotChecks
{
    sealed class Source : IReplaySessionSource
    {
        readonly IReplaySessionSource backend;
        readonly ReplayReferenceModel model;
        readonly List<Segment> segments = [];
        internal readonly List<object> Events = [];
        internal double MaximumStorageWork, MaximumStorageGap;
        public ReplaySessionInfo ReplayInfo { get; }
        public bool Stopped => true;
        public string Diagnostics => backend.Diagnostics;
        public ReplayProducerProof ProducerProof => backend.ProducerProof;
        public string OutputDemandIdentity => ReplayReferenceModel.ExporterIdentity;
        public bool OutputProducerStopped => backend.OutputProducerStopped;
        internal Source(string directory, IReplaySessionSource backend)
        {
            this.backend = backend; Directory.CreateDirectory(directory);
            ReplayInfo = new(directory, Path.Combine(directory, "segments.csv"), 300, 20, 60, "h264_nvenc", ".ts", false, false);
            for (int n = 0; n < 300; n++)
            {
                string path = Path.Combine(directory, $"chunk-{n:D9}.ts");
                File.WriteAllBytes(path, new byte[1024]); segments.Add(new() { Path = path, Start = n, End = n + 1 });
            }
            var target = ReplayStorage.ObserveCapacity(directory);
            model = new("controlled-snapshot-not-qualification", new string('E', 64), target.Volume, target.AllocationUnit, 0, 0,
                [new string('1', 64), new string('2', 64), new string('3', 64)]);
        }
        public List<Segment> ReadSegments() => segments.Select(s => new Segment { Path = s.Path, Start = s.Start, End = s.End }).ToList();
        public double SaveCutoffAt(long at) => 300;
        public void FreezeAt(long at) { }
        public void StopCapture() { }
        public void CheckCaptureHealth(double end) { }
        public ReplayOutputDemand EstimateOutput(ReplayOutputSelection selection) => model.Estimate(selection, model.Scope, ReplayInfo.Directory);
        public void Export(IReadOnlyList<string> arguments, string snapshot) => throw new IOException("Guarded export overload required.");
        public void Export(IReadOnlyList<string> arguments, string snapshot, Action<ReplayProducerProof> changed, Action check)
        {
            long last = Stopwatch.GetTimestamp();
            void Check()
            {
                double gap = Stopwatch.GetElapsedTime(last).TotalMilliseconds;
                var timer = Stopwatch.StartNew();
                try { check(); }
                finally
                {
                    MaximumStorageWork = Math.Max(MaximumStorageWork, timer.Elapsed.TotalMilliseconds);
                    MaximumStorageGap = Math.Max(MaximumStorageGap, gap + timer.Elapsed.TotalMilliseconds);
                    Events.Add(new { stage = "storage", gap, work = timer.Elapsed.TotalMilliseconds });
                    last = Stopwatch.GetTimestamp();
                }
            }
            backend.Export(["-hide_banner", "-loglevel", "warning", "-nostdin", "-re", "-f", "lavfi", "-i",
                "color=c=black:s=16x16:r=10", "-t", "1.5", "-f", "null", "-"], snapshot, proof =>
                {
                    var timer = Stopwatch.StartNew();
                    try { changed(proof); }
                    finally { Events.Add(new { stage = proof.Sealed ? "seal" : "unseal", work = timer.Elapsed.TotalMilliseconds }); }
                }, Check);
            File.WriteAllBytes(arguments[^1], new byte[2048]); // Exact-size nonmedia fixture after owned CPU producer absence.
        }
        public void Dispose() { }
    }
    internal static int Run(string root, string engineRoot, string contextPlan = null, string contextHash = null)
    {
        using var owned = PilotStorageObservation.Create(root);
        MediaDependency.RootDirectory = Path.GetFullPath(engineRoot); Log.DirectoryPath = root;
        var timer = Stopwatch.StartNew(); CaptureSession capture = null; Source source = null; ReplayCache cache = null;
        var diagnostics = new List<string>(); ReplaySaveResult saved = null; Exception failure = null;
        ReplayCalibrationRun.Sampler sampler = null;
        try
        {
            if (contextPlan != null)
            {
                using var input = File.OpenRead(contextPlan);
                if (input.Length > 65536 || Convert.ToHexString(SHA256.HashData(input)) != contextHash) throw new IOException("Snapshot context plan changed.");
                var plan = JsonSerializer.Deserialize<CalibrationPlan>(File.ReadAllText(contextPlan));
                if (Path.GetFullPath(plan.Root) != Path.GetFullPath(root) || plan.BudgetBytes != 96_000_000_000
                    || plan.FloorBytes != 60_000_000_000 || plan.FileLimit != 12288 || plan.DirectoryLimit != 192
                    || plan.RetainedRoots.Length != 16 || plan.RetainedBudgetBytes != 56_000_000_000)
                    throw new IOException("Native snapshot context differs from its admitted scope.");
                // Match the full calibration route: acquire retained metadata
                // before any producer exists, then establish a fresh boundary.
                sampler = new(plan, active: false);
                sampler.BeginActive();
            }
            RecorderRuntime.CaptureArgumentProbe = original => [ "-hide_banner", "-loglevel", "warning", "-y",
                "-stats_period", "0.25", "-progress", "pipe:1", "-f", "lavfi", "-i", "color=c=black:s=16x16:r=10",
                "-t", "1.1", "-c:v", "mpeg2video", "-g", "10", "-an", "-f", "segment", "-segment_time", "1",
                "-segment_format", "mpegts", "-reset_timestamps", "1", .. ReplayManifestStream.OutputArguments(), original[^1] ];
            capture = new(new Settings { FramesPerSecond = 10, BitrateMbps = 1, SystemAudio = false }, Path.Combine(root, "software"), true);
            while (!capture.HasExited) { if (timer.Elapsed.TotalSeconds > 10) throw new IOException("CPU source deadline."); Thread.Sleep(10); }
            capture.StopCapture();
            if (capture.CaptureExitCode != 0 || !capture.CaptureProducerAbsent) throw new IOException("CPU source closure failed.");
            source = new(Path.Combine(root, "buffer", "session-fixture"), capture);
            var claims = new ReplayVolumeReservations(Path.Combine(root, "calibration-ownership"), ReplayStorageSafety.MinimumBytes);
            cache = new(diagnostic: message => diagnostics.Add(message), cacheDirectory: Path.Combine(root, "buffer"), volumeReservations: claims);
            var id = cache.RegisterSession(source);
            var save = cache.TrySave(id, 0, Path.Combine(root, "output"));
            if (!save.Accepted) throw new IOException("Snapshot control admission rejected: " + save.Rejection);
            // Includes native snapshot preparation under retained-root I/O,
            // matching the full route's existing export-controller ceiling.
            if (!save.Completion.Wait(130000)) throw new IOException("Snapshot/export130second deadline.");
            saved = save.Completion.GetAwaiter().GetResult();
            if (!saved.Succeeded) throw new IOException(saved.Error);
            if (capture.LastExport?.Success != true || source.MaximumStorageGap > 500) throw new IOException("Actual guarded CPU export did not retain500mscontinuity.");
            sampler?.StopAndCheck();
        }
        catch (Exception ex) { failure = ex; }
        finally { RecorderRuntime.CaptureArgumentProbe = null; cache?.Dispose(); capture?.Dispose(); sampler?.Dispose(); }
        File.WriteAllText(Path.Combine(root, "verification.json"), JsonSerializer.Serialize(new { result = failure == null ? "passed" : "failed",
            error = failure?.ToString(), saved, diagnostics, events = source?.Events, maximumStorageWorkMs = source?.MaximumStorageWork,
            maximumStorageGapMs = source?.MaximumStorageGap, export = capture?.LastExport, elapsedSeconds = timer.Elapsed.TotalSeconds,
            contextSamplerMaximumActiveGapMs = sampler?.MaximumActiveGap, contextSamplerPeakUsedBytes = sampler?.PeakUsed,
            newMediaChildren = 2, effects = "300tiny nonmedia inputs, actual native snapshot/cache/ledger and actual CaptureSession/guarded CPU export; one1.1second16x16CPU source and one1.5secondnull-output CPU export. No desktop/audio/GPU/window/model activation. Final2KiBstand-in is not qualified media." },
            new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Native300-input guarded export: " + (failure == null ? "passed" : "failed")); return failure == null ? 0 : 1;
    }
}
