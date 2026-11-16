using System.Text.Json;
using System.Text.RegularExpressions;

namespace F10Recorder;

internal static partial class ReplayCalibrationRun
{
    internal static bool ValidOutsidePartition(long baseline, long future, long observed, long charged, long total = 10_000_000_000)
    {
        try { return baseline >= 9_000_000_000 && future >= 67_108_864 && observed > 0 && observed <= baseline
            && charged >= 0 && charged <= future && checked(baseline + future) == total; }
        catch (OverflowException) { return false; }
    }
    internal static int OutsidePartitionChecks()
    {
        int checks = 0;
        void Check(bool expected, long baseline, long future, long observed, long charged)
        { Require(ValidOutsidePartition(baseline, future, observed, charged) == expected, "Outside partition arithmetic control failed."); checks++; }
        Check(true, 9_000_000_000, 1_000_000_000, 8_703_487_400, 8_388_608);
        Check(true, 9_700_000_000, 300_000_000, 9_691_414_560, 280_000_000);
        Check(false, 9_700_000_000, 300_000_001, 9_691_414_560, 280_000_000);
        Check(false, 9_700_000_000, 300_000_000, 9_700_000_001, 280_000_000);
        Check(false, 9_700_000_000, 300_000_000, 0, 280_000_000);
        Check(false, 9_700_000_000, 300_000_000, -1, 280_000_000);
        Check(false, 9_700_000_000, 300_000_000, 9_691_414_560, 300_000_001);
        Check(false, 9_700_000_000, 300_000_000, 9_691_414_560, -1);
        Check(false, 10_000_000_000, 0, 9_691_414_560, 0);
        Check(false, 9_950_000_000, 50_000_000, 9_691_414_560, 1);
        Check(false, long.MaxValue, long.MaxValue, 1, 0);
        Check(false, -1, 10_000_000_001, 1, 0);
        Console.WriteLine(checks + " pure outside-partition controls passed; no native/media/state action."); return 0;
    }
    static void ValidateCompositeOutsideBaseline(CalibrationPlan plan)
    {
        Require(plan.CompositeQualification, "Outside baseline belongs to explicit composite qualification only.");
        if(OwnedHistoryQualification(plan)){ValidateOwnedHistoryOutside(plan);return;}
        if (CapacityQualification(plan)) ValidateCapacityDocument(plan);
        string file = EvidenceRole(plan, "outside-storage-baseline");
        Require(HashFile(file) == plan.Files[file], "Outside native accounting statement changed.");
        using var summary = JsonDocument.Parse(File.ReadAllText(file)); var s = summary.RootElement;
        string inventory = EvidenceRole(plan, "outside-inventory");
        string inventoryPlan = EvidenceRole(plan, "outside-inventory-plan");
        Require(HashFile(inventory) == plan.Files[inventory] && HashFile(inventoryPlan) == plan.Files[inventoryPlan], "Outside native inventory changed.");
        using var native = JsonDocument.Parse(File.ReadAllText(inventory)); using var inputs = JsonDocument.Parse(File.ReadAllText(inventoryPlan));
        long baseline = s.GetProperty("baselineCapBytes").GetInt64(), future = s.GetProperty("futureGrowthAllowanceBytes").GetInt64();
        long observed = s.GetProperty("observedNativeBytes").GetInt64();
        string[] excluded = inputs.RootElement.GetProperty("excludedRoots").EnumerateArray().Select(v => v.GetString()).ToArray();
        Require(excluded.Length == excluded.Distinct(StringComparer.OrdinalIgnoreCase).Count()
            && plan.RetainedRoots.Length == plan.RetainedRoots.Distinct(StringComparer.OrdinalIgnoreCase).Count()
            && excluded.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(plan.RetainedRoots.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase),
            "Outside exclusions must exactly equal the separately charged retained roots.");
        long charged = 0;
        if (baseline != 9_000_000_000 || future != 1_000_000_000)
        {
            string proof = EvidenceRole(plan, "outside-forward-envelope");
            Require(new FileInfo(proof).Length is > 0 and <= 4_194_304 && HashFile(proof) == plan.Files[proof], "Bound prospective outside envelope required.");
            using var envelope = JsonDocument.Parse(File.ReadAllText(proof)); var e = envelope.RootElement;
            string forecastFile = EvidenceRole(plan, "outside-outstanding-writes");
            Require(new FileInfo(forecastFile).Length is > 0 and <= 65536 && HashFile(forecastFile) == plan.Files[forecastFile], "Bound outstanding-write forecast required.");
            using var forecast = JsonDocument.Parse(File.ReadAllText(forecastFile)); var f = forecast.RootElement;
            long forecastBytes = 0; var chargeNames = new HashSet<string>(StringComparer.Ordinal);
            Require(f.GetProperty("kind").GetString() == "outside-outstanding-writes-v1"
                && f.GetProperty("charges").GetArrayLength() is > 0 and <= 32, "Outstanding-write forecast shape is invalid.");
            foreach (var charge in f.GetProperty("charges").EnumerateArray())
            {
                string name = charge.GetProperty("name").GetString(); long bytes = charge.GetProperty("bytes").GetInt64();
                Require(!string.IsNullOrWhiteSpace(name) && chargeNames.Add(name) && bytes >= 0, "Outstanding-write charge is invalid or duplicated.");
                forecastBytes = checked(forecastBytes + bytes);
            }
            long accrued = e.GetProperty("newOrTouchedNativeBytes").GetInt64(), outstanding = e.GetProperty("archiveReserveBytes").GetInt64(),
                reports = e.GetProperty("reportAndLogReserveBytes").GetInt64();
            charged = checked(accrued + outstanding + reports);
            var ids = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Require(e.GetProperty("rows").GetArrayLength() <= 20000, "Outside accrued-row count bound.");
            foreach (var row in e.GetProperty("rows").EnumerateArray())
            {
                string path = row.GetProperty("path").GetString(), id = row.GetProperty("identity").GetString();
                long allocation = row.GetProperty("nativeAllocatedBytes").GetInt64();
                Require(Path.IsPathFullyQualified(path) && paths.Add(path) && !string.IsNullOrWhiteSpace(id) && allocation >= 0
                    && !plan.RetainedRoots.Any(root => path.Equals(root, StringComparison.OrdinalIgnoreCase)
                        || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)), "Invalid or excluded outside accrued row.");
                if (ids.TryGetValue(id, out long prior)) Require(prior == allocation, "Inconsistent outside native alias charge.");
                else ids.Add(id, allocation);
            }
            long actualAccrued = 0; foreach (long allocation in ids.Values) actualAccrued = checked(actualAccrued + allocation);
            Require(e.GetProperty("revision").GetString() == "outside-partition-v2"
                && e.GetProperty("result").GetString() == "complete-forward-native-changed-set" && e.GetProperty("withinAllowance").GetBoolean()
                && e.GetProperty("forecastSha256").GetString() == HashFile(forecastFile) && outstanding == forecastBytes
                && e.GetProperty("baselineResultSha256").GetString() == HashFile(inventory)
                && e.GetProperty("baselinePlanSha256").GetString() == HashFile(inventoryPlan)
                && e.GetProperty("allowanceBytes").GetInt64() == future
                && accrued == actualAccrued && outstanding >= 0 && reports >= 8_388_608
                && e.GetProperty("chargedFutureBytes").GetInt64() == charged
                && e.GetProperty("namespaceRetainedRoots").EnumerateArray().Select(v => v.GetString()).Order(StringComparer.OrdinalIgnoreCase)
                    .SequenceEqual(plan.RetainedRoots.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase),
                "Prospective outside envelope identity, namespace or accrued/outstanding charge is incomplete.");
        }
        Require(native.RootElement.GetProperty("result").GetString() == "complete-native-outside-inventory"
            && native.RootElement.GetProperty("error").ValueKind == JsonValueKind.Null
            && native.RootElement.GetProperty("planSha256").GetString() == HashFile(inventoryPlan)
            && observed == native.RootElement.GetProperty("nativeAllocatedBytes").GetInt64()
            && ValidOutsidePartition(baseline, future, observed, charged, plan.OutsideBuildAllowanceBytes)
            && plan.OutsideBuildAllowanceBytes == (CapacityQualification(plan)
                ? CapacityOutsideBytes : 10_000_000_000)
            && (!CapacityQualification(plan) || baseline == 100_000_000_000 && future == plan.OutsideBuildAllowanceBytes - 100_000_000_000),
            "Outside native baseline plus separately bounded future growth is incomplete.");
    }

    // A narrowly admitted continuation of TWO completed pairs, never a model
    // import, a replacement of a heldout, or retrovalidation of the failed T03.
    static List<(Sample Sample, ReplayCalibrationSourceReceipt Primary, ReplayCalibrationSourceReceipt Legacy)> ReadCompositeTraining(CalibrationPlan plan)
    {
        string root = plan.CompositeTrainingRoot;
        Require(root != null && !File.Exists(Path.Combine(root, "frozen-training-model.json"))
            && !File.Exists(Path.Combine(root, "qualified-reference.json")), "A paired continuation cannot import a frozen model.");
        string bindings = plan.Files.Keys.Single(p => Path.GetFileName(p) == "composite-training-bindings.json");
        Require(new FileInfo(bindings).Length is > 0 and <= 1_048_576 && HashFile(bindings) == plan.Files[bindings], "Prior metadata bindings changed.");
        var expected = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(bindings));
        Require(expected.Count is >= 20 and <= 1024, "Prior metadata envelope changed.");
        string priorPlanDirectory = Path.GetDirectoryName(expected.Keys.Single(p => Path.GetFileName(p) == "controller.json"));
        foreach (var file in expected)
            Require((Path.GetFullPath(file.Key).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || Path.GetDirectoryName(file.Key).Equals(priorPlanDirectory, StringComparison.OrdinalIgnoreCase))
                && File.Exists(file.Key) && new FileInfo(file.Key).Length <= 4_194_304 && HashFile(file.Key) == file.Value,
                "Prior paired evidence changed: " + file.Key);
        using var controller = JsonDocument.Parse(File.ReadAllText(Path.Combine(priorPlanDirectory, "controller.json")));
        Require(controller.RootElement.GetProperty("absent").GetBoolean() && controller.RootElement.GetProperty("exitCode").GetInt32() == 1
            && CompositeElapsedMatches(plan.RetainedElapsedSeconds, controller.RootElement.GetProperty("seconds").GetDouble()),
            "Prior controller closure or cumulative wall charge changed.");
        using var preservation = JsonDocument.Parse(File.ReadAllText(Path.Combine(priorPlanDirectory, "preservation-after.json")));
        Require(expected.ContainsKey(Path.Combine(priorPlanDirectory, "preservation-after.json"))
            && preservation.RootElement.GetProperty("allFilesUnchanged").GetBoolean()
            && preservation.RootElement.GetProperty("startupUnchanged").GetBoolean()
            && preservation.RootElement.GetProperty("allListedIdsAbsent").GetBoolean(), "Prior state restoration/owned-process closure is incomplete.");
        void Bound(string file) => Require(expected.ContainsKey(Path.GetFullPath(file)), "Required prior evidence is not prospectively bound: " + file);
        foreach (string name in new[] { "admitted-plan.json", "failure.json", "storage.csv", "child-admissions.jsonl" }) Bound(Path.Combine(root, name));
        using var failed = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "failure.json")));
        Require(failed.RootElement.GetProperty("model").ValueKind == JsonValueKind.Null
            && failed.RootElement.GetProperty("samples").EnumerateArray().Select(s => s.GetProperty("Label").GetString()).SequenceEqual(Order.Take(2))
            && failed.RootElement.GetProperty("MaximumActiveGap").GetDouble() is > 0 and <= 500
            && Math.Ceiling(failed.RootElement.GetProperty("offlineSeconds").GetDouble()) == plan.RetainedOfflineSeconds
            && failed.RootElement.GetProperty("error").GetString().Contains("Controlled source lost foreground or exited.", StringComparison.Ordinal)
            && File.ReadLines(Path.Combine(root, "child-admissions.jsonl")).Count() == 21
            && !File.Exists(Path.Combine(root, "T03", "acceptance-demand.json")), "Only the admitted interrupted pre-save T03 phase may continue.");
        Bound(Path.Combine(root, "T03", "foreground-failure.json")); Bound(Path.Combine(root, "T03", "failure-context-after-drain.json"));
        using var drain = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "T03", "failure-context-after-drain.json")));
        Require(drain.RootElement.GetProperty("actualResult").ValueKind == JsonValueKind.Null
            && drain.RootElement.GetProperty("completionStatus").ValueKind == JsonValueKind.Null
            && drain.RootElement.GetProperty("cleanupErrors").GetArrayLength() == 0
            && drain.RootElement.GetProperty("producerStopped").GetBoolean(), "Failed acquisition has accepted or unclosed work.");
        using var admitted = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "admitted-plan.json")));
        var old = admitted.RootElement.GetProperty("plan").Deserialize<CalibrationPlan>();
        Require(old.CompositeQualification && old.CompositeTrainingRoot == null && old.Order.SequenceEqual(Order)
            && old.ReplayExporterRecipe == ReplaySaveExport.PolicyIdentity
            && old.Files.Any(f => Path.GetFileName(f.Key) == "F10Recorder.Windows.dll" && f.Value == ReplayReferenceModel.AssemblyHash)
            && old.Files.Any(f => Path.GetFileName(f.Key) == "F10Recorder.Core.dll" && f.Value == ReplayReferenceModel.CoreHash)
            && old.Files.GetValueOrDefault(MediaDependency.ResolveExecutable()) == HashFile(MediaDependency.ResolveExecutable()),
            "Retained observations belong to different product/engine/policy bytes.");
        var target = ReplayStorage.ObserveCapacity(Path.GetDirectoryName(plan.Root));
        var result = new List<(Sample, ReplayCalibrationSourceReceipt, ReplayCalibrationSourceReceipt)>();
        foreach (string label in Order.Take(2))
        {
            string cell = Path.Combine(root, label), mainFile = Path.Combine(cell, "sample.json"), legacyFile = Path.Combine(cell, "legacy", "sample.json");
            Bound(mainFile); Bound(legacyFile);
            var sample = JsonSerializer.Deserialize<Sample>(File.ReadAllText(mainFile), Json);
            var legacy = JsonSerializer.Deserialize<Sample>(File.ReadAllText(legacyFile), Json);
            ReplayCalibrationSourceReceipt ReadVariant(Sample s, string directory) => ReadBoundCompositeVariant(s, directory, label, expected, target.Volume, target.AllocationUnit);
            var primary = ReadVariant(sample, cell); var paired = ReadVariant(legacy, Path.Combine(cell, "legacy"));
            RequireCompositePair(sample, legacy, primary, paired);
            string growth = Path.Combine(cell, "capture-growth.json"), capture = Path.Combine(cell, "capture-exit.json"), fixture = Path.Combine(cell, "fixture-exit.json");
            Bound(growth); Bound(capture); Bound(fixture);
            Require(primary.Capture.EvidenceHash == HashFile(capture) && primary.Source.EvidenceHash == HashFile(fixture)
                && paired.Capture == primary.Capture && paired.Source == primary.Source, "Prior common acquisition/fixture receipts changed.");
            using var g = JsonDocument.Parse(File.ReadAllText(growth));
            Require(primary.CaptureVolume == g.RootElement.GetProperty("Volume").GetString()
                && primary.CapturePollGrowthBytes == g.RootElement.GetProperty("PollGrowth").GetInt64()
                && primary.CaptureStopGrowthBytes == g.RootElement.GetProperty("StopGrowth").GetInt64()
                && g.RootElement.GetProperty("MaximumActiveGap").GetDouble() <= 500, "Prior capture headroom observations changed.");
            string continued = expected.Keys.Single(p => Path.GetFileName(p) == "CONTINUED-CAPTURE-" + label + ".json");
            using var e = JsonDocument.Parse(File.ReadAllText(continued));
            using var c = JsonDocument.Parse(File.ReadAllText(capture));
            string session = c.RootElement.GetProperty("sessionDirectory").GetString(); string manifest = Path.Combine(session, "segments.csv"); Bound(manifest);
            double start = double.Parse(File.ReadLines(manifest).First(line => !line.StartsWith("chunk-000000000.ts,", StringComparison.Ordinal)).Split(',')[1],
                System.Globalization.CultureInfo.InvariantCulture);
            var calls = ReadCompositeClockCalls(root, session, expected);
            Require(calls.Length > 100 && calls.Select(x => (x.epoch, x.frequency)).Distinct().Count() == 1 && calls[0].frequency > 0,
                "Prior acquisition lacks a single bound live media-clock timeline.");
            double threshold = start + 300 + (label == "T01" ? 1 : .25) + 10;
            long thresholdQpc = checked(calls[0].epoch + (long)Math.Ceiling(threshold * calls[0].frequency));
            Require(e.RootElement.GetProperty("completionStrictlyBeforeThreshold").GetBoolean()
                && e.RootElement.GetProperty("completionReadyQpc").GetInt64() == sample.Timing.CompletionReady
                && e.RootElement.GetProperty("thresholdQpc").GetInt64() == thresholdQpc
                && sample.Timing.CompletionReady > 0 && sample.Timing.CompletionReady < thresholdQpc
                && !calls.Any(x => x.qpc <= sample.Timing.CompletionReady && x.cutoff >= threshold), "Prior capture-continuation eligibility changed.");
            result.Add((sample, primary, paired));
        }
        Require(result.Select(r => r.Item2.AcquisitionIdentity).Distinct().Count() == 2
            && result.Select(r => r.Item1.AcquisitionSelectionHash).Distinct().Count() == 2, "Prior acquisitions are not independent.");
        return result;
    }


    static ReplayCalibrationSourceReceipt ReadBoundCompositeVariant(Sample s, string directory, string label,
        IReadOnlyDictionary<string, string> expected, string targetVolume, long targetUnit, int referenceSeconds = 300)
    {
        void Bound(string file) => Require(expected.ContainsKey(Path.GetFullPath(file)), "Required source evidence is not bound: " + file);
        ReplayExportProcessReceipt ReadProcess(string path)
        {
            Bound(path); using var d = JsonDocument.Parse(File.ReadAllText(path)); var v = d.RootElement;
            string stdout = v.GetProperty("stdout").GetString(), stderr = v.GetProperty("stderr").GetString(); Bound(stdout); Bound(stderr);
            return new() { ProcessId = v.GetProperty("ProcessId").GetInt32(), ProcessStartedUtc = v.GetProperty("ProcessStartedUtc").GetDateTime(),
                ExitCode = v.GetProperty("ExitCode").GetInt32(), ProducerAbsent = v.GetProperty("ProducerAbsent").GetBoolean(),
                ActualExitObserved = v.GetProperty("ActualExitObserved").GetBoolean(), TimedOut = v.GetProperty("TimedOut").GetBoolean(),
                OutputLimitExceeded = v.GetProperty("OutputLimitExceeded").GetBoolean(), CaptureComplete = v.GetProperty("CaptureComplete").GetBoolean(),
                Failure = v.GetProperty("Failure").GetString(), Error = v.GetProperty("Error").GetString(),
                ElapsedMilliseconds = v.GetProperty("ElapsedMilliseconds").GetInt64(), StandardOutput = File.ReadAllText(stdout), StandardError = File.ReadAllText(stderr) };
        }

        string receipt = Path.Combine(directory, "qualification-source.json"); Bound(receipt);
        var q = JsonSerializer.Deserialize<ReplayCalibrationSourceReceipt>(File.ReadAllText(receipt));
        Require(q.SampleHash == HashFile(Path.Combine(directory, "sample.json")), "Prior sample hash changed.");
        RequireExportEvidence(s, q);
        string nativeWitness = Path.Combine(directory, "native-export-witness.json"); Bound(nativeWitness);
        Require(q.NativeExport != null && q.NativeExport.EvidenceHash == HashFile(nativeWitness),
            "Prior native request ownership witness changed or is missing.");
        var current = ReplayStorage.Observe(s.File);
        Require(q.Volume == targetVolume && q.AllocationUnit == targetUnit
            && current.Volume == q.QualificationOutput.Volume && current.Identity == q.QualificationOutput.Identity
            && current.Length == q.QualificationOutput.Length && current.Length == s.FinalBytes,
            "Prior checked final file identity/bytes changed.");
        string decode = Path.Combine(directory, Path.GetFileName(s.File) + ".decode.json"); Bound(decode);
        Require(q.Decode.EvidenceHash == HashFile(decode), "Prior full decoder receipt changed.");
        using var process = JsonDocument.Parse(File.ReadAllText(decode));
        string stdout = process.RootElement.GetProperty("stdout").GetString(); Bound(stdout);
        var frames = Regex.Matches(File.ReadAllText(stdout), @"(?m)^frame=(\d+)\s*$");
        Require(frames.Count > 0 && frames[^1].Groups[1].Value == (referenceSeconds * 60).ToString(System.Globalization.CultureInfo.InvariantCulture)
            && File.ReadAllText(stdout).Contains("progress=end", StringComparison.Ordinal), "Prior independent decoder did not complete every duration-owned picture.");
        string pictures = Path.Combine(directory, Path.GetFileName(s.File) + ".source-pictures.json"); Bound(pictures);
        Require(q.SourcePicturesHash == HashFile(pictures), "Prior source-content observations changed.");
        if (s.ManagedExport != null)
        {
            string managed = Path.Combine(directory, "managed-export.json"); Bound(managed);
            Require(q.ManagedExport.EvidenceHash == HashFile(managed)
                && JsonSerializer.Serialize(s.ManagedExport, Json) == JsonSerializer.Serialize(JsonSerializer.Deserialize<ReplayManagedMuxReceipt>(File.ReadAllText(managed), Json), Json),
                "Prior managed closure changed.");
        }
        else
        {
            string runtime = Path.Combine(directory, "runtime-validation-exit.json"); Bound(runtime);
            using var runtimeDoc = JsonDocument.Parse(File.ReadAllText(runtime));
            var runtimeProcess = ReadProcess(runtime);
            Require(q.RuntimeValidation.EvidenceHash == HashFile(runtime)
                && ReplayReferenceModel.Hash(s.RuntimeProcess) == ReplayReferenceModel.Hash(runtimeDoc.RootElement)
                && s.RuntimeArguments.SequenceEqual(ReplayExactExport.DecodeArguments(s.File + ".partial.mp4", true, 60))
                && ReplayReferenceModel.Hash(s.RuntimeArguments) == q.RuntimeValidationArgumentsHash
                && ReplayExactExport.ReadValidation(runtimeProcess, s.Interval, 60).Frames == referenceSeconds * 60,
                "Prior normal full validation/strict arguments changed.");
        }
        string acquisition = s.Variant == "legacy" ? Path.GetDirectoryName(directory) : directory;
        string growthFile = Path.Combine(acquisition, "capture-growth.json"); Bound(growthFile);
        using var growth = JsonDocument.Parse(File.ReadAllText(growthFile)); var gv = growth.RootElement;
        Require(gv.GetProperty("ObservationMilliseconds").GetInt32() == ReplayCapacityPolicy.CoverageMilliseconds,
            "Prior growth evidence has no matching capacity observation horizon.");
        Bound(Path.Combine(acquisition, "capture-arguments.json")); Bound(Path.Combine(acquisition, "selection.jsonl"));
        Require(Directory.GetFiles(acquisition, "export-arguments-*.json").Any(path => expected.ContainsKey(path)
            && ReplayReferenceModel.Hash(JsonSerializer.Deserialize<string[]>(File.ReadAllText(path))) == q.ExportArgumentsHash),
            "Prior actual export argument fingerprint is absent.");
        string argsFile = Path.Combine(directory, Path.GetFileName(s.File) + ".decode-arguments.json"); Bound(argsFile);
        Require(JsonSerializer.Deserialize<string[]>(File.ReadAllText(argsFile)).SequenceEqual(InspectionArguments(directory, s.File, referenceSeconds)),
            "Prior independent strict decoder arguments changed.");
        Bound(Path.Combine(directory, Path.GetFileName(s.File) + ".barcodes.raw"));
        Bound(Path.Combine(directory, Path.GetFileName(s.File) + ".pictures.raw"));
        var inspection = ReadInspectionEvidence(ReadProcess(decode), directory, s.File, referenceSeconds, label, q.QualificationOutput, false);
        var rebuilt = QualificationSource(s, inspection, directory,
            (gv.GetProperty("PollGrowth").GetInt64(), gv.GetProperty("StopGrowth").GetInt64(), gv.GetProperty("Volume").GetString()),
            gv.GetProperty("MaximumActiveGap").GetDouble(), gv.GetProperty("MaximumCapacityGap").GetDouble());
        Require(ReplayReferenceModel.Hash(q) == ReplayReferenceModel.Hash(rebuilt),
            "Prior typed source/managed/index/arguments/decoder/audio/picture receipt disagrees with its original raw evidence.");
        return q;
    }
    static bool CompositeElapsedMatches(double declared, double actual) => double.IsFinite(actual) && actual > 0
        && double.IsFinite(declared) && declared > 0 && declared < 5400 && declared == Math.Ceiling(actual);

    static (long qpc, double cutoff, long epoch, long frequency)[] ReadCompositeClockCalls(
        string root, string session, IReadOnlyDictionary<string, string> expected, int maximumRotations = 8, long maximumBytes = 16_777_216)
    {
        // Read the original bound rotations without joining or rewriting files.
        var logs = expected.Keys.Where(file => Path.GetDirectoryName(file).Equals(root, StringComparison.OrdinalIgnoreCase)
            && (Path.GetFileName(file).Equals("recorder.log", StringComparison.OrdinalIgnoreCase)
                || Regex.IsMatch(Path.GetFileName(file), @"^recorder\.log\.[0-9]{18,19}\.old$", RegexOptions.CultureInvariant)))
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        Require(logs.Length >= 1 && logs.Length <= maximumRotations && logs.Contains(Path.Combine(root, "recorder.log"), StringComparer.OrdinalIgnoreCase),
            "A bounded complete original clock-log set is required.");
        var present = Directory.EnumerateFiles(root, "recorder.log*", SearchOption.TopDirectoryOnly)
            .Where(file => Path.GetFileName(file).Equals("recorder.log", StringComparison.OrdinalIgnoreCase)
                || Regex.IsMatch(Path.GetFileName(file), @"^recorder\.log\.[0-9]{18,19}\.old$", RegexOptions.CultureInvariant))
            .Take(maximumRotations + 1).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        Require(present.SequenceEqual(logs, StringComparer.OrdinalIgnoreCase), "Every original clock rotation must be bound.");
        long bytes = 0; var calls = new List<(long qpc, double cutoff, long epoch, long frequency)>();
        foreach (string file in logs)
        {
            long length = new FileInfo(file).Length; bytes = checked(bytes + length);
            Require(length is >= 0 and <= 4_194_304 && bytes <= maximumBytes && HashFile(file) == expected[file],
                "Original clock evidence changed or exceeds its bounded read envelope.");
            calls.AddRange(Regex.Matches(File.ReadAllText(file), @"Replay save trigger: QPC=(\d+); session=" + Regex.Escape(session)
                + @"; cutoff=([0-9.eE+-]+); timeline=media-clock; epoch=(\d+); frequency=(\d+)")
                .Select(m => (long.Parse(m.Groups[1].Value), double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture),
                    long.Parse(m.Groups[3].Value), long.Parse(m.Groups[4].Value))));
        }
        return calls.OrderBy(call => call.qpc).ToArray();
    }

    static bool CompositeIndexValid(ReplayCalibrationIndexReceipt index, int inputs, int referenceSeconds)
        => ReplayCalibrationCatalog.IndexAcquisitionValid(index, inputs, referenceSeconds / 60);
    static void RequireCompositePair(Sample main, Sample legacy, ReplayCalibrationSourceReceipt q, ReplayCalibrationSourceReceipt lq,
        int referenceSeconds = 300, double maximumActiveGap = 500)
    {
        Require(main.LegacyOutput != null && JsonSerializer.Serialize(main.LegacyOutput, Json) == JsonSerializer.Serialize(legacy, Json)
            && main.Label == legacy.Label && main.Label == q.Label && q.Label == lq.Label
            && main.Scope == legacy.Scope && q.Scope == main.Scope && lq.Scope == main.Scope
            && main.Interval == legacy.Interval && main.Interval.Seconds == referenceSeconds && main.InputBytes == legacy.InputBytes
            && main.AcquisitionSelectionHash == legacy.AcquisitionSelectionHash && !string.IsNullOrWhiteSpace(main.AcquisitionSelectionHash)
            && q.SelectionHash == main.AcquisitionSelectionHash && lq.SelectionHash == main.AcquisitionSelectionHash
            && q.RequestSelectionHash == main.SelectionIdentity && lq.RequestSelectionHash == legacy.SelectionIdentity
            && main.SelectionIdentity != legacy.SelectionIdentity && q.AcquisitionIdentity == lq.AcquisitionIdentity
            && q.QualificationOutput.Identity != lq.QualificationOutput.Identity,
            "Prior pair changed its interval, acquisition or independent snapshot selections.");
        foreach (var item in new[] { (s: main, r: q), (s: legacy, r: lq) })
        {
            var r = item.r; var s = item.s;
            Require(r.Closed && r.AllTracksDecoded && r.SourceLineageVerified && r.NonSilentAudio && r.ReferenceVideoFormat && r.ReferenceAudioFormat
                && double.IsFinite(r.AudioRmsDb) && r.AudioRmsDb <= 0
                && r.VideoWidth == 1920 && r.VideoHeight == 1080 && r.AudioSampleRate == 48000 && r.AudioChannels == 2
                && r.InputBytes == s.InputBytes && r.FinalBytes == s.FinalBytes && r.FinalAllocation == s.FinalAllocation && r.PeakAllocation == s.PeakAllocation
                && r.OutputStart == s.Interval.Start && r.OutputEnd == s.Interval.End && r.ExportSeconds == referenceSeconds && s.ExportSeconds == referenceSeconds
                && r.MaximumActiveGapMs > 0 && r.MaximumActiveGapMs <= maximumActiveGap && r.CapturePollGrowthBytes > 0 && r.CaptureStopGrowthBytes >= 0
                && s.NormalValidation is { PublicationIdentityBound: true, CheckedOutput: not null }
                && r.NormalValidation == s.NormalValidation && r.QualificationOutput == s.NormalValidation.CheckedOutput
                && r.NativeExport == s.NativeExport
                && r.NativeExport is { OwnedRequest: true, EligibleAtExport: true, ScopeClosed: true, EvidenceHash.Length: 64 }
                && r.NativeExport.PolicyIdentity == ReplaySaveExport.PolicyIdentity
                && r.NativeExport.PipelineSelected == (s.Variant == "packet")
                && new[] { r.Capture, r.Source, r.Export, r.Decode }.All(p => p is
                    { ProducerAbsent: true, ActualExitObserved: true, ExitCode: 0, TimedOut: false, OutputLimitExceeded: false, ForcedTermination: false }),
                "Prior pair lacks its original complete source/output/closure qualification.");
        }
        Require(main.NormalValidation is { Kind: "SourcePacketIntegrity", Frames: 0, AudioPackets: > 0, SourceProofHash.Length: 64 }
            && main.NormalValidation.CodedPictures == referenceSeconds * 60
            && main.NormalValidation.ExportRecipe == ReplaySaveExport.Recipe && main.RuntimeProcess == null && q.RuntimeValidation == null
            && q.ManagedExport is { Closed: true, InputHashesComplete: true, State: "Completed" }
            && CompositeIndexValid(q.ManagedExport.Index,q.SegmentCount,referenceSeconds)
            && q.ManagedExport.Output == main.NormalValidation.CheckedOutput
            && legacy.NormalValidation is { Kind: "FullDecode" } && legacy.NormalValidation.Frames == referenceSeconds * 60
            && legacy.NormalValidation.ExportRecipe == ReplayExactExport.Recipe
            && lq.RuntimeValidation is { ProducerAbsent: true, ActualExitObserved: true, ExitCode: 0, TimedOut: false, OutputLimitExceeded: false, ForcedTermination: false },
            "Prior packet proof or separate normal/qualification full decode is incomplete.");
    }

    internal static int CheckCompositeTraining(string root, string planFile, string expectedPlanHash, string mode)
    {
        Require(mode == "metadata-only" && HashFile(planFile) == expectedPlanHash && !Directory.Exists(root), "Fresh exact metadata-only audit required.");
        using var document = JsonDocument.Parse(File.ReadAllText(planFile));
        var plan = document.RootElement.GetProperty("plan").Deserialize<CalibrationPlan>(); ValidateResources(plan);
        foreach (var file in plan.Files) Require(HashFile(file.Key) == file.Value, "Audit binding changed.");
        MediaDependency.RootDirectory = plan.EngineRoot;
        using var owned = PilotStorageObservation.Create(root);
        using var retainedOwnership = DurationTrainingContinuation(plan)
            ? new FrozenCalibrationHistory(plan.RetainedRoots, plan.FileLimit, plan.DirectoryLimit) : null;
        var rows = new List<object>();
        if (CapacityTrainingContinuation(plan) || DurationTrainingContinuation(plan))
        {
            var training = DurationTrainingContinuation(plan) ? ReadCompleteDurationTraining(plan) : ReadCompleteCapacityTraining(plan);
            int seconds = DurationTrainingContinuation(plan) ? 1800 : 300;
            double gap = DurationTrainingContinuation(plan) ? ReplayCapacityPolicy.CoverageMilliseconds : 500;
            for (int index = 0; index < training.Samples.Length; index++)
            {
                var main = training.Samples[index]; var legacy = main.LegacyOutput;
                var q = training.Primary[index]; var lq = training.Legacy[index];
                void Positive(string name, Action run) { run(); rows.Add(new { name = main.Label + ":" + name, result = "passed" }); }
                void Negative(string name, Action run)
                { try { run(); } catch (IOException) { rows.Add(new { name = main.Label + ":" + name, result = "passed" }); return; } throw new IOException("Bad retained proof accepted: " + name); }
                Positive("complete-pair", () => RequireCompositePair(main, legacy, q, lq, seconds, gap));
                Negative("changed-acquisition", () => RequireCompositePair(main, legacy with { AcquisitionSelectionHash = "other" }, q, lq, seconds, gap));
                Negative("missing-full-qualification", () => RequireCompositePair(main, legacy, q with { AllTracksDecoded = false }, lq, seconds, gap));
                Negative("unclosed-index-readers", () => RequireCompositePair(main, legacy,
                    q with { ManagedExport = q.ManagedExport with { Index = q.ManagedExport.Index with { SourceHandlesClosed = false } } }, lq, seconds, gap));
            }
            Write(Path.Combine(root, "verification.json"), new { result = "passed", rows, modelRevision = training.Model.Revision,
                interpretation = "Three exact current-v4 completed training pairs, reconstructed unchanged frozen model and adversarial metadata controls only; no capture, codec, account catalog or installed-state mutation." });
            return 0;
        }
        var pairs = ReadCompositeTraining(plan);
        foreach (var pair in pairs)
        {
            var main = pair.Sample; var legacy = main.LegacyOutput; var q = pair.Primary; var lq = pair.Legacy;
            void Positive(string name, Action run) { run(); rows.Add(new { name = main.Label + ":" + name, result = "passed" }); }
            void Negative(string name, Action run)
            { try { run(); } catch (IOException) { rows.Add(new { name = main.Label + ":" + name, result = "passed" }); return; } throw new IOException("Bad retained proof accepted: " + name); }
            Positive("complete-pair", () => RequireCompositePair(main, legacy, q, lq));
            Negative("packet-is-not-a-full-normal-decode", () => RequireCompositePair(main, legacy, q with { RuntimeValidation = q.Decode }, lq));
            var missingProof = main.NormalValidation with { SourceProofHash = null };
            Negative("missing-packet-proof", () => RequireCompositePair(main with { NormalValidation = missingProof }, legacy, q with { NormalValidation = missingProof }, lq));
            Negative("missing-full-qualification", () => RequireCompositePair(main, legacy, q with { AllTracksDecoded = false }, lq));
            Negative("missing-legacy-normal-decode", () => RequireCompositePair(main, legacy, q, lq with { RuntimeValidation = null }));
            Negative("changed-acquisition", () => RequireCompositePair(main, legacy with { AcquisitionSelectionHash = "other" }, q, lq));
            Negative("changed-request-snapshot", () => RequireCompositePair(main, legacy, q with { RequestSelectionHash = lq.RequestSelectionHash }, lq));
            Negative("unbound-publication", () => RequireCompositePair(main with { NormalValidation = main.NormalValidation with { PublicationIdentityBound = false } }, legacy, q, lq));
            Negative("unclosed-index-readers", () => RequireCompositePair(main, legacy, q with { ManagedExport = q.ManagedExport with { Index = q.ManagedExport.Index with { SourceHandlesClosed = false } } }, lq));
            Negative("noncancellable-index", () => RequireCompositePair(main, legacy, q with { ManagedExport = q.ManagedExport with { Index = q.ManagedExport.Index with { CancellableReads = false } } }, lq));
            Negative("wrong-read-deadline", () => RequireCompositePair(main, legacy, q with { ManagedExport = q.ManagedExport with { Index = q.ManagedExport.Index with { ReadDeadlineMilliseconds = 0 } } }, lq));
            Negative("unfinished-sources", () => RequireCompositePair(main, legacy, q with { ManagedExport = q.ManagedExport with { Index = q.ManagedExport.Index with { FinishedSources = 0 } } }, lq));
            Negative("unsupported-source", () => RequireCompositePair(main, legacy, q with { ManagedExport = q.ManagedExport with { Index = q.ManagedExport.Index with { UnsupportedSources = 1 } } }, lq));
            Negative("worker-overlap-outside-policy", () => RequireCompositePair(main, legacy, q with { ManagedExport = q.ManagedExport with { Index = q.ManagedExport.Index with { PeakActive = 5 } } }, lq));
            Negative("different-managed-output", () => RequireCompositePair(main, legacy, q with { ManagedExport = q.ManagedExport with { Output = q.ManagedExport.Output with { Identity = "other" } } }, lq));
            Negative("missing-legacy-pair", () => RequireCompositePair(main with { LegacyOutput = null }, legacy, q, lq));
        }
        Write(Path.Combine(root, "verification.json"), new { result = "passed", rows,
            interpretation = "Two same-product retained pairs and adversarial metadata controls only; no capture, codec, model import, account catalog or installed-state mutation." });
        return 0;
    }
}
