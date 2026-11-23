using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace F10Recorder;

internal static partial class ReplayCalibrationRun
{
    internal const string RetainedCoverageAdmission = "issue83-retained-H03-final-minute-pair-v9";
    internal const string SameCaptureRetainedAdmission = "issue83-retained-same-C00-final-minute-v10";
    internal sealed record SameCaptureDependencies(string ConfirmationPath, string ManifestPath, string AcceptancePath,
        string ControllerPath, string C00PlanPath);
    internal sealed record RetainedInputMapping(string OriginalPath, string CurrentPath, string ContentSha256);
    internal sealed record RetainedCoveragePlan(CalibrationPlan Resources, string FreezePath, string FreezeSha256,
        string OriginalSelectionPath, string OriginalSelectionSha256, RetainedInputMapping[] Inputs,
        string MappingReviewHash, string MappingReviewer, DateTime ExpiresUtc, SameCaptureDependencies SameCapture = null);

    // v9 keeps its original strict payload reader. v10 has a separately
    // reviewed controller envelope; no unknown outer field enters the payload.
    internal static RetainedCoveragePlan ReadRetainedFinal(byte[] raw,string path)
    {
        using var document=JsonDocument.Parse(raw);var root=document.RootElement;
        if(!root.TryGetProperty("Resources",out var resources)||!resources.TryGetProperty("FreshCandidateAdmission",out var kind)
            ||kind.GetString()!=SameCaptureRetainedAdmission)
            return ReplayCalibrationCatalog.ReadEvidence<RetainedCoveragePlan>(raw);
        Require(raw.Length<=131072,"v10 retained final envelope128KiB bound.");RequireUniqueJsonMembers(root);
        string[] names=["operation","controllerKind","prospectivePlanPath","Resources","createdUtc","expiresUtc","parentHandleBudget",
            "maximumSupportChildren","maximumManagedRoles","maximumOuterRoles","creatorProofPolicy","creatorProofMilliseconds","controlCreatorEvidence",
            "entryPath","stopFile","watchStatusFile","runtimeManifest","controllerRuntimeManifest","controllerRuntime","rawChildByteLimit","prospectiveSameCaptureRules",
            "FreezePath","FreezeSha256","OriginalSelectionPath","OriginalSelectionSha256","Inputs","MappingReviewHash","MappingReviewer","SameCapture"];
        Require(root.EnumerateObject().Select(p=>p.Name).ToHashSet(StringComparer.Ordinal).SetEquals(names)
            &&root.GetProperty("operation").GetString()=="retained-H03-1m"
            &&root.GetProperty("controllerKind").GetString()=="issue83-two-confirmation-owner-v2"
            &&root.GetProperty("prospectivePlanPath").GetString()==path,"Exact v10 retained envelope/owning path required.");
        DateTime created=root.GetProperty("createdUtc").GetDateTime(),expires=root.GetProperty("expiresUtc").GetDateTime();
        Require(created.Kind==DateTimeKind.Utc&&expires.Kind==DateTimeKind.Utc&&created<=DateTime.UtcNow&&expires>created,
            "Actual v10 retained UTC release fields required.");
        T Read<T>(string name)=>ReplayCalibrationCatalog.ReadEvidence<T>(JsonSerializer.SerializeToUtf8Bytes(root.GetProperty(name)));
        return new(Read<CalibrationPlan>("Resources"),root.GetProperty("FreezePath").GetString(),root.GetProperty("FreezeSha256").GetString(),
            root.GetProperty("OriginalSelectionPath").GetString(),root.GetProperty("OriginalSelectionSha256").GetString(),
            root.GetProperty("Inputs").Deserialize<RetainedInputMapping[]>(new JsonSerializerOptions {
                UnmappedMemberHandling=System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,RespectRequiredConstructorParameters=true }),root.GetProperty("MappingReviewHash").GetString(),
            root.GetProperty("MappingReviewer").GetString(),expires,Read<SameCaptureDependencies>("SameCapture"));
    }

