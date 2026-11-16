using System.Diagnostics;

namespace F10Recorder;

internal sealed record ReplayHistoryStatus(int Entries, int Pending, bool Parsing, long OwnedBytes,
    long MetadataBytes, long PreparedSources, long PreparedReadBytes, long PreparedAllocatedBytes,
    double PreparationMilliseconds, long BorrowedSources);

// One capture session. Disk payloads remain in their original completed files;
// history refs and request refs share immutable native owners, never file data.
internal sealed class ReplayCompletedHistory : IDisposable
{
    const int MaximumEntries = 2048, MaximumPending = 2;
    const long MaximumMetadata = 64L * 1024 * 1024, MaximumPreparedInput = 8L * 1024 * 1024;
    sealed class Entry(Segment segment, ReplayGuardDirectory.FileTicket ticket, ReplayFileExtent extent)
    {
        internal readonly Segment Segment = segment;
        internal readonly ReplayGuardDirectory.FileTicket Ticket = ticket;
        internal readonly ReplayFileExtent Extent = extent;
        internal ReplayTransportIndex Index;
        internal long Allowance;
        internal int References = 1; // rolling history (or the active builder)
    }
    readonly object gate = new();
    readonly ReplaySessionInfo info;
    readonly ReplayGuardDirectory directory;
    readonly long maximumBytes;
    readonly int maximumEntries;
    readonly long maximumMetadata;
    internal Action<CancellationToken> BeforeParseForChecks;
    readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
    readonly Queue<Segment> queue = new();
    readonly HashSet<string> scheduled = new(StringComparer.OrdinalIgnoreCase);
    readonly CancellationTokenSource cancellation = new();
    readonly Task worker;
    string oldest = "";
    string preparingPath;
    Entry active;
    int liveEntries;
    long bytes, metadata, prepared, readBytes, allocated, borrowed;
    double milliseconds;
    bool closing, directoryClosed;
    static readonly List<ReplayCompletedHistory> retained = [];

