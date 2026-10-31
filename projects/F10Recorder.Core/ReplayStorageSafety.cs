namespace F10Recorder;

// Only a qualified, matching model supplies these growth bounds. Missing model
// evidence is represented by null, never by an assumed zero growth allowance.
internal sealed record ReplayCaptureHeadroom(string Scope, string Volume, long AllocationUnit, long PollGrowthBytes, long StopGrowthBytes);
internal sealed record ReplayUnreservedCapacity(string Volume, long AllocationUnit, long Bytes);
internal enum ReplayStorageSafetyKind { Unmeasured, Safe, Warning, Stop }
internal enum ReplayStorageAssessmentSource { Unmeasured, Qualified }
internal sealed class ReplayStorageAssessment
{
    internal ReplayStorageSafetyKind Kind { get; }
    internal bool ShouldWarn { get; }
    internal bool ShouldStop => Kind is ReplayStorageSafetyKind.Stop or ReplayStorageSafetyKind.Unmeasured;
    internal bool SafeToResume { get; }
    internal string Reason { get; }
    internal bool HeadroomKnown => SourceKind == ReplayStorageAssessmentSource.Qualified;
    internal ReplayStorageAssessmentSource SourceKind { get; }
    internal ReplayUnreservedCapacity Capacity { get; }
    internal ReplayCaptureHeadroom Headroom { get; }
    internal ReplayStorageAssessment(ReplayStorageSafetyKind kind, bool warn, bool resume, string reason, ReplayUnreservedCapacity capacity, ReplayCaptureHeadroom headroom, bool qualified)
    {
        Kind = kind; ShouldWarn = warn; SafeToResume = resume; Reason = reason;
        Capacity = capacity; Headroom = headroom;
        SourceKind = qualified ? ReplayStorageAssessmentSource.Qualified : ReplayStorageAssessmentSource.Unmeasured;
    }
}

internal static class ReplayStorageSafety
{
    internal const long WarningBytes = 5_000_000_000;
    internal const long MinimumBytes = 2_000_000_000;
    internal static ReplayStorageAssessment Assess(ReplayUnreservedCapacity capacity, ReplayCaptureHeadroom headroom)
    {
        ReplayStorageAssessment Stop(string reason) => new(ReplayStorageSafetyKind.Stop, false, false, reason, capacity, headroom, false);
        if (capacity == null || !ValidVolume(capacity.Volume) || !ValidUnit(capacity.AllocationUnit) || capacity.Bytes < 0)
            return Stop("Unreserved storage capacity or its volume identity is unavailable or invalid.");
        bool warn = capacity.Bytes < WarningBytes;
        if (headroom == null)
        {
            if (capacity.Bytes <= MinimumBytes)
                return new(ReplayStorageSafetyKind.Stop, warn, false, "Capture headroom is unmeasured and unreserved storage is at or below the necessary two decimal GB floor.", capacity, null, false);
            return new(ReplayStorageSafetyKind.Unmeasured, warn, false, "Capture poll and stop growth have not been qualified for this scope and volume.", capacity, null, false);
        }
        if (!ValidScope(headroom.Scope) || !ValidVolume(headroom.Volume) || !ValidUnit(headroom.AllocationUnit)
            || !string.Equals(capacity.Volume, headroom.Volume, StringComparison.OrdinalIgnoreCase)
            || capacity.AllocationUnit != headroom.AllocationUnit || headroom.PollGrowthBytes < 0 || headroom.StopGrowthBytes < 0)
            return Stop("Capture headroom is invalid or does not match the observed volume and allocation unit.");
        long threshold;
        try { threshold = checked(MinimumBytes + headroom.PollGrowthBytes + headroom.StopGrowthBytes); }
        catch (OverflowException) { return Stop("Qualified capture headroom exceeds the supported byte range."); }
        if (capacity.Bytes <= threshold)
            return new(ReplayStorageSafetyKind.Stop, warn, false, "Unreserved storage has reached the qualified capture stop boundary.", capacity, headroom, true);
        return new(warn ? ReplayStorageSafetyKind.Warning : ReplayStorageSafetyKind.Safe, warn, true,
            warn ? "Unreserved storage is below five decimal GB." : "Unreserved storage exceeds the qualified capture stop boundary.", capacity, headroom, true);
    }
    static bool ValidScope(string scope) => scope != null && scope.Length == 64 && scope.All(Uri.IsHexDigit);
    static bool ValidUnit(long unit) => unit > 0 && unit <= (1L << 32) && (unit & (unit - 1)) == 0;
    static bool ValidVolume(string volume) => volume != null && volume.Length == 49
        && volume.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase) && volume.EndsWith(@"}\", StringComparison.Ordinal)
        && Guid.TryParseExact(volume.Substring(11, 36), "D", out _);
}
