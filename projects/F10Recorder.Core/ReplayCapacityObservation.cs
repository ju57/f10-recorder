using System.Diagnostics;

namespace F10Recorder;

// Only capacity queries run here. File identity/write/publication failures stay
// with their existing sticky owners. One unjoined query prevents its replacement.
internal static class ReplayCapacityPolicy
{
    internal const int IntervalMilliseconds = 200;
    internal const int CoverageMilliseconds = 2000;
}

internal sealed class ReplayCapacityObservation<T>
{
    internal sealed record Completed(T Value, long Started, long Finished);
    readonly Func<T> observe;
    readonly Func<long> timestamp;
    Task<Completed> pending;
    long lastStarted, lastFinished;
    internal long LastStarted => Volatile.Read(ref lastStarted);
    internal long LastFinished => Volatile.Read(ref lastFinished);
    internal ReplayCapacityObservation(Func<T> observe, Func<long> timestamp = null)
    {
        this.observe = observe ?? throw new ArgumentNullException(nameof(observe));
        this.timestamp = timestamp ?? Stopwatch.GetTimestamp;
    }
    // Called only by the serialized supervisor. The query never enters its
    // owner's stop lock, and no cancellation pretends to establish query absence.
    internal bool Start()
    {
        if (pending != null) return false;
        long started = timestamp();
        Volatile.Write(ref lastStarted, started); Volatile.Write(ref lastFinished, 0);
        pending = Task.Run(() =>
        {
            T value;
            try { value = observe(); }
            finally { Volatile.Write(ref lastFinished, timestamp()); }
            long finished = LastFinished;
            if (finished < started) throw new IOException("Capacity observation clock moved backwards.");
            return new Completed(value, started, finished);
        });
        return true;
    }
    internal bool Running => Volatile.Read(ref pending) is { IsCompleted: false };
    internal bool WaitForClosure(int milliseconds)
    {
        if (milliseconds < 0) throw new ArgumentOutOfRangeException(nameof(milliseconds));
        if (pending == null) return true;
        try { return pending.Wait(milliseconds); }
        catch (AggregateException) { return true; } // TryComplete observes the original error.
    }
    internal bool TryComplete(out Completed completed)
    {
        completed = null;
        if (pending == null || !pending.IsCompleted) return false;
        var finished = pending;
        pending = null;
        completed = finished.GetAwaiter().GetResult();
        return true;
    }
}
