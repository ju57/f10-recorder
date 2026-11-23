using F10Recorder;

internal static class ReplayCapacityObservationChecks
{
    internal static void Run()
    {
        static void Require(bool value, string text) { if (!value) throw new Exception(text); }
        long clock = 100; int calls = 0;
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var query = new ReplayCapacityObservation<ReplayStorageAssessment>(() =>
        {
            Interlocked.Increment(ref calls); entered.Set();
            if (!release.Wait(2000)) throw new IOException("Controlled capacity query release timed out.");
            return null;
        }, () => Interlocked.Read(ref clock));
        Require(query.Start() && entered.Wait(1000), "A capacity query must start.");
        for (int i = 0; i < 1000; i++) Require(!query.Start(), "A blocked query must not accumulate replacements.");
        Require(!query.TryComplete(out _) && calls == 1 && query.Running, "Pending work cannot fabricate successful evidence.");
        Interlocked.Exchange(ref clock, 709); release.Set();
        Require(SpinWait.SpinUntil(() => !query.Running, 1000), "Controlled query must join.");
        Require(query.TryComplete(out var completed) && completed.Started == 100 && completed.Finished == 709,
            "The actual609ms query must retain both its original start and genuine completion.");
        Require(!query.TryComplete(out _), "A completion may be consumed only once.");
        const string volume = @"\\?\Volume{00000000-0000-0000-0000-000000000001}\";
        var legacy = new ReplayCaptureHeadroom(new string('A', 64), volume, 4096, 65536, 32768);
        var capacity = new ReplayUnreservedCapacity(volume, 4096, 9_000_000_000);
        Require(ReplayStorageSafety.Assess(capacity, legacy).ShouldStop,
            "Old500ms evidence must not gain a larger growth horizon by default.");
        Require(!ReplayStorageSafety.Assess(capacity, legacy with { ObservationMilliseconds = ReplayCapacityPolicy.CoverageMilliseconds }).ShouldStop,
            "An explicit matching controlled horizon should be admissible with cover.");
        var failed = new ReplayCapacityObservation<ReplayStorageAssessment>(() => throw new IOException("Controlled capacity failure."));
        Require(failed.Start() && SpinWait.SpinUntil(() => !failed.Running, 1000), "Controlled failure must terminate.");
        bool rejected = false;
        try { failed.TryComplete(out _); } catch (IOException) { rejected = true; }
        Require(rejected && !failed.Running, "Query errors must be observed, not reported as capacity.");
        long now = 0, evidence = 0;
        var gate = new ReplayStorageObservationGate(() => true, 100, ReplayCapacityPolicy.CoverageMilliseconds,
            timestamp: () => now, frequency: 1000, completedEvidenceAt: () => evidence);
        gate.Check(); now = 609; gate.CheckNow();
        now = ReplayCapacityPolicy.CoverageMilliseconds + 1;
        rejected = false;
        try { gate.CheckNow(); } catch (IOException) { rejected = true; }
        Require(rejected, "Callbacks and forced boundaries must not refresh the genuine capacity evidence epoch.");
        Console.WriteLine("Passed: one in-flight capacity query, genuine609ms timing, single-use completion, error and legacy-horizon rejection.");
    }
}
