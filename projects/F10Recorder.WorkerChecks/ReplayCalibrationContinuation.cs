using System.Diagnostics;
using System.Text.Json;
using Forms = System.Windows.Forms;

namespace F10Recorder;

internal sealed record CalibrationContinuationPlan(CalibrationPlan Plan, string PriorRoot, string PriorAdmission,
    string PriorAdmissionHash, string PriorAudit, Dictionary<string, string> SourceRoots, string[] FailedContinuations = null);

internal static partial class ReplayCalibrationRun
{
    internal static int ContinuationChecks(string path)
    {
        var c = JsonSerializer.Deserialize<CalibrationContinuationPlan>(File.ReadAllBytes(path), Json);
        ValidateContinuation(c);
        int rejected = 0;
        var invalid = new[] {
            c with { Plan = c.Plan with { MaximumChildren = 430 } },
            c with { Plan = c.Plan with { RetainedChildren = 422 } },
            c with { Plan = c.Plan with { RetainedElapsedSeconds = double.NaN } },
            c with { Plan = c.Plan with { RetainedElapsedSeconds = c.Plan.DeadlineSeconds - 100 } },
            c with { Plan = c.Plan with { RetainedOfflineSeconds = c.Plan.OfflineDeadlineSeconds - 100 } },
            c with { SourceRoots = c.SourceRoots.Where(k => k.Key != "H02").ToDictionary() },
            c with { PriorAdmissionHash = new string('0', 64) },
            c with { Plan = c.Plan with { Root = Path.Combine(c.PriorRoot, "nested") } }
        };
        foreach (var candidate in invalid)
        {
            try { ValidateContinuation(candidate); }
            catch (IOException) { rejected++; }
        }
        Require(rejected == invalid.Length, "Continuation admitted an invalid resource/provenance boundary.");
        Console.WriteLine($"Continuation admission controls passed: valid plus {rejected} rejected boundaries; no media/child.");
        return 0;
    }
    internal static void ValidateContinuation(CalibrationContinuationPlan continuation)
    {
        var p = continuation?.Plan;
        int priorFailed = continuation?.FailedContinuations?.Length ?? 0;
        Require(priorFailed is 0 or 1 && p != null && p.Order != null && p.Order.SequenceEqual(Order)
            && p.MaximumChildren == 431 + 8 * priorFailed && p.RetainedChildren == 423 + 8 * priorFailed
            && p.BudgetBytes == 144_000_000_000 && p.StopBytes == 142_500_000_000 && p.FloorBytes == 60_000_000_000
            && p.DeadlineSeconds == 46800 + 1800 * priorFailed && p.OfflineDeadlineSeconds == 28800 + 1800 * priorFailed && p.DirectoryLimit == 448 && p.FileLimit == 24576
            && p.RetainedBudgetBytes == 124_000_000_000 && p.OutsideBuildAllowanceBytes == 2_000_000_000
            && p.OutputComponentBytes == 2_000_000_000 && p.RetainedRoots.Length == 29 + priorFailed
            && p.RetainedRoots.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 29 + priorFailed
            && p.RetainedRoots.All(Directory.Exists), "Continuation resource boundary changed.");
        Require(double.IsFinite(p.RetainedElapsedSeconds) && p.RetainedElapsedSeconds >= 0
            && p.RetainedElapsedSeconds + 1800 + 720 <= p.DeadlineSeconds
            && p.RetainedOfflineSeconds >= 0 && p.RetainedOfflineSeconds <= p.RetainedElapsedSeconds
            && p.RetainedOfflineSeconds + 240 + 720 <= p.OfflineDeadlineSeconds, "Complete continuation and audit cannot fit.");
        Require(!Directory.Exists(p.Root) && !File.Exists(p.Root)
            && p.RetainedRoots.All(r => !Path.GetFullPath(p.Root).StartsWith(Path.GetFullPath(r).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            && p.RetainedRoots.Contains(continuation.PriorRoot, StringComparer.OrdinalIgnoreCase), "Continuation needs a fresh independent root and retained original batch.");
        Require(continuation.SourceRoots != null && continuation.SourceRoots.Count == 4 && Order.Take(4).All(label =>
            continuation.SourceRoots.GetValueOrDefault(label) == Path.Combine(continuation.PriorRoot, label)),
            "Only the four independently completed original cases may be retained.");
        Require(HashFile(continuation.PriorAdmission) == continuation.PriorAdmissionHash, "Original recording admission changed.");
        using var audit = JsonDocument.Parse(File.ReadAllBytes(continuation.PriorAudit));
        Require(audit.RootElement.GetProperty("result").GetString() == "failed-batch-absence-and-preservation-audited"
            && !audit.RootElement.GetProperty("batchPassed").GetBoolean()
            && audit.RootElement.GetProperty("cumulativeChildren").GetInt32() == 423, "Original failed attempt must remain failed and preservation-audited.");
        using var failed = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(continuation.PriorRoot, "failure.json")));
        Require(failed.RootElement.GetProperty("error").GetString().Contains("foreground", StringComparison.OrdinalIgnoreCase)
            && failed.RootElement.GetProperty("samples").GetArrayLength() == 4
            && !Directory.Exists(Path.Combine(continuation.PriorRoot, "H03"))
            && !File.Exists(Path.Combine(continuation.PriorRoot, "H01", "capture-arguments.json")),
            "Retained prefix must end before an unadmitted H01 capture; do not reuse a failed acquisition.");
        foreach (string stopped in continuation.FailedContinuations ?? [])
        {
            using var previous = JsonDocument.Parse(File.ReadAllBytes(stopped));
            Require(previous.RootElement.GetProperty("result").GetString() == "failed-batch-absence-and-preservation-audited"
                && !previous.RootElement.GetProperty("batchPassed").GetBoolean()
                && previous.RootElement.GetProperty("cumulativeChildren").GetInt32() == 431,
                "The stopped continuation must retain its failure, full child charge and preservation audit.");
        }
    }

