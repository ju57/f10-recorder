namespace F10Recorder;

internal sealed partial record CalibrationPlan
{
    public int ReplayMinutes { get; init; } = 5;
}

// Three named reference cells in the existing six-acquisition collector. No
// historical model/sample/approval is imported into these fresh admissions.
internal static partial class ReplayCalibrationRun
{
    internal const string DurationAdmission = "issue83-duration-reference-fresh-v1";
    internal const string DurationTrainingAdmission = "issue83-duration-30m-three-fresh-heldouts-v1";
    internal const string DurationRetainedHistoryAdmission = "issue83-duration-1m-5m-fresh-retained-history-v1";
    static bool DurationAdmissionKind(string kind) => kind is DurationAdmission or DurationTrainingAdmission or DurationRetainedHistoryAdmission or ContextConfirmationAdmission or SameCaptureContextAdmission;
    static bool DurationTrainingContinuation(CalibrationPlan p) => p?.FreshCandidateAdmission == DurationTrainingAdmission;
    static bool DurationRetainedHistory(CalibrationPlan p) => p?.FreshCandidateAdmission == DurationRetainedHistoryAdmission;
    static bool DurationQualification(CalibrationPlan p) => DurationAdmissionKind(p?.FreshCandidateAdmission);
    readonly record struct DurationPreparationShape(int Minutes, long Source, long Output, long Copy, long Scratch, long Trace);
    static DurationPreparationShape? DurationPreparation(string kind, int minutes)
    {
        if (!DurationAdmissionKind(kind)) return null;
        Require(ReplayReferenceModel.SupportedMinutes(minutes) && (kind != DurationRetainedHistoryAdmission || minutes is 1 or 5),
            "Unsupported duration preparation cell.");
        return new(minutes, DurationSourceBytes(minutes), DurationOutputBytes(minutes), DurationCopyBytes(minutes),
            checked(ReplaySaveExport.WorkDemand(ReplayReferenceModel.InputLimit(minutes),65536).Total + 256_000_000L),
            checked(((minutes == 30 ? 30000L : 7200L) * 10 + 2) * 8192));
    }
    static int ReferenceSeconds(CalibrationPlan p) => ReplayReferenceModel.Seconds(p.ReplayMinutes);
    static int AcquisitionSeconds(CalibrationPlan p) => DurationQualification(p) ? ReferenceSeconds(p) + 120 : 330;
    static int MainInspectionLimit(CalibrationPlan p) => DurationQualification(p) && p.ReplayMinutes == 30 ? 360 : MainInspectionSeconds;
    static int ExportControllerLimit(CalibrationPlan p) => DurationQualification(p) && p.ReplayMinutes == 30 ? 1200 : 130;
    static int FixtureSeconds(CalibrationPlan p) => DurationQualification(p)
        ? CalibrationForeground.UserHandoffSeconds + AcquisitionSeconds(p) + 15
        : p.UserForegroundHandoff || p.CompositeManualForegroundHandoff ? CalibrationPattern.ManualLifetimeSeconds : 350;
    static long DurationSourceBytes(int minutes) => minutes == 30 ? 6_000_000_000L : ReplayReferenceModel.InputLimit(minutes);
    static long DurationCopyBytes(int minutes) => ReplayReferenceModel.SnapshotEnvelope(65536,minutes);
    static long QualificationScratchBytes(CalibrationPlan p) => DurationQualification(p)
        ? checked(ReplaySaveExport.WorkDemand(ReplayReferenceModel.InputLimit(p.ReplayMinutes), 65536).Total + 256_000_000L)
        : 1_256_000_000L;
    // At most ten storage rows/second (the sampler waits to100ms/start), plus
    // header and final row. Book even the full8192-byte row bound for the whole
    // admitted lifetime; no dropped rows or uncharged larger trace.
    static long QualificationTraceBytes(CalibrationPlan p) => DurationQualification(p)
        ? checked(((long)p.DeadlineSeconds * 10 + 2) * 8192) : 67_108_864L;
    // Separate from the existing per-case auxiliary allowance: one possible
    // re-rooted hardlink per input, conservatively booked at64KiB per name.
    // Native ID deduplication credits no namespace allocation or file count.
    static long DurationAliasNamespaceBytes(string kind, int minutes, int sources) => kind is DurationRetainedHistoryAdmission or RetainedCoverageAdmission or SameCaptureRetainedAdmission
        ? checked((long)sources * ReplayReferenceModel.InputCountLimit(minutes) * 65536) : 0;
    static long QualificationAliasNamespaceBytes(CalibrationPlan p) => DurationAliasNamespaceBytes(p.FreshCandidateAdmission, p.ReplayMinutes, p.Order.Length);
    static int QualificationFileForecast(CalibrationPlan p) => DurationQualification(p)
        ? checked(p.Order.Length * (AcquisitionSeconds(p) + (DurationRetainedHistory(p) ? 3 : 2) * ReplayReferenceModel.InputCountLimit(p.ReplayMinutes) + 512) + 1288)
        : CapacityNewCohortFiles;
    static long DurationOutputBytes(int minutes) => minutes switch { 1 => 500_000_000L, 5 => 2_000_000_000L,
        30 => 11_000_000_000L, _ => throw new IOException("Unsupported duration qualification.") };
    static long DurationStopBytes(int minutes) => minutes switch { 1 => 18_000_000_000L, 5 => 32_000_000_000L,
        30 => 130_000_000_000L, _ => throw new IOException("Unsupported duration qualification.") };
    static bool DurationEnvelopeValid(CalibrationPlan p) => ContextQualification(p) ? ContextEnvelopeValid(p) : DurationQualification(p)
        && ReplayReferenceModel.SupportedMinutes(p.ReplayMinutes)
        && (!DurationRetainedHistory(p) || p.ReplayMinutes is 1 or 5)
        && p.CompositeQualification && p.CompositeManualForegroundHandoff && p.CompositeExplicitForegroundRelease
        && (DurationTrainingContinuation(p) ? p.ReplayMinutes == 30 && CanonicalOwnedPath(p.CompositeTrainingRoot) : p.CompositeTrainingRoot == null)
        && !p.ProductionQualification && p.ProductionTrainingRoot == null
        && p.ExactContinuationRoot == null && p.ExactHeldoutContinuationRoot == null
        && p.Order != null && p.Order.SequenceEqual(DurationTrainingContinuation(p) ? Order.Skip(3) : Order)
        && p.RetainedChildren == (DurationTrainingContinuation(p) ? 29 : 0) && p.MaximumChildren == 61
        && p.ValidationChildren == (DurationTrainingContinuation(p) ? 9 : 18)
        && (DurationTrainingContinuation(p) ? p.RetainedElapsedSeconds == 9786.7313576
            && p.RetainedOfflineSeconds == 749.5707710000011 : p.RetainedElapsedSeconds == 0 && p.RetainedOfflineSeconds == 0)
        && p.DeadlineSeconds == (p.ReplayMinutes == 30 ? 30000 : 7200)
        && p.OfflineDeadlineSeconds == (p.ReplayMinutes == 30 ? 12000 : 1800)
        && p.StopBytes == (DurationRetainedHistory(p) ? 130_000_000_000L : DurationStopBytes(p.ReplayMinutes))
        && p.BudgetBytes == p.StopBytes + 1_000_000_000L
        && p.FloorBytes == 60_000_000_000L && p.RetainedBudgetBytes == (DurationTrainingContinuation(p) ? 60_000_000_000L
            : DurationRetainedHistory(p) ? 106_000_000_000L : 8_000_000_000L)
        && p.OutsideBuildAllowanceBytes == 2_000_000_000L
        && p.AdditionalHistoricalReserveBytes == 0 && p.CopyFallbackReserveBytes == DurationCopyBytes(p.ReplayMinutes)
        && p.SourceComponentBytes == DurationSourceBytes(p.ReplayMinutes) && p.OutputComponentBytes == DurationOutputBytes(p.ReplayMinutes)
        && p.DirectoryLimit == 4096 && p.FileLimit == 49152 && p.FreezeRetainedAllocations && p.ReuseCurrentMetadataHandles
        && !p.RetainActiveOutputHandles && !p.ReadonlyValidationPhases && !p.OperatorForegroundHandoff && !p.UserForegroundHandoff && !p.AutomaticFixtureHandoff
        && p.ReplayExporterRecipe == ReplaySaveExport.PolicyIdentity && p.Files != null && p.EvidenceRoles?.ContainsKey("fresh-candidate-qualification") == true;

