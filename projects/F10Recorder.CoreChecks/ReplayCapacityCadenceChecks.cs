using F10Recorder;

internal static class ReplayCapacityCadenceChecks
{
    internal static void Run()
    {
        static void Need(bool value, string text) { if (!value) throw new Exception(text); }
        static void Reject(Action body)
        { try { body(); } catch (IOException) { return; } throw new Exception("Expected genuine evidence rejection."); }
        long now = 0, evidence = 0;
        int work = 0; var timing = new ReplayStorageCheckTiming();
        var gate = new ReplayStorageObservationGate(() => { work++; return true; }, 100,
            ReplayCapacityPolicy.CoverageMilliseconds, timing, timestamp: () => now, frequency: 1000, completedEvidenceAt: () => evidence);
        gate.Check(); now = 100;
        for (int i = 0; i < 1000; i++) gate.Check();
        Console.WriteLine($"Lagging external evidence callback burst: actual full observations={work}; expected=2; genuine epoch={evidence}; now={now}.");
        Need(work == 2, "Unchanged external capacity evidence must not trigger full work for every callback inside one100ms cadence.");
        Need(timing.ObservationAttempts == 2 && timing.Observations == 1,
            "Actual full-work attempts must be distinct from genuine external evidence renewals.");
        now = 199; gate.Check(); Need(work == 2, "Full-work cadence must include its own successful completion.");
        now = 200; gate.Check(); Need(work == 3, "Due full work must run even while external evidence stays old.");
        now = 201; gate.MarkChanged(); gate.Check(); Need(work == 4, "A producer mutation must bypass coalescing.");
        now = 202; gate.CheckNow(); Need(work == 5, "A forced boundary must bypass coalescing.");
        // Neither ordinary success nor a forced callback may fabricate a new
        // capacity epoch, including immediately after a recent full check.
        now = ReplayCapacityPolicy.CoverageMilliseconds; gate.Check();
        now++; Reject(gate.Check); Reject(gate.CheckNow); Reject(gate.RequireFreshness);

        now = 0; evidence = 0; bool busy = false; work = 0;
        gate = new(() => { work++; return !busy; }, 100, ReplayCapacityPolicy.CoverageMilliseconds,
            timestamp: () => now, frequency: 1000, completedEvidenceAt: () => evidence);
        gate.Check(); busy = true; now = 100; gate.Check(); now = 101; gate.Check();
        Need(work == 3, "Busy work must remain dirty and retry without supplying evidence or coalescing.");
        Reject(gate.CheckNow);
        busy = false; evidence = 102; now = 102; gate.Check();
        now = 201; gate.Check(); Need(work == 5, "A completed retry should restore its work cadence.");
        now = 202; gate.Check(); Need(work == 6, "A genuine new epoch must not hide due full work.");

        // The observer itself drains completed query results. A recently
        // completed full check must not delay an available result beyond the
        // old epoch's remaining coverage.
        now = 0; evidence = 0; bool queryReady = false; work = 0;
        gate = new(() => { work++; if (queryReady) { evidence = now; queryReady = false; } return true; }, 100,
            ReplayCapacityPolicy.CoverageMilliseconds, timestamp: () => now, frequency: 1000, completedEvidenceAt: () => evidence);
        gate.Check(); now = 1950; gate.Check();
        now = 1980; queryReady = true; gate.Check();
        Console.WriteLine($"Available near-expiry query consumed: {evidence == 1980}; prior work=1950; callback=1980; old epoch=0.");
        Need(evidence == 1980, "Work coalescing must consume available genuine evidence before the previous cover expires.");
        now = 2001; gate.Check(); gate.RequireFreshness();
        now = 3981; Reject(gate.Check); // The consumed result is single-use.

        now = 0; evidence = 0; work = 0;
        gate = new(() => { work++; now += 60; return true; }, 100, ReplayCapacityPolicy.CoverageMilliseconds,
            timestamp: () => now, frequency: 1000, completedEvidenceAt: () => evidence);
        gate.Check(); Need(now == 60 && work == 1, "Controlled full work must advance the real clock.");
        now = 159; gate.Check(); Need(work == 1, "Cadence must start at actual work completion, not its start or old evidence.");
        now = 160; gate.Check(); Need(work == 2 && now == 220, "The next full check must include actual work time.");
        Console.WriteLine("Passed: full-work cadence remains separate from genuine evidence freshness, dirty/forced boundaries and busy retries.");
    }
}
