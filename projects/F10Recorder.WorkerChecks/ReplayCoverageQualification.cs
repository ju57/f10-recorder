using System.Text.Json;

namespace F10Recorder;

// Extensions of the existing bounded collector. Selecting a mode never supplies
// recording authorization; Run retains its final-plan and authorization fences.
internal static partial class ReplayCalibrationRun
{
    internal const string ContextConfirmationAdmission = "issue83-C00-current-context-frozen-union-v9";
    internal const string SameCaptureContextAdmission = "issue83-C00-same-capture-reference-v10";
    static bool SameCaptureQualification(CalibrationPlan p) => p?.FreshCandidateAdmission is SameCaptureContextAdmission or FiveMinuteStorageAdmission;
    static bool ContextQualification(CalibrationPlan p) => p?.FreshCandidateAdmission is ContextConfirmationAdmission or SameCaptureContextAdmission or FiveMinuteStorageAdmission;
    internal static CalibrationPlan ContextFixturePlan() => new("unqueried-current", "unqueried-engine", "controlled-C00",
        new(), ["H03"], ["unqueried-retained"], 0, 0, MaximumChildren: 13, BudgetBytes: 13_000_000_000,
        StopBytes: 12_000_000_000, DeadlineSeconds: 1800, RetainedOfflineSeconds: 0, MetadataHandleRoots: ["unqueried-retained"],
        ReuseCurrentMetadataHandles: true, DirectoryLimit: 4096, OutputComponentBytes: 2_000_000_000,
        RetainedBudgetBytes: 1_000_000_000, FileLimit: 49152, OfflineDeadlineSeconds: 600, OutsideBuildAllowanceBytes: 2_000_000_000,
        FreezeRetainedAllocations: true, ReplayExporterRecipe: ReplaySaveExport.PolicyIdentity, ValidationChildren: 3,
        CompositeQualification: true, CompositeManualForegroundHandoff: true, CompositeExplicitForegroundRelease: true,
        CopyFallbackReserveBytes: DurationCopyBytes(5)) { FreshCandidateAdmission = ContextConfirmationAdmission,
            EvidenceRoles = new() { ["fresh-candidate-qualification"] = "unqueried", ["coverage-freeze"] = "unqueried" } };
    internal static int ContextAdmissionChecks()
    {
        int count = 0; void Check(bool v) { Require(v, "C00 shape/preparation regression."); count++; }
        var p = ContextFixturePlan(); Check(ContextEnvelopeValid(p)); Check(DurationEnvelopeValid(p));
        var shape = DurationPreparation(ContextConfirmationAdmission, 5).Value;
        Check(shape.Source == p.SourceComponentBytes && shape.Output == p.OutputComponentBytes && shape.Copy == p.CopyFallbackReserveBytes);
        Check(AcquisitionSeconds(p) == 420 && QualificationFileForecast(p) == 2830);
        Check(NewCohortBytes(p) > 3_000_000_000 && checked(p.RetainedBudgetBytes + p.OutsideBuildAllowanceBytes + NewCohortBytes(p)) < p.StopBytes);
        var now = new DateTime(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);
        Check(MinimumFixedPurposeSeconds(p) == 1200 && FixedPurposeWindowValid(p, now.AddSeconds(1200), now.AddHours(2), now));
        Check(!FixedPurposeWindowValid(p, now.AddSeconds(1199), now.AddHours(2), now));
        foreach (var bad in new[] { p with { ReplayMinutes = 1 }, p with { Order = Order }, p with { ValidationChildren = 18 },
            p with { MaximumChildren = 61 }, p with { CompositeTrainingRoot = "old-training" }, p with { DeadlineSeconds = 7200 },
            p with { EvidenceRoles = new() { ["fresh-candidate-qualification"] = "unqueried" } } }) Check(!ContextEnvelopeValid(bad));
        Console.WriteLine($"Passed {count} pure single-C00/preparation/commitment/fixed-window checks; no files/native/media."); return 0;
    }
    static bool ContextEnvelopeValid(CalibrationPlan p) => ContextQualification(p) && p.ReplayMinutes == 5
        && p.CompositeQualification && p.CompositeManualForegroundHandoff && p.CompositeExplicitForegroundRelease
        && p.CompositeTrainingRoot == null && !p.ProductionQualification && p.ProductionTrainingRoot == null
        && p.ExactContinuationRoot == null && p.ExactHeldoutContinuationRoot == null
        && p.Order != null && p.Order.SequenceEqual(new[] { "H03" }) && p.RetainedChildren == 0
        && p.MaximumChildren == (SameCaptureQualification(p) ? 16 : 13) && p.ValidationChildren == (SameCaptureQualification(p) ? 6 : 3) && p.RetainedElapsedSeconds == 0 && p.RetainedOfflineSeconds == 0
        && p.DeadlineSeconds == 1800 && p.OfflineDeadlineSeconds == (SameCaptureQualification(p) ? 900 : 600) && p.FloorBytes == 60_000_000_000L
        && p.BudgetBytes > 0 && p.BudgetBytes == checked(p.StopBytes + 1_000_000_000L)
        && p.RetainedBudgetBytes >= 0 && p.OutsideBuildAllowanceBytes == 2_000_000_000L && p.AdditionalHistoricalReserveBytes == 0
        && p.CopyFallbackReserveBytes == DurationCopyBytes(5) && p.SourceComponentBytes == 1_000_000_000L
        && p.OutputComponentBytes == 2_000_000_000L && p.DirectoryLimit == 4096 && p.FileLimit == 49152
        && p.FreezeRetainedAllocations && p.ReuseCurrentMetadataHandles && !p.RetainActiveOutputHandles && !p.ReadonlyValidationPhases
        && !p.OperatorForegroundHandoff && !p.UserForegroundHandoff && !p.AutomaticFixtureHandoff
        && p.SelectedRetainedFiles == null && p.ReplayExporterRecipe == ReplaySaveExport.PolicyIdentity && p.Files != null
        && p.EvidenceRoles?.ContainsKey("fresh-candidate-qualification") == true && (FiveMinuteStorage(p) ? FiveMinuteShape(p) : p.EvidenceRoles.ContainsKey("coverage-freeze"));

