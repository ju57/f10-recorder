namespace F10Recorder;

// Exporter-owned closure facts. Diagnostic timestamps are independent of these
// decisions, so changing or disabling instrumentation cannot release ownership.
internal sealed class ReplayConstructionClosure
{
    internal bool PacketStarted { get; private set; }
    internal bool AssemblyStarted { get; private set; }
    internal bool AssemblyClosed { get; private set; }
    internal bool SourcesClosed { get; private set; } = true;
    bool sourceCloseFailed;
    internal void BeginPacketMux() => PacketStarted = true;
    internal void BeginAssembly() => AssemblyStarted = true;
    internal void CompleteAssembly() => AssemblyClosed = true;
    internal void SourceOpened() => SourcesClosed = false;
    internal void BeginSourceClose() => SourcesClosed = false;
    internal void CompleteSourceClose(bool closed)
    {
        sourceCloseFailed |= !closed;
        SourcesClosed = closed && !sourceCloseFailed;
    }
}

// Request activity and actual child/managed closure are independent facts.
// A stopped head encoder does not certify the remaining parent-owned work.
internal sealed class ReplayExportLifetime
{
    int active, childAbsent = 1, managedUncertain;
    internal bool ChildrenAbsent => Volatile.Read(ref childAbsent) == 1;
    internal bool Stopped => Volatile.Read(ref active) == 0 && Volatile.Read(ref childAbsent) == 1
        && Volatile.Read(ref managedUncertain) == 0;
    internal IDisposable Begin()
    {
        if (Interlocked.CompareExchange(ref active, 1, 0) != 0) throw new IOException("Replay export already owns this source.");
        if (Volatile.Read(ref childAbsent) == 0 || Volatile.Read(ref managedUncertain) != 0)
        {
            Volatile.Write(ref active, 0);
            throw new IOException("Previous replay producer closure is unconfirmed; retained ownership cannot be reset.");
        }
        return new Scope(this);
    }
    internal void ChildStopped(bool absent) => Volatile.Write(ref childAbsent, absent ? 1 : 0);
    internal void ObserveManaged(bool started, bool closed, bool readersJoined)
    {
        // No subsequent request/receipt can clear an uncertain writer or reader.
        if (started && !closed || !readersJoined) Volatile.Write(ref managedUncertain, 1);
    }
    internal void ObserveRequest(ReplayExportRequest request)
    {
        var closure = request.Closure;
        bool packetStarted = closure.PacketStarted;
        bool assemblyUncertain = closure.AssemblyStarted && !closure.AssemblyClosed;
        ObserveManaged(packetStarted || assemblyUncertain,
            !assemblyUncertain && (!packetStarted || request.ManagedMux?.Closed == true),
            closure.SourcesClosed && (request.SourceProtection?.DependenciesClosed??true) && request.IndexReaders.Joined && request.FallbackIndexReaders.Joined && request.PacketReaders.Joined && request.PacketRead is not {Joined:false});
    }
    sealed class Scope(ReplayExportLifetime owner) : IDisposable
    {
        ReplayExportLifetime current = owner;
        public void Dispose()
        {
            var owned = Interlocked.Exchange(ref current, null);
            if (owned != null) Volatile.Write(ref owned.active, 0);
        }
    }
}
