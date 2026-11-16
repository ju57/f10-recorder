using System.Text.Json;

namespace F10Recorder;

internal static partial class ReplayCalibrationRun
{
    sealed record CapacityTraining(Sample[] Samples, ReplayCalibrationSourceReceipt[] Primary,
        ReplayCalibrationSourceReceipt[] Legacy, ReplayReferenceModel Model);

    // Only three COMPLETE current-horizon pairs may cross this boundary. The
    // interrupted H02 contributes no sample, coefficient or capture allowance.
    static CapacityTraining ReadCompleteCapacityTraining(CalibrationPlan plan)
    {
        Require(CapacityTrainingContinuation(plan) && CapacityEnvelopeValid(plan), "Unknown complete-training continuation.");
        string root = Path.GetFullPath(plan.CompositeTrainingRoot);
        Require(plan.RetainedRoots.Contains(root, StringComparer.OrdinalIgnoreCase)
            && !File.Exists(Path.Combine(root, "qualified-reference.json"))
            && !File.Exists(Path.Combine(root, "H02", "qualification-source.json"))
            && !File.Exists(Path.Combine(root, "H02", "primary-normal-sample.json")), "Failed held-out data cannot be imported.");
        string bindingFile = plan.Files.Keys.Single(file => Path.GetFileName(file) == "composite-training-bindings.json");
        Require(new FileInfo(bindingFile).Length is > 0 and <= 1_048_576 && HashFile(bindingFile) == plan.Files[bindingFile], "Changed training metadata binding.");
        var bound = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(bindingFile));
        Require(bound.Count is >= 60 and <= 1024, "Complete-training metadata bound.");
        string controllerFile = bound.Keys.Single(file => Path.GetFileName(file) == "controller.json");
        string priorAdmission = Path.GetDirectoryName(controllerFile);
        foreach (var item in bound)
            Require((Path.GetFullPath(item.Key).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || Path.GetDirectoryName(item.Key).Equals(priorAdmission, StringComparison.OrdinalIgnoreCase))
                && File.Exists(item.Key) && new FileInfo(item.Key).Length <= (Path.GetFileName(item.Key) == "storage.csv" ? 67_108_864 : 4_194_304)
                && HashFile(item.Key) == item.Value, "Retained current-training evidence changed: " + item.Key);
        void Bound(string file) => Require(bound.ContainsKey(Path.GetFullPath(file)), "Missing complete-training evidence: " + file);
        foreach (string name in new[] { "admitted-plan.json", "frozen-training-model.json", "storage.csv", "child-admissions.jsonl", "failure.json" }) Bound(Path.Combine(root, name));
        using var controller = JsonDocument.Parse(File.ReadAllText(controllerFile)); var c = controller.RootElement;
        Require(c.GetProperty("absent").GetBoolean() && c.GetProperty("exitCode").GetInt32() == 1
            && c.GetProperty("collectorCleanupError").ValueKind == JsonValueKind.Null
            && c.GetProperty("finalObservationError").ValueKind == JsonValueKind.Null
            && c.GetProperty("pendingControlProofCount").GetInt32() == 0
            && c.GetProperty("pendingCreatorProofCount").GetInt32() == 0
            && c.GetProperty("observedChildClosure").GetArrayLength() > 0
            && c.GetProperty("observedChildClosure").EnumerateArray().All(row => row.GetProperty("absent").GetBoolean() && row.GetProperty("error").ValueKind == JsonValueKind.Null)
            && c.GetProperty("error").GetString().StartsWith("Qualification failed:1; preserve all cases, no retry/refit", StringComparison.Ordinal),
            "Only the closed pre-capture foreground-timeout controller may reuse these completed pairs.");
        string preservation = Path.Combine(priorAdmission, "preservation-after-reconciled.json"); Bound(preservation);
        using (var after = JsonDocument.Parse(File.ReadAllText(preservation)))
            Require(after.RootElement.GetProperty("allFilesUnchanged").GetBoolean() && after.RootElement.GetProperty("startupUnchanged").GetBoolean()
                && after.RootElement.GetProperty("allListedBirthIdentitiesAbsent").GetBoolean()
                && after.RootElement.GetProperty("matchingBirthIdentities").GetArrayLength() == 0,
                "Prior preservation/birth-identity absence audit failed.");
        string admittedFile = Path.Combine(root, "admitted-plan.json");
        using var admitted = JsonDocument.Parse(File.ReadAllText(admittedFile));
        string originalPlanFile = Path.Combine(priorAdmission, "plan.json"); Bound(originalPlanFile);
        Require(c.GetProperty("planHash").GetString() == admitted.RootElement.GetProperty("planHash").GetString()
            && c.GetProperty("planHash").GetString() == HashFile(originalPlanFile), "Retained controller/original-plan identity mismatch.");
        using var failed = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "failure.json"))); var failure = failed.RootElement;
        Require(failure.GetProperty("result").GetString() == "failed-no-retry"
            && failure.GetProperty("error").GetString().StartsWith("System.IO.IOException: Controlled fixture foreground deadline or clock failed; no capture admitted.", StringComparison.Ordinal)
            && failure.GetProperty("samples").EnumerateArray().Select(row => row.GetProperty("Label").GetString()).SequenceEqual(Order.Take(3))
            && failure.GetProperty("model").GetProperty("Revision").GetString() == "5E8AAAE710C6675314A78E9D2D69032023B91D42CB848A59196B74D4B08F6B91",
            "Only the exact completed T01/T02/T03 foreground-timeout failure may continue.");
        string h02 = Path.Combine(root, "H02");
        foreach (string name in new[] { "primary-failure.json", "failure-context-after-drain.json", "fixture-exit.json", "fixture-display-exit.json",
            "foreground-handoff-ready.json", "computer-use-ready.json" }) Bound(Path.Combine(h02, name));
        Require(!File.Exists(Path.Combine(h02, "capture-arguments.json")) && !File.Exists(Path.Combine(h02, "capture-exit.json"))
            && !File.Exists(Path.Combine(h02, "acceptance-demand.json")) && !File.Exists(Path.Combine(h02, "sample.json"))
            && !File.Exists(Path.Combine(h02, "qualification-source.json")) && !File.Exists(Path.Combine(h02, "primary-normal-sample.json")),
            "The failed H02 handoff must contribute no acquisition, sample, export or model input.");
        using (var drain = JsonDocument.Parse(File.ReadAllText(Path.Combine(h02, "failure-context-after-drain.json"))))
        {
            var d = drain.RootElement;
            Require(d.GetProperty("cleanupErrors").GetArrayLength() == 0
                && new[] { "completionStatus", "actualResult", "producerStopped", "timing", "managed", "index", "gate" }
                    .All(name => d.GetProperty(name).ValueKind == JsonValueKind.Null),
                "The pre-capture H02 failure acquired or left an unclosed producer/export stage.");
        }
        using (var ready = JsonDocument.Parse(File.ReadAllText(Path.Combine(h02, "foreground-handoff-ready.json"))))
        using (var ack = JsonDocument.Parse(File.ReadAllText(Path.Combine(h02, "computer-use-ready.json"))))
        using (var display = JsonDocument.Parse(File.ReadAllText(Path.Combine(h02, "fixture-display-exit.json"))))
        {
            var r = ready.RootElement; var release = r.GetProperty("release"); var a = ack.RootElement;
            Require(r.GetProperty("label").GetString() == "H02" && r.GetProperty("maximumSeconds").GetInt32() == 180
                && !r.GetProperty("captureStarted").GetBoolean() && a.GetProperty("allUiCallsReturned").GetBoolean()
                && new[] { "Label", "Pid", "Hwnd", "Nonce" }.All(name => a.GetProperty(name).ToString() == release.GetProperty(name).ToString())
                && a.GetProperty("acknowledgedUtc").GetDateTime() > display.RootElement.GetProperty("desktop").GetProperty("utc").GetDateTime(),
                "The preserved H02 acknowledgement was not the exact late post-timeout release.");
        }
        using (var admissions = JsonDocument.Parse("[" + string.Join(',', File.ReadLines(Path.Combine(root, "child-admissions.jsonl"))) + "]"))
            Require(admissions.RootElement.GetArrayLength() == 27
                && admissions.RootElement.EnumerateArray().Last().GetProperty("role").GetString() == "fixture:H02"
                && !admissions.RootElement.EnumerateArray().Any(row => row.GetProperty("role").GetString() == "capture:H02"),
                "H02 capture admission changed or was present.");
        var old = admitted.RootElement.GetProperty("plan").Deserialize<CalibrationPlan>();
        Require(old.FreshCandidateAdmission == CapacityAdmission && old.CompositeTrainingRoot == null && old.Order.SequenceEqual(Order)
            && old.ReplayExporterRecipe == ReplaySaveExport.PolicyIdentity
            && old.Files.Any(f => Path.GetFileName(f.Key) == "F10Recorder.Windows.dll" && f.Value == ReplayReferenceModel.AssemblyHash)
            && old.Files.Any(f => Path.GetFileName(f.Key) == "F10Recorder.Core.dll" && f.Value == ReplayReferenceModel.CoreHash)
            && old.Files.GetValueOrDefault(MediaDependency.ResolveExecutable()) == HashFile(MediaDependency.ResolveExecutable()),
            "Complete training belongs to different product/engine/policy bytes.");
        var target = ReplayStorage.ObserveCapacity(Path.GetDirectoryName(plan.Root));
        var samples = new List<Sample>(); var primary = new List<ReplayCalibrationSourceReceipt>(); var legacy = new List<ReplayCalibrationSourceReceipt>();
        foreach (string label in Order.Take(3))
        {
            string cell = Path.Combine(root, label), other = Path.Combine(cell, "legacy");
            string sampleFile = Path.Combine(cell, "sample.json"), pairedFile = Path.Combine(other, "sample.json"); Bound(sampleFile); Bound(pairedFile);
            var sample = JsonSerializer.Deserialize<Sample>(File.ReadAllText(sampleFile), Json);
            var paired = JsonSerializer.Deserialize<Sample>(File.ReadAllText(pairedFile), Json);
            var q = ReadBoundCompositeVariant(sample, cell, label, bound, target.Volume, target.AllocationUnit);
            var lq = ReadBoundCompositeVariant(paired, other, label, bound, target.Volume, target.AllocationUnit);
            RequireCompositePair(sample, paired, q, lq);
            string growthFile = Path.Combine(cell, "capture-growth.json"), captureFile = Path.Combine(cell, "capture-exit.json"), fixtureFile = Path.Combine(cell, "fixture-exit.json");
            Bound(growthFile); Bound(captureFile); Bound(fixtureFile);
            Require(q.Capture.EvidenceHash == HashFile(captureFile) && q.Source.EvidenceHash == HashFile(fixtureFile)
                && q.Capture == lq.Capture && q.Source == lq.Source && q.CaptureObservationMilliseconds == ReplayCapacityPolicy.CoverageMilliseconds
                && q.MaximumCapacityGapMs is > 0 and <= ReplayCapacityPolicy.CoverageMilliseconds
                && q.MaximumActiveGapMs is > 0 and <= ReplayCapacityPolicy.CoverageMilliseconds, "Current growth/process horizon changed.");
            using var capture = JsonDocument.Parse(File.ReadAllText(captureFile));
            string session = capture.RootElement.GetProperty("sessionDirectory").GetString(), manifest = Path.Combine(session, "segments.csv"); Bound(manifest);
            var calls = ReadCompositeClockCalls(root, session, bound);
            Require(calls.Length > 100 && calls.Select(call => (call.epoch, call.frequency)).Distinct().Count() == 1 && calls[0].frequency > 0,
                "Training lacks the original common live acquisition clock.");
            double lastEnd = double.Parse(File.ReadLines(manifest).Last().Split(',')[2], System.Globalization.CultureInfo.InvariantCulture);
            double availableStart = double.Parse(File.ReadLines(manifest).First(line => !line.StartsWith("chunk-000000000.ts,", StringComparison.Ordinal)).Split(',')[1],
                System.Globalization.CultureInfo.InvariantCulture);
            double stopThreshold = availableStart + 300 + (label == "T01" ? 1 : label == "T02" ? .25 : .75) + 10;
            long thresholdQpc = checked(calls[0].epoch + (long)Math.Ceiling(stopThreshold * calls[0].frequency));
            // The pinned collector stops immediately AFTER successful Cache
            // completion, or at this forced-stop threshold while still saving.
            // It never promised ten additional seconds after completion.
            Require(sample.Timing?.CompletionReady > 0 && sample.Timing.CompletionReady < thresholdQpc
                && !calls.Any(call => call.qpc <= sample.Timing.CompletionReady && call.cutoff >= stopThreshold)
                && lastEnd >= sample.Interval.End, "Training crossed the forced-stop boundary before primary completion.");
            samples.Add(sample); primary.Add(q); legacy.Add(lq);
        }
        Require(primary.Select(q => q.AcquisitionIdentity).Distinct().Count() == 3
            && samples.Select(s => s.AcquisitionSelectionHash).Distinct().Count() == 3 && samples.Select(s => s.Scope).Distinct().Count() == 1,
            "Complete training acquisitions are not independent.");
        string modelFile = Path.Combine(root, "frozen-training-model.json");
        var model = JsonSerializer.Deserialize<ReplayReferenceModel>(File.ReadAllText(modelFile));
        var all = samples.SelectMany(s => new[] { s, s.LegacyOutput }).ToArray();
        long residual = Math.Max(0, all.Max(s => s.FinalBytes - s.InputBytes));
        long extra = Math.Max(0, all.Max(s => s.PeakAllocation - ReplayReferenceModel.Round(s.FinalBytes, target.AllocationUnit)));
        var receipts = Enumerable.Range(0, 3).Select(i => ReplayCalibrationCatalog.PairHash(primary[i], legacy[i])).ToArray();
        var headroom = new ReplayCaptureHeadroom(samples[0].Scope, target.Volume, target.AllocationUnit,
            ReplayReferenceModel.CaptureAllowance(primary.Max(q => q.CapturePollGrowthBytes.Value), target.AllocationUnit),
            ReplayReferenceModel.CaptureAllowance(primary.Max(q => q.CaptureStopGrowthBytes.Value), target.AllocationUnit), ReplayCapacityPolicy.CoverageMilliseconds);
        string revision = ReplayCalibrationCatalog.ModelRevision(samples[0].Scope, target.Volume, target.AllocationUnit, residual, extra, receipts, captureHeadroom: headroom);
        var reconstructed = new ReplayReferenceModel(revision, samples[0].Scope, target.Volume, target.AllocationUnit, residual, extra, receipts, headroom);
        Require(ReplayReferenceModel.Hash(model) == ReplayReferenceModel.Hash(reconstructed), "Frozen complete-training model disagrees with its three original pairs; no refit.");
        return new(samples.ToArray(), primary.ToArray(), legacy.ToArray(), model);
    }
}
