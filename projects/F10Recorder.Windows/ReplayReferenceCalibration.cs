using System.Security.Cryptography;
using System.Text.Json;

namespace F10Recorder;

// A finite empirical model for this recorder recipe, never an arbitrary MP4 bound.
internal sealed record ReplayReferenceModel(string Revision, string Scope, string Volume, long AllocationUnit,
    long FinalResidual, long SampledFinalizationExtra, string[] TrainingReceipts, ReplayCaptureHeadroom CaptureHeadroom = null)
{
    internal const long Guard = 16 * 1024 * 1024;
    internal const long MaximumInputBytes = 1_000_000_000;
    internal static bool ReferenceSettings(Settings settings) => settings.CaptureMode == "monitor" && settings.DisplayIndex == -1
        && settings.SystemAudio && settings.VideoCodec == "h264_nvenc" && settings.FramesPerSecond == 60
        && settings.BitrateMbps == 20 && settings.ReplayMinutes == 5;
    // Each of at most 305 files may waste one allocation unit. Concat text is
    // separately bounded before writing, including paths, durations and newlines.
    internal static long SnapshotEnvelope(long unit) => checked(Round(MaximumInputBytes, unit) + 305 * unit + Round(65536, unit));
    internal static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    internal static readonly string AssemblyHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(CaptureSession).Assembly.Location)));
    internal static readonly string CoreHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(Settings).Assembly.Location)));
    internal static string ExporterIdentity => MediaDependency.ExecutableSha256 + "/" + AssemblyHash + "/" + CoreHash + "/replay-copy-mp4-v1";
    internal static long Round(long value, long unit) => checked((value / unit + (value % unit == 0 ? 0 : 1)) * unit);
    internal static long CaptureAllowance(long observed, long unit) => Round(checked(observed + Math.Max(Guard, (observed + 19) / 20)), unit);
    internal long FinalBytes(long inputBytes) => checked(inputBytes + FinalResidual + Math.Max(Guard, (inputBytes + 19) / 20));
    internal long PeakBytes(long inputBytes) => checked(Round(FinalBytes(inputBytes), AllocationUnit)
        + SampledFinalizationExtra + Round(Math.Max(Guard, (FinalBytes(inputBytes) + 19) / 20), AllocationUnit));
    internal bool CoversObservations(long inputBytes, long finalEof, long finalAllocation, long sampledPeak) => finalEof >= 0
        && finalEof <= FinalBytes(inputBytes) && finalAllocation >= 0 && finalAllocation <= Round(FinalBytes(inputBytes), AllocationUnit)
        && sampledPeak >= finalAllocation && sampledPeak <= PeakBytes(inputBytes);

    internal ReplayOutputDemand Estimate(ReplayOutputSelection selection, string actualScope, string sourceDirectory)
    {
        if (actualScope != Scope || selection.ExporterIdentity != ExporterIdentity)
            return ReplayOutputDemand.Unmeasured("Reference recipe or exporter changed.");
        double duration = selection.Inputs.Count == 0 ? 0 : selection.End - selection.Inputs[0].Start;
        if (duration < 298 || duration > 300.1 || selection.Inputs.Count < (selection.Closed ? 298 : 297) || selection.Inputs.Count > 305
            || !selection.Closed && (selection.End <= selection.Inputs[^1].End || selection.End - selection.Inputs[^1].End > 1.25))
            return ReplayOutputDemand.Unmeasured("Selection is outside the requested-five-minute reference envelope.");
        if (FinalResidual < 0 || SampledFinalizationExtra < 0 || AllocationUnit <= 0 || TrainingReceipts?.Length != 3 || string.IsNullOrWhiteSpace(Revision))
            throw new IOException("Reference model is incomplete.");
        long bytes = 0;
        foreach (var input in selection.Inputs)
        {
            if (!Path.GetFullPath(input.Path).StartsWith(Path.GetFullPath(sourceDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !input.Path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
                || (File.GetAttributes(input.Path) & (FileAttributes.Compressed | FileAttributes.SparseFile | FileAttributes.ReparsePoint)) != 0)
                return ReplayOutputDemand.Unmeasured("Input provenance or allocation attributes are outside the reference envelope.");
            bytes = checked(bytes + input.Extent.Length);
        }
        if (bytes is < 1 or > MaximumInputBytes) return ReplayOutputDemand.Unmeasured("Selected input bytes exceed the frozen reference range.");
        var target = ReplayStorage.ObserveCapacity(selection.Destination);
        if (target.Volume != Volume || target.AllocationUnit != AllocationUnit)
            return ReplayOutputDemand.Unmeasured("Destination volume/unit has not been calibrated.");
        return new(ReplayOutputEvidenceKind.CalibratedEstimate, Revision, selection.Identity, selection.ExporterIdentity,
            Volume, AllocationUnit, PeakBytes(selection.Closed ? bytes : MaximumInputBytes), !selection.Closed, null,
            selection.Closed ? null : SnapshotEnvelope(AllocationUnit));
    }
}
