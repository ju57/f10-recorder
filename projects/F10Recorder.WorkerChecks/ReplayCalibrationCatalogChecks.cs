using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace F10Recorder;

// Controlled nonmedia receipt fixtures. These checks do not calibrate a model.
internal static class ReplayCalibrationCatalogChecks
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
    static ReplayCalibrationProcessReceipt Process(string label) => new(Hash(label), true, true, 0, false, false, false);

    // Controlled metadata: fixtures do not need an adapter, driver or GPU.
    internal static readonly string ControlledHost = Hash("fixed nonmedia host identity; not empirical qualification");
    static ReplayCalibrationAvailability Load(string directory) => ReplayCalibrationCatalog.LoadFrom(directory, ControlledHost);
    static string Revision(string scope, string volume, long unit, long residual, long extra, string[] samples) =>
        ReplayCalibrationCatalog.ModelRevision(scope, volume, unit, residual, extra, samples, ControlledHost);
    static bool Applicable(ReplayReferenceModel model, string directory) =>
        ReplayCalibrationCatalog.IsApplicableToDestination(model, directory, ControlledHost);

    internal static ReplayCalibrationReceipt Fixture(string directory)
    {
        var volume = ReplayStorage.ObserveCapacity(directory);
        Require(volume.AllocationUnit == 4096, "This literal fixture requires a 4096-byte local NTFS unit.");
        string scope = Hash("controlled receipt fixture; no media calibration");
        var sources = new[] { "T01", "T02", "T03", "H02", "H01", "H03" }.Select((label, index) =>
            new ReplayCalibrationSourceReceipt(label, Hash("acquisition-" + label), Hash("sample-" + label),
                scope, volume.Volume, 4096, 1000, label == "T01" ? 2234 : 2000, 4096, label == "T01" ? 12288 : 8192,
                300, 300, true, Process(label + "-capture") with { EvidenceHash = ReplayReferenceModel.Hash(CalibrationCaptureEvidence.Closure(
                    new(100 + index, DateTime.UnixEpoch.AddSeconds(index + 1), Path.Combine(directory, "controlled-session-" + label)), true, true, 0, "")) },
                Process(label + "-source"), Process(label + "-export"), Process(label + "-decode"),
                Hash("selection-" + label), Hash("capture-arguments"), Hash("export-arguments"), Hash("pictures-" + label),
                true, true, true, true, true, 100,
                label.EndsWith('1') ? Enumerable.Repeat((label.StartsWith('T') ? 13579 : 24680) * 256, 3).ToArray()
                    : new[] { 1, 2, 3 }.Select(n => (label.StartsWith('T') ? 13579 : 24680) * 256 + n).ToArray(),
                [Hash(label + "-picture-1"), Hash(label + "-picture-2"), Hash(label + "-picture-3")], -20, 1920, 1080, 48000, 2)).ToArray();
        string[] training = sources.Take(3).Select(s => s.SampleHash).ToArray();
        var model = new ReplayReferenceModel(Revision(scope, volume.Volume, 4096, 1234, 8192, training),
            scope, volume.Volume, 4096, 1234, 8192, training);
        return ReplayCalibrationCatalog.CreateReceipt(model, sources, 100, Hash("observer fixture"), Hash("admission fixture"), ControlledHost);
    }
    static void Write(string directory, ReplayCalibrationReceipt receipt) => File.WriteAllBytes(
        Path.Combine(directory, ReplayCalibrationCatalog.FileName), JsonSerializer.SerializeToUtf8Bytes(receipt));
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
        Require(result.Model.FinalBytes(1000) == 16_779_450 && result.Model.PeakBytes(1000) == 33_566_720,
            "Supplied coefficients must reach the existing estimate arithmetic unchanged.");
        var cases = new List<string> { "missing-read-only", "complete-supplied-receipt", "actual-volume-NTFS", "literal-model-estimates" };
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
        Reject("observer-gap-501", r => r with { MaximumActiveGapMs = 501 });
        Reject("source-observer-gap-501", r => Source(r, 4, s => s with { MaximumActiveGapMs = 501 }));
        Reject("producer-not-absent", r => Source(r, 4, s => s with { Export = s.Export with { ProducerAbsent = false } }));
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
            CaptureVolume = fixture.Model.Volume
        }).ToArray();
        var headroom = new ReplayCaptureHeadroom(fixture.Model.Scope, fixture.Model.Volume, 4096,
            16_789_504, 16_801_792); // 12288/24576 observed training maxima plus16MiB, rounded4096.
        var growthModel = fixture.Model with { CaptureHeadroom = headroom };
        growthModel = growthModel with { Revision = ReplayCalibrationCatalog.ModelRevision(growthModel.Scope, growthModel.Volume,
            4096, 1234, 8192, growthModel.TrainingReceipts, ControlledHost, headroom) };
        fixture = ReplayCalibrationCatalog.CreateReceipt(growthModel, growthSources, 100, Hash("growth observer"), Hash("growth admission"), ControlledHost);
        Write(catalog, fixture);
        Require(Load(catalog).Model?.CaptureHeadroom == headroom, "Qualified supplied capture terms must survive catalog loading.");
        cases.Add("capture-headroom-supplied-positive");
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
        Require(demand.Kind == ReplayOutputEvidenceKind.CalibratedEstimate && demand.PeakAllocatedBytes == 33_566_720 && !demand.CoversOpenTail,
            "Loaded model must reach real Estimate seam with literal rounded estimate."); cases.Add("loaded-model-real-estimate");
        Require(admitted.Estimate(selection with { Closed = false }, admitted.Scope, inputs).Kind == ReplayOutputEvidenceKind.Unmeasured,
            "An open flag without an unfinished tail must not receive envelope evidence."); cases.Add("loaded-model-invalid-open-tail-unmeasured");
        var open = selection with { End = 300.5, Closed = false, Inputs = selectionInputs.Skip(1).ToArray() };
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




