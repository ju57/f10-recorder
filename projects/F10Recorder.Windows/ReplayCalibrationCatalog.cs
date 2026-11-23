using System.ComponentModel;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace F10Recorder;

internal sealed record ReplayCalibrationAvailability(ReplayReferenceModel Model, string Reason)
{
    internal ReplayOutputDemand Estimate(ReplayOutputSelection selection, string scope, string sourceDirectory, string hostIdentity = null)
    {
        if (Model == null) return ReplayOutputDemand.Unmeasured(Reason ?? "Reference calibration is absent.");
        if (!ReplayCalibrationCatalog.IsApplicableToDestination(Model, selection.Destination, hostIdentity))
            return ReplayOutputDemand.Unmeasured("Reference calibration no longer applies to this environment or output volume.");
        return Model.Estimate(selection, scope, sourceDirectory);
    }
}

// These data receipts attest the bounded reference experiment, under the current
// Windows account trust rule. Hashes bind evidence; they are not signatures or an
// arbitrary MP4 proof. The collector must retain the original evidence separately.
internal sealed record ReplayCalibrationProcessReceipt(string EvidenceHash, bool ProducerAbsent,
    bool ActualExitObserved, int ExitCode, bool TimedOut, bool OutputLimitExceeded, bool ForcedTermination,
    int? ProcessId = null, long? StartedUtcTicks = null);
internal sealed record ReplayCalibrationIndexReceipt(string Construction, int AsynchronousInputs, int Workers, int PeakActive,
    int FinishedSources, int UnsupportedSources, bool Joined, bool CancellableReads, double ReadDeadlineMilliseconds,
    bool DeadlineExceeded, string DrainError, bool SourceHandlesClosed, ReplayCalibrationIndexAcquisition Acquisition = null);
internal sealed record ReplayCalibrationIndexAcquisition([property: JsonRequired] int PreparedOwners,
    [property: JsonRequired] int Hits, [property: JsonRequired] int Misses,
    [property: JsonRequired] bool Complete, [property: JsonRequired] bool ReadersStarted, [property: JsonRequired] bool ReadersJoined);
internal sealed record ReplayCalibrationManagedReceipt(string EvidenceHash, int ParentPid, long ParentStartedUtcTicks,
    bool Closed, bool InputHashesComplete, string State, ReplayFileExtent Output, ReplayCalibrationIndexReceipt Index);
internal sealed record ReplayCalibrationNativeReceipt(string EvidenceHash, string PolicyIdentity,
    bool OwnedRequest, bool EligibleAtExport, bool PipelineSelected, bool ScopeClosed,
    bool PreparationSelected = false, bool PreparationJoined = false, string PreparedInputHash = null, long PreparedInputBytes = 0);
internal sealed record ReplayCalibrationSourceReceipt(string Label, string AcquisitionIdentity, string SampleHash,
    string Scope, string Volume, long AllocationUnit, long InputBytes, long FinalBytes, long FinalAllocation,
    long PeakAllocation, double ExportSeconds, int SegmentCount, bool Closed,
    ReplayCalibrationProcessReceipt Capture, ReplayCalibrationProcessReceipt Source,
    ReplayCalibrationProcessReceipt Export, ReplayCalibrationProcessReceipt Decode,
    string SelectionHash, string CaptureArgumentsHash, string ExportArgumentsHash,
    string SourcePicturesHash, bool AllTracksDecoded, bool ReferenceVideoFormat,
    bool ReferenceAudioFormat, bool NonSilentAudio, bool SourceLineageVerified,
    double MaximumActiveGapMs, int[] SourcePictureIds, string[] SourcePictureHashes, double AudioRmsDb,
    int VideoWidth, int VideoHeight, int AudioSampleRate, int AudioChannels,
    long? CapturePollGrowthBytes = null, long? CaptureStopGrowthBytes = null, string CaptureVolume = null,
    ReplayCalibrationProcessReceipt RuntimeValidation = null, string RuntimeValidationArgumentsHash = null,
    double OutputStart = 0, double OutputEnd = 0, long? CombinedPeakAllocation = null, long? WorkPeakAllocation = null,
    long? AssemblyPeakAllocation = null, long[] BoundaryPeakAllocations = null, ReplayCalibrationProcessReceipt[] ExportProcesses = null,
    ReplayValidation NormalValidation = null, ReplayFileExtent QualificationOutput = null,
    ReplayCalibrationManagedReceipt ManagedExport = null, string RequestSelectionHash = null,
    ReplayCalibrationNativeReceipt NativeExport = null, int CaptureObservationMilliseconds = 500,
    double MaximumCapacityGapMs = 0);