    static ReplayCoverageFreeze ReadContextFreeze(CalibrationPlan plan)
    {
        string path = EvidenceRole(plan, "coverage-freeze");
        var f = ReplayCalibrationCatalog.ReadEvidence<ReplayCoverageFreeze>(ReadCoverageMetadata(path, plan.Files[path]));
        ReplayCalibrationCatalog.ValidateFreeze(f, ReplayCalibrationCatalog.HostIdentity);
        return f;
    }
    // Qualification-only physical promises, not empirical coefficients and not
    // a SafeToResume/catalog substitute. Existing Sampler/commitments continue
    // charging the entire unsealed source, TWO outputs, scratch/copy and closure.
    // The actual Cache still owns its shared-volume reservation and freshness.
    internal static ReplayOutputDemand UnmodelledQualificationDemand(ReplayOutputSelection selection, CalibrationPlan plan,
        string scope, string sourceDirectory)
    {
        Require(ContextEnvelopeValid(plan), "Unmodelled admission is restricted to the single prospective C00 pair.");
        return UnmodelledSelectionDemand(selection, 5, plan.OutputComponentBytes, scope, sourceDirectory);
    }
    internal static ReplayOutputDemand UnmodelledSelectionDemand(ReplayOutputSelection selection, int minutes, long outputBytes,
        string scope, string sourceDirectory)
    {
        double duration = selection.End - selection.Start;
        double span = selection.Inputs.Count == 0 ? 0 : selection.End - selection.Inputs[0].Start;
        Require(selection.ExporterIdentity == ReplayReferenceModel.ExporterIdentity && !string.IsNullOrWhiteSpace(scope)
            && double.IsFinite(duration) && duration >= 1 && duration <= minutes * 60 + .000001
            && span >= duration - .000001 && span <= duration + 1.1 && selection.Inputs.Count is > 0
            && selection.Inputs.Count <= ReplayReferenceModel.InputCountLimit(minutes), "Qualification selection is outside its complete-input domain.");
        long bytes = 0;
        foreach (var input in selection.Inputs)
        {
            Require(Path.GetFullPath(input.Path).StartsWith(Path.GetFullPath(sourceDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && input.Path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
                && (File.GetAttributes(input.Path) & (FileAttributes.Compressed | FileAttributes.SparseFile | FileAttributes.ReparsePoint)) == 0,
                "Qualification source provenance changed.");
            bytes = checked(bytes + input.Extent.Length);
        }
        Require(bytes is > 0 && bytes <= ReplayReferenceModel.InputLimit(minutes), "Qualification whole-tier input limit exceeded.");
        var target = ReplayStorage.ObserveCapacity(selection.Destination);
        var work = ReplaySaveExport.WorkDemand(ReplayReferenceModel.InputLimit(minutes), target.AllocationUnit);
        return new(ReplayOutputEvidenceKind.SourceBound, "explicit-unmodelled-qualification-physical-commitment-v9", selection.Identity,
            selection.ExporterIdentity, target.Volume, target.AllocationUnit,
            checked(ReplayReferenceModel.Round(outputBytes, target.AllocationUnit) + work.Total), !selection.Closed, null,
            ReplayReferenceModel.SnapshotEnvelope(target.AllocationUnit, minutes), work, ConcatMaximumBytes: ReplayReferenceModel.ConcatLimit(minutes));
    }
    static byte[] ReadCoverageMetadata(string path, string hash)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Require(input.Length is > 0 and <= ReplayCalibrationCatalog.CoverageMaximumBytes, "Bounded coverage metadata required.");
        byte[] bytes = new byte[(int)input.Length]; input.ReadExactly(bytes);
        Require(ReplayCalibrationCatalog.ByteHash(bytes) == hash, "Coverage metadata bytes changed."); return bytes;
    }

    // The request file is prospective metadata, never a receipt made from old
    // coefficient metadata alone. Neither command installs an account catalog.
    internal sealed record CoverageFreezeRequest(ReplayQualificationParent[] Parents, ReplayQualificationContext Context,
        ReplayHostFacts Host, ReplayApplicabilityReview Applicability);
    internal sealed record CoverageIssueRequest(ReplayCoverageFreeze Frozen, ReplayCoverageConfirmation CurrentContext,
        ReplayCoverageConfirmation RetainedMinute, ReplayConfirmationAcceptance[] Acceptances);
    internal sealed record SameCaptureFileBinding(string Path, string Sha256);
    internal sealed record SameCaptureIssueRequest(SameCaptureFileBinding Frozen, SameCaptureFileBinding C00,
        SameCaptureFileBinding Manifest, SameCaptureFileBinding Retained, SameCaptureFileBinding[] Acceptances,
        string Destination);
    static T ReadSameCaptureBinding<T>(SameCaptureFileBinding binding)
    {
        Require(binding != null && Path.IsPathFullyQualified(binding.Path), "Absolute original evidence binding required.");
        return ReplayCalibrationCatalog.ReadEvidence<T>(ReadCoverageMetadata(binding.Path, binding.Sha256));
    }
    internal static int IssueSameCapture(string input, string expectedHash, string privateDirectory)
    {
        Require(Path.IsPathFullyQualified(input) && Path.IsPathFullyQualified(privateDirectory), "Absolute issuer paths required.");
        var request = ReplayCalibrationCatalog.ReadEvidence<SameCaptureIssueRequest>(ReadCoverageMetadata(input, expectedHash));
        Require(request?.Acceptances is { Length: 2 } && Path.IsPathFullyQualified(request.Destination),
            "Two original acceptances and an absolute intended destination are required.");
        var frozen = ReadSameCaptureBinding<ReplayCoverageFreeze>(request.Frozen);
        var c00 = ReadSameCaptureBinding<ReplayCoverageConfirmation>(request.C00);
        var manifest = ReadSameCaptureBinding<ReplaySameCaptureManifest>(request.Manifest);
        var retained = ReadSameCaptureBinding<ReplayCoverageConfirmation>(request.Retained);
        var acceptances = request.Acceptances.Select(ReadSameCaptureBinding<ReplaySameCaptureAcceptance>).ToArray();
        var actual = ReplayCalibrationCatalog.CaptureHostFacts();
        Require(actual == frozen.Host, "Issuer differs from the frozen actual host operands.");
        var receipt = new ReplaySameCaptureReceipt(ReplayCalibrationCatalog.SameCaptureRevision,
            frozen, c00, manifest, retained, acceptances, null);
        receipt = receipt with { ReceiptHash = ReplayCalibrationCatalog.SameReceiptHash(receipt) };
        ReplayCalibrationCatalog.ValidateSameCapture(receipt, actual.Identity);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(receipt);
        Require(bytes.Length is > 0 and <= ReplayCalibrationCatalog.CoverageMaximumBytes, "Bounded v10 receipt required before storage creation.");
        string root = Path.GetFullPath("artifacts") + Path.DirectorySeparatorChar;
        Require(Path.GetFullPath(privateDirectory).StartsWith(root, StringComparison.OrdinalIgnoreCase)
            && !Directory.Exists(privateDirectory) && !File.Exists(privateDirectory), "Issuance requires a fresh ignored PRIVATE catalog.");
        TrustedStorage.CreatePrivateDirectory(privateDirectory);
        using var owner = TrustedStorage.Acquire(privateDirectory);
        string path = Path.Combine(privateDirectory, ReplayCalibrationCatalog.SameCaptureFileName);
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { file.Write(bytes); file.Flush(true); }
        TrustedStorage.ValidateFile(path);
        for (int minutes = 1; minutes <= 30; minutes++)
        {
            var availability = ReplayCalibrationCatalog.LoadFrom(privateDirectory, replayMinutes: minutes);
            Require(availability.Model?.ReplayMinutes == minutes
                && ReplayCalibrationCatalog.IsApplicableToDestination(availability.Model, request.Destination),
                availability.Reason ?? "Issued v10 bridge failed actual-host duration/destination resolution.");
        }
        byte[] verification = JsonSerializer.SerializeToUtf8Bytes(new { requestSha256 = expectedHash, request,
            receipt.ReceiptHash, actualHost = actual, destination = request.Destination, resolvedMinutes = Enumerable.Range(1, 30).ToArray(),
            accountActivated = false, capture = false, ordinarySaved = 0 });
        Require(verification.Length <= 65536, "Bounded private issuer verification required.");
        string verificationPath = Path.Combine(privateDirectory, "issuance-verification.json");
        using (var file = new FileStream(verificationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { file.Write(verification); file.Flush(true); }
        TrustedStorage.ValidateFile(verificationPath);
        Console.WriteLine("Issued private v10 bridge from six original accepted evidence bindings; actual host/destination1..30 resolved; no account activation/capture/Saved.");
        return 0;
    }
    internal static int FreezeCoverage(string request, string expectedHash, string output, bool sameCapture = false)
    {
        Require(!File.Exists(output) && Path.IsPathFullyQualified(output), "Preserve any preceding freeze.");
        var p = ReplayCalibrationCatalog.ReadEvidence<CoverageFreezeRequest>(ReadCoverageMetadata(request, expectedHash));
        var actual = ReplayCalibrationCatalog.CaptureHostFacts();
        Require(actual == p.Host, "Freeze request differs from actual current host operands.");
        var frozen = ReplayCalibrationCatalog.CreateCoverageFreeze(p.Parents, p.Context, actual, p.Applicability, DateTime.UtcNow);
        if (sameCapture)
        {
            frozen = frozen with { Revision = ReplayCalibrationCatalog.SameCaptureFreezeRevision, FreezeHash = null };
            frozen = frozen with { FreezeHash = ReplayCalibrationCatalog.FreezeHash(frozen) };
            ReplayCalibrationCatalog.ValidateFreeze(frozen, actual.Identity);
        }
        using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(file, frozen); file.Flush(true);
        Console.WriteLine("Frozen original training union; both confirmations pending; no runtime eligibility or recording."); return 0;
    }
    internal static int IssueCoverage(string input, string expectedHash, string privateDirectory)
    {
        var request = ReplayCalibrationCatalog.ReadEvidence<CoverageIssueRequest>(ReadCoverageMetadata(input, expectedHash));
        var receipt = ReplayCalibrationCatalog.CreateCoverageReceipt(request.Frozen, request.CurrentContext, request.RetainedMinute, request.Acceptances);
        ReplayCalibrationCatalog.ValidateCoverage(receipt, ReplayCalibrationCatalog.HostIdentity);
        string root = Path.GetFullPath("artifacts") + Path.DirectorySeparatorChar;
        Require(Path.GetFullPath(privateDirectory).StartsWith(root, StringComparison.OrdinalIgnoreCase)
            && !Directory.Exists(privateDirectory) && !File.Exists(privateDirectory), "Bridge issuance requires a fresh ignored PRIVATE catalog.");
        TrustedStorage.CreatePrivateDirectory(privateDirectory);
        string path = Path.Combine(privateDirectory, ReplayCalibrationCatalog.CoverageFileName);
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(file, receipt); file.Flush(true); }
        for (int minutes = 1; minutes <= 30; minutes++)
            Require(ReplayCalibrationCatalog.LoadFrom(privateDirectory, replayMinutes: minutes).Model?.ReplayMinutes == minutes,
                "Issued bridge failed ordinary private catalog resolution.");
        Console.WriteLine("Issued private v9 bridge after genuine accepted confirmations; no account activation or recording."); return 0;
    }
}
