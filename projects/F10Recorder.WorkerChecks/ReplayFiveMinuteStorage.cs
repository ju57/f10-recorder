using System.Text.Json;

namespace F10Recorder;

// Prospective experiment on one acquisition. None of these records is an
// account catalog input or grants permission to launch a capture.
internal static partial class ReplayCalibrationRun
{
    internal const string FiveMinuteStorageAdmission = "issue83-C00-five-minute-queue-storage-v1";
    internal const string FiveMinuteContractRole = "five-minute-storage-contract";
    const string QueueImage = "4A1AF73D7E64DCA262A435A72D2D292FDBAB877BAE786036169C497DFEA9076A";
    const long FiveMinutePollLimit = 32_833_536, FiveMinuteStopLimit = 19_820_544;
    // A file can change anywhere between two scans. Include the intersecting
    // edge interval. A disappearance never cancels another object's increase.
    sealed class FiveMinuteGrowthJournal
    {
        readonly List<(double Start, double End)> scans = [];
        readonly List<(int Scan, int Identity, long Bytes)> changes = [];
        readonly Dictionary<string, int> ids = new(StringComparer.OrdinalIgnoreCase);
        sealed record Prefix(int CompletedScans, long Total);
        sealed record Stop(double RequestedMs, Prefix Prefix);
        Prefix published = new(0,0);
        Stop stop;
        internal void Publish(long total) => Volatile.Write(ref published, new(scans.Count,total));
        internal long MarkStop(double now)
        {
            var mark = new Stop(now, Volatile.Read(ref published));
            return (Interlocked.CompareExchange(ref stop, mark, null) ?? mark).Prefix.Total;
        }
        internal bool HasChanges => changes.Count > 0;
        internal double Observe(double start, double end, Dictionary<string,long> previous, Dictionary<string,long> current)
        {
            Require(scans.Count < 18_002 && end >= start && (scans.Count == 0 || start >= scans[^1].End),
                "Growth journal scan order or capacity exceeded.");
            double earliest = scans.Count == 0 ? start : scans[^1].Start;
            foreach (var (id, bytes) in current)
                if (!previous.TryGetValue(id, out var before) || before != bytes) Change(id, bytes);
            foreach (var id in previous.Keys)
                if (!current.ContainsKey(id)) Change(id, -1);
            scans.Add((start,end));
            return earliest;
            void Change(string id, long bytes)
            {
                Require(changes.Count < 32_768 && id.Length <= 192, "Growth journal change bound exceeded.");
                if (!ids.TryGetValue(id, out int index))
                {
                    Require(ids.Count < 2048, "Growth journal identity bound exceeded.");
                    ids.Add(id,index = ids.Count);
                }
                changes.Add((scans.Count,index,bytes));
            }
        }
        internal void WriteTo(string cell, long stopPrefix, bool partial = false)
        {
            Require(partial || scans.Count > 0 && changes.Count > 0 && stopPrefix >= 0 && stop?.Prefix.Total == stopPrefix
                && scans[^1].Start >= stop.RequestedMs, "Growth observations or terminal scan after stop missing.");
            // Compact arrays bound the complete retained journal to 4 MiB.
            var journal = new { partial, stopPrefix, stopAtMs = stop?.RequestedMs, stopAfterScans = stop?.Prefix.CompletedScans,
                identities = ids.OrderBy(i => i.Value).Select(i => i.Key),
                scans = scans.Select(s => new[] { s.Start,s.End }),
                changes = changes.Select(c => new long[] { c.Scan,c.Identity,c.Bytes }) };
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(journal);
            Require(bytes.Length <= 4_194_304, "Growth journal exceeds its evidence budget.");
            string name = partial ? "native-allocation-growth-incomplete.json" : "native-allocation-growth.json";
            using var output = new FileStream(Path.Combine(cell, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            output.Write(bytes); output.Flush(true);
        }
    }
    sealed record FiveMinuteContract(string Revision, string Exporter, ReplayQualificationContext Context,
        ReplayHostFacts Host, string Volume, long AllocationUnit, long PollGrowthBytes, long StopGrowthBytes,
        long FinalResidualBytes, long FinalizationExtraBytes, int ObservationMilliseconds);

    static bool FiveMinuteStorage(CalibrationPlan plan) => plan?.FreshCandidateAdmission == FiveMinuteStorageAdmission;
    static bool FiveMinuteShape(CalibrationPlan plan) => plan.StopBytes == 12_000_000_000
        && plan.BudgetBytes == 13_000_000_000 && plan.RetainedBudgetBytes == 1_000_000_000
        && plan.EvidenceRoles.ContainsKey(FiveMinuteContractRole) && plan.EvidenceRoles.ContainsKey(PresentationMethod.Role)
        && !plan.EvidenceRoles.ContainsKey("coverage-freeze");

    internal static int FiveMinuteStorageChecks()
    {
        CheckReopenedOracleOutput();
        var plan = ContextFixturePlan() with { FreshCandidateAdmission = FiveMinuteStorageAdmission,
            MaximumChildren = 16, ValidationChildren = 6, OfflineDeadlineSeconds = 900,
            EvidenceRoles = new() { ["fresh-candidate-qualification"]="candidate",
                [FiveMinuteContractRole]="experiment", [PresentationMethod.Role]="display" } };
        Require(ContextEnvelopeValid(plan) && DurationEnvelopeValid(plan) && SameCaptureQualification(plan)
            && OwnedHistoryPreparationKind(plan.FreshCandidateAdmission), "Scoped C00 cannot enter the paired collector.");
        foreach (var invalid in new[] { plan with { ReplayMinutes = 1 }, plan with { ReplayMinutes = 30 },
            plan with { BudgetBytes = plan.BudgetBytes + 1 }, plan with { MaximumChildren = 17 },
            plan with { Order = ["H03","H03"] }, plan with { FreshCandidateAdmission = SameCaptureContextAdmission },
            plan with { EvidenceRoles = new(plan.EvidenceRoles) { ["coverage-freeze"] = "old" } } })
            Require(!ContextEnvelopeValid(invalid), "Changed C00 scope was admitted.");
        var noDisplay = new Dictionary<string,string>(plan.EvidenceRoles);
        noDisplay.Remove(PresentationMethod.Role);
        Require(!ContextEnvelopeValid(plan with { EvidenceRoles=noDisplay }), "Missing display reference was admitted.");
        Require(ExpectedCaptureTemplate(plan,"0").Single(a => a.StartsWith("ddagrab=")).StartsWith("ddagrab=f10_queue=1:", StringComparison.Ordinal)
            && !CaptureTemplate("0").Any(a => a.Contains("f10_queue")), "Capture graph selected for the wrong admission.");
        var journal = new FiveMinuteGrowthJournal();
        Dictionary<string,long> before = new() { ["gone"] = 16384 }, after = new() { ["other"] = 4096 };
        Require(journal.Observe(0,30,[],before) == 0, "First native scan edge changed.");
        journal.Publish(16384);
        Require(journal.MarkStop(55) == 16384 && journal.Observe(100,131,before,after) == 0,
            "Stop prefix or conservative window edge changed.");
        journal.Publish(20480);
        Require(journal.MarkStop(150) == 16384 && journal.Observe(200,239,after,after) == 100 && journal.HasChanges,
            "Repeated stop or native journal edge regressed.");
        Console.WriteLine("Passed five-minute-only C00 scope, queue selection, closed authorization domains and native growth prefix/edge checks.");
        return 0;
    }

    static void CheckReopenedOracleOutput()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, [1, 2, 3, 4]);
            var extent = ReplayStorage.Observe(path);
            string hash = HashFile(path);
            var preClose = extent with { AllocatedBytes = extent.AllocatedBytes + extent.AllocationUnit };
            using (OpenSameOracleOutput(path, preClose, hash)) { }
            void Reject(ReplayFileExtent original)
            {
                try { using var unexpected = OpenSameOracleOutput(path, original, hash); }
                catch (IOException) { return; }
                throw new IOException("Reopened qualification output accepted changed identity, length or content.");
            }
            Reject(preClose with { Identity = "other" });
            Reject(preClose with { Length = extent.Length + 1 });
            Reject(preClose with { AllocationUnit = extent.AllocationUnit * 2 });
            Reject(preClose with { Volume = "other" });
            File.WriteAllBytes(path, [1, 2, 3, 5]);
            Reject(preClose);
        }
        finally { File.Delete(path); }
    }

    static string[] ExpectedCaptureTemplate(CalibrationPlan plan, string primary)
    {
        var arguments = CaptureTemplate(primary);
        if (FiveMinuteStorage(plan) || plan?.FreshCandidateAdmission == OwnedHistoryAdmission
            && string.Equals(MediaDependency.ExecutableSha256, QueueImage, StringComparison.OrdinalIgnoreCase))
        {
            int graph = Array.FindIndex(arguments, a => a.StartsWith("ddagrab=", StringComparison.Ordinal));
            arguments[graph] = arguments[graph].Replace("ddagrab=", "ddagrab=f10_queue=1:", StringComparison.Ordinal);
        }
        return arguments;
    }

    static FiveMinuteContract ReadFiveMinuteContract(CalibrationPlan plan)
    {
        Require(ContextEnvelopeValid(plan), "Five-minute storage experiment shape changed.");
        string path = EvidenceRole(plan, FiveMinuteContractRole);
        using var json = JsonDocument.Parse(ReadCoverageMetadata(path, plan.Files[path]));
        RequireUniqueJsonMembers(json.RootElement);
        var c = json.RootElement.Deserialize<FiveMinuteContract>();
        Require(c?.Revision == FiveMinuteStorageAdmission && c.Exporter == ReplayReferenceModel.ExporterIdentity
            && c.Exporter.StartsWith(QueueImage + "/", StringComparison.OrdinalIgnoreCase)
            && c.Context?.Valid == true && c.Context.Arguments.SequenceEqual(ExpectedCaptureTemplate(plan, "0")
                .Select(a => a.Replace("<clock>", "<common-clock-filter>", StringComparison.Ordinal)))
            && c.Host?.Valid() == true && c.Host == ReplayCalibrationCatalog.CaptureHostFacts()
            && c.AllocationUnit == 4096 && !string.IsNullOrEmpty(c.Volume)
            && c.ObservationMilliseconds == ReplayCapacityPolicy.CoverageMilliseconds
            && c.PollGrowthBytes == FiveMinutePollLimit && c.StopGrowthBytes == FiveMinuteStopLimit
            && c.FinalResidualBytes == 0 && c.FinalizationExtraBytes == 2_211_840,
            "Five-minute operating context or prospective guarded limits changed.");
        var destination = ReplayStorage.ObserveCapacity(Path.GetDirectoryName(plan.Root));
        Require(c.Volume == destination.Volume && c.AllocationUnit == destination.AllocationUnit,
            "Five-minute capture and output must use the reviewed native volume and allocation unit.");
        return c;
    }

    static void WriteFiveMinuteResult(CalibrationPlan plan, FiveMinuteContract contract, string planHash,
        string observationHash, DateTime started, ReplayCalibrationSourceReceipt packet,
        ReplayCalibrationSourceReceipt exact, SameOracleResult oracle)
    {
        var completed = DateTime.UtcNow;
        bool Fresh(ReplayCalibrationProcessReceipt process) => ReplayCalibrationCatalog.IdentifiedProcess(process)
            && process.StartedUtcTicks >= started.Ticks && process.StartedUtcTicks <= completed.Ticks;
        Require(Fresh(packet.Capture) && Fresh(packet.Source) && packet.Label == "H03", "Expected fresh H03 acquisition and fixture.");
        ReplayCalibrationCatalog.ValidatePair(packet, exact);
        // Reuse the product's checked arithmetic only. This object stays in
        // memory and is never offered to reservation, Resume or admission.
        var limits = new ReplayReferenceModel(contract.Revision, contract.Context.Scope(contract.Exporter, 5),
            contract.Volume, contract.AllocationUnit, contract.FinalResidualBytes, contract.FinalizationExtraBytes, []);
        ReplayCalibrationSourceReceipt[] outputs = [packet, exact];
        foreach (var output in outputs)
        {
            ReplayCalibrationCatalog.ValidateOutput(output, limits, output.MaximumActiveGapMs);
            Require(output.CaptureVolume == limits.Volume && output.CaptureObservationMilliseconds == contract.ObservationMilliseconds
                && output.MaximumCapacityGapMs > 0 && output.MaximumCapacityGapMs <= contract.ObservationMilliseconds
                && output.CapturePollGrowthBytes >= 0 && output.CapturePollGrowthBytes <= contract.PollGrowthBytes
                && output.CaptureStopGrowthBytes >= 0 && output.CaptureStopGrowthBytes <= contract.StopGrowthBytes
                && output.CombinedPeakAllocation.HasValue && limits.CoversObservations(output.InputBytes, output.FinalBytes,
                    output.FinalAllocation, output.CombinedPeakAllocation.Value), "Observed growth or output exceeds the predeclared experiment bounds.");
        }
        var children = outputs.SelectMany(o => o.ExportProcesses.Append(o.Decode)
            .Concat(o.RuntimeValidation == null ? [] : new[] { o.RuntimeValidation })).ToArray();
        Require(children.All(Fresh) && children.Select(p => (p.ProcessId,p.StartedUtcTicks)).Distinct().Count() == children.Length
            && children.Select(p => p.EvidenceHash).Distinct().Count() == children.Length, "Output producer or decoder provenance changed.");
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        Require(packet.ManagedExport.ParentPid == self.Id && packet.ManagedExport.ParentStartedUtcTicks == self.StartTime.ToUniversalTime().Ticks,
            "Packet mux was not closed by the admitted collector.");
        string cell = Path.Combine(plan.Root, "H03");
        string inputMap = Path.Combine(cell, "completed-input-mapping.json");
        ReplayCalibrationCatalog.ValidateFiveMinuteInputs(JsonSerializer.Deserialize<ReplayCompletedInputDigest[]>(File.ReadAllBytes(inputMap)), packet);
        Require(oracle?.Outputs?.Length == 2 && oracle.Outputs.Select(o => o.SampleHash).SequenceEqual(outputs.Select(o => o.SampleHash))
            && oracle.Outputs.All(o => o.Pictures == 18_000 && o.ComparedPictures == o.Pictures), "Full paired picture reference evidence is missing.");
        Write(Path.Combine(plan.Root, "five-minute-evidence.json"), new {
            contract.Revision, planHash, contractHash = plan.Files[EvidenceRole(plan, FiveMinuteContractRole)], observationHash,
            started, completed, packet, exact, oracle.ReportHash, oracle.SourceEvidenceHash, oracle.Outputs,
            inputMapHash = HashFile(inputMap), clockHash = HashFile(Path.Combine(cell, "source-clock.json")),
            growthHash = HashFile(Path.Combine(cell, "capture-growth.json")),
            nativeGrowthHash = HashFile(Path.Combine(cell, "native-allocation-growth.json")),
            reference = CalibrationPresentationIntegration.HashBinding(cell, PresentationPlanBinding.For(plan).Spec) });
        Write(Path.Combine(plan.Root,"result.json"), new { result = "passed-C00-five-minute-storage-pending-independent-acceptance",
            evidenceHash = HashFile(Path.Combine(plan.Root,"five-minute-evidence.json")), ordinarySavedSamples = 0, modelActivated = false });
    }
}
