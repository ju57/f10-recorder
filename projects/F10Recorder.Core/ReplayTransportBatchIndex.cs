using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text.Json.Serialization;

namespace F10Recorder;

internal sealed record ReplayTransportSource(Stream Stream, long Bytes);
internal sealed class ReplayTransportBatchTiming
{
    [JsonInclude] public int Workers, PeakActive, FinishedSources, UnsupportedSources, OwnerChecks;
    [JsonInclude] public bool Joined;
    [JsonInclude] public bool CancellableReads, DeadlineExceeded;
    [JsonInclude] public double ReadDeadlineMilliseconds;
    [JsonInclude] public long WorkerAllocatedBytes, ProcessAllocatedBytes;
    [JsonInclude] public double Milliseconds;
    [JsonInclude] public string DrainError;
}

// Bounded source indexing: the earlier synchronous pilot and the selected
// cancellable construction share source semantics and distinct drain policies.
// Every stream is exclusive to one ordinal and stays caller-owned. No worker
// invokes the external guard. All workers are drained before returning/throwing;
// synchronous blocked I/O still requires the admitted outer host deadline.
internal static class ReplayTransportBatchIndex
{
    internal static ReplayTransportIndex[] Read(IReadOnlyList<ReplayTransportSource> inputs, int fps, bool audio,
        int workers, Action check, ReplayTransportBatchTiming timing, bool cancellableReads = false, TimeSpan? readDeadline = null, ReplayWorkerLifetime lifetime = null)
    {
        ArgumentNullException.ThrowIfNull(inputs); ArgumentNullException.ThrowIfNull(timing);
        double deadlineMs = (readDeadline ?? TimeSpan.FromSeconds(30)).TotalMilliseconds;
        if (!double.IsFinite(deadlineMs) || deadlineMs is <= 0 or > 120000) throw new ArgumentOutOfRangeException(nameof(readDeadline));
        if (workers is < 1 or > 4 || inputs.Count is < 1 or > 4096)
            throw new ArgumentException("Bounded replay index requires1–4 workers and1–4096 independent inputs.");
        var sources = inputs.ToArray();
        if (sources.Any(s => s?.Stream == null || !s.Stream.CanRead || !s.Stream.CanSeek || s.Stream.Position != 0)
            || sources.Select(s => s.Stream).Distinct(ReferenceEqualityComparer.Instance).Count() != sources.Length)
            throw new IOException("Replay batch requires independent readable streams positioned at zero.");
        lifetime ??= new();
        long started = Stopwatch.GetTimestamp(), allocated = GC.GetTotalAllocatedBytes(precise: true);
        timing.CancellableReads = cancellableReads; timing.ReadDeadlineMilliseconds = deadlineMs;
        var results = new ReplayTransportIndex[sources.Length]; var failures = new ExceptionDispatchInfo[sources.Length];
        var tasks = new List<Task>(Math.Min(workers, sources.Length));
        using var cancellation = new CancellationTokenSource(); var token = cancellation.Token;
        int next = -1, active = 0; ExceptionDispatchInfo ownerFailure = null;
        void Check()
        {
            // Keep resource callbacks running even while a timed-out batch
            // drains an uncooperative read. A deadline never permits abandonment.
            timing.OwnerChecks++; check?.Invoke();
            if (Stopwatch.GetElapsedTime(started).TotalMilliseconds > deadlineMs)
            {
                timing.DeadlineExceeded = true;
                throw new TimeoutException("Replay source-index read deadline exceeded.");
            }
        }
        void RecordDrain(Exception error)
        {
            ownerFailure ??= ExceptionDispatchInfo.Capture(error);
            timing.DrainError ??= error.GetType().Name + ": " + error.Message[..Math.Min(900, error.Message.Length)];
        }
        void Work()
        {
            long workerAllocated = GC.GetAllocatedBytesForCurrentThread();
            try
            {
                while (!token.IsCancellationRequested)
                {
                    int n = Interlocked.Increment(ref next); if (n >= sources.Length) return;
                    int current = Interlocked.Increment(ref active), peak;
                    do { peak = Volatile.Read(ref timing.PeakActive); if (peak >= current) break; }
                    while (Interlocked.CompareExchange(ref timing.PeakActive, current, peak) != peak);
                    try
                    {
                        if (cancellableReads)
                        {
                            using var access = new CancellableReadStream(sources[n].Stream, token);
                            results[n] = ReplayTransportReader.Read(access, sources[n].Bytes, fps, audio, token.ThrowIfCancellationRequested);
                        }
                        else results[n] = ReplayTransportReader.Read(sources[n].Stream, sources[n].Bytes, fps, audio, token.ThrowIfCancellationRequested);
                    }
                    catch (OperationCanceledException error) when (token.IsCancellationRequested && error.CancellationToken == token) { }
                    catch (NotSupportedException error)
                    {
                        // Finish eligibility for the other sources. A recognized
                        // corruption must defeat any valid-unsupported outcome.
                        failures[n] = ExceptionDispatchInfo.Capture(error); Interlocked.Increment(ref timing.UnsupportedSources);
                    }
                    catch (Exception error) { failures[n] = ExceptionDispatchInfo.Capture(error); cancellation.Cancel(); }
                    finally { Interlocked.Decrement(ref active); Interlocked.Increment(ref timing.FinishedSources); }
                }
            }
            finally { Interlocked.Add(ref timing.WorkerAllocatedBytes, GC.GetAllocatedBytesForCurrentThread() - workerAllocated); }
        }
        lifetime.Begin();
        try
        {
            Check();
            for (int n = 0; n < Math.Min(workers, sources.Length); n++)
            {
                tasks.Add(Task.Factory.StartNew(Work, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default));
                timing.Workers = tasks.Count;
            }
            var pending = tasks.ToArray();
            do { Check(); } while (!Task.WaitAll(pending, 50));
            Check();
        }
        catch (Exception error) { ownerFailure = ExceptionDispatchInfo.Capture(error); cancellation.Cancel(); }
        finally
        {
            cancellation.Cancel(); var pending = tasks.ToArray();
            // A guard failure cannot let a reader escape its owning request.
            // Keep calling on the owner during drain; never close active streams.
            while (pending.Any(t => !t.IsCompleted))
            {
                try { Check(); }
                catch (Exception error) { RecordDrain(error); }
                try { Task.WaitAll(pending, 50); }
                catch (Exception error) { RecordDrain(error); }
            }
            try { Check(); }
            catch (Exception error) { RecordDrain(error); }
            lifetime.Complete(pending.All(t => t.IsCompleted) && Volatile.Read(ref active) == 0);
            timing.Joined = lifetime.Joined;
            timing.ProcessAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocated;
            timing.Milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        ownerFailure?.Throw();
        // Inspect every observed error after joining; input order cannot let a
        // NotSupported result mask an actual source/reader failure.
        (failures.FirstOrDefault(f => f != null && f.SourceException is not NotSupportedException)
            ?? failures.FirstOrDefault(f => f != null))?.Throw();
        if (!lifetime.Joined || results.Any(r => r == null)) throw new IOException("Replay batch indexing did not complete every source.");
        return results;
    }

    // ReadAsync carries this batch's cancellation to actual async FileStreams.
    // GetResult on an incomplete ValueTask alone does NOT provide a blocking
    // wait, so convert that case to a Task. This adapter never owns the stream.
    sealed class CancellableReadStream(Stream source, CancellationToken cancellation) : Stream
    {
        public override bool CanRead => source.CanRead;
        public override bool CanSeek => source.CanSeek;
        public override bool CanWrite => false;
        public override long Length => source.Length;
        public override long Position { get => source.Position; set => source.Position = value; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var pending = source.ReadAsync(buffer.AsMemory(offset, count), cancellation);
            return pending.IsCompleted ? pending.GetAwaiter().GetResult() : pending.AsTask().GetAwaiter().GetResult();
        }
        public override long Seek(long offset, SeekOrigin origin) => source.Seek(offset, origin);
        public override void Flush() => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
