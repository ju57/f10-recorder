using System.Text.Json;

namespace F10Recorder;

internal static partial class ReplayCalibrationRun
{
    static readonly string[] Heldout14 = ["H02", "H01", "H03"];
    const int ContinuationPriorSeconds14 = 10120;
    const int ContinuationFreshSeconds14 = 3600;
    const int ContinuationTotalSeconds14 = ContinuationPriorSeconds14 + ContinuationFreshSeconds14;
    static bool ExactContinuation14(CalibrationPlan p) => p?.MaximumChildren is 118 or 122 or 125 or 129 or 137 or 141 or 145 or 147 or 151 && p.ExactContinuationRoot != null;
    static bool ExactFinalWorker14(CalibrationPlan p) => p?.MaximumChildren is 141 or 145 or 147 or 151 && p.ExactHeldoutContinuationRoot != null;
    static void ValidateFinalWorker14(CalibrationPlan p)
    {
        Require(ExactFinalWorker14(p) && p.RetainedChildren == (p.MaximumChildren == 151 ? 145 : p.MaximumChildren == 147 ? 140 : p.MaximumChildren == 145 ? 138 : 134)
            && p.AutomaticFixtureHandoff == (p.MaximumChildren is 147 or 151) && p.Order.SequenceEqual(new[] { "H03" })
            && p.OperatorForegroundHandoff && p.UserForegroundHandoff && p.ValidationChildren == 1 && !p.ReadonlyValidationPhases
            && p.ReplayExporterRecipe == ReplayExactExport.Recipe && p.BudgetBytes == 38_000_000_000 && p.StopBytes == 36_000_000_000
            && p.FloorBytes == 60_000_000_000 && p.DeadlineSeconds == (p.MaximumChildren == 151 ? 17320 : p.MaximumChildren == 147 ? 16420 : 15520) && double.IsFinite(p.RetainedElapsedSeconds)
            && p.RetainedElapsedSeconds >= ContinuationPriorSeconds14 && p.RetainedElapsedSeconds + (p.MaximumChildren is 145 or 147 or 151 ? 960 : 1200) < p.DeadlineSeconds
            && p.RetainedOfflineSeconds is >= 0 and <= 1140 && p.RetainedOfflineSeconds <= p.RetainedElapsedSeconds
            && p.OfflineDeadlineSeconds == 2100 && p.SourceComponentBytes == 850_000_000 && p.OutputComponentBytes == 1_100_000_000
            && p.RetainedBudgetBytes == 32_000_000_000 && p.OutsideBuildAllowanceBytes == 268_435_456
            && p.FileLimit == 32768 && p.DirectoryLimit == 4096 && p.RetainActiveOutputHandles && p.ReuseCurrentMetadataHandles && p.FreezeRetainedAllocations
            && p.RetainedRoots is { Length: > 0 and <= 64 } && p.RetainedRoots.All(Directory.Exists)
            && p.RetainedRoots.Contains(p.ExactContinuationRoot, StringComparer.OrdinalIgnoreCase)
            && p.RetainedRoots.Contains(p.ExactHeldoutContinuationRoot, StringComparer.OrdinalIgnoreCase), "Final worker-only continuation boundary changed.");
        ValidateContinuationRoots14(p);
    }
    static void ValidateExactContinuation14(CalibrationPlan p)
    {
        if (p?.MaximumChildren is 141 or 145 or 147 or 151) { ValidateFinalWorker14(p); return; }
        Require(p?.ExactHeldoutContinuationRoot == null && !p.AutomaticFixtureHandoff, "Only final-worker admission may select held-out/automatic policy.");
        Require(p != null && p.MaximumChildren is 118 or 122 or 125 or 129 or 137 && p.RetainedChildren == (p.MaximumChildren == 137 ? 120 : p.MaximumChildren == 129 ? 112 : p.MaximumChildren == 125 ? 108 : p.MaximumChildren == 122 ? 105 : 101)
            && p.OperatorForegroundHandoff == (p.MaximumChildren is 122 or 125 or 129 or 137) && p.UserForegroundHandoff == (p.MaximumChildren is 125 or 129 or 137) && p.Order.SequenceEqual(Heldout14)
            && p.ReplayExporterRecipe == ReplayExactExport.Recipe && p.ValidationChildren == 3 && !p.ReadonlyValidationPhases
            && p.BudgetBytes == (p.MaximumChildren == 137 ? 38_000_000_000 : p.MaximumChildren == 129 ? 36_000_000_000 : 34_000_000_000)
            && p.StopBytes == (p.MaximumChildren == 137 ? 36_000_000_000 : p.MaximumChildren == 129 ? 34_000_000_000 : 32_000_000_000) && p.FloorBytes == 60_000_000_000
            && p.DeadlineSeconds == (p.MaximumChildren == 137 ? 15520 : ContinuationTotalSeconds14) && double.IsFinite(p.RetainedElapsedSeconds)
            && p.RetainedElapsedSeconds >= ContinuationPriorSeconds14 && p.RetainedElapsedSeconds + (p.UserForegroundHandoff ? 2340 : 2160) < p.DeadlineSeconds
            && p.RetainedOfflineSeconds >= 0 && p.RetainedOfflineSeconds <= (p.MaximumChildren == 137 ? 960 : 900) && p.RetainedOfflineSeconds <= p.RetainedElapsedSeconds
            && p.OfflineDeadlineSeconds == 2100 && p.SourceComponentBytes == 850_000_000 && p.OutputComponentBytes == 1_100_000_000
            && p.RetainedBudgetBytes == (p.MaximumChildren == 137 ? 29_000_000_000 : 27_000_000_000) && p.OutsideBuildAllowanceBytes == 268_435_456
            && p.FileLimit == 32768 && p.DirectoryLimit == 4096 && p.RetainActiveOutputHandles && p.ReuseCurrentMetadataHandles && p.FreezeRetainedAllocations
            && p.RetainedRoots is { Length: > 0 and <= 64 } && p.RetainedRoots.All(Directory.Exists)
            && p.RetainedRoots.Contains(p.ExactContinuationRoot, StringComparer.OrdinalIgnoreCase), "Exact native continuation resource/order boundary changed.");
        ValidateContinuationRoots14(p);
    }
    static void ValidateContinuationRoots14(CalibrationPlan p)
    {
        string mutable = Path.GetFullPath(p.Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var prior = p.RetainedRoots.Select(r => Path.GetFullPath(r).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar).ToArray();
        Require(prior.Distinct(StringComparer.OrdinalIgnoreCase).Count() == prior.Length && prior.All(h => !mutable.StartsWith(h, StringComparison.OrdinalIgnoreCase)
            && !h.StartsWith(mutable, StringComparison.OrdinalIgnoreCase) && prior.Count(other => h.StartsWith(other, StringComparison.OrdinalIgnoreCase)) == 1), "Continuation history must be complete and disjoint.");
    }
    static (DateTime Resume, DateTime Deadline, double Elapsed) ContinuationTiming14(JsonElement phase, DateTime now, int reserve = 2160, int freshSeconds = ContinuationFreshSeconds14)
    {
        DateTime resumed = phase.GetProperty("resumeStartedUtc").GetDateTime();
        DateTime deadline = phase.GetProperty("absoluteDeadlineUtc").GetDateTime();
        Require(resumed.Kind == DateTimeKind.Utc && now.Kind == DateTimeKind.Utc && resumed <= now
            && freshSeconds is 3600 or 5400 or 6300 or 7200 && deadline.Kind == DateTimeKind.Utc && deadline == resumed.AddSeconds(freshSeconds)
            && phase.GetProperty("priorElapsedSeconds").GetInt32() == ContinuationPriorSeconds14,
            "Resumed continuation must preserve prior time and bind its declared fresh-phase deadline.");
        double elapsed = ContinuationPriorSeconds14 + (now - resumed).TotalSeconds;
        Require(reserve is 960 or 1200 or 2160 or 2340 && elapsed + reserve < ContinuationPriorSeconds14 + freshSeconds, "The complete resumed continuation no longer fits.");
        return (resumed, deadline, elapsed);
    }
    static ReplayReferenceModel FrozenContinuationModel(CalibrationPlan p)
    {
        string root = p.ExactContinuationRoot, file = Path.Combine(root, "frozen-training-model.json");
        var model = JsonSerializer.Deserialize<ReplayReferenceModel>(File.ReadAllText(file));
        Require(HashFile(file) == p.Files.GetValueOrDefault(file) && model.TrainingReceipts.SequenceEqual(Order.Take(3).Select(l => HashFile(Path.Combine(root, l, "sample.json"))))
            && model.Revision == ReplayCalibrationCatalog.ModelRevision(model.Scope, model.Volume, model.AllocationUnit, model.FinalResidual,
                model.SampledFinalizationExtra, model.TrainingReceipts, captureHeadroom: model.CaptureHeadroom), "Frozen training receipts/model/exporter/host changed; no refit or transfer is allowed.");
        foreach (string label in Order.Take(3))
        {
            var q = JsonSerializer.Deserialize<ReplayCalibrationSourceReceipt>(File.ReadAllText(Path.Combine(root, label, "qualification-source.json")));
            Require(q.Label == label && q.Scope == model.Scope && q.AllTracksDecoded && q.SourceLineageVerified && q.NonSilentAudio
                && q.MaximumActiveGapMs <= 500 && q.RuntimeValidation is { ProducerAbsent: true, ActualExitObserved: true, ExitCode: 0 }
                && q.Capture is { ProducerAbsent: true, ActualExitObserved: true, ExitCode: 0 }
                && q.Export is { ProducerAbsent: true, ActualExitObserved: true, ExitCode: 0 }
                && q.Decode is { ProducerAbsent: true, ActualExitObserved: true, ExitCode: 0 }, "A retained training source is not independently complete.");
        }
        if (ExactFinalWorker14(p))
            foreach (string label in new[] { "H02", "H01" })
            {
                string cell = Path.Combine(p.ExactHeldoutContinuationRoot, label);
                var sample = JsonSerializer.Deserialize<Sample>(File.ReadAllText(Path.Combine(cell, "sample.json")));
                var q = JsonSerializer.Deserialize<ReplayCalibrationSourceReceipt>(File.ReadAllText(Path.Combine(cell, "qualification-source.json")));
                Require(q.Label == label && sample.Label == label && q.SampleHash == HashFile(Path.Combine(cell, "sample.json"))
                    && q.Scope == model.Scope && sample.Scope == model.Scope && q.AllTracksDecoded && q.SourceLineageVerified && q.NonSilentAudio
                    && q.MaximumActiveGapMs <= 500 && sample.ExportSeconds == 300 && q.ExportSeconds == 300
                    && new[] { q.Capture, q.Source, q.Export, q.Decode, q.RuntimeValidation }.All(child => child is
                        { ProducerAbsent: true, ActualExitObserved: true, ExitCode: 0, TimedOut: false, OutputLimitExceeded: false, ForcedTermination: false })
                    && model.CoversObservations(sample.InputBytes, sample.FinalBytes, sample.FinalAllocation, sample.PeakAllocation),
                    "A retained held-out source is incomplete or exceeds the unchanged frozen model.");
            }
        return model;
    }
    internal static int PrepareNativeContinuation14(string path, string engineRoot, string root, string phaseFile)
    {
        root = Path.GetFullPath(root); Require(!Directory.Exists(root) && !File.Exists(root), "Fresh continuation root required.");
        using var phaseDocument = JsonDocument.Parse(File.ReadAllText(phaseFile)); var phase = phaseDocument.RootElement;
        bool coordinated = phase.TryGetProperty("operatorForegroundHandoff", out var handoff) && handoff.GetBoolean();
        bool manual = phase.TryGetProperty("userForegroundHandoff", out var userHandoff) && userHandoff.GetBoolean();
        bool archiveRepair = phase.TryGetProperty("coordinatedArchiveRepair", out var repair) && repair.GetBoolean();
        bool terminalHooks = phase.TryGetProperty("terminalEvidenceHooks", out var hooks) && hooks.GetBoolean();
        bool finalWorker = phase.TryGetProperty("finalWorkerOnly", out var finalOnly) && finalOnly.GetBoolean();
        bool startupRepair = phase.TryGetProperty("excludedStartupRepair", out var startup) && startup.GetBoolean();
        bool automatic = phase.TryGetProperty("automaticFixtureHandoff", out var auto) && auto.GetBoolean();
        bool latchRepair = phase.TryGetProperty("noncontendingWorkerObservation", out var noncontending) && noncontending.GetBoolean();
        Require(!latchRepair || automatic, "Noncontending worker observation requires automatic final-worker admission.");
        Require(!manual || coordinated, "User focus requires explicit operator coordination.");
        Require(!archiveRepair || manual, "Coordinated archive repair requires the manual-focus continuation.");
        Require(!terminalHooks || archiveRepair, "Terminal evidence correction requires coordinated archival.");
        Require(!finalWorker || terminalHooks, "Final-worker continuation requires the verified terminal hooks.");
        Require(!startupRepair || finalWorker, "Excluded startup repair requires final-worker continuation.");
        Require(!automatic || startupRepair, "Automatic foreground observation requires the eligible-input final-worker admission.");
        int retainedChildren = latchRepair ? 145 : automatic ? 140 : startupRepair ? 138 : finalWorker ? 134 : terminalHooks ? 120 : archiveRepair ? 112 : manual ? 108 : coordinated ? 105 : 101,
            maximumChildren = latchRepair ? 151 : automatic ? 147 : startupRepair ? 145 : finalWorker ? 141 : terminalHooks ? 137 : archiveRepair ? 129 : manual ? 125 : coordinated ? 122 : 118;
        int freshSeconds = latchRepair ? 7200 : automatic ? 6300 : terminalHooks ? 5400 : 3600, totalSeconds = ContinuationPriorSeconds14 + freshSeconds;
        long budget = terminalHooks ? 38_000_000_000 : archiveRepair ? 36_000_000_000 : 34_000_000_000,
            stopBytes = terminalHooks ? 36_000_000_000 : archiveRepair ? 34_000_000_000 : 32_000_000_000;
        int reserve = startupRepair ? 960 : finalWorker ? 1200 : manual ? 2340 : 2160;
        var timing = ContinuationTiming14(phase, DateTime.UtcNow, reserve, freshSeconds); double elapsed = timing.Elapsed;
        Require(phase.GetProperty("childrenIncludingPreparation").GetInt32() == retainedChildren,
            "The complete native continuation cannot fit its prospective time/process boundary.");
        string prefix = Path.GetFullPath(phase.GetProperty("continuationRoot").GetString());
        MediaDependency.RootDirectory = Path.GetFullPath(engineRoot); string engine = MediaDependency.ResolveExecutable();
        string build = Path.GetDirectoryName(typeof(ReplayCalibrationRun).Assembly.Location);
        var files = new[] { Environment.ProcessPath, typeof(ReplayCalibrationRun).Assembly.Location, Path.Combine(build, "F10Recorder.Windows.dll"), Path.Combine(build, "F10Recorder.Core.dll"),
            Path.Combine(build, "F10Recorder.WorkerChecks.deps.json"), Path.Combine(build, "F10Recorder.WorkerChecks.runtimeconfig.json"), engine, Path.GetFullPath("tests/replay-calibration.ps1"), Path.GetFullPath(phaseFile) }
            .ToDictionary(Path.GetFullPath, HashFile, StringComparer.OrdinalIgnoreCase);
        string originalPhase = Path.GetFullPath(phase.GetProperty("originalPhase").GetString());
        string pausedState = Path.GetFullPath(phase.GetProperty("pausedState").GetString());
        files[originalPhase] = HashFile(originalPhase); files[pausedState] = HashFile(pausedState);
        using (var original = JsonDocument.Parse(File.ReadAllText(originalPhase)))
        using (var paused = JsonDocument.Parse(File.ReadAllText(pausedState)))
        {
            DateTime originalStart = original.RootElement.GetProperty("startedUtc").GetDateTime();
            DateTime stopped = paused.RootElement.GetProperty("checkedUtc").GetDateTime();
            Require(originalStart.Kind == DateTimeKind.Utc && stopped.Kind == DateTimeKind.Utc && stopped <= timing.Resume
                && Math.Ceiling((stopped - originalStart).TotalSeconds) == ContinuationPriorSeconds14
                && paused.RootElement.GetProperty("matchingTaskMediaOrCheckProcesses").GetInt32() == 0
                && paused.RootElement.GetProperty("nativeAcquisitionsCharged").GetInt32() == 10
                && paused.RootElement.GetProperty("cumulativeMediaHelperCharges").GetInt32() == 100,
                "Original phase, pause closure or retained charges changed.");
        }
        foreach (string label in Order.Take(3)) foreach (string name in new[] { "sample.json", "qualification-source.json", "capture-exit.json", "fixture-exit.json" })
        { string file = Path.Combine(prefix, label, name); files[file] = HashFile(file); }
        foreach (string name in new[] { "frozen-training-model.json", "storage.csv", "failure.json", "child-admissions.jsonl" })
        { string file = Path.Combine(prefix, name); files[file] = HashFile(file); }
        string closureFile = Path.GetFullPath(phase.GetProperty("priorClosure").GetString()); files[closureFile] = HashFile(closureFile);
        using (var closure = JsonDocument.Parse(File.ReadAllText(closureFile)))
            Require(closure.RootElement.GetProperty("nativeAcquisitionsCharged").GetInt32() == (latchRepair ? 17 : startupRepair ? 16 : finalWorker ? 15 : terminalHooks ? 12 : archiveRepair ? 11 : 10) && closure.RootElement.GetProperty("cumulativeProcessCharges").GetInt32() == retainedChildren - 1
                && closure.RootElement.GetProperty("remainingOwnedMediaProcesses").GetInt32() == 0, "Continuation charges/producer closure changed.");
        if (coordinated)
        {
            string failedRoot = Path.GetFullPath(phase.GetProperty("foregroundFailureRoot").GetString());
            foreach (string name in new[] { "failure.json", "child-admissions.jsonl", "H02/fixture-exit.json", "H02/fixture-ready.json" })
            { string file = Path.Combine(failedRoot, name); files[file] = HashFile(file); }
            using var failed = JsonDocument.Parse(File.ReadAllText(Path.Combine(failedRoot, "failure.json")));
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(failedRoot, "H02", "fixture-exit.json")));
            var admissions = File.ReadAllLines(Path.Combine(failedRoot, "child-admissions.jsonl")).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
            Require(failed.RootElement.GetProperty("error").GetString().Contains("foreground deadline")
                && admissions.Length == 2 && admissions.Select(a => a.GetProperty("ordinal").GetInt32()).SequenceEqual(manual ? new[] { 106, 107 } : new[] { 102, 103 })
                && admissions.Select(a => a.GetProperty("role").GetString()).SequenceEqual(new[] { "collector", "fixture:H02" })
                && fixture.RootElement.GetProperty("exited").GetBoolean() && fixture.RootElement.GetProperty("exit").GetInt32() == 0
                && !fixture.RootElement.GetProperty("forced").GetBoolean(),
                "Only the reviewed foreground-before-acquisition failure can select coordinated continuation.");
            string controllerFile = Path.GetFullPath(phase.GetProperty("foregroundControllerReceipt").GetString()); files[controllerFile] = HashFile(controllerFile);
            using var controller = JsonDocument.Parse(File.ReadAllText(controllerFile));
            Require(controller.RootElement.GetProperty("confirmedExited").GetBoolean() && controller.RootElement.GetProperty("exit").GetInt32() == 1
                && Path.GetFullPath(controller.RootElement.GetProperty("root").GetString()).Equals(failedRoot, StringComparison.OrdinalIgnoreCase),
                "Previous foreground controller absence is unconfirmed.");
            string manifestProof = Path.GetFullPath(phase.GetProperty("manifestVerification").GetString()); files[manifestProof] = HashFile(manifestProof);
            using var proof = JsonDocument.Parse(File.ReadAllText(manifestProof));
            Require(proof.RootElement.GetProperty("result").GetString() == "passed", "Corrected manifest regression is not verified.");
            if (manual)
            {
                string activationFile = Path.GetFullPath(phase.GetProperty("activationFailure").GetString()); files[activationFile] = HashFile(activationFile);
                using var activation = JsonDocument.Parse(File.ReadAllText(activationFile));
                Require(!activation.RootElement.GetProperty("actualCaptureAdmitted").GetBoolean()
                    && activation.RootElement.GetProperty("markerIsNotActivationEvidence").GetBoolean()
                    && activation.RootElement.GetProperty("guardIndependentlyRejected").GetBoolean(), "Failed activation must not become source evidence.");
                string authorityFile = Path.GetFullPath(phase.GetProperty("broadAuthorization").GetString()); files[authorityFile] = HashFile(authorityFile);
            }
        }
        if (terminalHooks)
        {
            string transitionFile = Path.GetFullPath(phase.GetProperty("terminalTransitionProof").GetString()); files[transitionFile] = HashFile(transitionFile);
            using var transition = JsonDocument.Parse(File.ReadAllText(transitionFile));
            var result = transition.RootElement.GetProperty("results").EnumerateArray().Single();
            Require(transition.RootElement.GetProperty("result").GetString() == "passed-native-path" && result.GetProperty("nativeReader").GetBoolean()
                && result.GetProperty("Validation").GetProperty("Frames").GetInt64() == 18000
                && result.GetProperty("MaximumActiveGap").GetDouble() <= 500, "Combined retained-TS/calibrated encode/validation proof is absent.");
            foreach (string role in new[] { "encoder", "decoder" })
            {
                var child = result.GetProperty(role);
                Require(child.GetProperty("ProducerAbsent").GetBoolean() && child.GetProperty("ActualExitObserved").GetBoolean()
                    && child.GetProperty("ExitCode").GetInt32() == 0, "Combined diagnostic child closure is incomplete.");
            }
            foreach (string role in new[] { "terminalTransitionController", "terminalTimingProof", "previousResumedPhase", "latestAuthorization" })
            { string file = Path.GetFullPath(phase.GetProperty(role).GetString()); files[file] = HashFile(file); }
            using (var controller = JsonDocument.Parse(File.ReadAllText(phase.GetProperty("terminalTransitionController").GetString())))
                Require(controller.RootElement.GetProperty("confirmedExited").GetBoolean() && controller.RootElement.GetProperty("exit").GetInt32() == 0,
                    "Combined diagnostic helper closure is incomplete.");
            using (var previous = JsonDocument.Parse(File.ReadAllText(phase.GetProperty("previousResumedPhase").GetString())))
                Require(previous.RootElement.GetProperty("resumeStartedUtc").GetDateTime() == timing.Resume
                    && previous.RootElement.GetProperty("absoluteDeadlineUtc").GetDateTime() == timing.Resume.AddSeconds(3600),
                    "The extended phase must preserve the original resume instant and prior sixty-minute receipt.");
            string failedRoot = Path.GetFullPath(phase.GetProperty("terminalFailureRoot").GetString());
            foreach (string name in new[] { "failure.json", "child-admissions.jsonl", "H02/capture-exit.json", "H02/fixture-exit.json", "H02/last-export-exit.json" })
            { string file = Path.Combine(failedRoot, name); files[file] = HashFile(file); }
            var charged = File.ReadAllLines(Path.Combine(failedRoot, "child-admissions.jsonl")).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
            Require(charged.Select(c => c.GetProperty("ordinal").GetInt32()).SequenceEqual(new[] { 113, 114, 115, 116 })
                && charged.Select(c => c.GetProperty("role").GetString()).SequenceEqual(new[] { "collector", "fixture:H02", "capture:H02", "export:H02" }),
                "The failed live source/export charges must be retained exactly.");
        }
        if (archiveRepair)
        {
            string failedRoot = Path.GetFullPath(phase.GetProperty("manifestFailureRoot").GetString());
            foreach (string name in new[] { "failure.json", "child-admissions.jsonl", "H02/fixture-exit.json" })
            { string file = Path.Combine(failedRoot, name); files[file] = HashFile(file); }
            using var failed = JsonDocument.Parse(File.ReadAllText(Path.Combine(failedRoot, "failure.json")));
            var admissions = File.ReadAllLines(Path.Combine(failedRoot, "child-admissions.jsonl")).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
            Require(failed.RootElement.GetProperty("error").GetString().Contains("Replay manifest publication failed")
                && admissions.Length == 3 && admissions.Select(a => a.GetProperty("ordinal").GetInt32()).SequenceEqual(new[] { 109, 110, 111 })
                && admissions.Select(a => a.GetProperty("role").GetString()).SequenceEqual(new[] { "collector", "fixture:H02", "capture:H02" }),
                "Coordinated archive correction must retain the charged failed acquisition.");
            string closure = Path.GetFullPath(phase.GetProperty("manifestProducerClosure").GetString()); files[closure] = HashFile(closure);
            using var proof = JsonDocument.Parse(File.ReadAllText(closure));
            Require(proof.RootElement.GetProperty("remainingOwnedProcesses").GetInt32() == 0
                && proof.RootElement.GetProperty("failedNotQualified").GetBoolean(), "Manifest failure producers remain unresolved.");
        }
        string probe = Path.GetFullPath(phase.GetProperty("calibratedProbe").GetString()); files[probe] = HashFile(probe);
        using (var proof = JsonDocument.Parse(File.ReadAllText(probe)))
        {
            var native = proof.RootElement.GetProperty("results").EnumerateArray().Single(r => r.GetProperty("mode").GetString() == "native");
            Require(proof.RootElement.GetProperty("result").GetString() == "passed-native-path" && native.GetProperty("nativeReader").GetBoolean()
                && native.GetProperty("Validation").GetProperty("Frames").GetInt64() == 18000 && native.GetProperty("decoder").GetProperty("ActualExitObserved").GetBoolean()
                && native.GetProperty("decoder").GetProperty("ProducerAbsent").GetBoolean() && native.GetProperty("MaximumActiveGap").GetDouble() <= 500,
                "Actual calibrated native-provider/full-decode proof is absent.");
        }
        string heldoutRoot = null;
        if (finalWorker)
        {
            heldoutRoot = Path.GetFullPath(phase.GetProperty("heldoutContinuationRoot").GetString());
            foreach (string label in new[] { "H02", "H01" })
                foreach (string name in new[] { "sample.json", "qualification-source.json", "capture-exit.json", "fixture-exit.json", "runtime-validation-exit.json", "output-handle-release.json" })
                { string file = Path.Combine(heldoutRoot, label, name); files[file] = HashFile(file); }
            foreach (string name in new[] { "storage.csv", "failure.json", "child-admissions.jsonl", "H03/capture-exit.json", "H03/fixture-exit.json" })
            { string file = Path.Combine(heldoutRoot, name); files[file] = HashFile(file); }
            string closure = Path.GetFullPath(phase.GetProperty("workerFailureClosure").GetString()); files[closure] = HashFile(closure);
            using var proof = JsonDocument.Parse(File.ReadAllText(closure));
            Require(proof.RootElement.GetProperty("remainingOwnedProcesses").GetInt32() == 0
                && proof.RootElement.GetProperty("acquisitionsCharged").GetInt32() == 15
                && proof.RootElement.GetProperty("processesCharged").GetInt32() == 133
                && proof.RootElement.GetProperty("failedWorkerCaseNotQualified").GetBoolean(), "Failed worker-case closure changed.");
        }
        string controls = Path.GetFullPath(phase.GetProperty("priorControls").GetString());
        if (automatic)
        {
            string failedRoot = Path.GetFullPath(phase.GetProperty("handoffFailureRoot").GetString());
            foreach (string name in new[] { "failure.json", "child-admissions.jsonl" })
            { string file = Path.Combine(failedRoot, name); files[file] = HashFile(file); }
            foreach (string role in new[] { "handoffFailureClosure", "previousNinetyMinutePhase" })
            { string file = Path.GetFullPath(phase.GetProperty(role).GetString()); files[file] = HashFile(file); }
            using var closure = JsonDocument.Parse(File.ReadAllText(phase.GetProperty("handoffFailureClosure").GetString()));
            Require(closure.RootElement.GetProperty("remainingOwnedProcesses").GetInt32() == 0
                && closure.RootElement.GetProperty("processesCharged").GetInt32() == 139
                && closure.RootElement.GetProperty("acquisitionsCharged").GetInt32() == 16, "Late operator handoff charges/closure changed.");
            using var previous = JsonDocument.Parse(File.ReadAllText(phase.GetProperty("previousNinetyMinutePhase").GetString()));
            Require(previous.RootElement.GetProperty("resumeStartedUtc").GetDateTime() == timing.Resume
                && previous.RootElement.GetProperty("absoluteDeadlineUtc").GetDateTime() == timing.Resume.AddSeconds(5400), "Automatic phase cannot reset the original resume/elapsed history.");
        }
        if (startupRepair)
        {
            string failedRoot = Path.GetFullPath(phase.GetProperty("startupFailureRoot").GetString());
            foreach (string name in new[] { "failure.json", "child-admissions.jsonl", "H03/capture-exit.json", "H03/fixture-exit.json", "H03/worker-exit.json" })
            { string file = Path.Combine(failedRoot, name); files[file] = HashFile(file); }
            string closure = Path.GetFullPath(phase.GetProperty("startupFailureClosure").GetString()); files[closure] = HashFile(closure);
            using var proof = JsonDocument.Parse(File.ReadAllText(closure));
            Require(proof.RootElement.GetProperty("remainingOwnedProcesses").GetInt32() == 0
                && proof.RootElement.GetProperty("acquisitionsCharged").GetInt32() == 16
                && proof.RootElement.GetProperty("processesCharged").GetInt32() == 137
                && proof.RootElement.GetProperty("failedNotQualified").GetBoolean(), "Discarded-startup failure closure changed.");
        }
        foreach (string name in new[] { "version", "nonzero", "timeout", "output-limit" }) { string file = Path.Combine(controls, name + ".json"); files[file] = HashFile(file); }
        using var audio = new LoopbackAudio(); var bounds = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
        Require(bounds.Width == 1920 && bounds.Height == 1080 && audio.SampleRate == 48000 && audio.Channels == 2, "Reference environment changed.");
        string[] histories = Directory.GetDirectories(Path.GetDirectoryName(root));
        long baseline = ReplayStorage.Bytes(ReplayStorage.Inventory(histories, new ReplayFiles(), 32768, 4096)); var capacity = ReplayStorage.ObserveCapacity(Path.GetDirectoryName(root));
        int freshCases = finalWorker ? 1 : 3;
        long forward = freshCases * 850_000_000L + freshCases * 1_100_000_000L + 1_010_000_000L + 387_108_864L + 268_435_456L;
        Require(baseline + forward < stopBytes && baseline + forward + 1_256_000_000 < budget && capacity.AvailableBytes >= 60_000_000_000 + budget,
            "Complete retained continuation plus explicit trace/evidence/fallback/closure cannot fit.");
        string numeric = Path.Combine(Path.GetDirectoryName(path), "continuation-controls"); Directory.CreateDirectory(numeric);
        Write(Path.Combine(numeric, "evidence.json"), NativeEvidenceChecks(Path.Combine(numeric, "evidence"))); files[Path.Combine(numeric, "evidence.json")] = HashFile(Path.Combine(numeric, "evidence.json"));
        Write(Path.Combine(numeric, "worker-profile.json"), WorkerProfileObservationChecks(Path.Combine(numeric, "worker-profile")));
        files[Path.Combine(numeric, "worker-profile.json")] = HashFile(Path.Combine(numeric, "worker-profile.json"));
        if (latchRepair)
        {
            string failedPreparer = Path.GetFullPath(phase.GetProperty("failedNoncapturePreparer").GetString());
            files[failedPreparer] = HashFile(failedPreparer);
            using (var failed = JsonDocument.Parse(File.ReadAllText(failedPreparer)))
                Require(failed.RootElement.GetProperty("processCharge").GetInt32() == 144
                    && failed.RootElement.GetProperty("acquisitionsCharged").GetInt32() == 17
                    && failed.RootElement.GetProperty("result").GetString() == "failed-no-capture", "Noncapture preparer failure charges changed.");
            string failureRoot = Path.GetFullPath(phase.GetProperty("latchFailureRoot").GetString());
            foreach (string name in new[] { "failure.json", "child-admissions.jsonl", "H03/capture-exit.json", "H03/fixture-exit.json" })
            { string file = Path.Combine(failureRoot, name); files[file] = HashFile(file); }
            var frozen = JsonSerializer.Deserialize<ReplayReferenceModel>(File.ReadAllText(Path.Combine(prefix, "frozen-training-model.json")));
            Write(Path.Combine(numeric, "worker-latch.json"), WorkerLatchObservationChecks(Path.Combine(numeric, "worker-latch"), frozen));
            files[Path.Combine(numeric, "worker-latch.json")] = HashFile(Path.Combine(numeric, "worker-latch.json"));
        }
        var environment = new { os = Environment.OSVersion.VersionString, machine = Environment.MachineName, bounds.Width, bounds.Height, primaryOutput = Native.PrimaryOutputIndex(), audio.EndpointId, audio.SampleRate, audio.Channels, audio.FfmpegFormat, capacity };
        var p = new CalibrationPlan(root, MediaDependency.RootDirectory, ReplayReferenceModel.Hash(environment), files, finalWorker ? ["H03"] : Heldout14, histories, elapsed, retainedChildren,
            MaximumChildren: maximumChildren, BudgetBytes: budget, StopBytes: stopBytes, DeadlineSeconds: totalSeconds, RetainedOfflineSeconds: finalWorker ? 1140 : terminalHooks ? 960 : 900,
            MetadataHandleRoots: histories, ReuseCurrentMetadataHandles: true, DirectoryLimit: 4096, OutputComponentBytes: 1_100_000_000, RetainedBudgetBytes: finalWorker ? 32_000_000_000 : terminalHooks ? 29_000_000_000 : 27_000_000_000,
            FileLimit: 32768, OfflineDeadlineSeconds: 2100, OutsideBuildAllowanceBytes: 268_435_456, FreezeRetainedAllocations: true,
            ReplayExporterRecipe: ReplayExactExport.Recipe, ValidationChildren: finalWorker ? 1 : 3, SourceComponentBytes: 850_000_000, RetainActiveOutputHandles: true,
            ExactContinuationRoot: prefix, OperatorForegroundHandoff: coordinated, UserForegroundHandoff: manual, ExactHeldoutContinuationRoot: heldoutRoot,
            AutomaticFixtureHandoff: automatic);
        ValidateExactContinuation14(p); RequireExactExporterAdmission(p); _ = FrozenContinuationModel(p);
        int rejected = 0;
        foreach (var bad in new[] { p with { MaximumChildren = p.MaximumChildren + 1 }, p with { RetainedChildren = p.RetainedChildren + 1 }, p with { BudgetBytes = p.BudgetBytes + 1 },
            p with { StopBytes = p.StopBytes + 1 }, p with { FloorBytes = p.FloorBytes - 1 }, p with { DeadlineSeconds = p.DeadlineSeconds + 1 },
            p with { ValidationChildren = 4 }, p with { Order = Heldout14.Reverse().ToArray() }, p with { ReadonlyValidationPhases = true },
            p with { ExactContinuationRoot = null }, p with { SourceComponentBytes = 850_000_001 }, p with { OutputComponentBytes = 1_100_000_001 },
            p with { RetainedBudgetBytes = p.RetainedBudgetBytes + 1 }, p with { Root = prefix }, p with { OperatorForegroundHandoff = !p.OperatorForegroundHandoff },
            p with { UserForegroundHandoff = !p.UserForegroundHandoff }, p with { ExactHeldoutContinuationRoot = finalWorker ? null : prefix },
            p with { AutomaticFixtureHandoff = !p.AutomaticFixtureHandoff } })
        { bool failed = false; try { ValidateResources(bad); } catch (IOException) { failed = true; } Require(failed, "Changed continuation boundary passed."); rejected++; }
        Write(Path.Combine(numeric, "resources.json"), new { result = "passed", exactFit = true, rejected });
        files[Path.Combine(numeric, "resources.json")] = HashFile(Path.Combine(numeric, "resources.json"));
        int timeRejected = 0;
        foreach (var bad in new[] { (Now: timing.Resume.AddSeconds(-1), Phase: phase), (Now: timing.Deadline.AddSeconds(-reserve), Phase: phase) })
        { bool failed = false; try { _ = ContinuationTiming14(bad.Phase, bad.Now, reserve, freshSeconds); } catch (IOException) { failed = true; } Require(failed, "Changed resumed timing boundary passed."); timeRejected++; }
        Write(Path.Combine(numeric, "timing.json"), new { result = "passed", priorSeconds = ContinuationPriorSeconds14, freshSeconds, timeRejected });
        files[Path.Combine(numeric, "timing.json")] = HashFile(Path.Combine(numeric, "timing.json"));
        Write(path, new { plan = p, environment, nativePhase = new { resumeStartedUtc = timing.Resume, absoluteDeadlineUtc = timing.Deadline,
                priorElapsedSeconds = ContinuationPriorSeconds14, conservativeRemainingSeconds = reserve, receipt = Path.GetFullPath(phaseFile) },
            effects = Effects(p), recipe = Recipe(p), baseline, forward, projected = baseline + forward, projectedWithClosure = baseline + forward + 1_256_000_000,
            childTable = new { priorCharges = retainedChildren, collector = 1, fixtures = freshCases, captures = freshCases, exports = freshCases, runtimeValidators = freshCases, inspections = freshCases, spare = latchRepair ? 0 : 1 },
            timingBudget = new { perCaseSetup = 330, perCaseExport = 130, perCaseInspection = 60, perCaseRelease = 30,
                perCaseOperatorLaunch = automatic ? 0 : coordinated ? 60 : 0, perCaseUserFocus = manual ? 60 : 0,
                perCaseSeparateFixtureReady = manual ? 15 : 0, perCaseSeparateFocusConfirmation = manual ? 10 : 0, fixtureLifetimeSeconds = manual ? 420 : 350,
                preflightAndClosure = 240, total = automatic ? 865 : finalWorker ? 925 : manual ? 2325 : coordinated ? 2070 : 1890, reserved = reserve },
            policy = (finalWorker ? "Three retained training samples/model and two complete held-out H02/H01 sources remain unchanged. Fresh H03 only; failed worker case/batches stay failed. "
                : "Three retained valid training samples/model unchanged; failed prior batch stays failed. Fresh H02/H01/H03 only. ")
                + "Ordinary native snapshot reader; continuous active500ms through validation; completed raw archive before310s prune horizon with<=5s controller polling; one fallback fully reserved and abort-before-encoder/no next case; all MP4 aliases deduped/released before final allocation. H03 encoder failure before the validation hook can lose its temporary partial to ordinary cleanup; source/log/receipt and all earlier evidence remain. Exact candidate only; no account activation, rebuilt-binary transfer or release claim.",
            terminalHooks = terminalHooks ? "Complete capture claim immediately after actual capture/audio absence, preserving any safety failure. Stop cutoff/log/archive polling once source is stopped. Retain MP4 by hardlink only in the guarded transition; verify identity/length terminally. Record bounded callback/hardlink timing; all500ms product/sampler guards unchanged." : null,
            workerProfileObservation = "Fresh anchored directory extents for all current H03 worker-profile controls; no leaf opens, including pending/journal/backup files. Actual concurrent Commit/PersistState/status replacement control must pass. Historical leases/media handling unchanged.",
            workerLatchObservation = latchRepair ? "During acquisition/export/retirement, collector health uses ordinary worker protocol metrics only. No independent latch reads contend with the coordinator's exclusive lock. Pause proves producer absence; raw durable guard proof is deferred until quit and worker/host join, and must be clear before sample qualification. A retained hold fails the source. Actual private-lock negative and coordinator status/normal-retirement positive controls must pass." : null,
            archiveEligibility = "Archive every eligible completed source input through the coordinated source reader. Skip only startup chunk0 when the actual source declares ExcludeInitialSegment; the worker may already have discarded that ineligible file. Any missing eligible input still aborts.",
            automaticFixtureHandoff = automatic ? "The reviewed plan admits automatic fixture launch without external operator markers. Actual owned-window foreground/visibility still required within60s and independently confirmed; no explicit activation/input or focus-policy bypass. Abort before acquisition if focus fails." : null,
            foreground = automatic ? "Automatically launch the one admitted owned fixture without external markers. Normal window appearance must supply actual foreground; owner/visible/foreground checked within60s and reconfirmed before capture. No explicit input/activation or Windows focus-policy bypass; abort before acquisition if focus fails. Normal owned-window cleanup/restoration remains."
                : manual ? "Before each source: explicit start/readiness markers; normal fixture appearance may acquire foreground automatically, or an available user may focus it. Actual foreground/owner/visibility required within60s before source setup clock starts. Fixture lifetime420s, capture setup330s unchanged. No explicit automated input/activation or Windows focus-policy bypass; normal owned-window cleanup/restoration remains. Abort before acquisition if focus fails." : coordinated ? "Before each source: explicit launch marker, normal ComputerUse activation of the identity-checked returned fixture window, then marker AND actual foreground required within10s. No injected keystrokes/clicks or Windows focus-policy bypass. No capture before actual foreground." : null });
        Console.WriteLine(path); return 0;
    }
}