internal sealed record ReplayCalibrationReceipt(string Revision, string RecipeHash, string ExporterIdentity, string HostIdentity,
    ReplayReferenceModel Model, ReplayCalibrationSourceReceipt[] Sources, double MaximumActiveGapMs,
    string ObservationHash, string AdmissionHash, string ReceiptHash, ReplayCalibrationSourceReceipt[] LegacyOutputs = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReplayHostFacts HostFacts = null);

// Read-only account-private catalog. No settings/profile path or executable is
// loaded. No coefficient, empirical result or qualified receipt ships here.
internal static partial class ReplayCalibrationCatalog
{
    internal static Func<int, ReplayCalibrationAvailability> CandidateForChecks;
    internal const string ReceiptRevision = "reference-measurement-qualification-v9";
    internal const string FileName = "reference-v1.json";
    internal static string FileNameFor(int replayMinutes) => replayMinutes == 5 ? FileName
        : ReplayReferenceModel.SupportedMinutes(replayMinutes) ? $"reference-{replayMinutes}m-v1.json"
        : throw new IOException("Replay duration has no reference qualification cell.");
    internal const int MaximumBytes = 128 * 1024;
    internal static string EvidenceFileName(string protectedDirectory, int replayMinutes = 5)
        => replayMinutes == 5 && File.Exists(Path.Combine(protectedDirectory, PrivateFiveMinuteFile)) ? PrivateFiveMinuteFile
            : File.Exists(Path.Combine(protectedDirectory, SameCaptureFileName)) ? SameCaptureFileName
            : File.Exists(Path.Combine(protectedDirectory, CoverageFileName)) ? CoverageFileName : FileNameFor(replayMinutes);
    // Versioned canonical numeric recipe. Changing any requirement invalidates
    // both model and qualification revisions. Capture/audio arguments additionally
    // belong to Scope; current managed-engine/Windows/Core hashes bind Exporter.
    const string Recipe = "reference-native-owned-bounded-packet-pipeline-exact-fallback-v2|native=owned-eligible-export-thread-selected-policy-closed-scope|normal=packet-source-integrity-or-exact-full-decode|checked-object-held-through-publication|qualification=fresh-all-track-full-decode-clock-count-and-source-lineage-same-output|baseline=sum-selected-TS-EOF|training=union-packet-and-legacy-for-three-paired-acquisitions|trainingReceipts=hash-label-packetSample-legacySample|residual=max(0,max(training-finalEOF-inputEOF))|guard=max(16MiB,ceil(base/20))|peakExtra=max(0,max(training-sampledPeak-round(finalEOF)))|peak=round(finalBytes)+peakExtra+round(max(16MiB,ceil(finalBytes/20)))+work|work=round(inputBytes+64MiB)+2round(48MiB)|work-guard=retained-boundary-plus-exact-fallback|receipts=managed-parent-closed-hashes-and-all0..2-distinct-export-child-identities|unit=actual-local-NTFS|duration-cells=1m:60s,65inputs,200000000bytes,65536concat;5m:300s,305inputs,1000000000bytes,65536concat;30m:1800s,1805inputs,5000000000bytes,1048576concat|output-duration=1..cellSeconds|main-output=cellSeconds|head-inclusive-input-span=output..output+1.1|duration-specific-independent-training-and-heldouts-no-transfer|order=T01,T02,T03,H02,H01,H03|independent-acquisitions=6|paired-outputs=12-not-independent-captures|sampleIntervalMs=100|maximumInventoryEvidenceGapMs=2000|capacity=one-inflight-caller-free-minus-full-commitments-versioned-ledger|admission=fresh|accepted=bounded-covered-delay|stop=observer-independent-claims-retained|non-silent-48kHz-stereo-audio+1920x1080-video|normal-producer-exits|heldout=zero-final-or-observed-peak-exceedances-both-variants,no-refit|capture-poll=rolling2000ms-positive-native-cache-growth-explicit-receipt-horizon|capture-stop=positive-native-cache-growth-through-confirmed-absence|capture-margin=training-max+max16MiB-or5percent-rounded-to-unit|open-tail=reserve-output-at-cell-maximum-input-and-snapshot-round-cell-maximum-plus-cell-input-count-units-plus-rounded-cell-concat|close-and-rebind-before-output|finite-empirical-reference-not-universal-bound";
    const string StagingRecipe = "staging=accepted-calibrated-scratch-and-held-authority-only|training=unstaged|packet-heldouts=protected-input-sha256-at-most8MiB-feed-and-producer-joined|exact-fallback=unstaged";
    const string AcquisitionRecipe = "index=complete-accepted-selection-prepared-hits-plus-real-misses|hits<=live-prepared-owners<=inputs|misses=actual-finished-sources|miss-workers=min(4,misses)-cancellable30s-real-joined|all-hit=pristine-unused-batch-no-started-reader-empty-lifetime-joined|all-accepted-source-byte-hashes-remain-complete";
    internal static readonly string RecipeHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Recipe + "|" + StagingRecipe + "|" + AcquisitionRecipe)));

    internal static bool IndexAcquisitionValid(ReplayCalibrationIndexReceipt index,int inputs,int replayMinutes=5)
    {
        var a=index?.Acquisition;
        if(!ReplayReferenceModel.SupportedMinutes(replayMinutes) || inputs<1 || inputs>ReplayReferenceModel.InputCountLimit(replayMinutes)
            || index==null || a==null || !a.Complete || !a.ReadersJoined
            || a.Hits<0 || a.PreparedOwners<a.Hits || a.PreparedOwners>inputs || a.Misses<0 || a.Misses>inputs
            || a.Hits!=inputs-a.Misses || index.Construction!=ReplaySaveExport.BoundedPacketConstruction
            || index.AsynchronousInputs!=inputs || index.UnsupportedSources!=0 || index.DeadlineExceeded
            || index.DrainError!=null || !index.SourceHandlesClosed)return false;
        if(a.Misses==0)return !a.ReadersStarted && index.Workers==0 && index.PeakActive==0 && index.FinishedSources==0
            && !index.Joined && !index.CancellableReads && index.ReadDeadlineMilliseconds==0;
        return a.ReadersStarted && index.Workers==Math.Min(4,a.Misses) && index.PeakActive>=1 && index.PeakActive<=index.Workers
            && index.FinishedSources==a.Misses && index.Joined && index.CancellableReads && index.ReadDeadlineMilliseconds==30000;
    }
    // Native metadata only: no device context, probe, renderer or encoder starts.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DisplayDevice
    {
        internal uint Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string Description;
        internal uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string Identity;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string Key;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplayDevices(string device, uint index, ref DisplayDevice display, uint flags);
    internal static ReplayHostFacts CaptureHostFacts()
    {
            using var account = WindowsIdentity.GetCurrent();
            var adapters = new List<object>();
            for (uint index = 0; ; index++)
            {
                Require(index < 32);
                var display = new DisplayDevice { Size = (uint)Marshal.SizeOf<DisplayDevice>() };
                if (!EnumDisplayDevices(null, index, ref display, 0)) break;
                if (!display.Identity.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase)) continue;
                const string prefix = @"\Registry\Machine\";
                Require(display.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                using var key = Registry.LocalMachine.OpenSubKey(display.Key[prefix.Length..], false);
                string version = key?.GetValue("DriverVersion") as string;
                Require(!string.IsNullOrWhiteSpace(version) && version.Length <= 128);
                adapters.Add(new { display.Name, display.Identity, display.Key, version });
            }
            Require(adapters.Count > 0);
            return ReplayHostFacts.Create(new { os = Environment.OSVersion.VersionString,
                machine = Environment.MachineName, account = account.User.Value,
                architecture = RuntimeInformation.OSArchitecture.ToString(), adapters });
    }
    internal static string HostIdentity => CaptureHostFacts().Identity;
    static readonly string[] Order = ["T01", "T02", "T03", "H02", "H01", "H03"];
    static readonly JsonSerializerOptions Json = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 16
    };

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetVolumeInformation(string root, StringBuilder name, uint nameLength,
        out uint serial, out uint maximumComponentLength, out uint flags, StringBuilder fileSystem, uint fileSystemLength);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern uint GetDriveType(string root);

    internal static ReplayCalibrationAvailability Load() => Load(5);
    internal static ReplayCalibrationAvailability Load(int replayMinutes) => CandidateForChecks?.Invoke(replayMinutes)
        ?? LoadFrom(Path.Combine(TrustedStorage.DefaultRoot, "storage-calibration"), replayMinutes: replayMinutes);

    internal static ReplayCalibrationAvailability LoadFrom(string protectedDirectory, string hostIdentity = null, int replayMinutes = 5)
    {
        try
        {
            if (!Directory.Exists(protectedDirectory)) return new(null, "Reference calibration is absent.");
            if (File.Exists(Path.Combine(protectedDirectory, PrivateFiveMinuteFile)))
            {
                Require(!HasSharedCoverage(protectedDirectory), "Conflicting private and shared coverage receipts.");
                if (replayMinutes == 5) return LoadPrivateFiveMinute(protectedDirectory, hostIdentity ?? HostIdentity);
            }
            if (File.Exists(Path.Combine(protectedDirectory, SameCaptureFileName)))
                return LoadSameCaptureFrom(protectedDirectory, hostIdentity, replayMinutes);
            if (File.Exists(Path.Combine(protectedDirectory, CoverageFileName)))
                return LoadCoverageFrom(protectedDirectory, hostIdentity, replayMinutes);
            using var directory = TrustedStorage.Acquire(protectedDirectory);
            string file = Path.Combine(protectedDirectory, FileNameFor(replayMinutes));
            // A read lease denies replacement, deletion and writers through validation.
            using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            TrustedStorage.ValidateFile(file);
            Require(input.Length is > 0 and <= MaximumBytes, "Receipt size is invalid.");
            byte[] bytes = new byte[checked((int)input.Length)];
            input.ReadExactly(bytes);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            UniqueProperties(document.RootElement);
            var receipt = JsonSerializer.Deserialize<ReplayCalibrationReceipt>(bytes, Json);
            Validate(receipt, hostIdentity ?? HostIdentity);
            Require(receipt.Model.ReplayMinutes == replayMinutes, "Catalog duration differs from the requested profile.");
            return new(receipt.Model, null);
        }
        catch (FileNotFoundException) { return new(null, "Reference calibration is absent."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception
            or JsonException or ArgumentException or OverflowException or InvalidOperationException or NotSupportedException)
        {
            return new(null, ReplayOutputDemand.Bounded("Reference calibration rejected: " + ex.Message, 160));
        }
    }

    // Call before numeric use, for the actual destination. Never infer filesystem
    // identity from a nominal drive letter (mount points may name another volume).
    internal static bool IsApplicableToDestination(ReplayReferenceModel model, string destination, string hostIdentity = null)
    {
        try
        {
            if (model?.Coverage != null) return CoverageApplicable(model, destination, hostIdentity);
            if (model == null) return false;
            var revision = ModelRevision(model.Scope, model.Volume, model.AllocationUnit, model.FinalResidual,
                model.SampledFinalizationExtra, model.TrainingReceipts, hostIdentity, model.CaptureHeadroom, model.ReplayMinutes);
            if (model.PrivateReceipt is { } binding)
            {
                if (model.ReplayMinutes != 5 || model.Revision != PrivateRevision(revision, binding.FileHash)
                    || !PrivateReceiptStillPresent(binding)) return false;
            }
            else if (model.Revision != revision) return false;
            var capacity = ReplayStorage.ObserveCapacity(destination);
            if (capacity.Volume != model.Volume || capacity.AllocationUnit != model.AllocationUnit
                || GetDriveType(capacity.Volume) != 3) return false; // DRIVE_FIXED
            string existing = Path.GetFullPath(destination);
            while (!Directory.Exists(existing)) existing = Path.GetDirectoryName(existing) ?? throw new IOException("Destination ancestor is unavailable.");
            if ((File.GetAttributes(existing) & (FileAttributes.Compressed | FileAttributes.SparseFile | FileAttributes.ReparsePoint)) != 0) return false;
            var format = new StringBuilder(32);
            return GetVolumeInformation(capacity.Volume, null, 0, out _, out _, out _, format, (uint)format.Capacity)
                && format.ToString().Equals("NTFS", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException)
        { return false; }
    }

    // The calibration runner freezes this revision after T03, before observing
    // any held-out sample. The receipt creation path never refits a model.
    internal static string ModelRevision(string scope, string volume, long allocationUnit,
        long residual, long extra, string[] trainingReceipts, string hostIdentity = null, ReplayCaptureHeadroom captureHeadroom = null,
        int replayMinutes = 5) => ReplayReferenceModel.Hash(new
        {
            recipe = RecipeHash, exporter = ReplayReferenceModel.ExporterIdentity, host = hostIdentity ?? HostIdentity,
            scope, volume, allocationUnit, residual, extra, trainingReceipts, captureHeadroom, replayMinutes,
            inputLimit = ReplayReferenceModel.InputLimit(replayMinutes), inputCountLimit = ReplayReferenceModel.InputCountLimit(replayMinutes),
            concatLimit = ReplayReferenceModel.ConcatLimit(replayMinutes)
        });

    internal static ReplayCalibrationReceipt CreateReceipt(ReplayReferenceModel model,
        ReplayCalibrationSourceReceipt[] sources, double maximumActiveGapMs, string observationHash, string admissionHash, string hostIdentity = null,
        ReplayCalibrationSourceReceipt[] legacyOutputs = null, ReplayHostFacts hostFacts = null)
    {
        hostFacts ??= hostIdentity == null ? CaptureHostFacts() : null;
        hostIdentity ??= hostFacts.Identity;
        var receipt = new ReplayCalibrationReceipt(ReceiptRevision, RecipeHash, ReplayReferenceModel.ExporterIdentity, hostIdentity,
            model, sources?.ToArray(), maximumActiveGapMs, observationHash, admissionHash, null, legacyOutputs?.ToArray(), hostFacts);
        receipt = receipt with { ReceiptHash = HashReceipt(receipt) };
        Validate(receipt, hostIdentity);
        // Serializability and the same cap apply before a future collector writes.
        Require(JsonSerializer.SerializeToUtf8Bytes(receipt, Json).Length <= MaximumBytes);
        return receipt;
    }

    static string HashReceipt(ReplayCalibrationReceipt receipt) => ReplayReferenceModel.Hash(receipt with { ReceiptHash = null });
    static void Require(bool condition, string reason = null) { if (!condition) throw new IOException("Incomplete reference qualification." + (reason == null ? "" : " " + reason)); }
    static bool Hash(string value) => value?.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
    static bool Gap(double value) => double.IsFinite(value) && value > 0 && value <= ReplayCapacityPolicy.CoverageMilliseconds;
    static bool Process(ReplayCalibrationProcessReceipt process) => process != null && Hash(process.EvidenceHash)
        && process.ProducerAbsent && process.ActualExitObserved && process.ExitCode == 0 && !process.TimedOut && !process.OutputLimitExceeded && !process.ForcedTermination;
    internal static bool IdentifiedProcess(ReplayCalibrationProcessReceipt process) => Process(process)
        && process.ProcessId > 0 && process.StartedUtcTicks > DateTime.UnixEpoch.Ticks && process.StartedUtcTicks <= DateTime.MaxValue.Ticks;

    internal static string PairHash(ReplayCalibrationSourceReceipt packet, ReplayCalibrationSourceReceipt legacy) =>
        ReplayReferenceModel.Hash(new { packet.Label, packet = packet.SampleHash, legacy = legacy.SampleHash });

    static bool Output(ReplayFileExtent file, ReplayCalibrationSourceReceipt source) => file != null
        && file.Volume == source.Volume && !string.IsNullOrWhiteSpace(file.Identity) && file.Identity.Length <= 128
        && file.Length == source.FinalBytes && file.AllocatedBytes == source.FinalAllocation && file.AllocationUnit == source.AllocationUnit;

    static void Validate(ReplayCalibrationReceipt receipt, string hostIdentity, ReplayMeasurementIdentity original = null)
    {
        Require(receipt != null, "Receipt is absent.");
        Require(Hash(hostIdentity) && receipt.HostIdentity == hostIdentity, "Host identity mismatch (OS/account/device/driver context).");
        Require(receipt.RecipeHash == (original?.Recipe ?? RecipeHash), "Measurement recipe changed.");
        Require(receipt.ExporterIdentity == (original?.Exporter ?? ReplayReferenceModel.ExporterIdentity), "Measurement exporter bytes changed.");
        Require(receipt.Revision == (original?.Revision ?? ReceiptRevision)
            && Hash(receipt.ReceiptHash) && (original != null || receipt.ReceiptHash == HashReceipt(receipt)) && Gap(receipt.MaximumActiveGapMs)
            && Hash(receipt.ObservationHash) && Hash(receipt.AdmissionHash));
        if (receipt.HostFacts != null) Require(receipt.HostFacts.Valid() && receipt.HostFacts.Identity == receipt.HostIdentity, "Raw host operands changed.");
        Require(receipt.Revision != ReceiptRevision || receipt.HostFacts != null, "New measurement evidence requires exact raw host operands.");
        var model = receipt.Model;
        Require(model != null && ReplayReferenceModel.SupportedMinutes(model.ReplayMinutes)
            && Hash(model.Scope) && Hash(model.Revision) && model.Volume?.Length <= 128
            && model.Volume.StartsWith(@"\\?\Volume{", StringComparison.Ordinal) && model.Volume.EndsWith(@"}\", StringComparison.Ordinal)
            && Guid.TryParse(model.Volume[11..^2], out _) && model.AllocationUnit is >= 512 and <= 65536
            && (model.AllocationUnit & (model.AllocationUnit - 1)) == 0
            && model.FinalResidual >= 0 && model.SampledFinalizationExtra >= 0
            && model.TrainingReceipts?.Length == 3 && model.TrainingReceipts.All(Hash));
        Require(receipt.Sources?.Length == 6 && receipt.Sources.All(s => s != null)
            && receipt.LegacyOutputs?.Length == 6 && receipt.LegacyOutputs.All(s => s != null),
            "Six packet observations and six paired legacy outputs are required.");
        var sources = receipt.Sources;
        var legacyOutputs = receipt.LegacyOutputs;
        var outputs = sources.Concat(legacyOutputs).ToArray();
        Require(sources.Select(s => s.Label).SequenceEqual(Order)
            && sources.Select(s => s.AcquisitionIdentity).Distinct(StringComparer.Ordinal).Count() == 6
            && sources.Select(s => s.SampleHash).Distinct(StringComparer.Ordinal).Count() == 6
            && sources.Select(s => s.SelectionHash).Distinct(StringComparer.Ordinal).Count() == 6
            && sources.Select(s => s.Capture?.EvidenceHash).Distinct(StringComparer.Ordinal).Count() == 6,
            "All six acquisitions, selections, samples and capture evidence receipts must be distinct.");
        Require(legacyOutputs.Select(s => s.Label).SequenceEqual(Order));
        for (int n = 0; n < 6; n++)
        {
            var packet = sources[n]; var legacy = legacyOutputs[n];
            Require(packet.NormalValidation?.ExportRecipe == ReplaySaveExport.PacketRecipe
                && legacy.NormalValidation?.ExportRecipe == ReplayExactExport.Recipe
                && packet.AcquisitionIdentity == legacy.AcquisitionIdentity && packet.Capture == legacy.Capture
                && packet.Source == legacy.Source && packet.SelectionHash == legacy.SelectionHash
                && packet.CaptureArgumentsHash == legacy.CaptureArgumentsHash && packet.InputBytes == legacy.InputBytes
                && packet.OutputStart == legacy.OutputStart && packet.OutputEnd == legacy.OutputEnd
                && packet.CapturePollGrowthBytes == legacy.CapturePollGrowthBytes && packet.CaptureStopGrowthBytes == legacy.CaptureStopGrowthBytes
                && packet.CaptureObservationMilliseconds == legacy.CaptureObservationMilliseconds
                && packet.CaptureVolume == legacy.CaptureVolume,
                "Paired variants must share the protected acquisition and selection, not output evidence.");
        }
        Require(outputs.Select(s => s.SampleHash).Distinct().Count() == 12
            && outputs.Select(s => s.NormalValidation?.CheckedOutput?.Identity).Distinct().Count() == 12
            && outputs.Select(s => s.Decode?.EvidenceHash).Distinct().Count() == 12,
            "Every variant needs distinct sample, final object and independent decode evidence.");
        foreach (var source in outputs) ValidateOutput(source, model, receipt.MaximumActiveGapMs);
        var outputProcesses = outputs.SelectMany(s => s.ExportProcesses.Append(s.Decode)
            .Concat(s.RuntimeValidation == null ? [] : new[] { s.RuntimeValidation })).ToArray();
        Require(outputProcesses.Select(p => (p.ProcessId, p.StartedUtcTicks)).Distinct().Count() == outputProcesses.Length
            && outputProcesses.Select(p => p.EvidenceHash).Distinct().Count() == outputProcesses.Length,
            "Normal export/decoder and independent qualification child receipts cannot be reused between output requests.");
        Require(outputs.Select(s => s.RequestSelectionHash).Distinct().Count() == 12,
            "Each paired export must preserve its own actual protected-request selection identity.");
        Require(outputs.Select(s => s.NativeExport.EvidenceHash).Distinct(StringComparer.Ordinal).Count() == 12,
            "Each export requires its own actual native ownership witness.");
        var training = sources.Take(3).Concat(legacyOutputs.Take(3)).ToArray();
        Require(model.TrainingReceipts.SequenceEqual(Enumerable.Range(0, 3).Select(n => PairHash(sources[n], legacyOutputs[n]))));
        long residual = Math.Max(0, training.Max(s => checked(s.FinalBytes - s.InputBytes)));
        long extra = Math.Max(0, training.Max(s => checked(s.PeakAllocation - ReplayReferenceModel.Round(s.FinalBytes, model.AllocationUnit))));
        Require(model.FinalResidual == residual && model.SampledFinalizationExtra == extra
            && model.Revision == MeasurementModelRevision(model, receipt.RecipeHash, receipt.ExporterIdentity, hostIdentity, original?.Revision == LegacyReceiptRevision));
        // Coverage is recomputed against the frozen training model; never refit
        // using held-out observations, and keep the final-allocation gate separate.
        Require(outputs.All(s => model.CoversObservations(s.InputBytes, s.FinalBytes, s.FinalAllocation, s.CombinedPeakAllocation.Value)));
        if (model.CaptureHeadroom != null)
        {
            var headroom = model.CaptureHeadroom;
            Require(sources.All(s => s.CapturePollGrowthBytes is > 0 && s.CaptureStopGrowthBytes is >= 0 && s.CaptureVolume == model.Volume
                && s.CaptureObservationMilliseconds == ReplayCapacityPolicy.CoverageMilliseconds));
            Require(outputs.All(s => Gap(s.MaximumCapacityGapMs)), "Capacity query coverage evidence is missing or outside the declared horizon.");
            Require(headroom.Scope == model.Scope && headroom.Volume == model.Volume && headroom.AllocationUnit == model.AllocationUnit
                && headroom.ObservationMilliseconds == ReplayCapacityPolicy.CoverageMilliseconds
                && headroom.PollGrowthBytes == ReplayReferenceModel.CaptureAllowance(training.Max(s => s.CapturePollGrowthBytes.Value), model.AllocationUnit)
                && headroom.StopGrowthBytes == ReplayReferenceModel.CaptureAllowance(training.Max(s => s.CaptureStopGrowthBytes.Value), model.AllocationUnit));
            Require(sources.All(s => s.CapturePollGrowthBytes <= headroom.PollGrowthBytes && s.CaptureStopGrowthBytes <= headroom.StopGrowthBytes));
            Require(ReplayStorageSafety.Assess(new(model.Volume, model.AllocationUnit, long.MaxValue), headroom).SafeToResume);
        }
        _ = model.PeakBytes(model.InputByteLimit); // reject representational overflow in the whole declared range
    }

    internal static void ValidateOutput(ReplayCalibrationSourceReceipt source, ReplayReferenceModel model, double maximumActiveGapMs)
    {
            Require(Hash(source.AcquisitionIdentity) && Hash(source.SampleHash) && Hash(source.SelectionHash) && Hash(source.RequestSelectionHash)
                && Hash(source.CaptureArgumentsHash) && Hash(source.ExportArgumentsHash) && Hash(source.SourcePicturesHash)
                && source.Scope == model.Scope && source.Volume == model.Volume && source.AllocationUnit == model.AllocationUnit
                && source.InputBytes >= 1 && source.InputBytes <= model.InputByteLimit && source.FinalBytes > 0
                && source.FinalAllocation >= source.FinalBytes && source.FinalAllocation % model.AllocationUnit == 0
                && source.PeakAllocation >= source.FinalAllocation && source.PeakAllocation % model.AllocationUnit == 0
                && double.IsFinite(source.ExportSeconds) && Math.Abs(source.ExportSeconds - ReplayReferenceModel.Seconds(model.ReplayMinutes)) <= 0.000001
                && double.IsFinite(source.OutputStart) && double.IsFinite(source.OutputEnd) && source.OutputStart >= 0
                && Math.Abs(source.OutputEnd - source.OutputStart - source.ExportSeconds) <= 0.000001
                && source.SegmentCount >= ReplayReferenceModel.Seconds(model.ReplayMinutes)-2
                && source.SegmentCount <= ReplayReferenceModel.InputCountLimit(model.ReplayMinutes) && source.Closed && Gap(source.MaximumActiveGapMs)
                && source.MaximumActiveGapMs <= maximumActiveGapMs
                && Process(source.Capture) && Process(source.Source) && IdentifiedProcess(source.Decode)
                && source.AllTracksDecoded && source.ReferenceVideoFormat && source.ReferenceAudioFormat
                && source.NonSilentAudio && source.SourceLineageVerified
                && source.VideoWidth == 1920 && source.VideoHeight == 1080 && source.AudioSampleRate == 48000 && source.AudioChannels == 2
                && double.IsFinite(source.AudioRmsDb) && source.AudioRmsDb <= 0
                && source.SourcePictureIds?.Length == 3 && source.SourcePictureHashes?.Length == 3 && source.SourcePictureHashes.All(Hash));
            var normal = source.NormalValidation;
            Require(normal != null && double.IsFinite(normal.Milliseconds) && normal.Milliseconds >= 0
                && normal.PublicationIdentityBound && Output(normal.CheckedOutput, source)
                && Output(source.QualificationOutput, source) && normal.CheckedOutput == source.QualificationOutput,
                "Normal publication and independent qualification must attest the same final file object.");
            bool packet = normal.ExportRecipe == ReplaySaveExport.PacketRecipe;
            var native = source.NativeExport;
            Require(native is { OwnedRequest: true, EligibleAtExport: true, ScopeClosed: true }
                && Hash(native.EvidenceHash) && native.PolicyIdentity == ReplaySaveExport.PolicyIdentity
                && native.PipelineSelected == packet,
                "Native-v2 qualification requires actual owned export entry, selected pipeline/fallback and closed scope evidence.");
            if (!packet)
                Require(!native.PreparationSelected && !native.PreparationJoined && native.PreparedInputHash == null && native.PreparedInputBytes == 0,
                    "Exact fallback must retain its unstaged qualification path.");
            if (packet)
            {
                bool stagedHeldout = source.Label.StartsWith("H", StringComparison.Ordinal);
                Require(native.PreparationSelected == stagedHeldout
                    && (stagedHeldout ? native.PreparationJoined && Hash(native.PreparedInputHash)
                        && native.PreparedInputBytes is > 0 and <= 8388608
                        : !native.PreparationJoined && native.PreparedInputHash == null && native.PreparedInputBytes == 0),
                    "Staged policy requires genuinely joined, protected-input preparation on every packet heldout.");
                var managed = source.ManagedExport;
                Require(normal.Kind == ReplayOutputIntegrity.Kind && normal.Frames == 0 && normal.CodedPictures == ReplayReferenceModel.Seconds(model.ReplayMinutes)*60
                    && normal.AudioPackets > 0 && Hash(normal.SourceProofHash) && source.RuntimeValidation == null
                    && source.RuntimeValidationArgumentsHash == null && managed != null && Hash(managed.EvidenceHash)
                    && managed.ParentPid > 0 && managed.ParentStartedUtcTicks > DateTime.UnixEpoch.Ticks
                    && managed.ParentStartedUtcTicks <= DateTime.MaxValue.Ticks && managed.Closed && managed.InputHashesComplete
                    && managed.State == "Completed" && managed.Index != null && managed.Output == normal.CheckedOutput,
                    "Packet normal checking requires actual managed closure, joined readers and frozen source-byte proof; no fake decoder.");
                var index = managed.Index;
                Require(IndexAcquisitionValid(index,source.SegmentCount,model.ReplayMinutes),
                    "Packet calibration requires complete prepared/fresh acquisition, actual miss-reader work and closed input receipts.");
            }
            else
                Require(normal.ExportRecipe == ReplayExactExport.Recipe && normal.Kind == "FullDecode"
                    && normal.Frames >= ReplayReferenceModel.Seconds(model.ReplayMinutes)*60-1
                    && normal.Frames <= ReplayReferenceModel.Seconds(model.ReplayMinutes)*60+1 && normal.SourceProofHash == null && source.ManagedExport == null
                    && IdentifiedProcess(source.RuntimeValidation) && Hash(source.RuntimeValidationArgumentsHash)
                    && source.RuntimeValidation.EvidenceHash != source.Decode.EvidenceHash,
                    "Legacy fallback requires fresh normal decoding plus a separate qualification decoder.");
            var work = ReplaySaveExport.WorkDemand(source.InputBytes, model.AllocationUnit);
            Require(source.CombinedPeakAllocation.HasValue && source.WorkPeakAllocation.HasValue && source.AssemblyPeakAllocation.HasValue
                && source.CombinedPeakAllocation >= source.PeakAllocation && source.CombinedPeakAllocation >= source.WorkPeakAllocation
                && source.CombinedPeakAllocation % model.AllocationUnit == 0 && source.WorkPeakAllocation % model.AllocationUnit == 0
                && source.AssemblyPeakAllocation >= 0 && source.AssemblyPeakAllocation <= work.AssemblyPeakAllocatedBytes
                && source.AssemblyPeakAllocation % model.AllocationUnit == 0
                && source.BoundaryPeakAllocations?.Length == 2 && source.BoundaryPeakAllocations.All(p => p >= 0
                    && p % model.AllocationUnit == 0 && p <= work.BoundaryPeakAllocatedBytes)
                && source.WorkPeakAllocation >= source.AssemblyPeakAllocation && source.WorkPeakAllocation >= source.BoundaryPeakAllocations.Max()
                && source.WorkPeakAllocation <= work.Total && source.PeakAllocation <= model.PeakBytes(source.InputBytes) - work.Total,
                "Combined output/work peak evidence is incomplete or outside the qualified envelope.");
            Require(source.ExportProcesses != null && source.ExportProcesses.Length >= (packet ? 0 : 1)
                && source.ExportProcesses.Length <= 2 && source.ExportProcesses.All(p => Process(p)
                && p.ProcessId > 0 && p.StartedUtcTicks > 0 && p.StartedUtcTicks <= DateTime.MaxValue.Ticks)
                && source.ExportProcesses.Select(p => (p.ProcessId, p.StartedUtcTicks)).Distinct().Count() == source.ExportProcesses.Length
                && source.ExportProcesses.Select(p => p.EvidenceHash).Distinct(StringComparer.Ordinal).Count() == source.ExportProcesses.Length
                && source.ExportProcesses.LastOrDefault() == source.Export, "All export child identities and normal cleanup receipts are required.");
            int seed = (source.Label.StartsWith('T') ? 13579 : 24680) * 256;
            Require(source.SourcePictureIds.All(id => id >= seed && id < seed + 25000));
            Require(source.Label.EndsWith('1') ? source.SourcePictureIds.All(id => id == seed)
                : source.SourcePictureIds[0] < source.SourcePictureIds[1] && source.SourcePictureIds[1] < source.SourcePictureIds[2]
                    && source.SourcePictureHashes.Distinct().Count() >= 2);
    }

    static void UniqueProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                Require(names.Add(property.Name));
                UniqueProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) UniqueProperties(item);
    }
}






