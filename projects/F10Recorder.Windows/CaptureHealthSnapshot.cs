namespace F10Recorder;

// An observation, retained before shutdown changes process/audio state.
internal sealed record CaptureHealthSnapshot(bool HasExited, int? ExitCode, string AudioFailure,
    double ElapsedSeconds, double ProgressSeconds, double ProgressAgeSeconds,
    double CompletedSegmentEnd, double SegmentAgeSeconds, long EncodedFrames, string Diagnostics)
{
    public bool Healthy => !HasExited && AudioFailure == null && SegmentAgeSeconds < 15 && ProgressAgeSeconds < 15;
}
