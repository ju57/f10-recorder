using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Forms = System.Windows.Forms;

namespace F10Recorder;

internal sealed partial record CalibrationPlan(string Root, string EngineRoot, string Environment,
    Dictionary<string, string> Files, string[] Order, string[] RetainedRoots, double RetainedElapsedSeconds, int RetainedChildren,
    int MaximumChildren = 46, long BudgetBytes = 64_000_000_000,
    long StopBytes = 62_500_000_000, long FloorBytes = 60_000_000_000, int DeadlineSeconds = 9000,
    double? RetainedOfflineSeconds = null, string RehearsalEvidenceRoot = null, double RehearsalElapsedSeconds = 0,
    string[] MetadataHandleRoots = null, bool ReuseCurrentMetadataHandles = false,
    int DirectoryLimit = 48, long OutputComponentBytes = 1_032_000_000, long RetainedBudgetBytes = 32_000_000_000,
    int FileLimit = 4096, int OfflineDeadlineSeconds = 1800, long OutsideBuildAllowanceBytes = 1_000_000_000,
    bool FreezeRetainedAllocations = false, string ReplayExporterRecipe = null, int ValidationChildren = 0, string RehearsalReuseRoot = null,
    long SourceComponentBytes = 1_000_000_000, bool RetainActiveOutputHandles = false, bool ReadonlyValidationPhases = false,
    string ExactContinuationRoot = null, bool OperatorForegroundHandoff = false, bool UserForegroundHandoff = false, string ExactHeldoutContinuationRoot = null,
    bool AutomaticFixtureHandoff = false, bool ProductionQualification = false, string ProductionTrainingRoot = null,
    bool CompositeQualification = false, bool CompositeManualForegroundHandoff = false, string CompositeTrainingRoot = null,
    bool CompositeExplicitForegroundRelease = false,
    long AdditionalHistoricalReserveBytes = 0, long CopyFallbackReserveBytes = 0,
    string[] SelectedRetainedFiles = null);

