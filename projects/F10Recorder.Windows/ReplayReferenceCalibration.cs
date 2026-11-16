using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace F10Recorder;

// A finite empirical model for this recorder recipe, never an arbitrary MP4 bound.
internal sealed record ReplayReferenceModel(string Revision, string Scope, string Volume, long AllocationUnit,
    long FinalResidual, long SampledFinalizationExtra, string[] TrainingReceipts, ReplayCaptureHeadroom CaptureHeadroom = null,
    int ReplayMinutes = 5)
{
    // Runtime projection of separately validated v9 evidence. Never serialized
    // into an original measurement model or its historical hash.
    [JsonIgnore] internal ReplayCoverageBinding Coverage { get; init; }
    internal const long Guard = 16 * 1024 * 1024;
    internal const long MaximumInputBytes = 1_000_000_000;
    // Legacy measurements retain their three cells. Only a validated coverage
    // bridge can resolve an intermediate profile into a runtime model.
    internal static bool SupportedMinutes(int minutes) => minutes is 1 or 5 or 30;
    internal static bool CoveredMinutes(int minutes) => minutes is >= 1 and <= 30;
    internal static long InputLimit(int minutes) => minutes switch
    {
        1 => 200_000_000L, >= 2 and <= 5 => MaximumInputBytes, >= 6 and <= 30 => 5_000_000_000L,
        _ => throw new IOException("Replay duration has no reference qualification cell.")
    };
    internal static int InputCountLimit(int minutes) => checked(Seconds(minutes) + 5);
    internal static int Seconds(int minutes) => CoveredMinutes(minutes) ? checked(minutes * 60)
        : throw new IOException("Replay duration has no reference qualification cell.");
    internal static int ConcatLimit(int minutes) => CoveredMinutes(minutes) ? minutes > 5 ? 1024 * 1024 : 65536
        : throw new IOException("Replay duration has no reference qualification cell.");
    internal long InputByteLimit => InputLimit(ReplayMinutes);
    internal static bool ReferenceSettings(Settings settings) => settings.CaptureMode == "monitor" && settings.DisplayIndex == -1
        && settings.SystemAudio && settings.VideoCodec == "h264_nvenc" && settings.FramesPerSecond == 60
        && settings.BitrateMbps == 20 && CoveredMinutes(settings.ReplayMinutes);
    // Each input file may waste one allocation unit. Concat text is
    // separately bounded before writing, including paths, durations and newlines.
    internal static long SnapshotEnvelope(long unit, int replayMinutes = 5) => checked(Round(InputLimit(replayMinutes), unit)
        + InputCountLimit(replayMinutes) * unit + Round(ConcatLimit(replayMinutes), unit));
    internal static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    internal static readonly string AssemblyHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(CaptureSession).Assembly.Location)));
    internal static readonly string CoreHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(Settings).Assembly.Location)));
    internal static string ExporterIdentity => MediaDependency.ExecutableSha256 + "/" + AssemblyHash + "/" + CoreHash + "/" + ReplaySaveExport.PolicyIdentity;
    internal static long Round(long value, long unit) => checked((value / unit + (value % unit == 0 ? 0 : 1)) * unit);
    internal static long CaptureAllowance(long observed, long unit) => Round(checked(observed + Math.Max(Guard, (observed + 19) / 20)), unit);
    internal long FinalBytes(long inputBytes) => checked(inputBytes + FinalResidual + Math.Max(Guard, (inputBytes + 19) / 20));
    internal long PeakBytes(long inputBytes) => checked(Round(FinalBytes(inputBytes), AllocationUnit)
        + SampledFinalizationExtra + Round(Math.Max(Guard, (FinalBytes(inputBytes) + 19) / 20), AllocationUnit)
        + ReplaySaveExport.WorkDemand(inputBytes, AllocationUnit).Total);
    internal bool CoversObservations(long inputBytes, long finalEof, long finalAllocation, long sampledPeak) => finalEof >= 0
        && finalEof <= FinalBytes(inputBytes) && finalAllocation >= 0 && finalAllocation <= Round(FinalBytes(inputBytes), AllocationUnit)
        && sampledPeak >= finalAllocation && sampledPeak <= PeakBytes(inputBytes);

    internal ReplayOutputDemand Estimate(ReplayOutputSelection selection, string actualScope, string sourceDirectory)
    {
        if (!(Coverage == null ? SupportedMinutes(ReplayMinutes) : CoveredMinutes(ReplayMinutes))
            || actualScope != Scope || selection.ExporterIdentity != ExporterIdentity)
            return ReplayOutputDemand.Unmeasured("Reference recipe or exporter changed.");
        double duration = selection.End - selection.Start;
        double inputSpan = selection.Inputs.Count == 0 ? 0 : selection.End - selection.Inputs[0].Start;
        if (!double.IsFinite(duration) || duration < 1 - 0.000000001 || duration > Seconds(ReplayMinutes) + 0.000001
            || inputSpan < duration - 0.000001 || inputSpan > duration + 1.1
            || selection.Inputs.Count < 1 || selection.Inputs.Count > InputCountLimit(ReplayMinutes)
            || !selection.Closed && selection.End <= selection.Inputs[^1].End)
            return ReplayOutputDemand.Unmeasured("Selection is outside its duration-specific reference envelope.");
        if (FinalResidual < 0 || SampledFinalizationExtra < 0 || AllocationUnit <= 0
            || (Coverage == null ? TrainingReceipts?.Length != 3 : TrainingReceipts?.Length != 0)
            || string.IsNullOrWhiteSpace(Revision))
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
        if (bytes < 1 || bytes > InputByteLimit) return ReplayOutputDemand.Unmeasured("Selected input bytes exceed the frozen duration-specific reference range.");
        var target = ReplayStorage.ObserveCapacity(selection.Destination);
        if (target.Volume != Volume || target.AllocationUnit != AllocationUnit)
            return ReplayOutputDemand.Unmeasured("Destination volume/unit has not been calibrated.");
        var readiness = new ReplayOutputReadiness(Hash(this),
            !selection.Closed && selection.End - selection.Inputs[^1].End > 1.25);
        if (readiness.AwaitingCompletedTail)
            return ReplayOutputDemand.Unmeasured("Selection is outside its duration-specific reference envelope.")
                with { Readiness = readiness };
        return new(ReplayOutputEvidenceKind.CalibratedEstimate, Revision, selection.Identity, selection.ExporterIdentity,
            Volume, AllocationUnit, PeakBytes(selection.Closed ? bytes : InputByteLimit), !selection.Closed, null,
            selection.Closed ? null : SnapshotEnvelope(AllocationUnit, ReplayMinutes),
            ReplaySaveExport.WorkDemand(selection.Closed ? bytes : InputByteLimit, AllocationUnit), readiness, ConcatLimit(ReplayMinutes));
    }
}