    internal ReplayCompletedHistory(ReplaySessionInfo info,int maximumEntries=MaximumEntries,long maximumMetadata=MaximumMetadata)
    {
        if(maximumEntries is <1 or >MaximumEntries || maximumMetadata is <0 or >MaximumMetadata)throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        this.maximumEntries=maximumEntries;this.maximumMetadata=maximumMetadata;
        this.info = info;
        maximumBytes = checked((long)((info.ReplaySeconds + 30) * (info.BitrateMbps * 1000000L + 192000) / 8d * 1.5));
        var capacity = ReplayStorage.ObserveCapacity(info.Directory);
        directory = new(info.Directory, Native.DirectoryIdentity(info.Directory), capacity.Volume);
        worker = Task.Factory.StartNew(Work, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    internal ReplayHistoryStatus Status
    { get { lock(gate)return new(liveEntries,queue.Count,active!=null,bytes,metadata,prepared,readBytes,allocated,milliseconds,borrowed); } }

    internal void Offer(IEnumerable<Segment> completed)
    {
        lock(gate)
        {
            if(closing)return;
            foreach(var segment in completed)
            {
                if(queue.Count==MaximumPending)break;
                if(StringComparer.OrdinalIgnoreCase.Compare(segment.Path,oldest)<0 || scheduled.Contains(segment.Path))continue;
                if(scheduled.Count>=maximumEntries+MaximumPending)break;
                scheduled.Add(segment.Path);
                queue.Enqueue(new(){Path=segment.Path,Start=segment.Start,End=segment.End});
            }
            Monitor.PulseAll(gate);
        }
    }

    void Work()
    {
        while(true)
        {
            Segment segment;
            lock(gate)
            {
                while(!closing && queue.Count==0)Monitor.Wait(gate);
                if(closing)return;
                segment=queue.Dequeue();
                preparingPath=segment.Path;
            }
            Entry entry=null;
            long started=Stopwatch.GetTimestamp(), before=GC.GetAllocatedBytesForCurrentThread();
            try
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var extent=ReplayStorage.Observe(segment.Path);
                if(extent.Length<=0 || extent.Length>MaximumPreparedInput)continue;
                lock(gate)
                {
                    if(closing || liveEntries>=maximumEntries || extent.Length>maximumBytes-bytes
                        || StringComparer.OrdinalIgnoreCase.Compare(segment.Path,oldest)<0)continue;
                }
                var ticket=directory.OpenAsyncRead(Path.GetFileName(segment.Path),extent);
                entry=new(segment,ticket,extent);
                lock(gate){active=entry;liveEntries++;bytes=checked(bytes+extent.Length);}
                BeforeParseForChecks?.Invoke(cancellation.Token);
                using(var source=ticket.OpenHistoryRead(cancellation.Token))
                {
                    void Check()
                    {
                        cancellation.Token.ThrowIfCancellationRequested();
                        if(Stopwatch.GetElapsedTime(started).TotalSeconds>30)throw new TimeoutException("Completed replay preparation deadline exceeded.");
                    }
                    var index=ReplayTransportReader.Read(source,extent.Length,info.FramesPerSecond,info.RecordAudio,Check);
                    long allowance=ReplayPreparedIndexAcquisition.Allowance(index);
                    lock(gate)
                    {
                        readBytes=checked(readBytes+extent.Length);prepared++;
                        if(allowance<=maximumMetadata-metadata){entry.Index=index;entry.Allowance=allowance;metadata+=allowance;}
                    }
                }
            }
            catch(Exception error) when(error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception
                or NotSupportedException or OperationCanceledException or TimeoutException or OverflowException)
            {
                // No failed parse is a healthy metadata hit. A request performs
                // its ordinary fresh parse; source corruption is not certified
                // or replaced by an unsupported result from this background job.
            }
            finally
            {
                lock(gate)
                {
                    allocated=checked(allocated+GC.GetAllocatedBytesForCurrentThread()-before);
                    milliseconds+=Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    active=null;preparingPath=null;
                    if(entry!=null)
                    {
                        if(!closing && StringComparer.OrdinalIgnoreCase.Compare(segment.Path,oldest)>=0)entries.Add(segment.Path,entry);
                        else Release(entry);
                    }
                    Monitor.PulseAll(gate);
                }
            }
        }
    }

    // Caller already pinned selection against pruning under ReplayCache.gate.
    // Take refs before doing native observations/copies outside that cache gate.
    internal Snapshot Acquire(IReadOnlyList<Segment> selection,Action check=null)
        => Snapshot.Acquire(this,selection,check);

    internal void Trim(string keepFrom)
    {
        lock(gate)
        {
            oldest=keepFrom;
            foreach(string path in entries.Keys.Where(p=>StringComparer.OrdinalIgnoreCase.Compare(p,oldest)<0).ToArray())
            {var entry=entries[path];entries.Remove(path);Release(entry);}
            scheduled.RemoveWhere(p=>StringComparer.OrdinalIgnoreCase.Compare(p,oldest)<0 && p!=preparingPath);
        }
    }
    internal string[] ActivePaths
    {get{lock(gate)return preparingPath==null?[]:[preparingPath];}}

    void Release(Entry entry)
    {
        if(entry.References>1){entry.References--;return;}
        if(entry.References!=1)throw new IOException("Completed replay reference was already released.");
        try{entry.Ticket.Dispose();}
        catch{lock(retained)if(!retained.Contains(this))retained.Add(this);throw;}
        entry.References=0;liveEntries--;bytes-=entry.Extent.Length;metadata-=entry.Allowance;
        if(closing && liveEntries==0 && !directoryClosed){directory.Dispose();directoryClosed=true;}
    }

    internal sealed class Snapshot : IDisposable
    {
        readonly ReplayCompletedHistory owner;
        readonly Dictionary<string,Entry> owned;
        readonly Dictionary<string,ReplayTransportIndex> indices=new(StringComparer.OrdinalIgnoreCase);
        bool disposed;
        Snapshot(ReplayCompletedHistory owner,Dictionary<string,Entry> owned){this.owner=owner;this.owned=owned;}
        internal static Snapshot Acquire(ReplayCompletedHistory owner,IReadOnlyList<Segment> selection,Action check)
        {
            var owned=new Dictionary<string,Entry>(StringComparer.OrdinalIgnoreCase);
            lock(owner.gate)
            {
                if(owner.closing)return null;
                foreach(var segment in selection)
                    if(owner.entries.TryGetValue(segment.Path,out var entry) && entry.Segment.Start==segment.Start && entry.Segment.End==segment.End)
                    {entry.References++;owned.Add(segment.Path,entry);owner.borrowed++;}
            }
            if(owned.Count==0)return null;
            var snapshot=new Snapshot(owner,owned);
            try{snapshot.Prepare(check);return snapshot;}
            catch{snapshot.Dispose();throw;}
        }
        internal int Count=>owned.Count;
        void Prepare(Action check)
        {
            var guard=new ReplayPlanningGuard(check);guard.CheckNow();
            foreach(var pair in owned)
            {
                guard.Check();
                _=Extent(pair.Key);
                if(pair.Value.Index!=null)indices.Add(pair.Key,ReplayPreparedIndexAcquisition.Copy(pair.Value.Index));
            }
            guard.CheckNow();
        }
        internal ReplayTransportIndex Index(string path)
        {Require();return indices.GetValueOrDefault(path);}
        internal bool Contains(string path){Require();return owned.ContainsKey(path);}
        void Require(){if(disposed)throw new ObjectDisposedException(nameof(Snapshot));}
        internal ReplayFileExtent Extent(string path)
        {
            Require();var entry=owned[path];var actual=entry.Ticket.Observe().Extent;
            if(actual.Volume!=entry.Extent.Volume || actual.Identity!=entry.Extent.Identity || actual.Length!=entry.Extent.Length
                || actual.AllocationUnit!=entry.Extent.AllocationUnit)throw new IOException("Completed replay owner changed.");
            return actual;
        }
        internal ReplayFileExtent Admit(string path,ReplayGuardDirectory target)
        {
            Require();
            if(target.OriginalPath!=owner.directory.OriginalPath || target.Identity!=owner.directory.Identity)
                throw new IOException("Prepared replay input is outside its owned session directory.");
            return Extent(path);
        }
        public void Dispose()
        {
            lock(owner.gate)
            {
                if(disposed)return;
                List<Exception> errors=[];
                foreach(var pair in owned.ToArray())
                    try{owner.Release(pair.Value);owned.Remove(pair.Key);}catch(Exception error){errors.Add(error);}
                if(errors.Count!=0)throw new AggregateException("Completed replay snapshot closure is uncertain.",errors);
                disposed=true;
            }
        }
    }

    public void Dispose()
    {
        lock(gate){if(!closing){closing=true;queue.Clear();cancellation.Cancel();Monitor.PulseAll(gate);}}
        // A timeout retains the task/tickets and directory; it never certifies
        // closure or allows the caller to delete their source namespace.
        if(!worker.Wait(2000)){lock(retained)if(!retained.Contains(this))retained.Add(this);throw new IOException("Completed replay history reader closure is uncertain.");}
        worker.GetAwaiter().GetResult();
        lock(gate)
        {
            foreach(var pair in entries.ToArray()){Release(pair.Value);entries.Remove(pair.Key);}
            scheduled.Clear();
            if(liveEntries==0 && !directoryClosed){directory.Dispose();directoryClosed=true;}
        }
    }
}