// Purpose-specific controller. It never provisions tools, edits ordinary profiles,
// runs games, changes NVIDIA/display/audio preferences or retries a failed batch.
internal static partial class ReplayCalibrationRun
{
    static ReplayPairedSelectionLease sameCaptureInputOwner;
    static FrozenCalibrationHistory sameCapturePresentationOwner;
    internal const int MainInspectionSeconds = 60, ShortInspectionSeconds = 30, ArchiveSeconds = 60;
    internal const int CompleteOfflineBatchSeconds = 6 * MainInspectionSeconds + 2 * ShortInspectionSeconds + 8 * ArchiveSeconds;
    internal const int ReplacementVerificationSeconds = 60 + 120; // Preflight plus retained-output/preservation audit.
    internal const int FullReplacementVerificationSeconds = 150 + 240;
    internal const int FullReplacementWallSeconds = 4200;
    const int InspectionCleanupSeconds = 10;
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, IncludeFields = true };
    static readonly JsonSerializerOptions TerminalJson = new(Json) { WriteIndented = false };
    static readonly string[] Order = ["T01", "T02", "T03", "H02", "H01", "H03"];
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    static void Require(bool value, string message) { if (!value) throw new IOException(message); }
    internal static bool ConfirmedProducerAbsent(ReplayExportProcessReceipt receipt)
        => receipt is { ProducerAbsent: true } &&
            (receipt is { ProcessId: > 0, ProcessStartedUtc: not null, ActualExitObserved: true }
                // Run joins launch work before returning this exact no-child
                // shape. A missing PID or an arbitrary failure is insufficient.
                || receipt is { ProcessId: null, ProcessStartedUtc: null, ActualExitObserved: false, Failure: "launch-failed" });
    static void RequireExactExporterAdmission(CalibrationPlan plan)
        => Require(plan?.ReplayExporterRecipe == ReplayExactExport.Recipe && plan.ValidationChildren >= (plan.ProductionQualification ? plan.Order.Length : ExactFinalWorker14(plan) ? 1 : ExactContinuation14(plan) ? 3 : 8)
            && plan.MaximumChildren - plan.RetainedChildren >= (plan.ProductionQualification ? 1 + 5 * plan.Order.Length : ExactFinalWorker14(plan) ? 6 : ExactContinuation14(plan) ? 16 : plan.ReadonlyValidationPhases && (plan.MaximumChildren == 97 && plan.RetainedChildren == 60 || plan.MaximumChildren == 105 && plan.RetainedChildren == 68) ? 37 : 40),
            "Legacy copy-export calibration admission does not cover exact re-encoding and eight runtime validation children. Prepare a newly reviewed recipe/resource admission before live collection.");
    static string HashFile(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
    static void Write(string path, object value, int maximumBytes = 131072)
    {
        // Terminal reports aggregate six full samples. Whitespace must not make
        // a valid complete report exceed the same bounded evidence envelope.
        bool terminal = Path.GetFileName(path) is "result.json" or "failure.json";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, terminal ? TerminalJson : Json);
        Require(maximumBytes is >= 131072 and <= 4194304 && bytes.Length <= maximumBytes, "Calibration JSON exceeds its finite byte bound.");
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        output.Write(bytes); output.Flush(true);
    }
    static void WriteFailure(string root, Exception error, object report)
    {
        Console.Error.WriteLine(error + " Evidence: " + root);
        try { Write(Path.Combine(root, "failure.json"), report); }
        catch (Exception reportingError)
        {
            // Reporting failure must not replace the original failure or skip
            // Run's finally/owned-output cleanup. Never overwrite a partial file.
            static string Bounded(Exception e) { string text = e.ToString(); return text.Length <= 8192 ? text : text[..8192]; }
            Console.Error.WriteLine("Failure report could not be written: " + Bounded(reportingError));
            try { Write(Path.Combine(root, "failure-summary.json"), new { result = "failed-incomplete-report", error = Bounded(error),
                reportingError = Bounded(reportingError), fullReportUnavailable = true }); }
            catch (Exception fallbackError) { Console.Error.WriteLine("Failure summary could not be written: " + Bounded(fallbackError)); }
        }
    }
    internal static int Prepare(string path, string engineRoot, bool composite = false, bool compositeManualForegroundHandoff = false,
        string compositeTrainingRoot = null, bool compositeExplicitForegroundRelease = false)
    {
        string assembly = typeof(ReplayCalibrationRun).Assembly.Location;
        string build = Path.GetDirectoryName(assembly);
        MediaDependency.RootDirectory = Path.GetFullPath(engineRoot);
        string engine = MediaDependency.ResolveExecutable(); // Read-only verification, no probe or repair.
        var files = new[] { Environment.ProcessPath, assembly, Path.Combine(build, "F10Recorder.Windows.dll"), Path.Combine(build, "F10Recorder.Core.dll"),
            Path.Combine(build, "F10Recorder.WorkerChecks.deps.json"), Path.Combine(build, "F10Recorder.WorkerChecks.runtimeconfig.json"), engine }
            .ToDictionary(Path.GetFullPath, HashFile, StringComparer.OrdinalIgnoreCase);
        string admission = Path.GetDirectoryName(Path.GetFullPath(path));
        if(composite)PrepareOwnedHistoryOutside(admission);
        if (composite)
        {
            string controller = Path.GetFullPath(Path.Combine("tests", "replay-composite-calibration.ps1"));
            files.Add(controller, HashFile(controller));
        }
        foreach (string evidence in new[] { "source-manifest.json", "host-facts.json", "preservation-before.json", "RUN.md", "retained-attempt.json", "resources.json", "rehearsal.json" })
        {
            string file = Path.Combine(admission, evidence);
            Require(File.Exists(file), "Missing frozen admission evidence: " + file);
            files.Add(file, HashFile(file));
        }
        if (compositeTrainingRoot != null)
        {
            string retainedBinding = Path.Combine(admission, "composite-training-bindings.json");
            files.Add(retainedBinding, HashFile(retainedBinding));
        }
        if (composite)
        {
            foreach (string name in new[] { "outside-storage-baseline.json", "outside-inventory.json", "outside-inventory-plan.json" })
            { string file = Path.Combine(admission, name); files.Add(file, HashFile(file)); }
            using var outside=JsonDocument.Parse(File.ReadAllText(Path.Combine(admission,"outside-inventory-plan.json")));
            if(outside.RootElement.TryGetProperty("kind",out var outsideKind) && outsideKind.GetString()=="owned-history-fixed-outside-inputs-v1")
                foreach(var entry in outside.RootElement.GetProperty("files").EnumerateArray())
                {
                    string file=entry.GetProperty("path").GetString(),hash=entry.GetProperty("sha256").GetString();
                    Require(HashFile(file)==hash && (!files.TryGetValue(file,out var bound)||bound==hash),"Changed declared fixed outside input.");
                    files.TryAdd(file,hash);
                }
        }
        string dotnet = Path.GetDirectoryName(Environment.ProcessPath);
        foreach (string directory in new[] { Path.Combine(dotnet, "host", "fxr"), Path.Combine(dotnet, "shared", "Microsoft.NETCore.App"),
            Path.Combine(dotnet, "shared", "Microsoft.WindowsDesktop.App") })
            foreach (string name in new[] { "hostfxr.dll", "coreclr.dll", "hostpolicy.dll", "System.Private.CoreLib.dll", "System.Windows.Forms.dll", "System.Drawing.Common.dll", "System.Windows.Extensions.dll" })
                foreach (string file in Directory.GetFiles(directory, name, SearchOption.AllDirectories)) files.TryAdd(file, HashFile(file));
        using var endpoint = new LoopbackAudio(); // Mix-format query only, never Start.
        var bounds = Forms.Screen.PrimaryScreen.Bounds;
        Require(bounds.Width == 1920 && bounds.Height == 1080, "Reference requires current primary1920x1080; display changes are not permitted.");
        using var resourceRecord = JsonDocument.Parse(File.ReadAllText(Path.Combine(admission, "resources.json")));
        string root = Path.GetFullPath(resourceRecord.RootElement.GetProperty("experimentRoot").GetString());
        Require(!Directory.Exists(root) && !File.Exists(root), "Calibration needs its approved fresh experiment root.");
        var capacity = ReplayStorage.ObserveCapacity(Path.GetDirectoryName(root));
        bool capacityContinuation = resourceRecord.RootElement.TryGetProperty("freshCandidateAdmission", out var candidateKind)
            && candidateKind.GetString() == CapacityContinuationAdmission;
        bool durationContinuation = candidateKind.ValueKind == JsonValueKind.String && candidateKind.GetString() == DurationTrainingAdmission;
        string durationKind = candidateKind.ValueKind == JsonValueKind.String ? candidateKind.GetString() : null;
        var durationShape = DurationPreparation(durationKind,
            DurationAdmissionKind(durationKind) ? resourceRecord.RootElement.GetProperty("replayMinutes").GetInt32() : 5);
        bool durationCohort = durationShape.HasValue;
        int replayMinutes = durationShape?.Minutes ?? 5;
        Require(ReplayReferenceModel.SupportedMinutes(replayMinutes), "Unsupported duration cell.");
        long sourceBytes = durationShape?.Source ?? 1_000_000_000L;
        long outputBytes = durationShape?.Output ?? 2_000_000_000L;
        long copyBytes = durationShape?.Copy ?? 1_000_000_000L;
        long scratchBytes = durationShape?.Scratch ?? 1_256_000_000L;
        long traceBytes = durationShape?.Trace ?? 67_108_864L;
        bool capacityCohort = durationCohort || capacityContinuation || candidateKind.ValueKind == JsonValueKind.String && candidateKind.GetString() is CapacityAdmission or OwnedHistoryAdmission or OwnedHistoryRepairAdmission;
        string[] admittedOrder = durationKind is ContextConfirmationAdmission or SameCaptureContextAdmission or FiveMinuteStorageAdmission ? ["H03"] : compositeTrainingRoot == null ? Order : Order.Skip(capacityContinuation || durationContinuation ? 3 : 2).ToArray();
        long namespaceBytes = DurationAliasNamespaceBytes(durationKind, replayMinutes, admittedOrder.Length);
        long requiredFree = capacityCohort ? new QualificationCommitments(admittedOrder, sourceBytes, outputBytes, copyBytes, scratchBytes,traceBytes,namespaceBytes).InitialRequiredFreeBytes(60_000_000_000L+(durationCohort || candidateKind.GetString() is OwnedHistoryAdmission or OwnedHistoryRepairAdmission?OwnedHistoryOutsideFuture:0))
            : checked(resourceRecord.RootElement.GetProperty("budgetBytes").GetInt64() + 60_000_000_000L);
        Require(capacity.AvailableBytes >= requiredFree,
            "Insufficient caller-free floor plus new finite acquisition/output commitments.");
        var environment = new { os = Environment.OSVersion.VersionString, machine = Environment.MachineName,
            bounds.Width, bounds.Height, primaryOutput = Native.PrimaryOutputIndex(), endpoint.EndpointId, endpoint.SampleRate, endpoint.Channels, endpoint.FfmpegFormat, capacity };
        using var retained = JsonDocument.Parse(File.ReadAllText(Path.Combine(admission, "retained-attempt.json")));
        string[] retainedRoots = retained.RootElement.GetProperty("roots").EnumerateArray().Select(v => Path.GetFullPath(v.GetString())).ToArray();
        double retainedSeconds = retained.RootElement.GetProperty("elapsedSeconds").GetDouble();
        int retainedChildren = retained.RootElement.GetProperty("retainedChildren").GetInt32();
        double offline = retained.RootElement.GetProperty("offlineSeconds").GetDouble();
        var plan = new CalibrationPlan(root, MediaDependency.RootDirectory, ReplayReferenceModel.Hash(environment), files,
            admittedOrder, retainedRoots, retainedSeconds, retainedChildren,
            MaximumChildren: resourceRecord.RootElement.GetProperty("maximumChildren").GetInt32(),
            RetainedOfflineSeconds: offline, MetadataHandleRoots: retainedRoots, ReuseCurrentMetadataHandles: true,
            DirectoryLimit: resourceRecord.RootElement.TryGetProperty("directoryLimit", out var dl) ? dl.GetInt32() : 48,
            OutputComponentBytes: resourceRecord.RootElement.TryGetProperty("outputComponentBytes", out var ob) ? ob.GetInt64() : 1_032_000_000,
            RetainedBudgetBytes: resourceRecord.RootElement.TryGetProperty("retainedBudgetBytes", out var rb) ? rb.GetInt64() : 32_000_000_000,
            FileLimit: resourceRecord.RootElement.TryGetProperty("fileLimit", out var fl) ? fl.GetInt32() : 4096,
            FreezeRetainedAllocations: resourceRecord.RootElement.TryGetProperty("freezeRetainedAllocations", out var fr) && fr.GetBoolean(),
            OfflineDeadlineSeconds: resourceRecord.RootElement.GetProperty("offlineDeadlineSeconds").GetInt32(),
            OutsideBuildAllowanceBytes: resourceRecord.RootElement.GetProperty("outsideBuildAllowanceBytes").GetInt64(),
            AdditionalHistoricalReserveBytes: resourceRecord.RootElement.TryGetProperty("additionalHistoricalReserveBytes", out var hr) ? hr.GetInt64() : 0,
            CopyFallbackReserveBytes: resourceRecord.RootElement.TryGetProperty("copyFallbackReserveBytes", out var cr) ? cr.GetInt64() : 0);
        plan = plan with { BudgetBytes = resourceRecord.RootElement.GetProperty("budgetBytes").GetInt64(),
            ReplayMinutes = replayMinutes,
            StopBytes = resourceRecord.RootElement.GetProperty("stopBytes").GetInt64(), DeadlineSeconds = resourceRecord.RootElement.GetProperty("deadlineSeconds").GetInt32(),
            FreshCandidateAdmission = resourceRecord.RootElement.TryGetProperty("freshCandidateAdmission", out var admissionKind) ? admissionKind.GetString() : null };
        if (plan.FreshCandidateAdmission != null)
            foreach (string name in new[] { "fresh-candidate-qualification.json", "outside-forward-envelope.json", "outside-outstanding-writes.json" })
            {
                string file = Path.Combine(admission, name);
                Require(File.Exists(file), "Fresh candidate preparation is missing a bound role: " + name);
                files.Add(file, HashFile(file));
            }
        if (ContextQualification(plan))
        {
            string file = Path.Combine(admission, FiveMinuteStorage(plan) ? FiveMinuteContractRole + ".json" : "coverage-freeze.json"); files.Add(file, HashFile(file));
            if (FiveMinuteStorage(plan))
            {
                string method = Path.Combine(admission, PresentationMethod.Role + ".json");
                files.TryAdd(method, HashFile(method));
            }
        }
        if(OwnedHistoryRepair(plan))
        {
            string file=Path.Combine(admission,"prior-failed-attempt.json");files.Add(file,HashFile(file));
            using var prior=ReadOwnedHistoryJson(file);
            foreach(string role in new[]{"planPath","controllerPath"})
            {string evidence=prior.RootElement.GetProperty(role).GetString();files.TryAdd(evidence,HashFile(evidence));}
        }
        if (durationContinuation)
        {
            string disposition = Path.Combine(admission, "duration-failure-disposition.json"); files.Add(disposition, HashFile(disposition));
            using var prior = ReadOwnedHistoryJson(disposition);
            foreach (string role in new[] { "oldPlan", "oldController", "nativeClosure", "reuseAudit", "reuseReview", "helperCompatibilityReview" })
            { string evidence = prior.RootElement.GetProperty(role).GetString(); files.TryAdd(evidence, HashFile(evidence)); }
        }
        Require(!compositeManualForegroundHandoff || composite, "Manual composite handoff requires the composite route.");
        Require(!compositeExplicitForegroundRelease || composite && compositeManualForegroundHandoff,
            "Explicit controller release requires the composite pre-capture handoff.");
        if (composite) plan = plan with { CompositeQualification = true, CompositeManualForegroundHandoff = compositeManualForegroundHandoff, ReplayExporterRecipe = ReplaySaveExport.PolicyIdentity,
            ValidationChildren = SameCaptureQualification(plan) ? 6 : ContextQualification(plan) ? 3 : compositeTrainingRoot == null ? 18 : capacityContinuation || durationContinuation ? 9 : 12, SourceComponentBytes = sourceBytes,
            CompositeTrainingRoot = compositeTrainingRoot == null ? null : Path.GetFullPath(compositeTrainingRoot),
            CompositeExplicitForegroundRelease = compositeExplicitForegroundRelease };
        plan = plan with { EvidenceRoles = PreparedEvidenceRoles(admission, files) };
        ValidateResources(plan);
        using var resources = JsonDocument.Parse(File.ReadAllText(Path.Combine(admission, "resources.json")));
        Require(resources.RootElement.GetProperty("budgetBytes").GetInt64() == plan.BudgetBytes
            && resources.RootElement.GetProperty("maximumChildren").GetInt32() == plan.MaximumChildren
            && resources.RootElement.GetProperty("deadlineSeconds").GetInt32() == plan.DeadlineSeconds, "Approved resource record differs from plan.");
        using var rehearsal = JsonDocument.Parse(File.ReadAllText(Path.Combine(admission, "rehearsal.json")));
        Require(rehearsal.RootElement.GetProperty("result").GetString() == (composite ? "passed-composite-storage-rehearsal" : "passed-full-nonmedia-rehearsal"),
            "A successful full-size rehearsal of this collector's storage shape is required.");
        var prepared = DateTime.UtcNow;
        Write(path, new { plan, environment, effects = Effects(plan), recipe = Recipe(plan), execution_requires_review = true,
            createdUtc = prepared, expiresUtc = prepared.AddHours(durationCohort ? 10 : 2) });
        Console.WriteLine(path); return 0;
    }
    static object Effects(CalibrationPlan plan) => new { realPrimaryMonitor = true, realDefaultSystemAudio = true, quietChirpPlayback = true,
        gpuNvenc = true, fullScreenControlledFixture = true, startupWrites = false, installedProfileWrites = false,
        vendorPreferencesWrites = false, displayChanges = false, sourceSeconds = AcquisitionSeconds(plan), replayMinutes = plan.ReplayMinutes,
        sources = plan.Order.Length, maximumChildren = plan.MaximumChildren,
        hardBytes = plan.BudgetBytes, minimumFreeBytes = plan.FloorBytes, deadlineSeconds = plan.DeadlineSeconds };
    internal static void ValidateResources(CalibrationPlan plan)
    {
        Require(plan != null && (plan.ReplayMinutes == 5 || DurationQualification(plan)), "Other durations require their distinct fresh reference admission.");
        Require(plan?.CompositeTrainingRoot == null || plan.CompositeQualification, "A paired training continuation requires its distinct composite admission.");
        Require(plan?.CompositeManualForegroundHandoff != true || plan.CompositeQualification,
            "Composite operator handoff cannot alter a historical or non-composite admission.");
        Require(plan?.CompositeExplicitForegroundRelease != true || plan.CompositeQualification && plan.CompositeManualForegroundHandoff,
            "Explicit controller release cannot bypass the composite foreground handoff.");
        if (plan?.CompositeQualification == true) { ValidateCompositeResources(plan); return; }
        Require(plan?.ProductionTrainingRoot == null || plan.ProductionQualification, "Retained production training requires its explicit qualification route.");
        if (plan?.MaximumChildren == 48 || plan?.ReplayExporterRecipe != null) { ValidateExact14(plan); return; }
        Require(plan?.OperatorForegroundHandoff != true && plan?.UserForegroundHandoff != true && plan?.AutomaticFixtureHandoff != true, "Explicit foreground handoff requires its exact continuation admission.");
        bool shutdownNoticeReplacement = plan != null && plan.MaximumChildren == 806 && plan.RetainedChildren == 774
            && plan.BudgetBytes == 80_000_000_000 && plan.StopBytes == 78_500_000_000
            && plan.DeadlineSeconds == 115000 && plan.DirectoryLimit == 768 && plan.FileLimit == 36864 && plan.OfflineDeadlineSeconds == 85000
            && plan.OutputComponentBytes == 2_000_000_000 && plan.RetainedBudgetBytes == 16_000_000_000
            && plan.OutsideBuildAllowanceBytes == 5_000_000_000 && plan.FreezeRetainedAllocations;
        bool reviewedCorrectionReplacement = shutdownNoticeReplacement || plan != null
            && (plan.MaximumChildren == 706 && plan.RetainedChildren == 674 || plan.MaximumChildren == 738 && plan.RetainedChildren == 706)
            && plan.BudgetBytes == 80_000_000_000 && plan.StopBytes == 78_500_000_000
            && plan.DeadlineSeconds == 100000 && plan.DirectoryLimit == 640 && plan.FileLimit == 36864 && plan.OfflineDeadlineSeconds == 74000
            && plan.OutputComponentBytes == 2_000_000_000 && plan.RetainedBudgetBytes == 16_000_000_000
            && plan.OutsideBuildAllowanceBytes == 3_200_000_000 && plan.FreezeRetainedAllocations;
        bool resumedHostReplacement = reviewedCorrectionReplacement || plan != null && plan.MaximumChildren == 654 && plan.RetainedChildren == 622
            && plan.BudgetBytes == 192_000_000_000 && plan.StopBytes == 190_500_000_000
            && plan.DeadlineSeconds == 86400 && plan.DirectoryLimit == 640 && plan.FileLimit == 36864 && plan.OfflineDeadlineSeconds == 61200
            && plan.OutputComponentBytes == 2_000_000_000 && plan.RetainedBudgetBytes == 160_000_000_000
            && plan.OutsideBuildAllowanceBytes == 2_200_000_000 && plan.FreezeRetainedAllocations;
        bool frozenHistoryReplacement = resumedHostReplacement || plan != null && plan.MaximumChildren == 577 && plan.RetainedChildren == 545
            && plan.BudgetBytes == 168_000_000_000 && plan.StopBytes == 166_500_000_000
            && plan.DeadlineSeconds == 72000 && plan.DirectoryLimit == 576 && plan.FileLimit == 32768 && plan.OfflineDeadlineSeconds == 50400
            && plan.OutputComponentBytes == 2_000_000_000 && plan.RetainedBudgetBytes == 144_000_000_000
            && plan.OutsideBuildAllowanceBytes == 2_200_000_000 && plan.FreezeRetainedAllocations;
        bool coordinatedReplacement = frozenHistoryReplacement || plan != null && plan.MaximumChildren == 545 && plan.RetainedChildren == 513
            && plan.BudgetBytes == 168_000_000_000 && plan.StopBytes == 166_500_000_000
            && plan.DeadlineSeconds == 72000 && plan.DirectoryLimit == 576 && plan.FileLimit == 32768 && plan.OfflineDeadlineSeconds == 50400
            && plan.OutputComponentBytes == 2_000_000_000 && plan.RetainedBudgetBytes == 144_000_000_000
            && plan.OutsideBuildAllowanceBytes == 2_200_000_000;
        bool snapshotReplacement = coordinatedReplacement || plan != null && plan.MaximumChildren == 513 && plan.RetainedChildren == 481
            && plan.BudgetBytes == 168_000_000_000 && plan.StopBytes == 166_500_000_000
            && plan.DeadlineSeconds == 64800 && plan.DirectoryLimit == 576 && plan.FileLimit == 32768 && plan.OfflineDeadlineSeconds == 46800
            && plan.OutputComponentBytes == 2_000_000_000 && plan.RetainedBudgetBytes == 144_000_000_000
            && plan.OutsideBuildAllowanceBytes == 2_200_000_000;
        bool cadenceReplacement = snapshotReplacement || plan != null && plan.MaximumChildren == 423 && plan.RetainedChildren == 391
            && plan.BudgetBytes == 144_000_000_000 && plan.StopBytes == 142_500_000_000
            && plan.DeadlineSeconds == 46800 && plan.DirectoryLimit == 448 && plan.FileLimit == 24576 && plan.OfflineDeadlineSeconds == 28800
            && plan.OutputComponentBytes == 2_000_000_000 && plan.RetainedBudgetBytes == 124_000_000_000
            && plan.OutsideBuildAllowanceBytes == 2_000_000_000;
        bool foregroundReplacement = cadenceReplacement || plan != null && plan.MaximumChildren == 374 && plan.RetainedChildren == 342
            && plan.BudgetBytes == 128_000_000_000 && plan.StopBytes == 126_500_000_000
            && plan.DeadlineSeconds == 39600 && plan.DirectoryLimit == 352 && plan.FileLimit == 21504 && plan.OfflineDeadlineSeconds == 25200
            && plan.OutputComponentBytes == 2_000_000_000 && plan.RetainedBudgetBytes == 106_000_000_000
            && plan.OutsideBuildAllowanceBytes == 2_000_000_000;
        bool lifecycleReplacement = foregroundReplacement || plan != null && plan.MaximumChildren == 342 && plan.RetainedChildren == 310
            && plan.BudgetBytes == 112_000_000_000 && plan.StopBytes == 110_500_000_000
            && plan.DeadlineSeconds == 36000 && plan.DirectoryLimit == 320 && plan.FileLimit == 20480 && plan.OfflineDeadlineSeconds == 21600
            && plan.OutputComponentBytes == 2_000_000_000 && plan.RetainedBudgetBytes == 92_000_000_000
            && plan.OutsideBuildAllowanceBytes == 2_000_000_000;
        bool evidenceReplacement = lifecycleReplacement || plan != null && plan.MaximumChildren == 288 && plan.BudgetBytes == 112_000_000_000 && plan.StopBytes == 110_500_000_000
            && plan.DeadlineSeconds == 28800 && plan.DirectoryLimit == 224 && plan.FileLimit == 16384 && plan.OfflineDeadlineSeconds == 16200
            && plan.OutputComponentBytes == 2_000_000_000 && plan.RetainedBudgetBytes == 72_000_000_000
            && plan.OutsideBuildAllowanceBytes == 1_400_000_000;
        bool exportReplacement = evidenceReplacement || plan != null && plan.MaximumChildren == 256 && plan.BudgetBytes == 96_000_000_000 && plan.StopBytes == 94_500_000_000
            && plan.DeadlineSeconds == 23400 && plan.DirectoryLimit == 192 && plan.FileLimit == 12288 && plan.OfflineDeadlineSeconds == 12600
            && plan.OutputComponentBytes == 2_000_000_000 && plan.RetainedBudgetBytes == 56_000_000_000
            && plan.OutsideBuildAllowanceBytes == 1_200_000_000;
        bool readinessReplacement = exportReplacement || plan != null && plan.MaximumChildren == 179 && plan.BudgetBytes == 84_000_000_000 && plan.StopBytes == 82_500_000_000
            && plan.DeadlineSeconds == 18000 && plan.DirectoryLimit == 160 && plan.FileLimit == 10240 && plan.OfflineDeadlineSeconds == 9000
            && plan.OutputComponentBytes == 2_000_000_000 && plan.RetainedBudgetBytes == 44_000_000_000
            && plan.OutsideBuildAllowanceBytes == 1_200_000_000;
        bool startupReplacement = readinessReplacement || plan != null && plan.MaximumChildren == 147 && plan.BudgetBytes == 84_000_000_000 && plan.StopBytes == 82_500_000_000
            && plan.DeadlineSeconds == 18000 && plan.DirectoryLimit == 160 && plan.FileLimit == 10240 && plan.OfflineDeadlineSeconds == 8400
            && plan.OutputComponentBytes == 2_000_000_000 && plan.RetainedBudgetBytes == 44_000_000_000
            && plan.OutsideBuildAllowanceBytes == 1_100_000_000;
        bool contentionReplacement = startupReplacement || plan != null && plan.MaximumChildren == 137 && plan.BudgetBytes == 84_000_000_000 && plan.StopBytes == 82_500_000_000
            && plan.DeadlineSeconds == 14400 && plan.DirectoryLimit == 160 && plan.FileLimit == 10240 && plan.OfflineDeadlineSeconds == 5400
            && plan.OutputComponentBytes == 2_000_000_000 && plan.RetainedBudgetBytes == 44_000_000_000
            && plan.OutsideBuildAllowanceBytes == 1_100_000_000;
        bool replacement = contentionReplacement || plan != null && plan.MaximumChildren == 115 && plan.BudgetBytes == 72_000_000_000 && plan.StopBytes == 70_500_000_000
            && plan.DeadlineSeconds == 11700 && plan.DirectoryLimit == 128 && plan.FileLimit == 8192 && plan.OfflineDeadlineSeconds == 4500
            && plan.OutputComponentBytes == 2_000_000_000 && plan.RetainedBudgetBytes == 34_000_000_000
            && plan.OutsideBuildAllowanceBytes == 1_100_000_000;
        bool fresh = replacement || plan != null && plan.MaximumChildren == 108 && plan.BudgetBytes == 72_000_000_000 && plan.StopBytes == 70_500_000_000
            && plan.DeadlineSeconds == 10800 && plan.DirectoryLimit == 128 && plan.FileLimit == 8192 && plan.OfflineDeadlineSeconds == 3600
            && plan.OutputComponentBytes == 2_000_000_000 && plan.RetainedBudgetBytes == 32_000_000_000;
        Require(plan != null && plan.Order != null && plan.Order.SequenceEqual(Order)
            && plan.FloorBytes == 60_000_000_000 && (replacement || plan.OutsideBuildAllowanceBytes == 1_000_000_000)
            && (fresh || plan.BudgetBytes == 64_000_000_000 && plan.StopBytes == 62_500_000_000 && plan.DeadlineSeconds == 9000
            && ((plan.MaximumChildren is 46 or 51 && plan.DirectoryLimit == 48 && plan.OutputComponentBytes == 1_032_000_000 && plan.RetainedBudgetBytes == 32_000_000_000
                    || plan.MaximumChildren == 57 && plan.DirectoryLimit == 56 && plan.OutputComponentBytes == 2_000_000_000 && plan.RetainedBudgetBytes == 24_000_000_000)
                    && plan.FileLimit == 4096 && plan.OfflineDeadlineSeconds == 1800
                || plan.MaximumChildren == 74 && plan.DirectoryLimit == 72 && plan.FileLimit == 5120 && plan.OfflineDeadlineSeconds == 2700
                    && plan.OutputComponentBytes == 2_000_000_000 && plan.RetainedBudgetBytes == 24_000_000_000)),
            "Calibration limits or frozen six-source order differ from an admitted historical or lifecycle replacement boundary.");
        Require(!plan.FreezeRetainedAllocations || frozenHistoryReplacement, "Read-only history requires its exact prospective admission.");
        Require(plan.RetainedRoots != null && plan.RetainedRoots.Length >= 4 && plan.RetainedRoots.Length <= (shutdownNoticeReplacement ? 51 : reviewedCorrectionReplacement ? plan.MaximumChildren == 738 ? 47 : 46 : resumedHostReplacement ? 43 : frozenHistoryReplacement ? 39 : coordinatedReplacement ? 38 : snapshotReplacement ? 37 : cadenceReplacement ? 28 : foregroundReplacement ? 25 : lifecycleReplacement ? 24 : evidenceReplacement ? 17 : exportReplacement ? 16 : readinessReplacement ? 15 : startupReplacement ? 14 : contentionReplacement ? 11 : fresh ? 10 : 8)
            && plan.RetainedRoots.Distinct(StringComparer.OrdinalIgnoreCase).Count() == plan.RetainedRoots.Length
            && plan.RetainedRoots.All(r => Directory.Exists(r) && !Path.GetFullPath(r).Equals(Path.GetFullPath(plan.Root), StringComparison.OrdinalIgnoreCase))
            && double.IsFinite(plan.RetainedElapsedSeconds) && plan.RetainedElapsedSeconds >= 0 && plan.RetainedElapsedSeconds < plan.DeadlineSeconds
            && plan.RetainedChildren >= 0 && (long)plan.RetainedChildren + 32 <= plan.MaximumChildren
            && double.IsFinite(plan.RehearsalElapsedSeconds) && plan.RehearsalElapsedSeconds is >= 0 and < 1800
            && plan.RetainedOfflineSeconds >= 0 && plan.RetainedOfflineSeconds <= plan.OfflineDeadlineSeconds && plan.RetainedOfflineSeconds <= plan.RetainedElapsedSeconds,
            "Retained evidence, children or cumulative phase time cannot fit the approved resource boundary.");
        if (plan.MaximumChildren == 74 || fresh)
            Require(plan.RetainedOfflineSeconds + CompleteOfflineBatchSeconds + (startupReplacement ? FullReplacementVerificationSeconds : contentionReplacement ? 210 : replacement ? ReplacementVerificationSeconds : 0) <= plan.OfflineDeadlineSeconds,
                "The fresh batch offline ceilings and replacement verification reserve cannot fit; do not start capture.");
        if (fresh) Require(plan.RetainedElapsedSeconds + (startupReplacement ? FullReplacementWallSeconds : 4080) <= plan.DeadlineSeconds,
            "Fresh calibration requires its full conservative phase/closeout envelope before capture.");
    }
    static object Recipe(CalibrationPlan plan) => new { version = plan.CompositeQualification ? "reference-composite-paired-v1" : "reference-exact-bytes-v2", baseline = "sum-selected-TS-EOF",
        compositePolicy = plan.CompositeQualification ? ReplaySaveExport.PolicyIdentity : null,
        compositeCapture = plan.CompositeQualification
            ? "all-six-primary-saves-while-capture-continues; actual-request-clock-interval; stop-after-primary-before-paired-exact-fallback"
            : null,
        compositeForegroundHandoff = plan.CompositeManualForegroundHandoff
            ? $"up-to180seconds-for-operator-click-on-owned-visible-fixture-before-capture; then-independent10second-foreground-proof; fixture{FixtureSeconds(plan)}seconds-including-handoff-and-startup; acquisition{AcquisitionSeconds(plan)}seconds-from-completed-handoff"
            : null,
        sourceBarcode = "seed256+floor(animationFrame/divisor); divisor=ceil((ceil(fixtureLifetimeSeconds*1000/16)+1)/25000); timer-enforces-finite-frame-and-lifetime; unchanged-seed-complement-static-motion-and-25000-ID-range",
        compositeControllerRelease = plan.CompositeExplicitForegroundRelease
            ? "capture-disarmed-until-all-UI-calls-return-and-controller-atomically-publishes-matching-nonce-PID-window-case; actual-foreground-still-required; same180second-handoff"
            : null,
        pairedOutputs = plan.CompositeQualification ? "six acquisitions; packet+exact-fallback from same protected selection; train union first3pairs; both heldout variants, no-refit" : null,
        residual = "max(0,max(training-finalEOF-inputEOF))", guard = "max(16MiB,ceil(base/20))",
        peakExtra = "max-training(sampled-peakAllocation-round(finalEOF))", rounding = $"requested-output{ReferenceSeconds(plan)}seconds; preceding-head-input-span-up-to{ReferenceSeconds(plan)+1.1}seconds",
        heldoutMustPass = "zero-final-or-observed-peak-exceedances; no-refit; no-universal-bound",
        inventorySampleIntervalMs = 100, maximumInventoryEvidenceGapMs = ReplayCapacityPolicy.CoverageMilliseconds,
        capacitySampleIntervalMs = ReplayCapacityPolicy.IntervalMilliseconds, maximumCapacityObservationGapMs = ReplayCapacityPolicy.CoverageMilliseconds,
        tracePersistence = "FIFO1024rows/8192bytes-per-row/64MiB-total; nonblocking enqueue; writer-only IO outside observation gate; overflow/error/incomplete3s drain invalidates qualification",
        observationContract = plan.ReadonlyValidationPhases ? "active-payload-readonly-validation-v2" : "active-offline-v1", expectedBytes = plan.StopBytes,
        fileLimit = plan.FileLimit, directoryLimit = plan.DirectoryLimit, offlineDeadlineSeconds = plan.OfflineDeadlineSeconds,
        completeOfflineBatchCeilingSeconds = plan.CompositeQualification ? 12 * MainInspectionLimit(plan) : plan.ProductionQualification ? plan.Order.Length * (MainInspectionSeconds + 30) : ExactFinalWorker14(plan) ? MainInspectionSeconds + 30 : ExactContinuation14(plan) ? 3 * MainInspectionSeconds + 3 * 30 : Exact14(plan)
            ? 6 * MainInspectionSeconds + 2 * ShortInspectionSeconds + (plan.RetainActiveOutputHandles ? 6 * 30 : 0) + (plan.ReadonlyValidationPhases ? 8 * 120 : 0)
            : CompleteOfflineBatchSeconds,
        readonlyValidation = plan.ReadonlyValidationPhases ? "120s per runtime decoder only after capture/audio absence and the same successful encoder's actual exit/absence; exact closed MP4/null-output recipe;130s combined save controller and product500ms guard unchanged; active before every encoder" : null,
        additionalReplacementVerificationSeconds = ExactContinuation14(plan) ? 0 : plan.MaximumChildren is 147 or 179 or 256 or 288 or 342 or 374 or 423 or 513 or 545 or 577 or 654 or 706 or 738 or 806 ? FullReplacementVerificationSeconds : plan.MaximumChildren == 137 ? 210 : plan.MaximumChildren == 115 ? ReplacementVerificationSeconds : 0,
        historicalAllocation = plan.FreezeRetainedAllocations ? "fresh native baseline; GENERIC_READ/share-read leases deny data writes and unlink through aliases; sticky synchronous subtree change handles; per-volume caller-free; full pre/post hashes" : "native allocation refreshed each scan",
        activeOutputMetadataHandles = plan.RetainActiveOutputHandles,
        outputAllocation = plan.RetainActiveOutputHandles ? "fresh native peak includes held allocation; final allocation after bounded producer-absent metadata release" : "short-lived metadata observations",
        phaseLimitsSeconds = new { captureSetup = AcquisitionSeconds(plan), exportController = ExportControllerLimit(plan), exportFailureCleanup = "close the owned capture job before joining accepted export/validator work; no long child timeout wait",
            foregroundAdmission = CalibrationForeground.DeadlineSeconds, mainInspection = MainInspectionLimit(plan), mainDecodeChild = MainInspectionLimit(plan) - InspectionCleanupSeconds,
            shortInspection = ShortInspectionSeconds, shortDecodeChild = ShortInspectionSeconds - InspectionCleanupSeconds,
            archive = ArchiveSeconds, engineControls = 40 },
        componentBytes = new { retainedCap = plan.RetainedBudgetBytes,
            additionalHistoricalReserve = plan.AdditionalHistoricalReserveBytes,
            sources = checked((ExactFinalWorker14(plan) ? 1 : ExactContinuation14(plan) ? 3 : 6) * plan.SourceComponentBytes),
            copyFallback = plan.CompositeQualification ? plan.CopyFallbackReserveBytes : Exact14(plan) ? 1_010_000_000L : 6_000_000_000L,
            outputs = checked((ExactFinalWorker14(plan) ? 1 : ExactContinuation14(plan) ? 3 : 6) * plan.OutputComponentBytes),
            snapshotArchives = Exact14(plan) || plan.CompositeQualification ? 0 : 6_000_000_000L,
            controls = 64_000_000L, builds = plan.OutsideBuildAllowanceBytes, evidence = 256_000_000L,
            boundedTrace = QualificationTraceBytes(plan), scratchAndClosure = QualificationScratchBytes(plan),
            newCohortFileForecast = QualificationFileForecast(plan), checkedEarlyCeiling = plan.StopBytes,
            accounting = "native allocation plus booked commitments; copied bytes may conservatively coexist with the fixed copy reservation" },
        captureArguments = ExpectedCaptureTemplate(plan, "<primary-index>"),
        exportArguments = ReplayExactExport.Arguments(new("<session>", "<manifest>", ReferenceSeconds(plan), 20, 60, "h264_nvenc", ".ts", true, true), new ReplayInterval(3.25, 3.25 + ReferenceSeconds(plan), ReferenceSeconds(plan)), 3, "<owned-concat>", "<owned-partial>"),
        validationArguments = ReplayExactExport.DecodeArguments("<owned-partial>", true, 60),
        analysisArguments = InspectionArguments("<evidence-root>", "<owned-final>", ReferenceSeconds(plan)),
        dynamicTokens = "task-owned paths/nonces, actual acquisition-clock epoch, observed source keyframe-rounded duration only; exact arrays persisted before each launch",
        analysis = "one bounded all-track full decode with audio astats per export; no packet predictor/index prerequisite" };

    internal static string[] CaptureTemplate(string primary) => ["-hide_banner", "-loglevel", "warning", "-y", "-stats_period", "0.25", "-progress", "pipe:1",
        "-copyts", "-thread_queue_size", "64", "-probesize", "32", "-analyzeduration", "0", "-ch_layout", "stereo", "-f", "matroska", "-i", "<audio-pipe>",
        "-thread_queue_size", "32", "-f", "lavfi", "-i", "ddagrab=framerate=240:output_idx=" + primary + ":draw_mouse=1,<clock>,fps=fps=60:round=near",
        "-map", "1:v:0", "-map", "0:a:0", "-af", AudioPacketClock.ResamplerFilter, "-ac", "2", "-ar", "48000", "-c:a", "aac", "-b:a", "192k",
        "-fps_mode", "passthrough", "-c:v", "h264_nvenc", "-preset", "p3", "-tune", "ll", "-rc", "cbr", "-b:v", "20M", "-maxrate", "20M", "-bufsize", "40M",
        "-g", "60", "-bf", "0", "-forced-idr", "1", "-force_key_frames", "expr:gte(t,n_forced*1)", "-flags", "+cgop", "-f", "segment", "-segment_time", "1",
        "-segment_format", "mpegts", "-reset_timestamps", "0", "-avoid_negative_ts", "disabled",
        "-segment_format_options", "mpegts_copyts=1:avoid_negative_ts=disabled", .. ReplayManifestStream.OutputArguments(), Path.Combine("<session>", "chunk-%09d.ts")];

    static string[] TimeoutControlArguments(string source) =>
        ["-hide_banner", "-nostdin", "-re", "-f", "lavfi", "-i", source, "-t", "10", "-f", "null", "-"];

    internal static int Controls(string root, string engineRoot)
    {
        Require(!Directory.Exists(root) && !File.Exists(root), "Controls need a fresh root.");
        Directory.CreateDirectory(root); MediaDependency.RootDirectory = engineRoot;
        string engine = MediaDependency.ResolveExecutable();
        var cases = new[]
        {
            (Name: "version", Args: new[] { "-version" }, Ms: 5000, Bytes: 2097152),
            (Name: "nonzero", Args: new[] { "-this-option-is-not-real" }, Ms: 5000, Bytes: 2097152),
            (Name: "timeout", Args: TimeoutControlArguments("sine"), Ms: 200, Bytes: 2097152),
            (Name: "output-limit", Args: new[] { "-version" }, Ms: 5000, Bytes: 128)
        };
        foreach (var item in cases)
        {
            AdmitNativeChild("engine-control:" + item.Name);
            var receipt = ReplayExportProcess.Run(engine, item.Args, root, null, timeoutMilliseconds: item.Ms, outputLimitBytes: item.Bytes);
            Write(Path.Combine(root, item.Name + ".json"), new { arguments = item.Args, receipt });
            Require(receipt.ProducerAbsent && receipt.ActualExitObserved && receipt.CapturedBytes <= item.Bytes, "Engine control cleanup/capture boundary failed.");
            Require(item.Name switch { "version" => receipt.Success, "nonzero" => receipt.ExitCode != 0 && !receipt.Success,
                "timeout" => receipt.TimedOut && !receipt.Success, _ => receipt.OutputLimitExceeded && !receipt.Success }, "Engine control did not demonstrate " + item.Name);
        }
        Console.WriteLine("Bounded engine controls passed: " + root); return 0;
    }

    internal static int SamplingWaitMilliseconds(double started, double now)
    {
        double elapsed = now - started;
        Require(double.IsFinite(elapsed) && elapsed >= 0, "Storage sampling clock moved backwards or is invalid.");
        return elapsed >= 100 ? 0 : (int)Math.Ceiling(100 - elapsed);
    }

    internal static double SamplingGap(double previous, double now)
    {
        double gap = now - previous;
        Require(double.IsFinite(gap) && gap >= 0, "Inventory measurement clock moved backwards or is invalid.");
        return gap;
    }

    internal static void ValidateMediaAttributes(string path, FileAttributes attributes)
    {
        if (path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            Require((attributes & (FileAttributes.Compressed | FileAttributes.SparseFile)) == 0, "Unsupported media allocation attributes.");
    }

    internal sealed class Sampler : IDisposable
    {
        sealed class TimedFiles : ReplayInventoryReader
        {
            readonly string atomicControlPrefix;
            readonly string atomicWorkerProfilePrefix;
            internal int WorkerControlObservations;
            internal TimedFiles(IEnumerable<string> roots, Func<string, bool> mayRetain, string currentRoot, bool reuseOutputs) : base(roots, mayRetain, reuseOutputs)
            {
                atomicControlPrefix = Path.GetFullPath(Path.Combine(currentRoot, "calibration-ownership")) + Path.DirectorySeparatorChar;
                atomicWorkerProfilePrefix = Path.GetFullPath(Path.Combine(currentRoot, "H03", "worker-profile")) + Path.DirectorySeparatorChar;
            }
            internal double LeafMilliseconds, MaximumLeafMilliseconds;
            internal string MaximumLeafPath;
            internal int LeafCount;
            internal void Reset()
            { BeginScan(); LeafMilliseconds = MaximumLeafMilliseconds = 0; MaximumLeafPath = null; LeafCount = 0; }
            internal override ReplayFileExtent ObserveInventory(string path, ReplayFileExtent enumerated)
            {
                long at = Stopwatch.GetTimestamp();
                try
                {
                    // Guard/ledger/pending files are atomically replaced under
                    // this task-private authority. Even a delete-sharing metadata
                    // handle can reject Windows replacement. Fresh anchored
                    // directory entries supply allocation/EOF/identity without
                    // opening those volatile leaves, just as for segment lists.
                    bool workerControl = Path.GetFullPath(path).StartsWith(atomicWorkerProfilePrefix, StringComparison.OrdinalIgnoreCase);
                    if (workerControl) Interlocked.Increment(ref WorkerControlObservations);
                    if (workerControl || Path.GetFullPath(path).StartsWith(atomicControlPrefix, StringComparison.OrdinalIgnoreCase)
                        || CalibrationFixturePublication.IsVolatile(path))
                        return ReplayStorage.Validate(enumerated);
                    return base.ObserveInventory(path, enumerated);
                }
                finally
                {
                    double elapsed = Stopwatch.GetElapsedTime(at).TotalMilliseconds;
                    LeafMilliseconds += elapsed;
                    if (elapsed > MaximumLeafMilliseconds) { MaximumLeafMilliseconds = elapsed; MaximumLeafPath = path; }
                    LeafCount++;
                }
            }
        }
        internal readonly object Gate = new();
        readonly string root;
        readonly string rootPrefix;
        readonly CalibrationPlan plan;
        readonly string[] retainedPrefixes;
        readonly string traceName;
        readonly long? currentBudgetBytes;
        internal long CurrentAllocatedBytes;
        readonly TimedFiles inventoryFiles;
        readonly CalibrationFileOperationSummary metadataOperations = new();
        readonly FrozenCalibrationHistory frozenHistory;
        readonly BoundedCalibrationTrace trace;
        readonly object writerReceiptGate = new();
        bool writerReceiptAttempted, writerReceiptDurable, traceClosedHealthy, disposalAttempted;
        Exception writerReceiptError, disposalError, closureError;
        static readonly List<Sampler> uncertainDisposals = new();
        readonly HashSet<string> closedCases = new(StringComparer.OrdinalIgnoreCase);
        readonly QualificationCommitments commitments;
        readonly Thread thread;
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly Dictionary<string, long> peaks = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, long> combinedPeaks = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, long> workspacePeaks = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, long> workspaceIdentityPeaks = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, long> workFilePeaks = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, (long Assembly, long First, long Second)> workFileIdentityPeaks = new(StringComparer.OrdinalIgnoreCase);
        readonly ManualResetEventSlim ready = new();
        volatile bool stop;
        string failure;
        internal string Failure => Volatile.Read(ref failure);
        readonly CalibrationObserverProbe probe;
        internal bool ObserverAbsent => thread?.IsAlive != true && capacityThread?.IsAlive != true && capacityQuery?.Running != true;
        internal bool StopRequested => stop;
        internal long Used;
        internal long PeakUsed;
        internal string SlowestLeafPath;
        internal double SlowestLeafMilliseconds;
        internal double MaximumGap;
        internal int Observations;
        internal int MetadataHandles;
        internal object LastObservation;
        readonly Dictionary<string, (int Count, double MaximumMilliseconds, string MaximumPath)> outputOperations = new();
        readonly Dictionary<string, ReplayFileExtent> outputExtents = new(StringComparer.OrdinalIgnoreCase);
        internal ReplayFileExtent OutputExtent(string path) { lock (Gate) return outputExtents.GetValueOrDefault(path); }
        internal object OutputOperationTimings { get { lock (Gate) return outputOperations.ToDictionary(p => p.Key,
            p => new { p.Value.Count, p.Value.MaximumMilliseconds, p.Value.MaximumPath }); } }
        internal object MetadataOperationTimings { get { lock (Gate) return new { failure = inventoryFiles.TimingFailure, operations = metadataOperations.Snapshot() }; } }
        double lastCompletion, lastObservationStarted, activeStarted, offlineDeadline, offlineStarted, offlineElapsed;
        readonly object capacityGate = new();
        ReplayCapacityObservation<ReplayVolumeCapacity> capacityQuery;
        Thread capacityThread;
        ReplayVolumeCapacity capacityEvidence;
        long capacityEvidenceAt;
        internal double MaximumCapacityGap, MaximumCapacityObservationMilliseconds;
        internal long CapacityObservations;
        double RetainedOfflineSeconds => plan.RetainedOfflineSeconds ?? plan.RetainedElapsedSeconds;
        double MaximumOfflineSeconds => plan.OfflineDeadlineSeconds;
        volatile bool activeCoverage;
        string phase;

        sealed class CaptureGrowth
        {
            internal readonly Dictionary<string, long> Previous = new(StringComparer.OrdinalIgnoreCase);
            internal readonly Queue<(double At, long Bytes)> Window = new();
            internal long WindowBytes, PeakWindowBytes, Total, StopAt = -1;
            internal string Volume;
        }
        readonly Dictionary<string, CaptureGrowth> captureGrowth = Order.ToDictionary(label => label, _ => new CaptureGrowth());

        internal double MaximumActiveGap, MaximumOfflineGap;
        internal bool ActiveCoverage { get { lock (Gate) return activeCoverage; } }
        internal int MaximumFiles, MaximumDirectories;
        internal double OfflineSeconds
        {
            get { lock (Gate) return RetainedOfflineSeconds + offlineElapsed
                + (offlineDeadline == 0 ? 0 : clock.Elapsed.TotalSeconds - offlineStarted); }
        }
        internal double ElapsedSeconds => clock.Elapsed.TotalSeconds;
        internal object TraceReceipt => trace?.Receipt;
        internal double TraceMaximumWriteMilliseconds => trace?.MaximumWriteMilliseconds ?? 0;
        internal Sampler(CalibrationPlan plan, bool active = true, string traceName = "storage.csv", long? currentBudgetBytes = null, Func<Stream> traceOpen = null,
            CalibrationObserverProbe probe = null)
        {
            Require(probe == null || plan.Environment == "nonmedia-observer-followup" && plan.EngineRoot == "unused-no-engine"
                && plan.Order.Length == 0 && plan.RetainedRoots.Length == 0, "Observer probe is restricted to the isolated nonmedia fixture.");
            this.probe = probe;
            plan = plan with { Root = Path.GetFullPath(plan.Root), RetainedRoots = plan.RetainedRoots.Select(Path.GetFullPath).ToArray(),
                MetadataHandleRoots = plan.MetadataHandleRoots?.Select(Path.GetFullPath).ToArray() };
            this.plan = plan; root = plan.Root; rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            commitments = new(plan.Order, plan.SourceComponentBytes, plan.OutputComponentBytes, plan.CopyFallbackReserveBytes,
                QualificationScratchBytes(plan),QualificationTraceBytes(plan),QualificationAliasNamespaceBytes(plan));
            Require(currentBudgetBytes == null || currentBudgetBytes > 0, "Current source byte ceiling must be positive.");
            this.currentBudgetBytes = currentBudgetBytes;
            retainedPrefixes = plan.RetainedRoots.Select(r => Path.GetFullPath(r).TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar).ToArray();
            Require((plan.MetadataHandleRoots ?? []).All(r => plan.RetainedRoots.Select(Path.GetFullPath)
                .Contains(Path.GetFullPath(r), StringComparer.OrdinalIgnoreCase)), "Reused metadata handles must belong to declared retained roots.");
            Require((plan.MetadataHandleRoots ?? []).All(r => !(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar)
                .StartsWith(Path.GetFullPath(r).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !(Path.GetFullPath(r).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar)
                .StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)),
                "Current mutable experiment data cannot use retained metadata handles.");
            Require(Regex.IsMatch(traceName, "^[a-z0-9-]{1,48}\\.csv$"), "Storage trace needs a bounded local filename.");
            this.traceName = traceName;
            Require(double.IsFinite(RetainedOfflineSeconds) && RetainedOfflineSeconds >= 0
                && RetainedOfflineSeconds <= plan.RetainedElapsedSeconds && RetainedOfflineSeconds <= MaximumOfflineSeconds,
                "Retained offline time is missing a valid cumulative bound.");
            activeCoverage = active; phase = active ? "active" : "boundary";
            inventoryFiles = new TimedFiles(plan.MetadataHandleRoots ?? [], CanRetainMetadata, root, plan.RetainActiveOutputHandles);
            if (probe?.DiagnosticsEnabled != false) inventoryFiles.OperationObserved = metadataOperations.Record;
            inventoryFiles.InventoryOpenedForChecks = probe?.Opened;
            inventoryFiles.OperationTimed = (path, operation, elapsed) => { lock (Gate) { var previous = outputOperations.GetValueOrDefault(operation);
                outputOperations[operation] = (previous.Count + 1, Math.Max(previous.MaximumMilliseconds, elapsed),
                    elapsed >= previous.MaximumMilliseconds ? path : previous.MaximumPath); } };
            inventoryFiles.OutputObserved = (path, extent) => outputExtents[path] = extent;
            if (plan.ReuseCurrentMetadataHandles) inventoryFiles.RetainHandlesUnder(root);
            Require(!plan.FreezeRetainedAllocations || !active, "Historical leases must be acquired outside active media coverage.");
            Require(plan.SelectedRetainedFiles == null || plan.FreshCandidateAdmission is RetainedCoverageAdmission or SameCaptureRetainedAdmission
                && plan.FreezeRetainedAllocations && !active, "Selected-only leases are restricted to the fixed retained pair.");
            try
            {
                frozenHistory = plan.FreezeRetainedAllocations ? new(plan.RetainedRoots, plan.FileLimit, plan.DirectoryLimit,
                    report => Write(Path.Combine(root, "history-admission-events.json"), report), plan.SelectedRetainedFiles) : null;
                if (frozenHistory != null)
                    Write(Path.Combine(root, "frozen-history.json"), new { frozenHistory.FileCount, frozenHistory.DirectoryCount,
                        frozenHistory.AllocatedBytes, frozenHistory.Volumes, readOnlyDataLeases = true, synchronousStickyChanges = true,
                        metadataHash = ReplayReferenceModel.Hash(frozenHistory.Inventory), mutation = "abort without rearming or rewriting evidence" });
                trace = new BoundedCalibrationTrace(Path.Combine(root, traceName), traceOpen, maximumBytes:QualificationTraceBytes(plan));
                capacityEvidence = ReplayStorage.ObserveCapacity(root);
                capacityEvidenceAt = Stopwatch.GetTimestamp();
                capacityQuery = new(() => ReplayStorage.ObserveCapacity(root));
                capacityThread = new Thread(() =>
                {
                    try
                    {
                        while (!stop) { CheckCapacity(0); Thread.Sleep(50); }
                    }
                    catch (Exception error) { Interlocked.CompareExchange(ref failure, error.Message, null); stop = true; ready.Set(); }
                }) { IsBackground = true, Name = "bounded-calibration-capacity" };
                capacityThread.Start();
                thread = new Thread(Loop) { IsBackground = true, Name = "bounded-calibration-storage" };
                thread.Start();
                Require(ready.Wait(5000), "Initial storage observation timed out.");
                Check();
                if (CapacityQualification(plan))
                {
                    lock (Gate)
                    {
                        Write(Path.Combine(root, "file-count-admission.json"), new { baselineFiles = MaximumFiles,
                            newCohortFiles = QualificationFileForecast(plan), maximumFiles = plan.FileLimit,
                            projectedFiles = (long)MaximumFiles + QualificationFileForecast(plan),
                            accepted = CapacityFileForecastFits(MaximumFiles, plan.FileLimit,QualificationFileForecast(plan)), captureStarted = false,
                            scope = "Fresh complete inventory plus whole fixed cohort file reserve, before fixture/capture. No existing file/alias credit; runtime file/handle caps remain independent." });
                        Require(CapacityFileForecastFits(MaximumFiles, plan.FileLimit,QualificationFileForecast(plan)),
                            "Retained inventory plus the complete cohort file reserve exceeds the file limit; no fixture/capture may start.");
                    }
                }
            }
            catch
            {
                stop = true;
                if (capacityThread?.IsAlive == true) Require(capacityThread.Join(1000), "Failed capacity supervisor constructor did not stop.");
                if (thread?.IsAlive == true) Require(thread.Join(5000), "Failed observer constructor did not stop.");
                try { trace?.Dispose(); }
                finally { frozenHistory?.Dispose(); inventoryFiles.Dispose(); ready.Dispose(); }
                throw;
            }
        }
        void Loop()
        {
            try
            {
                using var files = inventoryFiles;
                trace.Append("elapsed_ms,observation_ms,start_gap_ms,completion_gap_ms,allocated_bytes,caller_free_bytes,files,lock_ms,inventory_ms,leaf_ms,leaf_count,max_leaf_ms,phase,max_leaf_path,native_opens,native_handles,accounting_ms,capacity_ms");
                double previous = 0, previousCompletion = 0;
                TimeSpan previousGCPause = GC.GetTotalPauseDuration();
                while (!stop)
                {
                    double at = clock.Elapsed.TotalMilliseconds, gap = at - previous; previous = at;
                    long allocatedBefore = probe == null ? 0 : GC.GetAllocatedBytesForCurrentThread();
                    MaximumGap = Math.Max(MaximumGap, gap);
                    Require(double.IsFinite(gap) && gap >= 0, "Storage clock moved backwards or is invalid.");
                    lock (Gate)
                    {
                        double entered = clock.Elapsed.TotalMilliseconds;
                        Require(offlineDeadline == 0 || clock.Elapsed.TotalSeconds <= offlineDeadline, "Offline storage phase deadline exceeded.");
                        files.Reset();
                        frozenHistory?.Check();
                        // Inspect fresh attributes from the same anchored native
                        // enumeration as identity/EOF/allocation, avoiding a second
                        // path-based query for every media entry on every scan.
                        var inventory = ReplayStorage.Inventory(frozenHistory == null ? [root, .. plan.RetainedRoots] : [root], files,
                            maximumFiles: plan.FileLimit - (frozenHistory?.FileCount ?? 0),
                            maximumDirectories: plan.DirectoryLimit - (frozenHistory?.DirectoryCount ?? 0), inspectAttributes: ValidateMediaAttributes);
                        files.EndScan(); // Release absent names before allocation/capacity accounting.
                        Require(files.TimingFailure == null, "Metadata timing failed: " + files.TimingFailure);
                        Volatile.Write(ref MetadataHandles, files.HandleCount + (frozenHistory?.FileCount ?? 0));
                        double inventoried = clock.Elapsed.TotalMilliseconds;
                        if (files.MaximumLeafMilliseconds > SlowestLeafMilliseconds)
                        { SlowestLeafMilliseconds = files.MaximumLeafMilliseconds; SlowestLeafPath = files.MaximumLeafPath; }
                        int fileCount = checked(inventory.Count + (frozenHistory?.FileCount ?? 0));
                        Require(fileCount <= plan.FileLimit, "Calibration inventory exceeded the approved file limit.");
                        MaximumFiles = Math.Max(MaximumFiles, fileCount);
                        MaximumDirectories = Math.Max(MaximumDirectories, frozenHistory == null
                            ? new[] { root }.Concat(plan.RetainedRoots).Sum(r => 1 + Directory.GetDirectories(r, "*", SearchOption.AllDirectories).Length)
                            : frozenHistory.DirectoryCount + 1 + Directory.GetDirectories(root, "*", SearchOption.AllDirectories).Length);
                        Used = checked((frozenHistory?.BytesIncluding(inventory) ?? ReplayStorage.Bytes(inventory)) + plan.OutsideBuildAllowanceBytes
                            + plan.AdditionalHistoricalReserveBytes + plan.CopyFallbackReserveBytes);
                        var accounting = new CalibrationInventoryAccounting(inventory, root, Order,
                            frozenHistory == null ? retainedPrefixes : [], currentBudgetBytes.HasValue);
                        if (currentBudgetBytes.HasValue)
                        {
                            CurrentAllocatedBytes = ReplayStorage.Bytes(accounting.Current);
                            Require(CurrentAllocatedBytes < currentBudgetBytes.Value, "Startup diagnostic source byte stop reached.");
                        }
                        Require((frozenHistory?.AllocatedBytes ?? ReplayStorage.Bytes(accounting.Retained)) <= plan.RetainedBudgetBytes, "Retained evidence envelope exceeded.");
                        PeakUsed = Math.Max(PeakUsed, Used);
                        foreach (string label in Order)
                        {
                            var cell = accounting.Cells[label];
                            Require(ReplayStorage.Bytes(cell.Sources) <= plan.SourceComponentBytes,
                                "Source component exceeded its admitted byte limit.");
                            Require(ReplayStorage.Bytes(cell.OutputsAndWork) <= plan.OutputComponentBytes, "Output/scratch component exceeded limit.");
                        }
                        double accounted = clock.Elapsed.TotalMilliseconds;
                        var capacity = ReplayStorage.ObserveCapacity(root);
                        if (frozenHistory == null)
                            foreach (string retained in plan.RetainedRoots)
                                Require(ReplayStorage.ObserveCapacity(retained).AvailableBytes >= plan.FloorBytes + 1_256_000_000L,
                                    "A retained evidence volume no longer has its caller-free reserve.");
                        else
                            foreach (var volume in frozenHistory.Volumes)
                            {
                                var observedCapacity = volume.Identity.Equals(capacity.Volume, StringComparison.OrdinalIgnoreCase)
                                    ? capacity : ReplayStorage.ObserveCapacity(volume.Directory);
                                Require(observedCapacity.Volume.Equals(volume.Identity, StringComparison.OrdinalIgnoreCase)
                                    && observedCapacity.AllocationUnit == volume.AllocationUnit
                                    && observedCapacity.AvailableBytes >= plan.FloorBytes + 1_256_000_000L,
                                    "A frozen historical volume changed or lost its caller-free reserve.");
                            }
                        Require(Used < plan.StopBytes && capacity.AvailableBytes >= plan.FloorBytes + 1_256_000_000L,
                            "Calibration byte stop or free-space reserve breached.");
                        Require(clock.Elapsed.TotalSeconds + plan.RetainedElapsedSeconds < plan.DeadlineSeconds, "Calibration cumulative wall deadline exceeded.");
                        var workspaceCurrent = accounting.Workspaces.ToDictionary(i => i.Key,
                            i => ReplayStorage.Bytes(i.Value), StringComparer.OrdinalIgnoreCase);
                        foreach (var work in workspaceCurrent) workspacePeaks[work.Key] = Math.Max(workspacePeaks.GetValueOrDefault(work.Key), work.Value);
                        foreach (var file in accounting.Workspaces.Values.SelectMany(i => i))
                        {
                            workFilePeaks[file.Key] = Math.Max(workFilePeaks.GetValueOrDefault(file.Key), file.Value.AllocatedBytes);
                        }
                        foreach (var item in accounting.Outputs)
                            RecordOutput(item.Key, item.Value, workspaceCurrent.GetValueOrDefault(item.Key));
                        double observed = clock.Elapsed.TotalMilliseconds;
                        if (activeCoverage) ObserveCaptureGrowth(accounting, at, observed);
                        frozenHistory?.Check();
                        long traceStarted = Stopwatch.GetTimestamp();
                        trace.Append(FormattableString.Invariant($"{at},{observed - at},{gap},{observed - previousCompletion},{Used},{capacity.AvailableBytes},{fileCount},{entered - at},{inventoried - entered},{files.LeafMilliseconds},{files.LeafCount},{files.MaximumLeafMilliseconds},{phase},\"{files.MaximumLeafPath?.Replace("\"", "\"\"")}\",{files.OpenCount},{MetadataHandles},{accounted - inventoried},{observed - accounted}"));
                        long traceCompleted = Stopwatch.GetTimestamp();
                        double traceEndedAt = clock.Elapsed.TotalMilliseconds;
                        var gc = GC.GetGCMemoryInfo();
                        var totalGCPause = GC.GetTotalPauseDuration();
                        var latestGCPauses = gc.PauseDurations.ToArray().Select(duration => duration.TotalMilliseconds).ToArray();
                        double completed = clock.Elapsed.TotalMilliseconds;
                        LastObservation = new { at, completed, startGap = gap, completionGap = completed - previousCompletion,
                            work = completed - at, gateWait = entered - at, inventory = inventoried - entered,
                            files.MaximumLeafMilliseconds, files.MaximumLeafPath, files.LeafMilliseconds,
                            traceEndedAt, traceMilliseconds = Stopwatch.GetElapsedTime(traceStarted, traceCompleted).TotalMilliseconds,
                            gcTelemetryMilliseconds = completed - traceEndedAt,
                            gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2), gc.Index,
                            gc.HeapSizeBytes, gc.TotalCommittedBytes, gc.PauseTimePercentage,
                            totalGCPauseMilliseconds = totalGCPause.TotalMilliseconds,
                            gcPauseSincePreviousObservationMilliseconds = (totalGCPause - previousGCPause).TotalMilliseconds,
                            lastGCPausesMs = latestGCPauses, phase };
                        previousGCPause = totalGCPause;
                        MaximumGap = Math.Max(MaximumGap, Math.Max(completed - at, completed - previousCompletion));
                        if (activeCoverage)
                        {
                            double work = completed - Math.Max(at, activeStarted), coverage = completed - Math.Max(previousCompletion, activeStarted);
                            if (double.IsFinite(work) && double.IsFinite(coverage) && work >= 0 && coverage >= 0)
                                MaximumActiveGap = Math.Max(MaximumActiveGap, Math.Max(work, coverage));
                            SamplingGap(Math.Max(at, activeStarted), completed);
                            SamplingGap(Math.Max(previousCompletion, activeStarted), completed);
                        }
                        else MaximumOfflineGap = Math.Max(MaximumOfflineGap, completed - previousCompletion);
                        previousCompletion = completed;
                        Volatile.Write(ref lastCompletion, completed);
                        Volatile.Write(ref lastObservationStarted, at);
                        Interlocked.Increment(ref Observations);
                        ready.Set();
                        probe?.Record(completed - at, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
                    }
                    Thread.Sleep(SamplingWaitMilliseconds(at, clock.Elapsed.TotalMilliseconds));
                }
            }
            catch (Exception ex) { Interlocked.CompareExchange(ref failure, ex.Message, null); ready.Set(); }
        }

        internal void SealClosedCase(string label)
        {
            lock (Gate)
            {
                Require(!activeCoverage && offlineDeadline == 0 && Order.Contains(label), "Only a closed case between producer/offline phases may retain metadata handles.");
                string cell = Path.Combine(root, label);
                Require(Directory.Exists(cell), "Closed metadata case is absent.");
                closedCases.Add(cell + Path.DirectorySeparatorChar);
                // Caller has confirmed source/export/inspection absence and
                // retired snapshots. No further mutations occur in this case
                // before this sampler is disposed. Allocation is still queried
                // natively on every scan, never inferred from closure.
                inventoryFiles.RetainHandlesUnder(cell);
                lock (capacityGate) commitments.Seal(label);
            }
        }
        bool CanRetainMetadata(string path)
        {
            if (!path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
                || closedCases.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase))) return true;
            // The worker retires the staging directory and its hard-linked
            // snapshot as soon as the save closes. A retained reader can keep
            // a delete-pending entry alive through the next storage scan.
            if (path.Contains(".partial.mp4.work" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(Path.GetDirectoryName(path)).StartsWith("save-", StringComparison.OrdinalIgnoreCase))
                return false;
            // Holding a handle through producer close can delay filesystem
            // allocation trimming. Current captured chunks and MP4s therefore
            // use short-lived observations until the case is fully closed.
            return !Path.GetFileName(path).StartsWith("chunk-", StringComparison.OrdinalIgnoreCase)
                && (plan.RetainActiveOutputHandles || !path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase));
        }
        internal bool RetainsMetadata(string path) { lock (Gate) return inventoryFiles.Retains(path); }
        internal int WorkerControlObservations => Volatile.Read(ref inventoryFiles.WorkerControlObservations);
        internal sealed record OutputRelease(string Path, ReplayFileExtent Before, ReplayFileExtent After, double Milliseconds, bool MetadataHandleAbsent);
        internal OutputRelease ReleaseClosedOutputHandles(string final) => ReleaseClosedOutputHandles(new[] { final }).Single();
        internal OutputRelease[] ReleaseClosedOutputHandles(IReadOnlyList<string> finals)
        {
            lock (Gate)
            {
                Require(!activeCoverage, "Output handles release only after terminal producer-absent coverage.");
                var before = finals.ToDictionary(path => path, ReplayStorage.Observe, StringComparer.OrdinalIgnoreCase);
                var timer = Stopwatch.StartNew();
                inventoryFiles.ReleaseOutputHandles();
                var after = finals.ToDictionary(path => path, ReplayStorage.Observe, StringComparer.OrdinalIgnoreCase);
                Require(finals.All(path => !inventoryFiles.Retains(path)), "Metadata handle reopened before the release observation.");
                return finals.Select(path => new OutputRelease(path, before[path], after[path], timer.Elapsed.TotalMilliseconds, true)).ToArray();
            }
        }
        internal void ReleaseInventoryHandles()
        {
            Require(Monitor.IsEntered(Gate), "Native handle release must share the inventory mutation gate.");
            inventoryFiles.ReleaseHandles(); Volatile.Write(ref MetadataHandles, 0);
        }

        readonly FiveMinuteGrowthJournal fiveMinuteJournal = new();
        internal void WriteFiveMinuteGrowth(string cell) => fiveMinuteJournal.WriteTo(cell, captureGrowth["H03"].StopAt);

        void ObserveCaptureGrowth(CalibrationInventoryAccounting accounting, double scanStart, double at)
        {
            foreach (var label in Order)
            {
                var state = captureGrowth[label];
                var current = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                foreach (var member in accounting.Cells[label].Capture)
                {
                    string volume = member.Value.Volume, identity = volume + ":" + member.Value.Identity;
                    Require(state.Volume == null || state.Volume == volume, "Capture allocation volume changed.");
                    state.Volume = volume;
                    current[identity] = Math.Max(current.GetValueOrDefault(identity), member.Value.AllocatedBytes);
                }
                long growth = 0;
                foreach (var member in current)
                    growth = checked(growth + Math.Max(0, member.Value - state.Previous.GetValueOrDefault(member.Key)));
                bool journal = label == "H03" && FiveMinuteStorage(plan);
                double earliest = journal ? fiveMinuteJournal.Observe(scanStart, at, state.Previous, current) : at;
                state.Previous.Clear(); foreach (var member in current) state.Previous.Add(member.Key, member.Value);
                Interlocked.Exchange(ref state.Total, checked(Interlocked.Read(ref state.Total) + growth));
                if (journal) fiveMinuteJournal.Publish(state.Total);
                state.Window.Enqueue((at, growth)); state.WindowBytes = checked(state.WindowBytes + growth);
                while (state.Window.Count > 1 && state.Window.Peek().At < earliest - ReplayCapacityPolicy.CoverageMilliseconds)
                    state.WindowBytes -= state.Window.Dequeue().Bytes;
                state.PeakWindowBytes = Math.Max(state.PeakWindowBytes, state.WindowBytes);
                if (journal && (state.PeakWindowBytes > FiveMinutePollLimit || state.StopAt >= 0 && state.Total - state.StopAt > FiveMinuteStopLimit))
                    throw new IOException($"Five-minute growth exceeded at {at:F3} ms: rolling peak {state.PeakWindowBytes} B, cumulative {state.Total} B, stop prefix {state.StopAt} B.");
            }
        }
        internal void MarkCaptureStop(string label)
        {
            // Never delay owned cleanup behind observer I/O. The latest completed
            // allocation prefix is deliberately conservative for the drain interval.
            var state = captureGrowth[label];
            long prefix = FiveMinuteStorage(plan) && label == "H03"
                ? fiveMinuteJournal.MarkStop(clock.Elapsed.TotalMilliseconds) : Interlocked.Read(ref state.Total);
            Interlocked.CompareExchange(ref state.StopAt, prefix, -1);
        }
        // A request is not absence or observer closure. In particular, never
        // acquire Gate here: a native inventory call may hold it indefinitely.
        internal void RequestCaptureStop(string label, Action requestStop)
        {
            stop = true;
            MarkCaptureStop(label);
            requestStop();
        }
        internal void RequireFailureClosure(bool producerAbsent)
        {
            Require(!StopRequested && Failure == null || producerAbsent && ObserverAbsent && Failure == null && writerReceiptDurable && traceClosedHealthy && closureError == null,
                "Storage observer/producer closure remains uncertain; retain ownership and reservations.");
        }
        internal (long PollGrowth, long StopGrowth, string Volume) CaptureMetrics(string label)
        {
            lock (Gate)
            {
                var state = captureGrowth[label];
                Require(!activeCoverage && state.StopAt >= 0 && state.Volume != null, "Capture growth evidence is incomplete.");
                Require(MaximumActiveGap <= ReplayCapacityPolicy.CoverageMilliseconds,
                    "Inventory evidence has gaps outside the measured growth horizon; qualification remains inconclusive.");
                return (state.PeakWindowBytes, checked(state.Total - state.StopAt), state.Volume);
            }
        }

        void FreshObservation()
        {
            double requested = clock.Elapsed.TotalMilliseconds;
            var wait = Stopwatch.StartNew();
            while (Volatile.Read(ref lastObservationStarted) < requested)
            {
                Check();
                Require(wait.ElapsedMilliseconds < 5000, "Terminal storage observation did not complete.");
                Thread.Sleep(5);
            }
        }
        internal void BeginActive()
        {
            FreshObservation();
            lock (Gate)
            {
                Check();
                Require(!activeCoverage && offlineDeadline == 0, "Previous calibration phase has not closed.");
                activeStarted = clock.Elapsed.TotalMilliseconds;
                activeCoverage = true; phase = "active";
            }
        }
        internal void EndActive()
        {
            // Caller confirmed all producers absent. Require a scan that began later.
            FreshObservation();
            lock (Gate)
            {
                Check(); Require(activeCoverage, "There is no active measurement to close.");
                double gap = SamplingGap(Math.Max(lastCompletion, activeStarted), clock.Elapsed.TotalMilliseconds);
                MaximumActiveGap = Math.Max(MaximumActiveGap, gap);
                activeCoverage = false; phase = "boundary";
            }
        }
        internal void RequireFreshActiveObservation()
        {
            lock (Gate) Require(activeCoverage, "Output-owner release must remain inside active coverage.");
            FreshObservation();
        }
        internal void BeginOffline(string name, long allocationBytes, int deadlineSeconds)
        {
            Require(Regex.IsMatch(name, "^[a-z0-9-]{1,32}$") && allocationBytes >= 0 && deadlineSeconds > 0
                && deadlineSeconds <= (DurationQualification(plan) ? 1200 : 300),
                "Offline calibration phase is not bounded.");
            FreshObservation();
            RequireTime(deadlineSeconds);
            lock (Gate)
            {
                Require(!activeCoverage && offlineDeadline == 0, "Media coverage or another offline phase is still open.");
                Require(checked(Used + allocationBytes + QualificationScratchBytes(plan)) <= plan.BudgetBytes,
                    "Proposed offline stage cannot fit existing bytes plus closure reserves; no stage was accepted.");
                Check(allocationBytes);
                Require(RetainedOfflineSeconds + offlineElapsed + deadlineSeconds <= MaximumOfflineSeconds,
                    "The complete offline phase deadline cannot fit the cumulative analysis allowance.");
                offlineStarted = clock.Elapsed.TotalSeconds;
                offlineDeadline = offlineStarted + deadlineSeconds; phase = name;
            }
        }
        internal void EndOffline()
        {
            FreshObservation();
            lock (Gate)
            {
                Require(!activeCoverage && offlineDeadline != 0, "No offline phase is admitted.");
                Check(); offlineElapsed += clock.Elapsed.TotalSeconds - offlineStarted;
                offlineDeadline = 0; phase = "boundary";
            }
        }
        internal void Check(long next = 0)
        {
            try
            {
            // Controller-side liveness must not wait for Loop/Gate to return.
            // A late call still fails; no timestamp is renewed on this path.
            if (activeCoverage)
            {
                double previous = Math.Max(Volatile.Read(ref lastCompletion), Volatile.Read(ref activeStarted));
                SamplingGap(previous, clock.Elapsed.TotalMilliseconds);
            }
            Require(inventoryFiles.TimingFailure == null, "Metadata timing failed: " + inventoryFiles.TimingFailure);
            Require(trace?.Failure == null, trace?.Failure);
            Require(Failure == null, Failure);
            frozenHistory?.Check();
            Require(offlineDeadline == 0 || clock.Elapsed.TotalSeconds <= offlineDeadline, "Offline storage phase deadline exceeded.");
            Require(RetainedOfflineSeconds + offlineElapsed + (offlineDeadline == 0 ? 0 : clock.Elapsed.TotalSeconds - offlineStarted) <= MaximumOfflineSeconds,
                "Cumulative offline analysis exceeded its approved allowance.");
            Require(Used + next + 1_256_000_000L <= plan.BudgetBytes, "Next stage cannot fit retained bytes plus closure/poll reserves.");
            CheckCapacity(next);
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref failure, ex.Message, null);
                stop = true;
                throw;
            }
        }
        void CheckCapacity(long next)
        {
            lock (capacityGate)
            {
                Require(Stopwatch.GetElapsedTime(capacityEvidenceAt).TotalMilliseconds <= ReplayCapacityPolicy.CoverageMilliseconds,
                    "Qualification capacity monitoring exhausted conservative cover.");
                if (capacityQuery.TryComplete(out var completed))
                {
                    Require(completed.Value.Volume == capacityEvidence.Volume && completed.Value.AllocationUnit == capacityEvidence.AllocationUnit,
                        "Qualification capacity volume or allocation unit changed.");
                    Require(Stopwatch.GetElapsedTime(completed.Started, completed.Finished).TotalMilliseconds <= ReplayCapacityPolicy.CoverageMilliseconds,
                        "Qualification capacity query exceeded its bounded cover.");
                    MaximumCapacityGap = Math.Max(MaximumCapacityGap, Stopwatch.GetElapsedTime(capacityEvidenceAt, completed.Finished).TotalMilliseconds);
                    MaximumCapacityObservationMilliseconds = Math.Max(MaximumCapacityObservationMilliseconds,
                        Stopwatch.GetElapsedTime(completed.Started, completed.Finished).TotalMilliseconds);
                    CapacityObservations++;
                    capacityEvidence = completed.Value; capacityEvidenceAt = completed.Started;
                }
                // Only confirmed closed cases retire their future commitment.
                // Their actual native allocation remains charged in Used.
                Require(capacityEvidence.AvailableBytes >= commitments.RuntimeRequiredFreeBytes(QualificationPhysicalFloor(plan), next),
                    "Caller-free cannot cover remaining finite cohort commitments and physical floor.");
                if (!stop && Stopwatch.GetElapsedTime(capacityEvidenceAt).TotalMilliseconds >= ReplayCapacityPolicy.IntervalMilliseconds)
                    capacityQuery.Start();
            }
        }
        internal void RequireTime(int seconds)
        {
            Check();
            Require(seconds > 0 && clock.Elapsed.TotalSeconds + plan.RetainedElapsedSeconds + seconds <= plan.DeadlineSeconds,
                "The full stage deadline cannot fit the remaining cumulative experiment wall time.");
        }
        internal long Peak(string output)
        {
            lock (Gate)
            {
                var extent = ReplayStorage.Observe(output);
                return Math.Max(extent.AllocatedBytes, peaks.GetValueOrDefault(extent.Identity));
            }
        }
        void RecordOutput(string path, ReplayFileExtent extent, long work)
        {
            peaks[extent.Identity] = Math.Max(peaks.GetValueOrDefault(extent.Identity), extent.AllocatedBytes);
            combinedPeaks[extent.Identity] = Math.Max(combinedPeaks.GetValueOrDefault(extent.Identity),
                Math.Max(checked(extent.AllocatedBytes + work), workspacePeaks.GetValueOrDefault(path)));
            workspaceIdentityPeaks[extent.Identity] = Math.Max(workspaceIdentityPeaks.GetValueOrDefault(extent.Identity), workspacePeaks.GetValueOrDefault(path));
            var prior = workFileIdentityPeaks.GetValueOrDefault(extent.Identity);
            workFileIdentityPeaks[extent.Identity] = (Math.Max(prior.Assembly, workFilePeaks.GetValueOrDefault(ReplayOutputWorkspace.AssemblyPath(path))),
                Math.Max(prior.First, workFilePeaks.GetValueOrDefault(ReplayOutputWorkspace.BoundaryPath(path, 0))),
                Math.Max(prior.Second, workFilePeaks.GetValueOrDefault(ReplayOutputWorkspace.BoundaryPath(path, 1))));
        }
        internal void BeforePublication(string partial, ReplayFileExtent expected = null)
        {
            // The output producer has joined. These observations do not
            // authorize publication or replace normal validation/ownership.
            // Native observations can be slow on close; they
            // must not prevent the independent sampler from making progress.
            // Only merge the peaks under its gate. RetainFiles still serializes
            // the actual rename with namespace enumeration below.
            CalibrationOutputObservation.Run(partial, Gate, (work, extent, bytes) => {
                foreach (var file in work) workFilePeaks[file.Key] = Math.Max(workFilePeaks.GetValueOrDefault(file.Key), file.Value.AllocatedBytes);
                workspacePeaks[partial] = Math.Max(workspacePeaks.GetValueOrDefault(partial), bytes);
                RecordOutput(partial, extent, bytes);
            }, expected);
        }
        internal (long Combined, long Work, long Assembly, long[] Boundaries) OutputAndWorkspacePeak(string output)
        {
            lock (Gate)
            {
                var extent = ReplayStorage.Observe(output);
                var files = workFileIdentityPeaks.GetValueOrDefault(extent.Identity);
                return (Math.Max(extent.AllocatedBytes, combinedPeaks.GetValueOrDefault(extent.Identity)),
                    workspaceIdentityPeaks.GetValueOrDefault(extent.Identity), files.Assembly, [files.First, files.Second]);
            }
        }
        // Failure cleanup cannot take Gate: the observer may still own it while
        // finishing native I/O. Actual joins and writer persistence are required
        // before a stopped capture's durable claims may be released.
        internal void JoinStoppedObservers(int observerJoinMilliseconds = 5000)
        {
            if (closureError != null) throw new IOException("Earlier observer closure failed.", closureError);
            try
            {
                Require(observerJoinMilliseconds is > 0 and <= 5000, "Observer join budget is outside its finite bound.");
                stop = true;
                Require(thread.Join(observerJoinMilliseconds), "Storage observer did not stop.");
                Require(capacityThread.Join(1000), "Capacity supervisor did not stop.");
                Require(capacityQuery.WaitForClosure(1000), "Capacity observer closure is unconfirmed; retain experiment ownership.");
                capacityQuery.TryComplete(out _);
                CompleteTraceAndPersist(dispose: false);
                Require(ObserverAbsent, "Storage observer closure changed after joining; retain experiment ownership.");
            }
            catch (Exception error) { RetainClosureError(error); throw; }
        }
        void RetainClosureError(Exception error)
        {
            closureError ??= error;
            lock (uncertainDisposals) if (!uncertainDisposals.Contains(this)) uncertainDisposals.Add(this);
        }
        void PersistWriterReceipt()
        {
            lock (writerReceiptGate)
            {
                if (!writerReceiptAttempted)
                {
                    writerReceiptAttempted = true;
                    try
                    {
                        // Exactly one CreateNew/Flush(true) attempt. Never
                        // replace a partial, failed or incomplete first receipt.
                        Write(Path.Combine(root, Path.GetFileNameWithoutExtension(traceName) + "-writer.json"), trace.Receipt);
                        writerReceiptDurable = true;
                    }
                    catch (Exception error) { writerReceiptError = error; }
                }
                if (writerReceiptError != null) throw new IOException("Actual trace receipt persistence failed.", writerReceiptError);
            }
        }
        void CompleteTraceAndPersist(bool dispose)
        {
            var errors = new List<Exception>();
            try { if (dispose) trace.Dispose(); else trace.CompleteAndCheck(); }
            catch (Exception error) { errors.Add(error); }
            try { PersistWriterReceipt(); } catch (Exception error) { errors.Add(error); }
            if (errors.Count != 0) throw new IOException("Actual trace closure or receipt failed: " + string.Join("; ", errors.Select(e => e.Message)), new AggregateException(errors));
            traceClosedHealthy = trace.Receipt is BoundedCalibrationTrace.TraceReceipt { final: true, writerAbsent: true, failure: null } receipt
                && receipt.enqueued == receipt.written && receipt.bytes == receipt.persistedBytes;
            Require(traceClosedHealthy, "Actual persisted trace closure is incomplete or failed.");
        }
        internal void StopAndCheck()
        {
            if (activeCoverage) EndActive();
            Require(offlineDeadline == 0, "Offline phase must close before the sampler.");
            JoinStoppedObservers();
            double stopped = clock.Elapsed.TotalMilliseconds;
            MaximumGap = Math.Max(MaximumGap, stopped - lastCompletion);
            if (activeCoverage) SamplingGap(lastCompletion, stopped);
            Check();
        }
        public void Dispose()
        {
            if (disposalAttempted)
            {
                if (disposalError != null) throw new IOException("Earlier sampler disposal failed.", disposalError);
                return;
            }
            disposalAttempted = true;
            try
            {
                stop = true; Require(thread.Join(5000), "Storage observer did not stop.");
                Require(capacityThread.Join(1000), "Capacity supervisor did not stop.");
                Require(capacityQuery.WaitForClosure(1000), "Capacity observer closure is unconfirmed; retain experiment ownership.");
                var errors = new List<Exception>();
                if (FiveMinuteStorage(plan) && fiveMinuteJournal.HasChanges)
                {
                    string cell = Path.Combine(plan.Root, "H03");
                    if (!File.Exists(Path.Combine(cell, "native-allocation-growth.json")))
                        try { fiveMinuteJournal.WriteTo(cell, captureGrowth["H03"].StopAt, partial: true); }
                        catch (Exception error) { errors.Add(error); }
                }
                if (closureError != null) errors.Add(closureError);
                if (Failure != null) errors.Add(new IOException(Failure));
                try { capacityQuery.TryComplete(out _); } catch (Exception error) { errors.Add(error); }
                try { CompleteTraceAndPersist(dispose: true); } catch (Exception error) { errors.Add(error); }
                if (errors.Count != 0) throw new IOException("Actual sampler disposal failed: " + string.Join("; ", errors.Select(e => e.Message)), new AggregateException(errors));
                frozenHistory?.Dispose(); ready.Dispose();
            }
            catch (Exception error)
            {
                disposalError = error;
                // Retain uncertainty and native history owners through actual
                // controller exit; a failed receipt is not permission to release.
                RetainClosureError(error);
                throw;
            }
        }
    }
    internal static void VerifyPublicationObservation(ReplayFileExtent observed, ReplayFileExtent expected)
        => Require(expected == null || observed.Volume == expected.Volume && observed.Identity == expected.Identity
            && observed.Length == expected.Length && observed.AllocationUnit == expected.AllocationUnit,
            "Publication bookkeeping observed a different checked output object.");
    internal static void CompleteStoppedCaptureClaim(Sampler sampler, bool producerAbsent, Action complete,
        int observerJoinMilliseconds = 5000)
    {
        if (sampler.StopRequested || sampler.Failure != null)
        {
            Require(producerAbsent, "Capture producer absence is unconfirmed; retain ownership and reservations.");
            sampler.JoinStoppedObservers(observerJoinMilliseconds);
        }
        sampler.RequireFailureClosure(producerAbsent);
        complete();
    }

    internal static void WithPublicationGate(object gate, Action prepare, Action publish)
    {
        prepare();
        lock (gate) publish();
    }

    sealed class RetainFiles(Sampler sampler, bool hardlinksRequired = false,
        Action<ReplayExportRequest, string> retainOutput = null) : ReplayFiles
    {
        internal override ReplayNamespaceIdentity ObserveNamespace(string path, ReplayFileExtent enumerated)
            => new(enumerated.Volume, enumerated.Identity); // Real native namespace, never allocation credit.
        internal override bool TryHardLink(string copy, string original)
        {
            bool linked = base.TryHardLink(copy, original);
            if (!linked && hardlinksRequired) throw new IOException("Native qualification requires admitted same-volume hard links; no payload copy was reserved.");
            return linked;
        }
        internal override void DeleteDirectory(string path, string workspacePartial = null) { } // All successes and failures are retained.
        internal override void DeleteFile(string path) { }
        internal override void Publish(string partial, string final)
            => WithPublicationGate(sampler.Gate, () => sampler.BeforePublication(partial), () => base.Publish(partial, final));
        internal override void Publish(ReplayExportRequest request, string final)
        {
            if (request.ValidatedOutput == null) { base.Publish(request, final); return; }
            WithPublicationGate(sampler.Gate, () => { sampler.BeforePublication(request.Partial, request.ValidatedOutput.CheckedIdentity); retainOutput?.Invoke(request, final); },
                () => base.Publish(request, final));
        }
    }
    internal sealed class ShortSource(IReplaySessionSource source, int seconds, double end, bool exactFallback = false,
        Action<ReplayExportRequest> protect = null, CalibrationValidationWork.GuardContext validationContext = null,
        bool preserveEvidence = false, Action<ReplayExportRequest, Action> inspectPublication = null,
        Func<List<Segment>> protectedSegments = null, Func<ReplayOutputSelection, ReplayOutputDemand> qualificationDemand = null) : IReplaySessionSource
    {
        public ReplayValidation ValidateOutput(ReplayExportRequest request, Action<ReplayProducerProof> changed, Action check)
        { using var scope = validationContext?.Enter(check); var result = inner.ValidateOutput(request, changed, check); inspectPublication?.Invoke(request, check); return result; }
        public ReplayValidation ValidateOutput(string path, ReplayInterval interval, Action<ReplayProducerProof> changed, Action check)
        { using var scope = validationContext?.Enter(check); return inner.ValidateOutput(path, interval, changed, check); }
        readonly IReplaySessionSource inner = source;
        public ReplaySessionInfo ReplayInfo => inner.ReplayInfo with { ReplaySeconds = seconds };
        public double InputPrerollSeconds => inner.InputPrerollSeconds;
        public bool PreserveEvidence => preserveEvidence || inner.PreserveEvidence;
        public bool SupportsOwnedExport => inner.SupportsOwnedExport;
        public bool SupportsStagedPreparation => !exactFallback && inner.SupportsStagedPreparation;
        public ReplaySavePreparation BeginPreparation(ReplayRootedFiles files, ReplayInterval interval, Segment candidate, string partial,
            Action accepted, Action<ReplayProducerProof> changed, Action check, Action<ReplaySavePreparation> take)
            => !exactFallback ? inner.BeginPreparation(files, interval, candidate, partial, accepted, changed, check, take)
                : throw new IOException("Exact-fallback qualification does not stage a packet boundary.");
        public ReplayProducerProof ProducerProof => inner.ProducerProof;
        public ReplayOutputDemand OutputDemandStatus => inner.OutputDemandStatus;
        public List<Segment> ReadSegments() => protectedSegments?.Invoke() ?? inner.ReadSegments();
        public bool Stopped => true;
        public string Diagnostics => inner.Diagnostics;
        public string OutputDemandIdentity => qualificationDemand == null ? inner.OutputDemandIdentity : ReplayReferenceModel.ExporterIdentity;
        public bool OutputProducerStopped => inner.OutputProducerStopped;
        public ReplayOutputDemand EstimateOutput(ReplayOutputSelection selection) => qualificationDemand?.Invoke(selection) ?? inner.EstimateOutput(selection);
        public ReplayOutputDemand RecheckOutputDemand(ReplayOutputSelection selection) => qualificationDemand?.Invoke(selection) ?? inner.RecheckOutputDemand(selection);
        public double SaveCutoffAt(long timestamp) => end;
        public void FreezeAt(long timestamp) { }
        public void StopCapture() { }
        public void CheckCaptureHealth(double completedEnd) { }
        public void Export(IReadOnlyList<string> arguments, string snapshot) => inner.Export(arguments, snapshot);
        public void Export(ReplayExportRequest request, string snapshot, Action<ReplayProducerProof> changed, Action check)
        {
            protect?.Invoke(request);
            if (exactFallback) ((CaptureSession)inner).ExportRequest(request, snapshot, changed, check, ReplaySaveExport.Construction.ExactFallback);
            else inner.Export(request, snapshot, changed, check);
        }
        public void Export(IReadOnlyList<string> arguments, string snapshot, Action<ReplayProducerProof> changed, Action check)
            => inner.Export(arguments, snapshot, changed, check);
        public void Dispose() { } // Original source registration owns its job.
    }
    static object SaveProcess(string root, string prefix, ReplayExportProcessReceipt receipt)
    {
        string stdout = Path.Combine(root, prefix + ".stdout.txt"), stderr = Path.Combine(root, prefix + ".stderr.txt");
        foreach (var item in new[] { (Path: stdout, Text: receipt.StandardOutput), (Path: stderr, Text: receipt.StandardError) })
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(item.Text);
            Require(bytes.Length <= 2_097_152, "Bounded process log exceeded cap.");
            using var output = new FileStream(item.Path, FileMode.CreateNew, FileAccess.Write, FileShare.Read); output.Write(bytes);
        }
        return new { receipt.ProcessId, receipt.ProcessStartedUtc, receipt.ExitCode, receipt.ProducerAbsent, receipt.ActualExitObserved,
            receipt.TimedOut, receipt.OutputLimitExceeded, receipt.CaptureComplete, receipt.CapturedBytes, receipt.ElapsedMilliseconds,
            receipt.LastStorageCheckWorkMilliseconds, receipt.LastStorageCheckIdleMilliseconds,
            receipt.CpuMilliseconds, receipt.ReadBytes, receipt.WriteBytes, receipt.SampledPeakWorkingSetBytes, receipt.ResourcesFinal, receipt.ResourceError,
            receipt.Failure, receipt.Error, stdout, stderr };
    }
    static object[] SaveProcesses(string root, string prefix, CaptureSession source) => source.LastExportProcesses
        .Select((receipt, number) => SaveProcess(root, prefix + "-stage-" + number, receipt)).ToArray();
    static ReplayCalibrationProcessReceipt[] ExportChildren(Sample sample)
    {
        Require(sample.ExportProcesses != null && sample.ExportProcesses.Length >= (sample.ManagedExport == null ? 1 : 0)
            && sample.ExportProcesses.Length <= 3, "Complete actual export child list is required.");
        return sample.ExportProcesses.Select(child =>
        {
            var observed = JsonSerializer.SerializeToElement(child);
            Require(observed.GetProperty("CaptureComplete").GetBoolean() && observed.GetProperty("ProducerAbsent").GetBoolean()
                && observed.GetProperty("ActualExitObserved").GetBoolean() && observed.GetProperty("ExitCode").GetInt32() == 0
                && !observed.GetProperty("TimedOut").GetBoolean() && !observed.GetProperty("OutputLimitExceeded").GetBoolean(),
                "An export child lacks complete normal closure evidence.");
            return new ReplayCalibrationProcessReceipt(ReplayReferenceModel.Hash(child), true, true, 0, false, false, false,
                observed.GetProperty("ProcessId").GetInt32(), observed.GetProperty("ProcessStartedUtc").GetDateTime().ToUniversalTime().Ticks);
        }).ToArray();
    }
    static void RequireExportEvidence(Sample sample, ReplayCalibrationSourceReceipt receipt)
    {
        var children = ExportChildren(sample);
        Require(receipt.ExportProcesses != null && receipt.ExportProcesses.SequenceEqual(children) && receipt.Export == children.LastOrDefault()
            && receipt.CombinedPeakAllocation == sample.CombinedPeakAllocation && receipt.WorkPeakAllocation == sample.WorkPeakAllocation
            && receipt.AssemblyPeakAllocation == sample.AssemblyPeakAllocation && receipt.BoundaryPeakAllocations != null
            && sample.BoundaryPeakAllocations != null && receipt.BoundaryPeakAllocations.SequenceEqual(sample.BoundaryPeakAllocations)
            && receipt.NativeExport == sample.NativeExport,
            "Retained output/work peaks or export child evidence changed.");
    }
    sealed record Sample(string Label, string Scope, string File, long InputBytes, long FinalBytes, long FinalAllocation,
        long PeakAllocation, double RequestedEnd, double ExportSeconds, string SelectionIdentity, ReplayOutputDemand Demand,
        object Process, string[] Arguments, ReplayInterval Interval = null, object RuntimeProcess = null, string[] RuntimeArguments = null,
        long CombinedPeakAllocation = 0, long WorkPeakAllocation = 0, object[] ExportProcesses = null,
        long AssemblyPeakAllocation = 0, long[] BoundaryPeakAllocations = null,
        ReplayValidation NormalValidation = null, ReplayManagedMuxReceipt ManagedExport = null,
        ReplayTransportBatchTiming IndexBatch = null, ReplaySaveTiming Timing = null, Sample LegacyOutput = null, string Variant = null,
        string AcquisitionSelectionHash = null, ReplayCalibrationNativeReceipt NativeExport = null);

    static Sample Collect(CalibrationPlan plan, string label, Sampler sampler, ReplayReferenceModel model, List<object> processes,
        Action<int, IntPtr> beforeForeground = null, Dictionary<string, ReplayPublicationLease> qualificationOutputs = null)
    {
        sampler.RequireTime(AcquisitionSeconds(plan));
        sampler.Check(DurationQualification(plan) ? checked(plan.SourceComponentBytes + plan.OutputComponentBytes + QualificationScratchBytes(plan)) : 3_100_000_000L);
        string root = Path.Combine(plan.Root, label); Directory.CreateDirectory(root);
        var previous = GetForegroundWindow();
        using var fixtureJob = new Native.ProcessJob();
        Process fixture = null; CaptureSession capture = null; ReplayCache cache = null;
        Issue14WorkerCase workerCase = null;
        CalibrationCaptureIdentity captureIdentity = null;
        ReplayStorageCoordinator captureStorage = null;
        ReplayStorageStopLatch captureLatch = null;
        ReplayVolumeReservations sharedClaims = null;
        ReplaySessionId id = default;
        var total = Stopwatch.StartNew();
        int exports = 0;
        bool successful = false;
        Exception primaryFailure = null;
        SaveSubmission activeSubmission = null;
        using var bootstrapCancel = new CancellationTokenSource();
        Task<CaptureSession> create = null;
        bool runtimeOffline = false; int runtimePhases = 0; NativeEvidence nativeEvidence = null;
        bool captureClaimClosed = false;
        bool fixtureForced = false;
        ReplayPairedSelectionLease pairedInputs = null;
        var cadence = plan.CompositeQualification ? new CalibrationMaintenanceCadence(TimeProvider.System) : null;
        var maintenanceIntervals = new List<object[]>();
        var validationContext = new CalibrationValidationWork.GuardContext();
        var validationObserverTimings = new List<CalibrationValidationWorkTiming>();
        var nativeRequests = new Dictionary<string, (ReplayExportRequest Request, bool Eligible, int Thread, long Started,int Linked,int Prepared)>(StringComparer.OrdinalIgnoreCase);
        (bool Forced, int Code) CloseFixture()
        {
            var owned = fixture ?? throw new IOException("Owned source fixture is unavailable for closure.");
            if (!owned.HasExited)
            {
                owned.CloseMainWindow();
                if (!owned.WaitForExit(5000))
                {
                    fixtureForced = true;
                    owned.Kill();
                    Require(owned.WaitForExit(5000), "Fixture exit unconfirmed.");
                }
            }
            int code = owned.ExitCode;
            try
            {
                var closure = new { role = "fixture-exit", label, pid = owned.Id, startedUtc = owned.StartTime.ToUniversalTime(), exited = owned.HasExited, exit = code, forced = fixtureForced };
                processes.Add(closure);
                Write(Path.Combine(root, "fixture-exit.json"), closure);
            }
            finally { fixture = null; owned.Dispose(); }
            return (fixtureForced, code);
        }
        void ObserveNativeExport(ReplayExportRequest request)
        {
            Require(request.NativeFiles != null && request.NativeFiles.Eligible,
                "Native qualification requires an owned request on its actual export thread.");
            int owned=request.PreparedInputs?.Count??0;
            Require(request.Timing.HardLinkedInputs+owned == request.Inputs.Count && request.Timing.PreparedOwnedInputs==owned && request.Timing.CopiedInputs == 0,
                "Paired qualification requires live original-object ownership or hardlinks; retain copied-source failure and launch no next source.");
            var guard=new ReplayPlanningGuard(()=>sampler.Check());guard.CheckNow();
            foreach(var input in request.Inputs)
            {
                guard.Check();
                if(request.PreparedInputs?.Contains(input.Path)==true)
                {var extent=request.PreparedInputs.Extent(input.Path);Require(extent.Volume==input.Extent.Volume && extent.Identity==input.Extent.Identity
                    && extent.Length==input.Extent.Length && extent.AllocationUnit==input.Extent.AllocationUnit,"Prepared original ownership differs from accepted input.");}
            }
            guard.CheckNow();
            nativeRequests.Add(request.Partial, (request, request.NativeFiles.Eligible,
                Environment.CurrentManagedThreadId, Stopwatch.GetTimestamp(),request.Timing.HardLinkedInputs,owned));
        }
        void RetainCheckedOutput(ReplayExportRequest request, Action check)
        {
            CalibrationValidationWork.Run(() => {
                Require(qualificationOutputs != null && request.ValidatedOutput != null,
                    "Qualification requires continuous checked-output ownership.");
                sampler.BeforePublication(request.Partial, request.ValidatedOutput.CheckedIdentity);
                var lease = new ReplayPublicationLease(request.Partial);
                try
                {
                    Require(lease.CheckedIdentity == request.ValidatedOutput.CheckedIdentity,
                        "Qualification handoff changed the normally checked file.");
                    qualificationOutputs.Add(request.Partial, lease);
                }
                catch { lease.Dispose(); throw; }
            }, check, receipt => {
                Require(validationObserverTimings.Count < 16, "Validation observer timing bound.");
                validationObserverTimings.Add(receipt);
            });
        }
        ReplayCalibrationNativeReceipt CompleteNativeExport(string file, string evidenceRoot, bool packet)
        {
            var published = ReplayStorage.Observe(file);
            var matches = nativeRequests.Values.Where(row => qualificationOutputs.TryGetValue(row.Request.Partial, out var lease)
                && lease.CheckedIdentity.Volume == published.Volume && lease.CheckedIdentity.Identity == published.Identity
                && lease.CheckedIdentity.Length == published.Length && lease.CheckedIdentity.AllocationUnit == published.AllocationUnit).ToArray();
            Require(matches.Length == 1, "Published output has no unique continuously owned native request.");
            var row = matches[0]; var request = row.Request;
            Require(request.NativeFiles is { Closed: true } && row.Eligible && request.PacketPipelineSelected == packet
                && request.Timing.ExportConstruction == (packet ? ReplaySaveExport.BoundedPacketConstruction
                    : nameof(ReplaySaveExport.Construction.ExactFallback)),
                "Native qualification selected the wrong construction or failed to close its request scope.");
            if(OwnedHistoryQualification(plan))
                Require(request.Timing.CopiedInputs==0 && row.Linked+row.Prepared==request.Inputs.Count
                    && request.Timing.HardLinkedInputs+request.Timing.PreparedOwnedInputs-request.Timing.PreparedFallbackInputs==request.Inputs.Count
                    && (!packet || DurationQualification(plan) || request.Timing.PreparedOwnedInputs>=request.Inputs.Count-2
                        && request.Timing.PreparedIndexHits>=request.Inputs.Count-2
                        && request.Timing.PreparedIndexHits+request.Timing.PreparedIndexMisses==request.Inputs.Count),
                    "Fresh owned-history qualification did not exercise completed prepared ownership or normalized closure accounting.");
            Require(!request.OwnWorkDirectory || !Directory.Exists(request.WorkDirectory) && !File.Exists(request.WorkDirectory),
                "Native qualification retained its work directory after save cleanup.");
            if(packet)
                Require(request.Timing.IndexReadersStarted==request.IndexReaders.Started
                    && request.Timing.IndexReadersJoined==request.IndexReaders.Joined
                    && request.Timing.IndexAcquisitionComplete
                    && (request.PreparedInputs==null
                        ?request.IndexBatch.FinishedSources==request.Inputs.Count && request.IndexReaders.Joined
                        :request.PreparedAcquisition.Complete && request.Timing.PreparedIndexHits==request.PreparedAcquisition.Hits
                            && request.Timing.PreparedIndexMisses==request.PreparedAcquisition.Misses),
                    "Index acquisition diagnostics differ from the actual request-owned acquisition/lifetime.");
            var held = qualificationOutputs[request.Partial];
            Require(!qualificationOutputs.ContainsKey(file), "Duplicate qualification output handoff.");
            qualificationOutputs.Add(file, held); qualificationOutputs.Remove(request.Partial);
            string witness = Path.Combine(evidenceRoot, "native-export-witness.json");
            var staged = request.Preparation;
            bool requireStaged = packet && label.StartsWith("H", StringComparison.Ordinal);
            Require((staged != null) == requireStaged && (!requireStaged || staged.Joined
                && staged.Timing is { FeedJoined: true, Aborted: false, Error: null, FeedSha256.Length: 64, FedBytes: > 0 and <= 8388608 }),
                "Current packet heldouts must exercise actual reserved staging and join its verified feeder/producer.");
            Write(witness, new { policy = ReplaySaveExport.PolicyIdentity, ownedRequest = request.NativeFiles != null,
                eligibleAtExport = row.Eligible, pipelineSelected = request.PacketPipelineSelected,
                scopeClosed = request.NativeFiles.Closed, request.Partial, published = file,
                exportThread = row.Thread, exportEntered = row.Started,
                selectionIdentity = capture.LastOutputSelection.Identity,
                protectedSelectionHash = ReplayReferenceModel.Hash(new { request.Info, request.Interval, request.Inputs }),
                linkedAtExportEntry=row.Linked,preparedAtExportEntry=row.Prepared,request.Timing.HardLinkedInputs, request.Timing.CopiedInputs,
                request.Timing.PreparedOwnedInputs,request.Timing.PreparedIndexHits,request.Timing.PreparedIndexMisses,request.Timing.PreparedFallbackInputs,
                request.Timing.IndexAcquisitionComplete,request.Timing.IndexReadersStarted,request.Timing.IndexReadersJoined,
                checkedOutput = held.CheckedIdentity, request.Timing.ExportConstruction,
                request.Timing.ExportRecipe, sourcePruningRetainedForQualification = true,
                workBodiesRetiredAfterForcedObservation = true,
                preparationSelected = staged != null, preparationJoined = staged?.Joined ?? false, preparation = staged?.Timing });
            nativeRequests.Remove(request.Partial);
            return new(HashFile(witness), ReplaySaveExport.PolicyIdentity, true, row.Eligible,
                request.PacketPipelineSelected, request.NativeFiles.Closed,
                staged != null, staged?.Joined ?? false, staged?.Timing.FeedSha256, staged?.Timing.FedBytes ?? 0);
        }
        void MaintainCapture()
        {
            if (cache == null || cadence != null && !cadence.MaintenanceDue) return;
            long started = Stopwatch.GetTimestamp();
            Exception failed = null;
            try { cache.Maintain(id); }
            catch (Exception error) { failed = error; throw; }
            finally
            {
                long completed = Stopwatch.GetTimestamp();
                if (cadence != null)
                {
                    if (maintenanceIntervals.Count < (DurationQualification(plan) ? 16384 : 2048))
                        maintenanceIntervals.Add([started, completed, failed == null ? null : ReplayOutputDemand.Bounded(failed.ToString(), 1024)]);
                    else if (failed == null) throw new IOException("Composite maintenance trace bound.");
                    if (failed == null) cadence.MaintenanceCompleted(); // No catch-up or renewal on failure.
                }
            }
        }
        void BeginPayloadWork()
        {
            if (!plan.ReadonlyValidationPhases) return;
            if (runtimeOffline) { sampler.EndOffline(); runtimeOffline = false; }
            if (!sampler.ActiveCoverage) sampler.BeginActive();
        }
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = false, CreateNoWindow = true };
            if (Path.GetFileNameWithoutExtension(Environment.ProcessPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(typeof(ReplayCalibrationRun).Assembly.Location);
            if (DurationQualification(plan))
            {
                foreach (string value in new[] { SameCaptureQualification(plan) ? "--replay-same-C00-fixture" : "--replay-calibration-duration-fixture", root, label, plan.ReplayMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture) }) start.ArgumentList.Add(value);
                var presentationBinding=PresentationPlanBinding.For(plan);
                if(presentationBinding!=null)foreach(string value in new[]{"--presentation-reference",presentationBinding.Path,presentationBinding.Hash})start.ArgumentList.Add(value);
            }
            else
            {
                foreach (string value in new[] { "--calibration-fixture", root, label }) start.ArgumentList.Add(value);
                if (plan.UserForegroundHandoff || plan.CompositeManualForegroundHandoff) start.ArgumentList.Add("manual-handoff");
            }
            AdmitNativeChild("fixture:" + label); fixture = Process.Start(start); fixtureJob.Add(fixture);
            processes.Add(new { role = "fixture", label, pid = fixture.Id, start = fixture.StartTime.ToUniversalTime() });
            while (!File.Exists(Path.Combine(root, "fixture-ready.json")))
            {
                sampler.Check(); Require(!fixture.HasExited && total.Elapsed.TotalSeconds < 15, "Controlled fixture did not become ready."); Thread.Sleep(100);
            }
            using var ready = CalibrationFixturePublication.Read(root);
            int fixtureLifetime = FixtureSeconds(plan);
            Require(ready.RootElement.GetProperty("lifetimeSeconds").GetInt32() == fixtureLifetime
                && ready.RootElement.GetProperty("maximumAnimatedFrame").GetInt32() == CalibrationPattern.MaximumAnimatedFrame(fixtureLifetime)
                && ready.RootElement.GetProperty("barcodeFrameDivisor").GetInt32() == (SameCaptureQualification(plan) ? 1 : CalibrationPattern.BarcodeFrameDivisor(fixtureLifetime))
                && (!SameCaptureQualification(plan) || ready.RootElement.GetProperty("sourceIdRange").GetInt32()==CalibrationPattern.ReferenceSourceIdRange)
                && ready.RootElement.GetProperty("timerIntervalMilliseconds").GetInt32() == CalibrationPattern.TimerIntervalMilliseconds,
                "Fixture lifetime/barcode contract differs from the admitted helper.");
            IntPtr hwnd = (IntPtr)ready.RootElement.GetProperty("hwnd").GetInt64();
            Require(ready.RootElement.GetProperty("width").GetInt32() == 1920 && ready.RootElement.GetProperty("height").GetInt32() == 1080, "Fixture dimensions changed.");
            uint windowProcess; Native.GetWindowThreadProcessId(hwnd, out windowProcess);
            Require(windowProcess == fixture.Id && ready.RootElement.GetProperty("pid").GetInt32() == fixture.Id,
                "Fixture window does not belong to the task-owned process; no capture admitted.");
            beforeForeground?.Invoke(fixture.Id, hwnd);
            if (plan.CompositeManualForegroundHandoff)
            {
                var release = plan.CompositeExplicitForegroundRelease
                    ? CalibrationForegroundRelease.Create(label, fixture.Id, hwnd.ToInt64()) : null;
                Write(Path.Combine(root, "foreground-handoff-ready.json"), new { label, pid = fixture.Id, hwnd = hwnd.ToInt64(),
                    maximumSeconds = CalibrationForeground.UserHandoffSeconds, captureStarted = false,
                    release,
                    instruction = release == null
                        ? "Click the controlled pattern and keep it foreground while recording. No capture starts before verified foreground."
                        : "Finish all UI permission/activation calls, then atomically publish the matching release. Foreground alone cannot start capture. Keep the pattern foreground afterward." });
                var handoffClock = Stopwatch.StartNew();
                CalibrationForeground.WaitForUserHandoff(() => { Native.GetWindowThreadProcessId(hwnd, out uint owner);
                        return owner == fixture.Id && Native.IsWindowVisible(hwnd) && GetForegroundWindow() == hwnd
                            && (release == null || release.IsReleased(root)); },
                    () => { Native.GetWindowThreadProcessId(hwnd, out uint owner); return fixture.HasExited || owner != fixture.Id; },
                    () => sampler.Check(), () => Thread.Sleep(50), () => handoffClock.Elapsed.TotalSeconds);
                Write(Path.Combine(root, "foreground-handoff-complete.json"), new { label, pid = fixture.Id, hwnd = hwnd.ToInt64(),
                    waitedMilliseconds = handoffClock.Elapsed.TotalMilliseconds, captureStarted = false, release });
                total.Restart(); // The declared operator wait precedes the unchanged acquisition deadline.
            }
            if (plan.UserForegroundHandoff) total.Restart(); // The separately bounded focus handoff precedes source setup.
            bool requested = false;
            if (!plan.UserForegroundHandoff && !plan.CompositeManualForegroundHandoff)
            {
                Native.ShowWindow(hwnd, 5); // Show only our own window; no preference or input changes.
                requested = SetForegroundWindow(hwnd);
            }
            var focusClock = Stopwatch.StartNew();
            CalibrationForeground.Wait(() => { Native.GetWindowThreadProcessId(hwnd, out uint currentOwner);
                    return currentOwner == fixture.Id && GetForegroundWindow() == hwnd && Native.IsWindowVisible(hwnd); },
                () => fixture.HasExited, () => { sampler.Check(); Require(total.Elapsed.TotalSeconds < AcquisitionSeconds(plan), "Source setup deadline."); },
                () => Thread.Sleep(50), () => focusClock.Elapsed.TotalSeconds);
            Write(Path.Combine(root, "foreground-admission.json"), new { fixturePid = fixture.Id, hwnd = hwnd.ToInt64(), requested,
                visible = Native.IsWindowVisible(hwnd), foreground = GetForegroundWindow() == hwnd, waitedMilliseconds = focusClock.Elapsed.TotalMilliseconds });
            var independentPresentation=PresentationPlanBinding.For(plan);
            if(independentPresentation!=null)
                CalibrationPresentationIntegration.RequestAndWait(root,independentPresentation,fixture.Id,hwnd,()=>sampler.Check(),()=>fixture.HasExited);
            RecorderRuntime.CaptureArgumentObserver = a =>
            {
                var normalized = ReplayCalibrationStartup.NormalizeCaptureArguments(a, root,
                    Exact14(plan) && label == "H03" ? Issue14WorkerCase.CacheRoot(root) : null);
                Require(normalized.SequenceEqual(ExpectedCaptureTemplate(plan, Native.PrimaryOutputIndex().ToString(CultureInfo.InvariantCulture))), "Real capture arguments differ from frozen reference recipe.");
                Write(Path.Combine(root, "capture-arguments.json"), a);
            };
            RecorderRuntime.ExportArgumentObserver = a => { BeginPayloadWork(); nativeEvidence?.RequireNativeSnapshot(a, capture.LastOutputSelection); AdmitNativeChild("export:" + label); Write(Path.Combine(root, "export-arguments-" + ++exports + ".json"), a); };
            var settings = new Settings { ReplayMinutes = plan.ReplayMinutes, FramesPerSecond = 60, BitrateMbps = 20, VideoCodec = "h264_nvenc",
                SystemAudio = true, CaptureMode = "monitor", DisplayIndex = -1, OutputDirectory = root };
            Write(Path.Combine(root, "settings.json"), settings);
            if (ContextQualification(plan))
                sharedClaims = new ReplayVolumeReservations(Path.Combine(plan.Root, "calibration-ownership"), ReplayStorageSafety.MinimumBytes);
            if (model != null && !(Exact14(plan) && label == "H03"))
            {
                string profile = Path.Combine(root, "storage-profile"); Directory.CreateDirectory(profile);
                string authority = Path.Combine(plan.Root, "calibration-ownership");
                sharedClaims = new ReplayVolumeReservations(authority, ReplayStorageSafety.MinimumBytes);
                captureLatch = new ReplayStorageStopLatch(profile, authority);
                captureStorage = new ReplayStorageCoordinator(captureLatch, sharedClaims, catalog: () => new(model, null));
                captureStorage.RequireAdmission(Path.Combine(root, "buffer"), root, settings);
            }
            if (Exact14(plan) && label == "H03")
            {
                workerCase = new Issue14WorkerCase(root, settings, model);
                create = workerCase.Ready;
            }
            else
            {
            AdmitNativeChild("capture:" + label);
            create = Task.Run(() => new CaptureSession(settings, Path.Combine(root, "buffer"), false,
                () => bootstrapCancel.IsCancellationRequested || sampler.Failure != null || GetForegroundWindow() != hwnd,
                null, beforeLaunch: source =>
                {
                    if (ContextQualification(plan))
                        Require(source.ReferenceScope == (FiveMinuteStorage(plan) ? ReadFiveMinuteContract(plan).Context : ReadContextFreeze(plan).Context).Scope(ReplayReferenceModel.ExporterIdentity, 5),
                            "C00 actual capture context differs from the prospective freeze.");
                    if (captureStorage == null) return;
                    captureStorage.BeginCapture(source.ReferenceScope, source.DirectoryPath, root, () =>
                    {
                        bootstrapCancel.Cancel();
                        _ = Task.Run(source.StopCapture);
                    }, producerProof: source.ProducerProof, replayMinutes: plan.ReplayMinutes);
                    captureStorage.StartSupervision();
                }, afterLaunch: source => captureStorage?.SealProducer(source.ProducerProof), qualifyDesktopTiming: true));
            }
            var bootstrapClock = Stopwatch.StartNew();
            while (!create.IsCompleted) { sampler.Check();
                IntPtr actualForeground = GetForegroundWindow();
                if (actualForeground != hwnd || fixture.HasExited)
                {
                    GetWindowThreadProcessId(actualForeground, out uint actualOwner);
                    Write(Path.Combine(root, "foreground-failure.json"), new { utc = DateTime.UtcNow, label, phase = "bootstrap",
                        expectedWindow = hwnd.ToInt64(), actualWindow = actualForeground.ToInt64(), actualOwner,
                        fixturePid = fixture.Id, fixtureExited = fixture.HasExited });
                    throw new IOException("Controlled fixture lost foreground during bootstrap.");
                }
                Require(bootstrapClock.Elapsed.TotalSeconds < 35
                && total.Elapsed.TotalSeconds < AcquisitionSeconds(plan), "Capture bootstrap deadline."); Thread.Sleep(100); }
            capture = create.GetAwaiter().GetResult();
            if (plan.ExactContinuationRoot != null || plan.ProductionQualification) nativeEvidence = new NativeEvidence(root, capture.DirectoryPath,
                completedSegments: ((IReplaySessionSource)capture).ReadSegments, excludeInitial: ((IReplaySessionSource)capture).ReplayInfo.ExcludeInitialSegment);
            capture.ValidationArgumentObserver = args =>
            {
                Require(!plan.CompositeQualification || validationContext.Current != null,
                    "Composite validation observer is missing its actual product guard.");
                CalibrationValidationWork.Run(() => {
                nativeEvidence?.PreserveOutput(args[args.ToList().IndexOf("-i") + 1]);
                sampler.BeforePublication(args[args.ToList().IndexOf("-i") + 1]);
                if (plan.ReadonlyValidationPhases && capture.CaptureProducerAbsent
                    && capture.LastExport is { Success: true, ActualExitObserved: true, ProducerAbsent: true })
                {
                    Require(!runtimeOffline, "A previous read-only validation phase is still open.");
                    Require(capture.ExportArguments is { Length: > 0 } && args.SequenceEqual(
                        ReplayExactExport.DecodeArguments(capture.ExportArguments[^1], true, 60)),
                        "Read-only phase requires the exact closed-MP4/null-output validation recipe.");
                    sampler.EndActive(); sampler.BeginOffline("runtime-readonly-validation", 32_000_000, 120); runtimeOffline = true;
                    Write(Path.Combine(root, "runtime-readonly-phase-" + ++runtimePhases + ".json"), new {
                        capture.CaptureProducerAbsent, capture.CaptureExitCode, capture.LastExport.ProcessId,
                        capture.LastExport.ProcessStartedUtc, capture.LastExport.ActualExitObserved, capture.LastExport.ProducerAbsent,
                        encodedPath = capture.ExportArguments[^1], validationArguments = args, limitSeconds = 120, payloadWritersAbsent = true,
                        meaning = "Closed MP4 to null-output decoder plus bounded receipts/publication metadata. Product storage guard unchanged." });
                }
                AdmitNativeChild("runtime-validation:" + label);
                }, validationContext.Current, receipt => {
                    Require(validationObserverTimings.Count < 16, "Validation observer timing bound.");
                    validationObserverTimings.Add(receipt);
                });
            };
            capture.ObserveOutputDemand = workerCase == null; capture.OutputCalibration = model;
            if (SameCaptureQualification(plan))
            {
                var clock = capture.AcquisitionClock;
                Require(clock != null, "Actual C00 acquisition clock is required.");
                Write(Path.Combine(root, "source-clock.json"), new { clock.QpcEpoch, clock.QpcFrequency, clock.UnixMicroseconds, clock.BridgeUncertaintyTicks });
                ReplayPacketMuxExport.CompletedInputProofForChecks = (request, indices) =>
                {
                    Require(request.Inputs.Count == indices.Count && request.Inputs.Count <= 305, "Complete C00 digest/selection count mismatch.");
                    var proof = request.Inputs.Select((input, n) => {
                        Require(input.Extent.Length == indices[n].Bytes, "C00 established index EOF changed.");
                        return new ReplayCompletedInputDigest(input.Path, input.Start, input.End, input.Extent, indices[n].Sha256);
                    }).ToArray();
                    Write(Path.Combine(root, "completed-input-proof.json"), proof, 524288);
                };
            }
            Require(capture.ReferenceScope != null, "Real reference recipe or audio/dimensions are unqualified.");
            using (var ownedCapture = Process.GetProcessById(capture.ProcessId))
                captureIdentity = new(ownedCapture.Id, ownedCapture.StartTime.ToUniversalTime(), capture.DirectoryPath);
            processes.Add(new { role = "capture", label, pid = captureIdentity.Pid, start = captureIdentity.StartedUtc, sessionDirectory = captureIdentity.SessionDirectory });
            if (workerCase == null)
            {
                cache = new ReplayCache(plan.ExactContinuationRoot != null || plan.ProductionQualification || plan.CompositeQualification ? null
                    : new RetainFiles(sampler, Exact14(plan)), cacheDirectory: Path.Combine(root, "buffer"), volumeReservations: sharedClaims,
                    fileMode: plan.CompositeQualification ? ReplayFileMode.Production : null);
                if (plan.CompositeQualification)
                {
                    cache.RetainSourceNamespaceForQualificationChecks = true;
                    cache.SerializeNamespaceMutationForChecks = mutation => {
                        lock (sampler.Gate) { sampler.ReleaseInventoryHandles(); mutation(); }
                    };
                }
                id = cache.RegisterSession(plan.CompositeQualification
                    ? new ProtectedPairSource(capture, request => {
                        ObserveNativeExport(request);
                        pairedInputs = new ReplayPairedSelectionLease(request, capture.ReferenceScope, () => sampler.Check());
                    }, validationContext, RetainCheckedOutput, ContextQualification(plan) ? selection => capture.LastOutputDemand = UnmodelledQualificationDemand(capture.LastOutputSelection = selection, plan, capture.ReferenceScope, capture.DirectoryPath) : null)
                    : nativeEvidence != null ? new EvidenceSource(capture, nativeEvidence) : capture);
            }
            void StopSource()
            {
                if (workerCase != null) workerCase.StopCapture(); else cache.StopAcquisitionAsync(id).GetAwaiter().GetResult();
                if (captureStorage != null && !captureClaimClosed)
                {
                    Require(capture.CaptureProducerAbsent, "Capture/audio absence must precede capture-claim closure.");
                    captureStorage.CompleteCapture(true);
                    Require(!captureStorage.CapturePending && !captureStorage.Stopped && !captureLatch.Read().Held,
                        "Capture claim did not close normally; retain failure and admit no next source.");
                    captureClaimClosed = true;
                }
                nativeEvidence?.CaptureClosed();
            }
            while (((IReplaySessionSource)capture).ReadSegments().Count < 2) { sampler.Check(); Require(total.Elapsed.TotalSeconds < 35, "Initial source history timeout."); Thread.Sleep(50); }
            double availableStart = ((IReplaySessionSource)capture).ReadSegments().Where(s => !Path.GetFileName(s.Path).StartsWith("chunk-000000000.")).First().Start;
            double triggerAt = Exact14(plan) || plan.CompositeQualification ? availableStart + ReferenceSeconds(plan) + (label is "T01" or "H01" ? 1 : label is "T02" or "H03" ? .25 : .75)
                : label is "T01" or "H01" ? 304 : label is "T02" or "H03" ? 302.25 : 302.75;
            while (((IReplaySessionSource)capture).SaveCutoffAt(Stopwatch.GetTimestamp()) < triggerAt)
            {
                sampler.Check();
                Require(total.Elapsed.TotalSeconds < AcquisitionSeconds(plan), "Live acquisition exceeded its admitted deadline.");
                if (cadence == null || cadence.ObservationDue)
                {
                IntPtr foreground = GetForegroundWindow();
                if (fixture.HasExited || foreground != hwnd)
                {
                    GetWindowThreadProcessId(foreground, out uint foregroundOwner);
                    Write(Path.Combine(root, "foreground-failure.json"), new { utc = DateTime.UtcNow, label,
                        expectedWindow = hwnd.ToInt64(), actualWindow = foreground.ToInt64(), actualOwner = foregroundOwner,
                        fixturePid = fixture.Id, fixtureExited = fixture.HasExited,
                        effects = "Foreground owner identity only; interrupted source remains invalid and is never repaired into a passing sample." });
                    throw new IOException("Controlled source lost foreground or exited.");
                }
                var bounds = Native.DisplayBounds(IntPtr.Zero); Require(bounds.Width == 1920 && bounds.Height == 1080, "Display dimensions changed during capture.");
                var health = capture.ReadHealth();
                workerCase?.CheckHealth();
                if (total.Elapsed.TotalSeconds >= AcquisitionSeconds(plan) || !health.Healthy)
                {
                    Write(Path.Combine(root, "capture-health-failure.json"), health);
                    throw new IOException("Capture source failed or exceeded deadline: " + JsonSerializer.Serialize(health));
                }
                nativeEvidence?.CaptureClosed(acquiring: true);
                cadence?.ObservationCompleted();
                }
                MaintainCapture();
                Thread.Sleep(cadence?.DelayMilliseconds ?? 100);
            }
            // The composite model qualifies the live reference save workload.
            // A stopped capture can lack real trailing AAC at its video cutoff;
            // that correctly selects the exact fallback, not a packet sample.
            // Keep historical paused-source cases in their original routes.
            if (!plan.CompositeQualification && label is ("T01" or "H01"))
            {
                sampler.MarkCaptureStop(label);
                StopSource();
            }
            sampler.RequireTime(ExportControllerLimit(plan));
            long? pressSourceChecked = null;
            CaptureHealthSnapshot healthAtPress = independentPresentation!=null ? capture.ReadHealth() : null;
            if (cadence != null)
            {
                // A maintenance wake may cross triggerAt between observations.
                // Recheck immediately before stamping the immutable request.
                Require(GetForegroundWindow() == hwnd && !fixture.HasExited && Native.IsWindowVisible(hwnd)
                    && (healthAtPress??capture.ReadHealth()).Healthy && captureStorage?.Stopped != true, "Controlled source is not valid at the save press.");
                var pressBounds = Native.DisplayBounds(IntPtr.Zero);
                Require(pressBounds.Width == 1920 && pressBounds.Height == 1080, "Display dimensions changed at save press.");
                pressSourceChecked = Stopwatch.GetTimestamp();
            }
            long requestedQpc = Stopwatch.GetTimestamp();
            var save = workerCase != null ? workerCase.Save() : cache.TrySave(id, requestedQpc, settings.OutputDirectory);
            activeSubmission = save;
            if (!save.Accepted && plan.CompositeQualification)
            {
                try
                {
                    var observed = capture.LastOutputSelection;
                    var observedDemand = capture.LastOutputDemand;
                    double pressEndAfterRejection = capture.SaveCutoffAt(requestedQpc);
                    Write(Path.Combine(root, "admission-rejection.json"), new {
                        requestedQpc, pressSourceChecked, contextQpc = Stopwatch.GetTimestamp(), rejection = save.Rejection,
                        pressEndAfterRejection,
                        lastSelectionMatchesFrozenPress = observed != null && observed.End == pressEndAfterRejection,
                        selection = observed == null ? null : new {
                            observed.Identity, observed.ExporterIdentity, observed.Destination, observed.Start, observed.End, observed.Closed,
                            inputCount = observed.Inputs.Count,
                            first = observed.Inputs.FirstOrDefault(), last = observed.Inputs.LastOrDefault(),
                            duration = observed.End - observed.Start,
                            headSpan = observed.Inputs.Count == 0 ? (double?)null : observed.End - observed.Inputs[0].Start,
                            tailGap = observed.Inputs.Count == 0 ? (double?)null : observed.End - observed.Inputs[^1].End },
                        observedDemand,
                        interpretation = "Actual last estimator operands captured immediately after rejection, before drain. Match flag distinguishes any earlier estimate. The original press timestamp/end is unchanged; no retry, acceptance or publication is performed." });
                }
                catch (Exception diagnosticError)
                { throw new IOException(save.Rejection, diagnosticError); } // Preserve rejection as primary cause.
            }
            Require(save.Accepted, save.Rejection);
            Write(Path.Combine(root, "acceptance-demand.json"), save.AdmissionDemand);
            if (pressSourceChecked != null) Write(Path.Combine(root, "press-source-check.json"), new {
                checkedQpc = pressSourceChecked, requestedQpc,
                interpretation = "Actual foreground/fixture/health/display rechecked immediately before TrySave timestamp; no file I/O between check and request." });
            if (label is "H02" or "H03") Require(save.AdmissionDemand?.CoversOpenTail == true
                && save.AdmissionDemand.SnapshotPeakAllocatedBytes.HasValue, "Held-out live press did not exercise reserved unfinished-tail acceptance.");
            var saveClock = Stopwatch.StartNew();
            while (!save.Completion.IsCompleted)
            {
                sampler.Check(); Require(saveClock.Elapsed.TotalSeconds < ExportControllerLimit(plan), "Export controller deadline.");
                if(independentPresentation!=null && (cadence==null || cadence.ObservationDue))
                    CalibrationReferencePhase.CheckRecordingDuringSave(((IReplaySessionSource)capture).Stopped,capture.ReadHealth());
                if (!plan.CompositeQualification && !((IReplaySessionSource)capture).Stopped
                    && ((IReplaySessionSource)capture).SaveCutoffAt(Stopwatch.GetTimestamp()) >= (Exact14(plan) || plan.CompositeQualification ? triggerAt + 10 : 310))
                {
                    sampler.MarkCaptureStop(label);
                    StopSource();
                }
                if (!((IReplaySessionSource)capture).Stopped)
                { Require(total.Elapsed.TotalSeconds < AcquisitionSeconds(plan), "Live acquisition exceeded its admitted deadline during save.");
                    if (cadence == null || cadence.ObservationDue)
                    {
                        Require(GetForegroundWindow() == hwnd && !fixture.HasExited, "Source lost during live export.");
                        nativeEvidence?.CaptureClosed(acquiring: true); cadence?.ObservationCompleted();
                    }
                    MaintainCapture(); }
                Thread.Sleep(cadence != null && !((IReplaySessionSource)capture).Stopped ? cadence.DelayMilliseconds : 100);
            }
            var result = save.Completion.GetAwaiter().GetResult();
            CaptureHealthSnapshot healthAtPublication = independentPresentation!=null ? capture.ReadHealth() : null;
            if(independentPresentation!=null && result.Succeeded)
                CalibrationReferencePhase.CheckRecordingAtPublication(((IReplaySessionSource)capture).Stopped,healthAtPress,healthAtPublication,result.Interval.End);
            sampler.MarkCaptureStop(label); StopSource();
            Require(capture.HasExited && capture.CaptureExitCode == 0 && string.IsNullOrWhiteSpace(capture.ErrorTail),
                "Capture did not exit normally with empty diagnostics; source is not eligible.");
            Require(captureStorage?.Stopped != true, "Production storage supervision stopped this held-out source; no qualification.");
            Require(result.Succeeded, result.Error);
            var selection = capture.LastOutputSelection;
            Require(selection.Closed && selection.Inputs.Count >= ReferenceSeconds(plan)-2
                && (plan.CompositeQualification ? capture.LastManagedMux is { Closed: true, InputHashesComplete: true }
                    && capture.LastExportProcesses.All(p => p.Success) : capture.LastExport?.Success == true), "Export selection/process incomplete.");
            double exportSeconds = result.Interval.Seconds;
            Require(Math.Abs(exportSeconds - ReferenceSeconds(plan)) <= 0.000001
                && (plan.CompositeQualification ? result.Validation is { Kind: ReplayOutputIntegrity.Kind, Frames: 0, PublicationIdentityBound: true }
                    : result.Validation?.Kind == "FullDecode" && capture.LastValidation?.Success == true),
                "Requested duration-specific output interval or runtime validation is incomplete.");
            var extent = ReplayStorage.Observe(result.File);
            using (var output = new StreamWriter(new FileStream(Path.Combine(root, "selection.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read)))
                foreach (var input in selection.Inputs)
                {
                    string line = JsonSerializer.Serialize(input); Require(line.Length < 4096, "Selection evidence record too large."); output.WriteLine(line);
                }
            var outputPeaks = sampler.OutputAndWorkspacePeak(result.File);
            var sample = new Sample(label, capture.ReferenceScope, result.File, selection.Inputs.Sum(i => i.Extent.Length), result.Bytes,
                extent.AllocatedBytes, sampler.Peak(result.File), selection.End, exportSeconds, selection.Identity,
                capture.LastOutputDemand, capture.LastExport == null ? null : SaveProcess(root, "main-export", capture.LastExport), capture.ExportArguments, result.Interval,
                capture.LastValidation == null ? null : SaveProcess(root, "runtime-validation", capture.LastValidation), capture.ValidationArguments,
                outputPeaks.Combined, outputPeaks.Work, SaveProcesses(root, "main-export", capture), outputPeaks.Assembly, outputPeaks.Boundaries,
                result.Validation, capture.LastManagedMux, capture.LastIndexBatch, result.Timing, Variant: plan.CompositeQualification ? "packet" : null,
                AcquisitionSelectionHash: pairedInputs?.AcquisitionSelectionHash,
                NativeExport: plan.CompositeQualification ? CompleteNativeExport(result.File, root, true) : null);
            if (sample.RuntimeProcess != null) Write(Path.Combine(root, "runtime-validation-exit.json"), sample.RuntimeProcess);
            if (sample.ManagedExport != null) Write(Path.Combine(root, "managed-export.json"), sample.ManagedExport);
            if (plan.CompositeQualification)
            {
                // Preserve the completed primary before a paired failure can
                // overwrite CaptureSession's last-result diagnostics. This is
                // after CompletionReady and never inside the timed save.
                PublishCreatorProofs(plan, root, label, "packet", capture, exports);
                Write(Path.Combine(root, "primary-normal-sample.json"), new {
                    qualification = "pending-paired-and-independent-inspections", sample, healthAtPress, healthAtPublication });
                Require(cache != null && workerCase == null && ((IReplaySessionSource)capture).Stopped,
                    "Paired output collection requires the stopped original capture and its owned cache.");
                Require(pairedInputs != null, "Primary protected input ownership was not retained.");
                if (independentPresentation != null)
                {
                    // Keep the live fixture through the primary save and recorder drain.
                    // The paired export uses already protected source inputs.
                    var clock = capture.AcquisitionClock;
                    CalibrationReferencePhase.CloseBeforePairedExport(capture.CaptureProducerAbsent, CloseFixture, () =>
                        CalibrationPresentationReference.ValidateClosure(
                            CalibrationPresentationReference.ReadJson<PresentationClosure>(Path.Combine(root, CalibrationPresentationReference.ClosureName)),
                            independentPresentation.Spec, independentPresentation.Hash, clock.QpcEpoch, clock.QpcFrequency));
                }
                string legacyRoot = Path.Combine(root, "legacy"); Directory.CreateDirectory(legacyRoot);
                pairedInputs.PrepareForReplay(capture, () => sampler.Check());
                if (SameCaptureQualification(plan))
                    pairedInputs.WriteCompletedProofMapping(Path.Combine(root, "completed-input-proof.json"), Path.Combine(root, "completed-input-mapping.json"), () => sampler.Check());
                Write(Path.Combine(root, "paired-source-aliases.json"), new { pairedInputs.AcquisitionSelectionHash, aliases = pairedInputs.ReplayAliases }, maximumBytes: 1048576);
                var legacyId = cache.RegisterSession(new ShortSource(capture, ReferenceSeconds(plan), result.Interval.End, exactFallback: true,
                    protect: request => { ObserveNativeExport(request); pairedInputs.Verify(request, capture.ReferenceScope, () => sampler.Check()); },
                    validationContext: validationContext, preserveEvidence: true, inspectPublication: RetainCheckedOutput,
                    protectedSegments: pairedInputs.ReadSegments, qualificationDemand: ContextQualification(plan)
                        ? selection => capture.LastOutputDemand = UnmodelledQualificationDemand(capture.LastOutputSelection = selection, plan, capture.ReferenceScope, capture.DirectoryPath) : null));
                var legacySave = cache.TrySave(legacyId, Stopwatch.GetTimestamp(), settings.OutputDirectory);
                activeSubmission = legacySave;
                Require(legacySave.Accepted, legacySave.Rejection);
                var legacyClock = Stopwatch.StartNew();
                while (!legacySave.Completion.IsCompleted)
                { sampler.Check(); Require(legacyClock.Elapsed.TotalSeconds < ExportControllerLimit(plan), "Paired exact-fallback deadline."); Thread.Sleep(100); }
                var legacy = legacySave.Completion.GetAwaiter().GetResult();
                Require(legacy.Succeeded && legacy.Validation is { Kind: "FullDecode", PublicationIdentityBound: true }
                    && legacy.Timing.ExportRecipe == ReplayExactExport.Recipe && capture.LastValidation?.Success == true
                    && capture.LastOutputSelection.Inputs.Sum(i => i.Extent.Length) == sample.InputBytes,
                    "Paired fallback must preserve the original protected selection and complete its actual full normal decoder: " + legacy.Error);
                var legacyExtent = ReplayStorage.Observe(legacy.File);
                PublishCreatorProofs(plan, root, label, "legacy", capture, exports);
                var legacyPeaks = sampler.OutputAndWorkspacePeak(legacy.File);
                var paired = new Sample(label, capture.ReferenceScope, legacy.File, sample.InputBytes, legacy.Bytes, legacyExtent.AllocatedBytes,
                    sampler.Peak(legacy.File), selection.End, legacy.Interval.Seconds, capture.LastOutputSelection.Identity, capture.LastOutputDemand,
                    SaveProcess(legacyRoot, "main-export", capture.LastExport), capture.ExportArguments, legacy.Interval,
                    SaveProcess(legacyRoot, "runtime-validation", capture.LastValidation), capture.ValidationArguments,
                    legacyPeaks.Combined, legacyPeaks.Work, SaveProcesses(legacyRoot, "main-export", capture), legacyPeaks.Assembly, legacyPeaks.Boundaries,
                    legacy.Validation, null, capture.LastIndexBatch, legacy.Timing, Variant: "legacy",
                    AcquisitionSelectionHash: pairedInputs.AcquisitionSelectionHash,
                    NativeExport: CompleteNativeExport(legacy.File, legacyRoot, false));
                Write(Path.Combine(legacyRoot, "sample.json"), paired);
                Write(Path.Combine(legacyRoot, "runtime-validation-exit.json"), paired.RuntimeProcess);
                sample = sample with { LegacyOutput = paired };
            }
            Write(Path.Combine(root, plan.RetainActiveOutputHandles ? "pre-release-sample.json" : "sample.json"), sample);
            if (label == "T02" && !plan.ProductionQualification && !plan.CompositeQualification)
                foreach (var control in new[] { (Seconds: 1, End: availableStart + 3), (Seconds: 2, End: availableStart + 6) })
                {
                    sampler.Check(32_000_000);
                    sampler.RequireTime(130);
                    var shortId = cache.RegisterSession(new ShortSource(capture, control.Seconds, control.End));
                    var submission = cache.TrySave(shortId, 0, settings.OutputDirectory);
                    Require(submission.Accepted, submission.Rejection);
                    Require(submission.Completion.Wait(130000), "Short control export deadline.");
                    var shortResult = submission.Completion.GetAwaiter().GetResult(); Require(shortResult.Succeeded, shortResult.Error);
                    Write(Path.Combine(root, "control-" + control.Seconds + ".json"), new { shortResult,
                        process = SaveProcess(root, "control-export-" + control.Seconds, capture.LastExport), arguments = capture.ExportArguments,
                        processes = SaveProcesses(root, "control-export-" + control.Seconds, capture),
                        validation = SaveProcess(root, "control-runtime-" + control.Seconds, capture.LastValidation), validationArguments = capture.ValidationArguments });
                }
            nativeEvidence?.Seal(result.File); successful = true; return sample;
        }
        catch (Exception error) { primaryFailure = error; throw; }
        finally
        {
            RecorderRuntime.CaptureArgumentObserver = null; RecorderRuntime.ExportArgumentObserver = null;
            ReplayPacketMuxExport.CompletedInputProofForChecks = null;
            var cleanupErrors = new List<Exception>();
            void Clean(Action action) { try { action(); } catch (Exception error) { cleanupErrors.Add(error); } }
            // Request stop before reporting, owner disposal or cache drain can
            // wait on the observer. Only later confirmed closure releases claims.
            if (primaryFailure != null && capture != null)
                Clean(() => sampler.RequestCaptureStop(label, capture.StopCapture));
            Clean(() => Write(Path.Combine(root, "validation-observer-timings.json"), new {
                qualificationOnly = true, frequency = Stopwatch.Frequency, timings = validationObserverTimings,
                scope = "Actual synchronous qualification observer costs with joined cooperative same-product-guard coverage; all costs remain before decoder launch/publication." }));
            Clean(() => {
                if ((sampler.StopRequested || sampler.Failure != null) && !sampler.ObserverAbsent) ReplayPairedSelectionLease.RetainUntilControllerExit(pairedInputs);
                else if (SameCaptureQualification(plan) && successful)
                {
                    Require(sameCaptureInputOwner == null, "Only one C00 input owner may transfer through manifest/acceptance closure.");
                    sameCaptureInputOwner = pairedInputs;
                }
                else if (capture == null || ((IReplaySessionSource)capture).OutputProducerStopped) pairedInputs?.Dispose();
                else ReplayPairedSelectionLease.RetainUntilControllerExit(pairedInputs);
            });
            Clean(() => {
            bootstrapCancel.Cancel();
            if (capture == null && create != null)
            {
                try { if (create.Wait(15000)) capture = create.GetAwaiter().GetResult(); }
                catch (AggregateException) { } // Constructor already disposed its owned job on failure.
                Require(create.IsCompleted, "Capture bootstrap cleanup unconfirmed; no further stages permitted.");
            }
            });
            Clean(() => {
            if (capture != null)
            {
                sampler.MarkCaptureStop(label);
                capture.StopCapture();
                Require(capture.CaptureProducerAbsent && capture.HasExited && capture.CaptureExitCode.HasValue, "Capture or audio producer absence is unconfirmed; offline work is blocked.");
                if (successful) Require(capture.CaptureExitCode == 0, "Capture did not exit normally.");
                Write(Path.Combine(root, "capture-exit.json"), captureIdentity != null
                    ? CalibrationCaptureEvidence.Closure(captureIdentity, capture.HasExited, capture.CaptureProducerAbsent, capture.CaptureExitCode, capture.ErrorTail)
                    : new { pid = capture.ProcessId, identityUnavailable = true, exited = capture.HasExited, producersAbsent = capture.CaptureProducerAbsent, exit = capture.CaptureExitCode, diagnostics = capture.ErrorTail });
            }
            });
            Clean(() => {
            if (captureStorage != null)
            {
                CompleteStoppedCaptureClaim(sampler, capture?.CaptureProducerAbsent == true,
                    () => captureStorage.CompleteCapture(capture?.CaptureProducerAbsent == true));
                if (successful) Require(!captureStorage.CapturePending && !captureStorage.Stopped && !captureLatch.Read().Held,
                    "Held-out capture claim/guard did not complete normally.");
                captureStorage.Dispose();
            }
            });
            // The application's large-window child timeout is deliberately long.
            // A failed finite experiment must first close its own process job,
            // then join receipts, rather than waiting that production timeout.
            Clean(() => { if (!successful || cleanupErrors.Count != 0) { workerCase?.AbortCapture(); capture?.Dispose(); } });
            Clean(() => { if (workerCase != null) workerCase.Dispose(); else if (cache != null) cache.CloseAsync().GetAwaiter().GetResult(); else capture?.Dispose(); });
            Clean(() => {
                if (primaryFailure == null && cleanupErrors.Count == 0) return;
                // Actual state after the close/drain attempt. Late publication
                // never reverses the original finite-controller failure.
                var completion = activeSubmission?.Completion;
                Write(Path.Combine(root, "failure-context-after-drain.json"), new {
                    primaryFailure = primaryFailure?.ToString(), cleanupErrors = cleanupErrors.Select(e => e.ToString()).ToArray(),
                    completionStatus = completion?.Status.ToString(),
                    actualResult = completion?.IsCompletedSuccessfully == true ? completion.Result : null,
                    producerStopped = capture == null ? (bool?)null : ((IReplaySessionSource)capture).OutputProducerStopped,
                    timing = capture?.LastExportTiming, managed = capture?.LastManagedMux,
                    index = capture?.LastIndexBatch, gate = capture?.LastIntegrityGate,
                    interpretation = "Post-drain diagnostics only; missing/unconfirmed stages stay null. The primary failed experiment remains failed, including any late successful publication."
                });
            });
            Clean(() => {
                // A controller failure can race an active export. Its receipt is
                // available only after that save has drained during close.
                if (capture?.LastExport != null)
                {
                    Write(Path.Combine(root, "last-export-exit.json"), SaveProcess(root, "last-export", capture.LastExport));
                    Write(Path.Combine(root, "last-export-processes.json"), SaveProcesses(root, "last-export", capture));
                    Require(ConfirmedProducerAbsent(capture.LastExport),
                        "Export producer absence is unconfirmed; offline work is blocked.");
                }
            });
            Clean(() => {
                if (capture?.LastValidation != null)
                {
                    Write(Path.Combine(root, "last-validation-exit.json"), SaveProcess(root, "last-validation", capture.LastValidation));
                    Require(ConfirmedProducerAbsent(capture.LastValidation),
                        "Validation producer absence is unconfirmed; offline work is blocked.");
                }
            });
            Clean(() => { if (runtimeOffline) { sampler.EndOffline(); runtimeOffline = false; } });
            Clean(() => nativeEvidence?.WriteTimings());
            Clean(() => {
                if (fixture != null)
                {
                    var exit = CloseFixture();
                    if (successful) CalibrationReferencePhase.RequireNormalFixtureExit(exit);
                }
            });
            if (cadence != null) Clean(() => Write(Path.Combine(root, "maintenance-cadence.json"), new {
                frequency = Stopwatch.Frequency, completionDelayMilliseconds = 250, sourceObservationDelayMilliseconds = 100,
                columns = new[] { "startedQpc", "completedQpc", "errorOrNull" },
                intervals = maintenanceIntervals,
                interpretation = "Qualification caller only;actual dispatch/completionQPC,completion+250ms/no catch-up;independent100ms source/foreground schedule and native Sampler guards retained. Not ordinary worker Saved latency." }, DurationQualification(plan) ? 4194304 : 131072));
            Clean(() => SetForegroundWindow(previous));
            // Preserve the triggering error after stopping owned producers. A
            // cleanup failure must not replace the original diagnostic.
            if (primaryFailure != null)
                Clean(() => Write(Path.Combine(root, "primary-failure.json"), new { error = primaryFailure.ToString() }));
            if (primaryFailure != null && cleanupErrors.Count != 0) cleanupErrors.Insert(0, primaryFailure);
            if (cleanupErrors.Count != 0) throw new AggregateException("Owned source/export/worker cleanup failed; retain all evidence and do not continue.", cleanupErrors);
        }
    }


    sealed record Inspection(ReplayExportProcessReceipt Process, int[] Ids, string[] Hashes, double AudioRmsDb, ReplayFileExtent CheckedOutput = null);

    static ReplayCalibrationSourceReceipt QualificationSource(Sample sample, Inspection inspection, string root, Sampler sampler)
    {
        string acquisition = sample.Variant == "legacy" ? Path.GetDirectoryName(root) : root;
        using var growth = JsonDocument.Parse(File.ReadAllText(Path.Combine(acquisition, "capture-growth.json")));
        Require(growth.RootElement.GetProperty("ObservationMilliseconds").GetInt32() == ReplayCapacityPolicy.CoverageMilliseconds,
            "Raw capture growth has no matching observation horizon.");
        return QualificationSource(sample, inspection, root, sampler.CaptureMetrics(sample.Label), sampler.MaximumActiveGap,
            growth.RootElement.GetProperty("MaximumCapacityGap").GetDouble());
    }

    static ReplayCalibrationSourceReceipt QualificationSource(Sample sample, Inspection inspection, string root,
        (long PollGrowth, long StopGrowth, string Volume) growth, double activeGap, double capacityGap, ReplayCalibrationSourceReceipt original = null)
    {
        string samplePath = Path.Combine(root, "sample.json");
        string acquisitionRoot = sample.Variant == "legacy" ? Path.GetDirectoryName(root) : root;
        string capturePath = Path.Combine(acquisitionRoot, "capture-exit.json");
        string sourcePath = Path.Combine(acquisitionRoot, "fixture-exit.json");
        using var capture = original == null ? JsonDocument.Parse(File.ReadAllText(capturePath)) : null;
        using var source = original == null ? JsonDocument.Parse(File.ReadAllText(sourcePath)) : null;
        var c = capture?.RootElement ?? default; var f = source?.RootElement ?? default;
        if (original == null)
        {
        Require(c.GetProperty("pid").GetInt32() > 0 && c.GetProperty("startedUtc").GetDateTime().Kind == DateTimeKind.Utc
            && c.GetProperty("startedUtc").GetDateTime() > DateTime.UnixEpoch
            && Path.GetFullPath(c.GetProperty("sessionDirectory").GetString()).StartsWith(Path.Combine(acquisitionRoot, "buffer") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "Capture closure is missing its independent actual-process/session identity.");
        Require(c.GetProperty("exited").GetBoolean() && c.GetProperty("producersAbsent").GetBoolean() && c.GetProperty("exit").GetInt32() == 0
            && f.GetProperty("exited").GetBoolean() && f.GetProperty("exit").GetInt32() == 0 && !f.GetProperty("forced").GetBoolean(),
            "Reference producers did not exit normally before offline inspection.");
        }
        var export = JsonSerializer.SerializeToElement(sample.Process);
        var runtime = JsonSerializer.SerializeToElement(sample.RuntimeProcess);
        bool packet = sample.Variant == "packet";
        bool exportNormal = packet && sample.ManagedExport is { Closed: true, InputHashesComplete: true }
            || export.ValueKind == JsonValueKind.Object && export.GetProperty("ProducerAbsent").GetBoolean() && export.GetProperty("ActualExitObserved").GetBoolean()
            && export.GetProperty("ExitCode").GetInt32() == 0 && !export.GetProperty("TimedOut").GetBoolean()
            && !export.GetProperty("OutputLimitExceeded").GetBoolean() && export.GetProperty("CaptureComplete").GetBoolean();
        bool runtimeNormal = packet && sample.NormalValidation is { Kind: ReplayOutputIntegrity.Kind, Frames: 0, PublicationIdentityBound: true }
            || runtime.ValueKind == JsonValueKind.Object && runtime.GetProperty("ProducerAbsent").GetBoolean() && runtime.GetProperty("ActualExitObserved").GetBoolean()
            && runtime.GetProperty("ExitCode").GetInt32() == 0 && !runtime.GetProperty("TimedOut").GetBoolean()
            && !runtime.GetProperty("OutputLimitExceeded").GetBoolean() && runtime.GetProperty("CaptureComplete").GetBoolean()
            && runtime.GetProperty("ProcessId").GetInt32() != export.GetProperty("ProcessId").GetInt32();
        Require(exportNormal && runtimeNormal && inspection.Process.Success, "Export/runtime-validation/inspection process evidence is incomplete.");
        var children = ExportChildren(sample);
        int segments = File.ReadLines(Path.Combine(acquisitionRoot, "selection.jsonl")).Count();
        var decode = inspection.Process;
        ReplayCalibrationProcessReceipt runtimeReceipt = packet ? null : new(HashFile(Path.Combine(root, "runtime-validation-exit.json")),
            runtime.GetProperty("ProducerAbsent").GetBoolean(), runtime.GetProperty("ActualExitObserved").GetBoolean(), runtime.GetProperty("ExitCode").GetInt32(),
            runtime.GetProperty("TimedOut").GetBoolean(), runtime.GetProperty("OutputLimitExceeded").GetBoolean(), false,
            runtime.GetProperty("ProcessId").GetInt32(), runtime.GetProperty("ProcessStartedUtc").GetDateTime().ToUniversalTime().Ticks);
        ReplayCalibrationManagedReceipt managedReceipt = null;
        if (packet)
        {
            var managed = sample.ManagedExport; var index = sample.IndexBatch; var timing = sample.Timing;
            Require(index != null && timing != null, "Actual candidate indexing/lifetime evidence is absent.");
            managedReceipt = new(HashFile(Path.Combine(root, "managed-export.json")), managed.ParentPid, managed.ParentStartedUtc.ToUniversalTime().Ticks,
                managed.Closed, managed.InputHashesComplete, managed.State, managed.FinalOutput,
                new(timing.ExportConstruction, timing.AsynchronousInputs, index.Workers, index.PeakActive,
                    index.FinishedSources, index.UnsupportedSources, index.Joined, index.CancellableReads, index.ReadDeadlineMilliseconds,
                    index.DeadlineExceeded, index.DrainError, timing.SourceHandlesClosed == true && timing.SourceCloseCompleted.HasValue,
                    new(timing.PreparedOwnedInputs,timing.PreparedIndexHits,timing.PreparedIndexMisses,
                        timing.IndexAcquisitionComplete,timing.IndexReadersStarted,timing.IndexReadersJoined)));
        }
        return new(sample.Label, original?.AcquisitionIdentity ?? ReplayReferenceModel.Hash(new { sample.Scope, capture = HashFile(capturePath), source = HashFile(sourcePath) }),
            HashFile(samplePath), sample.Scope, ReplayStorage.ObserveCapacity(root).Volume,
            ReplayStorage.ObserveCapacity(root).AllocationUnit, sample.InputBytes, sample.FinalBytes, sample.FinalAllocation,
            sample.PeakAllocation, sample.ExportSeconds, segments, true,
            original?.Capture ?? new(HashFile(capturePath), true, true, 0, false, false, false, c.GetProperty("pid").GetInt32(), c.GetProperty("startedUtc").GetDateTime().Ticks),
            original?.Source ?? new(HashFile(sourcePath), true, true, 0, false, false, false, f.GetProperty("pid").GetInt32(),
                f.TryGetProperty("startedUtc", out var fixtureBirth) ? fixtureBirth.GetDateTime().Ticks : null),
            children.LastOrDefault(),
            new(HashFile(Path.Combine(root, Path.GetFileName(sample.File) + ".decode.json")),
                decode.ProducerAbsent, decode.ActualExitObserved, decode.ExitCode ?? -1, decode.TimedOut, decode.OutputLimitExceeded, !decode.Success,
                decode.ProcessId, decode.ProcessStartedUtc?.ToUniversalTime().Ticks),
            sample.AcquisitionSelectionHash ?? sample.SelectionIdentity, original?.CaptureArgumentsHash ?? HashFile(Path.Combine(acquisitionRoot, "capture-arguments.json")),
            ReplayReferenceModel.Hash(sample.Arguments ?? []), HashFile(Path.Combine(root, Path.GetFileName(sample.File) + ".source-pictures.json")),
            true, true, true, true, true, activeGap, inspection.Ids, inspection.Hashes, inspection.AudioRmsDb,
            1920, 1080, 48000, 2, growth.PollGrowth, growth.StopGrowth, growth.Volume,
            runtimeReceipt, packet ? null : ReplayReferenceModel.Hash(sample.RuntimeArguments), sample.Interval.Start, sample.Interval.End,
            sample.CombinedPeakAllocation, sample.WorkPeakAllocation, sample.AssemblyPeakAllocation, sample.BoundaryPeakAllocations, children,
            sample.NormalValidation, inspection.CheckedOutput, managedReceipt, sample.SelectionIdentity, sample.NativeExport,
            ReplayCapacityPolicy.CoverageMilliseconds, capacityGap);
    }

    internal static int[] InspectionFrames(double seconds)
    {
        Require(double.IsFinite(seconds) && seconds is >= 0.05 and <= 1800, "Inspection duration cannot supply three reference frames.");
        int frames = (int)Math.Floor(seconds * 60), inset = Math.Min(15, frames / 4);
        int[] selected = [inset, frames / 2, Math.Min(frames - 1, frames - inset)];
        Require(selected[0] < selected[1] && selected[1] < selected[2] && selected[2] < frames,
            "Inspection frame selection is not three distinct in-range frames.");
        return selected;
    }

    static Inspection Inspect(string engine, string root, string file, double seconds, string label, int timeoutSeconds,
        ReplayPublicationLease retainedOutput = null, CalibrationPlan creatorPlan = null)
    {
        var input = new ReplayDecodeLease(file, retainedOutput); bool absent = true;
        try
        {
        string[] args = InspectionArguments(root, file, seconds);
        Write(Path.Combine(root, Path.GetFileName(file) + ".decode-arguments.json"), args);
        AdmitNativeChild("offline-inspection:" + label);
        var receipt = ReplayExportProcess.Run(engine, args, root, null, producerStopped: stopped => absent = stopped,
            timeoutMilliseconds: checked(timeoutSeconds * 1000), inputLease: input);
        PublishInspectionCreatorProof(creatorPlan, root, label, Path.Combine(root, Path.GetFileName(file) + ".decode-arguments.json"), args, receipt);
        Write(Path.Combine(root, Path.GetFileName(file) + ".decode.json"), SaveProcess(root, Path.GetFileName(file) + ".decode", receipt));
        object frameContract = null;
        if (creatorPlan != null && creatorPlan.FreshCandidateAdmission is not (RetainedCoverageAdmission or SameCaptureRetainedAdmission))
        {
            string fixtureRoot = Path.Combine(creatorPlan.Root, label);
            using var fixtureReady = CalibrationFixturePublication.Read(fixtureRoot);
            var source = fixtureReady.RootElement;
            frameContract = new { lifetimeSeconds = source.GetProperty("lifetimeSeconds").GetInt32(),
                maximumAnimatedFrame = source.GetProperty("maximumAnimatedFrame").GetInt32(),
                barcodeFrameDivisor = source.GetProperty("barcodeFrameDivisor").GetInt32(),
                timerIntervalMilliseconds = source.GetProperty("timerIntervalMilliseconds").GetInt32(),
                fixtureReadySha256 = HashFile(Path.Combine(fixtureRoot, CalibrationFixturePublication.ReadyName)) };
        }
        int sourceIdRange=creatorPlan?.FreshCandidateAdmission is SameCaptureContextAdmission or SameCaptureRetainedAdmission or FiveMinuteStorageAdmission
            ? CalibrationPattern.ReferenceSourceIdRange : CalibrationPattern.SourceIdRange;
        return ReadInspectionEvidence(receipt, root, file, seconds, label, input.CheckedIdentity, true, input.VerifyUnchanged, frameContract,sourceIdRange,
            (SameCaptureQualification(creatorPlan) || creatorPlan?.FreshCandidateAdmission == SameCaptureRetainedAdmission) ? HashFile(file) : null);
        }
        finally { if (absent) input.Dispose(); }
    }

    // Re-read bound genuine decoder receipts/raw samples without inventing or
    // launching a new decoder when continuing an interrupted training phase.
    static Inspection ReadInspectionEvidence(ReplayExportProcessReceipt receipt, string root, string file, double seconds,
        string label, ReplayFileExtent checkedOutput, bool persistPictures, Action verify = null, object frameContract = null,
        int sourceIdRange=CalibrationPattern.SourceIdRange, string inspectedFileSha256=null)
    {
        int[] frames = InspectionFrames(seconds);
        string evidence = Path.Combine(root, Path.GetFileName(file));
        string barcode = evidence + ".barcodes.raw", pictures = evidence + ".pictures.raw";
        Require(receipt.Success && receipt.StandardError.Contains("1920x1080") && receipt.StandardError.Contains("48000 Hz") && receipt.StandardError.Contains("stereo"), "All-track full decode or reference stream format failed.");
        var decoded = ReplayExactExport.ReadValidation(receipt, new(0, seconds, Math.Max(1, (int)Math.Round(seconds))), 60);
        Require(decoded.Frames == (long)Math.Round(seconds * 60), "Independent qualification requires the exact requested physical frame count.");
        verify?.Invoke();
        Require(receipt.StandardError.Contains("RMS level dB:") && !receipt.StandardError.Contains("RMS level dB: -inf"), "Actual loopback audio is absent/silent; not an eligible reference source.");
        byte[] codes = File.ReadAllBytes(barcode), pixels = File.ReadAllBytes(pictures);
        Require(codes.Length == 144 && pixels.Length == 3 * 64 * 36 * 3, "Sampled source pictures missing.");
        int baseId = (label.StartsWith('T') ? 13579 : 24680) * 256;
        var ids = new int[3]; var hashes = new string[3];
        for (int sample = 0; sample < 3; sample++)
        {
            for (int bit = 0; bit < 24; bit++)
            {
                byte value = codes[sample * 48 + bit], inverse = codes[sample * 48 + 24 + bit];
                Require(value is <= 70 or >= 180 && inverse is <= 70 or >= 180 && (value >= 180) != (inverse >= 180), "Source barcode/complement ambiguous.");
                if (value >= 180) ids[sample] |= 1 << bit;
            }
            hashes[sample] = Convert.ToHexString(SHA256.HashData(pixels.AsSpan(sample * 6912, 6912)));
            Require(sourceIdRange is >0 and <=CalibrationPattern.ReferenceSourceIdRange && ids[sample] >= baseId && ids[sample] < baseId + sourceIdRange, "Decoded source ID has wrong seed/workload lineage.");
        }
        if (label.EndsWith('1')) Require(ids.All(id => id == baseId), "Static source changed its ID.");
        else Require(ids[0] < ids[1] && ids[1] < ids[2] && hashes.Distinct().Count() >= 2, "Motion/cut source samples did not progress.");
        if (persistPictures)
        {
            var proof = JsonSerializer.SerializeToNode(new { ids, hashes, sampleFrames = frames, frameContract,
                limits = "Three sampled source IDs/complement rows and coarse images; no full picture cadence or A/V alignment claim." });
            if (inspectedFileSha256 != null) proof["inspectedFileSha256"] = inspectedFileSha256;
            Write(evidence + ".source-pictures.json", proof);
        }
        var audio = Regex.Matches(receipt.StandardError, @"RMS level dB:\s*([-+]?[0-9]+(?:\.[0-9]+)?)")
            .Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToArray();
        Require(audio.Length > 0 && audio.All(double.IsFinite), "Reference RMS evidence is unavailable.");
        return new(receipt, ids, hashes, audio.Max(), checkedOutput);
    }

    internal static int InspectExisting(string path, string expectedPlanHash)
    {
        Require(new FileInfo(path).Length <= 65536 && HashFile(path) == expectedPlanHash, "Offline inspection plan identity mismatch.");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var p = document.RootElement;
        Require(p.GetProperty("kind").GetString() == "two-retained-short-controls", "Unknown offline inspection scope.");
        string engine = p.GetProperty("engine").GetString(), root = p.GetProperty("root").GetString();
        double retainedWall = p.GetProperty("retainedWallSeconds").GetDouble(), retainedOffline = p.GetProperty("retainedOfflineSeconds").GetDouble();
        int retainedChildren = p.GetProperty("retainedChildren").GetInt32();
        Require(double.IsFinite(retainedWall) && retainedWall >= 0 && retainedWall + 60 <= 9000
            && double.IsFinite(retainedOffline) && retainedOffline >= 0 && retainedOffline <= retainedWall && retainedOffline + 60 <= 1800
            && retainedChildren >= 0 && retainedChildren + 2 <= 57, "Offline inspection cannot fit existing resource allowance.");
        Require(HashFile(engine) == p.GetProperty("engineSha256").GetString(), "Reviewed engine changed.");
        MediaDependency.RootDirectory = Path.GetFullPath(p.GetProperty("engineRoot").GetString());
        Require(MediaDependency.ResolveExecutable().Equals(Path.GetFullPath(engine), StringComparison.OrdinalIgnoreCase), "Offline inspection engine is outside the verified managed package.");
        var cases = p.GetProperty("cases").EnumerateArray().ToArray();
        Require(cases.Length == 2 && cases.Select(c => Path.GetFullPath(c.GetProperty("file").GetString())).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2,
            "Offline correction needs exactly two distinct retained short exports.");
        foreach (var c in cases)
        {
            string file = c.GetProperty("file").GetString();
            Require(c.GetProperty("label").GetString() == "T02" && c.GetProperty("seconds").GetDouble() is >= 0.05 and <= 2.1
                && Path.GetExtension(file).Equals(".mp4", StringComparison.OrdinalIgnoreCase)
                && new FileInfo(file).Length is > 0 and <= 10_000_000 && HashFile(file) == c.GetProperty("sha256").GetString(),
                "Retained short export binding or duration is invalid.");
        }
        Require(ReplayStorage.ObserveCapacity(Path.GetDirectoryName(Path.GetFullPath(root))).AvailableBytes >= 61_256_000_000,
            "Offline inspection free-space reserve is absent.");
        using var owned = PilotStorageObservation.Create(root);
        Write(Path.Combine(root, "admitted-plan.json"), new { planHash = expectedPlanHash, plan = p.Clone(), maximumChildren = 2,
            limits = "Two retained MP4s read only; no capture/UI/audio playback/GPU. New raw outputs under1MB; child20sec, controller60sec. Failed batch remains failed." });
        var timer = Stopwatch.StartNew(); var results = new List<object>();
        try
        {
            foreach (var c in cases)
            {
                Require(timer.Elapsed.TotalSeconds + 20 <= 60, "Complete remaining decode child cannot fit offline correction deadline.");
                var inspection = Inspect(engine, root, c.GetProperty("file").GetString(), c.GetProperty("seconds").GetDouble(), "T02", 20);
                results.Add(new { file = c.GetProperty("file").GetString(), inspection.Process, inspection.Ids, inspection.Hashes, inspection.AudioRmsDb });
            }
            Write(Path.Combine(root, "result.json"), new { result = "passed-two-retained-short-inspections", elapsedSeconds = timer.Elapsed.TotalSeconds,
                results, failedBatchRemainsFailed = true, modelActivated = false });
            Console.WriteLine("Both retained short inspections passed: " + root); return 0;
        }
        catch (Exception ex)
        {
            Write(Path.Combine(root, "failure.json"), new { error = ex.ToString(), elapsedSeconds = timer.Elapsed.TotalSeconds, results });
            Console.Error.WriteLine(ex.Message); return 1;
        }
    }

    internal static int Run(string path, string expectedPlanHash, CalibrationAdmissionMode admissionMode = CalibrationAdmissionMode.Capture)
    {
        var preflight = Stopwatch.StartNew();
        JsonDocument document;
        CalibrationPlan plan;
        string engine;
        try
        {
            document = ReadFinalAdmission(path, expectedPlanHash);
            try { plan = ValidateFinalAdmission(path, expectedPlanHash, document, out engine); }
            catch { document.Dispose(); throw; }
            if (admissionMode == CalibrationAdmissionMode.ValidateOnly)
            {
                using (document) WriteFinalAdmissionReceipt(path, expectedPlanHash, document);
                return 0;
            }
            try { RequireFinalAdmissionReceipt(path, expectedPlanHash, document); }
            catch { document.Dispose(); throw; }
            if (admissionMode == CalibrationAdmissionMode.CheckLaunch)
            {
                document.Dispose();
                Console.WriteLine("Passed launch admission; source/device/capture boundary was not entered.");
                return 0;
            }
        }
        catch (Exception error) { return ReportStartupFailure(path, expectedPlanHash, "final-plan-admission", error); }
        using var documentOwner = document;
        try { RequireRecordingAuthorization(path, expectedPlanHash, document); }
        catch (Exception error) { return ReportStartupFailure(path, expectedPlanHash, "recording-authorization", error); }
        try
        {
        using (var currentEndpoint = new LoopbackAudio())
        {
            var prior = document.RootElement.GetProperty("environment");
            var bounds = Forms.Screen.PrimaryScreen.Bounds;
            Require(bounds.Width == 1920 && bounds.Height == 1080 && prior.GetProperty("os").GetString() == Environment.OSVersion.VersionString
                && prior.GetProperty("machine").GetString() == Environment.MachineName
                && prior.GetProperty("EndpointId").GetString() == currentEndpoint.EndpointId
                && prior.GetProperty("SampleRate").GetInt32() == currentEndpoint.SampleRate
                && prior.GetProperty("Channels").GetInt32() == currentEndpoint.Channels
                && prior.GetProperty("FfmpegFormat").GetString() == currentEndpoint.FfmpegFormat, "Host/display/audio binding changed after review.");
            var capacity = ReplayStorage.ObserveCapacity(Path.GetDirectoryName(plan.Root));
            Require(capacity.Volume == prior.GetProperty("capacity").GetProperty("Volume").GetString()
                && capacity.AllocationUnit == prior.GetProperty("capacity").GetProperty("AllocationUnit").GetInt64(), "Volume binding changed after review.");
            Require(new DriveInfo(Path.GetPathRoot(plan.Root)).DriveFormat == "NTFS", "Only local NTFS is qualified by this run.");
        }
        }
        catch (Exception error) { return ReportStartupFailure(path, expectedPlanHash, "current-display-audio-volume", error); }
        MediaDependency.ExecutableLease engineLease = null;
        PilotStorageObservation owned = null;
        try
        {
            engineLease = MediaDependency.OpenVerifiedExecutable(engine);
            owned = PilotStorageObservation.Create(plan.Root);
            Log.DirectoryPath = plan.Root;
            Write(Path.Combine(plan.Root, "admitted-plan.json"), new { plan, planHash = expectedPlanHash, effects = Effects(plan), recipe = Recipe(plan), authorization = "External task approval is required; the plan hash alone is not execution authorization." });
        }
        catch (Exception error)
        {
            try { owned?.Dispose(); } catch (Exception cleanup) { Console.Error.WriteLine("Root startup cleanup: " + cleanup.Message); }
            try { engineLease?.Dispose(); } catch (Exception cleanup) { Console.Error.WriteLine("Engine lease cleanup: " + cleanup.Message); }
            return ReportStartupFailure(path, expectedPlanHash, "collector-resource-startup", error);
        }
        using var engineOwner = engineLease;
        using var rootOwner = owned;
        Sampler sampler;
        try { sampler = new Sampler(plan with { RetainedElapsedSeconds = plan.RetainedElapsedSeconds + preflight.Elapsed.TotalSeconds }, active: false); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Write(Path.Combine(plan.Root, "failure.json"), new { result = "failed-no-retry", phase = "pre-capture-history-admission",
                error = error.ToString(), planHash = expectedPlanHash, mediaChildren = 0, captureStarted = false, seconds = preflight.Elapsed.TotalSeconds });
            Console.Error.WriteLine(error.Message + " Evidence: " + plan.Root); return 1;
        }
        using var samplerOwner = sampler;
        if (Exact14(plan) || plan.CompositeQualification) { nativeChildRoot = plan.Root; nativeChildren = plan.RetainedChildren; nativeChildLimit = plan.MaximumChildren; AdmitNativeChild("collector"); }
        var samples = new List<Sample>(); var processes = new List<object>();
        var qualifications = new List<ReplayCalibrationSourceReceipt>();
        var legacyQualifications = new List<ReplayCalibrationSourceReceipt>();
        SameOracleResult sameOracle = null;
        var qualificationOutputs = new Dictionary<string, ReplayPublicationLease>(StringComparer.OrdinalIgnoreCase);
        ReplayPublicationLease TakeOutput(string file)
        {
            if (!plan.CompositeQualification) return null;
            Require(qualificationOutputs.Remove(file, out var lease), "Independent decoder lost continuous normal-output ownership.");
            return lease;
        }
        ReplayReferenceModel model = null;
        var scoped = FiveMinuteStorage(plan) ? ReadFiveMinuteContract(plan) : null;
        var contextFreeze = ContextQualification(plan) && scoped == null ? ReadContextFreeze(plan) : null;
        DateTime confirmationStarted = DateTime.UtcNow;
        if (CapacityTrainingContinuation(plan) || DurationTrainingContinuation(plan))
        {
            var retained = DurationTrainingContinuation(plan) ? ReadCompleteDurationTraining(plan) : ReadCompleteCapacityTraining(plan);
            samples.AddRange(retained.Samples); qualifications.AddRange(retained.Primary); legacyQualifications.AddRange(retained.Legacy);
            model = retained.Model;
            File.Copy(Path.Combine(plan.CompositeTrainingRoot, "frozen-training-model.json"), Path.Combine(plan.Root, "frozen-training-model.json"));
        }
        else if (plan.CompositeTrainingRoot != null)
            foreach (var retained in ReadCompositeTraining(plan))
            { samples.Add(retained.Sample); qualifications.Add(retained.Primary); legacyQualifications.Add(retained.Legacy); }
        if (plan.ProductionTrainingRoot != null)
            foreach (var retained in ReadProductionTraining(plan.ProductionTrainingRoot, plan.Root))
            { samples.Add(retained.Sample); qualifications.Add(retained.Receipt); }
        if (ExactContinuation14(plan))
        {
            model = FrozenContinuationModel(plan);
            foreach (string label in Order.Take(3))
            {
                samples.Add(JsonSerializer.Deserialize<Sample>(File.ReadAllText(Path.Combine(plan.ExactContinuationRoot, label, "sample.json"))));
                qualifications.Add(JsonSerializer.Deserialize<ReplayCalibrationSourceReceipt>(File.ReadAllText(Path.Combine(plan.ExactContinuationRoot, label, "qualification-source.json"))));
            }
            if (ExactFinalWorker14(plan))
                foreach (string label in new[] { "H02", "H01" })
                {
                    samples.Add(JsonSerializer.Deserialize<Sample>(File.ReadAllText(Path.Combine(plan.ExactHeldoutContinuationRoot, label, "sample.json"))));
                    qualifications.Add(JsonSerializer.Deserialize<ReplayCalibrationSourceReceipt>(File.ReadAllText(Path.Combine(plan.ExactHeldoutContinuationRoot, label, "qualification-source.json"))));
                }
        }
        try
        {
                if (plan.ProductionQualification || ExactContinuation14(plan) || Exact14(plan) && (plan.MaximumChildren == 97 && plan.RetainedChildren == 60 || plan.MaximumChildren == 105 && plan.RetainedChildren == 68))
                    ReuseIssue14EngineControls(plan);
                else
                {
                    sampler.RequireTime(40); sampler.BeginActive();
                    Controls(Path.Combine(plan.Root, "controls"), plan.EngineRoot);
                    sampler.EndActive();
                }
            foreach (string label in plan.Order)
            {
                Action<int, IntPtr> focus = null;
                if ((ExactContinuation14(plan) || plan.ProductionQualification) && plan.OperatorForegroundHandoff || plan.MaximumChildren is 513 or 545 or 577 or 654 or 706 or 738 or 806)
                {
                    // Normal desktop activation completes before capture/bootstrap.
                    // Keep the same bounded handshake already used by continuation.
                    Write(Path.Combine(plan.Root, "operator-ready-" + label + ".json"), new { label, noCaptureAdmitted = true });
                    var wait = Stopwatch.StartNew();
                    while (!plan.AutomaticFixtureHandoff && !File.Exists(Path.Combine(plan.Root, "operator-launch-" + label + ".json")))
                    { sampler.Check(); Require(wait.Elapsed.TotalSeconds < 60, "Operator source-launch coordination deadline; no capture admitted."); Thread.Sleep(50); }
                    if (plan.AutomaticFixtureHandoff)
                        Write(Path.Combine(plan.Root, "automatic-source-launch-" + label + ".json"), new { label, authorizedByReviewedPlan = true, actualForegroundStillRequired = true });
                    focus = (pid, hwnd) =>
                    {
                        Write(Path.Combine(plan.Root, "operator-focus-ready-" + label + ".json"), new { label, pid, hwnd = hwnd.ToInt64(), noCaptureAdmitted = true });
                        if (plan.ProductionQualification)
                        {
                            Native.GetWindowThreadProcessId(hwnd, out uint owned);
                            Require(owned == pid && Native.IsWindowVisible(hwnd), "Only the verified visible owned fixture may be activated.");
                            Native.ShowWindow(hwnd, 5);
                            bool requested = SetForegroundWindow(hwnd);
                            Write(Path.Combine(plan.Root, "operator-activation-" + label + ".json"), new { label, pid,
                                hwnd = hwnd.ToInt64(), requested, actualForeground = GetForegroundWindow() == hwnd,
                                meaning = "One normal owned-window activation request before acquisition; actual foreground is checked independently. No focus-policy bypass or injected input." });
                        }
                        var activation = Stopwatch.StartNew();
                        if (plan.UserForegroundHandoff)
                            while ((!plan.AutomaticFixtureHandoff && !File.Exists(Path.Combine(plan.Root, "operator-focused-" + label + ".json"))) || GetForegroundWindow() != hwnd)
                            {
                                sampler.Check(); Native.GetWindowThreadProcessId(hwnd, out uint owner);
                                Require(owner == pid && Native.IsWindowVisible(hwnd) && activation.Elapsed.TotalSeconds < 60,
                                    "User foreground handoff expired; no capture admitted.");
                                Thread.Sleep(50);
                            }
                        if (plan.UserForegroundHandoff)
                            Require(activation.Elapsed.TotalSeconds < 60, "User foreground handoff expired; no capture admitted.");
                        double userFocusMilliseconds = plan.UserForegroundHandoff ? activation.Elapsed.TotalMilliseconds : 0;
                        activation.Restart();
                        CalibrationForeground.Wait(() => (plan.AutomaticFixtureHandoff || File.Exists(Path.Combine(plan.Root, "operator-focused-" + label + ".json"))) && GetForegroundWindow() == hwnd,
                            () => { Native.GetWindowThreadProcessId(hwnd, out uint owner); return owner != pid; },
                            () => sampler.Check(), () => Thread.Sleep(25), () => activation.Elapsed.TotalSeconds);
                        Write(Path.Combine(plan.Root, "operator-handoff-" + label + ".json"), new { label, pid, hwnd = hwnd.ToInt64(),
                            activationCompleteBeforeBootstrap = true, waitedMilliseconds = activation.Elapsed.TotalMilliseconds, userFocusMilliseconds });
                    };
                }
                sampler.BeginActive();
                var sample = Collect(plan, label, sampler, model, processes, focus, qualificationOutputs);
                if (sampler.ActiveCoverage) sampler.EndActive();
                var captureGrowth = sampler.CaptureMetrics(label);
                if (scoped != null) sampler.WriteFiveMinuteGrowth(Path.Combine(plan.Root, label));
                Write(Path.Combine(plan.Root, label, "capture-growth.json"), new {
                    captureGrowth.PollGrowth, captureGrowth.StopGrowth, captureGrowth.Volume,
                    sampler.MaximumActiveGap, sampler.MaximumCapacityGap, ObservationMilliseconds = ReplayCapacityPolicy.CoverageMilliseconds,
                    meaning = "Completed per-source allocation observations, persisted before later inspection/report assembly. Not qualification by itself." });
                if (plan.RetainActiveOutputHandles)
                {
                    sampler.BeginOffline("output-release", 0, 30);
                    string cell = Path.Combine(plan.Root, label);
                    var finals = new List<string> { sample.File };
                    if (ExactContinuation14(plan) || plan.ProductionQualification) finals = Directory.GetFiles(cell, "*.mp4", SearchOption.AllDirectories).ToList();
                    foreach (string control in Directory.GetFiles(cell, "control-?.json"))
                    {
                        using var entry = JsonDocument.Parse(File.ReadAllText(control));
                        finals.Add(entry.RootElement.GetProperty("shortResult").GetProperty("File").GetString());
                    }
                    Require((ExactContinuation14(plan) || plan.ProductionQualification ? finals.Count is >= 2 and <= 16 : finals.Count == (label == "T02" ? 3 : 1)) && finals.Distinct(StringComparer.OrdinalIgnoreCase).Count() == finals.Count
                        && finals.All(file => Path.GetFullPath(file).StartsWith(cell + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                            && file.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) && !file.EndsWith(".partial.mp4", StringComparison.OrdinalIgnoreCase) && File.Exists(file)),
                        "Release must cover exactly this case's declared main/short published files, including worker application folders.");
                    var releases = sampler.ReleaseClosedOutputHandles(finals);
                    Require(releases.All(release => release.MetadataHandleAbsent && release.Before.Identity == release.After.Identity
                        && release.Before.Length == release.After.Length && release.After.AllocatedBytes <= release.Before.AllocatedBytes),
                        "Output metadata release changed bytes/identity or increased final allocation.");
                    var main = releases.Single(release => string.Equals(release.Path, sample.File, StringComparison.OrdinalIgnoreCase));
                    sample = sample with { FinalAllocation = main.After.AllocatedBytes,
                        PeakAllocation = Math.Max(sample.PeakAllocation, main.Before.AllocatedBytes) };
                    Write(Path.Combine(plan.Root, label, "output-handle-release.json"), new { releases, operations = sampler.OutputOperationTimings });
                    Write(Path.Combine(plan.Root, label, "sample.json"), sample);
                    sampler.EndOffline();
                }
                samples.Add(sample);
                sampler.BeginOffline("inspection", 32_000_000, MainInspectionLimit(plan));
                // Capture has stopped before decoding. Held-out observations never fit the model.
                string caseRoot = Path.Combine(plan.Root, label);
                var inspection = Inspect(engine, caseRoot, sample.File, sample.ExportSeconds, label, MainInspectionLimit(plan) - InspectionCleanupSeconds, TakeOutput(sample.File), plan);
                sampler.EndOffline();
                foreach (string control in Directory.GetFiles(caseRoot, "control-?.json"))
                {
                    using var entry = JsonDocument.Parse(File.ReadAllText(control));
                    var arguments = entry.RootElement.GetProperty("arguments").EnumerateArray().Select(v => v.GetString()).ToArray();
                    sampler.BeginOffline("short-inspection", 4_000_000, ShortInspectionSeconds);
                    Inspect(engine, caseRoot, entry.RootElement.GetProperty("shortResult").GetProperty("File").GetString(),
                        entry.RootElement.GetProperty("shortResult").GetProperty("Interval").GetProperty("End").GetDouble()
                            - entry.RootElement.GetProperty("shortResult").GetProperty("Interval").GetProperty("Start").GetDouble(), label, ShortInspectionSeconds - InspectionCleanupSeconds);
                    sampler.EndOffline();
                }
                qualifications.Add(QualificationSource(sample, inspection, caseRoot, sampler));
                Write(Path.Combine(caseRoot, "qualification-source.json"), qualifications[^1]);
                if (plan.CompositeQualification)
                {
                    var legacy = sample.LegacyOutput ?? throw new IOException("Paired legacy sample is missing.");
                    sampler.BeginOffline("legacy-inspection", 32_000_000, MainInspectionLimit(plan));
                    string legacyRoot = Path.Combine(caseRoot, "legacy");
                    var legacyInspection = Inspect(engine, legacyRoot, legacy.File, legacy.ExportSeconds, label, MainInspectionLimit(plan) - InspectionCleanupSeconds, TakeOutput(legacy.File), plan);
                    sampler.EndOffline();
                    legacyQualifications.Add(QualificationSource(legacy, legacyInspection, legacyRoot, sampler));
                    Write(Path.Combine(legacyRoot, "qualification-source.json"), legacyQualifications[^1]);
                }
                if (SameCaptureQualification(plan))
                    sameOracle = CollectSameOracle(plan, engine, [qualifications[^1],legacyQualifications[^1]],
                        [sample,sample.LegacyOutput], sampler,caseRoot,createSource:true,scoped?.Context ?? contextFreeze.Context);
                // All source/export/inspection children of this case have exited.
                // Keep complete snapshot bytes and provenance in verified archives
                // before retiring only these new duplicate directory entries.
                foreach (string snapshot in Exact14(plan) || plan.CompositeQualification ? [] : Directory.GetDirectories(Path.Combine(caseRoot, "buffer"), "save-*", SearchOption.AllDirectories))
                {
                    sampler.BeginOffline("archive", 1_068_194_304, ArchiveSeconds);
                    var archived = ReplayCalibrationArchive.Create(caseRoot, snapshot, bytes => sampler.Check(bytes), sampler.Gate);
                    ReplayCalibrationArchive.Retire(caseRoot, archived, sampler.Gate, sampler.ReleaseInventoryHandles);
                    sampler.EndOffline();
                }
                if (label == "T03")
                {
                    Require(samples.Select(s => s.Scope).Distinct().Count() == 1, "Training source/endpoint/recipe changed.");
                    var target = ReplayStorage.ObserveCapacity(caseRoot);
                    var outputTraining = plan.CompositeQualification ? samples.SelectMany(s => new[] { s, s.LegacyOutput }).ToArray() : samples.ToArray();
                    long residual = Math.Max(0, outputTraining.Max(s => s.FinalBytes - s.InputBytes));
                    long extra = Math.Max(0, outputTraining.Max(s => s.PeakAllocation - ReplayReferenceModel.Round(s.FinalBytes, target.AllocationUnit)));
                    string[] receipts = plan.CompositeQualification ? Enumerable.Range(0, 3).Select(n =>
                        ReplayCalibrationCatalog.PairHash(qualifications[n], legacyQualifications[n])).ToArray()
                        : Order.Take(3).Select(l => HashFile(Path.Combine(
                        plan.ProductionTrainingRoot != null && l is "T01" or "T02" ? plan.ProductionTrainingRoot : plan.Root, l, "sample.json"))).ToArray();
                    var training = qualifications.Take(3).ToArray();
                    Require(training.All(q => q.CaptureVolume == target.Volume), "Capture/output volume is outside the first reference cell.");
                    var headroom = new ReplayCaptureHeadroom(sample.Scope, target.Volume, target.AllocationUnit,
                        ReplayReferenceModel.CaptureAllowance(training.Max(q => q.CapturePollGrowthBytes.Value), target.AllocationUnit),
                        ReplayReferenceModel.CaptureAllowance(training.Max(q => q.CaptureStopGrowthBytes.Value), target.AllocationUnit),
                        ReplayCapacityPolicy.CoverageMilliseconds);
                    string revision = ReplayCalibrationCatalog.ModelRevision(sample.Scope, target.Volume, target.AllocationUnit, residual, extra, receipts, captureHeadroom: headroom,replayMinutes:plan.ReplayMinutes);
                    model = new(revision, sample.Scope, target.Volume, target.AllocationUnit, residual, extra, receipts, headroom,plan.ReplayMinutes);
                    Write(Path.Combine(plan.Root, "frozen-training-model.json"), model);
                }
                else if (label.StartsWith('H') && !ContextQualification(plan))
                {
                    Require(sample.Scope == model.Scope && sample.Demand.Kind == ReplayOutputEvidenceKind.CalibratedEstimate, "Held-out real provider was unmeasured.");
                    var captureMetrics = qualifications[^1];
                    Require(captureMetrics.CaptureVolume == model.Volume && captureMetrics.CapturePollGrowthBytes <= model.CaptureHeadroom.PollGrowthBytes
                        && captureMetrics.CaptureStopGrowthBytes <= model.CaptureHeadroom.StopGrowthBytes, "Held-out capture growth exceeds the frozen headroom; no refit.");
                    Require(model.CoversObservations(sample.InputBytes, sample.FinalBytes, sample.FinalAllocation, sample.CombinedPeakAllocation)
                        && sample.WorkPeakAllocation <= ReplaySaveExport.WorkDemand(sample.InputBytes, model.AllocationUnit).Total,
                        "Held-out exceedance rejects frozen model; no refit.");
                    if (plan.CompositeQualification)
                    {
                        var legacy = sample.LegacyOutput;
                        Require(legacy.Demand.Kind == ReplayOutputEvidenceKind.CalibratedEstimate
                            && model.CoversObservations(legacy.InputBytes, legacy.FinalBytes, legacy.FinalAllocation, legacy.CombinedPeakAllocation)
                            && legacy.WorkPeakAllocation <= ReplaySaveExport.WorkDemand(legacy.InputBytes, model.AllocationUnit).Total,
                            "Held-out fallback exceeds the frozen union model; no refit.");
                    }
                }
                sampler.SealClosedCase(label);
                if ((Exact14(plan) || plan.CompositeQualification) && File.Exists(Path.Combine(plan.Root, "stop-after-case.request.json")))
                    throw new OperationCanceledException("Operator requested a stop after this closed case; preserve evidence and admit no next acquisition.");
            }
            sampler.StopAndCheck();
            Write(Path.Combine(plan.Root, "qualification-sources.json"), qualifications.ToArray());
            Write(Path.Combine(plan.Root, "observation-summary.json"), new { sampler.MaximumActiveGap, sampler.MaximumOfflineGap, sampler.MaximumGap,
                sampler.CapacityObservations, sampler.MaximumCapacityGap, sampler.MaximumCapacityObservationMilliseconds,
                sampler.Used, sampler.PeakUsed, sampler.MaximumFiles, sampler.MaximumDirectories,
                metadataOperations = sampler.MetadataOperationTimings, offlineSeconds = sampler.OfflineSeconds });
            double maximumActive = Math.Max(sampler.MaximumActiveGap, qualifications.Max(q => q.MaximumActiveGapMs));
            string partialTrainingRoot = plan.CompositeTrainingRoot ?? plan.ProductionTrainingRoot;
            if (partialTrainingRoot != null && !CapacityTrainingContinuation(plan) && !DurationTrainingContinuation(plan))
            {
                using var retainedFailure = JsonDocument.Parse(File.ReadAllText(Path.Combine(partialTrainingRoot, "failure.json")));
                maximumActive = Math.Max(maximumActive, retainedFailure.RootElement.GetProperty("MaximumActiveGap").GetDouble());
            }
            string observationHash = partialTrainingRoot != null ? ReplayReferenceModel.Hash(new {
                retained = HashFile(Path.Combine(partialTrainingRoot, "storage.csv")), current = HashFile(Path.Combine(plan.Root, "storage.csv")) })
                : ExactContinuation14(plan) ? ReplayReferenceModel.Hash(new { retained = HashFile(Path.Combine(plan.ExactContinuationRoot, "storage.csv")),
                heldout = ExactFinalWorker14(plan) ? HashFile(Path.Combine(plan.ExactHeldoutContinuationRoot, "storage.csv")) : null, current = HashFile(Path.Combine(plan.Root, "storage.csv")) })
                : HashFile(Path.Combine(plan.Root, "storage.csv"));
            if (scoped != null)
            {
                WriteFiveMinuteResult(plan, scoped, expectedPlanHash, observationHash, confirmationStarted,
                    qualifications.Single(), legacyQualifications.Single(), sameOracle);
                return 0;
            }
            if (ContextQualification(plan))
            {
                var confirmation = new ReplayCoverageConfirmation("C00-current-5m-pair", contextFreeze.FreezeHash,
                    ReplayReferenceModel.ExporterIdentity, ReplayCalibrationCatalog.CaptureHostFacts(), confirmationStarted, DateTime.UtcNow,
                    expectedPlanHash, observationHash, null, null, qualifications.Single(), legacyQualifications.Single(), null);
                confirmation = confirmation with { ConfirmationHash = ReplayCalibrationCatalog.ConfirmationHash(confirmation) };
                var parents = ReplayCalibrationCatalog.ValidateFreeze(contextFreeze, confirmation.Host.Identity);
                ReplayCalibrationCatalog.ValidateConfirmation(confirmation, contextFreeze, parents[1], "C00-current-5m-pair");
                Write(Path.Combine(plan.Root, "context-confirmation.json"), confirmation);
                if (SameCaptureQualification(plan))
                {
                    string cell = Path.Combine(plan.Root, "H03");
                    var inputs = JsonSerializer.Deserialize<ReplayCompletedInputDigest[]>(File.ReadAllBytes(Path.Combine(cell, "completed-input-mapping.json")));
                    var presentationBinding = PresentationPlanBinding.For(plan);
                    var reference = new { policy = HashFile(Path.Combine(cell, "source-reference-policy.json")),
                        audio = HashFile(Path.Combine(cell, "source-render-clock.csv")), video = HashFile(Path.Combine(cell, "source-paints.csv")),
                        costs = HashFile(Path.Combine(cell,"source-reference-costs.json")),
                        presentation=presentationBinding==null?null:CalibrationPresentationIntegration.HashBinding(cell,presentationBinding.Spec) };
                    Write(Path.Combine(cell, "source-reference-binding.json"), reference);
                    var manifest = new ReplaySameCaptureManifest(ReplayCalibrationCatalog.SameCaptureRevision, confirmation.ConfirmationHash,
                        confirmation.Packet.AcquisitionIdentity, confirmation.Packet.SelectionHash, confirmation.Packet.OutputStart,
                        confirmation.Packet.OutputEnd, HashFile(Path.Combine(cell, "source-clock.json")),
                        HashFile(Path.Combine(cell, "source-reference-binding.json")), inputs, null);
                    manifest = manifest with { ManifestHash = ReplayCalibrationCatalog.SameManifestHash(manifest) };
                    ReplayCalibrationCatalog.ValidateSameManifest(manifest, confirmation);
                    Write(Path.Combine(plan.Root, "C00-input-manifest.json"), manifest, 524288);
                    Require(sameOracle != null, "Concrete C00 source/reference oracle must exist before acceptance review.");
                    Write(Path.Combine(plan.Root,"independent-acceptance-data.json"), new {
                        confirmationHash=confirmation.ConfirmationHash,manifestHash=manifest.ManifestHash,oraclePolicy=CalibrationSourceReference.Policy,
                        oracleEvidenceHash=sameOracle.ReportHash,outputs=sameOracle.Outputs,
                        status="awaiting-named-independent-review",ordinarySavedSamples=0 });
                }
                Write(Path.Combine(plan.Root, "result.json"), new { result = "passed-C00-frozen-union-no-refit-pending-independent-acceptance",
                    confirmation.ConfirmationHash, contextFreeze.FreezeHash, ordinarySavedSamples = 0, modelActivated = false, samples });
                return 0;
            }
            var qualification = ReplayCalibrationCatalog.CreateReceipt(model, qualifications.ToArray(), maximumActive, observationHash, expectedPlanHash,
                legacyOutputs: plan.CompositeQualification ? legacyQualifications.ToArray() : null);
            Write(Path.Combine(plan.Root, "qualified-reference.json"), qualification);
            Write(Path.Combine(plan.Root, "result.json"), new { result = "passed-finite-reference-calibration", samples, model,
                limits = "Empirical six-source cell; sampled peak is not a physical upper bound. Held-out captures/exports join a task-private production ledger; unfinished-tail acceptance reserves the complete input/output envelope. No account catalog activation, speed/A-V/game/release acceptance.",
                sampler.Used, sampler.MaximumGap, sampler.MaximumActiveGap, sampler.MaximumOfflineGap, processes });
            Console.WriteLine("Calibration passed: " + plan.Root); return 0;
        }
        catch (Exception ex)
        {
            WriteFailure(plan.Root, ex, new { result = "failed-no-retry", error = ex.ToString(), samples, model, processes, sampler.Used, sampler.MaximumGap,
                sampler.MaximumActiveGap, sampler.SlowestLeafPath, sampler.SlowestLeafMilliseconds, sampler.LastObservation,
                outputOperations = sampler.OutputOperationTimings, metadataOperations = sampler.MetadataOperationTimings,
                offlineSeconds = sampler.OfflineSeconds });
            return 1;
        }
        finally { try { sameCaptureInputOwner?.Dispose(); } finally { try {sameCapturePresentationOwner?.Dispose();}finally{sameCapturePresentationOwner=null;sameCaptureInputOwner = null; foreach (var lease in qualificationOutputs.Values) lease.Dispose();} } }
    }
}
