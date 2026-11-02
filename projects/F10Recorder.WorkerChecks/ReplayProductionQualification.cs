using System.Text.Json;

namespace F10Recorder;

internal static partial class ReplayCalibrationRun
{
    const int ProductionPriorSeconds = 16200, ProductionFreshSeconds = 7200, ProductionRemainingSeconds = 4200;
    static readonly string[] ProductionTail = ["T03", "H02", "H01", "H03"];
    static int ProductionReserve(CalibrationPlan p) => p.ProductionTrainingRoot == null ? 4200 : 2820;
    static void ValidateProductionQualification(CalibrationPlan p)
    {
        bool resume = p.ProductionTrainingRoot != null;
        Require(p.ProductionQualification && p.Order.SequenceEqual(resume ? ProductionTail : Order)
            && p.RetainedChildren == (resume ? 174 : 161) && p.MaximumChildren == 210 && p.ValidationChildren == (resume ? 4 : 6)
            && p.BudgetBytes == 60_000_000_000 && p.StopBytes == 58_000_000_000 && p.FloorBytes == 60_000_000_000
            && p.DeadlineSeconds == ProductionPriorSeconds + ProductionFreshSeconds
            && double.IsFinite(p.RetainedElapsedSeconds) && p.RetainedElapsedSeconds >= ProductionPriorSeconds
            && p.RetainedElapsedSeconds + ProductionReserve(p) < p.DeadlineSeconds
            && p.RetainedOfflineSeconds == (resume ? 1240 : 1200) && p.OfflineDeadlineSeconds == 2700
            && p.SourceComponentBytes == 850_000_000 && p.OutputComponentBytes == 1_100_000_000
            && p.RetainedBudgetBytes == 36_000_000_000 && p.OutsideBuildAllowanceBytes == 1_073_741_824
            && p.FileLimit == 40960 && p.DirectoryLimit == 4096 && p.RetainActiveOutputHandles
            && p.ReuseCurrentMetadataHandles && p.FreezeRetainedAllocations && !p.ReadonlyValidationPhases
            && p.OperatorForegroundHandoff && p.UserForegroundHandoff && p.AutomaticFixtureHandoff
            && p.ExactContinuationRoot == null && p.ExactHeldoutContinuationRoot == null
            && p.RetainedRoots is { Length: > 0 and <= 64 } && p.RetainedRoots.All(Directory.Exists)
            && p.MetadataHandleRoots.SequenceEqual(p.RetainedRoots), "Production qualification boundary changed.");
        ValidateContinuationRoots14(p);
        Require(!resume || p.RetainedRoots.Contains(p.ProductionTrainingRoot, StringComparer.OrdinalIgnoreCase), "Retained training root must be charged.");
    }
    static double ProductionElapsed(JsonElement phase, DateTime now)
    {
        DateTime start = phase.GetProperty("startedUtc").GetDateTime(), end = phase.GetProperty("absoluteDeadlineUtc").GetDateTime();
        Require(start.Kind == DateTimeKind.Utc && end.Kind == DateTimeKind.Utc && now.Kind == DateTimeKind.Utc
            && start <= now && end == start.AddSeconds(ProductionFreshSeconds)
            && phase.GetProperty("priorElapsedSeconds").GetInt32() == ProductionPriorSeconds,
            "Fresh production phase must retain prior closed time and bind its absolute deadline.");
        double elapsed = ProductionPriorSeconds + (now - start).TotalSeconds;
        bool resume = phase.TryGetProperty("trainingContinuationRoot", out var training) && training.ValueKind == JsonValueKind.String;
        Require(elapsed + (resume ? 2820 : 4200) < ProductionPriorSeconds + ProductionFreshSeconds,
            "Full six-source production schedule no longer fits.");
        return elapsed;
    }
    static List<(Sample Sample, ReplayCalibrationSourceReceipt Receipt)> ReadProductionTraining(string root, string destination)
    {
        Require(!File.Exists(Path.Combine(root, "frozen-training-model.json")), "A partial-training continuation must not import a model.");
        using var failure = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "failure.json")));
        Require(failure.RootElement.GetProperty("model").ValueKind == JsonValueKind.Null
            && failure.RootElement.GetProperty("samples").EnumerateArray().Select(s => s.GetProperty("Label").GetString()).SequenceEqual(new[] { "T01", "T02" })
            && failure.RootElement.GetProperty("MaximumActiveGap").GetDouble() <= 500
            && failure.RootElement.GetProperty("offlineSeconds").GetDouble() <= 1240,
            "Partial training evidence, original gap or retained offline charge changed.");
        using var admitted = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "admitted-plan.json")));
        var bindings = admitted.RootElement.GetProperty("plan").GetProperty("Files").EnumerateObject().ToArray();
        Require(bindings.Any(p => Path.GetFileName(p.Name) == "F10Recorder.Windows.dll" && p.Value.GetString() == ReplayReferenceModel.AssemblyHash)
            && bindings.Any(p => Path.GetFileName(p.Name) == "F10Recorder.Core.dll" && p.Value.GetString() == ReplayReferenceModel.CoreHash),
            "Retained training belongs to different product bytes.");
        var capacity = ReplayStorage.ObserveCapacity(Path.GetDirectoryName(destination));
        var result = new List<(Sample Sample, ReplayCalibrationSourceReceipt Receipt)>();
        foreach (string label in new[] { "T01", "T02" })
        {
            string cell = Path.Combine(root, label), samplePath = Path.Combine(cell, "sample.json");
            var sample = JsonSerializer.Deserialize<Sample>(File.ReadAllText(samplePath));
            var q = JsonSerializer.Deserialize<ReplayCalibrationSourceReceipt>(File.ReadAllText(Path.Combine(cell, "qualification-source.json")));
            Require(sample.Label == label && q.Label == label && q.SampleHash == HashFile(samplePath)
                && q.Scope == sample.Scope && q.Volume == capacity.Volume && q.AllocationUnit == capacity.AllocationUnit
                && q.InputBytes == sample.InputBytes && q.FinalBytes == sample.FinalBytes && q.FinalAllocation == sample.FinalAllocation
                && q.PeakAllocation == sample.PeakAllocation && q.SelectionHash == sample.SelectionIdentity
                && sample.ExportSeconds == 300 && q.ExportSeconds == 300 && sample.Interval.Seconds == 300
                && q.OutputStart == sample.Interval.Start && q.OutputEnd == sample.Interval.End
                && q.Closed && q.AllTracksDecoded && q.SourceLineageVerified && q.NonSilentAudio && q.ReferenceVideoFormat && q.ReferenceAudioFormat
                && q.VideoWidth == 1920 && q.VideoHeight == 1080 && q.AudioSampleRate == 48000 && q.AudioChannels == 2
                && double.IsFinite(q.AudioRmsDb) && q.AudioRmsDb <= 0 && q.MaximumActiveGapMs is > 0 and <= 500
                && new[] { q.Capture, q.Source, q.Export, q.Decode, q.RuntimeValidation }.All(p => p is
                    { ProducerAbsent: true, ActualExitObserved: true, ExitCode: 0, TimedOut: false, OutputLimitExceeded: false, ForcedTermination: false }),
                "Retained training sample is incomplete.");
            foreach (object process in new[] { sample.Process, sample.RuntimeProcess })
            {
                var p = JsonSerializer.SerializeToElement(process);
                Require(p.GetProperty("ProcessId").GetInt32() > 0 && p.GetProperty("ProducerAbsent").GetBoolean()
                    && p.GetProperty("ActualExitObserved").GetBoolean() && p.GetProperty("ExitCode").GetInt32() == 0
                    && p.GetProperty("CaptureComplete").GetBoolean() && !p.GetProperty("TimedOut").GetBoolean()
                    && !p.GetProperty("OutputLimitExceeded").GetBoolean(), "Retained native process closure is incomplete.");
            }
            string capture = Path.Combine(cell, "capture-exit.json"), fixture = Path.Combine(cell, "fixture-exit.json");
            Require(q.Capture.EvidenceHash == HashFile(capture) && q.Source.EvidenceHash == HashFile(fixture)
                && q.Export.EvidenceHash == HashFile(samplePath)
                && q.RuntimeValidation.EvidenceHash == HashFile(Path.Combine(cell, "runtime-validation-exit.json"))
                && q.Decode.EvidenceHash == HashFile(Path.Combine(cell, Path.GetFileName(sample.File) + ".decode.json"))
                && q.SourcePicturesHash == HashFile(Path.Combine(cell, Path.GetFileName(sample.File) + ".source-pictures.json"))
                && new FileInfo(sample.File).Length == sample.FinalBytes, "Retained evidence bytes changed.");
            using var growth = JsonDocument.Parse(File.ReadAllText(Path.Combine(cell, "capture-growth.json")));
            Require(q.CaptureVolume == capacity.Volume && q.CaptureVolume == growth.RootElement.GetProperty("Volume").GetString()
                && q.CapturePollGrowthBytes == growth.RootElement.GetProperty("PollGrowth").GetInt64()
                && q.CaptureStopGrowthBytes == growth.RootElement.GetProperty("StopGrowth").GetInt64()
                && q.CapturePollGrowthBytes > 0 && q.CaptureStopGrowthBytes >= 0
                && growth.RootElement.GetProperty("MaximumActiveGap").GetDouble() <= 500,
                "Original per-source growth must be retained, never reconstructed.");
            result.Add((sample, q));
        }
        Require(result.Select(r => r.Sample.Scope).Distinct().Count() == 1
            && result.Select(r => r.Sample.SelectionIdentity).Distinct().Count() == 2
            && result.Select(r => r.Receipt.AcquisitionIdentity).Distinct().Count() == 2
            && result.Select(r => r.Receipt.Capture.EvidenceHash).Distinct().Count() == 2,
            "Retained training acquisitions or selections are not independent.");
        return result;
    }
    static void VerifyProductionDestinations(JsonElement phase, string root)
    {
        string settingsPath = Path.GetFullPath(phase.GetProperty("productionSettings").GetString());
        var settings = Settings.Load(settingsPath);
        Require(ReplayReferenceModel.ReferenceSettings(settings), "Preserved production settings are outside the reference cell.");
        string cache = ReplayCacheLocations.Current(settings, Path.GetDirectoryName(settingsPath));
        var reference = ReplayStorage.ObserveCapacity(Path.GetDirectoryName(root));
        foreach (string destination in new[] { cache, settings.OutputDirectory })
        {
            var capacity = ReplayStorage.ObserveCapacity(destination);
            Require(capacity.Volume == reference.Volume && capacity.AllocationUnit == reference.AllocationUnit,
                "Qualification must use the preserved production cache/output volume and allocation unit.");
        }
    }
    static object ProductionWorkingDirectoryCheck(string repository, string controlRoot)
    {
        string previous = Environment.CurrentDirectory;
        bool rejected = false;
        try { using var invalid = PilotStorageObservation.Create(controlRoot); }
        catch (IOException) { rejected = true; }
        Require(rejected && !Directory.Exists(controlRoot), "Wrong working-directory root must reject without creation.");
        try
        {
            Environment.CurrentDirectory = repository;
            using var valid = PilotStorageObservation.Create(controlRoot);
            Require(Directory.Exists(controlRoot), "Correct worktree did not admit its tiny fresh artifacts root.");
        }
        finally { Environment.CurrentDirectory = previous; }
        return new { result = "passed", wrongWorkingDirectoryRejected = true, correctWorkingDirectoryRootCreated = true,
            expectedWorkingDirectory = repository, restored = Environment.CurrentDirectory == previous,
            effects = "Tiny task-owned marker and directory only; no child, worker, capture, audio or account state." };
    }
    static object ProductionFixtureArgumentChecks(string root)
    {
        int valid = 0, invalid = 0;
        foreach (string label in Order)
            foreach (bool manual in new[] { false, true })
            {
                string[] args = manual ? ["--calibration-fixture", root, label, "manual-handoff"] : ["--calibration-fixture", root, label];
                Require(WorkerChecks.TryCalibrationFixture(args, out string parsedRoot, out string parsedLabel, out bool parsedManual)
                    && parsedRoot == Path.GetFullPath(root) && parsedLabel == label && parsedManual == manual,
                    "Actual fixture dispatch rejected an admitted training/held-out form."); valid++;
            }
        foreach (string[] bad in new[] { new[] { "--calibration-fixture", root, "UNKNOWN" },
            ["--calibration-fixture", root, "T01", "wrong-token"], ["--calibration-fixture", root, "H03", "manual-handoff", "extra"],
            ["--wrong-command", root, "T01"], ["--calibration-fixture", " ", "T02"] })
        { Require(!WorkerChecks.TryCalibrationFixture(bad, out _, out _, out _), "Malformed fixture arguments passed actual dispatch."); invalid++; }
        return new { result = "passed", validForms = valid, invalidForms = invalid, actualMainParser = true, effects = "Pure argument checks; no fixture/UI/process/device activity." };
    }
    static string VerifyProductionPackage(string manifestFile)
    {
        Require(new FileInfo(manifestFile).Length <= 131072, "Production manifest too large.");
        using var json = JsonDocument.Parse(File.ReadAllText(manifestFile)); var manifest = json.RootElement;
        string directory = Path.GetFullPath(manifest.GetProperty("packageDirectory").GetString());
        var files = manifest.GetProperty("files").EnumerateArray().ToArray();
        Require(files.Length is > 0 and <= 512 && Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Length == files.Length,
            "Production package membership changed.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            string path = Path.GetFullPath(Path.Combine(directory, file.GetProperty("path").GetString()));
            Require(path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && seen.Add(path)
                && new FileInfo(path).Length == file.GetProperty("bytes").GetInt64()
                && HashFile(path) == file.GetProperty("sha256").GetString(), "Frozen production package changed: " + path);
        }
        string build = Path.GetDirectoryName(typeof(ReplayCalibrationRun).Assembly.Location);
        foreach (string name in new[] { "F10Recorder.Windows.dll", "F10Recorder.Core.dll" })
            Require(HashFile(Path.Combine(directory, name)) == HashFile(Path.Combine(build, name)),
                "Collector must execute the exact frozen production library bytes.");
        return directory;
    }
    static CalibrationPlan VerifyProductionExecution(CalibrationPlan p, JsonElement document)
    {
        var phase = document.GetProperty("nativePhase");
        Require(Path.GetFullPath(Environment.CurrentDirectory).Equals(phase.GetProperty("workingRepository").GetString(), StringComparison.OrdinalIgnoreCase),
            "Production collector must run in its isolated recording worktree.");
        double elapsed = ProductionElapsed(phase, DateTime.UtcNow);
        string trainingRoot = phase.TryGetProperty("trainingContinuationRoot", out var trainingProperty) && trainingProperty.ValueKind == JsonValueKind.String
            ? Path.GetFullPath(trainingProperty.GetString()) : null;
        bool resume = trainingRoot != null;
        Require(trainingRoot == p.ProductionTrainingRoot && phase.GetProperty("conservativeRemainingSeconds").GetInt32() == ProductionReserve(p),
            "Production training continuation or schedule changed.");
        Require(elapsed >= p.RetainedElapsedSeconds, "Production phase clock moved backwards.");
        string historyFamily = Path.GetFullPath(phase.GetProperty("historyFamily").GetString());
        var actual = Directory.GetDirectories(historyFamily).Concat(Directory.GetDirectories(Path.GetDirectoryName(p.Root)))
            .Where(r => !Path.GetFullPath(r).Equals(Path.GetFullPath(p.Root), StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);
        Require(actual.SequenceEqual(p.RetainedRoots.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase),
            "Retained production/history family changed after admission.");
        VerifyProductionDestinations(phase, p.Root);
        _ = VerifyProductionPackage(p.Files.Keys.Single(f => Path.GetFileName(f) == "package-manifest.json"));
        return p with { RetainedElapsedSeconds = elapsed };
    }
    internal static int PrepareProductionQualification(string path, string engineRoot, string root, string phaseFile)
    {
        root = Path.GetFullPath(root); Require(!Directory.Exists(root), "Fresh production evidence root required.");
        using var document = JsonDocument.Parse(File.ReadAllText(phaseFile)); var phase = document.RootElement;
        double elapsed = ProductionElapsed(phase, DateTime.UtcNow);
        string trainingRoot = phase.TryGetProperty("trainingContinuationRoot", out var trainingProperty) && trainingProperty.ValueKind == JsonValueKind.String
            ? Path.GetFullPath(trainingProperty.GetString()) : null;
        bool resume = trainingRoot != null;
        string manifest = Path.GetFullPath(phase.GetProperty("packageManifest").GetString());
        _ = VerifyProductionPackage(manifest);
        string closure = Path.GetFullPath(phase.GetProperty("priorClosure").GetString());
        using (var previous = JsonDocument.Parse(File.ReadAllText(closure)))
            Require(previous.RootElement.GetProperty("nativeAcquisitionsCharged").GetInt32() == (resume ? 20 : 18)
                && previous.RootElement.GetProperty("cumulativeProcessCharges").GetInt32() == (resume ? 173 : 160)
                && previous.RootElement.GetProperty("remainingOwnedProcesses").GetInt32() == 0,
                "Prior acquisition/process closure changed.");
        MediaDependency.RootDirectory = Path.GetFullPath(engineRoot); string engine = MediaDependency.ResolveExecutable();
        string build = Path.GetDirectoryName(typeof(ReplayCalibrationRun).Assembly.Location);
        var files = new[] { Environment.ProcessPath, typeof(ReplayCalibrationRun).Assembly.Location,
            Path.Combine(build, "F10Recorder.Windows.dll"), Path.Combine(build, "F10Recorder.Core.dll"),
            Path.Combine(build, "F10Recorder.WorkerChecks.deps.json"), Path.Combine(build, "F10Recorder.WorkerChecks.runtimeconfig.json"),
            engine, Path.GetFullPath("tests/replay-calibration.ps1"), Path.GetFullPath(phaseFile), manifest, closure }
            .ToDictionary(Path.GetFullPath, HashFile, StringComparer.OrdinalIgnoreCase);
        string previousLaunch = Path.GetFullPath(phase.GetProperty("previousLaunchReceipt").GetString()); files[previousLaunch] = HashFile(previousLaunch);
        using (var failed = JsonDocument.Parse(File.ReadAllText(previousLaunch)))
        {
            Require(failed.RootElement.GetProperty("confirmedExited").GetBoolean() && failed.RootElement.GetProperty("exit").GetInt32() != 0,
                "Prior failed collector closure is unconfirmed.");
            foreach (string role in new[] { "stdout", "stderr" })
            { string file = failed.RootElement.GetProperty(role).GetString(); files[file] = HashFile(file); }
        }
        string priorControls = Path.GetFullPath(phase.GetProperty("engineControlsRoot").GetString());
        foreach (string name in new[] { "version", "nonzero", "timeout", "output-limit" })
        { string file = Path.Combine(priorControls, name + ".json"); files[file] = HashFile(file); }
        string historyFamily = Path.GetFullPath(phase.GetProperty("historyFamily").GetString());
        string[] histories = Directory.GetDirectories(historyFamily).Concat(Directory.GetDirectories(Path.GetDirectoryName(root)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        string workingRepository = Path.GetFullPath(phase.GetProperty("workingRepository").GetString());
        Require(File.Exists(Path.Combine(workingRepository, ".git"))
            && root.StartsWith(Path.Combine(workingRepository, "artifacts") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "Production evidence must be a fresh child of the isolated worktree artifacts.");
        VerifyProductionDestinations(phase, root);
        string settingsPath = Path.GetFullPath(phase.GetProperty("productionSettings").GetString()); files[settingsPath] = HashFile(settingsPath);
        if (resume)
        {
            var retained = ReadProductionTraining(trainingRoot, root);
            foreach (string name in new[] { "storage.csv", "failure.json", "admitted-plan.json", "child-admissions.jsonl", "T03/fixture-exit.json", "T03/primary-failure.json" })
            { string file = Path.Combine(trainingRoot, name); files[file] = HashFile(file); }
            foreach (var entry in retained)
            {
                string cell = Path.Combine(trainingRoot, entry.Sample.Label);
                foreach (string name in new[] { "sample.json", "qualification-source.json", "capture-growth.json", "capture-exit.json", "fixture-exit.json",
                    "runtime-validation-exit.json", "output-handle-release.json", "capture-arguments.json", "selection.jsonl",
                    Path.GetFileName(entry.Sample.File) + ".decode.json", Path.GetFileName(entry.Sample.File) + ".source-pictures.json" })
                { string file = Path.Combine(cell, name); files[file] = HashFile(file); }
                files[entry.Sample.File] = HashFile(entry.Sample.File);
            }
        }
        long baseline = ReplayStorage.Bytes(ReplayStorage.Inventory(histories, new ReplayFiles(), 40960, 4096));
        var capacity = ReplayStorage.ObserveCapacity(Path.GetDirectoryName(root));
        long forward = 6 * 850_000_000L + 6 * 1_100_000_000L + 1_010_000_000L + 387_108_864L + 1_073_741_824L;
        Require(baseline <= 36_000_000_000 && baseline + forward < 58_000_000_000
            && baseline + forward + 1_256_000_000 < 60_000_000_000 && capacity.AvailableBytes >= 120_000_000_000,
            "Full retained family plus six sources, outputs and closure cannot fit.");
        using var audio = new LoopbackAudio(); var bounds = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
        Require(bounds.Width == 1920 && bounds.Height == 1080 && audio.SampleRate == 48000 && audio.Channels == 2,
            "Reference display/audio geometry changed; no settings changes admitted.");
        var environment = new { os = Environment.OSVersion.VersionString, machine = Environment.MachineName, bounds.Width, bounds.Height,
            primaryOutput = Native.PrimaryOutputIndex(), audio.EndpointId, audio.SampleRate, audio.Channels, audio.FfmpegFormat, capacity };
        string controls = Path.Combine(Path.GetDirectoryName(path), "preparation-controls"); Directory.CreateDirectory(controls);
        Write(Path.Combine(controls, "evidence.json"), NativeEvidenceChecks(Path.Combine(controls, "evidence")));
        Write(Path.Combine(controls, "worker-profile.json"), WorkerProfileObservationChecks(Path.Combine(controls, "worker-profile")));
        Write(Path.Combine(controls, "working-directory.json"), ProductionWorkingDirectoryCheck(workingRepository,
            Path.Combine(workingRepository, "artifacts", "production-working-directory-control-" + Path.GetFileName(root))));
        Write(Path.Combine(controls, "fixture-arguments.json"), ProductionFixtureArgumentChecks(root));
        foreach (string file in Directory.GetFiles(controls, "*.json")) files[file] = HashFile(file);
        var p = new CalibrationPlan(root, MediaDependency.RootDirectory, ReplayReferenceModel.Hash(environment), files, resume ? ProductionTail : Order, histories, elapsed, resume ? 174 : 161,
            MaximumChildren: 210, BudgetBytes: 60_000_000_000, StopBytes: 58_000_000_000, DeadlineSeconds: 23400,
            RetainedOfflineSeconds: resume ? 1240 : 1200, MetadataHandleRoots: histories, ReuseCurrentMetadataHandles: true, DirectoryLimit: 4096,
            OutputComponentBytes: 1_100_000_000, RetainedBudgetBytes: 36_000_000_000, FileLimit: 40960, OfflineDeadlineSeconds: 2700,
            OutsideBuildAllowanceBytes: 1_073_741_824, FreezeRetainedAllocations: true, ReplayExporterRecipe: ReplayExactExport.Recipe,
            ValidationChildren: resume ? 4 : 6, SourceComponentBytes: 850_000_000, RetainActiveOutputHandles: true,
            OperatorForegroundHandoff: true, UserForegroundHandoff: true, AutomaticFixtureHandoff: true, ProductionQualification: true,
            ProductionTrainingRoot: trainingRoot);
        ValidateResources(p); RequireExactExporterAdmission(p);
        int rejected = 0;
        foreach (var bad in new[] { p with { MaximumChildren = 211 }, p with { RetainedChildren = p.RetainedChildren + 1 }, p with { ValidationChildren = p.ValidationChildren - 1 },
            p with { BudgetBytes = 60_000_000_001 }, p with { StopBytes = 58_000_000_001 }, p with { FloorBytes = 59_999_999_999 },
            p with { Order = Heldout14 }, p with { AutomaticFixtureHandoff = false }, p with { ReadonlyValidationPhases = true },
            p with { ExactContinuationRoot = histories[0] }, p with { RetainedElapsedSeconds = 23400 - ProductionReserve(p) }, p with { RetainedOfflineSeconds = 0 },
            p with { ProductionTrainingRoot = resume ? null : histories[0] } })
        { bool failed = false; try { ValidateResources(bad); } catch (IOException) { failed = true; } Require(failed, "Changed production boundary passed."); rejected++; }
        Write(Path.Combine(controls, "resources.json"), new { result = "passed", rejected });
        files[Path.Combine(controls, "resources.json")] = HashFile(Path.Combine(controls, "resources.json"));
        Write(path, new { plan = p, environment, nativePhase = new { startedUtc = phase.GetProperty("startedUtc").GetDateTime(),
                absoluteDeadlineUtc = phase.GetProperty("absoluteDeadlineUtc").GetDateTime(), priorElapsedSeconds = ProductionPriorSeconds,
                conservativeRemainingSeconds = ProductionReserve(p), trainingContinuationRoot = trainingRoot, historyFamily,
                productionSettings = settingsPath, workingRepository, receipt = Path.GetFullPath(phaseFile) },
            baseline, forward, projectedWithClosure = baseline + forward + 1_256_000_000, effects = Effects(p), recipe = Recipe(p),
            childTable = new { priorIncludingPreparer = p.RetainedChildren, collector = 1, engineControls = 0, reusedEngineControls = 4, fixtures = p.Order.Length,
                captures = p.Order.Length, encoders = p.Order.Length, runtimeDecoders = p.Order.Length, inspections = p.Order.Length,
                plannedTotal = p.RetainedChildren + 1 + 5 * p.Order.Length, ceiling = 210 },
            policy = "Exact frozen production package on C volume. A partial-training continuation retains only the two unchanged complete T01/T02 sources from the same package and C volume; no old Debug/D-volume model or held-out sample reuse. New model freezes only after fresh T03, no refit. Native reader/hardlinks, all500ms guards through validation, source-growth persisted before inspection, guard-clear after worker join. One NORMAL owned-window ShowWindow/SetForegroundWindow request before acquisition; actual owner/visibility/foreground still required, no injected input/focus-policy bypass. All failed batches stay failed/retained. No account activation, install/startup/preferences changes or deletions in this run.",
            timing = new { freshSeconds = 7200, priorSeconds = 16200, reservedSeconds = ProductionReserve(p), perSourceSetup = 330, perSourceSave = 130, perSourceInspection = 60, perSourceRelease = 30, perSourceFocus = 85 } });
        Console.WriteLine(path); return 0;
    }
}
