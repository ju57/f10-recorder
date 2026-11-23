namespace F10Recorder;

internal interface IReplaySaveObservationScope : IDisposable
{
    bool RequiresConservative { get; }
}
// Observation half of the V5 request owner. The host serializer is the actual
// Cache gate; no data producer may hold owner metadata locks when entering it.
// One gate serves BOTH eligible snapshots and the full conservative path.
internal sealed class ReplaySaveObservation<T> where T : class, IReplaySaveObservationScope
{
    readonly Action<Action> serialize;
    readonly Func<bool> eligible;
    readonly Func<T> observeLocal;
    readonly Action conservativeLocal;
    readonly Func<T, bool> observeGlobal;
    readonly Func<bool> conservativeGlobal;
    readonly ReplayStorageObservationGate freshness;
    readonly List<T> uncertainScopes = [];
    T current;
    bool active;
    volatile bool poisoned;
    bool hasCompletedEvidence;

    internal ReplaySaveObservation(Action<Action> serialize, Func<bool> eligible, Func<T> observeLocal,
        Action conservativeLocal, Func<T, bool> observeGlobal, Func<bool> conservativeGlobal,
        ReplayStorageCheckTiming timing = null, Func<long> timestamp = null, long frequency = 0,
        long? admittedAt = null, Func<long> completedEvidenceAt = null)
    {
        this.serialize = serialize ?? throw new ArgumentNullException(nameof(serialize));
        this.eligible = eligible ?? throw new ArgumentNullException(nameof(eligible));
        this.observeLocal = observeLocal ?? throw new ArgumentNullException(nameof(observeLocal));
        this.conservativeLocal = conservativeLocal ?? throw new ArgumentNullException(nameof(conservativeLocal));
        this.observeGlobal = observeGlobal ?? throw new ArgumentNullException(nameof(observeGlobal));
        this.conservativeGlobal = conservativeGlobal ?? throw new ArgumentNullException(nameof(conservativeGlobal));
        // An actual host may supply the completion tick of its genuine prior
        // admission observation, in this exact clock/frequency. Construction
        // time is never relabelled as a native observation. Without an admitted
        // epoch, the first global observation must complete (busy cannot defer).
        var clock = timestamp ?? System.Diagnostics.Stopwatch.GetTimestamp;
        bool initializing = true;
        long ReadClock()
        {
            if (initializing) { initializing = false; return admittedAt ?? clock(); }
            return clock();
        }
        hasCompletedEvidence = admittedAt.HasValue;
        freshness = new(CompleteGlobal, 100, ReplayCapacityPolicy.CoverageMilliseconds, timing, ReadClock, frequency,
            completedEvidenceAt: completedEvidenceAt);
    }
    bool CompleteGlobal()
    {
        try
        {
            bool complete;
            if (current == null) complete = conservativeGlobal();
            else if (current.RequiresConservative)
            {
                conservativeLocal();
                RequireNotPoisoned();
                complete = conservativeGlobal();
            }
            else complete = observeGlobal(current);
            RequireNotPoisoned(); return complete;
        }
        finally { DisposeScope(); RequireNotPoisoned(); }
    }
    void DisposeScope()
    {
        var scope = current; current = null;
        try { scope?.Dispose(); }
        catch { if (scope != null) uncertainScopes.Add(scope); poisoned = true; throw; }
    }
    void Require()
    {
        if (poisoned || active) throw new IOException("Accepted-save observation is uncertain or reentrant.");
    }
    void RequireNotPoisoned()
    {
        if (poisoned) throw new IOException("An inner observation failure cannot be swallowed or repaired.");
    }
    // Ownership may close after claim release. Assert the same completed
    // evidence epoch without running callbacks or renewing its timestamp.
    internal void RequireFreshness()=>Serialized(()=>
    {
        if(!hasCompletedEvidence)throw new IOException("No completed accepted-save evidence.");
        freshness.RequireFreshness();
    });
    void Serialized(Action body)
    {
        int thread = Environment.CurrentManagedThreadId, calls = 0;
        bool completed = false;
        try
        {
            serialize(() =>
            {
                bool entered = false;
                try
                {
                    if (Environment.CurrentManagedThreadId != thread || Interlocked.Increment(ref calls) != 1)
                        throw new IOException("Observation serializer must invoke synchronously, once, on this thread.");
                    Require(); active = true; entered = true;
                    body(); RequireNotPoisoned(); completed = true;
                }
                catch { poisoned = true; throw; }
                finally { if (entered) active = false; }
            });
            RequireNotPoisoned();
            if (calls != 1 || !completed) throw new IOException("Observation serializer returned without completing its operation.");
        }
        catch { poisoned = true; throw; }
    }
    // Unsupported is solely a pre-query context result. In particular, failed
    // native observation, expiry, poison and serializer errors never return it.
    internal bool TryCheck(bool force = false)
    {
        bool supported = false;
        Serialized(() =>
        {
            bool supportedContext = eligible(); RequireNotPoisoned();
            if (!supportedContext) return;
            try
            {
                freshness.RequireFreshness();
                current = observeLocal() ?? throw new IOException("Eligible observation returned no scoped output evidence.");
                RequireNotPoisoned();
                if (force || current.RequiresConservative || !hasCompletedEvidence) freshness.CheckNow(); else freshness.Check();
                hasCompletedEvidence = true;
                freshness.RequireFreshness(); supported = true;
            }
            finally
            {
                // Scope disposal is checking work too. It cannot be hidden
                // after a successful freshness result or before Saved.
                try { DisposeScope(); }
                finally { current = null; }
                freshness.RequireFreshness();
            }
        });
        return supported;
    }
    internal void Check(bool force = false)
    {
        if (!TryCheck(force)) CheckConservative(force);
    }
    internal void CheckConservative(bool force = false)
    {
        Serialized(() =>
        {
            try
            {
                freshness.RequireFreshness();
                conservativeLocal(); // Every callback; no scoped snapshot sharing.
                RequireNotPoisoned();
                if (force || !hasCompletedEvidence) freshness.CheckNow(); else freshness.Check();
                hasCompletedEvidence = true;
                freshness.RequireFreshness();
            }
            finally { RequireNotPoisoned(); }
        });
    }
    internal void MarkChanged() => Serialized(freshness.MarkChanged);
}
