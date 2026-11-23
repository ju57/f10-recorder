using F10Recorder;

internal static class ReplayAacCopyPlanChecks
{
    static void Require(bool value, string why) { if (!value) throw new Exception(why); }
    static void Reject<T>(Action action, string why) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception(why); }
    internal static void Run()
    {
        foreach (int seconds in new[] { 1, 60, 300, 1800 })
        foreach (int phase in new[] { 0, 1, 255, 512, 1023 })
        {
            var input = Input(seconds, phase); var interval = new ReplayInterval(0, seconds, seconds); var video = Plan();
            var copy = ReplayAacCopyPlan.Create([input], video, interval);
            Require(copy.PrimingSamples is >= 2048 and <= 3071 && copy.LogicalSamples == seconds * 48000L
                && copy.Expected.Sum(p => p.Duration) - copy.PrimingSamples == seconds * 48000L
                && copy.Expected.Take(copy.Expected.Count - 1).All(p => p.Duration == 1024)
                && copy.Expected[^1].Duration is >= 1 and <= 1024, "AAC copy changed genuine preroll or physical sample coverage.");
            Require(copy.Frames.Zip(copy.Expected).All(p => p.First.PayloadSha256 == p.Second.PayloadSha256)
                && copy.Filters.Contains("time_base=1/48000:prescale=1") && copy.Filters.Contains("amount=0"), "AAC copy lost source-byte proof or canonical sample clock.");
        }
        var good = Input(1, 0); var selected = new ReplayInterval(0, 1, 1);
        double minimumStart = 203d / 60;
        Require(ReplayAacCopyPlan.Create([good], Plan(), new(minimumStart, minimumStart + 1, 1)).LogicalSamples == 48000,
            "An exact minimum interval was lost to floating-point subtraction.");
        Reject<IOException>(() => ReplayAacCopyPlan.Create([good], Plan(), new(0, .999, 1)), "A genuinely short interval was admitted.");
        Reject<NotSupportedException>(() => ReplayAacCopyPlan.Create([good with { Index = good.Index with { AudioFrames = good.Index.AudioFrames.Where(f => f.Pts >= ReplayHybridPlan.OutputEpoch).ToArray() } }], Plan(), selected),
            "Missing genuine source preroll was synthesized.");
        Reject<NotSupportedException>(() => ReplayAacCopyPlan.Create([good with { Index = good.Index with { AudioFrames = good.Index.AudioFrames.Take(10).ToArray() } }], Plan(), selected),
            "Missing AAC tail was padded.");
        Reject<IOException>(() => ReplayAacCopyPlan.Create([good with { Index = good.Index with { AudioFrames = good.Index.AudioFrames.Where((_, n) => n != 10).ToArray() } }], Plan(), selected),
            "An AAC frame gap was hidden.");
        Reject<IOException>(() => ReplayAacCopyPlan.Create([good with { Index = good.Index with { AudioFrames = good.Index.AudioFrames.Select((f, n) => n == 10 ? f with { PayloadSha256 = null } : f).ToArray() } }], Plan(), selected),
            "Missing AAC byte proof was accepted.");
        var fractional = ReplayAacCopyPlan.Create([good], Plan(), new(0, 1 + .25 / 48000, 1));
        Require(fractional.LogicalSamples == 48000, "AAC endpoint did not use the explicit nearest-sample clock policy.");
        foreach (long shift in new[] { 1L, -1L, 900001L, -900001L, ReplayTransportReader.TimestampModulus - 1 })
        {
            long Modulo(long value) => (value % ReplayTransportReader.TimestampModulus + ReplayTransportReader.TimestampModulus) % ReplayTransportReader.TimestampModulus;
            var shifted = good with { Index = good.Index with { AudioFrames = good.Index.AudioFrames.Select(f => f with { Pts = Modulo(f.Pts - shift) }).ToArray() } };
            var actual = ReplayAacCopyPlan.Create([shifted], Plan() with { ClockShift = shift }, selected);
            var baseline = ReplayAacCopyPlan.Create([good], Plan(), selected);
            Require(actual.PrimingSamples == baseline.PrimingSamples && actual.Frames.SequenceEqual(baseline.Frames)
                && actual.Expected.SequenceEqual(baseline.Expected), "AAC clock shift or33bit wrap changed selected physical samples.");
        }
        foreach (var endpoint in new[] { (Extra: .49, Samples: 48000L), (Extra: .51, Samples: 48001L), (Extra: 1.49, Samples: 48001L), (Extra: 1.51, Samples: 48002L) })
        {
            var actual = ReplayAacCopyPlan.Create([good], Plan(), new(0, 1 + endpoint.Extra / 48000, 1));
            Require(actual.LogicalSamples == endpoint.Samples && actual.Expected.Sum(a => a.Duration) - actual.PrimingSamples == endpoint.Samples,
                "AAC nearest-sample endpoint changed around a half-sample boundary.");
        }
        Console.WriteLine("Passed: copied AAC 1/5/30-minute sample coverage, source phases, genuine preroll, no padding/gaps and complete packet proof; no decode/publication.");
    }
    static ReplayHybridPlan Plan() => new(0, 60, 0, new[] { new ReplayHybridPart(0, 0, 0, 0, true) });
    static ReplayTransportInput Input(int seconds, int phase)
    {
        int count = checked((int)Math.Ceiling(seconds * 48000d / 1024) + 12);
        var frames = Enumerable.Range(0, count).Select(n => new ReplayTransportAudioFrame(
            ReplayHybridPlan.OutputEpoch + (long)Math.Round((-4096 + phase + n * 1024L) * 90000d / 48000), 200, new string('A', 64))).ToArray();
        var index = new ReplayTransportIndex(188, "", Array.Empty<ReplayTransportPicture>(), Array.Empty<ReplayTransportAudio>(),
            Array.Empty<ReplayTransportTiming>(), ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty, frames);
        return new(0, seconds, index);
    }
}
