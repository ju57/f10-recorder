using System.Globalization;

namespace F10Recorder;

internal sealed record ReplayCopiedAudioFrame(long StartSamples, int Bytes, string PayloadSha256);
internal sealed record ReplayAacCopyPlan(long PrimingSamples, long LogicalSamples,
    IReadOnlyList<ReplayCopiedAudioFrame> Frames, IReadOnlyList<ReplayExpectedAudio> Expected)
{
    // Two complete preceding frames initialize ordinary AAC-LC overlap state.
    // This is not a semantic decode proof; native quality qualification remains
    // required, including arbitrary-content/priming behavior and held-outs.
    internal const double ProtectionSeconds = .08;
    internal static ReplayAacCopyPlan Create(IReadOnlyList<ReplayTransportInput> inputs,
        ReplayHybridPlan video, ReplayInterval interval, Action check = null)
    {
        if (!double.IsFinite(interval.Seconds) || interval.Seconds < 1 - 1e-9 || interval.Seconds > 1800.000001)
            throw new IOException("Invalid replay AAC copy interval.");
        long samples = checked((long)Math.Round(interval.Seconds * 48000));
        var frames = new List<ReplayCopiedAudioFrame>();
        foreach (var part in video.Parts.Where(p => p.Audio))
        {
            check?.Invoke(); var source = inputs[part.Input].Index.AudioFrames;
            if (source == null || source.Count == 0) throw new NotSupportedException("Replay AAC copy needs protected per-frame coded-byte evidence.");
            foreach (var frame in source)
            {
                if ((frames.Count & 255) == 0) check?.Invoke();
                if (frames.Count >= 85000 || frame.Bytes is < 1 or > 8191 || frame.PayloadSha256?.Length != 64
                    || frame.PayloadSha256.Any(c => c is not (>= '0' and <= '9' or >= 'A' and <= 'F')))
                    throw new IOException("Replay AAC coded frame evidence is missing or outside its bound.");
                long shifted = (frame.Pts + video.ClockShift) % ReplayTransportReader.TimestampModulus;
                if (shifted < 0) shifted += ReplayTransportReader.TimestampModulus;
                long position = checked((long)Math.Round(shifted * 48000d / 90000) - ReplayHybridPlan.OutputEpoch * 48000L / 90000);
                if (frames.Count > 0 && position != frames[^1].StartSamples + 1024)
                    throw new IOException("Protected replay AAC frames have a gap or overlap.");
                frames.Add(new(position, frame.Bytes, frame.PayloadSha256));
            }
        }
        int first = frames.FindLastIndex(f => f.StartSamples <= -2048);
        if (first < 0 || frames[first].StartSamples < -3071)
            throw new NotSupportedException("Replay AAC copy requires genuine protected decoder preroll.");
        var selected = frames.Skip(first).TakeWhile(f => f.StartSamples < samples).ToArray();
        if (selected.Length == 0 || selected[^1].StartSamples + 1024 < samples)
            throw new NotSupportedException("Protected replay AAC does not cover the frozen sample endpoint.");
        var expected = selected.Select((f, n) => new ReplayExpectedAudio(n + 1 < selected.Length ? 1024 : samples - f.StartSamples, f.PayloadSha256)).ToArray();
        if (expected.Any(f => f.Duration is < 1 or > 1024)) throw new IOException("Invalid replay AAC trailing sample duration.");
        return new(-selected[0].StartSamples, samples, Array.AsReadOnly(selected), Array.AsReadOnly(expected));
    }
    internal string Filters
    {
        get
        {
            string first = (-PrimingSamples).ToString(CultureInfo.InvariantCulture), end = LogicalSamples.ToString(CultureInfo.InvariantCulture);
            string epoch = (ReplayHybridPlan.OutputEpoch * 48000L / 90000).ToString(CultureInfo.InvariantCulture);
            // Canonicalize to integer audio samples before selecting packets.
            // The second setts sees normalized PTS and trims only the last real
            // packet's duration. No encoded sample or channel is re-created.
            return "setts=time_base=1/48000:prescale=1:pts=PTS-" + epoch + ":dts=DTS-" + epoch + ",noise=amount=0:drop='lt(pts," + first
                + ")+gte(pts," + end + ")',setts=duration='min(DURATION," + end + "-PTS)'";
        }
    }
}
