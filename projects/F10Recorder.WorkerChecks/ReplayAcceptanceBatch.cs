using System.Text.Json;

namespace F10Recorder;

// Prospective orchestration only. The unchanged qualified product still owns
// admission, capture, selection, publication, runtime validation and stopping.
internal static class ReplayAcceptanceBatch
{
    internal const long Hard = 31_000_000_000, Early = 30_500_000_000, Floor = 60_000_000_000;
    static readonly double[] Phases = [.10, .60, .30, .80, .50, .00, .70, .20, .90, .40];
    static void Need(bool value, string reason) { if (!value) throw new IOException(reason); }
    internal static int Prepare(string planFile, string root, string engine, string product, string production, string catalog,
        string ui, string uiPlan, string uiResult, string hardwareFile)
    {
        string artifacts = Path.GetFullPath("artifacts") + Path.DirectorySeparatorChar;
        foreach (string path in new[] { planFile, root, engine, product, production, catalog, ui, uiPlan, uiResult, hardwareFile }) Need(Path.IsPathFullyQualified(path), "Absolute batch paths required.");
        Need(root.StartsWith(artifacts, StringComparison.OrdinalIgnoreCase) && planFile.StartsWith(artifacts, StringComparison.OrdinalIgnoreCase)
            && !Directory.Exists(root) && !File.Exists(root) && !File.Exists(planFile), "Fresh ignored batch roots required.");
        Need(ReplayWorkerLatencyPilot.Hash(uiPlan) == "CFEECB668B25EFE92AC18E1FEA733C0AE597F2AF7B8709965B1F106789486F8A"
            && ReplayWorkerLatencyPilot.Hash(uiResult) == "1A3CFD0DDBDCD20063EE4193D63D00B58E302B09557F37B40DBA83AEA5B709CC", "Exact passed no-acquisition UI attachment/probe-path evidence required.");
        Need(new FileInfo(hardwareFile).Length is > 0 and <= 65536, "Bounded prospective hardware metadata required.");
        using var hardware = JsonDocument.Parse(File.ReadAllBytes(hardwareFile)); var machine = hardware.RootElement;
        Need(machine.GetProperty("logicalProcessors").GetInt32() == Environment.ProcessorCount
            && machine.GetProperty("machine").GetString() == Environment.MachineName
            && DateTime.UtcNow < machine.GetProperty("utc").GetDateTime().AddHours(4), "Current matching hardware snapshot required.");
        int diskNumber = machine.GetProperty("destinationDisk").GetProperty("Number").GetInt32();
        Need(machine.GetProperty("physicalDisks").EnumerateArray().Any(d => d.GetProperty("DeviceId").GetString() == diskNumber.ToString()
            && d.GetProperty("MediaType").GetString() == "SSD"), "Actual reference destination must be an SSD.");
        using var prior = JsonDocument.Parse(File.ReadAllBytes(uiPlan)); var bindings = prior.RootElement.GetProperty("files");
        foreach (string file in Directory.GetFiles(ui, "*", SearchOption.AllDirectories))
            Need(bindings.TryGetProperty(file, out var hash) && ReplayWorkerLatencyPilot.Hash(file) == hash.GetString(), "UI must remain exact tested bytes: " + file);
        string preservation = Path.Combine(Path.GetDirectoryName(uiPlan), "preservation-after.json");
        using var priorPreservation = JsonDocument.Parse(File.ReadAllBytes(preservation));
        var productionFiles = priorPreservation.RootElement.GetProperty("files").EnumerateArray().Select(file =>
        {
            string path = file.GetProperty("path").GetString(); bool exists = File.Exists(path);
            string sha = exists ? ReplayWorkerLatencyPilot.Hash(path, true) : null;
            Need(file.GetProperty("unchanged").GetBoolean() && exists == file.GetProperty("exists").GetBoolean()
                && sha == file.GetProperty("sha256").GetString(), "Production state changed before batch preparation: " + path);
            return new { path, exists, sha256 = sha };
        }).ToArray();
        Need(productionFiles.Length == 12, "All12 production preservation bindings required.");
        MediaDependency.RootDirectory = engine;
        var qualified = ReplayCalibrationCatalog.LoadFrom(catalog); Need(qualified.Model?.CaptureHeadroom != null, qualified.Reason ?? "Qualified model required.");
        long qualifiedSealedUpper = checked(2 * ReplayReferenceModel.Round(qualified.Model.FinalBytes(ReplayReferenceModel.MaximumInputBytes), qualified.Model.AllocationUnit) + 335_544_320);
        // Conditional experiment retention limit, NOT a new calibrated product
        // bound. A breach keeps the full12GB current slot and fails the WHOLE
        // experiment; it can never be skipped/substituted or permit another pair.
        long sealedPair = 2_000_000_000;
        long forward = checked(9 * sealedPair + 12_000_000_000 + 134_217_728);
        Need(forward < Early && qualified.Model.CaptureHeadroom.PollGrowthBytes + qualified.Model.CaptureHeadroom.StopGrowthBytes + 335_544_320 < Hard - Early,
            "Ten retained pairs plus one complete12GB in-flight/failed slot, reports and stop reserve must fit BEFORE recording.");
        Need(ReplayStorage.ObserveCapacity(Path.GetDirectoryName(root)).AvailableBytes >= Floor + Hard + 1_000_000_000,
            "31GB entire batch +1GB preparation/report promise +60GB free floor required before preparation.");
        string admission = Path.GetDirectoryName(planFile); Directory.CreateDirectory(admission);
        var batch = new { masterPlan = planFile, root, admission, hardwareFile, budgetBytes = Hard, stopBytes = Early, minimumFreeBytes = Floor,
            maximumFiles = 16384, maximumDirectories = 2048, sealedPairBytes = sealedPair };
        var pairs = new List<object>();
        for (int i = 0; i < 10; i++)
        {
            string pair = "pair-" + (i + 1).ToString("00"), pairPlan = Path.Combine(admission, pair, "plan.json"), pairRoot = Path.Combine(root, pair);
            ReplayWorkerLatencyPilot.Prepare(pairPlan, pairRoot, engine, product, production, catalog, ui, i + 1, Phases[i], batch, hardwareFile);
            pairs.Add(new { number = i + 1, phase = Phases[i], plan = pairPlan, sha256 = ReplayWorkerLatencyPilot.Hash(pairPlan), root = pairRoot });
        }
        ReplayWorkerLatencyPilot.Write(planFile, new { kind = "issue83-acceptance-ten-pairs-v2", root, admission, pairs,
            maximumAcquisitions = 10, maximumCaptureSecondsPerPair = 580, maximumTotalCaptureSeconds = 5800, deadlineSeconds = 9060,
            maximumProcessesIncludingPreparationAndController = 141, maximumConcurrentProcesses = 14,
            budgetBytes = Hard, stopBytes = Early, minimumFreeBytes = Floor, preparationBudgetBytes = 1_000_000_000,
            expectedForwardBytes = forward, sealedPairBytes = sealedPair, failedPairPromiseBytes = 12_000_000_000,
            qualifiedSealedUpperBytes = qualifiedSealedUpper,
            sealedPairRule = "Prospective experiment STOP at2,000,000,000 complete root+admission bytes. After closure, either verify ALL retained source/output/profile/log/resource/decoder/report bytes<=2GB and proceed, or retain entire current full12GB failed slot and FAIL WHOLE experiment immediately. No next capture/substitution/exclusion/20-success quantile. This is NOT a qualified2GB product-output bound; native model/perpair12GB envelope remains unchanged.",
            observerVersion = "retained-kernel-image-and-bounded-three-sample-wildcard-refresh-v2",
            maximumFiles = 16384, maximumDirectories = 2048, batchReportsBytes = 134_217_728,
            coldWarm = "Maintainer-selected: ten fresh worker-host processes, first export use then repeat use in each; no OS-cache flush, reboot, driver-cold or independent20-source claim.",
            statistics = new { sampleCount = 20, median = "Sorted positions10/11 arithmetic mean", p95 = "Nearest rank ceil(.95*20)=position19",
                secondary = "Ten first-use and ten repeat-use values, medianmean5/6 and nearest-rankp95position10, paired differences. Ten independent worker processes,20 correlated observations. No invented confidence interval.",
                medianThresholdMilliseconds = 3000, p95ThresholdMilliseconds = 5000,
                failures = "First invalid pair stops entire batch. Preserve all attempts/slow/failed values and full12GB failed promise. Fewer than20 successful matched exports cannot pass. No retries/substitutions in this batch." },
            resourceMethod = "Frozen tests/replay-acceptance-accounting.md defines roles/windows/monotonicCPU+IObounds/RAMcoverage/GPU-N-A and decisions. Combined recorder=workerhost+UI+capture/export/staging/allpreSavednormalvalidators. Fixture/console/controller+sampler separate. Later supplementaldecodes separate; no causal/gamingclaim.",
            resources = new { hiddenSettingsRamBytes = 1_000_000_000, averageTotalMachineCpuPercent = 5, samplerReportMaximumBytes = 33_554_432,
                sampleIntervalSeconds = 1, maximumSamplerGapSeconds = 5, observerCost = "Record controller CPU/RAM and actualCIM costs; worker host includes helper guard/polling. Included in latency, no subtraction/comparisoncausality." },
            source = "Same qualified H02 full-screen moving fixture+quietchirp; primary1920x1080/60fps/h26420Mbps/default48k2channel systemaudio. Each pair needs owned foreground handoff observed and explicitly released before capture; no UI automation duringcapture.",
            ui, uiPlan, uiResult, hardwareFile, hardwareSha256 = ReplayWorkerLatencyPilot.Hash(hardwareFile), hardware = machine,
            productionFiles, createdUtc = DateTime.UtcNow, startup = ReplayWorkerLatencyPilot.Startup(),
            boundaries = "Separate preparation1GB+batch31GB; preserve every older media/fullpromise unchanged, including failed1835F4ED whole35GB cap/full12GB failed slot and its two prior Saved observations, which are NOT imported into this fresh10pair cohort. No install, accountcatalogactivation, startup/vendor/game/settings change, public issue edit, push/merge/release. This plan alone does NOT authorize ten additional live captures. Need direct recording authorization +independent exact-planreview beforeRun." });
        Console.WriteLine("BATCH " + planFile + " " + ReplayWorkerLatencyPilot.Hash(planFile)); return 0;
    }
}
