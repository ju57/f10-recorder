using F10Recorder;

internal static class ReplayPlanningGuardChecks
{
    static void Need(bool value, string why) { if (!value) throw new Exception(why); }
    static void Reject(Action action)
    { try { action(); } catch (IOException) { return; } throw new Exception("Failed planning guard resumed."); }
    internal static void Run()
    {
        long now = 0; int calls = 0;
        var guard = new ReplayPlanningGuard(() => calls++, () => now, 1000);
        guard.CheckNow();
        for (now = 1; now < 100; now++) guard.Check();
        Need(calls == 1, "Skipped planning calls renewed or bypassed the initial check.");
        guard.Check(); Need(calls == 2, "Due planning check was skipped.");
        guard.CheckNow(); Need(calls == 3, "Forced phase boundary was coalesced.");

        now = 0; calls = 0;
        var slow = new ReplayPlanningGuard(() => { calls++; now += 125; }, () => now, 1000);
        slow.CheckNow(); slow.Check();
        Need(calls == 2, "Filesystem work was excluded by a completion-based cadence.");

        now = 0; calls = 0;
        var broken = new ReplayPlanningGuard(() => { calls++; throw new IOException("guard fault"); }, () => now, 1000);
        Reject(broken.CheckNow); now = 200; Reject(broken.Check); Reject(broken.CheckNow);
        Need(calls == 1, "Failure allowed a new planning observation.");

        now = 100; var backwards = new ReplayPlanningGuard(() => { }, () => now, 1000);
        backwards.CheckNow(); now = 99; Reject(backwards.Check); now = 300; Reject(backwards.CheckNow);
        now = 100;
        var during = new ReplayPlanningGuard(() => now = 99, () => now, 1000);
        Reject(during.CheckNow); now = 300; Reject(during.CheckNow);

        // The schedule must not conceal a missed real-observation deadline.
        now = 0; int observations = 0;
        var storage = new ReplayStorageObservationGate(() => { observations++; return true; }, 100, 500,
            timestamp: () => now, frequency: 1000);
        var coupled = new ReplayPlanningGuard(storage.Check, () => now, 1000);
        coupled.CheckNow(); now = 99; coupled.Check(); now = 501;
        Reject(coupled.Check); Need(observations == 1, "Skipped planning work fabricated fresh storage evidence.");

        now = 0; var busyTiming = new ReplayStorageCheckTiming();
        var busyStorage = new ReplayStorageObservationGate(() => false, 100, 500, busyTiming, () => now, 1000);
        var busy = new ReplayPlanningGuard(busyStorage.Check, () => now, 1000);
        busy.CheckNow(); now = 200; busy.Check(); now = 400; busy.Check(); now = 501;
        Reject(busy.CheckNow);
        Need(busyTiming.Observations == 0 && busyTiming.Deferred == 3, "Busy retries manufactured successful storage evidence.");

        bool persistentAlias = false; now = 0;
        var alias = new ReplayPlanningGuard(() => { if (persistentAlias) throw new IOException("alias"); }, () => now, 1000);
        alias.CheckNow(); persistentAlias = true; now = 1; alias.Check();
        Reject(alias.CheckNow); // Phase exit cannot enter a writer with the fault.

        foreach (int seconds in new[] { 1, 300, 1800 })
        {
            var frames = Enumerable.Range(0, (int)Math.Ceiling(seconds * 48000d / 1024) + 12)
                .Select(n => new ReplayTransportAudioFrame(ReplayHybridPlan.OutputEpoch +
                    (long)Math.Round((-4096 + n * 1024L) * 90000d / 48000), 200, new string('A', 64))).ToArray();
            var input = new ReplayTransportInput(0, seconds, new(188, "", [], [], [], ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty, frames));
            var video = new ReplayHybridPlan(0, seconds * 60L, 0, [new(0, 0, 0, 0, true)]);
            var interval = new ReplayInterval(0, seconds, seconds);
            var direct = ReplayAacCopyPlan.Create([input], video, interval);
            now = 0; int pacedCalls = 0;
            var paced = new ReplayPlanningGuard(() => pacedCalls++, () => now, 1000);
            paced.CheckNow(); var actual = ReplayAacCopyPlan.Create([input], video, interval, paced.Check); paced.CheckNow();
            Need(pacedCalls == 2 && actual.PrimingSamples == direct.PrimingSamples && actual.LogicalSamples == direct.LogicalSamples
                && actual.Frames.SequenceEqual(direct.Frames) && actual.Expected.SequenceEqual(direct.Expected),
                "Planning cadence changed complete AAC source expectations.");
        }
        Console.WriteLine("Passed: planning start cadence, forced boundaries, no skip freshness, slow guards, clock reversal, failure poison and phase-exit rejection.");
    }
}