    internal static int DurationAdmissionChecks()
    {
        int checks=0;
        void Check(bool value){if(!value)throw new IOException("Duration admission regression.");checks++;}
        bool Rejected(Action action) { try { action(); return false; } catch (IOException) { return true; } }
        Check(!DurationAdmissionKind(null) && !DurationAdmissionKind("unknown"));
        Check(DurationPreparation(null,5) == null && DurationPreparation("unknown",5) == null);
        Check(Rejected(() => DurationPreparation(DurationRetainedHistoryAdmission,30)));
        Check(Rejected(() => DurationPreparation(DurationRetainedHistoryAdmission,2)));
        // The actual retained-pair reader must carry its duration into the
        // shipping index guard. These are the failed audit's realT01 counts.
        var retainedIndex = new ReplayCalibrationIndexReceipt(ReplaySaveExport.BoundedPacketConstruction,1802,4,4,529,0,
            true,true,30000,false,null,true,new(1802,1273,529,true,true,true));
        Check(CompositeIndexValid(retainedIndex,1802,1800));
        Check(!CompositeIndexValid(retainedIndex,1802,300));
        Check(!CompositeIndexValid(retainedIndex with { SourceHandlesClosed = false },1802,1800));
        foreach(var cell in new[]{(Minutes:1,Free:69_375_939_328L,Files:6220),(Minutes:5,Free:88_707_388_416L,Files:10540),(Minutes:30,Free:206_641_473_536L,Files:37540)})
        {
            var p=new CalibrationPlan("unqueried-root","unqueried-engine","unqueried-environment",new(),Order,["unqueried-retained"],0,0,
                MaximumChildren:61,BudgetBytes:DurationStopBytes(cell.Minutes)+1_000_000_000L,StopBytes:DurationStopBytes(cell.Minutes),
                DeadlineSeconds:cell.Minutes==30?30000:7200,RetainedOfflineSeconds:0,MetadataHandleRoots:["unqueried-retained"],ReuseCurrentMetadataHandles:true,
                DirectoryLimit:4096,OutputComponentBytes:DurationOutputBytes(cell.Minutes),RetainedBudgetBytes:8_000_000_000L,
                FileLimit:49152,OfflineDeadlineSeconds:cell.Minutes==30?12000:1800,OutsideBuildAllowanceBytes:2_000_000_000L,FreezeRetainedAllocations:true,
                ReplayExporterRecipe:ReplaySaveExport.PolicyIdentity,ValidationChildren:18,SourceComponentBytes:DurationSourceBytes(cell.Minutes),
                CompositeQualification:true,CompositeManualForegroundHandoff:true,CompositeExplicitForegroundRelease:true,
                CopyFallbackReserveBytes:DurationCopyBytes(cell.Minutes))
                {ReplayMinutes=cell.Minutes,FreshCandidateAdmission=DurationAdmission,EvidenceRoles=new(){["fresh-candidate-qualification"]="unqueried-role"}};
            Check(DurationEnvelopeValid(p));
            var shape = DurationPreparation(DurationAdmission,cell.Minutes).Value;
            Check(shape.Minutes == cell.Minutes && shape.Source == p.SourceComponentBytes && shape.Output == p.OutputComponentBytes
                && shape.Copy == p.CopyFallbackReserveBytes && shape.Scratch == QualificationScratchBytes(p) && shape.Trace == QualificationTraceBytes(p));
            Check(new QualificationCommitments(p.Order,shape.Source,shape.Output,shape.Copy,shape.Scratch,shape.Trace)
                .InitialRequiredFreeBytes(61_000_000_000L) == cell.Free);
            Check(QualificationFreeRequirement(p)==cell.Free && QualificationFileForecast(p)==cell.Files);
            Check(QualificationTraceBytes(p)==(cell.Minutes==30?2_457_616_384L:589_840_384L));
            Check(p.RetainedBudgetBytes+p.OutsideBuildAllowanceBytes+NewCohortBytes(p)<p.StopBytes);
            Check(CapacityFileForecastFits(p.FileLimit-cell.Files,p.FileLimit,cell.Files));
            Check(!CapacityFileForecastFits(p.FileLimit-cell.Files+1,p.FileLimit,cell.Files));
            foreach(var bad in new[]{p with{ReplayMinutes=2},p with{MaximumChildren=62},p with{Order=Order.Reverse().ToArray()},
                p with{SourceComponentBytes=p.SourceComponentBytes-1},p with{OutputComponentBytes=p.OutputComponentBytes-1},
                p with{CopyFallbackReserveBytes=0},p with{CompositeTrainingRoot="old-model"},p with{CompositeExplicitForegroundRelease=false},
                p with{DeadlineSeconds=p.DeadlineSeconds-1},p with{OfflineDeadlineSeconds=p.OfflineDeadlineSeconds-1},p with{RetainedChildren=1}})
                Check(!DurationEnvelopeValid(bad));
            Check(InspectionFrames(cell.Minutes*60).SequenceEqual(new[]{15,cell.Minutes*1800,cell.Minutes*3600-15}));
            if (cell.Minutes is 1 or 5)
            {
                // The failed30-minute roots remain immutable and physically
                // retained. This fresh cell imports no samples/model or time
                // credit; only its explicit whole-history ceiling is larger.
                var retained = p with { FreshCandidateAdmission = DurationRetainedHistoryAdmission,
                    RetainedBudgetBytes = 106_000_000_000L, StopBytes = 130_000_000_000L, BudgetBytes = 131_000_000_000L };
                Check(DurationEnvelopeValid(retained));
                Check(DurationPreparation(DurationRetainedHistoryAdmission,cell.Minutes) == shape);
                long aliasNamespace = checked(6L * ReplayReferenceModel.InputCountLimit(cell.Minutes) * 65536);
                Check(QualificationAliasNamespaceBytes(retained) == aliasNamespace);
                Check(QualificationFreeRequirement(retained) == cell.Free + 2 * aliasNamespace
                    && QualificationFileForecast(retained) == cell.Files + 6 * ReplayReferenceModel.InputCountLimit(cell.Minutes));
                Check(NewCohortBytes(retained) == NewCohortBytes(p) + aliasNamespace);
                var now = new DateTime(2026,11,13,20,53,20,DateTimeKind.Utc);
                var originalPurpose = new DateTime(2026,11,13,21,36,30,DateTimeKind.Utc);
                Check(FixedPurposeWindowValid(retained,originalPurpose,now.AddHours(10),now) == (cell.Minutes == 1));
                int minimum = cell.Minutes == 1 ? 2400 : 3600;
                Check(MinimumFixedPurposeSeconds(retained) == minimum && MinimumFixedPurposeSeconds(p) == 3600);
                Check(FixedPurposeWindowValid(retained,now.AddSeconds(minimum),now.AddHours(10),now));
                Check(!FixedPurposeWindowValid(retained,now.AddSeconds(minimum).AddTicks(-1),now.AddHours(10),now));
                Check(FixedPurposeWindowValid(retained,now.AddSeconds(7200),now.AddHours(10),now));
                Check(!FixedPurposeWindowValid(retained,now.AddSeconds(7200).AddTicks(1),now.AddHours(10),now));
                Check(!FixedPurposeWindowValid(retained,DateTime.SpecifyKind(originalPurpose,DateTimeKind.Unspecified),now.AddHours(10),now));
                Check(!FixedPurposeWindowValid(retained,now.AddSeconds(minimum),now.AddSeconds(minimum),now));
                Check(retained.RetainedBudgetBytes + retained.OutsideBuildAllowanceBytes + NewCohortBytes(retained) < retained.StopBytes);
                foreach (var bad in new[] { retained with { ReplayMinutes = 30 }, retained with { CompositeTrainingRoot = "old-training" },
                    retained with { Order = Order.Skip(3).ToArray() }, retained with { RetainedChildren = 29 },
                    retained with { RetainedElapsedSeconds = 1 }, retained with { RetainedOfflineSeconds = 1 },
                    retained with { ValidationChildren = 9 }, retained with { RetainedBudgetBytes = 106_000_000_001L },
                    retained with { RetainedBudgetBytes = 8_000_000_000L }, retained with { StopBytes = retained.StopBytes - 1 },
                    retained with { BudgetBytes = retained.BudgetBytes + 1 }, retained with { SourceComponentBytes = retained.SourceComponentBytes + 1 },
                    retained with { OutputComponentBytes = retained.OutputComponentBytes + 1 }, retained with { DeadlineSeconds = 7201 } })
                    Check(!DurationEnvelopeValid(bad));
            }
            if (cell.Minutes == 30)
            {
                var recovery = p with { FreshCandidateAdmission = DurationTrainingAdmission, CompositeTrainingRoot = @"D:\retained-training",
                    Order = Order.Skip(3).ToArray(), RetainedChildren = 29, RetainedElapsedSeconds = 9786.7313576,
                    RetainedOfflineSeconds = 749.5707710000011, ValidationChildren = 9, RetainedBudgetBytes = 60_000_000_000L };
                Check(DurationEnvelopeValid(recovery));
                Check(QualificationFreeRequirement(recovery) == 155_641_473_536L && NewCohortBytes(recovery) == 64_320_736_768L);
                Check(QualificationFileForecast(recovery) == 19414);
                Check(recovery.RetainedBudgetBytes + recovery.OutsideBuildAllowanceBytes + NewCohortBytes(recovery) < recovery.StopBytes);
                foreach (var bad in new[] { recovery with { ReplayMinutes = 5 }, recovery with { CompositeTrainingRoot = null },
                    recovery with { Order = Order }, recovery with { RetainedChildren = 0 }, recovery with { RetainedElapsedSeconds = 0 },
                    recovery with { RetainedOfflineSeconds = 0 }, recovery with { RetainedBudgetBytes = 8_000_000_000L },
                    recovery with { ValidationChildren = 18 } }) Check(!DurationEnvelopeValid(bad));
            }
        }
        Console.WriteLine($"Passed {checks} pure duration-specific admission, whole/free/scratch/trace/file bounds and source/heldout separation controls; no files/native/media.");
        return 0;
    }
}
