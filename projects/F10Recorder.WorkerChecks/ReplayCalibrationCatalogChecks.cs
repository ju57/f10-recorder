using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace F10Recorder;

// Controlled nonmedia receipt fixtures. These checks do not calibrate a model.
internal static partial class ReplayCalibrationCatalogChecks
{
    internal static int AuditHistoricalReceipt(string root, string receiptPath, string expectedSha256)
    {
        Require(!Directory.Exists(root) && !File.Exists(root), "Receipt audit needs a fresh private fixture root.");
        byte[] bytes = File.ReadAllBytes(receiptPath);
        Require(bytes.Length is > 0 and <= ReplayCalibrationCatalog.MaximumBytes
            && Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)) == expectedSha256,
            "Historical qualification identity changed.");
        using var document = JsonDocument.Parse(bytes);
        string exporter = document.RootElement.GetProperty("ExporterIdentity").GetString();
        Require(exporter != ReplayReferenceModel.ExporterIdentity, "Historical receipt is not stale for this corrected binary.");
        string defaultFile = Path.Combine(TrustedStorage.DefaultRoot, "storage-calibration", ReplayCalibrationCatalog.FileName);
        string Before() => File.Exists(defaultFile) ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(defaultFile))) : "absent";
        string before = Before();
        TrustedStorage.CreatePrivateDirectory(root);
        string fixture = Path.Combine(root, "catalog"); TrustedStorage.CreatePrivateDirectory(fixture);
        File.WriteAllBytes(Path.Combine(fixture, ReplayCalibrationCatalog.FileName), bytes);
        var availability = ReplayCalibrationCatalog.LoadFrom(fixture);
        Require(availability.Model == null && !string.IsNullOrWhiteSpace(availability.Reason), "Changed exporter activated a historical calibration.");
        Require(Before() == before && Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(receiptPath))) == expectedSha256,
            "Receipt audit changed the installed catalog or original qualification.");
        File.WriteAllText(Path.Combine(root, "verification.json"), JsonSerializer.Serialize(new {
            result = "historical-receipt-rejected-as-unmeasured", receiptPath, receiptSha256 = expectedSha256,
            historicalExporter = exporter, correctedExporter = ReplayReferenceModel.ExporterIdentity,
            availability.Reason, defaultCatalogUnchanged = true, modelActivated = false,
            effects = "Read historical qualification; copied it into a new private fixture catalog solely for loader rejection. No default catalog write, worker, engine, capture, audio, GPU or UI."
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Corrected loader rejects historical qualification; default catalog unchanged."); return 0;
    }
    static void Require(bool value, string message) { if (!value) throw new IOException(message); }
    static string Hash(string value) => ReplayReferenceModel.Hash(value);
    static ReplayCalibrationIndexReceipt Index(int inputs,int hits,int owners)
    {
        int misses=inputs-hits,workers=Math.Min(4,misses);
        return new(ReplaySaveExport.BoundedPacketConstruction,inputs,workers,workers,misses,0,misses>0,misses>0,
            misses>0?30000:0,false,null,true,new(owners,hits,misses,true,misses>0,true));
    }
    internal static int AcquisitionChecks()
    {
        int checks=0;void Check(bool condition){Require(condition,"Catalog acquisition contract failed.");checks++;}
        var hit=Index(301,301,301);var mixed=Index(302,301,301);var fresh=Index(301,0,0);
        foreach(var valid in new[]{hit,mixed,fresh,Index(301,300,301),Index(301,0,301)})
            Check(ReplayCalibrationCatalog.IndexAcquisitionValid(valid,valid.AsynchronousInputs));
        foreach(int minutes in new[]{1,30})
        {
            int inputs=minutes*60+1;
            foreach(int hits in new[]{0,inputs/2,inputs})
                Check(ReplayCalibrationCatalog.IndexAcquisitionValid(Index(inputs,hits,inputs),inputs,minutes));
            Check(!ReplayCalibrationCatalog.IndexAcquisitionValid(Index(1801,900,1801),1801,1));
            Check(!ReplayCalibrationCatalog.IndexAcquisitionValid(Index(inputs,0,inputs),inputs,2));
        }
        foreach(var invalid in new[]{hit with{Acquisition=null},hit with{Workers=4},hit with{Joined=true},hit with{CancellableReads=true},
            hit with{ReadDeadlineMilliseconds=30000},hit with{FinishedSources=301},hit with{Acquisition=hit.Acquisition with{ReadersStarted=true}},
            mixed with{Acquisition=mixed.Acquisition with{ReadersJoined=false}},mixed with{Acquisition=mixed.Acquisition with{Complete=false}},
            mixed with{FinishedSources=302},mixed with{Workers=4},mixed with{Acquisition=mixed.Acquisition with{Misses=2}},
            mixed with{Acquisition=mixed.Acquisition with{PreparedOwners=300}},mixed with{Acquisition=mixed.Acquisition with{PreparedOwners=303}},
            fresh with{Acquisition=fresh.Acquisition with{Hits=-1}},fresh with{Joined=false},fresh with{CancellableReads=false},
            fresh with{ReadDeadlineMilliseconds=29999},fresh with{SourceHandlesClosed=false},fresh with{DeadlineExceeded=true},
            fresh with{DrainError="unknown"},fresh with{UnsupportedSources=1},fresh with{PeakActive=5}})
            Check(!ReplayCalibrationCatalog.IndexAcquisitionValid(invalid,invalid.AsynchronousInputs));
        Console.WriteLine($"Passed {checks} pure shipping catalog acquisition controls; no native/files/media action.");return 0;
    }
    static ReplayCalibrationProcessReceipt Process(string label) => new(Hash(label), true, true, 0, false, false, false,
        100 + (int)(Convert.ToUInt32(Hash(label)[..7], 16) % 1000000), DateTime.UnixEpoch.AddSeconds(1).Ticks + Convert.ToUInt32(Hash(label)[..7], 16));

    // Controlled metadata: fixtures do not need an adapter, driver or GPU.
    internal static readonly ReplayHostFacts ControlledHostFacts = ReplayHostFacts.Create(new {
        os = "controlled Windows", machine = "fixture", account = "S-1-5-21-controlled", architecture = "X64",
        adapters = new[] { new { Name = "fixture", Identity = @"PCI\fixture", Key = @"\Registry\Machine\fixture", version = "controlled-driver" } }
    });
    internal static readonly string ControlledHost = ControlledHostFacts.Identity;
    static ReplayCalibrationAvailability Load(string directory) => ReplayCalibrationCatalog.LoadFrom(directory, ControlledHost);
    static string Revision(string scope, string volume, long unit, long residual, long extra, string[] samples) =>
        ReplayCalibrationCatalog.ModelRevision(scope, volume, unit, residual, extra, samples, ControlledHost);
    static bool Applicable(ReplayReferenceModel model, string directory) =>
        ReplayCalibrationCatalog.IsApplicableToDestination(model, directory, ControlledHost);

    internal static ReplayCalibrationReceipt Fixture(string directory, int replayMinutes = 5)
    {
        var volume = ReplayStorage.ObserveCapacity(directory);
        Require(volume.AllocationUnit == 4096, "This literal fixture requires a 4096-byte local NTFS unit.");
        int seconds = ReplayReferenceModel.Seconds(replayMinutes), frames = seconds * 60;
        string scope = Hash("controlled receipt fixture; no media calibration; minutes=" + replayMinutes);
        var sources = new[] { "T01", "T02", "T03", "H02", "H01", "H03" }.Select((label, index) =>
            new ReplayCalibrationSourceReceipt(label, Hash("acquisition-" + label), Hash("sample-" + label),
                scope, volume.Volume, 4096, 1000, label == "T01" ? 2234 : 2000, 4096, label == "T01" ? 12288 : 8192,
                seconds, seconds, true, Process(label + "-capture") with { EvidenceHash = ReplayReferenceModel.Hash(CalibrationCaptureEvidence.Closure(
                    new(100 + index, DateTime.UnixEpoch.AddSeconds(index + 1), Path.Combine(directory, "controlled-session-" + label)), true, true, 0, "")) },
                Process(label + "-source"), Process(label + "-export"), Process(label + "-decode"),
                Hash("selection-" + label), Hash("capture-arguments"), Hash("export-arguments"), Hash("pictures-" + label),
                true, true, true, true, true, 100,
                label.EndsWith('1') ? Enumerable.Repeat((label.StartsWith('T') ? 13579 : 24680) * 256, 3).ToArray()
                    : new[] { 1, 2, 3 }.Select(n => (label.StartsWith('T') ? 13579 : 24680) * 256 + n).ToArray(),
                [Hash(label + "-picture-1"), Hash(label + "-picture-2"), Hash(label + "-picture-3")], -20, 1920, 1080, 48000, 2,
                RuntimeValidation: Process(label + "-runtime-validation"), RuntimeValidationArgumentsHash: Hash("runtime-validator-args"), OutputStart: 0, OutputEnd: seconds,
                CombinedPeakAllocation: label == "T01" ? 24576 : 20480, WorkPeakAllocation: 12288, AssemblyPeakAllocation: 4096,
                BoundaryPeakAllocations: [4096, 4096], ExportProcesses: [Process(label + "-export")],
                NativeExport: new(Hash(label + "-native-fallback"), ReplaySaveExport.PolicyIdentity, true, true, false, true))).ToArray();
        var legacyOutputs = sources.Select((s, n) =>
        {
            var identity = new ReplayFileExtent(s.Volume, "controlled-legacy-" + n, s.FinalAllocation, s.FinalBytes, s.AllocationUnit);
            var child = Process(s.Label + "-legacy-export");
            return s with { SampleHash = Hash(s.Label + "-legacy-sample"), Export = child, ExportProcesses = [child],
                Decode = Process(s.Label + "-legacy-decode"), RuntimeValidation = Process(s.Label + "-legacy-normal"),
                NormalValidation = new("FullDecode", frames, 10, frames, ExportRecipe: ReplayExactExport.Recipe,
                    CheckedOutput: identity, PublicationIdentityBound: true), QualificationOutput = identity, RequestSelectionHash = Hash(s.Label + "-legacy-request-selection") };
        }).ToArray();
        sources = sources.Select((s, n) =>
        {
            var identity = new ReplayFileExtent(s.Volume, "controlled-packet-" + n, s.FinalAllocation, s.FinalBytes, s.AllocationUnit);
            return s with { AssemblyPeakAllocation = 0, RuntimeValidation = null, RuntimeValidationArgumentsHash = null,
                NativeExport = new(Hash(s.Label + "-native-packet"), ReplaySaveExport.PolicyIdentity, true, true, true, true,
                    s.Label.StartsWith("H", StringComparison.Ordinal), s.Label.StartsWith("H", StringComparison.Ordinal),
                    s.Label.StartsWith("H", StringComparison.Ordinal) ? Hash(s.Label + "-prepared-input") : null,
                    s.Label.StartsWith("H", StringComparison.Ordinal) ? 4096 : 0),
                NormalValidation = new(ReplayOutputIntegrity.Kind, 0, 1, frames, (int)Math.Ceiling(seconds * 48000d / 1024) + 2, ReplaySaveExport.PacketRecipe,
                    Hash(s.Label + "-frozen-pre-mux-proof"), identity, true), QualificationOutput = identity,
                RequestSelectionHash = Hash(s.Label + "-packet-request-selection"), ManagedExport = new(Hash(s.Label + "-managed"), 55, DateTime.UnixEpoch.AddSeconds(1).Ticks, true, true, "Completed", identity,
                    new(ReplaySaveExport.BoundedPacketConstruction, s.SegmentCount, 4, 4, s.SegmentCount, 0,
                        true, true, 30000, false, null, true,new(0,0,s.SegmentCount,true,true,true))) };
        }).ToArray();
        string[] training = Enumerable.Range(0, 3).Select(n => ReplayCalibrationCatalog.PairHash(sources[n], legacyOutputs[n])).ToArray();
        var model = new ReplayReferenceModel(ReplayCalibrationCatalog.ModelRevision(scope, volume.Volume, 4096, 1234, 8192, training,
            ControlledHost, replayMinutes: replayMinutes), scope, volume.Volume, 4096, 1234, 8192, training, ReplayMinutes: replayMinutes);
        return ReplayCalibrationCatalog.CreateReceipt(model, sources, 100, Hash("observer fixture"), Hash("admission fixture"), ControlledHost, legacyOutputs, ControlledHostFacts);
    }
    static void Write(string directory, ReplayCalibrationReceipt receipt) => File.WriteAllBytes(
        Path.Combine(directory, ReplayCalibrationCatalog.FileNameFor(receipt.Model.ReplayMinutes)), JsonSerializer.SerializeToUtf8Bytes(receipt));
    internal static int Run(string root)
    {
        Directory.CreateDirectory(root);
        string missing = Path.Combine(root, "absent");
        var result = Load(missing);
        Require(result.Model == null && !string.IsNullOrWhiteSpace(result.Reason), "Missing qualification stays unmeasured with a reason.");
        Require(!Directory.Exists(missing), "Read-only loading must not create private storage.");
        string catalog = Path.Combine(root, "private");
        TrustedStorage.CreatePrivateDirectory(catalog);
        var fixture = Fixture(catalog);
        Write(catalog, fixture);
        result = Load(catalog);
        Require(result.Model == fixture.Model || result.Model?.Revision == fixture.Model.Revision, "Complete supplied evidence must load the bound model.");
        Require(Applicable(result.Model, catalog), "The actual resolved NTFS target must qualify.");
        Require(result.Model.FinalBytes(1000) == 16_779_450 && result.Model.PeakBytes(1000) == 201_342_976,
            "Supplied final-output coefficients and the complete rounded scratch envelope must reach estimate arithmetic.");
        var cases = new List<string> { "missing-read-only", "complete-supplied-receipt", "actual-volume-NTFS", "literal-model-estimates" };
        string privateFile = Path.Combine(catalog, ReplayCalibrationCatalog.PrivateFiveMinuteFile);
        File.WriteAllText(privateFile, "{}");
        try
        {
            Require(Load(catalog).Model == null, "A conflicting private receipt must not fall back to a direct five-minute measurement.");
            Require(ReplayCalibrationCatalog.EvidenceFileName(catalog, 5) == ReplayCalibrationCatalog.PrivateFiveMinuteFile
                && ReplayCalibrationCatalog.EvidenceFileName(catalog, 1) == ReplayCalibrationCatalog.FileNameFor(1),
                "Private five-minute evidence must stay bound to its duration.");
            cases.Add("private-five-minute-conflict-and-duration");
        }
        finally { File.Delete(privateFile); }
        foreach (int minutes in new[] { 1, 30 })
        {
            var durationFixture = Fixture(catalog, minutes);
            Write(catalog, durationFixture);
            var loaded = ReplayCalibrationCatalog.LoadFrom(catalog, ControlledHost, minutes);
            Require(loaded.Model?.ReplayMinutes == minutes && Applicable(loaded.Model, catalog), "Duration-specific receipt must load its exact cell.");
            string durationFile = Path.Combine(catalog, ReplayCalibrationCatalog.FileNameFor(minutes));
            byte[] original = File.ReadAllBytes(durationFile);
            File.WriteAllBytes(durationFile, JsonSerializer.SerializeToUtf8Bytes(fixture));
            Require(ReplayCalibrationCatalog.LoadFrom(catalog, ControlledHost, minutes).Model == null,
                "Five-minute receipt in another duration's filename must not transfer qualification.");
            File.WriteAllBytes(durationFile, original);
            Require(Load(catalog).Model?.ReplayMinutes == 5, "Additional catalogs must not change the five-minute selection.");
            // Self-consistent revision/hash cannot turn five-minute observations into another duration.
            var transplanted = fixture.Model with { ReplayMinutes = minutes };
            transplanted = transplanted with { Revision = ReplayCalibrationCatalog.ModelRevision(transplanted.Scope, transplanted.Volume,
                transplanted.AllocationUnit, transplanted.FinalResidual, transplanted.SampledFinalizationExtra, transplanted.TrainingReceipts,
                ControlledHost, replayMinutes: minutes) };
            bool rejected = false;
            try { ReplayCalibrationCatalog.CreateReceipt(transplanted, fixture.Sources, 100, fixture.ObservationHash, fixture.AdmissionHash, ControlledHost, fixture.LegacyOutputs, ControlledHostFacts); }
            catch (IOException) { rejected = true; }
            Require(rejected, "Duration-changed model must reject different-duration source receipts.");
            cases.Add($"duration-{minutes}-isolated-catalog-and-no-observation-transfer");
        }
        void Reject(string name, Func<ReplayCalibrationReceipt, ReplayCalibrationReceipt> change)
        {
            var changed = change(fixture);
            changed = changed with { ReceiptHash = ReplayReferenceModel.Hash(changed with { ReceiptHash = null }) };
            Write(catalog, changed);
            var rejected = Load(catalog);
            Require(rejected.Model == null && !string.IsNullOrWhiteSpace(rejected.Reason) && rejected.Reason.Length <= 160, name + " must stay bounded and unmeasured.");
            cases.Add(name);
        }
        ReplayCalibrationReceipt Source(ReplayCalibrationReceipt receipt, int index, Func<ReplayCalibrationSourceReceipt, ReplayCalibrationSourceReceipt> change)
        {
            var sources = receipt.Sources.ToArray(); sources[index] = change(sources[index]);
            return receipt with { Sources = sources };
        }
        ReplayCalibrationReceipt Legacy(ReplayCalibrationReceipt receipt, int index, Func<ReplayCalibrationSourceReceipt, ReplayCalibrationSourceReceipt> change)
        {
            var outputs = receipt.LegacyOutputs.ToArray(); outputs[index] = change(outputs[index]);
            return receipt with { LegacyOutputs = outputs };
        }
        Reject("v2-receipts-not-composite", r => r with { Revision = "reference-qualification-v2" });
        Reject("packet-only-cannot-qualify-fallback", r => r with { LegacyOutputs = null });
        Reject("missing-heldout-legacy-pair", r => r with { LegacyOutputs = r.LegacyOutputs[..5] });
        Reject("legacy-different-protected-selection", r => Legacy(r, 4, s => s with { SelectionHash = Hash("foreign-selection") }));
        Reject("paired-requests-cannot-reuse-snapshot-identity", r => Legacy(r, 4, s => s with { RequestSelectionHash = r.Sources[4].RequestSelectionHash }));
        Reject("legacy-is-not-an-extra-independent-capture", r => Legacy(r, 4, s => s with { AcquisitionIdentity = Hash("foreign-acquisition") }));
        Reject("legacy-requires-normal-full-decode", r => Legacy(r, 4, s => s with { RuntimeValidation = null }));
        Reject("legacy-cannot-reuse-qualification-decoder", r => Legacy(r, 4, s => s with { RuntimeValidation = s.Decode }));
        Reject("packet-cannot-claim-normal-decoded-frames", r => Source(r, 4, s => s with { NormalValidation = s.NormalValidation with { Frames = 18000 } }));
        Reject("packet-missing-frozen-source-proof", r => Source(r, 4, s => s with { NormalValidation = s.NormalValidation with { SourceProofHash = null } }));
        Reject("packet-missing-managed-producer", r => Source(r, 4, s => s with { ManagedExport = null }));
        Reject("packet-unclosed-managed-producer", r => Source(r, 4, s => s with { ManagedExport = s.ManagedExport with { Closed = false } }));
        Reject("packet-unjoined-readers", r => Source(r, 4, s => s with { ManagedExport = s.ManagedExport with { Index = s.ManagedExport.Index with { Joined = false } } }));
        Reject("packet-wrong-policy", r => Source(r, 4, s => s with { ManagedExport = s.ManagedExport with { Index = s.ManagedExport.Index with { Construction = "PacketCopyBatch" } } }));
        Reject("packet-serial-construction", r => Source(r, 4, s => s with { ManagedExport = s.ManagedExport with { Index = s.ManagedExport.Index with { Construction = nameof(ReplaySaveExport.Construction.PacketCopyBatchCancellable) } } }));
        Reject("native-witness-missing", r => Source(r, 4, s => s with { NativeExport = null }));
        Reject("native-witness-not-owned", r => Source(r, 4, s => s with { NativeExport = s.NativeExport with { OwnedRequest = false } }));
        Reject("native-witness-ineligible", r => Source(r, 4, s => s with { NativeExport = s.NativeExport with { EligibleAtExport = false } }));
        Reject("native-witness-wrong-policy", r => Source(r, 4, s => s with { NativeExport = s.NativeExport with { PolicyIdentity = "old-serial-policy" } }));
        Reject("native-witness-not-pipeline", r => Source(r, 4, s => s with { NativeExport = s.NativeExport with { PipelineSelected = false } }));
        Reject("native-witness-not-closed", r => Source(r, 4, s => s with { NativeExport = s.NativeExport with { ScopeClosed = false } }));
        Reject("heldout-preparation-not-exercised", r => Source(r, 4, s => s with { NativeExport = s.NativeExport with { PreparationSelected = false } }));
        Reject("heldout-preparation-not-joined", r => Source(r, 4, s => s with { NativeExport = s.NativeExport with { PreparationJoined = false } }));
        Reject("heldout-preparation-no-input-hash", r => Source(r, 4, s => s with { NativeExport = s.NativeExport with { PreparedInputHash = null } }));
        Reject("heldout-preparation-input-too-large", r => Source(r, 4, s => s with { NativeExport = s.NativeExport with { PreparedInputBytes = 8388609 } }));
        Reject("native-witness-reused", r => Source(r, 4, s => s with { NativeExport = s.NativeExport with { EvidenceHash = r.Sources[3].NativeExport.EvidenceHash } }));
        Reject("legacy-native-witness-missing", r => Legacy(r, 4, s => s with { NativeExport = null }));
        Reject("legacy-native-witness-pipeline", r => Legacy(r, 4, s => s with { NativeExport = s.NativeExport with { PipelineSelected = true } }));
        Reject("packet-synchronous-inputs", r => Source(r, 4, s => s with { ManagedExport = s.ManagedExport with { Index = s.ManagedExport.Index with { AsynchronousInputs = 0 } } }));
        Reject("packet-reader-deadline-exceeded", r => Source(r, 4, s => s with { ManagedExport = s.ManagedExport with { Index = s.ManagedExport.Index with { DeadlineExceeded = true } } }));
        Reject("packet-reader-finished-count-missing", r => Source(r, 4, s => s with { ManagedExport = s.ManagedExport with { Index = s.ManagedExport.Index with { FinishedSources = 299 } } }));
        Reject("old-four-reader-schema-rejected", r => r with{Revision="reference-reserved-staging-qualification-v6"});
        Reject("packet-acquisition-missing", r => Source(r,4,s=>s with{ManagedExport=s.ManagedExport with{Index=s.ManagedExport.Index with{Acquisition=null}}}));
        Reject("packet-acquisition-not-complete", r => Source(r,4,s=>s with{ManagedExport=s.ManagedExport with{Index=s.ManagedExport.Index with{Acquisition=s.ManagedExport.Index.Acquisition with{Complete=false}}}}));
        Reject("packet-acquisition-counts-incomplete", r => Source(r,4,s=>s with{ManagedExport=s.ManagedExport with{Index=s.ManagedExport.Index with{Acquisition=s.ManagedExport.Index.Acquisition with{Misses=299}}}}));
        Reject("packet-source-closure-unconfirmed", r => Source(r, 4, s => s with { ManagedExport = s.ManagedExport with { Index = s.ManagedExport.Index with { SourceHandlesClosed = false } } }));
        Reject("packet-picture-count-is-exact", r => Source(r, 4, s => s with { NormalValidation = s.NormalValidation with { CodedPictures = 17999 } }));
        Reject("packet-unpublished-checked-object", r => Source(r, 4, s => s with { NormalValidation = s.NormalValidation with { PublicationIdentityBound = false } }));
        Reject("qualification-checked-a-different-object", r => Legacy(r, 4, s => s with { QualificationOutput = s.QualificationOutput with { Identity = "foreign-file" } }));
        Reject("heldout-legacy-union-bound-exceedance", r => Legacy(r, 4, s => s with { CombinedPeakAllocation = r.Model.PeakBytes(s.InputBytes) + 4096 }));
        Reject("normal-or-qualification-process-reused-between-outputs", r => Source(r, 4, s => s with { Decode = r.Sources[3].Decode }));
        foreach(var hits in new[]{300,299,0})
        {
            var prepared=Source(fixture,4,s=>s with{ManagedExport=s.ManagedExport with{Index=Index(s.SegmentCount,hits,s.SegmentCount)}});
            prepared=ReplayCalibrationCatalog.CreateReceipt(prepared.Model,prepared.Sources,100,prepared.ObservationHash,prepared.AdmissionHash,ControlledHost,prepared.LegacyOutputs,ControlledHostFacts);
            Write(catalog,prepared);Require(Load(catalog).Model!=null,"Genuine prepared/mixed/fresh acquisition must load without invented parser work.");
            cases.Add("prepared-acquisition-positive-"+hits);
            if(hits==300)
                foreach(string absent in new[]{"Misses","ReadersStarted"})
                {
                    var raw=System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(prepared));
                    raw["Sources"][4]["ManagedExport"]["Index"]["Acquisition"].AsObject().Remove(absent);
                    File.WriteAllText(Path.Combine(catalog,ReplayCalibrationCatalog.FileName),raw.ToJsonString());
                    Require(Load(catalog).Model==null,"Omitted zero-valued acquisition facts cannot inherit truthful defaults.");
                    cases.Add("required-acquisition-field-"+absent);
                }
        }
        var childless = Source(fixture, 4, s => s with { Export = null, ExportProcesses = [], BoundaryPeakAllocations = [0, 0] });
        childless = ReplayCalibrationCatalog.CreateReceipt(childless.Model, childless.Sources, 100, childless.ObservationHash,
            childless.AdmissionHash, ControlledHost, childless.LegacyOutputs, ControlledHostFacts);
        Write(catalog, childless); Require(Load(catalog).Model != null, "Actual zero-child managed packet exports must remain representable.");
        cases.Add("zero-child-packet-output-positive");
        var fallbackAfterHead = Legacy(fixture, 4, s => s with { ExportProcesses = [Process("heldout-head-before-fallback"), s.Export] });
        fallbackAfterHead = ReplayCalibrationCatalog.CreateReceipt(fallbackAfterHead.Model, fallbackAfterHead.Sources, 100,
            fallbackAfterHead.ObservationHash, fallbackAfterHead.AdmissionHash, ControlledHost, fallbackAfterHead.LegacyOutputs, ControlledHostFacts);
        Write(catalog, fallbackAfterHead); Require(Load(catalog).Model != null,
            "Retained successful head plus fallback child must remain represented together.");
        cases.Add("head-then-fallback-two-children-positive");
        var legacyDominates = Legacy(fixture, 0, s => s with {
            FinalBytes = 3000, NormalValidation = s.NormalValidation with { CheckedOutput = s.NormalValidation.CheckedOutput with { Length = 3000 } },
            QualificationOutput = s.QualificationOutput with { Length = 3000 } });
        bool packetOnlyRejected = false;
        try { ReplayCalibrationCatalog.CreateReceipt(legacyDominates.Model, legacyDominates.Sources, 100,
            legacyDominates.ObservationHash, legacyDominates.AdmissionHash, ControlledHost, legacyDominates.LegacyOutputs, ControlledHostFacts); }
        catch (IOException) { packetOnlyRejected = true; }
        Require(packetOnlyRejected, "A packet-only residual silently covered an untrained larger fallback.");
        var unionModel = legacyDominates.Model with { FinalResidual = 2000 };
        unionModel = unionModel with { Revision = Revision(unionModel.Scope, unionModel.Volume, unionModel.AllocationUnit,
            2000, unionModel.SampledFinalizationExtra, unionModel.TrainingReceipts) };
        var union = ReplayCalibrationCatalog.CreateReceipt(unionModel, legacyDominates.Sources, 100,
            legacyDominates.ObservationHash, legacyDominates.AdmissionHash, ControlledHost, legacyDominates.LegacyOutputs, ControlledHostFacts);
        Write(catalog, union); Require(Load(catalog).Model?.FinalResidual == 2000,
            "Training union failed to retain the larger legacy coefficient.");
        cases.Add("legacy-dominant-union-training-positive-and-packet-only-rejected");
        Reject("stale-exporter", r => r with { ExporterIdentity = Hash("changed exporter") });
        Reject("stale-host-driver", r => r with { HostIdentity = Hash("changed host or display driver") });
        Reject("unknown-recipe", r => r with { RecipeHash = Hash("different recipe") });
        Reject("unknown-receipt-revision", r => r with { Revision = "reference-qualification-v99" });
        Reject("missing-observation", r => r with { ObservationHash = null });
        Reject("missing-admission", r => r with { AdmissionHash = null });
        Reject("incomplete-six-sources", r => r with { Sources = r.Sources.Take(5).ToArray() });
        Reject("independent-acquisition-required", r => Source(r, 4, s => s with { AcquisitionIdentity = r.Sources[0].AcquisitionIdentity }));
        Reject("legacy-success-only-capture-receipts-are-not-independent", r => r with { Sources = r.Sources.Select(s => s with {
            Capture = s.Capture with { EvidenceHash = ReplayReferenceModel.Hash(new { exited = true, producersAbsent = true, exit = 0, diagnostics = "" }) } }).ToArray() });
        Reject("original-fixed-order", r => r with { Sources = [r.Sources[0], r.Sources[1], r.Sources[2], r.Sources[4], r.Sources[3], r.Sources[5]] });
        Reject("scope-changed", r => Source(r, 4, s => s with { Scope = Hash("different endpoint") }));
        Reject("unit-changed", r => Source(r, 4, s => s with { AllocationUnit = 8192 }));
        Reject("observer-gap-beyond-explicit-horizon", r => r with { MaximumActiveGapMs = ReplayCapacityPolicy.CoverageMilliseconds + 1 });
        Reject("source-observer-gap-beyond-explicit-horizon", r => Source(r, 4, s => s with { MaximumActiveGapMs = ReplayCapacityPolicy.CoverageMilliseconds + 1 }));
        Reject("producer-not-absent", r => Source(r, 4, s => s with { Export = s.Export with { ProducerAbsent = false } }));
        Reject("missing-combined-peak", r => Source(r, 4, s => s with { CombinedPeakAllocation = null }));
        Reject("missing-work-peak", r => Source(r, 4, s => s with { WorkPeakAllocation = null }));
        Reject("missing-assembly-observation", r => Source(r, 4, s => s with { AssemblyPeakAllocation = null }));
        Reject("combined-below-work", r => Source(r, 4, s => s with { CombinedPeakAllocation = 8192 }));
        Reject("combined-model-exceedance", r => Source(r, 4, s => s with { CombinedPeakAllocation = 201347072 }));
        Reject("assembly-bucket-exceedance", r => Source(r, 4, s => s with { AssemblyPeakAllocation = 67117056, WorkPeakAllocation = 67117056, CombinedPeakAllocation = 67125248 }));
        Reject("boundary-bucket-exceedance", r => Source(r, 4, s => s with { BoundaryPeakAllocations = [50335744, 0], WorkPeakAllocation = 50335744, CombinedPeakAllocation = 50343936 }));
        Reject("missing-export-children", r => Source(r, 4, s => s with { ExportProcesses = null }));
        Reject("earlier-encoder-cleanup-failed", r => Source(r, 4, s => s with { ExportProcesses = [Process("failed-boundary") with { ProducerAbsent = false, ProcessId = 124 }, s.Export] }));
        Reject("earlier-encoder-identity-missing", r => Source(r, 4, s => s with { ExportProcesses = [Process("missing-identity") with { ProcessId = null }, s.Export] }));
        Reject("duplicate-export-child-identities", r => Source(r, 4, s => s with { ExportProcesses = [s.Export, s.Export] }));
        Reject("forced-capture-exit", r => Source(r, 4, s => s with { Capture = s.Capture with { ForcedTermination = true } }));
        Reject("missing-capture-receipt", r => Source(r, 4, s => s with { Capture = null }));
        Reject("full-decode-missing", r => Source(r, 4, s => s with { AllTracksDecoded = false }));
        Reject("source-lineage-wrong-seed", r => Source(r, 4, s => s with { SourcePictureIds = [1, 1, 1] }));
        Reject("source-motion-unverified", r => Source(r, 3, s => s with { SourcePictureIds = [6318081, 6318081, 6318081] }));
        Reject("silent-audio", r => Source(r, 4, s => s with { NonSilentAudio = false }));
        Reject("changed-audio-format", r => Source(r, 4, s => s with { AudioSampleRate = 44100 }));
        Reject("open-source-tail", r => Source(r, 4, s => s with { Closed = false }));
        Reject("heldout-final-exceedance-no-refit", r => Source(r, 4, s => s with { FinalBytes = 16_779_451, FinalAllocation = 16_781_312, PeakAllocation = 16_781_312 }));
        Reject("heldout-allocation-exceedance", r => Source(r, 4, s => s with { FinalAllocation = 16_785_408, PeakAllocation = 16_785_408 }));
        Reject("heldout-peak-exceedance", r => Source(r, 4, s => s with { PeakAllocation = 33_570_816 }));
        Reject("refitted-training-residual", r => r with { Model = r.Model with
        {
            FinalResidual = 1235,
            Revision = Revision(r.Model.Scope, r.Model.Volume, 4096, 1235, 8192, r.Model.TrainingReceipts)
        } });
        Reject("refitted-training-peak", r => r with { Model = r.Model with
        {
            SampledFinalizationExtra = 12288,
            Revision = Revision(r.Model.Scope, r.Model.Volume, 4096, 1234, 12288, r.Model.TrainingReceipts)
        } });
        // Literal capture-growth controls are supplied metadata, never empirical calibration.
        var outputOnly = fixture;
        var growthSources = fixture.Sources.Select((s, n) => s with {
            CapturePollGrowthBytes = n < 3 ? 4096L * (n + 1) : 8192,
            CaptureStopGrowthBytes = n < 3 ? 8192L * (n + 1) : 16384,
            CaptureVolume = fixture.Model.Volume, CaptureObservationMilliseconds = ReplayCapacityPolicy.CoverageMilliseconds, MaximumCapacityGapMs = 100
        }).ToArray();
        var headroom = new ReplayCaptureHeadroom(fixture.Model.Scope, fixture.Model.Volume, 4096,
            16_789_504, 16_801_792, ReplayCapacityPolicy.CoverageMilliseconds); // Controlled2000ms maxima, not transferred500ms evidence.
        var growthModel = fixture.Model with { CaptureHeadroom = headroom };
        growthModel = growthModel with { Revision = ReplayCalibrationCatalog.ModelRevision(growthModel.Scope, growthModel.Volume,
            4096, 1234, 8192, growthModel.TrainingReceipts, ControlledHost, headroom) };
        var growthLegacy = fixture.LegacyOutputs.Select((s, n) => s with {
            CapturePollGrowthBytes = growthSources[n].CapturePollGrowthBytes, CaptureStopGrowthBytes = growthSources[n].CaptureStopGrowthBytes,
            CaptureVolume = growthSources[n].CaptureVolume, CaptureObservationMilliseconds = growthSources[n].CaptureObservationMilliseconds,
            MaximumCapacityGapMs = growthSources[n].MaximumCapacityGapMs }).ToArray();
        fixture = ReplayCalibrationCatalog.CreateReceipt(growthModel, growthSources, 100, Hash("growth observer"), Hash("growth admission"), ControlledHost, growthLegacy, ControlledHostFacts);
        Write(catalog, fixture);
        Require(Load(catalog).Model?.CaptureHeadroom == headroom, "Qualified supplied capture terms must survive catalog loading.");
        cases.Add("capture-headroom-supplied-positive");
        Reject("capture-old-horizon-source", r => Source(r, 4, s => s with { CaptureObservationMilliseconds = 500 }));
        Reject("capture-capacity-coverage-missing", r => Source(r, 4, s => s with { MaximumCapacityGapMs = 0 }));
        Reject("capture-capacity-coverage-outage", r => Source(r, 4, s => s with { MaximumCapacityGapMs = ReplayCapacityPolicy.CoverageMilliseconds + 1 }));
        Reject("capture-term-missing", r => Source(r, 4, s => s with { CapturePollGrowthBytes = null }));
        Reject("capture-term-negative", r => Source(r, 4, s => s with { CaptureStopGrowthBytes = -1 }));
        Reject("capture-volume-mismatch", r => Source(r, 4, s => s with { CaptureVolume = @"\\?\Volume{00000000-0000-0000-0000-000000000000}\" }));
        Reject("heldout-capture-poll-exceedance-no-refit", r => Source(r, 4, s => s with { CapturePollGrowthBytes = 16_789_505 }));
        Reject("heldout-capture-drain-exceedance-no-refit", r => Source(r, 4, s => s with { CaptureStopGrowthBytes = 16_801_793 }));
        Reject("capture-headroom-revision-bound", r => r with { Model = r.Model with { CaptureHeadroom = headroom with { PollGrowthBytes = 16_793_600 } } });
        var changedHeadroom = headroom with { PollGrowthBytes = 16_793_600 };
        Reject("capture-headroom-cannot-refit-training", r => r with { Model = r.Model with {
            CaptureHeadroom = changedHeadroom,
            Revision = ReplayCalibrationCatalog.ModelRevision(r.Model.Scope, r.Model.Volume, 4096, 1234, 8192,
                r.Model.TrainingReceipts, ControlledHost, changedHeadroom)
        } });
        fixture = outputOnly; // Remaining legacy controls verify output-only models remain explicitly capture-unmeasured.
        string file = Path.Combine(catalog, ReplayCalibrationCatalog.FileName);
        void RejectJson(string name, string text)
        {
            File.WriteAllText(file, text);
            var rejected = Load(catalog);
            Require(rejected.Model == null && !string.IsNullOrWhiteSpace(rejected.Reason), name + " must be unmeasured.");
            cases.Add(name);
        }
        string valid = JsonSerializer.Serialize(fixture);
        RejectJson("duplicate-property", "{\"Revision\":\"anything\"," + valid[1..]);
        RejectJson("unknown-property", "{\"Unexpected\":true," + valid[1..]);
        RejectJson("nonfinite-json-number", valid.Replace("\"MaximumActiveGapMs\":100", "\"MaximumActiveGapMs\":1e999"));
        RejectJson("missing-required-constructor-fields", "{}");
        RejectJson("oversized-json", new string(' ', ReplayCalibrationCatalog.MaximumBytes + 1));
        Write(catalog, fixture with { ReceiptHash = Hash("tampered receipt") });
        Require(Load(catalog).Model == null, "Wrong receipt hash must reject."); cases.Add("receipt-hash-tamper");
        Write(catalog, fixture);
        using (var writer = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            Require(Load(catalog).Model == null, "Active writer must prevent read lease.");
        cases.Add("active-writer-blocked");
        var originalAcl = new FileInfo(file).GetAccessControl();
        var changedAcl = new FileInfo(file).GetAccessControl();
        changedAcl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Write, AccessControlType.Allow));
        try
        {
            new FileInfo(file).SetAccessControl(changedAcl);
            Require(Load(catalog).Model == null, "Another-account writable receipt must reject.");
        }
        finally { var restoredAcl = new FileSecurity(); restoredAcl.SetSecurityDescriptorBinaryForm(originalAcl.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access); new FileInfo(file).SetAccessControl(restoredAcl); }
        cases.Add("other-account-writes-rejected");
        Write(catalog, fixture);
        var admitted = Load(catalog).Model;
        Require(admitted != null, "Restored private ACL must allow model loading again.");
        string inputs = Path.Combine(catalog, "inputs"); Directory.CreateDirectory(inputs);
        var selectionInputs = new List<ReplayOutputInput>();
        for (int n = 0; n < 300; n++)
        {
            string path = Path.Combine(inputs, n.ToString("D3") + ".ts"); File.WriteAllBytes(path, [0]);
            selectionInputs.Add(new(path, n, n + 1, ReplayStorage.Observe(path)));
        }
        var selection = new ReplayOutputSelection(Hash("literal closed selection"), ReplayReferenceModel.ExporterIdentity, catalog, 300, true, selectionInputs.AsReadOnly());
        var demand = admitted.Estimate(selection, admitted.Scope, inputs);
        Require(demand.Kind == ReplayOutputEvidenceKind.CalibratedEstimate && demand.PeakAllocatedBytes == 201_342_976 && !demand.CoversOpenTail,
            "Loaded model must reach real Estimate seam with literal rounded estimate."); cases.Add("loaded-model-real-estimate");
        Require(admitted.Estimate(selection with { Closed = false }, admitted.Scope, inputs).Kind == ReplayOutputEvidenceKind.Unmeasured,
            "An open flag without an unfinished tail must not receive envelope evidence."); cases.Add("loaded-model-invalid-open-tail-unmeasured");
        var open = selection with { Start = .5, End = 300.5, Closed = false, Inputs = selectionInputs.ToArray() };
        var openDemand = admitted.Estimate(open, admitted.Scope, inputs);
        Require(openDemand.CoversOpenTail && openDemand.Kind == ReplayOutputEvidenceKind.CalibratedEstimate
            && openDemand.PeakAllocatedBytes == admitted.PeakBytes(1_000_000_000)
            && openDemand.SnapshotPeakAllocatedBytes == ReplayReferenceModel.SnapshotEnvelope(admitted.AllocationUnit),
            "Unfinished-tail admission must reserve the entire calibrated input domain and copy/concat envelope.");
        cases.Add("loaded-model-open-tail-full-envelope");
        Require(admitted.Estimate(open with { End = 302 }, admitted.Scope, inputs).Kind == ReplayOutputEvidenceKind.Unmeasured,
            "A missing multi-second tail must remain unmeasured."); cases.Add("loaded-model-stale-tail-rejected");
        Require(admitted.Estimate(selection, Hash("new source scope"), inputs).Kind == ReplayOutputEvidenceKind.Unmeasured,
            "Actual source scope change must invalidate loaded model."); cases.Add("loaded-model-scope-change-unmeasured");
        Require(!Applicable(admitted with { Volume = @"\\?\Volume{00000000-0000-0000-0000-000000000000}\" }, catalog),
            "Spare space on another volume never qualifies a model."); cases.Add("actual-volume-mismatch");
        File.Delete(file);
        Require(Load(catalog).Model == null, "Withdrawal must be observed on subsequent load."); cases.Add("withdrawal-unmeasured");
        long fixtureBytes = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Sum(p => ReplayStorage.Observe(p).AllocatedBytes);
        Require(fixtureBytes <= 4 * 1024 * 1024, "Controlled fixtures must remain within 4 MiB allocated.");
        File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new { result = "passed", fixtureOnly = true,
            empiricalCalibration = false, fixtureBytes, cases, limits = "No media, capture, engine, audio, GPU workload, arbitrary MP4 proof or native acceptance." }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Catalog controlled fixture checks passed: " + cases.Count);
        return 0;
    }
}




