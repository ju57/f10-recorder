using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace F10Recorder;

// Controlled metadata is deliberately nonempirical. No media reader or producer
// is reachable from this selector, including its actual coordinator guard case.
internal static partial class ReplayCalibrationCatalogChecks
{
    sealed class UnmodelledStagingSource(string directory) : IReplaySessionSource
    {
        internal bool ReachedReservedPreparation;
        public ReplaySessionInfo ReplayInfo => new(directory, Path.Combine(directory, "segments.csv"), 300, 20, 60, "h264_nvenc", ".ts", false, true);
        public bool Stopped => true;
        public bool PreserveEvidence => true;
        public bool SupportsOwnedExport => true;
        public bool SupportsStagedPreparation => true;
        public bool OutputProducerStopped => true;
        public string Diagnostics => "controlled unmodelled staging admission, before any codec";
        public string OutputDemandIdentity => ReplayReferenceModel.ExporterIdentity;
        public double SaveCutoffAt(long _) => 300.25;
        public void FreezeAt(long _) { }
        public void StopCapture() { }
        public void CheckCaptureHealth(double _) { }
        public ReplayOutputDemand EstimateOutput(ReplayOutputSelection s) => ReplayCalibrationRun.UnmodelledQualificationDemand(s,
            ReplayCalibrationRun.ContextFixturePlan(), "controlled-scope", directory);
        public ReplaySavePreparation BeginPreparation(ReplayRootedFiles files, ReplayInterval interval, Segment candidate, string partial,
            Action accepted, Action<ReplayProducerProof> changed, Action check, Action<ReplaySavePreparation> take)
        { accepted(); check(); ReachedReservedPreparation = true; throw new IOException("CONTROLLED-qualified-scratch-before-codec"); }
        public void Export(IReadOnlyList<string> _, string __) => throw new IOException("Unexpected codec entry.");
        public void Dispose() { }
    }
    internal static int CoverageParentChecks(string root, string fivePath, string fiveHash, string thirtyPath, string thirtyHash, string omissionReview)
    {
        Require(!Directory.Exists(root) && !File.Exists(root), "Preserve previous metadata check.");
        byte[] five = File.ReadAllBytes(fivePath), thirty = File.ReadAllBytes(thirtyPath);
        Require(ReplayCalibrationCatalog.ByteHash(five) == fiveHash && ReplayCalibrationCatalog.ByteHash(thirty) == thirtyHash, "Original receipt bytes changed.");
        var parents = new[] { new ReplayQualificationParent("original-5m-3T3H", five, fiveHash, ReplayCalibrationCatalog.OriginalModelHash(five), omissionReview),
            new ReplayQualificationParent("original-30m-3T3H", thirty, thirtyHash, ReplayCalibrationCatalog.OriginalModelHash(thirty), omissionReview) };
        var originals = parents.Select(ReplayCalibrationCatalog.ReadMeasurement).ToArray();
        var union = ReplayCalibrationCatalog.TrainingUnion(originals);
        Require(ReplayCalibrationCatalog.ByteHash(File.ReadAllBytes(fivePath)) == fiveHash
            && ReplayCalibrationCatalog.ByteHash(File.ReadAllBytes(thirtyPath)) == thirtyHash, "Original metadata changed during read-only verification.");
        Directory.CreateDirectory(root);
        File.WriteAllBytes(Path.Combine(root, "parent-checks.json"), JsonSerializer.SerializeToUtf8Bytes(new {
            result = "passed-original-v7-v8-metadata-training-rederivation", union, parents = originals.Select(r => new {
                r.Revision, r.RecipeHash, r.ExporterIdentity, r.HostIdentity, r.Model, r.ReceiptHash }),
            immutable = true, operationalBridgeCreated = false, currentMeasurements = 0, mediaPayloadRead = false,
            limitation = "Original missing capture/fixture process fields remain missing; the supplied review hash is a provenance pointer, not new closure evidence or acceptance." }));
        Console.WriteLine("Passed original v7/v8 byte/hash/model/training/heldout/growth checks; union=" + JsonSerializer.Serialize(union)
            + "; no measured model, bridge or current qualification created."); return 0;
    }
    internal static int CoverageChecks(string root)
    {
        Require(!Directory.Exists(root) && !File.Exists(root) && Path.GetFullPath(root).StartsWith(
            Path.GetFullPath("artifacts") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Fresh ignored fixture root required.");
        TrustedStorage.CreatePrivateDirectory(root);
        var watch = Stopwatch.StartNew();
        var cases = new List<string>();
        void Check(string name, bool value)
        {
            Require(value, "Coverage fixture: " + name); cases.Add(name);
            Require(watch.Elapsed.TotalSeconds < 45, "Coverage fixture work deadline.");
        }
        bool Rejects(Action action)
        { try { action(); return false; } catch (Exception e) when (e is IOException or JsonException or OverflowException or ArgumentException or InvalidOperationException) { return true; } }
        ReplayCalibrationReceipt Growth(int minutes)
        {
            var r = Fixture(root, minutes);
            ReplayCalibrationSourceReceipt Grow(ReplayCalibrationSourceReceipt s) => s with {
                CapturePollGrowthBytes = 8192, CaptureStopGrowthBytes = 4096, CaptureVolume = r.Model.Volume,
                CaptureObservationMilliseconds = ReplayCapacityPolicy.CoverageMilliseconds, MaximumCapacityGapMs = 100 };
            var h = new ReplayCaptureHeadroom(r.Model.Scope, r.Model.Volume, 4096,
                ReplayReferenceModel.CaptureAllowance(8192, 4096), ReplayReferenceModel.CaptureAllowance(4096, 4096), ReplayCapacityPolicy.CoverageMilliseconds);
            var m = r.Model with { CaptureHeadroom = h };
            m = m with { Revision = ReplayCalibrationCatalog.ModelRevision(m.Scope, m.Volume, 4096,
                m.FinalResidual, m.SampledFinalizationExtra, m.TrainingReceipts, ControlledHost, h, minutes) };
            return ReplayCalibrationCatalog.CreateReceipt(m, r.Sources.Select(Grow).ToArray(), 100, r.ObservationHash,
                r.AdmissionHash, ControlledHost, r.LegacyOutputs.Select(Grow).ToArray(), ControlledHostFacts);
        }
        var five = Growth(5); var thirty = Growth(30);
        ReplayQualificationParent Parent(ReplayCalibrationReceipt r, string role)
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(r);
            return new(role, bytes, ReplayCalibrationCatalog.ByteHash(bytes), ReplayCalibrationCatalog.OriginalModelHash(bytes), null);
        }
        var parents = new[] { Parent(five, "original-5m-3T3H"), Parent(thirty, "original-30m-3T3H") };
        var context = new ReplayQualificationContext(["controlled", "<session>", "<common-clock-filter>"], 1920, 1080,
            "controlled-endpoint", 48000, 2, "f32le");
        DateTime now = DateTime.UtcNow, frozenAt = now.AddMinutes(-5), started = now.AddMinutes(-4), completed = now.AddMinutes(-1);
        var review = new ReplayApplicabilityReview(Hash("fixture review"), Hash("fixture source delta"), "controlled reviewer", frozenAt.AddSeconds(-1), true,
            parents.Select(p => p.Sha256).ToArray());
        var frozen = ReplayCalibrationCatalog.CreateCoverageFreeze(parents, context, ControlledHostFacts, review, frozenAt);
        string originalParents = ReplayReferenceModel.Hash(parents);
        Check("training-only-union", frozen.Envelope == new ReplayFrozenEnvelope(1234, 8192,
            ReplayReferenceModel.CaptureAllowance(8192, 4096), ReplayReferenceModel.CaptureAllowance(4096, 4096)));
        Check("canonical-host-is-UTF8-not-JSON-string-hash", ControlledHostFacts.Valid()
            && ControlledHost != ReplayReferenceModel.Hash(ControlledHostFacts.CanonicalJson));
        string changedJson = ControlledHostFacts.CanonicalJson.Replace("controlled-driver", "changed-driver");
        var changedHost = new ReplayHostFacts(changedJson, ReplayHostFacts.Digest(changedJson));
        Check("actual-driver-operands-change-host", changedHost.Valid() && changedHost.Identity != ControlledHost);
        Check("scope-matches-existing-capture-algorithm", context.Scope(ReplayReferenceModel.ExporterIdentity, 5) == ReplayReferenceModel.Hash(new {
            arguments = context.Arguments, width = context.Width, height = context.Height, endpoint = context.Endpoint,
            context.SampleRate, context.Channels, context.FfmpegFormat, exporter = ReplayReferenceModel.ExporterIdentity, ReplayMinutes = 5 }));

        // Same type/gates as genuine helper output; all identities/times below
        // are explicitly supplied fixture metadata, never empirical evidence.
        ReplayCoverageConfirmation Confirmation(bool current)
        {
            int minutes = current ? 5 : 1, next = current ? 10 : 20;
            string tag = current ? "C00-fixture" : "retained-fixture";
            var basis = current ? five : thirty;
            string scope = current ? context.Scope(ReplayReferenceModel.ExporterIdentity, 5) : thirty.Model.Scope;
            ReplayCalibrationProcessReceipt FreshProcess(string role) => Process(tag + role) with {
                ProcessId = ++next, StartedUtcTicks = started.AddSeconds(next).Ticks };
            var capture = current ? FreshProcess("capture") : basis.Sources[^1].Capture;
            var source = current ? FreshProcess("source") : basis.Sources[^1].Source;
            string acquisition = current ? Hash(tag + "acquisition") : basis.Sources[^1].AcquisitionIdentity;
            ReplayCalibrationSourceReceipt Output(ReplayCalibrationSourceReceipt s, bool packet)
            {
                string variant = packet ? "packet" : "exact";
                var file = s.QualificationOutput with { Identity = tag + variant };
                var export = FreshProcess(variant + "export");
                return s with { Scope = scope, AcquisitionIdentity = acquisition, Capture = capture, Source = source,
                    SampleHash = Hash(tag + variant + "sample"), SelectionHash = Hash(tag + "selection"),
                    RequestSelectionHash = Hash(tag + variant + "request"), Decode = FreshProcess(variant + "decode"),
                    Export = export, ExportProcesses = [export], SegmentCount = 60 * minutes,
                    OutputStart = current ? 0 : thirty.Sources[^1].OutputEnd - 60, OutputEnd = current ? 300 : thirty.Sources[^1].OutputEnd,
                    ExportSeconds = 60 * minutes, QualificationOutput = file,
                    RuntimeValidation = packet ? null : FreshProcess(variant + "runtime"),
                    NormalValidation = s.NormalValidation with { CheckedOutput = file, CodedPictures = 3600L * minutes, Frames = packet ? 0 : 3600L * minutes },
                    NativeExport = s.NativeExport with { EvidenceHash = Hash(tag + variant + "native") },
                    ManagedExport = packet ? s.ManagedExport with { EvidenceHash = Hash(tag + "managed"), ParentStartedUtcTicks = started.Ticks,
                        Output = file, Index = Index(60 * minutes, 0, 0) } : null };
            }
            var c = new ReplayCoverageConfirmation(current ? "C00-current-5m-pair" : "retained-30m-H03-final-1m-pair", frozen.FreezeHash,
                ReplayReferenceModel.ExporterIdentity, ControlledHostFacts, started, completed, Hash(tag + "admission"), Hash(tag + "observation"),
                current ? null : parents[1].Sha256, current ? null : thirty.Sources[^1].AcquisitionIdentity,
                Output(basis.Sources[^1], true), Output(basis.LegacyOutputs[^1], false), null);
            return c with { ConfirmationHash = ReplayCalibrationCatalog.ConfirmationHash(c) };
        }
        var c00 = Confirmation(true); var retained = Confirmation(false);
        ReplayConfirmationAcceptance Acceptance(ReplayCoverageConfirmation c) => new(c.ConfirmationHash, Hash(c.Role + "accepted"),
            Hash(c.Role + "independent-source-clock-one-frame-endpoints"), true, "independent fixture reviewer", completed.AddSeconds(1));
        var receipt = ReplayCalibrationCatalog.CreateCoverageReceipt(frozen, c00, retained, [Acceptance(c00), Acceptance(retained)]);
        string catalog = Path.Combine(root, "catalog"); TrustedStorage.CreatePrivateDirectory(catalog);
        string file = Path.Combine(catalog, ReplayCalibrationCatalog.CoverageFileName);
        void WriteBridge(ReplayCoverageReceipt r) => File.WriteAllBytes(file, JsonSerializer.SerializeToUtf8Bytes(r));
        ReplayCalibrationAvailability LoadBridge(int minutes = 5) => ReplayCalibrationCatalog.LoadFrom(catalog, ControlledHost, minutes);
        WriteBridge(receipt);
        Check("existing-private-helper-coverage-shape", ReplayWorkerLatencyPilot.PrivateCatalogShape(JsonSerializer.SerializeToUtf8Bytes(receipt))
            == (ReplayCalibrationCatalog.CoverageFileName, 5));
        Check("benchmark-and-endpoint-pin-actual-coverage-file", ReplayCalibrationCatalog.EvidenceFileName(catalog, 30) == ReplayCalibrationCatalog.CoverageFileName);
        Check("existing-private-helper-legacy-shape", ReplayWorkerLatencyPilot.PrivateCatalogShape(JsonSerializer.SerializeToUtf8Bytes(thirty))
            == (ReplayCalibrationCatalog.FileNameFor(30), 30));
        string alias = Path.Combine(root, "catalog-alias"); TrustedStorage.CreatePrivateDirectory(alias);
        RootedOperationChecks.Junction(alias, catalog);
        try { Check("reparse-catalog-rejected", ReplayCalibrationCatalog.LoadFrom(alias, ControlledHost).Model == null); }
        finally { RootedOperationChecks.RemoveJunction(alias); }
        Check("protected-serialized-loader", LoadBridge().Model?.ReplayMinutes == 5);
        for (int minutes = 1; minutes <= 30; minutes++)
        {
            var m = LoadBridge(minutes).Model;
            Check("profile-routing-" + minutes, m != null && m.ReplayMinutes == minutes && m.Scope == context.Scope(receipt.Frozen.TargetExporter, minutes)
                && m.TrainingReceipts.Length == 0 && m.Coverage != null && m.CaptureHeadroom.Scope == m.Scope
                && m.InputByteLimit == (minutes == 1 ? 200_000_000 : minutes <= 5 ? 1_000_000_000 : 5_000_000_000L)
                && ReplayReferenceModel.InputCountLimit(minutes) == 60 * minutes + 5
                && ReplayReferenceModel.ConcatLimit(minutes) == (minutes <= 5 ? 65536 : 1048576)
                && ReplayCalibrationCatalog.IsApplicableToDestination(m, catalog, ControlledHost));
        }
        Check("profile-zero", LoadBridge(0).Model == null); Check("profile-31", LoadBridge(31).Model == null);
        void Reject(string name, Func<ReplayCoverageReceipt, ReplayCoverageReceipt> change)
        {
            var r = change(receipt); r = r with { ReceiptHash = ReplayCalibrationCatalog.CoverageHash(r) };
            WriteBridge(r); var loaded = LoadBridge();
            Check(name, loaded.Model == null && !string.IsNullOrWhiteSpace(loaded.Reason) && loaded.Reason.Length <= 160);
        }
        ReplayCoverageReceipt Freeze(ReplayCoverageReceipt r, Func<ReplayCoverageFreeze, ReplayCoverageFreeze> change)
        {
            var f = change(r.Frozen); f = f with { FreezeHash = ReplayCalibrationCatalog.FreezeHash(f) }; return r with { Frozen = f };
        }
        Reject("missing-C00", r => r with { CurrentContext = null });
        Reject("missing-retained-confirmation", r => r with { RetainedMinute = null });
        Reject("missing-independent-acceptance", r => r with { Acceptances = [] });
        Reject("missing-independent-source-clock-endpoints", r => r with { Acceptances = r.Acceptances.Select(a => a with { SourceClockEndpointsVerified = false }).ToArray() });
        Reject("wrong-schema", r => r with { Revision = "reference-coverage-v10" });
        Reject("target-bytes", r => Freeze(r, f => f with { TargetExporter = "other build" }));
        Reject("changed-recipe", r => Freeze(r, f => f with { TargetRecipe = Hash("other recipe") }));
        Reject("wrong-driver", r => Freeze(r, f => f with { Host = changedHost }));
        Reject("wrong-context", r => Freeze(r, f => f with { Context = f.Context with { Endpoint = "different endpoint" } }));
        Reject("wrong-volume", r => Freeze(r, f => f with { Volume = @"\\?\Volume{00000000-0000-0000-0000-000000000001}\" }));
        Reject("wrong-unit", r => Freeze(r, f => f with { AllocationUnit = 8192 }));
        Reject("heldout-or-C00-refit", r => Freeze(r, f => f with { Envelope = f.Envelope with { FinalizationExtra = f.Envelope.FinalizationExtra + 4096 } }));
        Reject("overflow-envelope", r => Freeze(r, f => f with { Envelope = f.Envelope with { Residual = long.MaxValue } }));
        Reject("unsupported-domain", r => Freeze(r, f => f with { DomainPolicy = "all profiles" }));
        Reject("parent-bytes-modified", r => Freeze(r, f => f with { Parents = [f.Parents[0] with { ReceiptBytes = [123, 125] }, f.Parents[1]] }));
        var nullBytes = Encoding.UTF8.GetBytes("null");
        Reject("null-parent-JSON", r => Freeze(r, f => f with { Parents = [f.Parents[0] with { ReceiptBytes = nullBytes, Sha256 = ReplayCalibrationCatalog.ByteHash(nullBytes) }, f.Parents[1]] }));
        ReplayCoverageReceipt ChangeConfirmation(ReplayCoverageReceipt r, bool current, Func<ReplayCoverageConfirmation, ReplayCoverageConfirmation> change)
        {
            var c = change(current ? r.CurrentContext : r.RetainedMinute); c = c with { ConfirmationHash = ReplayCalibrationCatalog.ConfirmationHash(c) };
            return current ? r with { CurrentContext = c } : r with { RetainedMinute = c };
        }
        Reject("historical-C00-acquisition", r => ChangeConfirmation(r, true, c => c with { Packet = c.Packet with { Capture = five.Sources[^1].Capture } }));
        Reject("historical-output-creator", r => ChangeConfirmation(r, false, c => c with { Packet = c.Packet with { Decode = thirty.Sources[^1].Decode } }));
        Reject("retained-different-window", r => ChangeConfirmation(r, false, c => c with { Packet = c.Packet with { OutputEnd = c.Packet.OutputEnd - 1 } }));
        Reject("confirmation-exceeds-frozen-growth", r => ChangeConfirmation(r, true, c => c with { Packet = c.Packet with { CapturePollGrowthBytes = frozen.Envelope.Poll + 1 } }));
        Reject("confirmation-exceeds-frozen-peak", r => ChangeConfirmation(r, true, c => c with { Packet = c.Packet with { CombinedPeakAllocation = long.MaxValue } }));
        Reject("packet-missing-source-byte-proof", r => ChangeConfirmation(r, true, c => c with { Packet = c.Packet with { NormalValidation = c.Packet.NormalValidation with { SourceProofHash = null } } }));
        Reject("unjoined-readers", r => ChangeConfirmation(r, true, c => c with { Packet = c.Packet with { ManagedExport = c.Packet.ManagedExport with { Index = c.Packet.ManagedExport.Index with { SourceHandlesClosed = false } } } }));
        Reject("same-independent-decode", r => ChangeConfirmation(r, true, c => c with { Exact = c.Exact with { Decode = c.Packet.Decode } }));
        Reject("exact-missing-normal-decode", r => ChangeConfirmation(r, true, c => c with { Exact = c.Exact with { RuntimeValidation = null } }));
        Reject("sample-role-change", r => Freeze(r, f => f with { Parents = [f.Parents[0] with { Role = "C00-training" }, f.Parents[1]] }));
        Reject("parent-path-traversal-is-not-a-role", r => Freeze(r, f => f with { Parents = [f.Parents[0] with { Role = "../../reference-v1.json" }, f.Parents[1]] }));
        WriteBridge(receipt);
        Check("wrong-current-host", ReplayCalibrationCatalog.LoadFrom(catalog, changedHost.Identity).Model == null);
        using (var writer = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) Check("writer-denies-bridge-read-lease", LoadBridge().Model == null);
        var acl = new FileInfo(file).GetAccessControl(); var badAcl = new FileInfo(file).GetAccessControl();
        badAcl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Write, AccessControlType.Allow));
        try { new FileInfo(file).SetAccessControl(badAcl); Check("untrusted-bridge-ACL", LoadBridge().Model == null); }
        finally { var restored = new FileSecurity(); restored.SetSecurityDescriptorBinaryForm(acl.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access); new FileInfo(file).SetAccessControl(restored); }
        string valid = JsonSerializer.Serialize(receipt);
        foreach (var bad in new[] { "null", "[]", "{}", "{\"Revision\":\"duplicate\"," + valid[1..], "{\"Unexpected\":0," + valid[1..], new string(' ', ReplayCalibrationCatalog.CoverageMaximumBytes + 1) })
        { File.WriteAllText(file, bad); Check("strict-JSON-" + cases.Count, LoadBridge().Model == null); }
        WriteBridge(receipt);
        string inputs = Path.Combine(root, "inputs"); Directory.CreateDirectory(inputs);
        string dummy = Path.Combine(inputs, "one.ts"); File.WriteAllBytes(dummy, [0]); var extent = ReplayStorage.Observe(dummy);
        foreach (int minutes in Enumerable.Range(1, 30))
        {
            var m = LoadBridge(minutes).Model; long limit = m.InputByteLimit;
            var input = new ReplayOutputInput(dummy, 0, 60 * minutes, extent with { Length = limit });
            var selection = new ReplayOutputSelection(Hash("selection" + minutes), ReplayReferenceModel.ExporterIdentity, root, 60 * minutes, true, new[] { input });
            var demand = m.Estimate(selection, m.Scope, inputs);
            Check("selection-tier-exact-" + minutes, demand.Kind == ReplayOutputEvidenceKind.CalibratedEstimate && demand.PeakAllocatedBytes == m.PeakBytes(limit));
            Check("selection-tier-plus-one-" + minutes, m.Estimate(selection with { Inputs = new[] { input with { Extent = extent with { Length = limit + 1 } } } }, m.Scope, inputs).Kind == ReplayOutputEvidenceKind.Unmeasured);
            Check("selection-count-plus-one-" + minutes, m.Estimate(selection with { Inputs = Enumerable.Repeat(input, ReplayReferenceModel.InputCountLimit(minutes) + 1).ToArray() }, m.Scope, inputs).Kind == ReplayOutputEvidenceKind.Unmeasured);
            Check("head-inclusive-cap-" + minutes, m.Estimate(selection with { Inputs = new[] { input with { Start = -1.10001 } } }, m.Scope, inputs).Kind == ReplayOutputEvidenceKind.Unmeasured);
            var open = selection with { Closed = false, End = selection.End + .25, Start = .25 };
            Check("whole-tier-open-tail-" + minutes, m.Estimate(open, m.Scope, inputs) is { Kind: ReplayOutputEvidenceKind.CalibratedEstimate } d
                && d.PeakAllocatedBytes == m.PeakBytes(limit) && d.SnapshotPeakAllocatedBytes == ReplayReferenceModel.SnapshotEnvelope(4096, minutes)
                && d.Scratch == ReplaySaveExport.WorkDemand(limit, 4096));
        }
        Check("overflow-no-credit", Rejects(() => ReplayReferenceModel.SnapshotEnvelope(long.MaxValue, 30)));
        string stagingRoot = Path.Combine(root, "staging"), stagingInputs = Path.Combine(stagingRoot, "buffer", "session-dummy");
        Directory.CreateDirectory(stagingInputs);
        var manifestLines = new List<string>();
        for (int n = 0; n <= 300; n++)
        {
            string leaf = $"chunk-{n + 1:D9}.ts"; File.WriteAllBytes(Path.Combine(stagingInputs, leaf), [0]);
            manifestLines.Add($"{leaf},{n},{n + 1}");
        }
        File.WriteAllLines(Path.Combine(stagingInputs, "segments.csv"), manifestLines);
        using (var stagingSource = new UnmodelledStagingSource(stagingInputs))
        using (var cache = new ReplayCache(cacheDirectory: Path.Combine(stagingRoot, "buffer"), cacheBudgetBytes: 16_777_216,
            volumeReservations: new ReplayVolumeReservations(Path.Combine(stagingRoot, "authority"), ReplayStorageSafety.MinimumBytes), fileMode: ReplayFileMode.Production))
        {
            var id = cache.RegisterSession(stagingSource); var submission = cache.TrySave(id, Stopwatch.GetTimestamp(), stagingRoot);
            Check("C00-unmodelled-actual-Cache-admission: " + (submission.Rejection ?? "accepted"), submission.Accepted
                && submission.AdmissionDemand is { Kind: ReplayOutputEvidenceKind.SourceBound, Scratch: not null });
            var result = submission.Completion.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            Check("C00-shared-reservation-authorizes-staging-before-codec", stagingSource.ReachedReservedPreparation && !result.Succeeded
                && result.Error.Contains("CONTROLLED-qualified-scratch-before-codec"));
        }
        // These are real serialized loads and actual filesystem applicability,
        // not the old in-memory model/unconditionally-true applicability seam.
        string profile = Path.Combine(root, "profile"), authority = Path.Combine(root, "authority"); Directory.CreateDirectory(profile);
        using (var coordinator = new ReplayStorageCoordinator(new(profile, authority), new(authority),
            catalog: () => LoadBridge(2), applicable: (m, p) => ReplayCalibrationCatalog.IsApplicableToDestination(m, p, ControlledHost)))
        {
            var model = LoadBridge(2).Model; int stop = 0;
            coordinator.RequireAdmission(profile, root, new Settings { ReplayMinutes = 2 });
            coordinator.RequireResume(profile, root, 2);
            coordinator.BeginCapture(model.Scope, profile, root, () => Interlocked.Increment(ref stop), replayMinutes: 2);
            Check("serialized-loader-to-admission-resume-begin", coordinator.CapturePending && !coordinator.Stopped);
            File.Delete(file); // only this test's private bridge, no parent or user evidence
            var wait = Stopwatch.StartNew();
            while (!coordinator.Stopped && wait.Elapsed.TotalSeconds < 5) { coordinator.Poll(); Thread.Sleep(5); }
            Check("withdrawal-stops-actual-guard", coordinator.Stopped && stop == 1);
            Check("withdrawn-model-no-numeric-use", !ReplayCalibrationCatalog.IsApplicableToDestination(model, root, ControlledHost));
            coordinator.CompleteCapture(true);
        }
        Check("original-parent-bytes-and-roles-immutable", originalParents == ReplayReferenceModel.Hash(parents));
        WriteBridge(receipt with { CurrentContext = null, ReceiptHash = "pending genuine evidence" });
        Check("final-pending-state-stays-ineligible", LoadBridge().Model == null);
        // The same native fixture also exercises the separate v10 serialized
        // policy. These are fabricated metadata, never empirical confirmations.
        var sf=frozen with {Revision=ReplayCalibrationCatalog.SameCaptureFreezeRevision,FreezeHash=null};
        sf=sf with {FreezeHash=ReplayCalibrationCatalog.FreezeHash(sf)};
        var digestInputs=Enumerable.Range(0,300).Select(n=>new ReplayCompletedInputDigest(Path.Combine(root,$"unread-generated-{n:D3}.ts"),n,n+1,
            new(five.Model.Volume,"generated-native-"+n,4096,n<100?4:3,4096),Hash("generated-digest-"+n))).ToArray();
        var sc=c00 with {FreezeHash=sf.FreezeHash};
        var sr=new ReplayExportRequest(new("metadata","metadata",300,20,60,"h264_nvenc",".ts",true,true),new(0,300,300),
            digestInputs.Select(i=>new ReplayOutputInput(i.Path,i.Start,i.End,i.Extent)).ToArray(),"unused","unused",new());
        string sel=ReplayPairedSelectionLease.Identity(sr,sc.Packet.Scope);
        sc=sc with {Packet=sc.Packet with {SelectionHash=sel},Exact=sc.Exact with {SelectionHash=sel},ConfirmationHash=null};
        sc=sc with {ConfirmationHash=ReplayCalibrationCatalog.ConfirmationHash(sc)};
        var sm=new ReplaySameCaptureManifest(ReplayCalibrationCatalog.SameCaptureRevision,sc.ConfirmationHash,sc.Packet.AcquisitionIdentity,sel,0,300,
            Hash("generated-clock"),Hash("generated-reference"),digestInputs,null);
        sm=sm with {ManifestHash=ReplayCalibrationCatalog.SameManifestHash(sm)};
        var fixedMinute=ReplayCalibrationCatalog.SameCaptureMinute(sm);
        DateTime rs=sc.CompletedUtc.AddSeconds(2),re=rs.AddSeconds(25);int processOrdinal=500;
        ReplayCalibrationProcessReceipt NewProcess(ReplayCalibrationProcessReceipt p)=>p with {ProcessId=++processOrdinal,StartedUtcTicks=rs.AddSeconds(1).Ticks};
        ReplayCalibrationSourceReceipt Short(ReplayCalibrationSourceReceipt p)
        {
            var checkedFile=p.QualificationOutput with {Length=512};
            var export=NewProcess(p.Export);
            return p with {AcquisitionIdentity=sc.Packet.AcquisitionIdentity,Capture=sc.Packet.Capture,Source=sc.Packet.Source,Scope=sc.Packet.Scope,
                CaptureArgumentsHash=sc.Packet.CaptureArgumentsHash,InputBytes=fixedMinute.Sum(i=>i.Extent.Length),SegmentCount=fixedMinute.Length,
                OutputStart=240,OutputEnd=300,FinalBytes=512,QualificationOutput=checkedFile,Export=export,ExportProcesses=[export],Decode=NewProcess(p.Decode),
                RuntimeValidation=p.RuntimeValidation==null?null:NewProcess(p.RuntimeValidation),
                NormalValidation=p.NormalValidation with {CheckedOutput=checkedFile},
                ManagedExport=p.ManagedExport==null?null:p.ManagedExport with {ParentStartedUtcTicks=rs.Ticks,Output=checkedFile,Index=Index(fixedMinute.Length,0,0)}};
        }
        var rc=retained with {Role=ReplayCalibrationCatalog.SameCaptureRole,FreezeHash=sf.FreezeHash,StartedUtc=rs,CompletedUtc=re,
            ParentReceiptHash=sc.ConfirmationHash,ParentAcquisitionIdentity=sc.Packet.AcquisitionIdentity,Packet=Short(retained.Packet),Exact=Short(retained.Exact),ConfirmationHash=null};
        rc=rc with {ConfirmationHash=ReplayCalibrationCatalog.ConfirmationHash(rc)};
        ReplaySameCaptureAcceptance SA(ReplayCoverageConfirmation c)=>new(c.ConfirmationHash,sm.ManifestHash,ReplayCalibrationCatalog.SameCaptureOracle,
            Hash(c.Role+"generated oracle report"),new[]{c.Packet,c.Exact}.Select(p=>new ReplaySameCaptureOutputCheck(p.SampleHash,Hash(p.SampleHash+"new oracle decode"),
                Hash("same generated source oracle"),(long)Math.Round(p.ExportSeconds*60),(long)Math.Round(p.ExportSeconds*60),28,.001,.004,.025)).ToArray(),
            "independent generated-fixture reviewer",c.CompletedUtc.AddSeconds(1));
        var sameReceipt=new ReplaySameCaptureReceipt(ReplayCalibrationCatalog.SameCaptureRevision,sf,sc,sm,rc,[SA(sc),SA(rc)],null);
        sameReceipt=sameReceipt with {ReceiptHash=ReplayCalibrationCatalog.SameReceiptHash(sameReceipt)};
        ReplayCalibrationCatalog.ValidateSameCapture(sameReceipt,ControlledHost);Check("v10-complete-generated-same-acquisition-policy",true);
        string sameCatalog=Path.Combine(root,"same-c00-catalog");TrustedStorage.CreatePrivateDirectory(sameCatalog);
        string sameFile=Path.Combine(sameCatalog,ReplayCalibrationCatalog.SameCaptureFileName);
        void WriteSame(ReplaySameCaptureReceipt r){r=r with{ReceiptHash=ReplayCalibrationCatalog.SameReceiptHash(r)};File.WriteAllBytes(sameFile,JsonSerializer.SerializeToUtf8Bytes(r));}
        WriteSame(sameReceipt);
        foreach(int minute in new[]{1,5,30})Check("v10-serialized-loader-"+minute,ReplayCalibrationCatalog.LoadFrom(sameCatalog,ControlledHost,minute).Model?.ReplayMinutes==minute);
        foreach(var bad in new[]{sameReceipt with{C00=null},sameReceipt with{Manifest=null},sameReceipt with{Acceptances=[]},
            sameReceipt with{Acceptances=[SA(sc)]},sameReceipt with{Frozen=frozen}})
        {WriteSame(bad);Check("v10-missing-parent-manifest-reference-policy-rejected-"+cases.Count,ReplayCalibrationCatalog.LoadFrom(sameCatalog,ControlledHost).Model==null);}
        WriteSame(sameReceipt);
        File.WriteAllBytes(Path.Combine(sameCatalog,ReplayCalibrationCatalog.CoverageFileName),JsonSerializer.SerializeToUtf8Bytes(receipt));
        Check("v9-v10-conflicting-catalog-rejected",ReplayCalibrationCatalog.LoadFrom(sameCatalog,ControlledHost).Model==null);
        var report = new { result = "passed-controlled-offline-coverage", count = cases.Count, cases,
            seconds = watch.Elapsed.TotalSeconds, genuineConfirmation = false, modelActivated = false, captureStarted = false };
        File.WriteAllBytes(Path.Combine(root, "coverage-checks.json"), JsonSerializer.SerializeToUtf8Bytes(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Passed {cases.Count} controlled v9 coverage/serialized-loader/admission/withdrawal checks; no media/capture."); return 0;
    }
}

