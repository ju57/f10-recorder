using System.Diagnostics;
using System.Text.Json;

namespace F10Recorder;

// Tiny NONMEDIA state fixtures. No real recording, codec, catalog or model
// qualification. Literal simulated coefficients are never used for user media.
internal static class ReplayAdmissionReadinessChecks
{
    const double End = 302.1207687;
    enum Scenario { Progress, Immediate, NoProgress, LateProgress, ModelChange, ExporterChange, InputChange,
        HistoryChange, Stop, HealthFailure, WrongScope, MissingCalibration, EmptyInput, ConcurrentRetirement }
    sealed class Source : IReplaySessionSource
    {
        internal readonly List<long> Cutoffs = new();
        internal readonly List<double> Estimates = new();
        internal readonly ReplayReferenceModel Model;
        internal Task Progress;
        internal int Stage, Exports;
        internal bool ExportBeforeEligibility;
        internal bool Disposed, ExportFinished;
        internal readonly ManualResetEventSlim Awaiting = new(), ExportEntered = new(), ReleaseExport = new();
        readonly Scenario scenario;
        int scheduled;
        public ReplaySessionInfo ReplayInfo { get; }
        public bool PreserveEvidence => true;
        public bool Stopped { get; private set; }
        public string Diagnostics => "Explicit NONMEDIA admission-readiness fixture";
        public bool OutputProducerStopped => true; // No process or file writer is ever started.
        public string OutputDemandIdentity => scenario == Scenario.ExporterChange && Volatile.Read(ref Stage) > 0
            ? "changed-exporter" : ReplayReferenceModel.ExporterIdentity;
        public Source(string directory, ReplayVolumeCapacity target, Scenario scenario)
        {
            this.scenario = scenario;
            ReplayInfo = new(directory, Path.Combine(directory, "segments.csv"), 300, 20, 60, "h264_nvenc", ".ts", false, true);
            Model = new("SIMULATED-readiness-control-NOT-qualified", "SIMULATED-scope", target.Volume, target.AllocationUnit,
                0, 0, ["SIMULATED-one", "SIMULATED-two", "SIMULATED-three"]);
            for (int n = 1; n <= 3; n++) File.WriteAllBytes(Path.Combine(directory, "chunk-" + n + ".ts"), new byte[4096]);
            if (scenario == Scenario.EmptyInput) File.WriteAllBytes(Path.Combine(directory, "chunk-1.ts"), []);
            if (scenario == Scenario.Immediate) Stage = 2;
        }
        public double SaveCutoffAt(long timestamp) { Cutoffs.Add(timestamp); return End; }
        public double InputPrerollSeconds => .08;
        public void FreezeAt(long timestamp) { }
        public void StopCapture() { Volatile.Write(ref Stage, 2); Stopped = true; }
        public void CheckCaptureHealth(double end)
        {
            // Retirement of an accepted request may stop acquisition after all
            // requested history completed. That is not a capture-health failure.
            if (Stopped && scenario != Scenario.ConcurrentRetirement) throw new IOException("Controlled source stopped");
            if (scenario == Scenario.HealthFailure && Volatile.Read(ref Stage) > 0) throw new IOException("Controlled unhealthy source");
        }
        public void Dispose() { Disposed = true; }
        public List<Segment> ReadSegments()
        {
            int stage = Volatile.Read(ref Stage);
            var result = new List<Segment> { new() { Path = Path.Combine(ReplayInfo.Directory, "chunk-1.ts"),
                Start = scenario == Scenario.HistoryChange && stage > 0 ? 2.333333 : 1.833333, End = 300.833333 } };
            if (stage >= 1) result.Add(new() { Path = Path.Combine(ReplayInfo.Directory, "chunk-2.ts"), Start = 300.833333, End = 301.833333 });
            if (stage >= 2) result.Add(new() { Path = Path.Combine(ReplayInfo.Directory, "chunk-3.ts"), Start = 301.833333, End = 302.833333 });
            return result;
        }
        public ReplayOutputDemand EstimateOutput(ReplayOutputSelection selection)
        {
            Estimates.Add(selection.End);
            var model = scenario == Scenario.ModelChange && Volatile.Read(ref Stage) > 0 ? Model with { Revision = "SIMULATED-changed" } : Model;
            var demand = scenario == Scenario.MissingCalibration ? ReplayOutputDemand.Unmeasured("Controlled absent calibration")
                : model.Estimate(selection, scenario == Scenario.WrongScope ? "wrong-scope" : model.Scope, ReplayInfo.Directory);
            if (demand.Kind == ReplayOutputEvidenceKind.Unmeasured && Interlocked.Exchange(ref scheduled, 1) == 0)
            {
                Awaiting.Set();
                if (scenario is not (Scenario.NoProgress or Scenario.WrongScope or Scenario.MissingCalibration or Scenario.EmptyInput))
                    Progress = Task.Run(async () =>
                    {
                        await Task.Delay(scenario == Scenario.LateProgress ? 250 : 25);
                        if (scenario == Scenario.InputChange)
                        {
                            string path = Path.Combine(ReplayInfo.Directory, "chunk-1.ts");
                            File.Move(path, path + ".original"); File.WriteAllBytes(path, new byte[4096]);
                        }
                        if (scenario == Scenario.Stop) Stopped = true;
                        Volatile.Write(ref Stage, scenario == Scenario.ConcurrentRetirement ? 2 : 1);
                        await Task.Delay(25); Volatile.Write(ref Stage, 2);
                    });
            }
            return demand;
        }
        public void Export(IReadOnlyList<string> arguments, string snapshot) => throw new IOException("Request overload required");
        public void Export(ReplayExportRequest request, string snapshot, Action<ReplayProducerProof> changed, Action check)
        {
            Exports++; ExportBeforeEligibility |= Volatile.Read(ref Stage) < 1;
            if (scenario == Scenario.ConcurrentRetirement)
            {
                ExportEntered.Set();
                if (!ReleaseExport.Wait(TimeSpan.FromSeconds(2))) throw new IOException("Controlled export release timed out");
            }
            ExportFinished = true;
            throw new IOException("Intentional NONMEDIA exporter stop; no output or publication");
        }
    }
    internal static int Run(string root, string engineRoot)
    {
        root = Path.GetFullPath(root);
        if (Directory.Exists(root)) throw new IOException("Fresh readiness fixture root required");
        Directory.CreateDirectory(root); Log.DirectoryPath = root; MediaDependency.RootDirectory = engineRoot;
        var rows = new List<object>();
        var clock = Stopwatch.StartNew();
        foreach (var scenario in Enum.GetValues<Scenario>())
        {
            string caseRoot = Path.Combine(root, scenario.ToString());
            string directory = Path.Combine(caseRoot, "buffer", "session"), output = Path.Combine(caseRoot, "clips");
            Directory.CreateDirectory(directory); Directory.CreateDirectory(output);
            var source = new Source(directory, ReplayStorage.ObserveCapacity(output), scenario);
            using var cache = new ReplayCache(cacheDirectory: Path.Combine(caseRoot, "buffer"),
                volumeReservations: new ReplayVolumeReservations(Path.Combine(caseRoot, "authority")));
            long requested = Stopwatch.GetTimestamp(); var admissionClock = Stopwatch.StartNew();
            var id = cache.RegisterSession(source);
            SaveSubmission submission; Task retirement = null;
            if (scenario == Scenario.ConcurrentRetirement)
            {
                var pending = Task.Run(() => cache.TrySave(id, requested, output));
                if (!source.Awaiting.Wait(TimeSpan.FromSeconds(1))) throw new IOException("Controlled admission not reached");
                // Both later requests contend with the original atomic admission.
                var second = Task.Run(() => cache.TrySave(id, requested + 1, output));
                retirement = Task.Run(async () => await cache.RetireSessionAsync(id));
                submission = pending.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
                bool secondRejected = !second.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult().Accepted;
                bool entered = source.ExportEntered.Wait(TimeSpan.FromSeconds(1));
                rows.Add(new { name = "later-save-rejected-and-retirement-joins-accepted-export", passed =
                    submission.Accepted && secondRejected && entered && !source.Disposed && !retirement.IsCompleted });
                source.ReleaseExport.Set();
            }
            else submission = cache.TrySave(id, requested, output);
            double admissionMilliseconds = admissionClock.Elapsed.TotalMilliseconds;
            if (source.Progress != null) source.Progress.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            var completion = submission.Accepted ? submission.Completion.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult() : null;
            if (retirement != null) retirement.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            bool expectedAccepted = scenario is Scenario.Progress or Scenario.Immediate or Scenario.ConcurrentRetirement;
            rows.Add(new { name = scenario.ToString(), passed = submission.Accepted == expectedAccepted,
                submission.Accepted, submission.Rejection, admissionMilliseconds, estimates = source.Estimates.Count });
            rows.Add(new { name = scenario + "-frozen-cutoff-and-estimates", passed = source.Cutoffs.SequenceEqual(new[] { requested })
                && source.Estimates.All(end => end == End) });
            rows.Add(new { name = scenario + "-no-premature-export-or-publication", passed = !source.ExportBeforeEligibility
                && (expectedAccepted || source.Exports == 0) && completion?.Succeeded != true && !Directory.EnumerateFiles(output).Any() });
            if (expectedAccepted)
                rows.Add(new { name = scenario + "-timing-includes-admission-and-zero-queue", passed = completion?.Timing is { } timing
                    && timing.Requested == requested && timing.Accepted >= timing.Requested && timing.QueueMilliseconds == 0
                    && (scenario == Scenario.Immediate ? timing.AdmissionReadinessStarted == null
                        : timing.AdmissionReadinessStarted >= requested && timing.AdmissionReadinessCompleted >= timing.AdmissionReadinessStarted
                            && timing.Accepted >= timing.AdmissionReadinessCompleted) });
        }
        int failed = rows.Count(row => !JsonSerializer.SerializeToElement(row).GetProperty("passed").GetBoolean());
        File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new { failed, rows,
            claim = "NONMEDIA simulated state controls only; no calibrated profile, real source/media/export/publication or performance result.",
            seconds = clock.Elapsed.TotalSeconds }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Admission readiness controls: {rows.Count - failed}/{rows.Count} passed");
        return failed == 0 ? 0 : 1;
    }
}
