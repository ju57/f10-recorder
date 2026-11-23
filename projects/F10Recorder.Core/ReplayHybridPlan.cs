namespace F10Recorder;

internal sealed record ReplayTransportInput(double Start, double End, ReplayTransportIndex Index);
internal sealed record ReplayHybridPart(int Input, int First, int Count, long FirstFrame, bool Audio);
internal sealed record ReplayHybridPlan(long FirstFrame, long EndFrame, long ClockShift,
    IReadOnlyList<ReplayHybridPart> Parts)
{
    // Positive unpublished transport epoch keeps protected AAC preroll before
    // zero until the final common-clock trim. It does not create output history.
    internal const long OutputEpoch = 900000;
    internal const double InputPrerollSeconds = 0.05;

    internal static ReplayHybridPlan Create(IReadOnlyList<ReplayTransportInput> inputs, ReplayInterval interval, int fps, bool audio)
    {
        RequireInterval(interval, fps);
        if (inputs.Count == 0) throw new IOException("Invalid replay hybrid interval or source clock.");
        long step = 90000 / fps, first = FrameAtOrAfter(interval.Start, fps), end = FrameAtOrAfter(interval.End, fps);
        long shift = checked(OutputEpoch - (long)Math.Round(interval.Start * 90000));
        long? previousEnd = null;
        var pictures = new List<(long First, long End)>();
        var samples = new List<(int Input, long Start, long End)>();
        var configuration = inputs[0].Index;
        for (int n = 0; n < inputs.Count; n++)
        {
            var source = inputs[n]; var index = source.Index;
            RequireSourceShape(source);
            RequireConfiguration(index, configuration);
            var range = PhysicalRange(source, fps);
            long startFrame = range.First, endFrame = range.End, startClock = checked(startFrame * step);
            if (previousEnd.HasValue && startFrame != previousEnd.Value)
                throw new IOException("Protected replay chunks have a physical picture gap or overlap.");
            previousEnd = endFrame; pictures.Add((startFrame, endFrame));
            if (audio)
                foreach (var packet in index.Audio)
                {
                    long start = Unwrap(packet.Pts, startClock);
                    long finish = checked(start + packet.Samples * 90000L / 48000);
                    if (packet.Samples <= 0 || Math.Abs(start - startClock) > 90000 * 121L)
                        throw new IOException("Replay AAC clock extent is invalid.");
                    samples.Add((n, start, finish));
                }
        }
        if (pictures[0].First > first || pictures[^1].End < end)
            throw new IOException("Protected replay inputs do not cover the frozen requested pictures.");
        long audioFirst = checked((long)Math.Floor(interval.Start * 90000) - 3840), audioEnd = checked((long)Math.Ceiling(interval.End * 90000));
        var audioInputs = new HashSet<int>();
        if (audio)
        {
            var selected = samples.Where(s => s.End > audioFirst && s.Start < audioEnd).OrderBy(s => s.Start).ToArray();
            if (selected.Length == 0 || selected[0].Start > audioFirst || selected[^1].End < audioEnd)
                throw new NotSupportedException("Protected AAC preroll/tail coverage requires the existing exporter.");
            for (int n = 1; n < selected.Length; n++)
                if (selected[n].Start != selected[n - 1].End) throw new IOException("Protected AAC chunks have a sample gap or overlap.");
            foreach (var sample in selected) audioInputs.Add(sample.Input);
        }
        var parts = new List<ReplayHybridPart>();
        for (int n = 0; n < inputs.Count; n++)
        {
            var part = ProjectPictures(pictures[n], first, end);
            if (part.Count != 0 || audioInputs.Contains(n))
                parts.Add(new(n, part.First, part.Count, part.FirstFrame, audioInputs.Contains(n)));
        }
        return new(first, end, shift, parts.AsReadOnly());
    }

    internal static (int First, int Count, long FirstFrame) ProjectPictures((long First, long End) source, long requestedFirst, long requestedEnd)
    {
        long left = Math.Max(requestedFirst, source.First), right = Math.Min(requestedEnd, source.End);
        int count = checked((int)Math.Max(0, right - left));
        return (count == 0 ? 0 : checked((int)(left - source.First)), count, left);
    }

    internal static void RequireInterval(ReplayInterval interval, int fps)
    {
        if (interval == null || fps <= 0 || 90000 % fps != 0 || !double.IsFinite(interval.Start)
            || !double.IsFinite(interval.End) || interval.Start < 0 || interval.Seconds < 1 - 1e-9 || interval.Seconds > 1800.000001)
            throw new IOException("Invalid replay hybrid interval or source clock.");
    }

    internal static void RequireSourceShape(ReplayTransportInput source)
    {
        if (source?.Index == null || !double.IsFinite(source.Start) || !double.IsFinite(source.End) || source.End <= source.Start
            || source.Index.Pictures.Count == 0 || !source.Index.Pictures[0].Idr || source.Index.Sps.IsEmpty || source.Index.Pps.IsEmpty)
            throw new IOException("Replay hybrid source interval is invalid.");
    }

    internal static void RequireConfiguration(ReplayTransportIndex index, ReplayTransportIndex configuration)
    {
        if (configuration == null || configuration.Sps.IsEmpty || configuration.Pps.IsEmpty)
            throw new IOException("Replay boundary configuration is absent.");
        if (!index.Sps.Span.SequenceEqual(configuration.Sps.Span) || !index.Pps.Span.SequenceEqual(configuration.Pps.Span))
            throw new NotSupportedException("Replay source configuration changes require the existing exporter.");
    }

    internal static (long First, long End) PhysicalRange(ReplayTransportInput source, int fps)
    {
        RequireSourceShape(source);
        if (fps <= 0 || 90000 % fps != 0) throw new IOException("Invalid replay physical source clock.");
        long step = 90000 / fps, start = Unwrap(source.Index.Pictures[0].Pts, source.Start * 90000);
        if (Math.Abs(start - source.Start * 90000) > 0.1 || Math.Abs(start + source.Index.Pictures.Count * step - source.End * 90000) > 0.1)
            throw new IOException("Replay source manifest disagrees with its physical picture clock.");
        if (start % step != 0) throw new NotSupportedException("Replay source picture phase requires the existing exporter.");
        for (int p = 0; p < source.Index.Pictures.Count; p++)
            if (Modulo(start + p * step) != source.Index.Pictures[p].Pts)
                throw new IOException("Replay physical picture clock has a gap or overlap.");
        long first = start / step;
        return (first, checked(first + source.Index.Pictures.Count));
    }

    internal static long FrameAtOrAfter(double cutoff, int fps)
    {
        // A rounded multiplication can put an exact boundary just above its
        // integer frame (62/60*60 is62.00000000000001). Compare source frame
        // timestamps with the actual cutoff, including adjacent double values.
        long frame = checked((long)Math.Floor(cutoff * fps));
        if (frame / (double)fps < cutoff) frame++;
        if (frame > 0 && (frame - 1) / (double)fps >= cutoff) frame--;
        return frame;
    }

    static long Unwrap(long value, double near)
    {
        if (value < 0 || value >= ReplayTransportReader.TimestampModulus || !double.IsFinite(near) || near < 0 || near > 1e15)
            throw new IOException("Replay transport epoch is outside its supported bound.");
        return checked(value + (long)Math.Round((near - value) / ReplayTransportReader.TimestampModulus) * ReplayTransportReader.TimestampModulus);
    }
    static long Modulo(long value) => (value % ReplayTransportReader.TimestampModulus + ReplayTransportReader.TimestampModulus) % ReplayTransportReader.TimestampModulus;
}
