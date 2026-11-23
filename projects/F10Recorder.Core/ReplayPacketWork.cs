using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text.Json.Serialization;
namespace F10Recorder;

internal sealed class ReplayPacketWorkTiming
{
    [JsonInclude] public int Workers,PeakActive,Prepared,Consumed,CallbackRequests,OwnerCallbacks,OwnerThread;
    [JsonInclude] public long PeakOwnedBytes,ReservedResultBytes,WorkerAllocatedBytes;
    [JsonInclude] public double Milliseconds,GuardWaitMilliseconds,MaximumGuardRoundTripMilliseconds;
    [JsonInclude] public bool Joined,Cancelled,DeadlineExceeded;
    [JsonInclude] public string FirstError,OwnerError,CancellationError,CleanupError;
    [JsonInclude] public string[] WorkerErrors;
}

// One synchronous owner pumps guards and consumes results. Worker data operations
// run outside this gate; each ordinal has progress space, including a slow first.
// Cancellation never permits a worker, buffer or borrowed stream to escape.
internal static class ReplayPacketWork
{
    internal sealed class Context
    {
        internal readonly CancellationToken Cancellation;
        internal readonly int Worker;
        readonly Action guard;readonly Action<long> report;readonly int workerThread=Environment.CurrentManagedThreadId;
        volatile bool active=true;
        internal Context(CancellationToken token,int worker,Action guard,Action<long> report){Cancellation=token;Worker=worker;this.guard=guard;this.report=report;}
        void Require(){if(!active)throw new IOException("Expired packet-work borrow.");if(Environment.CurrentManagedThreadId!=workerThread)throw new IOException("Packet borrow escaped its worker thread.");}
        internal void Check(){Require();if(!active)throw new IOException("Expired packet-work borrow.");Cancellation.ThrowIfCancellationRequested();guard();Cancellation.ThrowIfCancellationRequested();}
        internal void ReportOwnedCapacity(long bytes){Require();if(!active)throw new IOException("Expired packet buffer borrow.");report(bytes);}
        internal void End()=>active=false;
    }
    sealed class Slot<T> where T:class,IDisposable
    {
        internal int Ordinal=-1,State; // empty/preparing/ready/consuming
        internal T Result;
        internal long Bytes,GuardStarted;
        internal bool GuardPending;
        internal ExceptionDispatchInfo GuardFailure;
    }
    internal static void Run<TInput,TResult>(IReadOnlyList<TInput> inputs,int workers,long maximumResultBytes,
        Func<TInput,Context,TResult> prepare,Func<TResult,long> capacity,Action<TResult,Action> consume,
        Action guard,ReplayPacketWorkTiming timing,TimeSpan? deadline=null,ReplayWorkerLifetime lifetime=null)
        where TResult:class,IDisposable
    {
        ArgumentNullException.ThrowIfNull(inputs);ArgumentNullException.ThrowIfNull(prepare);ArgumentNullException.ThrowIfNull(capacity);
        ArgumentNullException.ThrowIfNull(consume);ArgumentNullException.ThrowIfNull(guard);ArgumentNullException.ThrowIfNull(timing);
        double deadlineMs=(deadline??TimeSpan.FromSeconds(30)).TotalMilliseconds;
        if(inputs.Count is <1 or >65536||workers is <1 or >4||maximumResultBytes is <1 or >16777216
            ||!double.IsFinite(deadlineMs)||deadlineMs<=0||deadlineMs>120000)throw new ArgumentException("Finite packet-work bounds required.");
        lifetime ??= new();
        var jobs=inputs.ToArray();int count=Math.Min(workers,jobs.Length),next=0,consumed=0,active=0;
        var gate=new object();var slots=Enumerable.Range(0,count).Select(_=>new Slot<TResult>()).ToArray();
        var tasks=new List<Task>(count);var failures=new ExceptionDispatchInfo[jobs.Length];
        using var cancellation=new CancellationTokenSource();var token=cancellation.Token;
        long started=Stopwatch.GetTimestamp(),lastOwnerCheck=0,ownedBytes=0,waitTicks=0,maximumRoundTripTicks=0;
        ExceptionDispatchInfo ownerFailure=null,firstFailure=null,cleanupFailure=null,cancellationFailure=null;
        int ownerThread=Environment.CurrentManagedThreadId;
        timing.OwnerThread=ownerThread;timing.ReservedResultBytes=checked(count*maximumResultBytes);
        void SelectFailure(ExceptionDispatchInfo failure){if(firstFailure==null||firstFailure.SourceException is NotSupportedException&&failure.SourceException is not NotSupportedException)firstFailure=failure;}
        void SignalCancellation()
        {
            try{cancellation.Cancel();}
            catch(Exception error){lock(gate){cancellationFailure??=ExceptionDispatchInfo.Capture(error);SelectFailure(cancellationFailure);Monitor.PulseAll(gate);}}
        }
        void Fail(Exception error,bool owner=false)
        {
            lock(gate){var failure=ExceptionDispatchInfo.Capture(error);SelectFailure(failure);if(owner)ownerFailure??=failure;Monitor.PulseAll(gate);}
            SignalCancellation(); // Registered callbacks may throw; never bypass final joins.
        }
        void RecordCleanup(Exception error){lock(gate)cleanupFailure??=ExceptionDispatchInfo.Capture(error);Fail(error);}
        void OwnerOnly(){if(Environment.CurrentManagedThreadId!=ownerThread)throw new IOException("Packet guard migrated from its owner thread.");}
        void OwnerGuard()
        {
            OwnerOnly();timing.OwnerCallbacks++;guard();lastOwnerCheck=Stopwatch.GetTimestamp();
            if(Stopwatch.GetElapsedTime(started).TotalMilliseconds>deadlineMs){timing.DeadlineExceeded=true;throw new TimeoutException("Packet-work phase read deadline exceeded.");}
        }
        void PumpGuards(bool propagate)
        {
            OwnerOnly();
            foreach(var slot in slots)
            {
                long requested;ExceptionDispatchInfo rejected;
                lock(gate){if(!slot.GuardPending)continue;requested=slot.GuardStarted;rejected=firstFailure;}
                ExceptionDispatchInfo failure=rejected;
                if(failure==null)
                {
                    try{OwnerGuard();if(Stopwatch.GetElapsedTime(requested).TotalMilliseconds>500)throw new IOException("Packet guard exceeded500ms including dispatch wait and checking.");}
                    catch(Exception error){failure=ExceptionDispatchInfo.Capture(error);Fail(error,true);}
                }
                long duration=Stopwatch.GetTimestamp()-requested;
                lock(gate){failure??=firstFailure;slot.GuardFailure=failure;slot.GuardPending=false;waitTicks+=duration;maximumRoundTripTicks=Math.Max(maximumRoundTripTicks,duration);Monitor.PulseAll(gate);}
            }
            if(propagate){ExceptionDispatchInfo failure;lock(gate)failure=firstFailure;failure?.Throw();}
        }
        void ServiceGuards()=>PumpGuards(true);
        void PulseOwner()
        {
            ServiceGuards();
            if(Stopwatch.GetElapsedTime(started).TotalMilliseconds>deadlineMs){timing.DeadlineExceeded=true;throw new TimeoutException("Packet-work phase read deadline exceeded.");}
            if(lastOwnerCheck==0||Stopwatch.GetElapsedTime(lastOwnerCheck).TotalMilliseconds>=50)OwnerGuard();
        }
        void RequestGuard(Slot<TResult> slot,int ordinal)
        {
            token.ThrowIfCancellationRequested();ExceptionDispatchInfo failure;Exception expired=null;
            lock(gate)
            {
                if(slot.Ordinal!=ordinal||slot.State!=1||slot.GuardPending)throw new IOException("Packet guard outside active exclusive work.");
                slot.GuardFailure=null;slot.GuardStarted=Stopwatch.GetTimestamp();slot.GuardPending=true;timing.CallbackRequests++;Monitor.PulseAll(gate);
                while(slot.GuardPending&&firstFailure==null&&!token.IsCancellationRequested)
                {
                    double remaining=500-Stopwatch.GetElapsedTime(slot.GuardStarted).TotalMilliseconds;
                    if(remaining<=0){expired=new IOException("Packet guard exceeded500ms waiting for its owner.");break;}
                    Monitor.Wait(gate,Math.Max(1,Math.Min(50,(int)Math.Ceiling(remaining))));
                }
                failure=slot.GuardFailure??firstFailure;
            }
            if(expired!=null){Fail(expired);throw expired;}
            failure?.Throw();token.ThrowIfCancellationRequested();
        }
        void Work(int worker)
        {
            long allocation=GC.GetAllocatedBytesForCurrentThread();
            try
            {
                while(!token.IsCancellationRequested)
                {
                    int ordinal;Slot<TResult> slot;
                    lock(gate)
                    {
                        while(!token.IsCancellationRequested&&firstFailure==null&&next<jobs.Length&&next>=consumed+count)Monitor.Wait(gate,50);
                        if(token.IsCancellationRequested||firstFailure!=null||next>=jobs.Length)return;
                        ordinal=next++;slot=slots[ordinal%count];if(slot.State!=0)throw new IOException("Packet ordinal progress slot reused early.");
                        slot.Ordinal=ordinal;slot.State=1;active++;timing.PeakActive=Math.Max(timing.PeakActive,active);
                    }
                    TResult result=null;var context=new Context(token,worker,()=>RequestGuard(slot,ordinal),bytes=>{lock(gate){if(slot.Ordinal!=ordinal||slot.State!=1)throw new IOException("Expired packet allocation epoch.");if(bytes<0||bytes>maximumResultBytes)throw new IOException("Building packet result exceeds byte allowance.");ownedBytes=checked(ownedBytes+bytes-slot.Bytes);slot.Bytes=bytes;timing.PeakOwnedBytes=Math.Max(timing.PeakOwnedBytes,ownedBytes);}});
                    try
                    {
                        result=prepare(jobs[ordinal],context);context.End();token.ThrowIfCancellationRequested();
                        long bytes=result==null?-1:capacity(result);if(bytes<0||bytes>maximumResultBytes)throw new IOException("Prepared packet result exceeds its byte allowance.");
                        lock(gate){if(firstFailure!=null)firstFailure.Throw();slot.Result=result;ownedBytes=checked(ownedBytes+bytes-slot.Bytes);slot.Bytes=bytes;timing.PeakOwnedBytes=Math.Max(timing.PeakOwnedBytes,ownedBytes);slot.State=2;timing.Prepared++;result=null;Monitor.PulseAll(gate);}
                    }
                    catch(OperationCanceledException error)when(token.IsCancellationRequested&&error.CancellationToken==token){}
                    catch(Exception error){failures[ordinal]=ExceptionDispatchInfo.Capture(error);Fail(error);}
                    finally
                    {
                        context.End();
                        try{result?.Dispose();}catch(Exception error){RecordCleanup(error);}
                        lock(gate){active--;Monitor.PulseAll(gate);}
                    }
                }
            }
            catch(Exception error){Fail(error);}
            finally{Interlocked.Add(ref timing.WorkerAllocatedBytes,GC.GetAllocatedBytesForCurrentThread()-allocation);}
        }
        lifetime.Begin();
        try
        {
            OwnerGuard();
            for(int n=0;n<count;n++){int worker=n;tasks.Add(Task.Factory.StartNew(()=>Work(worker),CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default));timing.Workers=tasks.Count;}
            for(int ordinal=0;ordinal<jobs.Length;ordinal++)
            {
                var slot=slots[ordinal%count];TResult result;
                while(true)
                {
                    PulseOwner();
                    lock(gate){firstFailure?.Throw();if(slot.State==2&&slot.Ordinal==ordinal){result=slot.Result;slot.State=3;break;}Monitor.Wait(gate,10);}
                }
                try{consume(result,ServiceGuards);ServiceGuards();}
                catch(Exception error){Fail(error,true);throw;}
                finally
                {
                    try{result.Dispose();}catch(Exception error){RecordCleanup(error);}
                    lock(gate){slot.Result=null;ownedBytes-=slot.Bytes;slot.Bytes=0;slot.State=0;consumed++;timing.Consumed++;Monitor.PulseAll(gate);}
                }
            }
            PulseOwner();
        }
        catch(Exception error){bool owner;lock(gate)owner=firstFailure==null||!ReferenceEquals(error,firstFailure.SourceException);Fail(error,owner);}
        finally
        {
            SignalCancellation();lock(gate)Monitor.PulseAll(gate);
            while(tasks.Any(t=>!t.IsCompleted))
            {
                try{PumpGuards(false);if(lastOwnerCheck==0||Stopwatch.GetElapsedTime(lastOwnerCheck).TotalMilliseconds>=50)OwnerGuard();}catch(Exception error){Fail(error,true);}
                lock(gate)Monitor.Wait(gate,10);
            }
            PumpGuards(false);
            foreach(var slot in slots){try{slot.Result?.Dispose();}catch(Exception error){RecordCleanup(error);}slot.Result=null;}
            lifetime.Complete(tasks.All(t=>t.IsCompleted)&&active==0);
            timing.Joined=lifetime.Joined;
            string Description(ExceptionDispatchInfo failure){string text=failure?.SourceException.ToString();return text==null?null:text[..Math.Min(4096,text.Length)];}
            timing.FirstError=Description(firstFailure);timing.OwnerError=Description(ownerFailure);timing.CancellationError=Description(cancellationFailure);timing.CleanupError=Description(cleanupFailure);timing.WorkerErrors=failures.Where(e=>e!=null).Take(8).Select(Description).ToArray();
            timing.Cancelled=firstFailure!=null;timing.Milliseconds=Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            timing.GuardWaitMilliseconds=waitTicks*1000d/Stopwatch.Frequency;timing.MaximumGuardRoundTripMilliseconds=maximumRoundTripTicks*1000d/Stopwatch.Frequency;
        }
        (firstFailure??cleanupFailure??cancellationFailure)?.Throw();
        if(!lifetime.Joined||consumed!=jobs.Length)throw new IOException("Packet work did not finish and join every ordinal.");
    }
}
