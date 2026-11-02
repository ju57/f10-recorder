using System.Diagnostics;
using System.Text.Json;

namespace F10Recorder;

// Three actual media children: generated capture, export and full validator.
// This checks backend wiring/lifetime; the separate ID oracle checks precision.
internal static class ReplayIntegrationChecks
{
    sealed class ObservedSource(CaptureSession capture, Action guard, bool corrupt) : IReplaySessionSource
    {
        readonly IReplaySessionSource inner = capture;
        internal readonly List<ReplayProducerProof> Proofs = [];
        public ReplaySessionInfo ReplayInfo => inner.ReplayInfo;
        public bool Stopped => inner.Stopped;
        public string Diagnostics => inner.Diagnostics;
        public bool OutputProducerStopped => inner.OutputProducerStopped;
        public ReplayProducerProof ProducerProof => inner.ProducerProof;
        public double SaveCutoffAt(long trigger) => inner.SaveCutoffAt(trigger);
        public void FreezeAt(long trigger) => inner.FreezeAt(trigger);
        public void StopCapture() => inner.StopCapture();
        public void CheckCaptureHealth(double end) => inner.CheckCaptureHealth(end);
        public List<Segment> ReadSegments() => inner.ReadSegments();
        public void Dispose() => inner.Dispose();
        public void Export(IReadOnlyList<string> arguments, string snapshot) => throw new IOException("Guarded overload required.");
        public void Export(IReadOnlyList<string> arguments, string snapshot, Action<ReplayProducerProof> changed, Action check)
        {
            inner.Export(arguments, snapshot, p => { Proofs.Add(p); changed?.Invoke(p); }, () => { guard(); check?.Invoke(); });
            if (corrupt) File.WriteAllBytes(arguments[^1], new byte[2048]); // Task-owned closed partial only.
        }
        public ReplayValidation ValidateOutput(string path, ReplayInterval interval, Action<ReplayProducerProof> changed, Action check)
            => inner.ValidateOutput(path, interval, p => { Proofs.Add(p); changed?.Invoke(p); }, () => { guard(); check?.Invoke(); });
    }
    static void Require(bool value, string message) { if (!value) throw new IOException(message); }
    internal static int Run(string root, Action outerGuard, bool corrupt = false)
    {
        var timer = Stopwatch.StartNew(); CaptureSession capture = null; ReplayCache cache = null;
        ReplaySaveResult saved = null; object before = null, after = null; int pid = 0, proofCount = 0; string failure = null;
        void Guard()
        {
            outerGuard(); Require(timer.Elapsed.TotalSeconds < 35, "Integration active phase exceeded35seconds; cleanup reserve remains.");
            Require(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length) < 268_435_456,
                "Integration local256MiB cap exceeded.");
        }
        try
        {
            RecorderRuntime.CaptureArgumentProbe = original =>
            {
                var args = original.ToList(); args.InsertRange(args.Count - 1, ["-t", "24"]); return args;
            };
            capture = new CaptureSession(new Settings { FramesPerSecond = 24, BitrateMbps = 5, SystemAudio = false, ReplayMinutes = 1 }, root, true,
                cancelled: () => timer.Elapsed.TotalSeconds > 15);
            pid = capture.ProcessId;
            var source = new ObservedSource(capture, Guard, corrupt);
            cache = new ReplayCache(cacheDirectory: root, cacheBudgetBytes: 268_435_456);
            var id = cache.RegisterSession(source); cache.PreserveSession(id);
            while (source.ReadSegments().Count < 3)
            {
                Guard(); Require(!capture.HasExited && timer.Elapsed.TotalSeconds < 15, "Generated capture did not establish completed inputs.");
                cache.Maintain(id); Thread.Sleep(50);
            }
            long framesBefore = capture.Metrics.EncodedFrames;
            double endBefore = source.ReadSegments()[^1].End;
            before = new { pid, frames = framesBefore, completedEnd = endBefore };
            var submission = cache.TrySave(id, Stopwatch.GetTimestamp(), Path.Combine(root, "clips"));
            Require(submission.Accepted, "Actual backend rejected generated request: " + submission.Rejection);
            while (!submission.Completion.Wait(100)) { Guard(); cache.Maintain(id); }
            saved = submission.Completion.GetAwaiter().GetResult();
            if (corrupt)
                Require(!saved.Succeeded && saved.Validation == null && !Directory.EnumerateFiles(Path.Combine(root, "clips"), "*.mp4").Any(),
                    "The actual backend published a corrupted partial MP4.");
            else Require(saved.Succeeded && saved.Validation?.Kind == "FullDecode", "Actual backend did not validate/publish: " + saved.Error);
            long framesAfter = capture.Metrics.EncodedFrames; double endAfter = source.ReadSegments()[^1].End;
            after = new { pid = capture.ProcessId, frames = framesAfter, completedEnd = endAfter };
            Require(!capture.HasExited && capture.ProcessId == pid && framesAfter > framesBefore && endAfter > endBefore,
                "Generated capture did not advance across the real save.");
            Require(capture.LastExport?.Success == true && capture.LastValidation?.Success == !corrupt && capture.LastValidation.ProducerAbsent
                && capture.LastExport.ProcessId != capture.LastValidation.ProcessId && capture.ExportArguments[^1].EndsWith(".partial.mp4")
                && capture.ValidationArguments.Contains("null"), "Export/validator evidence was overwritten or a child did not exit.");
            proofCount = source.Proofs.Count; Require(proofCount >= 2, "Actual backend lost producer callbacks across export/validation.");
            File.WriteAllText(Path.Combine(root, "arguments.json"), JsonSerializer.Serialize(new { capture.CaptureArguments, capture.ExportArguments, capture.ValidationArguments }));
        }
        catch (Exception ex) { failure = ex.ToString(); }
        finally
        {
            RecorderRuntime.CaptureArgumentProbe = null;
            try { capture?.StopCapture(); }
            catch (Exception ex) { failure = (failure ?? "") + "\nCleanup: " + ex; }
            try { cache?.Dispose(); }
            catch (Exception ex) { failure = (failure ?? "") + "\nCache cleanup: " + ex; }
            finally
            {
                try { capture?.Dispose(); }
                catch (Exception ex) { failure = (failure ?? "") + "\nCapture cleanup: " + ex; }
            }
        }
        bool absent = capture?.CaptureProducerAbsent == true;
        if (!absent) failure = (failure ?? "") + "\nOwned capture absence was not confirmed.";
        File.WriteAllText(Path.Combine(root, "verification.json"), JsonSerializer.Serialize(new { result = failure == null ? "passed" : "failed", error = failure,
            corrupt, saved, before, after, proofCount, capturePid = pid, captureAbsent = absent, elapsedSeconds = timer.Elapsed.TotalSeconds,
            export = capture?.LastExport, validation = capture?.LastValidation,
            children = (pid > 0 ? 1 : 0) + (capture?.LastExport?.ProcessId > 0 ? 1 : 0) + (capture?.LastValidation?.ProcessId > 0 ? 1 : 0),
            limits = "Actual synthetic CaptureSession/ReplayCache/export/validator wiring and advancing generated capture. No live devices, production ledger/calibration, content-ID oracle, worker RPC, latency or hardware qualification." }, new JsonSerializerOptions { WriteIndented = true }));
        return failure == null ? 0 : 1;
    }
}
