using System.Diagnostics;
using System.Text.Json.Serialization;

namespace F10Recorder;

public sealed class ReplayStorageCheckTiming
{
    [JsonInclude] public long Callbacks, ObservationAttempts, Observations, Coalesced, Deferred;
    [JsonInclude] public double ObservationMilliseconds, MaximumObservationMilliseconds;
    [JsonInclude] public double NamespaceMilliseconds, MaximumNamespaceMilliseconds;
    [JsonInclude] public double GateWaitMilliseconds, MaximumGateWaitMilliseconds;
    [JsonInclude] public long CapacityQueries, CapacityCompletions;
    [JsonInclude] public double CapacityQueryMilliseconds, MaximumCapacityQueryMilliseconds;
    [JsonInclude] public bool CapacityQueryPending;
}

// One accepted request, serialized by its owner. Cheap callbacks never renew
// freshness. False means a known busy observer deferred without new evidence.
internal sealed class ReplayStorageObservationGate
{
    readonly Func<bool> observe;
    readonly Func<long> timestamp;
    readonly Func<string, Exception> error;
    readonly long frequency;
    readonly double interval, maximumGap;
    readonly ReplayStorageCheckTiming timing;
    readonly Func<long> completedEvidenceAt;
    long observedAt, lastReadAt, lastWorkCompletedAt;
    bool dirty = true;
    internal ReplayStorageObservationGate(Func<bool> observe, double intervalMilliseconds, double maximumGapMilliseconds,
        ReplayStorageCheckTiming timing = null, Func<long> timestamp = null, long frequency = 0, Func<string, Exception> error = null,
        Func<long> completedEvidenceAt = null)
    {
        if (observe == null || !double.IsFinite(intervalMilliseconds) || !double.IsFinite(maximumGapMilliseconds)
            || intervalMilliseconds <= 0 || intervalMilliseconds >= maximumGapMilliseconds || frequency < 0)
            throw new ArgumentException("Invalid storage observation cadence.");
        this.observe = observe; interval = intervalMilliseconds; maximumGap = maximumGapMilliseconds;
        this.timestamp = timestamp ?? Stopwatch.GetTimestamp; this.frequency = frequency == 0 ? Stopwatch.Frequency : frequency;
        this.error = error ?? (message => new IOException(message)); this.timing = timing ?? new();
        this.completedEvidenceAt = completedEvidenceAt;
        lastReadAt = this.timestamp(); observedAt = completedEvidenceAt?.Invoke() ?? lastReadAt;
        lastWorkCompletedAt = lastReadAt;
    }
    long ReadTimestamp()
    {
        long now = timestamp();
        if (now < lastReadAt) throw error("Storage observation clock moved backwards; evidence is retained.");
        lastReadAt = now; return now;
    }
    double Age(long now)
    {
        if (now < observedAt) throw error("Storage observation clock moved backwards; evidence is retained.");
        return (now - (double)observedAt) * 1000 / frequency;
    }
    void RequireFresh(long now)
    {
        if (Age(now) > maximumGap) throw error("Replay export storage observation exceeded "
            + maximumGap.ToString(System.Globalization.CultureInfo.InvariantCulture) + " ms including filesystem work; evidence is retained.");
    }
    internal void MarkChanged() => dirty = true;
    internal void RequireFreshness() => RequireFresh(ReadTimestamp());
    internal void Check() => Check(false);
    internal void CheckNow() => Check(true);
    void Check(bool force)
    {
        timing.Callbacks++;
        long now = ReadTimestamp(); RequireFresh(now);
        // External capacity evidence can legitimately predate this full check.
        // Its age still controls expiry, but must not turn the100ms work cadence
        // into an inventory on every callback until the next query completes.
        // The observer also consumes completed capacity queries. Near expiry,
        // keep checking so coalescing cannot strand an already available result
        // behind the older epoch's deadline.
        if (!force && !dirty && Age(now) < maximumGap - interval
            && (now - (double)lastWorkCompletedAt) * 1000 / frequency < interval)
        { timing.Coalesced++; return; }
        dirty = true;
        timing.ObservationAttempts++;
        long started = Stopwatch.GetTimestamp(); bool complete;
        try { complete = observe(); }
        finally
        {
            double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            timing.ObservationMilliseconds += elapsed;
            timing.MaximumObservationMilliseconds = Math.Max(timing.MaximumObservationMilliseconds, elapsed);
        }
        now = ReadTimestamp(); RequireFresh(now);
        if (!complete)
        {
            timing.Deferred++;
            if (force || Age(now) >= maximumGap) throw error("Required replay storage observation could not complete; evidence is retained.");
            return;
        }
        long evidenceAt = completedEvidenceAt?.Invoke() ?? now;
        if (evidenceAt < observedAt || evidenceAt > now) throw error("Storage observation evidence epoch is invalid.");
        if (evidenceAt != observedAt || timing.Observations == 0) timing.Observations++;
        else timing.Coalesced++;
        observedAt = evidenceAt; RequireFresh(now);
        lastWorkCompletedAt = now; dirty = false;
    }
}
