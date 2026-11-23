using System.Globalization;
using System.Text.Json;

namespace F10Recorder;

internal static partial class ReplayCalibrationRun
{
    const string DurationTrainingModelHash = "A13D8E98B9AF56D90DD849D7DB6ADDDCD3CE1D1B8C4AF557FA29A5D3334C1621";

    // This boundary imports only the three completed original pairs. The old
    // cohort, its captured H02 and its missing final writer receipt stay failed.
    static CapacityTraining ReadCompleteDurationTraining(CalibrationPlan plan)
    {
        Require(DurationTrainingContinuation(plan) && DurationEnvelopeValid(plan), "Unknown duration training continuation.");
        string root = Path.GetFullPath(plan.CompositeTrainingRoot);
        Require(plan.RetainedRoots.Contains(root, StringComparer.OrdinalIgnoreCase)
            && !File.Exists(Path.Combine(root, "qualified-reference.json"))
            && !File.Exists(Path.Combine(root, "storage-writer.json"))
            && !File.Exists(Path.Combine(root, "H02", "qualification-source.json")), "The old failed cohort was changed or promoted.");
        string bindingFile = EvidenceRole(plan, "composite-training-bindings");
        Require(HashFile(bindingFile) == plan.Files[bindingFile] && new FileInfo(bindingFile).Length <= 1_048_576, "Changed duration training binding.");
        var bound = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(bindingFile));
        Require(bound.Count is >= 100 and <= 1024, "Duration training metadata count bound.");
        long metadataBytes = 0;
        foreach (var item in bound)
        {
            long bytes = new FileInfo(item.Key).Length;
            metadataBytes = checked(metadataBytes + bytes);
            Require(bytes is >= 0 and <= 33_554_432 && metadataBytes <= 134_217_728
                && !new[] { ".ts", ".mp4", ".dll", ".exe" }.Contains(Path.GetExtension(item.Key), StringComparer.OrdinalIgnoreCase)
                && HashFile(item.Key) == item.Value, "Changed or excessive retained metadata: " + item.Key);
        }
        void Bound(string file) => Require(bound.ContainsKey(Path.GetFullPath(file)), "Missing retained duration evidence: " + file);
        string disposition = EvidenceRole(plan, "duration-failure-disposition");
        using var decision = ReadOwnedHistoryJson(disposition); var d = decision.RootElement;
        Require(HashFile(disposition) == plan.Files[disposition]
            && d.GetProperty("kind").GetString() == "duration-30m-failed-cohort-training-only-v1"
            && !d.GetProperty("oldCohortPassed").GetBoolean() && !d.GetProperty("oldWriterDrainProven").GetBoolean()
            && !d.GetProperty("oldH02Accepted").GetBoolean() && !d.GetProperty("rewriteOldWindow").GetBoolean()
            && d.GetProperty("newAcquisitions").GetInt32() == 3 && d.GetProperty("retainedManagedRoles").GetInt32() == 29,
            "Failure disposition must preserve every old failure and exclude H02.");
        foreach (string name in new[] { "oldPlan", "oldController", "nativeClosure", "reuseAudit", "reuseReview", "helperCompatibilityReview" })
        {
            string file = d.GetProperty(name).GetString(); Bound(file);
            Require(plan.Files.TryGetValue(file, out string hash) && hash == bound[file], "Unbound independent reuse/failure decision.");
        }
        string controllerFile = d.GetProperty("oldController").GetString();
        using var controller = ReadOwnedHistoryJson(controllerFile, 262144); var c = controller.RootElement;
        Require(c.GetProperty("absent").GetBoolean() && c.GetProperty("exitCode").GetInt32() == 1
            && c.GetProperty("newManagedRows").GetInt32() == plan.RetainedChildren
            && c.GetProperty("seconds").GetDouble() == plan.RetainedElapsedSeconds
            && c.GetProperty("collectorCleanupError").ValueKind == JsonValueKind.Null
            && c.GetProperty("finalObservationError").ValueKind == JsonValueKind.Null
            && c.GetProperty("pendingControlProofCount").GetInt32() == 0 && c.GetProperty("pendingCreatorProofCount").GetInt32() == 0
            && c.GetProperty("observedChildClosure").EnumerateArray().All(row => row.GetProperty("absent").GetBoolean()
                && row.GetProperty("error").ValueKind == JsonValueKind.Null), "The failed controller lacks actual native closure.");
        using (var closure = ReadOwnedHistoryJson(d.GetProperty("nativeClosure").GetString(), 262144))
            Require(closure.RootElement.GetProperty("allExactBirthsAbsent").GetBoolean()
                && closure.RootElement.GetProperty("targetBirths").GetInt32() == 30
                && closure.RootElement.GetProperty("matchesAuthoritativeBefore").GetBoolean(), "Old process births or user preservation remain uncertain.");
        foreach (string name in new[] { "admitted-plan.json", "failure.json", "frozen-training-model.json", "storage.csv", "child-admissions.jsonl" }) Bound(Path.Combine(root, name));
        using var failed = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "failure.json"))); var f = failed.RootElement;
        Require(f.GetProperty("result").GetString() == "failed-no-retry"
            && f.GetProperty("error").GetString().Contains("Paired requests do not select the same ordered protected source objects and interval.", StringComparison.Ordinal)
            && f.GetProperty("error").GetString().Contains("Storage observer/producer closure remains uncertain", StringComparison.Ordinal)
            && f.GetProperty("samples").EnumerateArray().Select(s => s.GetProperty("Label").GetString()).SequenceEqual(Order.Take(3))
            && f.GetProperty("offlineSeconds").GetDouble() == plan.RetainedOfflineSeconds,
            "Only the exact failed pairing/observer cohort may supply completed training.");
        using var admitted = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "admitted-plan.json")));
        var old = admitted.RootElement.GetProperty("plan").Deserialize<CalibrationPlan>();
        Require(old.FreshCandidateAdmission == DurationAdmission && old.ReplayMinutes == 30 && old.Order.SequenceEqual(Order)
            && old.CompositeTrainingRoot == null && old.ReplayExporterRecipe == plan.ReplayExporterRecipe
            && c.GetProperty("planHash").GetString() == HashFile(d.GetProperty("oldPlan").GetString())
            && c.GetProperty("planHash").GetString() == admitted.RootElement.GetProperty("planHash").GetString()
            && old.Files.Any(file => Path.GetFileName(file.Key) == "F10Recorder.Windows.dll" && file.Value == ReplayReferenceModel.AssemblyHash)
            && old.Files.Any(file => Path.GetFileName(file.Key) == "F10Recorder.Core.dll" && file.Value == ReplayReferenceModel.CoreHash)
            && old.Files.GetValueOrDefault(MediaDependency.ResolveExecutable()) == HashFile(MediaDependency.ResolveExecutable()),
            "Retained training product/engine/duration/plan changed.");
        var target = ReplayStorage.ObserveCapacity(Path.GetDirectoryName(plan.Root));
        var samples = new List<Sample>(); var primary = new List<ReplayCalibrationSourceReceipt>(); var legacy = new List<ReplayCalibrationSourceReceipt>();
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string label in Order.Take(3))
        {
            string cell = Path.Combine(root, label), other = Path.Combine(cell, "legacy");
            string sampleFile = Path.Combine(cell, "sample.json"), pairedFile = Path.Combine(other, "sample.json"); Bound(sampleFile); Bound(pairedFile);
            var sample = JsonSerializer.Deserialize<Sample>(File.ReadAllText(sampleFile), Json);
            var paired = JsonSerializer.Deserialize<Sample>(File.ReadAllText(pairedFile), Json);
            var q = ReadBoundCompositeVariant(sample, cell, label, bound, target.Volume, target.AllocationUnit, 1800);
            var lq = ReadBoundCompositeVariant(paired, other, label, bound, target.Volume, target.AllocationUnit, 1800);
            RequireCompositePair(sample, paired, q, lq, 1800, ReplayCapacityPolicy.CoverageMilliseconds);
            string captureFile = Path.Combine(cell, "capture-exit.json"), fixtureFile = Path.Combine(cell, "fixture-exit.json"); Bound(captureFile); Bound(fixtureFile);
            Require(q.Capture.EvidenceHash == bound[captureFile] && q.Source.EvidenceHash == bound[fixtureFile]
                && q.Capture == lq.Capture && q.Source == lq.Source && q.CaptureObservationMilliseconds == 2000
                && q.MaximumCapacityGapMs is > 0 and <= 2000, "Original capture/growth ownership changed.");
            using var capture = JsonDocument.Parse(File.ReadAllText(captureFile));
            string session = capture.RootElement.GetProperty("sessionDirectory").GetString(), manifest = Path.Combine(session, "segments.csv"); Bound(manifest);
            var calls = ReadCompositeClockCalls(root, session, bound, 16, 33_554_432);
            Require(calls.Length > 100 && calls.Select(call => (call.epoch, call.frequency)).Distinct().Count() == 1
                && calls[0].frequency > 0 && sample.Timing?.CompletionReady > calls[0].qpc
                && sample.Timing.CompletionReady < calls[0].epoch + 1920L * calls[0].frequency
                && double.Parse(File.ReadLines(manifest).Last().Split(',')[2], CultureInfo.InvariantCulture) >= sample.Interval.End,
                "The original live acquisition/completion clock exceeds its actual1920s contract.");
            string selection = Path.Combine(cell, "selection.jsonl"); Bound(selection); int count = 0;
            foreach (string line in File.ReadLines(selection))
            {
                using var row = JsonDocument.Parse(line); var value = row.RootElement;
                var expected = value.GetProperty("Extent").Deserialize<ReplayFileExtent>();
                var current = ReplayStorage.Observe(value.GetProperty("Path").GetString());
                Require(current.Volume == expected.Volume && current.Identity == expected.Identity && current.Length == expected.Length
                    && current.AllocationUnit == expected.AllocationUnit && identities.Add(current.Volume + "/" + current.Identity),
                    "Retained selected source native identity/EOF changed or acquisitions overlap."); count++;
            }
            Require(count == q.SegmentCount, "Incomplete retained protected source set.");
            samples.Add(sample); primary.Add(q); legacy.Add(lq);
        }
        Require(primary.Select(q => q.AcquisitionIdentity).Distinct().Count() == 3 && samples.Select(s => s.Scope).Distinct().Count() == 1,
            "Retained training acquisitions are not three independent matching cells.");
        string modelFile = Path.Combine(root, "frozen-training-model.json");
        Require(HashFile(modelFile) == DurationTrainingModelHash, "The original model bytes changed; refitting is prohibited.");
        var model = JsonSerializer.Deserialize<ReplayReferenceModel>(File.ReadAllText(modelFile));
        var all = samples.SelectMany(s => new[] { s, s.LegacyOutput }).ToArray();
        long residual = Math.Max(0, all.Max(s => s.FinalBytes - s.InputBytes));
        long extra = Math.Max(0, all.Max(s => s.PeakAllocation - ReplayReferenceModel.Round(s.FinalBytes, target.AllocationUnit)));
        var receipts = Enumerable.Range(0, 3).Select(i => ReplayCalibrationCatalog.PairHash(primary[i], legacy[i])).ToArray();
        var headroom = new ReplayCaptureHeadroom(samples[0].Scope, target.Volume, target.AllocationUnit,
            ReplayReferenceModel.CaptureAllowance(primary.Max(q => q.CapturePollGrowthBytes.Value), target.AllocationUnit),
            ReplayReferenceModel.CaptureAllowance(primary.Max(q => q.CaptureStopGrowthBytes.Value), target.AllocationUnit), 2000);
        string revision = ReplayCalibrationCatalog.ModelRevision(samples[0].Scope, target.Volume, target.AllocationUnit, residual, extra, receipts,
            captureHeadroom: headroom, replayMinutes: 30);
        var reconstructed = new ReplayReferenceModel(revision, samples[0].Scope, target.Volume, target.AllocationUnit, residual, extra, receipts, headroom, 30);
        Require(ReplayReferenceModel.Hash(model) == ReplayReferenceModel.Hash(reconstructed), "Frozen model disagrees with its unchanged original three pairs.");
        return new(samples.ToArray(), primary.ToArray(), legacy.ToArray(), model);
    }
}