    // One fixed final minute of the successful parent H03, never a 30m reexport.
    // This operation is NOT run by preparation, issuance or any offline checks.
    internal static int RetainedCoveragePair(string path, string expectedHash)
    {
        var p = ReadRetainedFinal(ReadCoverageMetadata(path, expectedHash),path);
        var plan = p.Resources;
        bool same = plan?.FreshCandidateAdmission == SameCaptureRetainedAdmission;
        Require(plan != null && plan.FreshCandidateAdmission is RetainedCoverageAdmission or SameCaptureRetainedAdmission && plan.ReplayMinutes == 1 && plan.Order != null && plan.Order.SequenceEqual(new[] { "H03" })
            && plan.RetainedChildren == 0 && plan.MaximumChildren == (same ? 9 : 7) && plan.DeadlineSeconds == (same ? 900 : 600) && plan.OfflineDeadlineSeconds == 600
            && plan.FloorBytes == 60_000_000_000 && plan.SourceComponentBytes == 200_000_000 && plan.OutputComponentBytes == 500_000_000
            && plan.CopyFallbackReserveBytes == ReplayReferenceModel.SnapshotEnvelope(65536, 1)
            && plan.FreezeRetainedAllocations && plan.ReuseCurrentMetadataHandles && plan.MetadataHandleRoots != null
            && plan.RetainedRoots != null && plan.MetadataHandleRoots.SequenceEqual(plan.RetainedRoots)
            && plan.FileLimit is > 0 and <= 8192 && plan.DirectoryLimit is > 0 and <= 128
            && plan.RetainedBudgetBytes >= 0 && plan.OutsideBuildAllowanceBytes >= 0 && plan.AdditionalHistoricalReserveBytes >= 0
            && plan.BudgetBytes == checked(plan.StopBytes + 1_000_000_000L) && !plan.ProductionQualification
            && !plan.CompositeQualification && plan.CompositeTrainingRoot == null && plan.ExactContinuationRoot == null
            && plan.ProductionTrainingRoot == null && plan.ExactHeldoutContinuationRoot == null
            && p.ExpiresUtc.Kind == DateTimeKind.Utc && DateTime.UtcNow.AddSeconds(same ? 900 : 600) < p.ExpiresUtc
            && p.MappingReviewHash?.Length == 64 && !string.IsNullOrWhiteSpace(p.MappingReviewer),
            "Retained pair needs its reviewed finite one-minute/no-acquisition admission.");
        Require(OwnedHistoryPathsDisjoint(plan.RetainedRoots, plan.Root) && !Directory.Exists(plan.Root) && !File.Exists(plan.Root)
            && Path.GetFullPath(plan.Root).StartsWith(Path.GetFullPath("artifacts") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "Retained outputs must be fresh and disjoint from all preserved inputs.");
        string referenceCell=same&&p.SameCapture!=null?Path.Combine(Path.GetDirectoryName(p.SameCapture.ManifestPath),"H03"):null;
        string[] referenceMetadata=same&&p.SameCapture!=null ? [p.SameCapture.ConfirmationPath,p.SameCapture.ManifestPath,p.SameCapture.AcceptancePath,
            p.SameCapture.ControllerPath,p.SameCapture.C00PlanPath,p.FreezePath,p.OriginalSelectionPath,
            Path.Combine(referenceCell,"source-clock.json"),Path.Combine(referenceCell,"source-reference-binding.json"),
            Path.Combine(referenceCell,"source-reference-policy.json"),Path.Combine(referenceCell,"source-render-clock.csv"),
            Path.Combine(referenceCell,"source-paints.csv"),Path.Combine(referenceCell,"source-reference-costs.json"),
            Path.Combine(referenceCell,"reference-oracle","source-data.json"),Path.Combine(referenceCell,"reference-oracle","source","evidence.json"),
            Path.Combine(referenceCell,"reference-oracle","report.json")] : null;
        var presentationBinding=PresentationPlanBinding.For(plan);
        if(same&&presentationBinding!=null)referenceMetadata=referenceMetadata.Concat(CalibrationPresentationIntegration.Files.Select(n=>Path.Combine(referenceCell,n))).ToArray();
        int referenceFileCount=presentationBinding==null?16:25;
        Require(!same||referenceMetadata?.Length==referenceFileCount&&plan.AdditionalHistoricalReserveBytes==32_000_000,
            "v10 needs its exact closed reference metadata readset/32MB separate reserve.");
        // Reuse the existing selected-object lease for metadata too. This
        // separate bounded owner never opens an unselected original TS input.
        using var referenceOwner=same?new FrozenCalibrationHistory([],referenceFileCount,64,selectedFiles:referenceMetadata):null;
        if(same)Require(referenceOwner.AllocatedBytes<=32_000_000,"Closed reference metadata32MB bound.");
        foreach (var pin in plan.Files) Require(HashFile(pin.Key) == pin.Value, "Retained helper/source/runtime metadata binding changed.");
        Require(plan.Files.ContainsKey(Path.GetFullPath(Environment.ProcessPath)) && plan.Files.ContainsKey(typeof(ReplayCalibrationRun).Assembly.Location)
            && plan.Files.ContainsKey(typeof(CaptureSession).Assembly.Location) && plan.Files.ContainsKey(typeof(Settings).Assembly.Location)
            && plan.Files.TryGetValue(p.OriginalSelectionPath, out var selectionHash) && selectionHash == p.OriginalSelectionSha256
            && plan.Files.TryGetValue(p.FreezePath, out var freezeHash) && freezeHash == p.FreezeSha256,
            "Actual helper/product/runtime, original selection and freeze must be pinned.");
        var frozen = ReplayCalibrationCatalog.ReadEvidence<ReplayCoverageFreeze>(ReadCoverageMetadata(p.FreezePath, p.FreezeSha256));
        var parents = ReplayCalibrationCatalog.ValidateFreeze(frozen, ReplayCalibrationCatalog.HostIdentity);
        if(same)Require(referenceOwner.Volumes.All(v=>v.Identity==frozen.Volume&&v.AllocationUnit==frozen.AllocationUnit),
            "Closed reference metadata volume/unit changed.");
        ReplayCoverageConfirmation c00 = null;
        ReplaySameCaptureManifest c00Manifest = null;
        ReplaySameCaptureAcceptance c00Acceptance = null;
        if (same)
        {
            Require(frozen.Revision == ReplayCalibrationCatalog.SameCaptureFreezeRevision && p.SameCapture != null,
                "v10 requires its own freeze and genuine accepted C00 dependencies.");
            T Read<T>(string file) => ReplayCalibrationCatalog.ReadEvidence<T>(ReadCoverageMetadata(file,
                plan.Files.TryGetValue(file, out var digest) ? digest : throw new IOException("Unbound C00 dependency.")));
            var dependencies = p.SameCapture;
            c00 = Read<ReplayCoverageConfirmation>(dependencies.ConfirmationPath);
            ReplayCalibrationCatalog.ValidateConfirmation(c00, frozen, parents[1], "C00-current-5m-pair");
            c00Manifest = Read<ReplaySameCaptureManifest>(dependencies.ManifestPath);
            ReplayCalibrationCatalog.ValidateSameManifest(c00Manifest, c00);
            var acceptance = c00Acceptance = Read<ReplaySameCaptureAcceptance>(dependencies.AcceptancePath);
            ReplayCalibrationCatalog.ValidateSameAcceptance(acceptance, c00, c00Manifest);
            string sourceCell=Path.Combine(Path.GetDirectoryName(dependencies.ManifestPath),"H03");
            Require(HashFile(Path.Combine(sourceCell,"source-clock.json"))==c00Manifest.SourceClockHash
                && HashFile(Path.Combine(sourceCell,"source-reference-binding.json"))==c00Manifest.SourceReferenceHash,
                "C00 source clock/reference binding changed.");
            using var reference=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(sourceCell,"source-reference-binding.json")));
            if(presentationBinding!=null)
            {
                CalibrationPresentationIntegration.ValidateBinding(sourceCell,reference.RootElement.GetProperty("presentation"),presentationBinding.Spec);
                CalibrationPresentationIntegration.ValidateRetainedMethod(presentationBinding,
                    CalibrationPresentationReference.ReadJson<PresentationClosure>(Path.Combine(sourceCell,CalibrationPresentationReference.ClosureName)));
            }
            else if(reference.RootElement.TryGetProperty("presentation",out var unexpectedPresentation)&&unexpectedPresentation.ValueKind!=JsonValueKind.Null)
                throw new IOException("Retained plan cannot silently omit the accepted native presentation reference");
            foreach(var role in new[]{("policy","source-reference-policy.json"),("audio","source-render-clock.csv"),("video","source-paints.csv"),("costs","source-reference-costs.json")})
                Require(HashFile(Path.Combine(sourceCell,role.Item2))==reference.RootElement.GetProperty(role.Item1).GetString(),"Accepted source reference component changed.");
            // Reject unavailable/substituted accepted oracle dependencies BEFORE
            // selected input copying or either new retained media output.
            var sourceData=Read<SameOracleRaw>(Path.Combine(sourceCell,"reference-oracle","source-data.json"));
            string evidencePath=Path.Combine(sourceCell,"reference-oracle","source","evidence.json");
            byte[] sourceEvidence=ReadCoverageMetadata(evidencePath,plan.Files[evidencePath]);
            Require(acceptance.Outputs.All(x=>x.SourceEvidenceHash==sourceData.EvidenceHash)
                &&ReplayCalibrationCatalog.ByteHash(sourceEvidence)==sourceData.EvidenceHash,
                "Retained source oracle differs from genuine independently accepted C00 evidence.");
            using var sourceDocument=JsonDocument.Parse(sourceEvidence);var sourceRoot=sourceDocument.RootElement;
            Require(sourceData.Root==Path.Combine(sourceCell,"reference-oracle","source")
                &&sourceData.AudioPath==Path.Combine(sourceData.Root,"audio.raw")
                &&sourceData.FirstVideo==sourceRoot.GetProperty("FirstVideo").GetDouble()
                &&sourceData.FirstAudio==sourceRoot.GetProperty("FirstAudio").GetDouble()
                &&File.Exists(sourceData.AudioPath)&&File.Exists(Path.Combine(sourceData.Root,"ids.raw")),
                "Accepted source reference timing/paths/raw evidence are unavailable or changed.");
            using var closure = JsonDocument.Parse(ReadCoverageMetadata(dependencies.ControllerPath, plan.Files[dependencies.ControllerPath]));
            var closed = closure.RootElement;
            Require(plan.Files[dependencies.C00PlanPath] == c00.AdmissionHash && closed.GetProperty("planHash").GetString() == c00.AdmissionHash
                && closed.GetProperty("operation").GetString() == "C00" && closed.GetProperty("absent").GetBoolean()
                && closed.GetProperty("exitCode").GetInt32() == 0 && closed.GetProperty("postDisposal").GetBoolean()
                && closed.GetProperty("processHandlesDisposed").GetBoolean()
                && (closed.GetProperty("error").ValueKind == JsonValueKind.Null || string.IsNullOrEmpty(closed.GetProperty("error").GetString())),
                "C00 actual parent plan/native post-disposal closure is missing.");
        }
        else Require(p.SameCapture == null && frozen.Revision == ReplayCalibrationCatalog.FreezeRevision,
            "Historical v9 retained policy cannot be rebound to C00.");
        var original = same ? c00.Packet : parents[1].Sources[^1];
        byte[] rawSelection;
        using (var input = new FileStream(p.OriginalSelectionPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Require(input.Length is > 0 and <= 2 * 1024 * 1024, "Original selection metadata size.");
            rawSelection = new byte[(int)input.Length]; input.ReadExactly(rawSelection);
        }
        Require(ReplayCalibrationCatalog.ByteHash(rawSelection) == p.OriginalSelectionSha256, "Original selection changed.");
        var oldInputs = Encoding.UTF8.GetString(rawSelection).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonSerializer.Deserialize<ReplayOutputInput>(line)).ToArray();
        Require(oldInputs.Length == original.SegmentCount && oldInputs.All(i => i != null && i.Extent != null)
            && oldInputs.Sum(i => i.Extent.Length) == original.InputBytes, "Original complete-input witness changed.");
        int originalSeconds = same ? 300 : 1800;
        var oldInfo = new ReplaySessionInfo("metadata only", "metadata only", originalSeconds, 20, 60, "h264_nvenc", ".ts", true, true);
        var oldRequest = new ReplayExportRequest(oldInfo, new(original.OutputStart, original.OutputEnd, originalSeconds), oldInputs, "unused", "unused", new());
        Require(ReplayPairedSelectionLease.Identity(oldRequest, original.Scope) == original.SelectionHash,
            "Original ordered native selection does not match the parent receipt.");
        var allSegments = oldInputs.Select(i => new Segment { Path = i.Path, Start = i.Start, End = i.End }).ToList();
        var interval = ReplayInterval.AtRequest(allSegments, original.OutputEnd, 60);
        var selected = ReplaySelection.Select(allSegments, interval.End, interval.Seconds + ReplaySaveExport.Preroll(oldInfo));
        Require(Math.Abs(interval.Seconds - 60) <= .000001 && selected.Count is > 0 and <= 65
            && p.Inputs != null && p.Inputs.Length == selected.Count
            && p.Inputs.Select(i => i.OriginalPath).SequenceEqual(selected.Select(s => s.Path))
            && plan.SelectedRetainedFiles != null && plan.SelectedRetainedFiles.SequenceEqual(p.Inputs.Select(i => i.CurrentPath))
            && p.Inputs.All(i => Path.IsPathFullyQualified(i.CurrentPath) && Path.GetFullPath(i.CurrentPath) == i.CurrentPath
                && i.ContentSha256?.Length == 64 && i.ContentSha256.All(Uri.IsHexDigit))
            && OwnedHistoryPathsDisjoint(p.Inputs.Select(i => Path.GetDirectoryName(i.CurrentPath)).Distinct().ToArray(), plan.Root),
            "Only the prospectively mapped final [b−60,b] complete inputs may be read.");
        long selectedBytes = selected.Sum(s => oldInputs.Single(i => i.Path == s.Path).Extent.Length);
        if (same)
        {
            var fixedMinute = ReplayCalibrationCatalog.SameCaptureMinute(c00Manifest);
            Require(fixedMinute.Length == p.Inputs.Length && fixedMinute.Select(i => i.Path).SequenceEqual(p.Inputs.Select(i => i.CurrentPath))
                && fixedMinute.Select(i => i.Sha256).SequenceEqual(p.Inputs.Select(i => i.ContentSha256)),
                "Only the genuine immutable C00 mapping/hashes may derive the retained minute.");
            for (int n = 0; n < fixedMinute.Length; n++)
                Require(fixedMinute[n].Extent == oldInputs.Single(i => i.Path == p.Inputs[n].OriginalPath).Extent,
                    "C00 original/alias native attribution changed.");
        }
        Require(selectedBytes is > 0 and <= 200_000_000, "Fixed retained minute exceeds covered input bytes.");
        long whole = checked(plan.RetainedBudgetBytes + plan.OutsideBuildAllowanceBytes + plan.AdditionalHistoricalReserveBytes + NewCohortBytes(plan));
        Require(whole < plan.StopBytes && ReplayStorage.ObserveCapacity(Path.GetDirectoryName(plan.Root)).AvailableBytes >= QualificationFreeRequirement(plan),
            "Full retained/source/output/scratch/copy/closure physical promises do not fit.");
        MediaDependency.RootDirectory = plan.EngineRoot; string engine = MediaDependency.ResolveExecutable(); // verified reuse; no probe
        Require(plan.Files.TryGetValue(engine, out var engineHash) && engineHash.Equals(MediaDependency.ExecutableSha256, StringComparison.OrdinalIgnoreCase),
            "Verified engine bytes must be pinned.");
        using var current = Process.GetCurrentProcess(); DateTime started = DateTime.UtcNow;
        Directory.CreateDirectory(plan.Root); Log.DirectoryPath = plan.Root;
        if(same)Write(Path.Combine(plan.Root,"reference-metadata-lease.json"),new { files=referenceOwner.FileCount,
            referenceOwner.DirectoryCount,referenceOwner.AllocatedBytes,referenceOwner.Volumes,
            nativeInventoryHash=ReplayReferenceModel.Hash(referenceOwner.Inventory),maximumBytes=32_000_000,
            meaning="Exact16 closed metadata objects held read-only through this helper; no unselected original TS object opened." });
        Write(Path.Combine(plan.Root, "admitted-plan.json"), new { planHash = expectedHash, plan });
        nativeChildRoot = plan.Root; nativeChildren = 0; nativeChildLimit = plan.MaximumChildren; AdmitNativeChild("retained-confirmation-helper");
        using var sampler = new Sampler(plan, active: false);
        var outputs = new List<ReplayCalibrationSourceReceipt>(); var leases = new List<ReplayPublicationLease>();
        var actualSamples = new List<Sample>();
        try
        {
            string caseRoot = Path.Combine(plan.Root, "H03"); Directory.CreateDirectory(caseRoot);
            string session = Path.Combine(caseRoot, "buffer", "session-retained"); Directory.CreateDirectory(session);
            // Copy once through held current objects, checking the ORIGINAL
            // content expectations from the reviewed archive/current mapping.
            // No original directory, manifest, allocation or identity is edited.
            sampler.BeginActive(); var copied = new List<Segment>();
            for (int n = 0; n < selected.Count; n++)
            {
                sampler.Check(); var mapping = p.Inputs[n]; var old = oldInputs.Single(i => i.Path == mapping.OriginalPath);
                using var parentsOwner = TrustedStorage.PinDirectories(Path.GetDirectoryName(mapping.CurrentPath));
                Require((File.GetAttributes(mapping.CurrentPath) & (FileAttributes.Compressed | FileAttributes.SparseFile | FileAttributes.ReparsePoint)) == 0,
                    "Mapped input has unsupported allocation/reparse attributes.");
                string target = Path.Combine(session, $"chunk-{n + 1:D9}.ts");
                using var input = new FileStream(mapping.CurrentPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (same) Require(ReplayStorage.ObserveInventory(input.SafeFileHandle, old.Extent) == old.Extent,
                    "Retained C00 input native identity/length changed.");
                Require(input.Length == old.Extent.Length, "Mapped complete input EOF differs from original.");
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                {
                    byte[] buffer = new byte[1024 * 1024]; int read;
                    while ((read = input.Read(buffer)) > 0) { sampler.Check(); hash.AppendData(buffer.AsSpan(0, read)); output.Write(buffer, 0, read); }
                    output.Flush(true);
                }
                Require(Convert.ToHexString(hash.GetHashAndReset()) == mapping.ContentSha256, "Mapped content differs from original expected hash; no alternate window.");
                copied.Add(new() { Path = target, Start = old.Start, End = old.End });
            }
            string manifest = Path.Combine(session, "segments.csv");
            File.WriteAllText(manifest, string.Join('\n', copied.Select(s => FormattableString.Invariant($"{Path.GetFileName(s.Path)},{s.Start:R},{s.End:R}"))) + "\n");
            var info = new ReplaySessionInfo(session, manifest, 60, 20, 60, "h264_nvenc", ".ts", false, true);
            using var source = CaptureSession.OpenRetainedForQualification(info, original.Scope, original.OutputEnd);
            // The sampler observes this private ledger through directory entries so
            // its metadata handles cannot block the worker's atomic replacements.
            var reservations = new ReplayVolumeReservations(Path.Combine(plan.Root, "calibration-ownership"), ReplayStorageSafety.MinimumBytes);
            using var cache = new ReplayCache(cacheDirectory: Path.Combine(caseRoot, "buffer"), cacheBudgetBytes: 1_000_000_000,
                volumeReservations: reservations, fileMode: ReplayFileMode.Production);
            int exportArguments = 0;
            RecorderRuntime.ExportArgumentObserver = arguments => {
                AdmitNativeChild("export:H03");
                Write(Path.Combine(caseRoot, $"export-arguments-{++exportArguments}.json"), arguments);
            };
            source.ValidationArgumentObserver = _ => AdmitNativeChild("runtime-validation:H03");
            var validation = new CalibrationValidationWork.GuardContext(); ReplayPairedSelectionLease paired = null;
            try
            {
                for (int variant = 0; variant < 2; variant++)
                {
                    bool packet = variant == 0; string cell = packet ? caseRoot : Path.Combine(caseRoot, "legacy"); Directory.CreateDirectory(cell);
                    ReplayExportRequest request = null; bool eligible = false; ReplayPublicationLease held = null;
                    void Protect(ReplayExportRequest r)
                    {
                        request = r; eligible = r.NativeFiles?.Eligible == true;
                        Require(eligible, "Retained pair lost actual native request ownership.");
                        if (packet) paired = new(r, original.Scope, () => sampler.Check()); else paired.Verify(r, original.Scope, () => sampler.Check());
                        if (packet) File.WriteAllLines(Path.Combine(caseRoot, "selection.jsonl"), r.Inputs.Select(i => JsonSerializer.Serialize(i)));
                    }
                    void Hold(ReplayExportRequest r, Action check)
                    {
                        check(); Require(r.ValidatedOutput != null, "Retained normal gate has no checked output owner.");
                        sampler.BeforePublication(r.Partial, r.ValidatedOutput.CheckedIdentity);
                        held = new ReplayPublicationLease(r.Partial); leases.Add(held);
                        Require(held.CheckedIdentity == r.ValidatedOutput.CheckedIdentity, "Retained decoder handoff changed the output object.");
                    }
                    var wrapped = new ShortSource(source, 60, original.OutputEnd, exactFallback: !packet, protect: Protect,
                        validationContext: validation, preserveEvidence: true, inspectPublication: Hold,
                        protectedSegments: packet ? null : paired.ReadSegments,
                        qualificationDemand: s => source.LastOutputDemand = UnmodelledSelectionDemand(source.LastOutputSelection = s, 1, plan.OutputComponentBytes, original.Scope, session));
                    var id = cache.RegisterSession(wrapped); var submission = cache.TrySave(id, Stopwatch.GetTimestamp(), cell);
                    Require(submission.Accepted, submission.Rejection);
                    var deadline = Stopwatch.StartNew();
                    while (!submission.Completion.IsCompleted) { sampler.Check(); Require(deadline.Elapsed.TotalSeconds < 130, "Retained export130s deadline."); Thread.Sleep(20); }
                    var result = submission.Completion.GetAwaiter().GetResult();
                    Require(result.Succeeded && request != null && request.NativeFiles is { Closed: true } && eligible
                        && request.PacketPipelineSelected == packet && held != null && held.CheckedIdentity.Identity == ReplayStorage.Observe(result.File).Identity
                        && source.CaptureProducerAbsent && ((IReplaySessionSource)source).OutputProducerStopped, "Retained normal export/ownership/closure failed: " + result.Error);
                    var stage = request.Preparation;
                    Require(packet ? stage is { Joined: true } && stage.Timing is { FeedJoined: true, Aborted: false, Error: null, FedBytes: > 0 and <= 8388608 }
                        : stage == null, "Retained packet preparation/exact path differs from the frozen policy.");
                    var selectionIdentity = source.LastOutputSelection?.Identity ?? submission.AdmissionDemand.SelectionIdentity;
                    var native = new ReplayCalibrationNativeReceipt(ReplayReferenceModel.Hash(new { request.Interval, eligible,
                        source = original.AcquisitionIdentity, inputMappingReview = p.MappingReviewHash, selectionIdentity,
                        request.PacketPipelineSelected, request.NativeFiles.Closed, result.Validation.ExportRecipe, result.Validation.CheckedOutput }),
                        ReplaySaveExport.PolicyIdentity,
                        true, true, packet, true, packet, packet, packet ? stage.Timing.FeedSha256 : null, packet ? stage.Timing.FedBytes : 0);
                    var peaks = sampler.OutputAndWorkspacePeak(result.File); var extent = ReplayStorage.Observe(result.File);
                    var sample = new Sample("H03", original.Scope, result.File, request.Inputs.Sum(i => i.Extent.Length), result.Bytes, extent.AllocatedBytes,
                        sampler.Peak(result.File), original.OutputEnd, 60, selectionIdentity,
                        submission.AdmissionDemand, SaveProcess(cell, "main-export", source.LastExport), source.ExportArguments, result.Interval,
                        packet ? null : SaveProcess(cell, "runtime-validation", source.LastValidation), source.ValidationArguments,
                        peaks.Combined, peaks.Work, SaveProcesses(cell, "main-export", source), peaks.Assembly, peaks.Boundaries,
                        result.Validation, source.LastManagedMux, source.LastIndexBatch, result.Timing, Variant: packet ? "packet" : "legacy",
                        AcquisitionSelectionHash: paired.AcquisitionSelectionHash, NativeExport: native);
                    Write(Path.Combine(cell, "sample.json"), sample);
                    actualSamples.Add(sample);
                    if (packet) Write(Path.Combine(cell, "managed-export.json"), source.LastManagedMux);
                    else Write(Path.Combine(cell, "runtime-validation-exit.json"), sample.RuntimeProcess);
                    if (packet) Write(Path.Combine(caseRoot, "native-export-witness.json"), new {
                        policy = ReplaySaveExport.PolicyIdentity, ownedRequest = true, eligibleAtExport = eligible,
                        pipelineSelected = true, scopeClosed = request.NativeFiles.Closed, request.Partial,
                        selectionIdentity = source.LastOutputSelection.Identity,
                        preparationSelected = true, preparationJoined = stage.Joined, preparation = stage.Timing });
                    PublishCreatorProofs(plan, caseRoot, "H03", packet ? "packet" : "legacy", source, exportArguments);
                    sampler.EndActive(); sampler.BeginOffline("retained-independent-decode", 32_000_000, 60);
                    var inspection = Inspect(engine, cell, result.File, 60, "H03", 50, held, plan);
                    leases.Remove(held); held = null; sampler.EndOffline(); sampler.BeginActive();
                    var qualification = QualificationSource(sample, inspection, cell,
                        (original.CapturePollGrowthBytes.Value, original.CaptureStopGrowthBytes.Value, original.CaptureVolume),
                        sampler.MaximumActiveGap, sampler.MaximumCapacityGap, original);
                    outputs.Add(qualification); Write(Path.Combine(cell, "qualification-source.json"), qualification);
                    if (packet) paired.PrepareForReplay(source, () => sampler.Check());
                }
            }
            finally { paired?.Dispose(); }
            sampler.EndActive();
            SameOracleResult oracle = same ? CollectSameOracle(plan,engine,outputs.ToArray(),actualSamples.ToArray(),sampler,
                Path.Combine(Path.GetDirectoryName(p.SameCapture.ManifestPath),"H03"),createSource:false,frozen.Context) : null;
            if (same) Require(c00Acceptance.Outputs.All(x=>x.SourceEvidenceHash==oracle.SourceEvidenceHash),"Retained oracle source differs from accepted C00 source evidence.");
            sampler.SealClosedCase("H03"); sampler.StopAndCheck();
            var confirmation = new ReplayCoverageConfirmation(same ? ReplayCalibrationCatalog.SameCaptureRole : "retained-30m-H03-final-1m-pair", frozen.FreezeHash, frozen.TargetExporter,
                ReplayCalibrationCatalog.CaptureHostFacts(), started, DateTime.UtcNow, expectedHash, HashFile(Path.Combine(plan.Root, "storage.csv")),
                same ? c00.ConfirmationHash : frozen.Parents[1].Sha256, original.AcquisitionIdentity, outputs[0], outputs[1], null);
            confirmation = confirmation with { ConfirmationHash = ReplayCalibrationCatalog.ConfirmationHash(confirmation) };
            ReplayCalibrationCatalog.ValidateConfirmation(confirmation, frozen, parents[1], confirmation.Role, c00);
            Write(Path.Combine(plan.Root, "retained-confirmation.json"), confirmation);
            if (same) Write(Path.Combine(plan.Root,"independent-acceptance-data.json"),new {
                confirmationHash=confirmation.ConfirmationHash,manifestHash=c00Manifest.ManifestHash,oraclePolicy=CalibrationSourceReference.Policy,
                oracleEvidenceHash=oracle.ReportHash,outputs=oracle.Outputs,status="awaiting-named-independent-review",ordinarySavedSamples=0 });
            Write(Path.Combine(plan.Root, "result.json"), new { result = "passed-fixed-retained-1m-pair-pending-independent-acceptance",
                ordinarySavedSamples = 0, captureStarted = false, modelActivated = false, confirmation.ConfirmationHash }); return 0;
        }
        catch (Exception e)
        {
            WriteFailure(plan.Root, e, new { result = "failed-no-retry-no-alternate-window", error = e.ToString(), outputs }); return 1;
        }
        finally { RecorderRuntime.ExportArgumentObserver = null; foreach (var lease in leases) lease.Dispose(); }
    }
}
