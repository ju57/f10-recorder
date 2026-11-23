using System.Diagnostics;
using System.Text.Json;

namespace F10Recorder;

internal static partial class ReplayCalibrationRun
{
    // Recovery verifies already-completed evidence. It never calls Collect,
    // Inspect, a decoder, model fitting, or the ordinary account catalog writer.
    internal static int RecoverCompositeReport(string root, string input, string expectedHash)
    {
        var clock = Stopwatch.StartNew();
        Require(!Directory.Exists(root) && new FileInfo(input).Length <= 65536 && HashFile(input) == expectedHash,
            "Fresh exact file-only recovery admission required.");
        using var document = JsonDocument.Parse(File.ReadAllText(input)); var p = document.RootElement;
        Require(p.GetProperty("kind").GetString() == "completed-composite-report-recovery-v1", "Wrong recovery scope.");
        Require(Path.GetFullPath(p.GetProperty("outputRoot").GetString()) == root
            && Path.GetDirectoryName(root) == Path.GetDirectoryName(Path.GetDirectoryName(input)), "Recovery must use its bound fresh sibling artifact directory.");
        void Check() => Require(clock.Elapsed.TotalSeconds < 25, "Recovery25second active deadline.");
        foreach (var binding in p.GetProperty("files").EnumerateObject())
        { Check(); Require(HashFile(binding.Name) == binding.Value.GetString(), "Recovery input changed: " + binding.Name); }
        foreach (string field in new[] { "originalPlan", "originalController", "originalStderr", "preservation", "metadataManifest" })
            Require(p.GetProperty("files").TryGetProperty(p.GetProperty(field).GetString(), out _), "Recovery role is not bound: " + field);
        foreach (string actual in new[] { typeof(ReplayCalibrationRun).Assembly.Location, Environment.ProcessPath })
            Require(p.GetProperty("files").TryGetProperty(actual, out _), "Actual recovery helper/host is not bound.");
        string originalPath = p.GetProperty("originalPlan").GetString();
        using var originalDocument = JsonDocument.Parse(File.ReadAllText(originalPath));
        var original = originalDocument.RootElement.GetProperty("plan").Deserialize<CalibrationPlan>();
        ValidateResources(original);
        Require(original.CompositeQualification && original.CompositeTrainingRoot != null && original.Order.SequenceEqual(Order.Skip(2)),
            "Only the completed four-case continuation is eligible.");
        string originalHash = HashFile(originalPath), prior = original.CompositeTrainingRoot;
        string controllerPath = p.GetProperty("originalController").GetString();
        using var controllerDocument = JsonDocument.Parse(File.ReadAllText(controllerPath)); var controller = controllerDocument.RootElement;
        Require(controller.GetProperty("absent").GetBoolean() && controller.GetProperty("exitCode").GetInt32() == -532462766
            && controller.GetProperty("planSha256").GetString() == originalHash, "Original collector failure must remain explicit.");
        string stderr = p.GetProperty("originalStderr").GetString();
        Require(File.ReadAllText(stderr).Contains("Calibration JSON exceeds128KiB.", StringComparison.Ordinal), "Original reporting failure is not evidenced.");
        using var preservation = JsonDocument.Parse(File.ReadAllText(p.GetProperty("preservation").GetString()));
        foreach (string name in new[] { "allFilesUnchanged", "startupUnchanged", "allListedIdsAbsent" })
            Require(preservation.RootElement.GetProperty(name).GetBoolean(), "Original state/process preservation failed.");
        string manifest = p.GetProperty("metadataManifest").GetString();
        Require(new FileInfo(manifest).Length <= 1048576, "Metadata manifest1MiB bound.");
        var bound = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(manifest));
        Require(bound.Count is > 0 and <= 1024, "Recovery metadata file-count bound.");
        long readBytes = 0;
        foreach (var binding in bound)
        {
            Check(); long length = new FileInfo(binding.Key).Length; readBytes = checked(readBytes + length);
            Require(length <= 8388608 && readBytes <= 67108864 && Path.GetExtension(binding.Key) is not ".mp4" and not ".ts"
                && HashFile(binding.Key) == binding.Value, "Recovery metadata changed or exceeded64MiB envelope.");
        }
        void Bound(string file) => Require(bound.ContainsKey(Path.GetFullPath(file)), "Missing raw recovery binding: " + file);
        foreach (string name in new[] { "qualified-reference.json", "frozen-training-model.json", "qualification-sources.json", "observation-summary.json", "storage.csv", "storage-writer.json", "child-admissions.jsonl" }) Bound(Path.Combine(original.Root, name));
        MediaDependency.RootDirectory = original.EngineRoot;
        var first = ReadCompositeTraining(original); Check();
        var sources = first.Select(s => s.Primary).ToList(); var legacy = first.Select(s => s.Legacy).ToList();
        var target = ReplayStorage.ObserveCapacity(original.Root);
        foreach (string label in original.Order)
        {
            Check(); string cell = Path.Combine(original.Root, label), other = Path.Combine(cell, "legacy");
            Bound(Path.Combine(cell, "sample.json")); Bound(Path.Combine(other, "sample.json"));
            var sample = JsonSerializer.Deserialize<Sample>(File.ReadAllText(Path.Combine(cell, "sample.json")), Json);
            var paired = JsonSerializer.Deserialize<Sample>(File.ReadAllText(Path.Combine(other, "sample.json")), Json);
            var q = ReadBoundCompositeVariant(sample, cell, label, bound, target.Volume, target.AllocationUnit);
            var lq = ReadBoundCompositeVariant(paired, other, label, bound, target.Volume, target.AllocationUnit);
            RequireCompositePair(sample, paired, q, lq);
            foreach (var item in new[] { (name: "capture-exit.json", evidence: q.Capture), (name: "fixture-exit.json", evidence: q.Source) })
            { string file = Path.Combine(cell, item.name); Bound(file); Require(HashFile(file) == item.evidence.EvidenceHash, "Raw source closure changed."); }
            sources.Add(q); legacy.Add(lq);
        }
        var qualified = JsonSerializer.Deserialize<ReplayCalibrationReceipt>(File.ReadAllText(Path.Combine(original.Root, "qualified-reference.json")));
        var model = JsonSerializer.Deserialize<ReplayReferenceModel>(File.ReadAllText(Path.Combine(original.Root, "frozen-training-model.json")));
        using var observationDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(original.Root, "observation-summary.json"))); var observation = observationDocument.RootElement;
        using var priorFailure = JsonDocument.Parse(File.ReadAllText(Path.Combine(prior, "failure.json")));
        double maximumActive = Math.Max(priorFailure.RootElement.GetProperty("MaximumActiveGap").GetDouble(),
            Math.Max(observation.GetProperty("MaximumActiveGap").GetDouble(), sources.Max(s => s.MaximumActiveGapMs)));
        string observationHash = ReplayReferenceModel.Hash(new { retained = HashFile(Path.Combine(prior, "storage.csv")), current = HashFile(Path.Combine(original.Root, "storage.csv")) });
        var rebuilt = ReplayCalibrationCatalog.CreateReceipt(model, sources.ToArray(), maximumActive, observationHash, originalHash, legacyOutputs: legacy.ToArray());
        Require(ReplayReferenceModel.Hash(rebuilt) == ReplayReferenceModel.Hash(qualified), "Reconstructed twelve-output qualification differs from the original frozen receipt.");
        using var writer = JsonDocument.Parse(File.ReadAllText(Path.Combine(original.Root, "storage-writer.json")));
        Require(writer.RootElement.GetProperty("writerAbsent").GetBoolean() && writer.RootElement.GetProperty("failure").ValueKind == JsonValueKind.Null
            && writer.RootElement.GetProperty("enqueued").GetInt32() == writer.RootElement.GetProperty("written").GetInt32(), "Storage trace writer did not finish.");
        Require(observation.GetProperty("PeakUsed").GetInt64() < original.StopBytes
            && controller.GetProperty("peakRam").GetInt64() <= 805306368 && controller.GetProperty("peakHandles").GetInt32() <= 32768
            && controller.GetProperty("seconds").GetDouble() + original.RetainedElapsedSeconds < original.DeadlineSeconds
            && observation.GetProperty("offlineSeconds").GetDouble() + original.RetainedOfflineSeconds < original.OfflineDeadlineSeconds
            && File.ReadLines(Path.Combine(original.Root, "child-admissions.jsonl")).Count() + original.RetainedChildren <= original.MaximumChildren,
            "Original resource/time/process bounds failed.");
        var recordedIds = controller.GetProperty("children").EnumerateArray().Select(child => child.GetProperty("pid").GetInt32())
            .Append(controller.GetProperty("pid").GetInt32()).ToHashSet();
        foreach (var q in sources.Concat(legacy))
        { Require(q.Decode.ProcessId is > 0, "Independent decoder identity is missing."); recordedIds.Add(q.Decode.ProcessId.Value); }
        foreach (var q in sources)
        {
            string caseRoot = Path.Combine(q.Label is "T01" or "T02" ? prior : original.Root, q.Label);
            foreach (string name in new[] { "capture-exit.json", "fixture-exit.json" })
            {
                string file = Path.Combine(caseRoot, name); Bound(file);
                using var closure = JsonDocument.Parse(File.ReadAllText(file));
                int pid = closure.RootElement.GetProperty("pid").GetInt32(); Require(pid > 0, "Raw source identity is missing."); recordedIds.Add(pid);
            }
        }
        foreach (int pid in recordedIds)
        {
            try { using var process = Process.GetProcessById(pid); Require(process.HasExited, "Recorded task PID remains live; no recovery."); }
            catch (ArgumentException) { }
        }
        Check(); TrustedStorage.CreatePrivateDirectory(root);
        // This is a NEW evidence-verification receipt, never a replacement for
        // the failed collector's missing result or its nonzero exit status.
        Write(Path.Combine(root, "verification.json"), new { result = "verified-completed-evidence-after-terminal-report-failure", originalCollectorPassed = false,
            originalController = controllerPath, originalExitCode = controller.GetProperty("exitCode").GetInt32(), originalPlanHash = originalHash,
            originalQualification = Path.Combine(original.Root, "qualified-reference.json"), qualified.ReceiptHash, metadataManifestSha256 = HashFile(manifest),
            verifiedPacketOutputs = sources.Count, verifiedLegacyOutputs = legacy.Count, maximumActive, observationHash, readBytes, seconds = clock.Elapsed.TotalSeconds,
            newCaptures = 0, newDecodes = 0, modelRefitted = false, defaultCatalogWritten = false,
            claim = "Original typed receipts reconstructed from bound raw metadata/current native final identities. No fresh media payload hash/decode. Original collector remains failed; ordinary-worker/Saved/percentile/physical-feedback and other profiles remain unqualified." });
        return 0;
    }
}
