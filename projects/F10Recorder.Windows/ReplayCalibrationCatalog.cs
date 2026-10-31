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
    bool ActualExitObserved, int ExitCode, bool TimedOut, bool OutputLimitExceeded, bool ForcedTermination);
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
    long? CapturePollGrowthBytes = null, long? CaptureStopGrowthBytes = null, string CaptureVolume = null);
internal sealed record ReplayCalibrationReceipt(string Revision, string RecipeHash, string ExporterIdentity, string HostIdentity,
    ReplayReferenceModel Model, ReplayCalibrationSourceReceipt[] Sources, double MaximumActiveGapMs,
    string ObservationHash, string AdmissionHash, string ReceiptHash);

// Read-only account-private catalog. No settings/profile path or executable is
// loaded. No coefficient, empirical result or qualified receipt ships here.
internal static class ReplayCalibrationCatalog
{
    internal const string ReceiptRevision = "reference-qualification-v1";
    internal const string FileName = "reference-v1.json";
    internal const int MaximumBytes = 128 * 1024;
    // Versioned canonical numeric recipe. Changing any requirement invalidates
    // both model and qualification revisions. Capture/audio arguments additionally
    // belong to Scope; current managed-engine/Windows/Core hashes bind Exporter.
    const string Recipe = "reference-bytes-v1|baseline=sum-selected-TS-EOF|residual=max(0,max(training-finalEOF-inputEOF))|guard=max(16MiB,ceil(base/20))|peakExtra=max(0,max(training-sampledPeak-round(finalEOF)))|peak=round(finalBytes)+peakExtra+round(max(16MiB,ceil(finalBytes/20)))|unit=actual-local-NTFS|duration=298..300.1|segments=298..305|inputBytes=1..1000000000|order=T01,T02,T03,H02,H01,H03|independent-acquisitions=6|sampleIntervalMs=100|maximumActiveGapMs=500|all-track-full-decode+source-lineage+non-silent-48kHz-stereo-audio+1920x1080-video|normal-producer-exits|heldout=zero-final-or-observed-peak-exceedances,no-refit|capture-poll=rolling500ms-positive-native-cache-growth|capture-stop=positive-native-cache-growth-through-confirmed-absence|capture-margin=training-max+max16MiB-or5percent-rounded-to-unit|open-tail=reserve-output-at-maximum-input-1000000000-and-snapshot-round1000000000-plus305units-plus64KiB-concat|close-and-rebind-before-copy-export|finite-empirical-reference-not-universal-bound";
    internal static readonly string RecipeHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Recipe)));
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
    internal static string HostIdentity
    {
        get
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
            return ReplayReferenceModel.Hash(new { os = Environment.OSVersion.VersionString,
                machine = Environment.MachineName, account = account.User.Value,
                architecture = RuntimeInformation.OSArchitecture.ToString(), adapters });
        }
    }
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

    internal static ReplayCalibrationAvailability Load() => LoadFrom(Path.Combine(TrustedStorage.DefaultRoot, "storage-calibration"));

    internal static ReplayCalibrationAvailability LoadFrom(string protectedDirectory, string hostIdentity = null)
    {
        try
        {
            if (!Directory.Exists(protectedDirectory)) return new(null, "Reference calibration is absent.");
            using var directory = TrustedStorage.Acquire(protectedDirectory);
            string file = Path.Combine(protectedDirectory, FileName);
            // A read lease denies replacement, deletion and writers through validation.
            using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            TrustedStorage.ValidateFile(file);
            Require(input.Length is > 0 and <= MaximumBytes);
            byte[] bytes = new byte[checked((int)input.Length)];
            input.ReadExactly(bytes);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            UniqueProperties(document.RootElement);
            var receipt = JsonSerializer.Deserialize<ReplayCalibrationReceipt>(bytes, Json);
            Validate(receipt, hostIdentity ?? HostIdentity);
            return new(receipt.Model, null);
        }
        catch (FileNotFoundException) { return new(null, "Reference calibration is absent."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception
            or JsonException or ArgumentException or OverflowException or InvalidOperationException or NotSupportedException)
        {
            return new(null, "Reference calibration is unavailable, stale, malformed or incomplete.");
        }
    }

    // Call before numeric use, for the actual destination. Never infer filesystem
    // identity from a nominal drive letter (mount points may name another volume).
    internal static bool IsApplicableToDestination(ReplayReferenceModel model, string destination, string hostIdentity = null)
    {
        try
        {
            if (model == null || model.Revision != ModelRevision(model.Scope, model.Volume, model.AllocationUnit,
                model.FinalResidual, model.SampledFinalizationExtra, model.TrainingReceipts, hostIdentity, model.CaptureHeadroom)) return false;
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
        long residual, long extra, string[] trainingReceipts, string hostIdentity = null, ReplayCaptureHeadroom captureHeadroom = null) => ReplayReferenceModel.Hash(new
        {
            recipe = RecipeHash, exporter = ReplayReferenceModel.ExporterIdentity, host = hostIdentity ?? HostIdentity,
            scope, volume, allocationUnit, residual, extra, trainingReceipts, captureHeadroom
        });

    internal static ReplayCalibrationReceipt CreateReceipt(ReplayReferenceModel model,
        ReplayCalibrationSourceReceipt[] sources, double maximumActiveGapMs, string observationHash, string admissionHash, string hostIdentity = null)
    {
        hostIdentity ??= HostIdentity;
        var receipt = new ReplayCalibrationReceipt(ReceiptRevision, RecipeHash, ReplayReferenceModel.ExporterIdentity, hostIdentity,
            model, sources?.ToArray(), maximumActiveGapMs, observationHash, admissionHash, null);
        receipt = receipt with { ReceiptHash = HashReceipt(receipt) };
        Validate(receipt, hostIdentity);
        // Serializability and the same cap apply before a future collector writes.
        Require(JsonSerializer.SerializeToUtf8Bytes(receipt, Json).Length <= MaximumBytes);
        return receipt;
    }

    static string HashReceipt(ReplayCalibrationReceipt receipt) => ReplayReferenceModel.Hash(receipt with { ReceiptHash = null });
    static void Require(bool condition, string reason = null) { if (!condition) throw new IOException("Incomplete reference qualification." + (reason == null ? "" : " " + reason)); }
    static bool Hash(string value) => value?.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
    static bool Gap(double value) => double.IsFinite(value) && value is > 0 and <= 500;
    static bool Process(ReplayCalibrationProcessReceipt process) => process != null && Hash(process.EvidenceHash)
        && process.ProducerAbsent && process.ActualExitObserved && process.ExitCode == 0 && !process.TimedOut && !process.OutputLimitExceeded && !process.ForcedTermination;

    static void Validate(ReplayCalibrationReceipt receipt, string hostIdentity)
    {
        Require(receipt != null && receipt.Revision == ReceiptRevision && receipt.RecipeHash == RecipeHash
            && receipt.ExporterIdentity == ReplayReferenceModel.ExporterIdentity && Hash(hostIdentity) && receipt.HostIdentity == hostIdentity && Hash(receipt.ReceiptHash)
            && receipt.ReceiptHash == HashReceipt(receipt) && Gap(receipt.MaximumActiveGapMs)
            && Hash(receipt.ObservationHash) && Hash(receipt.AdmissionHash));
        var model = receipt.Model;
        Require(model != null && Hash(model.Scope) && Hash(model.Revision) && model.Volume?.Length <= 128
            && model.Volume.StartsWith(@"\\?\Volume{", StringComparison.Ordinal) && model.Volume.EndsWith(@"}\", StringComparison.Ordinal)
            && Guid.TryParse(model.Volume[11..^2], out _) && model.AllocationUnit is >= 512 and <= 65536
            && (model.AllocationUnit & (model.AllocationUnit - 1)) == 0
            && model.FinalResidual >= 0 && model.SampledFinalizationExtra >= 0
            && model.TrainingReceipts?.Length == 3 && model.TrainingReceipts.All(Hash));
        Require(receipt.Sources?.Length == 6 && receipt.Sources.All(s => s != null));
        var sources = receipt.Sources;
        Require(sources.Select(s => s.Label).SequenceEqual(Order)
            && sources.Select(s => s.AcquisitionIdentity).Distinct(StringComparer.Ordinal).Count() == 6
            && sources.Select(s => s.SampleHash).Distinct(StringComparer.Ordinal).Count() == 6
            && sources.Select(s => s.SelectionHash).Distinct(StringComparer.Ordinal).Count() == 6
            && sources.Select(s => s.Capture?.EvidenceHash).Distinct(StringComparer.Ordinal).Count() == 6,
            "All six acquisitions, selections, samples and capture evidence receipts must be distinct.");
        foreach (var source in sources)
        {
            Require(Hash(source.AcquisitionIdentity) && Hash(source.SampleHash) && Hash(source.SelectionHash)
                && Hash(source.CaptureArgumentsHash) && Hash(source.ExportArgumentsHash) && Hash(source.SourcePicturesHash)
                && source.Scope == model.Scope && source.Volume == model.Volume && source.AllocationUnit == model.AllocationUnit
                && source.InputBytes is >= 1 and <= 1_000_000_000 && source.FinalBytes > 0
                && source.FinalAllocation >= source.FinalBytes && source.FinalAllocation % model.AllocationUnit == 0
                && source.PeakAllocation >= source.FinalAllocation && source.PeakAllocation % model.AllocationUnit == 0
                && double.IsFinite(source.ExportSeconds) && source.ExportSeconds is >= 298 and <= 300.1
                && source.SegmentCount is >= 298 and <= 305 && source.Closed && Gap(source.MaximumActiveGapMs)
                && source.MaximumActiveGapMs <= receipt.MaximumActiveGapMs
                && Process(source.Capture) && Process(source.Source) && Process(source.Export) && Process(source.Decode)
                && source.AllTracksDecoded && source.ReferenceVideoFormat && source.ReferenceAudioFormat
                && source.NonSilentAudio && source.SourceLineageVerified
                && source.VideoWidth == 1920 && source.VideoHeight == 1080 && source.AudioSampleRate == 48000 && source.AudioChannels == 2
                && double.IsFinite(source.AudioRmsDb) && source.AudioRmsDb <= 0
                && source.SourcePictureIds?.Length == 3 && source.SourcePictureHashes?.Length == 3 && source.SourcePictureHashes.All(Hash));
            int seed = (source.Label.StartsWith('T') ? 13579 : 24680) * 256;
            Require(source.SourcePictureIds.All(id => id >= seed && id < seed + 25000));
            Require(source.Label.EndsWith('1') ? source.SourcePictureIds.All(id => id == seed)
                : source.SourcePictureIds[0] < source.SourcePictureIds[1] && source.SourcePictureIds[1] < source.SourcePictureIds[2]
                    && source.SourcePictureHashes.Distinct().Count() >= 2);
        }
        var training = sources.Take(3).ToArray();
        Require(model.TrainingReceipts.SequenceEqual(training.Select(s => s.SampleHash)));
        long residual = Math.Max(0, training.Max(s => checked(s.FinalBytes - s.InputBytes)));
        long extra = Math.Max(0, training.Max(s => checked(s.PeakAllocation - ReplayReferenceModel.Round(s.FinalBytes, model.AllocationUnit))));
        Require(model.FinalResidual == residual && model.SampledFinalizationExtra == extra
            && model.Revision == ModelRevision(model.Scope, model.Volume, model.AllocationUnit, residual, extra, model.TrainingReceipts, hostIdentity, model.CaptureHeadroom));
        // Coverage is recomputed against the frozen training model; never refit
        // using held-out observations, and keep the final-allocation gate separate.
        Require(sources.All(s => model.CoversObservations(s.InputBytes, s.FinalBytes, s.FinalAllocation, s.PeakAllocation)));
        if (model.CaptureHeadroom != null)
        {
            var headroom = model.CaptureHeadroom;
            Require(sources.All(s => s.CapturePollGrowthBytes.HasValue && s.CaptureStopGrowthBytes.HasValue
                && s.CapturePollGrowthBytes >= 0 && s.CaptureStopGrowthBytes >= 0 && s.CaptureVolume == model.Volume));
            Require(headroom.Scope == model.Scope && headroom.Volume == model.Volume && headroom.AllocationUnit == model.AllocationUnit
                && headroom.PollGrowthBytes == ReplayReferenceModel.CaptureAllowance(training.Max(s => s.CapturePollGrowthBytes.Value), model.AllocationUnit)
                && headroom.StopGrowthBytes == ReplayReferenceModel.CaptureAllowance(training.Max(s => s.CaptureStopGrowthBytes.Value), model.AllocationUnit));
            Require(sources.All(s => s.CapturePollGrowthBytes <= headroom.PollGrowthBytes && s.CaptureStopGrowthBytes <= headroom.StopGrowthBytes));
            Require(ReplayStorageSafety.Assess(new(model.Volume, model.AllocationUnit, long.MaxValue), headroom).SafeToResume);
        }
        _ = model.PeakBytes(1_000_000_000); // reject representational overflow in the whole declared range
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






