namespace F10Recorder;

// A local physical-picture projection, not full-source/AAC/ownership approval.
// Only the completed, protected source's actual index may supply this input.
// A staged caller must still protect every selected source before feeding a
// codec, then match the completed whole plan before mux/publication.
// File volume/identity/allocation-unit and lifetime are Windows owner facts;
// this portable index metadata cannot certify those capabilities.
internal sealed class ReplayBoundaryProjection
{
    internal readonly int Input, First, Count;
    internal readonly long FirstFrame;
    readonly double start, end;
    readonly long bytes;
    readonly string sourceHash;
    readonly byte[] sps, pps;
    readonly long requestedFirst, requestedEnd, clockShift;
    readonly ReplayInterval requestedInterval;
    readonly int framesPerSecond;

    ReplayBoundaryProjection(int input, int first, int count, long firstFrame, ReplayTransportInput source, ReplayInterval interval, int fps)
    {
        Input = input; First = first; Count = count; FirstFrame = firstFrame;
        start = source.Start; end = source.End; bytes = source.Index.Bytes;
        sourceHash = source.Index.Sha256;
        sps = source.Index.Sps.ToArray(); pps = source.Index.Pps.ToArray();
        requestedInterval = interval; framesPerSecond = fps;
        requestedFirst = ReplayHybridPlan.FrameAtOrAfter(interval.Start, fps);
        requestedEnd = ReplayHybridPlan.FrameAtOrAfter(interval.End, fps);
        clockShift = checked(ReplayHybridPlan.OutputEpoch - (long)Math.Round(interval.Start * 90000));
    }

    internal static ReplayBoundaryProjection Create(int input, ReplayTransportInput source,
        ReplayTransportIndex configuration, ReplayInterval interval, int fps)
    {
        if (input < 0) throw new IOException("Invalid boundary source ordinal.");
        ReplayHybridPlan.RequireInterval(interval, fps);
        ReplayHybridPlan.RequireSourceShape(source);
        ReplayHybridPlan.RequireConfiguration(source.Index, configuration);
        var range = ReplayHybridPlan.PhysicalRange(source, fps);
        var part = ReplayHybridPlan.ProjectPictures(range, ReplayHybridPlan.FrameAtOrAfter(interval.Start, fps), ReplayHybridPlan.FrameAtOrAfter(interval.End, fps));
        return part.Count == 0 || part.First == 0 ? null : new(input, part.First, part.Count, part.FirstFrame, source, interval, fps);
    }

    internal bool Matches(ReplayHybridPlan plan, ReplayTransportInput source, ReplayInterval interval, int fps)
    {
        if (interval != requestedInterval || fps != framesPerSecond
            || plan == null || plan.FirstFrame != requestedFirst || plan.EndFrame != requestedEnd || plan.ClockShift != clockShift
            || source?.Index == null || source.Start != start || source.End != end
            || source.Index.Bytes != bytes || source.Index.Sha256 != sourceHash
            || !source.Index.Sps.Span.SequenceEqual(sps) || !source.Index.Pps.Span.SequenceEqual(pps)) return false;
        var parts = plan.Parts.Where(part => part.Input == Input).ToArray();
        return parts.Length == 1 && parts[0].First == First && parts[0].Count == Count && parts[0].FirstFrame == FirstFrame;
    }
}
