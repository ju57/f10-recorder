using System.Text.Json;

namespace F10Recorder;

internal sealed partial record CalibrationPlan
{
    public string FreshCandidateAdmission { get; init; }
}

// The existing owning final-admission entry remains the only launch authority.
// This replacement cohort imports no old500ms sample/model and grants no refund
// for historical or uncertain resources. The user's carried mission authorizes
// recording/retries; UI release and exact plan/identity checks remain mandatory.
internal static partial class ReplayCalibrationRun
{
    const string CapacityAdmission = "issue83-reserved-staging-fresh-v1";
    const string CapacityContinuationAdmission = "issue83-capacity-coverage-three-heldout-v1";
    const long CapacityBudgetBytes = 585_000_000_000, CapacityStopBytes = 580_000_000_000;
    const long CapacityRetainedBytes = 227_000_000_000;
    const long CapacityOutsideBytes = 159_000_000_000;
    const long CapacityContinuationBudgetBytes = 597_000_000_000, CapacityContinuationStopBytes = 592_000_000_000;
    const long CapacityContinuationRetainedBytes = 239_000_000_000;
    // Six330s sources, two at-most305-input snapshots per source,512 small
    // per-case objects, plus1,288 root/control/report objects. This exceeds the
    // observed5,971-file cohort growth; it never credits existing aliases.
    const int CapacityNewCohortFiles = 10_000;
    static bool CapacityFileForecastFits(int baseline, int limit, int forecast = CapacityNewCohortFiles) => baseline >= 0 && limit >= 0 && forecast>=0
        && (long)baseline + forecast <= limit;
    static bool CapacityTrainingContinuation(CalibrationPlan p) => p?.FreshCandidateAdmission == CapacityContinuationAdmission;
    static bool CapacityQualification(CalibrationPlan p) => p?.FreshCandidateAdmission == CapacityAdmission || CapacityTrainingContinuation(p) || OwnedHistoryQualification(p);
    static long NewCohortBytes(CalibrationPlan p) => new QualificationCommitments(p.Order,
        p.SourceComponentBytes, p.OutputComponentBytes, p.CopyFallbackReserveBytes, QualificationScratchBytes(p), QualificationTraceBytes(p), QualificationAliasNamespaceBytes(p)).RemainingBytes;
    static long QualificationFreeRequirement(CalibrationPlan p)
        => CapacityQualification(p) ? new QualificationCommitments(p.Order, p.SourceComponentBytes,
            p.OutputComponentBytes, p.CopyFallbackReserveBytes, QualificationScratchBytes(p), QualificationTraceBytes(p), QualificationAliasNamespaceBytes(p)).InitialRequiredFreeBytes(QualificationPhysicalFloor(p))
            : checked(p.FloorBytes + p.BudgetBytes);
    static int CapacityHandleBudget(int? value)
    { Require(value == 98_304, "Fresh capacity cohort requires its declared98304 handle ceiling."); return value.Value; }
    static bool CapacityEnvelopeValid(CalibrationPlan p) => OwnedHistoryQualification(p)?OwnedHistoryEnvelopeValid(p):CapacityQualification(p)
        && p.CompositeQualification && p.CompositeManualForegroundHandoff && p.CompositeExplicitForegroundRelease
        && (CapacityTrainingContinuation(p) ? !string.IsNullOrWhiteSpace(p.CompositeTrainingRoot) : p.CompositeTrainingRoot == null)
        && !p.ProductionQualification && p.ProductionTrainingRoot == null
        && p.ExactContinuationRoot == null && p.ExactHeldoutContinuationRoot == null
        && p.Order != null && p.Order.SequenceEqual(CapacityTrainingContinuation(p) ? Order.Skip(3) : Order)
        && p.RetainedChildren == 0 && p.MaximumChildren == 61 && p.ValidationChildren == (CapacityTrainingContinuation(p) ? 9 : 18)
        && p.RetainedElapsedSeconds == 0 && p.RetainedOfflineSeconds == 0
        && p.DeadlineSeconds == 5400 && p.OfflineDeadlineSeconds == 1200
        && p.BudgetBytes == (CapacityTrainingContinuation(p) ? CapacityContinuationBudgetBytes : CapacityBudgetBytes)
        && p.StopBytes == (CapacityTrainingContinuation(p) ? CapacityContinuationStopBytes : CapacityStopBytes) && p.FloorBytes == 60_000_000_000
        && p.RetainedBudgetBytes == (CapacityTrainingContinuation(p) ? CapacityContinuationRetainedBytes : CapacityRetainedBytes)
        && p.OutsideBuildAllowanceBytes == CapacityOutsideBytes
        && p.AdditionalHistoricalReserveBytes == 164_980_108_864 && p.CopyFallbackReserveBytes == 1_000_000_000
        && p.SourceComponentBytes == 1_000_000_000 && p.OutputComponentBytes == 2_000_000_000
        && p.DirectoryLimit == 16384 && p.FileLimit == 81920 && p.FreezeRetainedAllocations && p.ReuseCurrentMetadataHandles
        && !p.RetainActiveOutputHandles && !p.ReadonlyValidationPhases && !p.OperatorForegroundHandoff && !p.UserForegroundHandoff && !p.AutomaticFixtureHandoff
        && p.ReplayExporterRecipe == ReplaySaveExport.PolicyIdentity && p.Files != null && p.EvidenceRoles != null
        && p.EvidenceRoles.ContainsKey("fresh-candidate-qualification");
    static void ValidateCapacityResources(CalibrationPlan p)
    {
        Require(CapacityEnvelopeValid(p), "The capacity-policy candidate needs its complete finite six-source admission.");
        Require(!OwnedHistoryQualification(p) || OwnedHistoryPathsDisjoint(p.RetainedRoots,p.Root,OwnedHistoryRootLimit(p.FreshCandidateAdmission)),"Owned-history retained roots overlap current work.");
        _ = CompositeParentHandleBudget(p);
        Require(checked(p.RetainedBudgetBytes + p.OutsideBuildAllowanceBytes + p.AdditionalHistoricalReserveBytes + NewCohortBytes(p)) < p.StopBytes,
            "All retained/historical promises plus the new finite cohort must fit the early ceiling.");
        Require(p.RetainedRoots is { Length: > 0 and <= 36 } && p.RetainedRoots.Distinct(StringComparer.OrdinalIgnoreCase).Count() == p.RetainedRoots.Length
            && p.RetainedRoots.All(Directory.Exists) && p.MetadataHandleRoots != null && p.MetadataHandleRoots.SequenceEqual(p.RetainedRoots),
            "Retained namespaces and metadata ownership must remain explicit.");
    }
    static void ValidateCapacityDocument(CalibrationPlan p)
    {
        Require(CapacityEnvelopeValid(p), "Unknown capacity candidate scope.");
        string file = EvidenceRole(p, "fresh-candidate-qualification");
        Require(new FileInfo(file).Length is > 0 and <= 65536 && HashFile(file) == p.Files[file], "Changed capacity-cohort document.");
        using var document = JsonDocument.Parse(File.ReadAllText(file)); var e = document.RootElement;
        Require(e.GetProperty("kind").GetString() == p.FreshCandidateAdmission
            && e.GetProperty("windowsSha256").GetString() == ReplayReferenceModel.AssemblyHash
            && e.GetProperty("coreSha256").GetString() == ReplayReferenceModel.CoreHash
            && e.GetProperty("recipeHash").GetString() == ReplayCalibrationCatalog.RecipeHash
            && e.GetProperty("observationMilliseconds").GetInt32() == ReplayCapacityPolicy.CoverageMilliseconds
            && e.GetProperty("sourceSeconds").GetInt32() == AcquisitionSeconds(p) && e.GetProperty("sources").GetInt32() == p.Order.Length
            && (!DurationQualification(p) || e.GetProperty("replayMinutes").GetInt32() == p.ReplayMinutes)
            && e.GetProperty("newCohortBytes").GetInt64() == NewCohortBytes(p)
            && e.GetProperty("newCohortFiles").GetInt32() == QualificationFileForecast(p)
            && e.GetProperty("importHistoricalSamplesOrModel").GetBoolean() == (CapacityTrainingContinuation(p) || DurationTrainingContinuation(p))
            && (!(CapacityTrainingContinuation(p) || DurationTrainingContinuation(p)) || e.GetProperty("reuseCompletedCurrentTraining").GetBoolean())
            && e.GetProperty("retainedRoots").EnumerateArray().Select(v => v.GetString()).SequenceEqual(p.RetainedRoots),
            "Capacity cohort identity, horizon or no-import contract changed.");
        DateTime created = e.GetProperty("createdUtc").GetDateTime(), expires = e.GetProperty("expiresUtc").GetDateTime();
        Require(created.Kind == DateTimeKind.Utc && expires.Kind == DateTimeKind.Utc && created <= DateTime.UtcNow && DateTime.UtcNow < expires,
            "Capacity-cohort document is expired or future dated.");
    }
    internal static int CapacityAdmissionChecks()
    {
        var files = new Dictionary<string, string>();
        var p = new CalibrationPlan("unused", "unused", "pure-capacity-admission", files, Order, ["unused"], 0, 0,
            MaximumChildren: 61, BudgetBytes: CapacityBudgetBytes, StopBytes: CapacityStopBytes, DeadlineSeconds: 5400,
            RetainedOfflineSeconds: 0, ReuseCurrentMetadataHandles: true, DirectoryLimit: 16384, OutputComponentBytes: 2_000_000_000,
            RetainedBudgetBytes: CapacityRetainedBytes, FileLimit: 81920, OfflineDeadlineSeconds: 1200, OutsideBuildAllowanceBytes: CapacityOutsideBytes,
            FreezeRetainedAllocations: true, ReplayExporterRecipe: ReplaySaveExport.PolicyIdentity, ValidationChildren: 18,
            CompositeQualification: true, CompositeManualForegroundHandoff: true, CompositeExplicitForegroundRelease: true,
            AdditionalHistoricalReserveBytes: 164_980_108_864, CopyFallbackReserveBytes: 1_000_000_000)
            { FreshCandidateAdmission = CapacityAdmission, EvidenceRoles = new() { ["fresh-candidate-qualification"] = "unused" } };
        int count = 0; void Check(bool value) { Require(value, "Capacity admission control failed."); count++; }
        Check(CapacityEnvelopeValid(p)); Check(QualificationFreeRequirement(p) == 86_286_217_728);
        // At admission's exact fit, materializing the active source/output and
        // shared copy/control/closure forecast must still leave the runtime
        // guard's complete, unsealed future commitment above the physical floor.
        Check(QualificationFreeRequirement(p) - 3_000_000_000L - 2_643_108_864L
            >= checked(p.FloorBytes + NewCohortBytes(p)));
        Check(!CapacityEnvelopeValid(p with { FreshCandidateAdmission = "issue83-anchor-guard2-fresh-v2" }));
        Check(!CapacityEnvelopeValid(p with { CompositeExplicitForegroundRelease = false }));
        Check(!CapacityEnvelopeValid(p with { CompositeTrainingRoot = "old" }));
        Check(!CapacityEnvelopeValid(p with { RetainedChildren = 1 }));
        Check(!CapacityEnvelopeValid(p with { Order = Order.Reverse().ToArray() }));
        Check(!CapacityEnvelopeValid(p with { RetainedBudgetBytes = 59_000_000_000 }));
        Check(!CapacityEnvelopeValid(p with { RetainedBudgetBytes = 122_000_000_000 }));
        Check(!CapacityEnvelopeValid(p with { RetainedBudgetBytes = 143_000_000_000 }));
        Check(!CapacityEnvelopeValid(p with { RetainedBudgetBytes = 164_000_000_000 }));
        Check(!CapacityEnvelopeValid(p with { RetainedBudgetBytes = 185_000_000_000 }));
        Check(!CapacityEnvelopeValid(p with { RetainedBudgetBytes = 206_000_000_000 }));
        Check(!CapacityEnvelopeValid(p with { OutsideBuildAllowanceBytes = 145_000_000_000 }));
        Check(!CapacityEnvelopeValid(p with { FileLimit = 65536 }));
        Check(!CapacityFileForecastFits(61719, 65536));
        Check(CapacityFileForecastFits(62723, 81920));
        Check(CapacityFileForecastFits(71920, 81920));
        Check(!CapacityFileForecastFits(71921, 81920));
        Check(!CapacityFileForecastFits(-1, 81920));
        Check(!CapacityFileForecastFits(int.MaxValue, int.MaxValue));
        Check(!CapacityEnvelopeValid(p with { OutsideBuildAllowanceBytes = 130_000_000_000 }));
        Check(!CapacityEnvelopeValid(p with { AdditionalHistoricalReserveBytes = 0 }));
        Check(!CapacityEnvelopeValid(p with { MaximumChildren = 62 }));
        Check(!CapacityEnvelopeValid(p with { SourceComponentBytes = 1_000_000_001 }));
        Check(!CapacityEnvelopeValid(p with { FloorBytes = 59_999_999_999 }));
        Check(!CapacityEnvelopeValid(p with { BudgetBytes = 440_000_000_001 }));
        Check(!CapacityEnvelopeValid(p with { DeadlineSeconds = 5401 }));
        Check(CapacityHandleBudget(98_304) == 98_304);
        var continuation = p with { FreshCandidateAdmission = CapacityContinuationAdmission, CompositeTrainingRoot = "current-complete-training",
            BudgetBytes = CapacityContinuationBudgetBytes, StopBytes = CapacityContinuationStopBytes,
            RetainedBudgetBytes = CapacityContinuationRetainedBytes, ValidationChildren = 9, Order = Order.Skip(3).ToArray() };
        Check(CapacityEnvelopeValid(continuation));
        Check(!CapacityEnvelopeValid(continuation with { CompositeTrainingRoot = null }));
        Check(!CapacityEnvelopeValid(continuation with { Order = Order }));
        Check(!CapacityEnvelopeValid(continuation with { ValidationChildren = 12 }));
        Check(!CapacityEnvelopeValid(continuation with { RetainedBudgetBytes = CapacityRetainedBytes }));
        Check(!CapacityEnvelopeValid(continuation with { BudgetBytes = 596_999_999_999 }));
        Check(!CapacityEnvelopeValid(continuation with { StopBytes = 591_999_999_999 }));
        Check(QualificationFreeRequirement(continuation) == 77_286_217_728);
        Console.WriteLine($"Capacity admission: {count} pure controls passed; no files/native/media/state action."); return 0;
    }
}