    internal static int Continue(string path, string expectedPlanHash)
    {
        Require(new FileInfo(path).Length <= 131072 && HashFile(path) == expectedPlanHash, "Continuation admission changed.");
        var continuation = JsonSerializer.Deserialize<CalibrationContinuationPlan>(File.ReadAllBytes(path), Json);
        ValidateContinuation(continuation);
        var plan = continuation.Plan;
        foreach (var input in plan.Files) Require(HashFile(input.Key) == input.Value, "Bound continuation input changed: " + input.Key);
        MediaDependency.RootDirectory = plan.EngineRoot;
        string engine = MediaDependency.ResolveExecutable();
        Require(plan.Files.GetValueOrDefault(Path.GetFullPath(engine)) == MediaDependency.ExecutableSha256.ToUpperInvariant(), "Managed engine not bound.");
        Require(plan.Files.ContainsKey(Path.GetFullPath(typeof(ReplayCalibrationRun).Assembly.Location))
            && plan.Files.ContainsKey(Path.GetFullPath(Environment.ProcessPath))
            && plan.Files.ContainsKey(Path.GetFullPath(typeof(ReplayCalibrationCatalog).Assembly.Location))
            && plan.Files.ContainsKey(Path.GetFullPath(typeof(ReplayReferenceModel).Assembly.Location)), "Actual continuation controller/consumer not bound.");
        using var prior = JsonDocument.Parse(File.ReadAllBytes(continuation.PriorAdmission));
        using (var endpoint = new LoopbackAudio())
        {
            var environment = prior.RootElement.GetProperty("environment");
            var bounds = Forms.Screen.PrimaryScreen.Bounds;
            Require(bounds.Width == 1920 && bounds.Height == 1080
                && environment.GetProperty("machine").GetString() == Environment.MachineName
                && environment.GetProperty("os").GetString() == Environment.OSVersion.VersionString
                && environment.GetProperty("EndpointId").GetString() == endpoint.EndpointId
                && environment.GetProperty("SampleRate").GetInt32() == endpoint.SampleRate
                && environment.GetProperty("Channels").GetInt32() == endpoint.Channels
                && environment.GetProperty("FfmpegFormat").GetString() == endpoint.FfmpegFormat, "Original reference environment changed.");
        }
        var model = JsonSerializer.Deserialize<ReplayReferenceModel>(File.ReadAllBytes(Path.Combine(continuation.PriorRoot, "frozen-training-model.json")), Json);
        Require(model.Revision == ReplayCalibrationCatalog.ModelRevision(model.Scope, model.Volume, model.AllocationUnit,
            model.FinalResidual, model.SampledFinalizationExtra, model.TrainingReceipts, captureHeadroom: model.CaptureHeadroom), "Original frozen model no longer applies to this consumer/host.");
        var sources = Order.Take(4).Select(label => JsonSerializer.Deserialize<ReplayCalibrationSourceReceipt>(
            File.ReadAllBytes(Path.Combine(continuation.SourceRoots[label], "qualification-source.json")), Json)).ToList();
        Require(sources.Select(s => s.Label).SequenceEqual(Order.Take(4)) && sources.All(s => s.Scope == model.Scope
            && s.MaximumActiveGapMs <= 500 && s.AllTracksDecoded && s.SourceLineageVerified && s.NonSilentAudio
            && s.Capture.ProducerAbsent && s.Capture.ExitCode == 0 && !s.Capture.ForcedTermination
            && s.Source.ProducerAbsent && s.Source.ExitCode == 0 && !s.Source.ForcedTermination
            && s.Export.ProducerAbsent && s.Export.ExitCode == 0 && s.Decode.ProducerAbsent && s.Decode.ExitCode == 0),
            "Retained cases lack complete independent media/producer evidence.");
        Require(Order.Take(3).Select(label => HashFile(Path.Combine(continuation.SourceRoots[label], "sample.json")))
            .SequenceEqual(model.TrainingReceipts), "Original training receipt identity changed; no refit.");
        using var lease = MediaDependency.OpenVerifiedExecutable(engine);
        using var owned = PilotStorageObservation.Create(plan.Root);
        Log.DirectoryPath = plan.Root;
        Write(Path.Combine(plan.Root, "admitted-plan.json"), new { continuation, planHash = expectedPlanHash, retainedSources = 4, newSources = 2, priorBatchRemainsFailed = true });
        File.Copy(Path.Combine(continuation.PriorRoot, "frozen-training-model.json"), Path.Combine(plan.Root, "frozen-training-model.json"));
        using var sampler = new Sampler(plan, active: false);
        var processes = new List<object>(); var samples = new List<Sample>();
        var sourceRoots = new Dictionary<string, string>(continuation.SourceRoots, StringComparer.Ordinal);
        try
        {
            foreach (string label in Order.Skip(4))
            {
                // Operator coordination occurs before source setup or capture.
                // It enables normal Windows activation without faking keyboard input.
                Write(Path.Combine(plan.Root, "operator-ready-" + label + ".json"), new { label, noCaptureAdmitted = true });
                var wait = Stopwatch.StartNew();
                while (!File.Exists(Path.Combine(plan.Root, "operator-launch-" + label + ".json")))
                { sampler.Check(); Require(wait.Elapsed.TotalSeconds < 60, "Operator source-launch coordination deadline; no capture admitted."); Thread.Sleep(50); }
                sampler.RequireTime(650); sampler.BeginActive();
                var sample = Collect(plan, label, sampler, model, processes, (pid, hwnd) =>
                {
                    Write(Path.Combine(plan.Root, "operator-focus-ready-" + label + ".json"), new { label, pid, hwnd = hwnd.ToInt64(), noCaptureAdmitted = true });
                    var activation = Stopwatch.StartNew();
                    CalibrationForeground.Wait(() => File.Exists(Path.Combine(plan.Root, "operator-focused-" + label + ".json"))
                            && GetForegroundWindow() == hwnd,
                        () => { Native.GetWindowThreadProcessId(hwnd, out uint owner); return owner != pid; },
                        () => sampler.Check(), () => Thread.Sleep(25), () => activation.Elapsed.TotalSeconds);
                    Write(Path.Combine(plan.Root, "operator-handoff-" + label + ".json"), new { label, pid, hwnd = hwnd.ToInt64(),
                        activationCompleteBeforeBootstrap = true, waitedMilliseconds = activation.Elapsed.TotalMilliseconds });
                });
                sampler.EndActive(); samples.Add(sample);
                string cell = Path.Combine(plan.Root, label); sourceRoots.Add(label, cell);
                sampler.BeginOffline("inspection", 32_000_000, MainInspectionSeconds);
                var inspection = Inspect(engine, cell, sample.File, sample.ExportSeconds, label, MainInspectionSeconds - InspectionCleanupSeconds);
                sampler.EndOffline();
                var source = QualificationSource(sample, inspection, cell, sampler);
                Require(source.Scope == model.Scope && sample.Demand.Kind == ReplayOutputEvidenceKind.CalibratedEstimate
                    && source.CaptureVolume == model.Volume && source.CapturePollGrowthBytes <= model.CaptureHeadroom.PollGrowthBytes
                    && source.CaptureStopGrowthBytes <= model.CaptureHeadroom.StopGrowthBytes
                    && model.CoversObservations(sample.InputBytes, sample.FinalBytes, sample.FinalAllocation, sample.PeakAllocation),
                    "New held-out observation exceeds the unchanged frozen model; no refit.");
                sources.Add(source); Write(Path.Combine(cell, "qualification-source.json"), source);
                foreach (string snapshot in Directory.GetDirectories(Path.Combine(cell, "buffer"), "save-*", SearchOption.AllDirectories))
                {
                    sampler.BeginOffline("archive", 1_068_194_304, ArchiveSeconds);
                    var archived = ReplayCalibrationArchive.Create(cell, snapshot, bytes => sampler.Check(bytes), sampler.Gate);
                    ReplayCalibrationArchive.Retire(cell, archived, sampler.Gate, sampler.ReleaseInventoryHandles); sampler.EndOffline();
                }
                sampler.SealClosedCase(label);
            }
            sampler.StopAndCheck();
            Write(Path.Combine(plan.Root, "source-roots.json"), sourceRoots);
            Write(Path.Combine(plan.Root, "qualification-sources.json"), sources);
            Write(Path.Combine(plan.Root, "observation-bundle.json"), new {
                priorAdmissionHash = continuation.PriorAdmissionHash,
                priorObservationPath = Path.Combine(continuation.PriorRoot, "storage.csv"), priorObservationHash = HashFile(Path.Combine(continuation.PriorRoot, "storage.csv")),
                completionObservationPath = Path.Combine(plan.Root, "storage.csv"), completionObservationHash = HashFile(Path.Combine(plan.Root, "storage.csv")),
                retainedSourceReceipts = Order.Take(4).ToDictionary(label => label, label => HashFile(Path.Combine(sourceRoots[label], "qualification-source.json"))),
                priorBatchRemainsFailed = true });
            double maximumActiveGap = Math.Max(sampler.MaximumActiveGap, sources.Max(s => s.MaximumActiveGapMs));
            var receipt = ReplayCalibrationCatalog.CreateReceipt(model, sources.ToArray(), maximumActiveGap,
                HashFile(Path.Combine(plan.Root, "observation-bundle.json")), expectedPlanHash);
            Write(Path.Combine(plan.Root, "qualified-reference.json"), receipt);
            Write(Path.Combine(plan.Root, "result.json"), new { result = "passed-composed-six-source-reference-calibration",
                retainedSources = 4, newSources = 2, modelRefit = false, priorBatchRemainsFailed = true, sourceRoots,
                MaximumActiveGap = maximumActiveGap, sampler.MaximumOfflineGap, sampler.Used, sampler.PeakUsed,
                sampler.MaximumFiles, sampler.MaximumDirectories, offlineSeconds = sampler.OfflineSeconds, samples, processes });
            Console.WriteLine("Composed six-source calibration passed: " + plan.Root); return 0;
        }
        catch (Exception error)
        {
            Write(Path.Combine(plan.Root, "failure.json"), new { result = "failed-no-retry", error = error.ToString(), sources, samples, processes,
                modelRefit = false, priorBatchRemainsFailed = true, offlineSeconds = sampler.OfflineSeconds });
            Console.Error.WriteLine(error.Message); return 1;
        }
    }
}
