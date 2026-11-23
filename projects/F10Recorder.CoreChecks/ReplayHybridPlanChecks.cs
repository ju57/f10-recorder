using F10Recorder;

internal static class ReplayHybridPlanChecks
{
    static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    static void Reject<T>(Action action, string message) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception(message); }

    internal static void Run()
    {
        // Metadata fixtures test selection/epoch math only, never decoding.
        foreach (int fps in new[] { 24, 60, 120 })
            foreach (int seconds in new[] { 1, 300, 1800 })
            {
                var inputs = Enumerable.Range(0, seconds + 3).Select(n => Input(n * (long)fps, fps, fps)).ToArray();
                var interval = new ReplayInterval(1.25, seconds + 1.25, seconds);
                var plan = ReplayHybridPlan.Create(inputs, interval, fps, true);
                Require(plan.FirstFrame == (long)Math.Ceiling(interval.Start * fps) && plan.EndFrame == (long)Math.Ceiling(interval.End * fps)
                    && plan.Parts.Sum(p => p.Count) == seconds * fps, "Hybrid selection changed frozen physical frame boundaries.");
                Require(plan.Parts.Where(p => p.Count > 0).First().First == fps / 4
                    && plan.Parts.Where(p => p.Count > 0).Last().Count == fps / 4,
                    "Partial boundary chunks must use exact picture offsets, not GOP ordinals.");
                Require(plan.ClockShift == ReplayHybridPlan.OutputEpoch - (long)Math.Round(interval.Start * 90000),
                    "All selected source streams require one common-clock translation.");
            }
        const int rate = 60;
        foreach (int fps in new[] { 24, 60, 120 })
        {
            var boundaries = Enumerable.Range(0, 7).Select(n => Input(n * (long)fps, fps, fps)).ToArray();
            double start = (fps + 2d) / fps, finish = (4d * fps + 2) / fps;
            foreach (var (cutoff, expected) in new[] { (Math.BitDecrement(start), fps + 2L), (start, fps + 2L), (Math.BitIncrement(start), fps + 3L) })
            {
                var exact = ReplayHybridPlan.Create(boundaries, new(cutoff, finish, 3), fps, true);
                Require(exact.FirstFrame == expected && exact.EndFrame == 4L * fps + 2
                    && exact.Parts.Sum(p => p.Count) == 4L * fps + 2 - expected, "An exact/adjacent source frame boundary selected the wrong picture.");
            }
        }
        long epochFrame = ((long)Math.Ceiling(ReplayTransportReader.TimestampModulus / 90000d) + 24) * rate;
        var wrapped = Enumerable.Range(-2, 9).Select(n => Input(epochFrame + n * rate, rate, rate)).ToArray();
        var wrappedInterval = new ReplayInterval(epochFrame / (double)rate + 0.25, epochFrame / (double)rate + 5.25, 5);
        var mapped = ReplayHybridPlan.Create(wrapped, wrappedInterval, rate, true);
        Require(mapped.FirstFrame == epochFrame + 15 && mapped.Parts.Sum(p => p.Count) == 300,
            "Transport wrap lost the acquisition epoch or selected the wrong source pictures.");
        long crossingFrame = 95442 * rate;
        var crossing = new[] { Input(crossingFrame - 60, 180, rate), Input(crossingFrame + 120, 30, rate),
            Input(crossingFrame + 150, 180, rate), Input(crossingFrame + 330, 30, rate) };
        Require(crossing[0].Index.Pictures[0].Pts > crossing[0].Index.Pictures[^1].Pts
            && crossing[0].Index.Audio[0].Pts > crossing[0].Index.Audio[^1].Pts, "The fixture must physically cross both transport clock wraps.");
        var crossingPlan = ReplayHybridPlan.Create(crossing, new(95442.25, 95447.25, 5), rate, true);
        Require(crossingPlan.FirstFrame == crossingFrame + 15 && crossingPlan.EndFrame == crossingFrame + 315
            && crossingPlan.Parts.Sum(p => p.Count) == 300, "Variable-size chunks crossing modulo zero changed source boundaries.");
        var regular = Enumerable.Range(0, 6).Select(n => Input(n * 60, 60, 60)).ToArray();
        var request = new ReplayInterval(1.25, 4.25, 3);
        Reject<IOException>(() => ReplayHybridPlan.Create([regular[0], regular[2], regular[3], regular[4]], request, 60, true),
            "Missing protected video chunk was padded or shortened.");
        var damaged = regular.ToArray();
        damaged[2] = damaged[2] with { End = damaged[2].End + 0.0001 };
        Reject<IOException>(() => ReplayHybridPlan.Create(damaged, request, 60, true), "Manifest/source clock mismatch was accepted.");
        damaged = regular.ToArray(); var audio = damaged[2].Index.Audio.ToArray(); audio[0] = audio[0] with { Pts = audio[0].Pts + 90 };
        damaged[2] = damaged[2] with { Index = damaged[2].Index with { Audio = audio } };
        Reject<IOException>(() => ReplayHybridPlan.Create(damaged, request, 60, true), "AAC context gap was silently normalized.");
        damaged = regular.ToArray(); damaged[2] = damaged[2] with { Index = damaged[2].Index with { Sps = new byte[] { 9 } } };
        Reject<NotSupportedException>(() => ReplayHybridPlan.Create(damaged, request, 60, true), "Different SPS was silently spliced.");
        Reject<NotSupportedException>(() => ReplayHybridPlan.Create(regular, new(0, 2, 300), 60, true),
            "Warm-up without the required real AAC preroll entered the fast path.");
        var warm = ReplayHybridPlan.Create(regular, new(0, 2, 300), 60, false);
        Require(warm.FirstFrame == 0 && warm.EndFrame == 120, "Video-only warm-up invented requested history.");
        Console.WriteLine("Passed: hybrid physical cuts at1/5/30minutes,24/60/120FPS,33-bit epoch mapping, honest warm-up and source/AAC gap rejection.");
    }

    internal static ReplayTransportInput Input(long firstFrame, int count, int fps)
    {
        long step = 90000 / fps, start = firstFrame * step, end = start + count * step;
        long Mod(long value) => (value % ReplayTransportReader.TimestampModulus + ReplayTransportReader.TimestampModulus) % ReplayTransportReader.TimestampModulus;
        var pictures = Enumerable.Range(0, count).Select(n => new ReplayTransportPicture(n * 188, Mod(start + n * step), n == 0, 1)).ToArray();
        var audio = new List<ReplayTransportAudio>();
        for (long pts = (long)Math.Ceiling(start / 1920d) * 1920; pts < end; pts += 1920)
            audio.Add(new(0, Mod(pts), 1024));
        return new(start / 90000d, end / 90000d, new(188, "metadata-fixture", pictures, audio, [], new byte[] { 1 }, new byte[] { 2 }));
    }
}
