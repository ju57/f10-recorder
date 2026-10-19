using F10Recorder;

internal static class AudioPacketChecks
{
    internal static void Run(string directory)
    {
        void Require(bool ok, string why) { if (!ok) throw new Exception(why); }
        void Reject(Action action, string why) { bool rejected = false; try { action(); } catch (IOException) { rejected = true; } Require(rejected, why); }
        const int rate = 48000, frames = 480;
        File.WriteAllText(Path.Combine(directory, "audio-resampler-filter.txt"), AudioPacketClock.ResamplerFilter);
        foreach (int ppm in new[] { -500, -100, 0, 100, 500 })
        {
            var clock = new AudioPacketClock(rate); long last = -1; double finalError = 0;
            // Thirty minutes of modeled clock drift, with alternating 0.6ms jitter.
            for (int i = 0; i < 180000; i++)
            {
                long nominal = i * (long)frames;
                long observed = 24000 + (long)Math.Round(nominal * (1 + ppm / 1000000.0)) + (i == 0 ? 0 : i % 2 == 0 ? 29 : -29);
                var time = clock.Map((ulong)nominal, frames, observed, true, i == 0);
                Require(time.Start > last && time.Boundary == (i == 0), "Jitter/drift must not break native packet order or create boundaries.");
                last = time.Start; finalError = time.Start - (24000 + nominal * (1 + ppm / 1000000.0));
                if (i > 2000) Require(Math.Abs(finalError) < rate * .008, "Declared +/-500ppm clock envelope did not converge within8ms.");
            }
        }
        var uncertain = new AudioPacketClock(rate);
        uncertain.Map(1000, frames, 2000, true, true);
        for (int i = 1; i <= 100; i++) Require(uncertain.Map(ulong.MaxValue, frames, long.MaxValue, false, false).Start == 2000 + i * frames, "Untrusted timestamp fields must not drive the packet clock.");
        Require(!uncertain.Map(1000 + 101 * (ulong)frames, frames, 2000 + 101 * frames, true, false).Boundary, "A valid counter must reconcile predicted frames after timestamp errors.");
        Reject(() => new AudioPacketClock(rate).Map(0, frames, 0, false, false), "First packet needs a trustworthy epoch.");
        Reject(() => uncertain.Map(0, frames, 0, false, true), "Unlocatable discontinuities need recovery.");
        var lost = new AudioPacketClock(rate); lost.Map(0, frames, 0, true, true);
        for (int i = 0; i < 500; i++) lost.Map(0, frames, 0, false, false);
        Reject(() => lost.Map(0, frames, 0, false, false), "Uncertainty must be bounded.");
        var reset = new AudioPacketClock(rate); reset.Map(100000, frames, 10000, true, true);
        Require(reset.Map(0, frames, 20000, true, true).Boundary, "Marked device resets must establish a new shared-clock anchor.");
        Reject(() => reset.Map(0, frames, 30000, true, false), "Unmarked backwards counters must not silently duplicate payload.");
        var backwards = new AudioPacketClock(rate); backwards.Map(0, frames, 10000, true, true);
        Reject(() => backwards.Map(0, frames, 9999, true, true), "A reset must not move media backwards.");
        using (var resetOverlap = new MemoryStream())
        {
            var buffer = new AudioPacketBuffer(resetOverlap, rate, 1, "s16le", 0);
            buffer.Add(new byte[960 * 2], 0, 0, 1, true); buffer.Advance(0);
            Reject(() => buffer.Add(new byte[480 * 2], 0, 1, 1, true), "A reset must not retreat the prior end and synthesize silence over emitted payload.");
        }
        var jump = new AudioPacketClock(rate); jump.Map(0, rate, 0, true, true);
        Reject(() => jump.Map((ulong)rate, frames, rate + 10000, true, false), "Large unexplained clock jumps need recovery.");
        var outlier = new AudioPacketClock(rate);
        for (int i = 0; i < 500; i++)
        {
            var time = outlier.Map((ulong)(i * frames), frames, i * frames + (i == 100 ? 1440 : 0), true, i == 0);
            Require(Math.Abs(time.Start - i * frames) <= 48, "A single30ms timing outlier must not cause an abrupt acquisition jump.");
        }
        using (var stalled = new MemoryStream())
        {
            var buffer = new AudioPacketBuffer(stalled, rate, 1, "s16le", 0);
            Reject(() => buffer.Advance(rate * 3), "A blocked capture must recover rather than synthesize a large silence burst.");
        }

        foreach (string format in new[] { "u8", "s16le", "s24le", "s32le", "f32le" })
        {
            int width = format == "u8" ? 1 : format == "s16le" ? 2 : format == "s24le" ? 3 : 4;
            string name = Path.Combine(directory, "packet-" + format);
            using var stream = File.Create(name + ".mka");
            using var expected = File.Create(name + "-expected.pcm");
            var buffer = new AudioPacketBuffer(stream, rate, 2, format, 0);
            // Real delivery path: jitter, silent flags, one timestamp-error packet,
            // then recovery. No per-packet sample padding/trimming is permitted.
            for (int i = 0; i < 2000; i++)
            {
                byte[] payload = new byte[frames * 2 * width];
                for (int b = 0; b < payload.Length; b++) payload[b] = format == "f32le" ? BitConverter.GetBytes(.25f + i / 10000f)[b % 4] : (byte)(1 + (i + b) % 120);
                uint flags = i == 10 ? 2u : i == 20 ? 4u : i == 0 ? 1u : 0u;
                if (flags == 2) Array.Fill(payload, (byte)(format == "u8" ? 128 : 0));
                expected.Write(payload);
                buffer.Add(payload, i == 20 ? ulong.MaxValue : (ulong)(i * frames), i * frames + (i == 0 ? 0 : i % 2 == 0 ? 29 : -29), flags, i != 20);
                buffer.Advance(Math.Max(0, i * frames - 4800));
            }
            buffer.Advance(2000L * frames - frames + 100);
        }

        foreach (int delta in new[] { -53, -43, 43, 53, 4752, 4848 })
        {
            using var file = File.Create(Path.Combine(directory, "resampler-step" + delta + ".mka"));
            var writer = new TimestampedAudio(file, rate, 2, "f32le");
            byte[] payload = Enumerable.Range(0, frames * 2).SelectMany(_ => BitConverter.GetBytes(.25f)).ToArray();
            for (int i = 0; i < 2000; i++) writer.WriteAcquired(payload, 0, payload.Length, 24000 + i * (long)frames + (i >= 1000 ? delta : 0));
        }
        using (var idle = new MemoryStream())
        {
            var buffer = new AudioPacketBuffer(idle, rate, 2, "u8", 0);
            buffer.Advance(24000);
            buffer.Add(Enumerable.Repeat((byte)140, frames * 2).ToArray(), 10000, 28800, 1, true);
            buffer.Advance(28800);
            File.WriteAllBytes(Path.Combine(directory, "packet-idle.mka"), idle.ToArray());
            File.WriteAllBytes(Path.Combine(directory, "packet-idle-expected.pcm"), Enumerable.Repeat((byte)128, 28800 * 2).Concat(Enumerable.Repeat((byte)140, frames * 2)).ToArray());
            buffer.Advance(30000);
            Reject(() => { buffer.Add(new byte[frames * 2], 10480, 29280, 0, true); buffer.Advance(30000); }, "Late packets must not silently overwrite emitted inactivity.");
        }
        using (var gaps = new MemoryStream())
        {
            var buffer = new AudioPacketBuffer(gaps, rate, 1, "s16le", 0);
            byte[] payload = Enumerable.Repeat((byte)1, frames * 2).ToArray();
            buffer.Add(payload, 0, 0, 1, true);
            buffer.Add(payload, 960, 960, 1, true);
            buffer.Advance(960);
            File.WriteAllBytes(Path.Combine(directory, "packet-gap.mka"), gaps.ToArray());
            File.WriteAllBytes(Path.Combine(directory, "packet-gap-expected.pcm"), payload.Concat(new byte[960]).Concat(payload).ToArray());
        }
        using (var pausedCounter = new MemoryStream())
        {
            var buffer = new AudioPacketBuffer(pausedCounter, rate, 1, "s16le", 0);
            byte[] payload = Enumerable.Repeat((byte)1, 960).ToArray();
            buffer.Add(payload, 1000, 0, 1, true); buffer.Advance(0);
            buffer.Advance(24000);
            buffer.Add(payload, 1480, 28800, 0, true); buffer.Advance(28800);
            File.WriteAllBytes(Path.Combine(directory, "packet-paused-counter.mka"), pausedCounter.ToArray());
            File.WriteAllBytes(Path.Combine(directory, "packet-paused-counter-expected.pcm"), payload.Concat(new byte[(28800 - frames) * 2]).Concat(payload).ToArray());
        }
        using (var negative = new MemoryStream())
        {
            var buffer = new AudioPacketBuffer(negative, rate, 1, "s16le", 0);
            buffer.Add(Enumerable.Repeat((byte)1, 960).ToArray(), 0, -240, 1, true);
            buffer.Advance(0);
            File.WriteAllBytes(Path.Combine(directory, "packet-negative.mka"), negative.ToArray());
            File.WriteAllBytes(Path.Combine(directory, "packet-negative-expected.pcm"), Enumerable.Repeat((byte)1, 480).ToArray());
        }
        Console.WriteLine("Passed: audio packet continuity, +/-500ppm modeled30minute drift, bounded timestamp uncertainty, silence/idle, gaps, reset, pre-epoch and late-packet recovery.");
    }
}
