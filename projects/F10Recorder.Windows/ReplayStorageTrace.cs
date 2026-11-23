using System.Diagnostics;

namespace F10Recorder;

// Optional finite-check instrumentation. The ordinary recorder never installs
// the observer; this does not persist telemetry or change storage decisions.
internal sealed record ReplayStorageTracePhase(string Name, long StartedQpc, long CompletedQpc);
internal sealed class ReplayStorageTrace
{
    public long Ordinal, ParentOrdinal, StartedQpc, CompletedQpc;
    public string Scope, Error, ObserverError;
    public List<ReplayStorageTracePhase> Phases = [];
}

internal sealed partial class ReplayCache
{
    internal Action<ReplayStorageTrace> StorageObservedForChecks;
    StorageTraceScope storageTrace;
    long storageTraceOrdinal;

    // Call only inside the cache gate. Nesting correlates a full observation
    // and its estimate with the actual callback, rather than adding maxima.
    StorageTraceScope TraceStorage(string scope, long? startedQpc = null)
        => StorageObservedForChecks == null ? null : new(this, scope, StorageObservedForChecks, startedQpc);

    sealed class StorageTraceScope : IDisposable
    {
        readonly ReplayCache owner;
        readonly StorageTraceScope parent;
        readonly Action<ReplayStorageTrace> observer;
        readonly ReplayStorageTrace trace;
        string phase;
        long phaseAt;
        internal StorageTraceScope(ReplayCache owner, string scope, Action<ReplayStorageTrace> observer, long? startedQpc)
        {
            this.owner = owner; this.observer = observer; parent = owner.storageTrace;
            trace = new() { Ordinal = ++owner.storageTraceOrdinal, ParentOrdinal = parent?.trace.Ordinal ?? 0,
                Scope = scope, StartedQpc = startedQpc ?? Stopwatch.GetTimestamp() };
            owner.storageTrace = this;
        }
        internal void AddPhase(string name, long started, long completed)
            => trace.Phases.Add(new(name, started, completed));
        internal void Phase(string next)
        {
            long now = Stopwatch.GetTimestamp();
            if (phase != null) AddPhase(phase, phaseAt, now);
            phase = next; phaseAt = now;
        }
        internal void Failed(Exception error) => trace.Error = error.ToString();
        public void Dispose()
        {
            Phase(null); trace.CompletedQpc = Stopwatch.GetTimestamp(); owner.storageTrace = parent;
            try { observer(trace); }
            catch (Exception error) when (trace.Error != null)
            { trace.ObserverError = error.ToString(); } // Never replace the actual storage failure.
        }
    }
}
