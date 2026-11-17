using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace F10Recorder;

internal static class WorkerChecks
{
    internal static bool TryCalibrationFixture(string[] args, out string root, out string label, out bool manual)
    {
        root = label = null; manual = false;
        if (args.Length is not (3 or 4) || args[0] != "--calibration-fixture" || !CalibrationPattern.ValidLabel(args[2])
            || string.IsNullOrWhiteSpace(args[1]) || args.Length == 4 && args[3] != "manual-handoff") return false;
        try { root = Path.GetFullPath(args[1]); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        label = args[2]; manual = args.Length == 4; return true;
    }
    static string data, desktop;
    static bool live;
    static bool gpuFixture;
    static int liveFps = 60;
    const string StartupKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    readonly record struct StartupState(object Value, RegistryValueKind Kind);
    static StartupState startupBefore;
    static readonly Dictionary<string, object> report = new();
    static readonly List<string> clips = [];
    static readonly List<RecorderMetrics> samples = [];
    static readonly List<object> saves = [];
    [STAThread]
    internal static int Main(string[] args)
    {
        if(args is ["--replay-presentation-offline-plan"]){Console.WriteLine("Generated independent presentation model/reader/lifecycle checks;no GPU/desktop/device/UI/audio/codec/native ownership action. One helper30s/256MiB,one generated observer thread at a time,<=1MiB fresh report.");return 0;}
        if(args is ["--replay-presentation-offline-checks",var presentationChecks])return CalibrationPresentationChecks.Run(presentationChecks);
        if(args is ["--replay-presentation-protection-plan"]){Console.WriteLine("Generated v5 fixed-monotonic-H03 same-ID/protection-policy controls only;30s/256MiB/2048handles/1MiB fresh report;no native/device/desktop/audio/UI/codec/children. --replay-presentation-protection-checks <fresh-report>.");return 0;}
        if(args is ["--replay-presentation-protection-checks",var protectionChecks])return CalibrationPresentationChecks.RunProtection(protectionChecks);
        if(args is ["--replay-presentation-metadata-plan"]){Console.WriteLine("Generated v5/v6 fixed-fixture metadata through actual ReadClosed parser, explicit method reader and early retained method guard;one helper30s/512MiB,zero children/GPU/UI/audio/codec/capture,<=1MiB/64files fresh ignored root. Reads its generated9-file reference plus generated method policy,never original media;no NativeFileSink/native ownership test. --replay-presentation-metadata-checks <fresh-root>. Exact source/route review before invocation.");return 0;}
        if(args is ["--replay-presentation-metadata-checks",var presentationMetadata])return CalibrationPresentationMetadataChecks.Run(presentationMetadata);
        if(args is ["--replay-presentation-files-plan"]){Console.WriteLine("Separate NOT-YET-ADMITTED native FILE fixtures:one helper30swork/60swhole,512MiB/4096handles,<=1MiB fresh ignored output,60GBfloor;guarded trace write/flush/hash,write/rename denial,real partial-publish polling with delete-sharing observer. Zero desktop/device/UI/audio/codec/capture. --replay-presentation-files-checks <fresh-root>. Independent exact source/effects/launcher review required before execution.");return 0;}
        if(args is ["--replay-presentation-files-checks",var presentationFiles])return CalibrationPresentationFileChecks.Run(presentationFiles);
        if(args is ["--replay-presentation-method-plan",var methodPath,var methodHash])
        {
            var method=PresentationMethod.Read(methodPath,methodHash);
            Console.WriteLine(JsonSerializer.Serialize(new{method,methodSha256=methodHash,primaryFreshness=method.Method,
                gdi="Retained diagnostic comparisons;not old-C00 acceptance",extraThreads=1,extraD3D11Devices=1,extraDuplicationClients=1,
                roiWidth=576,roiHeight=48,bytesPerReadback=110592,maximumRoiBytes=16588800000L,maximumTraceBytes=20971520,immutableReferenceFiles=25,
                referenceReserveBytes=32000000,originalEndpointFrames=1,avBoundSeconds=.05,sharedUncertaintiesRetained=true,extraNativeQpcTicks=1,
                nativeReferenceUncertainty=method.AllowsAdjacentTransition
                    ?"Conservative visual residual endpoints for uncertain births, plus one full QPC tick; GDI midpoint width is diagnostic"
                    :"One full QPC tick;GDI midpoint half-width diagnostic only;legacy GDI retains its width",
                referenceQualificationPhase=CalibrationReferencePhase.Identity,preAdmissionDiagnosticMaximumBytes=2113536,audioStartupInsideReadySeconds=5,
                previewNativeCalls=0,mediaAdmitted=false,extraAcquisitionNeedsDecision=true}));return 0;
        }
        if(args is ["--replay-same-C00-fixture",var presentationRoot,"H03","5","--presentation-reference",var presentationPath,var presentationHash])
            return CalibrationPattern.Run(Path.GetFullPath(presentationRoot),"H03",true,CalibrationForeground.UserHandoffSeconds+420+15,true,new(presentationPath,presentationHash,PresentationMethod.Read(presentationPath,presentationHash)));
        if (args is ["--replay-same-C00-fixture", var referenceRoot, "H03", "5"])
            return CalibrationPattern.Run(Path.GetFullPath(referenceRoot), "H03", true, CalibrationForeground.UserHandoffSeconds + 420 + 15, sourceReference: true);
        if (args is ["--replay-same-C00-freeze", var sameRequest, var sameHash, var sameOutput])
            return ReplayCalibrationRun.FreezeCoverage(Path.GetFullPath(sameRequest), sameHash, Path.GetFullPath(sameOutput), sameCapture: true);
        if (args is ["--replay-same-C00-issue", var sameIssueRequest, var sameIssueHash, var sameCatalog])
            return ReplayCalibrationRun.IssueSameCapture(sameIssueRequest, sameIssueHash, sameCatalog);
        if(args is ["--replay-same-C00-offline-plan"]){Console.WriteLine("Pure generated timing/PCM/metadata/byte rules; no file payload/native/device/media/codec/capture. One helper30s/256MiB/2048handles; <=1MiB fresh report. --replay-same-C00-offline-checks <fresh-report>.");return 0;}
        if(args is ["--replay-same-C00-offline-checks",var sameReport])return ReplayCalibrationRun.SameCaptureOfflineChecks(Path.GetFullPath(sameReport));
        if(args is ["--replay-same-C00-plan-shape",var sameShape])return ReplayCalibrationRun.SameCapturePlanShape(Path.GetFullPath(sameShape));
        if(args is ["--replay-same-C00-freeze-request",var sameConfig,var sameRequestOut])return ReplayCalibrationRun.SameCaptureFreezeRequest(Path.GetFullPath(sameConfig),Path.GetFullPath(sameRequestOut));
        if(args is ["--replay-same-C00-envelope-checks",var envelopeTemplate,var envelopeReport])return ReplayCalibrationRun.SameCaptureEnvelopeChecks(Path.GetFullPath(envelopeTemplate),Path.GetFullPath(envelopeReport));
        if(args is ["--replay-same-C00-accept",var sameData,var sameDataHash,var sameConfirmation,var sameConfirmationHash,var sameManifest,var sameManifestHash,var sameReviewer,var sameAcceptance])
            return ReplayCalibrationRun.AcceptSameCapture(sameData,sameDataHash,sameConfirmation,sameConfirmationHash,sameManifest,sameManifestHash,sameReviewer,sameAcceptance);
        if (args is ["--replay-coverage-plan"])
        { Console.WriteLine("V9 coverage controls: controlled private JSON/ACL/NTFS receipts and 302 one-byte dummy inputs; actual serialized loader and profile admission/resume/guard withdrawal, plus unmodelled C00 private-ledger staging sentinel before codec. No engine/codec/media payload/capture/audio/UI/worker/account activation. One helper, 45s work/60s outer, 512MiB/4096 handles, 8MiB private logical output, 60GB floor. Run --replay-coverage-checks <fresh-root>. Future --replay-coverage-freeze/issue are metadata only; C00 uses existing final calibration Run with separate recording authorization and exact-plan review; no future operation is authorized here."); return 0; }
        if (args is ["--replay-coverage-checks", var coverageRoot]) return ReplayCalibrationCatalogChecks.CoverageChecks(Path.GetFullPath(coverageRoot));
        if (args is ["--replay-coverage-C00-admission-checks"]) return ReplayCalibrationRun.ContextAdmissionChecks();
        if (args is ["--replay-coverage-parent-checks", var parentRoot, var fivePath, var fiveHash, var thirtyPath, var thirtyHash, var omissionReview])
            return ReplayCalibrationCatalogChecks.CoverageParentChecks(Path.GetFullPath(parentRoot), fivePath, fiveHash, thirtyPath, thirtyHash, omissionReview);
        if (args is ["--replay-coverage-freeze", var freezeRequest, var freezeHash, var freezeOutput])
            return ReplayCalibrationRun.FreezeCoverage(Path.GetFullPath(freezeRequest), freezeHash, Path.GetFullPath(freezeOutput));
        if (args is ["--replay-coverage-issue", var issueInput, var issueHash, var issueRoot])
            return ReplayCalibrationRun.IssueCoverage(Path.GetFullPath(issueInput), issueHash, Path.GetFullPath(issueRoot));
        if (args is ["--replay-coverage-retained-plan"])
        { Console.WriteLine("FUTURE fixed final60s of original qualified30m H03: hash-bound original ordered selection and reviewed archive mapping, <=65complete inputs/200MB, one normal/exact pair, independent fresh decodes and unchanged source/clock checks. No acquisition. Existing sampler/shared capacity/native ledger/full physical source+two-output+copy+scratch+trace+closure promises; seven total roles,600swhole,130s/export60s/inspection, explicit retained/allocation/8Kfile128dir ceilings and60GBfloor. --replay-coverage-retained-pair <reviewed-plan> <sha256>. Requires separate exact-plan approval; it is never run by coverage-checks."); return 0; }
        if (args is ["--replay-coverage-retained-pair", var retainedPlan, var retainedHash])
            return ReplayCalibrationRun.RetainedCoveragePair(Path.GetFullPath(retainedPlan), retainedHash);
        if(args is ["--replay-paired-selection-plan"]){Console.WriteLine("Pure qualification-source adapter/production interval replay: two deterministic post-prune cases, including the exact H02 timestamps. No files, native operations, children, media, capture, audio, UI or catalog. Run --replay-paired-selection-checks.");return 0;}
        if(args is ["--replay-paired-selection-checks"])return ReplayPairedSelectionChecks.Run();
        if(args is ["--replay-paired-selection-native-plan"]){Console.WriteLine("Three actual Cache requests with 3x1024B nonplayable inputs, native snapshots/hardlinks and paired lease; protected view and changed interval/object negatives. Deliberately stops before codec/publication. <=15swork/25swhole,256MiB4096handles,1MiBroot/256files/32dirs; zerochildren/capture/audio/UI/catalog. Run --replay-paired-selection-native-checks <fresh-root>.");return 0;}
        if(args is ["--replay-paired-selection-native-checks",var pairRoot])return ReplayPairedSelectionChecks.RunNative(Path.GetFullPath(pairRoot));
        if(args is ["--replay-paired-alias-native-plan"]){Console.WriteLine("Actual Cache/native-rooted protection with3x1024B dummyinputs andone same-object alias; strict realwrong-object/interval negatives, precodec markers. <=30swork40outer512MiB4096handles4MiB/512files128dirs; zerochildren/media/capture/audio/UI/worker/catalog. --replay-paired-alias-native-checks <fresh-root>.");return 0;}
        if(args is ["--replay-paired-alias-native-checks",var aliasRoot])return ReplayPairedAliasChecks.Run(Path.GetFullPath(aliasRoot));
        if(args is ["--replay-collector-dispose-plan"]){Console.WriteLine("Actual generic-exception Sampler disposal: healthy writer receipt/repeated-success preservation and real CreateNew receipt conflict/repeated-failure/private-claim retention. Two4KiB dummyfiles, real observers/query/writer; no producer. <=15swork25outer512MiB4096handles2MiB/512files128dirs; zerochildren/media/capture/audio/UI/worker/catalog. --replay-collector-dispose-checks <fresh-root>.");return 0;}
        if(args is ["--replay-collector-dispose-checks",var disposeRoot])return ReplayCollectorDisposeChecks.Run(Path.GetFullPath(disposeRoot));
        if(args is ["--replay-failure-observer-plan"]){Console.WriteLine("Four tiny actual collector-cleanup/sampler/writer/private-ledger cases: pending drain, producer still alive, observer still blocked, writer flush failure. 4x4096B nonplayable inputs; up to6 task managed threads, zerochildren/capture/media/audio/UI/catalog; <=30swork/40swhole,512MiB4096handles,2MiB/512files/128directories. Run --replay-failure-observer-checks <fresh-root>.");return 0;}
        if(args is ["--replay-failure-observer-checks",var observerRoot])return ReplayFailureObserverChecks.Run(Path.GetFullPath(observerRoot));
        if(args is ["--replay-duration-admission-checks"])return ReplayCalibrationRun.DurationAdmissionChecks();
        if(args is ["--replay-source-witness-probe",var witnessPlan,var witnessHash])return ReplaySourceWitnessProbe.Run(Path.GetFullPath(witnessPlan),witnessHash);
        if(args is ["--replay-calibration-duration-fixture",var durationRoot,var durationLabel,var durationMinutes]
            && int.TryParse(durationMinutes,out int minutes) && ReplayReferenceModel.SupportedMinutes(minutes) && CalibrationPattern.ValidLabel(durationLabel))
            return CalibrationPattern.Run(Path.GetFullPath(durationRoot),durationLabel,manualHandoff:true,
                lifetimeSeconds:CalibrationForeground.UserHandoffSeconds+minutes*60+120+15);
        if(args is ["--replay-owned-history-admission-checks"])return ReplayCalibrationRun.OwnedHistoryAdmissionChecks();
        if(args is ["--replay-catalog-acquisition-checks"])return ReplayCalibrationCatalogChecks.AcquisitionChecks();
        if(args is ["--replay-owned-history-outside-plan"])
        {Console.WriteLine("Read-only native allocation/hash evidence for<=128 explicitlydeclared fixedfiles/1GB plus<=512tiny metadatafiles in<=4ownedwriterroots/32dirs/64MB. Creates twoJSONrecords innewadmissionroot; no child/capture/audio/UI/codec/catalog/settings. Nativepreparation/exactinputreview and finiteouterbounds required.");return 0;}
        if(args is ["--replay-owned-history-outside-prepare",var outsideAdmission])return ReplayCalibrationRun.PrepareOwnedHistoryOutsideEvidence(Path.GetFullPath(outsideAdmission));
        if(args is ["--replay-completed-history-file-plan"])
        {Console.WriteLine("One retained301input/<=1GB cohort; control,actualhistory300prefixbuild,hitwith1miss,newestprepare,allhit,control,retirement. Existing shipping owner/parser/boundedextraction with freshallsourcehash. No capture/audio/UI/codec/MP4/Saved/catalog/settings/OBS. Onehelper,75swork90outer5drain,512MiB/4096handles,64MiBreports,60GBfloor. Exactplanreview required.");return 0;}
        if(args is ["--replay-completed-history-file-run",var historyPlan,var historyPlanHash])return ReplayCompletedHistoryFilePilot.Run(Path.GetFullPath(historyPlan),historyPlanHash);
        if(args is ["--replay-completed-history-plan"])
        {Console.WriteLine("14 tiny native completed-history/actual-Cache controls; structural coded-byte fixtures, intercepted codec calls, no child/capture/audio/UI/encoder/catalog/installed settings. Includes rolling-owner versus hardlink-alias retirement. One helper, <=60s work/75s outer/5s drain, <=1MiB fixture output/64MiB total diagnostic cap, 512MiB working set/4096handles/60GB floor. Run --replay-completed-history-checks <fresh-root> after exact-plan independent review.");return 0;}
        if(args is ["--replay-completed-history-checks",var historyRoot])return ReplayCompletedHistoryChecks.Run(Path.GetFullPath(historyRoot));
        if (args is ["--replay-qualification-commitment-checks"]) return QualificationCommitments.Checks();
        if (args.Length == 1 && args[0] == "--replay-reservation-anchor-plan")
        {
            Console.WriteLine("Corrected original ancestor oracle:8directexisting/late peer/producercases, changedID, rename denial, stickyfailure;3actualCache checked-publication cases. Tiny native private files/locks only; <=10s/256MiB/2048handles, <=1MiBroot; nochild/media/capture/audio/UI/catalog. Run --replay-reservation-anchor-checks <fresh-root>."); return 0;
        }
        if (args.Length == 2 && args[0] == "--replay-reservation-anchor-checks") return ReplayReservationAnchorChecks.Run(Path.GetFullPath(args[1]));
        if (args.Length == 1 && args[0] == "--replay-capacity-admission-checks") return ReplayCalibrationRun.CapacityAdmissionChecks();
        if (args.Length == 1 && args[0] == "--replay-capacity-export-plan")
        {
            Console.WriteLine("Six tiny synthetic actual-Cache saves:609ms/blocked capacity, blocked producer seal, controlled write/finalization/after-publication cleanup failures. Native private ledger/guard paths and one empty job, <=1MiB, <=25s; helper threads/tasks only, no children/playablemedia/capture/audio/UI/catalog/normal-profile changes. Run --replay-capacity-export-checks <fresh-root>."); return 0;
        }
        if (args.Length == 2 && args[0] == "--replay-capacity-export-checks") return ReplayOutputValidationChecks.RunCapacity(Path.GetFullPath(args[1]));
        if (args.Length == 1 && args[0] == "--replay-capacity-monitoring-plan")
        {
            Console.WriteLine("Six tiny private native ledger/guard cases; controlled609ms and <=5s released capacity/latch/seal delays, concurrent generation closure, one pending query, own managed supervisor and one empty job; <=20s, <=256KiB, no children/capture/audio/UI/catalog/normal-profile changes. Run --replay-capacity-monitoring-checks <fresh-root>.");
            return 0;
        }
        if (args.Length == 2 && args[0] == "--replay-capacity-monitoring-checks") return ReplayCapacityMonitoringChecks.Run(Path.GetFullPath(args[1]));
        if (args.Length == 1 && args[0] == "--replay-capacity-sampling-plan")
        {
            Console.WriteLine("Three tiny native ledger/identity cases with controlled capacity, including concurrent ledger epoch revalidation; <=256KiB task-private files, <=30s, no children/capture/audio/UI/catalog/normal-profile changes. Run --replay-capacity-sampling-checks <fresh-root>.");
            return 0;
        }
        if (args.Length == 2 && args[0] == "--replay-capacity-sampling-checks") return ReplayCapacitySamplingChecks.Run(Path.GetFullPath(args[1]));
        if (args.Length == 3 && args[0] == "--replay-creator-proof-checks") return ReplayCalibrationRun.CreatorProofChecks(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
        if (args.Length == 3 && args[0] == "--replay-calibration-admission-checks") return ReplayCalibrationAdmissionChecks.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
        if (args.Length == 3 && args[0] == "--replay-calibration-preflight") return ReplayCalibrationRun.Run(Path.GetFullPath(args[1]), args[2], CalibrationAdmissionMode.ValidateOnly);
        if (args.Length == 3 && args[0] == "--replay-calibration-check-launch") return ReplayCalibrationRun.Run(Path.GetFullPath(args[1]), args[2], CalibrationAdmissionMode.CheckLaunch);
        if (args.Length == 2 && args[0] == "--replay-observer-delivery-checks") return CalibrationObserverFollowupChecks.Run(Path.GetFullPath(args[1]), reuseBlockedEvidence: true);
        if (args.Length == 1 && args[0] == "--replay-observer-followup-plan")
        {
            Console.WriteLine("Eight native observer cases, four scans each, alternating detailed diagnostics off/on; complete process CPU/allocation/I/O and trace/report/closure cost. One blocked real inventory handle/Gate,3s finite release, dummy producer and private reservation. <=2MiB dummy payload; no child/capture/engine/audio/UI/catalog;500ms unchanged. Execute --replay-observer-followup-checks <fresh-artifacts-root> once under the60s/70s,512MiB/100MB controller.");
            return 0;
        }
        if (args.Length == 2 && args[0] == "--replay-observer-followup-checks") return CalibrationObserverFollowupChecks.Run(Path.GetFullPath(args[1]));
        if (args.Length == 1 && args[0] == "--replay-observer-mitigation-plan")
        {
            Console.WriteLine("Finite nonmedia checks:32 paired synthetic accounting iterations;20s measurement guard;two sequential4KiB task-owned native metadata fixtures;10,000 bounded summary updates;no child processes/capture/engine/audio/UI/catalog;500ms unchanged. Supply --replay-observer-mitigation-checks <fresh-artifacts-root> to execute.");
            return 0;
        }
        if (args.Length == 2 && args[0] == "--replay-observer-mitigation-checks") return CalibrationObserverMitigationChecks.Run(Path.GetFullPath(args[1]));
        if (args.Length == 1 && args[0] == "--staged-process-checks") { ReplayStagedProcessChecks.Run(); return 0; }
        if (args.Length == 1 && args[0] == "--replay-staging-admission-plan")
        { Console.WriteLine("Actual Cache unmeasured-staging, native workspace transfer/cleanup, async producer-callback/held-authority, late-capacity owner-thread cleanup/re-admission and reserved startup during tail wait and post-tail complete protection controls; tiny dummy inputs and three controlled2048byte publications, <=1MiB/25seconds, simulated transports, empty jobs and one bounded managed completion thread, no child/media/capture/audio/UI/profile/catalog action. Run --replay-staging-admission-checks <fresh-root>."); return 0; }
        if (args.Length == 2 && args[0] == "--replay-staging-admission-checks") return ReplayStagingAdmissionChecks.Run(Path.GetFullPath(args[1]));
        if (args.Length == 1 && args[0] == "--replay-staging-protection-costs-plan")
        { Console.WriteLine("File-only301-input native Cache OFF/ON/ON/OFF attribution plus301 direct preparation.Observe calls; canceled simulated transport/controlled dummy validation, <=32MiB/25seconds (external40second deadline), no child process/media/worker/capture/audio/UI/profile/catalog changes. Run --replay-staging-protection-costs <fresh-artifact-root> using pinned selected Windows/Core bytes; media qualification remains separate."); return 0; }
        if (args.Length == 2 && args[0] == "--replay-staging-protection-costs") return ReplayStagingProtectionCosts.Run(Path.GetFullPath(args[1]));
        if (args.Length == 1 && args[0] == "--replay-staging-evidence-costs-plan")
        { Console.WriteLine("File-only untimed301-throw shape check then local/full/full/local301-call native output-evidence timing with counter DETACHED on idle private Cache via reflection; <=1MiB/10seconds (external40second backstop), no child/media/accepted save/capture/audio/UI/profile/catalog. Run --replay-staging-evidence-costs <fresh-artifact-root> on unchanged product bytes."); return 0; }
        if (args.Length == 2 && args[0] == "--replay-staging-evidence-costs") return ReplayStagingProtectionCosts.EvidenceCosts(Path.GetFullPath(args[1]));
        if (args.Length == 3 && args[0] == "--replay-save-operation-checks") return RootedOperationChecks.Run(Path.GetFullPath(args[1]), args[2]);
        if (args.Length == 1 && args[0] == "--replay-directory-buffer-checks") return ReplayDirectoryBufferChecks.Run();
        if (args.Length == 3 && args[0] == "--replay-directory-scan-benchmark") return ReplayDirectoryScanBenchmark.Run(Path.GetFullPath(args[1]), args[2]);
        if (args.Length == 3 && args[0] == "--replay-validation-handoff-probe") return ReplayValidationHandoffProbe.Run(Path.GetFullPath(args[1]), args[2]);
        if (args.Length == 1 && args[0] == "--calibration-validation-work-checks") return CalibrationValidationWorkChecks.Run();
        if (args.Length == 1 && args[0] == "--calibration-producer-absence-checks") return CalibrationProducerAbsenceChecks.Run();
        if (args.Length == 3 && args[0] == "--replay-decode-handoff-probe") return ReplayDecodeHandoffProbe.Run(Path.GetFullPath(args[1]), args[2]);
        if (args.Length == 2 && args[0] == "--replay-maintenance-inventory-checks") return ReplayMaintenanceInventoryChecks.Run(Path.GetFullPath(args[1]));
        if (args.Length == 2 && args[0] == "--replay-mp4-metadata-costs") return ReplayMp4MetadataCosts.Run(Path.GetFullPath(args[1]));
        if (args.Length == 3 && args[0] == "--replay-closed-input-trace") return ReplayClosedInputTrace.Run(Path.GetFullPath(args[1]), args[2]);
        if (args.Length == 4 && args[0] == "--replay-mux-entry-comparison") return ReplayMuxEntryComparison.Run(Path.GetFullPath(args[1]), args[2], args[3]);
        if (args.Length == 3 && args[0] == "--replay-composite-budget-checks") return ReplayCalibrationRun.CompositeBudgetChecks(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
        if (args.Length == 3 && args[0] == "--replay-admission-readiness-checks") return ReplayAdmissionReadinessChecks.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
        if (args.Length == 3 && args[0] == "--replay-reference-envelope-diagnostic") return ReplayReferenceEnvelopeDiagnostic.Run(Path.GetFullPath(args[1]), args[2]);
        if (args.Length == 3 && args[0] == "--replay-post-close-phase-pilot") return ReplayPostClosePhasePilot.Run(Path.GetFullPath(args[1]), args[2]);
        if (args.Length == 1 && args[0] == "--replay-outside-partition-checks") return ReplayCalibrationRun.OutsidePartitionChecks();
        if (args.Length == 1 && args[0] == "--replay-calibration-publication-checks") return CalibrationPublicationChecks.Run();
        if (args.Length == 1 && args[0] == "--replay-construction-closure-checks") return ReplayConstructionClosureChecks.Run();
        if (args.Length == 1 && args[0] == "--replay-worker-pilot-manifest-checks") return ReplayWorkerPilotManifestChecks.Run();
        if (args.Length == 2 && args[0] == "--replay-outside-metadata-checks") return ReplayCalibrationRun.OutsideBaselineMetadataChecks(Path.GetFullPath(args[1]));
        if (args.Length == 1 && args[0] == "--replay-capacity-drain-plan")
        {
            Console.WriteLine("Separate real-capacity-expiry control; primary desktop/default system audio with15s acquisition deadline plus25s owned stop/cleanup reserve, owned235s fixture/180s handoff,300s whole,256MiB/192MiB stop/60GB floor. Actual qualified private catalog/engine/coordinator, one delayed BASE capacity query <=8s, exact FFmpeg/audio absence and retained growth claim, explicit closure-only retry, durable stop retained. Prepare exact bindings with --replay-capacity-drain-prepare <plan> <root> <engine-root> <qualified-product> <production-profile> <private-catalog>. New capture authorization and independent exact-plan review required before Run."); return 0;
        }
        if (args.Length == 2 && args[0] == "--replay-capacity-drain-fixture") return ReplayCapacityDrainPilot.Fixture(Path.GetFullPath(args[1]));
        if (args.Length == 1 && args[0] == "--replay-capacity-drain-claim-checks") return ReplayCapacityDrainPilot.ClaimChecks();
        if (args.Length == 7 && args[0] == "--replay-capacity-drain-prepare") return ReplayCapacityDrainPilot.Prepare(args[1], args[2], args[3], args[4], args[5], args[6]);
        if (args.Length == 3 && args[0] == "--replay-capacity-drain-run") return ReplayCapacityDrainPilot.Run(Path.GetFullPath(args[1]), args[2]);
        if (args.Length == 1 && args[0] == "--replay-staging-native-plan")
        {
            Console.WriteLine("Generated closed-media native controls only: two7s synthetic H264/AAC fixtures, actual staged FFmpeg/pipe, valid unsupported longGOP exactfallback+fullprepublicationdecode+independentpictureIDs, prefeed andpacedrealfeed cancellation/noMP4/actualchildfeedjoin. No desktop/system/microphone capture or playback/UI/catalog/profile/startup/vendor changes.120swhole,512MiBhard384MiBstop60GBfloor,512files64dirs,8mediachildren+helper+console+prep+outer12. Prepare --replay-staging-native-prepare <plan> <root> <engine-root> <frozen-product> <production-profile>; exact independentreview required beforeRun."); return 0;
        }
        if (args.Length == 6 && args[0] == "--replay-staging-native-prepare") return ReplayStagingNativeChecks.Prepare(args[1], args[2], args[3], args[4], args[5]);
        if (args.Length == 1 && args[0] == "--replay-staging-native-identity-checks") return ReplayStagingNativeChecks.IdentityChecks();
        if (args.Length == 3 && args[0] == "--replay-staging-native-run") return ReplayStagingNativeChecks.Run(Path.GetFullPath(args[1]), args[2]);
        if (args.Length == 1 && args[0] == "--replay-acceptance-ui-plan")
        {
            Console.WriteLine("Real frozen Avalonia App/WorkerRecorder attachment to private PAUSED worker; capture argument observer forbids acquisition. No save/encoder/decoder/capture/playback/catalog/startup/vendor change. Private profile/authority/output, oneUIchild+console+helper+prep+outer5,70swhole50work20cleanup,64MiBhard48MiBstop60GBfloor/256files64dirs. Prepare --replay-acceptance-ui-prepare <plan> <root> <frozen-ui-harness> <frozen-product> <production-profile>; independent exactreview beforeRun. Not resource/physical-feedback/live qualification."); return 0;
        }
        if (args.Length == 6 && args[0] == "--replay-acceptance-ui-prepare") return ReplayAcceptanceUiChecks.Prepare(args[1], args[2], args[3], args[4], args[5]);
        if (args.Length == 3 && args[0] == "--replay-acceptance-ui-run") return ReplayAcceptanceUiChecks.Run(Path.GetFullPath(args[1]), args[2]);
        if (args.Length == 11 && args[0] == "--replay-acceptance-batch-prepare") return ReplayAcceptanceBatch.Prepare(args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9], args[10]);
        if (args.Length == 3 && args[0] == "--replay-observer-gpu-fixture") return ReplayObserverGpuFixture.Run(args[1], args[2]);
        if (args.Length == 2 && args[0] == "--replay-worker-latency-pilot-fixture") return ReplayWorkerLatencyPilot.Fixture(Path.GetFullPath(args[1]));
        if(args is ["--replay-worker-duration-pilot-fixture",var durationFixtureRoot,var durationFixtureMinutes]
            && int.TryParse(durationFixtureMinutes,out int fixtureMinutes) && fixtureMinutes is 1 or 30)
            return ReplayWorkerLatencyPilot.DurationFixture(Path.GetFullPath(durationFixtureRoot),fixtureMinutes);
        if (args.Length == 5 && args[0] == "--replay-private-reference-catalog") return ReplayWorkerLatencyPilot.PreparePrivateCatalog(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), args[3], Path.GetFullPath(args[4]));
        if (args.Length == 7 && args[0] == "--replay-worker-latency-pilot-prepare") return ReplayWorkerLatencyPilot.Prepare(args[1], args[2], args[3], args[4], args[5], args[6]);
        if(args.Length==8 && args[0]=="--replay-worker-duration-pilot-prepare" && int.TryParse(args[7],out int pilotMinutes) && pilotMinutes is 1 or 30)
            return ReplayWorkerLatencyPilot.Prepare(args[1],args[2],args[3],args[4],args[5],args[6],replayMinutes:pilotMinutes);
        if (args.Length == 3 && args[0] == "--replay-worker-latency-pilot-run") return ReplayWorkerLatencyPilot.Run(Path.GetFullPath(args[1]), args[2]);
        if (args.Length == 3 && args[0] == "--replay-storage-capacity-phases") return ReplayStoragePhaseCosts.Run(Path.GetFullPath(args[1]), args[2]);
        if (args.Length == 3 && args[0] == "--replay-calibration-check-costs") return ReplayCalibrationCheckCosts.Run(Path.GetFullPath(args[1]), args[2]);
        if (args.Length == 3 && args[0] == "--replay-storage-check-costs") return ReplayStorageCheckCosts.Run(Path.GetFullPath(args[1]), args[2]);
        if (args.Length == 3 && args[0] == "--replay-cache-guard-costs") return ReplayCacheGuardCosts.Run(Path.GetFullPath(args[1]), args[2]);
        if (args.Length == 1 && args[0] == "--replay-maintenance-cadence-checks") return CalibrationMaintenanceCadence.Checks();
        if (args.Length == 3 && args[0] == "--replay-composite-calibration-prepare") return ReplayCalibrationRun.Prepare(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), composite: true);
        if (args.Length == 4 && args[0] == "--replay-composite-calibration-prepare" && args[3] == "manual-handoff") return ReplayCalibrationRun.Prepare(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), composite: true, compositeManualForegroundHandoff: true);
        if (args.Length == 4 && args[0] == "--replay-composite-calibration-prepare" && args[3] == "controller-release") return ReplayCalibrationRun.Prepare(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), composite: true, compositeManualForegroundHandoff: true, compositeExplicitForegroundRelease: true);
        if (args.Length == 4 && args[0] == "--replay-composite-continuation-prepare") return ReplayCalibrationRun.Prepare(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), true, true, Path.GetFullPath(args[3]));
        if (args.Length == 3 && args[0] == "--replay-calibration-report-checks") return ReplayCalibrationRun.ReportChecks(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
        if (args.Length == 4 && args[0] == "--replay-composite-report-recovery") return ReplayCalibrationRun.RecoverCompositeReport(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), args[3]);
        if (args.Length == 5 && args[0] == "--replay-composite-continuation-prepare" && args[4] == "controller-release") return ReplayCalibrationRun.Prepare(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), true, true, Path.GetFullPath(args[3]), compositeExplicitForegroundRelease: true);
        if (args.Length == 5 && args[0] == "--replay-composite-continuation-checks") return ReplayCalibrationRun.CheckCompositeTraining(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), args[3], args[4]);
        if (args.Length == 3 && args[0] == "--replay-composite-storage-rehearsal") return ReplayCalibrationRun.RehearseComposite(Path.GetFullPath(args[1]), args[2]);
        if (args.Length == 3 && args[0] == "--replay-composite-history-probe") return ReplayCalibrationRun.ProbeCompositeHistory(Path.GetFullPath(args[1]), args[2]);
        if (args.Length == 2 && args[0] == "--replay-decode-lease-child") return ReplayDecodeLeaseChecks.Child(args[1]);
        if (args.Length == 2 && args[0] == "--replay-decode-lease-retention") return ReplayDecodeLeaseChecks.Run(args[1]);
        if (args.Length == 4 && args[0] == "--replay-aac-copy-prepare") return ReplayAacCopyPilot.Prepare(args[1], args[2], args[3]);
        if (args.Length == 4 && args[0] == "--replay-packet-mux-prepare") return ReplayAacCopyPilot.Prepare(args[1], args[2], args[3], packetMux: true);
        if (args.Length == 3 && args[0] == "--replay-aac-copy-run") return ReplayAacCopyPilot.Run(args[1], args[2]);
        if (args.Length == 5 && args[0] == "--replay-intra-short-prepare") return ReplayIntraShortPilot.Prepare(args[1], args[2], args[3], args[4]);
        if (args.Length == 3 && args[0] == "--replay-intra-short-run") return ReplayIntraShortPilot.Run(args[1], args[2]);
        if (args.Length == 5 && args[0] == "--replay-audio-shards-prepare") return ReplayAudioShardPilot.Prepare(args[1], args[2], args[3], args[4]);
        if (args.Length == 3 && args[0] == "--replay-audio-shards-run") return ReplayAudioShardPilot.Run(args[1], args[2]);
        if (args.Length == 2 && args[0] == "--replay-memory-observation-check") return ReplayMemoryObservationChecks.Run(Path.GetFullPath(args[1]));
        if (args.Length == 4 && args[0] == "--replay-hybrid-file-profile-prepare") return ReplayHybridFileProfile.Prepare(args[1], args[2], args[3]);
        if (args.Length == 4 && args[0] == "--replay-hybrid-file-diagnostic-prepare") return ReplayHybridFileProfile.Prepare(args[1], args[2], args[3], diagnostic: true);
        if (args.Length == 3 && args[0] == "--replay-hybrid-file-profile-run") return ReplayHybridFileProfile.Run(args[1], args[2]);
        if (args.Length == 6 && args[0] == "--replay-hybrid-product-prepare")
            return ReplayHybridPilot.Prepare(args[1], args[2], args[3], args[4], args[5]);
        if (args.Length == 6 && args[0] == "--replay-packet-product-prepare")
            return ReplayHybridPilot.Prepare(args[1], args[2], args[3], args[4], args[5], packetMux: true);
        if (args.Length == 6 && args[0] == "--replay-packet-batch-product-prepare")
            return ReplayHybridPilot.Prepare(args[1], args[2], args[3], args[4], args[5], packetMux: true, parallelIndex: true);
        if (args.Length == 6 && args[0] == "--replay-packet-cancellable-product-prepare")
            return ReplayHybridPilot.Prepare(args[1], args[2], args[3], args[4], args[5], packetMux: true, parallelIndex: true, cancellableIndex: true);
        if (args.Length == 3 && args[0] == "--replay-hybrid-product-run") return ReplayHybridPilot.Run(args[1], args[2]);
        if (args.Length == 5 && args[0] == "--openh264-pilot-prepare")
            return NativeApiPilot.PrepareOpen(args[1], args[2], args[3], args[4]);
        if (args.Length == 6 && args[0] == "--replay-slices-prepare")
            return ReplayEntropyPilot.PrepareSlices(args[1], args[2], args[3], args[4], args[5]);
        if (args.Length == 5 && args[0] == "--replay-entropy-prepare")
            return ReplayEntropyPilot.Prepare(args[1], args[2], args[3], args[4]);
        if (args.Length == 3 && args[0] == "--replay-entropy-run")
            return ReplayEntropyPilot.Run(args[1], args[2]);
        if (args.Length == 4 && args[0] == "--continuous-validation-prepare")
            return ReplayContinuousInputPilot.PrepareValidation(args[1], args[2], args[3]);
        if (args.Length == 7 && args[0] == "--continuous-input-prepare")
            return ReplayContinuousInputPilot.Prepare(args[1], args[2], args[3], args[4], args[5], args[6]);
        if (args.Any(a => a.StartsWith("--native-api-", StringComparison.OrdinalIgnoreCase)))
        {
            if (args.Length == 5 && args[0] == "--native-api-prepare") return NativeApiPilot.Prepare(args[1], args[2], args[3], args[4]);
            if (args.Length == 3 && args[0] == "--native-api-run") return NativeApiPilot.Run(args[1], args[2]);
            return 2;
        }
        if (args.Any(a => a.StartsWith("--replay-parallel-pilot", StringComparison.OrdinalIgnoreCase)))
        {
            if (args.Length == 6 && args[0] == "--replay-parallel-pilot-prepare") return ReplayParallelValidationPilot.Prepare(args[1], args[2], args[3], args[4], args[5]);
            if (args.Length == 7 && args[0] == "--replay-parallel-pilot-prepare") return ReplayParallelValidationPilot.Prepare(args[1], args[2], args[3], args[4], args[5], args[6]);
            if (args.Length == 3 && args[0] == "--replay-parallel-pilot-run") return ReplayParallelValidationPilot.Run(args[1], args[2]);
            return 2;
        }
        if (args.Any(a => a.StartsWith("--replay-latency", StringComparison.OrdinalIgnoreCase)))
            return ReplayLatencyBaseline.Dispatch(args);
        if (args.Any(a => a.StartsWith("--replay-precision", StringComparison.OrdinalIgnoreCase)))
            return args.Length == 3 && args[0] == "--replay-precision" ? ReplayPrecisionChecks.Run(Path.GetFullPath(args[1]), args[2]) : 2;
        if (args.Any(a => a.StartsWith("--replay-storage-host-resume", StringComparison.OrdinalIgnoreCase)))
            return args.Length == 2 && args[0] == "--replay-storage-host-resume"
                ? ReplayStorageHostResumeChecks.Run(Path.GetFullPath(args[1])) : 2;
        if (args.Any(a => a.StartsWith("--replay-storage-contention", StringComparison.OrdinalIgnoreCase)))
            return args.Length == 2 && args[0] == "--replay-storage-contention" ? ReplayOwnershipChecks.RunContention(Path.GetFullPath(args[1])) : 2;
        if (args.Any(a => a.StartsWith("--replay-manifest", StringComparison.OrdinalIgnoreCase)))
        {
            if (args.Length == 2 && args[0] == "--replay-manifest-stream") return ReplayManifestStreamChecks.Run(Path.GetFullPath(args[1]));
            if (args.Length == 3 && args[0] == "--replay-manifest-software") return ReplayManifestSoftwareChecks.Run(Path.GetFullPath(args[1]), args[2]);
            return 2;
        }
        if (args.Length == 1 && args[0] == "--replay-producer-wait") { Thread.Sleep(15000); return 0; }
        if (args.Length == 3 && args[0] == "--replay-producer-proof-child" && args[2] is "sealed" or "unsealed")
            return ReplayProducerRecoveryChecks.Child(Path.GetFullPath(args[1]), args[2] == "sealed");
        if (args.Any(a => a.StartsWith("--replay-producer", StringComparison.OrdinalIgnoreCase)))
            return args.Length == 2 && args[0] == "--replay-producer-recovery" ? ReplayProducerRecoveryChecks.Run(Path.GetFullPath(args[1])) : 2;
        if (args.Any(a => a.StartsWith("--replay-storage-resume-worker", StringComparison.OrdinalIgnoreCase)))
            return args.Length == 3 && args[0] == "--replay-storage-resume-worker" ? ReplayStorageResumeWorkerChecks.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2])) : 2;
        if (args.Any(a => a.StartsWith("--replay-storage-initial-worker", StringComparison.OrdinalIgnoreCase)))
            return args.Length == 3 && args[0] == "--replay-storage-initial-worker" ? ReplayStorageResumeWorkerChecks.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), initialAdmission: true) : 2;
        if (args.Any(a => a.StartsWith("--replay-storage-prelaunch", StringComparison.OrdinalIgnoreCase)))
            return args.Length == 3 && args[0] == "--replay-storage-prelaunch" ? ReplayStorageRuntimeChecks.RunPrelaunch(Path.GetFullPath(args[1]), Path.GetFullPath(args[2])) : 2;
        if (args.Any(a => a.StartsWith("--replay-retirement", StringComparison.OrdinalIgnoreCase)))
            return args.Length == 2 && args[0] == "--replay-retirement" ? ReplayRetirementChecks.Run(Path.GetFullPath(args[1])) : 2;
        if (args.Any(a => a.StartsWith("--replay-storage-runtime", StringComparison.OrdinalIgnoreCase)))
            return args.Length == 2 && args[0] == "--replay-storage-runtime" ? ReplayStorageRuntimeChecks.Run(Path.GetFullPath(args[1])) : 2;
        if (args.Any(a => a.StartsWith("--replay-storage-safety", StringComparison.OrdinalIgnoreCase)))
            return args.Length == 2 && args[0] == "--replay-storage-safety" ? ReplayStorageSafetyChecks.Run(Path.GetFullPath(args[1])) : 2;
        if (args.Any(a => a.StartsWith("--replay-volume-reservation", StringComparison.OrdinalIgnoreCase)))
        {
            if (args.Length == 2 && args[0] == "--replay-volume-reservations") return ReplayVolumeReservationChecks.Run(Path.GetFullPath(args[1]));
            return ReplayVolumeReservationChecks.Dispatch(args);
        }
        if (args.Any(a => a.StartsWith("--replay-storage-worker", StringComparison.OrdinalIgnoreCase)))
            return args.Length == 4 && args[0] == "--replay-storage-worker"
                ? ReplayStorageWorkerChecks.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), Path.GetFullPath(args[3])) : 2;
        if (args.Any(a => a.StartsWith("--replay-storage-races", StringComparison.OrdinalIgnoreCase)))
            return args.Length == 2 && args[0] == "--replay-storage-races" ? ReplayStorageRaceChecks.Run(Path.GetFullPath(args[1])) : 2;
        if (args.Any(a => a.StartsWith("--replay-calibration", StringComparison.OrdinalIgnoreCase) || a.StartsWith("--calibration-fixture", StringComparison.OrdinalIgnoreCase)))
        {
            if (args.Length == 5 && args[0] == "--replay-calibration-production-prepare") return ReplayCalibrationRun.PrepareProductionQualification(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), Path.GetFullPath(args[3]), Path.GetFullPath(args[4]));
            if (args.Length == 3 && args[0] == "--replay-calibration-output-metadata") return ReplayCalibrationRun.OutputMetadataChecks(Path.GetFullPath(args[1]), args[2]);
            if (args.Length == 3 && args[0] == "--replay-calibration-closed-validation") return ReplayCalibrationRun.ClosedValidationChecks(Path.GetFullPath(args[1]), args[2]);
            if (args.Length == 3 && args[0] == "--replay-calibration-captured-transition") return ReplayCalibrationRun.CapturedTransitionChecks(Path.GetFullPath(args[1]), args[2]);
            if (args.Length == 4 && args[0] == "--replay-calibration-issue14-prepare") return ReplayCalibrationRun.PrepareIssue14(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), Path.GetFullPath(args[3]));
            if (args.Length == 5 && args[0] == "--replay-calibration-issue14-prepare") return ReplayCalibrationRun.PrepareIssue14(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), Path.GetFullPath(args[3]), Path.GetFullPath(args[4]));
            if (args.Length == 5 && args[0] == "--replay-calibration-issue14-correction-prepare") return ReplayCalibrationRun.PrepareIssue14(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), Path.GetFullPath(args[3]), Path.GetFullPath(args[4]), corrected: true);
            if (args.Length == 5 && args[0] == "--replay-calibration-issue14-lease-prepare") return ReplayCalibrationRun.PrepareIssue14(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), Path.GetFullPath(args[3]), Path.GetFullPath(args[4]), corrected: true, leased: true);
            if (args.Length == 5 && args[0] == "--replay-calibration-issue14-readonly-prepare") return ReplayCalibrationRun.PrepareIssue14(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), Path.GetFullPath(args[3]), Path.GetFullPath(args[4]), corrected: true, leased: true, readonlyRuntime: true);
            if (args.Length == 5 && args[0] == "--replay-calibration-issue14-trace-prepare") return ReplayCalibrationRun.PrepareIssue14(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), Path.GetFullPath(args[3]), Path.GetFullPath(args[4]), corrected: true, leased: true, readonlyRuntime: true, queuedTrace: true);
            if (args.Length == 5 && args[0] == "--replay-calibration-issue14-native-continue-prepare") return ReplayCalibrationRun.PrepareNativeContinuation14(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), Path.GetFullPath(args[3]), Path.GetFullPath(args[4]));
            if (args.Length == 5 && args[0] == "--replay-calibration-issue14-rehearsal-prepare") return ReplayCalibrationRun.PrepareIssue14(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), Path.GetFullPath(args[3]), retainedDummyRoot: Path.GetFullPath(args[4]));
            if (args.Length == 2 && args[0] == "--replay-calibration-issue14-checks") return ReplayCalibrationRun.Issue14AdmissionChecks(Path.GetFullPath(args[1]));
            if (args.Length == 3 && args[0] == "--replay-calibration-calibrated-validation") return ReplayCalibrationRun.CalibratedValidationChecks(Path.GetFullPath(args[1]), args[2]);
            if (args.Length == 3 && args[0] == "--replay-calibration-issue14-rehearse") return ReplayCalibrationRun.RehearseIssue14(Path.GetFullPath(args[1]), args[2]);
            if (args.Length == 2 && args[0] == "--replay-calibration-capture-evidence-checks") return CalibrationCaptureEvidence.Checks(Path.GetFullPath(args[1]));
            if (args.Length == 2 && args[0] == "--replay-calibration-desktop-observe") return CalibrationDesktop.Observe(Path.GetFullPath(args[1]));
            if (args.Length == 2 && args[0] == "--replay-calibration-startup-checks") return CalibrationStartupChecks.Run(Path.GetFullPath(args[1]));
            if (args.Length == 2 && args[0] == "--replay-calibration-fixture-publication-checks") return CalibrationFixturePublicationChecks.Run(Path.GetFullPath(args[1]));
            if (args.Length == 3 && args[0] == "--replay-calibration-export-startup-checks") return ReplayExportStartupChecks.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
            if (args.Length == 3 && args[0] == "--replay-calibration-continue") return ReplayCalibrationRun.Continue(Path.GetFullPath(args[1]), args[2]);
            if (args.Length == 2 && args[0] == "--replay-calibration-snapshot-inventory-checks") return ReplaySnapshotInventoryChecks.Run(Path.GetFullPath(args[1]));
            if (args.Length == 2 && args[0] == "--replay-calibration-frozen-history-checks") return FrozenCalibrationHistoryChecks.Run(Path.GetFullPath(args[1]));
            if (args.Length == 2 && args[0] == "--replay-calibration-selected-history-checks") return FrozenCalibrationHistoryChecks.Selected(Path.GetFullPath(args[1]));
            if (args.Length == 1 && args[0] == "--replay-calibration-selected-history-plan") { Console.WriteLine("OFFLINE: one helper; 45s work/60s outer; 512MiB/4096handles; 8MiB fresh dummy files; 60GB free floor; selected-only native read leases/aliases/parent pins and constructor/disposal checks; no engine/media/capture/audio/UI/device."); return 0; }
            if (args.Length == 2 && args[0] == "--replay-calibration-continuation-checks") return ReplayCalibrationRun.ContinuationChecks(Path.GetFullPath(args[1]));
            if (args.Length == 3 && args[0] == "--replay-calibration-export-slow-checks") return ReplayExportStartupChecks.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), slowObservation: true);
            if (args.Length == 3 && args[0] == "--replay-calibration-export-snapshot-checks") return ReplayExportSnapshotChecks.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
            if (args.Length == 3 && args[0] == "--replay-calibration-export-stale-checks") return ReplayExportStartupChecks.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), stale: true);
            if (args.Length == 3 && args[0] == "--replay-calibration-engine-lease-checks") return ReplayExecutableLeaseChecks.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
            if (args.Length == 5 && args[0] == "--replay-calibration-export-context-checks") return ReplayExportSnapshotChecks.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), Path.GetFullPath(args[3]), args[4]);
            if (args.Length == 3 && args[0] == "--replay-calibration-startup") return ReplayCalibrationStartup.Run(Path.GetFullPath(args[1]), args[2]);
            if (args.Length == 4 && args[0] == "--replay-calibration-startup-sampler") return ReplayCalibrationTimingChecks.ProspectiveSampler(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), args[3], startup: true);
            if (args.Length == 2 && args[0] == "--replay-calibration-publication-checks") return ReplaySamplerPublicationChecks.Run(Path.GetFullPath(args[1]));
            if (args.Length == 4 && args[0] == "--replay-calibration-audit-receipt") return ReplayCalibrationCatalogChecks.AuditHistoricalReceipt(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), args[3]);
            if (args.Length == 2 && args[0] == "--replay-calibration-foreground-checks") return CalibrationForeground.Checks(Path.GetFullPath(args[1]));
            if (args.Length == 2 && args[0] == "--replay-calibration-inventory-checks") return ReplayInventoryReaderChecks.Run(Path.GetFullPath(args[1]));
            if (args.Length == 2 && args[0] == "--replay-calibration-close-dummy-checks") return ReplayCalibrationDummyClosure.Checks(Path.GetFullPath(args[1]));
            if (args.Length == 3 && args[0] == "--replay-calibration-close-dummy-evidence") return ReplayCalibrationDummyClosure.Run(Path.GetFullPath(args[1]), args[2]);
            if (args.Length == 5 && args[0] == "--replay-calibration-write-diagnostic") return ReplayCalibrationWriteDiagnostic.Run(Path.GetFullPath(args[1]), args[2], Path.GetFullPath(args[3]), double.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture));
            if (args.Length == 2 && args[0] == "--replay-calibration-rehearsal-checks") return ReplayCalibrationRehearsal.Checks(Path.GetFullPath(args[1]));
            if (args.Length == 3 && args[0] == "--replay-calibration-rehearsal") return ReplayCalibrationRehearsal.Run(Path.GetFullPath(args[1]), args[2]);
            if (args.Length == 5 && args[0] == "--replay-calibration-pause-check") return ReplayCalibrationTimingChecks.PauseRun(Path.GetFullPath(args[1]), args.Skip(2).Select(Path.GetFullPath).ToArray());
            if (args.Length == 2 && args[0] == "--replay-calibration-archive-checks") return ReplayCalibrationArchiveChecks.Run(Path.GetFullPath(args[1]));
            if (args.Length == 2 && args[0] == "--replay-calibration-timing-checks") return ReplayCalibrationTimingChecks.Run(Path.GetFullPath(args[1]));
            if (args.Length == 4 && args[0] == "--replay-calibration-prospective-sampler") return ReplayCalibrationTimingChecks.ProspectiveSampler(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), args[3]);
            if (args.Length == 4 && args[0] == "--replay-calibration-sampler-check") return ReplayCalibrationTimingChecks.SamplerRun(Path.GetFullPath(args[1]), args.Skip(2).Select(Path.GetFullPath).ToArray());
            if (args.Length == 2 && args[0] == "--replay-calibration-phase-checks") return ReplayCalibrationPhaseChecks.Run(Path.GetFullPath(args[1]));
            if (args.Length == 2 && args[0] == "--replay-calibration-catalog-checks") return ReplayCalibrationCatalogChecks.Run(Path.GetFullPath(args[1]));
            if (args.Length == 2 && args[0] == "--replay-calibration-model-checks") return ReplayCalibrationModelChecks.Run(Path.GetFullPath(args[1]));
            if (args.Length == 3 && args[0] == "--replay-calibration-prepare") return ReplayCalibrationRun.Prepare(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
            if (args.Length == 3 && args[0] == "--replay-calibration-controls") return ReplayCalibrationRun.Controls(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
            if (args.Length == 3 && args[0] == "--replay-calibration-run") return ReplayCalibrationRun.Run(Path.GetFullPath(args[1]), args[2]);
            if (args.Length == 3 && args[0] == "--replay-calibration-inspect-existing") return ReplayCalibrationRun.InspectExisting(Path.GetFullPath(args[1]), args[2]);
            if (TryCalibrationFixture(args, out string fixtureRoot, out string fixtureLabel, out bool manualFixture))
                return CalibrationPattern.Run(fixtureRoot, fixtureLabel, manualFixture);
            return 2;
        }
        if (args.Any(value => value.StartsWith("--replay-output-validation", StringComparison.OrdinalIgnoreCase)))
            return args.Length == 2 && args[0] == "--replay-output-validation"
                ? ReplayOutputValidationChecks.Run(Path.GetFullPath(args[1])) : 2;
        if (args.Any(value => value.StartsWith("--pilot-stage", StringComparison.OrdinalIgnoreCase))) return PilotStageRunner.Dispatch(args);
        if (args.Any(value => value.StartsWith("--pilot-storage", StringComparison.OrdinalIgnoreCase))) return PilotStorageObservation.Dispatch(args);
        if (args.Length == 2 && args[0] == "--replay-ownership") return ReplayOwnershipChecks.Run(Path.GetFullPath(args[1]));
        if (args.Any(a => a.StartsWith("--replay-export-cadence", StringComparison.OrdinalIgnoreCase)))
            return args.Length == 2 && args[0] == "--replay-export-cadence" ? ReplayOwnershipChecks.RunExportCadence(Path.GetFullPath(args[1])) : 2;
        if (args.Length == 2 && args[0] == "--capture-status") return CaptureStatusChecks.Run(Path.GetFullPath(args[1]));
        if (args.Length == 2 && args[0] == "--profile-migration") return ProfileWorkerChecks.Run(Path.GetFullPath(args[1]));
        if (args.Length == 5 && args[0] == "--profile-correction-real" && args[4] == "--budget-apply-only") return ProfileCorrectionChecks.RunBudget(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), Path.GetFullPath(args[3]));
        if (args.Length == 4 && args[0] == "--profile-correction-real") return ProfileCorrectionChecks.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), Path.GetFullPath(args[3]));
        if (args.Length == 2 && args[0] == "--profile-status-transport") return ProfileStatusTransportChecks.Run(Path.GetFullPath(args[1]));
        if (args.Length >= 2 && args[0] == "--apply-acknowledgements") return ApplyAcknowledgementChecks.Run(Path.GetFullPath(args[1]), args.Contains("--sensitivity-control"));
        if (args.Length >= 2 && args[0] == "--engine-capabilities") return EngineCapabilityChecks.Run(Path.GetFullPath(args[1]), args.Contains("--sensitivity-control"));
        int engineRootIndex = Array.IndexOf(args, "--engine-root");
        if (engineRootIndex >= 0) MediaDependency.RootDirectory = Path.GetFullPath(args[engineRootIndex + 1]);
        int ownershipRootIndex = Array.IndexOf(args, "--ownership-root");
        if (ownershipRootIndex >= 0) ProfileOwnership.RootDirectory = Path.GetFullPath(args[ownershipRootIndex + 1]);
        if (args[0] == "--profile-migration-worker") return RecorderWorker.Run(Path.GetFullPath(args[1]), false, setStartup: _ => throw new Exception("Migration must not write startup."));
        if (args[0] == "--profile-correction-worker") return RecorderWorker.Run(Path.GetFullPath(args[1]), true,
            setStartup: enabled =>
            {
                string profile = Path.GetFullPath(args[1]);
                File.AppendAllLines(Path.Combine(profile, "fixture-startup-intent.txt"), new[] { enabled ? "true" : "false" });
                if (File.Exists(Path.Combine(profile, "fixture-reject-startup.txt"))) throw new IOException("Controlled fixture startup failure after profile commit.");
            });
        if (args[0] == "--engine-trust-worker")
        {
            string profile = Path.GetFullPath(args[1]), package = Path.GetFullPath(args[2]);
            return RecorderWorker.Run(profile, true, synthetic: true,
                setStartup: _ => File.WriteAllText(Path.Combine(profile, "unexpected-startup-write.txt"), "startup writer called"),
                repairEngine: () => MediaDependency.Install(profile, true, (_, destination) =>
                {
                    File.AppendAllText(Path.Combine(profile, "repair-acquisitions.txt"), "explicit repair\n");
                    if (File.Exists(Path.Combine(profile, "reject-repair.txt"))) File.WriteAllText(destination, "Rejected package fixture");
                    else File.Copy(package, destination);
                }));
        }
        if (args[0] == "--dependency-marker") { File.WriteAllText(args[1], "Unapproved engine executed"); return 0; }
        if (args[0] == "--dependency-test")
        {
            int packageIndex = Array.IndexOf(args, "--package");
            return DependencyVerification.Run(Path.GetFullPath(args[1]), args.Contains("--download"), packageIndex >= 0 ? Path.GetFullPath(args[packageIndex + 1]) : null);
        }
        if (args[0] == "--security-checks") return SecurityChecks.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
        if (args[0] == "--settings-validation") return SettingsValidationChecks.Run(Path.GetFullPath(args[1]));
        if (args[0] == "--screenshot-binding") return ScreenshotBindingChecks.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
        if (args[0] == "--screenshot-binding-fixture") { ScreenshotBindingFixture.Run(args[1]); return 0; }
        if (args[0] == "--security-live-audio") return SecurityChecks.LiveAudio(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
        if (args[0] == "--security-account-server") return CrossAccountChecks.Server(Path.GetFullPath(args[1]));
        if (args[0] == "--security-account-client") return CrossAccountChecks.Client(Path.GetFullPath(args[1]));
        if (args[0] == "--security-child") return SecurityChecks.Child(args);
        if (args[0] == "--window-fixture") { WindowCaptureChecks.Fixture(args[1]); return 0; }
        if (args[0] == "--window-checks") return WindowCaptureChecks.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
        if (args[0] == "--headless-status") return HeadlessStatusChecks.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
        if (args[0] == "--analyze-saved")
        {
            string folder = Path.GetFullPath(args[1]);
            using var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "verification.json")));
            var root = saved.RootElement;
            int rate = root.GetProperty("fixture").GetProperty("fps").GetInt32();
            var analyses = root.GetProperty("soakClips").EnumerateArray().Select(c => c.GetString()).Select(clip => new
            {
                clip, analysis = FramePacingChecks.Analyze(folder, clip, root.GetProperty("mediaEngine").GetString(), rate, true)
            }).ToArray();
            File.WriteAllText(Path.Combine(folder, "pacing-reanalysis.json"), JsonSerializer.Serialize(analyses, WorkerProtocol.Json));
            return analyses.All(a => JsonSerializer.SerializeToElement(a.analysis).GetProperty("acceptance").GetString() == "passed") ? 0 : 1;
        }
        if (args[0] == "--pacing-fixture") { PacingFixture.Run(args[1], int.Parse(args[2]), args[3]); return 0; }
        if (args[0] == "--pacing") return FramePacingChecks.Run(args[1], int.Parse(args[2]), int.Parse(args[3]), args[4], args[5] == "on", args.Length > 6 ? args[6] : "baseline", args.Length > 7 ? int.Parse(args[7]) : 0, args.Length > 8 ? int.Parse(args[8]) : int.Parse(args[3]), args.Length > 9 ? args[9] : "h264_nvenc");
        if (args[0] == "--fixture") { DesktopFixture.Run(args[1]); return 0; }
        if (args[0] == "--worker")
        {
            string fault = Path.Combine(args[1], "controlled-health-failure.txt");
            RecorderRuntime.CaptureHealthProbe = () =>
            {
                if (!File.Exists(fault)) return null;
                string error = File.ReadAllText(fault); File.Delete(fault); return error;
            };
            return RecorderWorker.Run(args[1], false, args[2], true, _ => { }, (_, folder) => ScreenshotCapture.CaptureSource("testsrc2=size=640x360:rate=1", folder, false));
        }
        if (args[0].StartsWith("--", StringComparison.Ordinal)) return 2;
        data = Path.GetFullPath(args[0]); desktop = Path.GetFullPath(args[1]);
        live = args.Contains("--live");
        gpuFixture = args.Contains("--gpu-fixture");
        int fpsIndex = Array.IndexOf(args, "--fps"); if (fpsIndex >= 0) liveFps = int.Parse(args[fpsIndex + 1]);
        Require(!gpuFixture || live, "GPU fixture requires live desktop capture.");
        startupBefore = ReadStartup(); report["startupEnabledBefore"] = startupBefore.Value != null;
        Directory.CreateDirectory(data); RecorderHost.Initialize(data);
        var settings = new Settings { ReplayMinutes = 1, FramesPerSecond = 30, BitrateMbps = 5, OutputDirectory = Path.Combine(data, "clips"), ScreenshotDirectory = Path.Combine(data, "screenshots"), SaveKey = 121, SaveModifiers = 7, OverlayKey = 122, OverlayModifiers = 7, ScreenshotKey = 123, ScreenshotModifiers = 7 };
        if (args.Contains("--alternate-shortcuts")) { settings.SaveKey = 117; settings.OverlayKey = 118; settings.ScreenshotKey = 119; }
        if (live) { settings.FramesPerSecond = liveFps; settings.BitrateMbps = 20; }
        settings.Save(Path.Combine(data, "settings.json"));
        report["mediaEngine"] = MediaDependency.ResolveExecutable();
        report["captureSource"] = live ? "Primary monitor native resolution, " + liveFps + "fps/20Mbps H264 NVENC; controlled " + (gpuFixture ? "DXGI flip-model GPU source" : "GDI animated borderless window") + " and real WASAPI default output (silence permitted); one-minute replay buffer" : "Synthetic 640x360/30 testsrc2 and 440Hz/48kHz tone; no desktop recording";
        try
        {
            Check(int.Parse(args[2])).GetAwaiter().GetResult();
            if (gpuFixture)
            {
                var analyses = new List<object>();
                foreach (string clip in (string[])report["soakClips"])
                {
                    object analysis = FramePacingChecks.Analyze(data, clip, (string)report["mediaEngine"], liveFps, true);
                    analyses.Add(new { clip, analysis }); report["pacingReports"] = analyses;
                    var measured = JsonSerializer.SerializeToElement(analysis);
                    Require(measured.GetProperty("acceptance").GetString() == "passed", "Soak clip failed pacing acceptance: " + clip + " " + measured);
                }
            }
            report["result"] = "passed";
        }
        catch (Exception ex) { report["result"] = "failed"; report["error"] = ex.ToString(); }
        File.WriteAllText(Path.Combine(data, "verification.json"), JsonSerializer.Serialize(report, WorkerProtocol.Json));
        return (string)report["result"] == "passed" ? 0 : 1;
    }
    static Process Launch(string executable, params string[] args)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in args) info.ArgumentList.Add(arg);
        return Process.Start(info) ?? throw new IOException("Test process did not start.");
    }
    static Task<WorkerReply> Call(string command = "status", bool value = false, Settings settings = null, long after = -1) => WorkerProtocol.CallAsync(data, new WorkerRequest { Command = command, Value = command == "apply" ? startupBefore.Value != null : value, Settings = settings, AfterEvent = after });
    static StartupState ReadStartup()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupKey);
        object value = key?.GetValue("F10Recorder", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return new StartupState(value, value == null ? RegistryValueKind.String : key.GetValueKind("F10Recorder"));
    }
    static bool SameStartup(StartupState a, StartupState b) => a.Kind == b.Kind && JsonSerializer.Serialize(a.Value) == JsonSerializer.Serialize(b.Value);
    static void PreserveStartup()
    {
        bool changed = !SameStartup(startupBefore, ReadStartup());
        if (changed)
        {
            using var key = Registry.CurrentUser.CreateSubKey(StartupKey);
            if (startupBefore.Value == null) key.DeleteValue("F10Recorder", false);
            else key.SetValue("F10Recorder", startupBefore.Value, startupBefore.Kind);
        }
        Require(SameStartup(startupBefore, ReadStartup()), "The original sign-in startup entry was not preserved.");
        report["startupEntryPreservedWithoutChange"] = !changed;
        report["startupEntryRestored"] = changed;
    }
    static void Require(bool condition, string error) { if (!condition) throw new Exception(error); }
    static async Task<WorkerStatus> Until(Func<WorkerStatus, bool> predicate, string error, int seconds = 30)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            try
            {
                var reply = await Call(); Require(reply.Error == null, reply.Error);
                if (predicate(reply.Status)) return reply.Status;
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
            await Task.Delay(100);
        }
        throw new TimeoutException(error);
    }
    static async Task UntilUi(Process ui, int workerPid)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < 20)
        {
            if (ui.HasExited) throw new Exception("Production UI exited unexpectedly.");
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(data, "status.json")));
                if (json.RootElement.GetProperty("WorkerProcessId").GetInt32() == workerPid && json.RootElement.GetProperty("UiProcessId").GetInt32() == ui.Id) return;
            }
            catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException) { }
            await Task.Delay(100);
        }
        throw new TimeoutException("Production UI did not attach to the worker.");
    }
    static async Task<WorkerEvent> Save(bool expected, bool hotkeyDispatch = false, Func<WorkerStatus, Task> afterAccepted = null)
    {
        long sequence = (await Call()).Status.Sequence;
        if (hotkeyDispatch)
            Require(Native.PostMessage(Native.FindWindow(null, WorkerIdentity.ControlTitle(data)), 0x0312, new IntPtr(1), IntPtr.Zero), "Headless worker hotkey dispatch failed.");
        else
        {
            var begin = await Call("save"); Require(begin.Error == null, begin.Error);
            if (afterAccepted != null) await afterAccepted(begin.Status);
        }
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < 20)
        {
            var response = await Call(after: sequence);
            var completed = response.Status.Events.FirstOrDefault(e => e.Kind == "saved");
            if (completed != null)
            {
                Require(completed.Success == expected, "Unexpected replay save result: " + completed.Message);
                saves.Add(new { success = completed.Success, encoder_save_ms = response.Status.Metrics.LastSaveMilliseconds, command_to_completion_ms = clock.Elapsed.TotalMilliseconds, bytes = response.Status.Metrics.LastSaveBytes });
                if (expected) { Require(File.Exists(completed.Message), "Successful replay file is missing."); clips.Add(completed.Message); }
                return completed;
            }
            await Task.Delay(100);
        }
        throw new TimeoutException("Replay completion event did not arrive.");
    }
    static Settings Copy(Settings config) => JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(config, WorkerProtocol.Json), WorkerProtocol.Json);
    static async Task Check(int soakSeconds)
    {
        Process worker = null, ui = null, fixture = null;
        try
        {
            await CheckProfileOwnership();
            if (live)
            {
                Require(new DriveInfo(Path.GetPathRoot(data)).AvailableFreeSpace > 8L * 1024 * 1024 * 1024, "Live acceptance requires at least 8GiB of free space.");
                fixture = gpuFixture ? Launch(Environment.ProcessPath, "--pacing-fixture", data, liveFps.ToString(), "dxgi") : Launch(Environment.ProcessPath, "--fixture", data);
                string readyFile = Path.Combine(data, gpuFixture ? "pacing-ready.json" : "fixture-ready.json");
                var start = Stopwatch.StartNew();
                while (!File.Exists(readyFile) && !fixture.HasExited && start.Elapsed.TotalSeconds < 10) await Task.Delay(100);
                Require(!fixture.HasExited && File.Exists(readyFile), "Controlled desktop fixture did not become ready.");
                report["fixture"] = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(readyFile));
                worker = Launch(desktop, "--recorder-worker", "--data-dir", data);
            }
            else worker = Launch(Environment.ProcessPath, "--worker", data, desktop);
            var initial = await Until(s => s.Ready && s.Metrics.CaptureProgressSeconds >= 5, "Worker did not begin buffering.");
            Require(initial.WorkerProcessId == worker.Id, "Unexpected worker ownership.");
            Require(initial.SaveRegistered && initial.OverlayRegistered && initial.ScreenshotRegistered, "Worker did not own all shortcuts.");
            ui = Launch(desktop, "--data-dir", data, "--settings"); await UntilUi(ui, worker.Id);
            int capture = initial.Metrics.CaptureProcessId;
            double progress = (await Call()).Status.Metrics.CaptureProgressSeconds;
            ui.Kill(); await ui.WaitForExitAsync(); ui.Dispose(); ui = null;
            await Task.Delay(2500);
            var survived = (await Call()).Status;
            Require(survived.Ready && survived.Metrics.CaptureProcessId == capture && survived.Metrics.CaptureProgressSeconds > progress, "Capture changed during UI-crash check: " + survived.State);
            await Save(true, true); report["uiCrashAndHeadlessSave"] = true;
            ui = Launch(desktop, "--data-dir", data); await UntilUi(ui, worker.Id);
            Require((await Call()).Status.Metrics.CaptureProcessId == capture, "UI reattachment rebuilt the buffer."); report["uiReattachmentPreservesBuffer"] = true;
            await Save(true, afterAccepted: async status =>
            {
                Require(status.Saving, "Save finished before the in-flight lifecycle check.");
                Require((await Call("quit")).Error != null, "Quit interrupted an in-flight save.");
                Require((await Call("apply", settings: Copy(status.Config))).Error != null, "Settings replacement interrupted an in-flight save.");
                Require((await Call()).Status.Saving, "Save finished before the UI crash could be injected.");
                ui.Kill(); await ui.WaitForExitAsync(); ui.Dispose(); ui = null;
            });
            report["uiCrashDuringSaveAndBusyCommandRejection"] = true;
            ui = Launch(desktop, "--data-dir", data); await UntilUi(ui, worker.Id);
            using (var command = Launch(desktop, "--data-dir", data, "--overlay"))
                await command.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(500);
            Require((await Call("screenshot")).Error == null, "Remote screenshot command failed.");
            var screenshot = await Until(s => s.LastScreenshot != null && !s.ScreenshotInProgress, "Cross-process screenshot preparation did not complete.");
            Require(File.Exists(screenshot.LastScreenshot), "Controlled screenshot is missing.");
            using (var uiStatus = JsonDocument.Parse(File.ReadAllText(Path.Combine(data, "status.json"))))
                Require(!uiStatus.RootElement.GetProperty("OverlayVisible").GetBoolean(), "Screenshot preparation left the UI overlay visible.");
            report["remoteScreenshotPreparation"] = screenshot.LastScreenshot;

            var requests = await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => Call()));
            Require(requests.All(r => r.Error == null && r.Status.Ready), "Concurrent status readers affected capture.");
            report["concurrentStatusReaders"] = requests.Length;

            using (var pipe = new NamedPipeClientStream(".", WorkerIdentity.PipeName(data), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
            {
                await pipe.ConnectAsync(2000);
                byte[] oversized = BitConverter.GetBytes(WorkerProtocol.MaxBytes + 1); await pipe.WriteAsync(oversized);
                using var limit = new CancellationTokenSource(3000); byte[] one = new byte[1];
                Require(await pipe.ReadAsync(one, limit.Token) == 0, "Oversized request was not rejected.");
            }
            var wrongVersion = await WorkerProtocol.CallAsync(data, new WorkerRequest { Version = 1 });
            Require(wrongVersion.Error != null && (await Call()).Status.Ready, "Bad protocol request affected capture."); report["boundedProtocolRejection"] = true;

            var original = Copy((await Call()).Status.Config);
            var invalid = Copy(original); invalid.VideoCodec = "invalid";
            Require((await Call("apply", settings: invalid)).Error != null && (await Call()).Status.Config.VideoCodec == original.VideoCodec, "Invalid settings changed live capture.");
            Require(Settings.Load(Path.Combine(data, "settings.json")).VideoCodec == original.VideoCodec, "Invalid settings changed persisted capture."); report["invalidApplyRollback"] = true;

            int priorCapture = (await Call()).Status.Metrics.CaptureProcessId;
            var restarting = await Call("restart");
            Require(restarting.Error == null && !restarting.Status.Ready, "Restart continued to advertise capture readiness.");
            await Until(s => s.Ready && s.Metrics.CaptureProcessId != 0 && s.Metrics.CaptureProcessId != priorCapture, "Explicit restart did not rebuild capture.");
            report["restartInvalidatesReadiness"] = true;

            var blocked = Copy(original); blocked.OutputDirectory = Path.Combine(data, "blocked-output"); File.WriteAllText(blocked.OutputDirectory, "Controlled non-directory output target");
            Require((await Call("apply", settings: blocked)).Error == null, "Controlled output settings did not apply.");
            await Until(s => s.Ready && s.Metrics.CaptureProgressSeconds > 4, "Capture did not recover after settings change.");
            await Save(false); Require((await Call()).Status.Ready, "Output failure stopped buffering.");
            Require((await Call("apply", settings: original)).Error == null, "Output settings did not restore.");
            await Until(s => s.Ready && s.Metrics.CaptureProgressSeconds > 4, "Restored capture did not become ready."); await Save(true);
            report["unwritableOutputAndRecovery"] = "Non-directory destination failure; actual volume exhaustion not simulated";

            var before = (await Call()).Status;
            using (var encoder = Process.GetProcessById(before.Metrics.CaptureProcessId))
            {
                Require(Path.GetFullPath(encoder.MainModule.FileName).Equals(Path.GetFullPath((string)report["mediaEngine"]), StringComparison.OrdinalIgnoreCase), "Unexpected encoder process identity.");
                encoder.Kill();
            }
            await Until(s => s.Ready && s.Metrics.CaptureProcessId != 0 && s.Metrics.CaptureProcessId != before.Metrics.CaptureProcessId, "Encoder crash did not recover."); report["encoderCrashRecovery"] = true;
            before = (await Call()).Status;
            Native.PostMessage(Native.FindWindow(null, WorkerIdentity.ControlTitle(data)), 0x007E, IntPtr.Zero, IntPtr.Zero);
            await Until(s => s.Ready && s.Metrics.CaptureProcessId != 0 && s.Metrics.CaptureProcessId != before.Metrics.CaptureProcessId, "Display-change message did not recover capture."); report["displayChangeMessageRecovery"] = true;
            before = (await Call()).Status;
            if (!live)
            {
                File.WriteAllText(Path.Combine(data, "controlled-health-failure.txt"), "Controlled audio endpoint change at capture-health boundary.");
                await Until(s => s.Ready && s.Metrics.CaptureProcessId != 0 && s.Metrics.CaptureProcessId != before.Metrics.CaptureProcessId && s.Metrics.Recoveries > before.Metrics.Recoveries, "Controlled audio-health failure did not recover capture.");
                report["audioHealthRecovery"] = "Injected health-boundary failure; physical audio endpoint was unchanged";
            }
            else
            {
                // Production UI must restart the real worker without any test-only launcher.
                using var oldEncoder = Process.GetProcessById(before.Metrics.CaptureProcessId);
                Require(oldEncoder.Handle != IntPtr.Zero, "Could not retain the owned encoder's exit handle.");
                worker.Kill(); await worker.WaitForExitAsync(); worker.Dispose(); worker = null;
                var recovered = await Until(s => s.Ready && s.Instance != before.Instance, "UI did not restart the crashed worker.", 45);
                worker = Process.GetProcessById(recovered.WorkerProcessId); await UntilUi(ui, worker.Id);
                await oldEncoder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Require(recovered.SaveRegistered && recovered.OverlayRegistered && recovered.ScreenshotRegistered, "Restarted worker did not reacquire shortcuts.");
                await Until(s => s.Ready && s.Metrics.CaptureProgressSeconds > 4, "Restarted worker did not refill its buffer."); await Save(true);
                report["workerCrashAutomaticRestartAndEncoderCleanup"] = new { oldWorker = before.WorkerProcessId, newWorker = recovered.WorkerProcessId, oldEncoder = oldEncoder.Id, bufferRebuilt = true };
                foreach (int message in new[] { 0x02B1, 0x0218 })
                {
                    before = (await Call()).Status;
                    Require(Native.PostMessage(Native.FindWindow(null, WorkerIdentity.ControlTitle(data)), (uint)message, new IntPtr(message == 0x02B1 ? 8 : 18), IntPtr.Zero), "Environment message dispatch failed.");
                    await Until(s => s.Ready && s.Metrics.CaptureProcessId != 0 && s.Metrics.CaptureProcessId != before.Metrics.CaptureProcessId, "Unlock/resume message did not rebuild capture.");
                }
                report["unlockResumeMessageRouting"] = "Injected unlock/resume messages; no real lock or suspend";
            }

            int soakClipStart = clips.Count;
            var soak = Stopwatch.StartNew(); int nextSave = 15;
            double previousProgress = -1;
            while (soak.Elapsed.TotalSeconds < soakSeconds)
            {
                if (live) Require(!fixture.HasExited, "Controlled fixture closed during desktop capture.");
                var snapshot = (await Call()).Status;
                Require(snapshot.Ready && snapshot.Metrics.CaptureProcessId != 0, "Soak capture stopped advancing.");
                Require(snapshot.Metrics.CaptureProgressSeconds > previousProgress, "Soak encoder progress stalled.");
                previousProgress = snapshot.Metrics.CaptureProgressSeconds;
                samples.Add(snapshot.Metrics);
                if (soak.Elapsed.TotalSeconds >= nextSave)
                {
                    await Save(true); nextSave += live ? 120 : 30;
                    File.WriteAllText(Path.Combine(data, "acceptance-progress.json"), JsonSerializer.Serialize(new { elapsedSeconds = soak.Elapsed.TotalSeconds, samples = samples.Count, clips = clips.Count, latest = snapshot.Metrics }, WorkerProtocol.Json));
                }
                await Task.Delay(1000);
            }
            await Save(true);
            report["soakClips"] = clips.Skip(soakClipStart).ToArray();
            Require(samples.Any(s => s.EncodedFrames > 0 && s.CacheBytes > 0 && s.CaptureWorkingSetBytes > 0), "Capture measurements were not populated.");
            double bound = original.BitrateMbps * 1000000d / 8 * (original.ReplayMinutes * 60 + 15) * 1.5;
            Require(samples.Max(s => s.CacheBytes) < bound, "Rolling cache exceeded its measured bitrate allowance.");
            report["soakSeconds"] = soak.Elapsed.TotalSeconds; report["cacheBoundBytes"] = bound;
            report["samples"] = samples; report["clips"] = clips; report["saves"] = saves;
            report["metricsMeaning"] = "CPU normalized across logical processors; drops/duplicates are FFmpeg progress counters; optional wrapper GPU counters cover the whole device; gaming FPS and perceptual sync are not measured";
            var hashes = clips.ToDictionary(p => p, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));

            await Call("pause", true); await Until(s => s.Paused && s.Metrics.CaptureProcessId == 0, "Pause did not stop encoder.");
            Require((await Call("quit")).Error == null, "Worker did not acknowledge explicit Quit.");
            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await ui.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); ui.Dispose(); ui = null;
            report["explicitQuitStopsWorkerAndUi"] = true; worker.Dispose();
            worker = live ? Launch(desktop, "--recorder-worker", "--data-dir", data) : Launch(Environment.ProcessPath, "--worker", data, desktop);
            var restored = await Until(s => s.Paused, "Worker restart lost persisted pause.");
            Require(restored.Metrics.CaptureProcessId == 0, "Paused worker restart launched an encoder.");
            Require(hashes.All(h => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(h.Key))) == h.Value), "Restart changed a saved clip.");
            report["pausePersistenceAndSavedClipPreservation"] = true;
            await Call("quit"); await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            try
            {
                if (ui != null) { if (!ui.HasExited) ui.Kill(); ui.Dispose(); }
                if (worker != null)
                {
                    if (!worker.HasExited) { try { await Call("quit"); await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch { worker.Kill(); await worker.WaitForExitAsync(); } }
                    worker.Dispose();
                }
                if (fixture != null) { if (!fixture.HasExited) { fixture.CloseMainWindow(); if (!fixture.WaitForExit(2000)) fixture.Kill(); } fixture.Dispose(); }
            }
            finally { PreserveStartup(); }
        }
    }

    static async Task CheckProfileOwnership()
    {
        string otherProfile = Path.Combine(data, "ownership-check");
        string sentinel = Path.Combine(otherProfile, "buffer", "session-sentinel", "chunk-sentinel.ts");
        Directory.CreateDirectory(Path.GetDirectoryName(sentinel)); File.WriteAllText(sentinel, "Preserve another session's active buffer");
        // Hold the session-independent profile lock without the session worker lock.
        // This exercises the same rejection path as a worker in another session.
        using (var owner = ProfileOwnership.Acquire(otherProfile))
        {
            using var blocked = Launch(desktop, "--recorder-worker", "--data-dir", otherProfile, "--paused");
            // Keep mutex ownership and disposal on this STA thread; do not await here.
            Require(blocked.WaitForExit(5000) && blocked.ExitCode == 1, "A second session was not refused before opening the profile.");
            Require(File.ReadAllText(sentinel) == "Preserve another session's active buffer", "Blocked worker changed the active session's buffer.");
        }
        using var replacement = Launch(desktop, "--recorder-worker", "--data-dir", otherProfile, "--paused");
        try
        {
            var ready = await WorkerProtocol.CallAsync(otherProfile, new WorkerRequest());
            Require(ready.Error == null && ready.Status.Paused, "Profile could not reopen after ownership was released.");
            Require((await WorkerProtocol.CallAsync(otherProfile, new WorkerRequest { Command = "quit" })).Error == null, "Ownership-check worker did not stop.");
            await replacement.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { if (!replacement.HasExited) { replacement.Kill(); await replacement.WaitForExitAsync(); } }
        report["profileOwnershipAndBufferPreservation"] = "Session-independent profile lock refused another owner before cache cleanup; profile reopened after release. Actual parallel Windows logins were not created.";
    }
}
