using F10Recorder;

internal static class ReplayStorageObservationChecks
{
    internal static void Run()
    {
        static void Require(bool value, string text) { if (!value) throw new Exception(text); }
        static void Reject(Action action)
        { try { action(); } catch (IOException) { return; } throw new Exception("Stale storage evidence should reject."); }
        long now = 0; int observations = 0; var timing = new ReplayStorageCheckTiming();
        var gate = new ReplayStorageObservationGate(() => { observations++; return true; }, 100, 500, timing, () => now, 1000);
        for (int n = 0; n < 1000; n++) gate.Check();
        Require(observations == 1 && timing.Coalesced == 999, "A callback burst must share one actual fresh observation.");
        now = 99; gate.Check(); Require(observations == 1, "Premature full storage refresh.");
        now = 100; gate.Check(); Require(observations == 2, "Due refresh must occur.");
        now = 101; gate.MarkChanged(); gate.Check(); Require(observations == 3, "Producer changes must invalidate freshness immediately.");
        now = 102; gate.CheckNow(); Require(observations == 4, "Critical boundary must refresh even inside the interval.");

        now = 0; bool busy = false; observations = 0;
        gate = new(() => { observations++; return !busy; }, 100, 500, timestamp: () => now, frequency: 1000);
        gate.Check(); busy = true;
        for (now = 100; now < 500; now += 10) gate.Check();
        now = 500; Reject(gate.Check);
        now = 501; Reject(gate.Check);
        Require(observations == 42, "Busy retries must neither coalesce nor advance successful observation time.");

        now = 0; busy = false;
        gate = new(() => !busy, 100, 500, timestamp: () => now, frequency: 1000);
        gate.Check(); busy = true; now = 1; Reject(gate.CheckNow);

        now = 0;
        gate = new(() => { now += 501; return true; }, 100, 500, timestamp: () => now, frequency: 1000);
        Reject(gate.Check); // Work itself belongs in the maximum gap.
        now = 0; bool changed = false;
        gate = new(() => { if (changed) throw new IOException("Output identity changed."); return true; }, 100, 500, timestamp: () => now, frequency: 1000);
        gate.Check(); changed = true; now = 1; gate.Check();
        Reject(gate.CheckNow); // Final checking must observe changes hidden during a short coalesced interval.
        now = -1; Reject(gate.Check);
        now = 0;
        gate = new(() => true, 100, 500, timestamp: () => now, frequency: 1000);
        gate.Check(); now = 50; gate.Check(); now = 40; Reject(gate.Check);
        now = 0;
        gate = new(() => { if (now == 50) now = 40; return true; }, 100, 500, timestamp: () => now, frequency: 1000);
        gate.Check(); now = 50; Reject(gate.CheckNow);
        using var serialized = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(timing));
        Require(serialized.RootElement.GetProperty("Callbacks").GetInt64() == timing.Callbacks
            && serialized.RootElement.GetProperty("Coalesced").GetInt64() == timing.Coalesced
            && serialized.RootElement.GetProperty("ObservationAttempts").GetInt64() == timing.ObservationAttempts
            && serialized.RootElement.TryGetProperty("ObservationMilliseconds", out _), "Storage timing must survive the default report serializer.");
        now = 0; gate = new(() => true, 100, 500, timestamp: () => now, frequency: 1000); gate.Check();
        now = 50; gate.RequireFreshness(); now = 490; gate.RequireFreshness(); now = 501; Reject(gate.RequireFreshness);
        Console.WriteLine("Passed: storage callback coalescing preserves deadlines, busy evidence, producer invalidation and forced final checks.");
    }
}
