using System.Diagnostics;

namespace F10Recorder;

// Only for serial CPU planning over immutable indexed evidence. I/O, producer
// transitions and publication keep their own immediate guards. This schedule
// never supplies capacity evidence; the callback owns the real freshness check.
internal sealed class ReplayPlanningGuard
{
    readonly Action check;
    readonly Func<long> timestamp;
    readonly long frequency;
    long lastRead, started;
    bool checkedOnce, failed;

    internal ReplayPlanningGuard(Action check, Func<long> timestamp = null, long frequency = 0)
    {
        if (frequency < 0) throw new ArgumentOutOfRangeException(nameof(frequency));
        this.check = check;
        this.timestamp = timestamp ?? Stopwatch.GetTimestamp;
        this.frequency = frequency == 0 ? Stopwatch.Frequency : frequency;
        lastRead = this.timestamp();
    }

    internal void Check() => Observe(false);
    internal void CheckNow() => Observe(true);
    void Observe(bool force)
    {
        if (failed) throw new IOException("Replay planning cannot resume after a failed guard.");
        try
        {
            long now = timestamp();
            if (now < lastRead) throw new IOException("Replay planning clock moved backwards.");
            lastRead = now;
            if (!force && checkedOnce && (now - (double)started) * 1000 / frequency < 100) return;
            // Start cadence includes the callback's lock/filesystem work. A
            // skipped call does not renew either this clock or storage evidence.
            started = now;
            check?.Invoke();
            long completed = timestamp();
            if (completed < lastRead) throw new IOException("Replay planning clock moved backwards during observation.");
            lastRead = completed;
            checkedOnce = true;
        }
        catch { failed = true; throw; }
    }
}
