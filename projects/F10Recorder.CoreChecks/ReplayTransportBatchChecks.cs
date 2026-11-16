using F10Recorder;

internal static class ReplayTransportBatchChecks
{
    static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    static Exception Failure(Action action)
    {
        try { action(); } catch (Exception error) { return error; }
        throw new Exception("Expected batch failure.");
    }
    sealed class ObservedStream(byte[] bytes, Action firstRead = null, Action endRead = null) : MemoryStream(bytes)
    {
        int first; internal int Busy, ReadCalls;
        public override int Read(byte[] buffer, int offset, int count)
        {
            Interlocked.Increment(ref Busy);
            try
            {
                ReadCalls++; if (Interlocked.Exchange(ref first, 1) == 0) firstRead?.Invoke();
                int read = base.Read(buffer, offset, count); if (read == 0) endRead?.Invoke(); return read;
            }
            finally { Interlocked.Decrement(ref Busy); }
        }
    }
    sealed class PendingAsyncStream(byte[] bytes, bool ignoreCancellation) : MemoryStream(bytes)
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<int> Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Pending;
        internal CancellationToken ObservedToken;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ObservedToken = cancellationToken; Interlocked.Increment(ref Pending); Entered.TrySetResult();
            return Wait();
            async ValueTask<int> Wait()
            {
                try { return await (ignoreCancellation ? Release.Task : Release.Task.WaitAsync(cancellationToken)).ConfigureAwait(false); }
                finally { Interlocked.Decrement(ref Pending); }
            }
        }
    }
    sealed class FaultAfterPeerStream(byte[] bytes, Task peerEntered, Exception failure) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException<int>(peerEntered.Wait(TimeSpan.FromSeconds(1)) ? failure : new TimeoutException("Peer reader never entered."));
    }
    internal static void Run()
    {
        byte[][] bytes = Enumerable.Range(0, 8).Select(n => ReplayTransportChecks.Fixture(codedPayload: Enumerable.Repeat((byte)(0xa0 + n), 70000).ToArray())).ToArray();
        var expected = bytes.Select(b => ReplayTransportReader.Read(new MemoryStream(b), b.Length, 60, true)).ToArray();
        foreach (int workers in new[] { 1, 4 })
        {
            var streams = bytes.Select(b => new ObservedStream(b)).ToArray(); var timing = new ReplayTransportBatchTiming();
            int owner = Environment.CurrentManagedThreadId, checks = 0;
            try
            {
                var actual = ReplayTransportBatchIndex.Read(streams.Select(s => new ReplayTransportSource(s, s.Length)).ToArray(), 60, true, workers,
                    () => { checks++; Require(Environment.CurrentManagedThreadId == owner, "Worker invoked owner storage callback."); }, timing);
                for (int n = 0; n < actual.Length; n++)
                    Require(actual[n].Sha256 == expected[n].Sha256 && actual[n].Bytes == expected[n].Bytes
                        && actual[n].Pictures.SequenceEqual(expected[n].Pictures) && actual[n].Audio.SequenceEqual(expected[n].Audio)
                        && actual[n].AudioFrames.SequenceEqual(expected[n].AudioFrames) && actual[n].Timing.SequenceEqual(expected[n].Timing)
                        && actual[n].Sps.Span.SequenceEqual(expected[n].Sps.Span) && actual[n].Pps.Span.SequenceEqual(expected[n].Pps.Span),
                        "Batch changed source ordering or complete transport evidence.");
                Require(timing.Joined && timing.FinishedSources == 8 && timing.Workers == workers && timing.PeakActive is >= 1 and <= 4
                    && checks >= 3 && timing.WorkerAllocatedBytes > 0 && timing.ProcessAllocatedBytes >= timing.WorkerAllocatedBytes
                    && streams.All(s => s.Busy == 0 && s.CanRead), "Batch escaped readers, concealed worker allocation or disposed caller streams.");
                using var json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { batch = timing }));
                var recorded = json.RootElement.GetProperty("batch");
                Require(recorded.GetProperty("Joined").GetBoolean() && recorded.GetProperty("Workers").GetInt32() == workers
                    && recorded.GetProperty("PeakActive").GetInt32() == timing.PeakActive
                    && recorded.GetProperty("WorkerAllocatedBytes").GetInt64() == timing.WorkerAllocatedBytes,
                    "Serialized batch receipt lost worker closure, concurrency or allocation evidence.");
            }
            finally { foreach (var stream in streams) stream.Dispose(); }
        }
        foreach (bool damage in new[] { false, true })
        {
            var test = bytes.ToArray(); test[0] = ReplayTransportChecks.Fixture(videoGap: true);
            if (damage) { test[^1] = bytes[^1].ToArray(); test[^1][0] = 0; }
            var streams = test.Select(b => new ObservedStream(b)).ToArray(); var timing = new ReplayTransportBatchTiming();
            try
            {
                Exception error = Failure(() => ReplayTransportBatchIndex.Read(streams.Select(s => new ReplayTransportSource(s, s.Length)).ToArray(), 60, true, 4, null, timing));
                Require(damage ? error is IOException : error is NotSupportedException, "Valid unsupported result hid source corruption or became a false corruption.");
                Require(timing.Joined && streams.All(s => s.Busy == 0 && s.CanRead)
                    && (damage ? timing.FinishedSources is >= 1 and <= 8 : timing.FinishedSources == 8 && streams.All(s => s.ReadCalls > 0)),
                    "Unsupported eligibility canceled remaining sources or fatal failure returned before worker drain.");
            }
            finally { foreach (var stream in streams) stream.Dispose(); }
        }
        using (var release = new ManualResetEventSlim())
        {
            var order = new System.Collections.Concurrent.ConcurrentQueue<int>();
            var streams = new[] { new ObservedStream(bytes[0], () =>
                { if (!release.Wait(TimeSpan.FromSeconds(2))) throw new TimeoutException("Reverse completion test did not release reader."); }, () => order.Enqueue(0)),
                new ObservedStream(bytes[1], endRead: () => { order.Enqueue(1); release.Set(); }) };
            try
            {
                var actual = ReplayTransportBatchIndex.Read(streams.Select(s => new ReplayTransportSource(s, s.Length)).ToArray(), 60, true, 2, null, new());
                Require(order.SequenceEqual(new[] { 1, 0 }) && actual[0].Sha256 == expected[0].Sha256 && actual[1].Sha256 == expected[1].Sha256,
                    "Reverse source completion changed result ordinals.");
            }
            finally { release.Set(); foreach (var stream in streams) stream.Dispose(); }
        }
        foreach (Exception injected in new Exception[] { new IOException("Original reader error."), new InvalidOperationException("Unexpected reader error."), new OperationCanceledException("Unrelated reader cancellation.") })
        {
            using var source = new ObservedStream(bytes[0], () => throw injected); var timing = new ReplayTransportBatchTiming();
            var error = Failure(() => ReplayTransportBatchIndex.Read([new(source, source.Length)], 60, true, 1, null, timing));
            Require(ReferenceEquals(error, injected) && timing.Joined && source.Busy == 0 && source.CanRead,
                "Batch suppressed unrelated cancellation/runtime error or replaced the original source failure.");
        }
        using (var source = new ObservedStream(bytes[0]))
        {
            var canceled = new OperationCanceledException("Owner canceled before launch."); var timing = new ReplayTransportBatchTiming();
            var error = Failure(() => ReplayTransportBatchIndex.Read([new(source, source.Length)], 60, true, 1, () => throw canceled, timing));
            Require(ReferenceEquals(error, canceled) && timing.Joined && timing.Workers == 0 && source.ReadCalls == 0 && source.CanRead,
                "Cancellation before launch started a reader or lost caller ownership.");
        }
        using (var started = new CountdownEvent(2))
        using (var release = new ManualResetEventSlim())
        {
            var primary = new IOException("Owner resource failure."); var secondary = new IOException("Worker read failure.");
            var streams = Enumerable.Range(0, 2).Select(n => new ObservedStream(bytes[n], () =>
            {
                started.Signal(); if (!release.Wait(TimeSpan.FromSeconds(2))) throw new TimeoutException("Unit test failed to release readers.");
                if (n == 1) throw secondary;
            })).ToArray();
            var timing = new ReplayTransportBatchTiming(); int owner = Environment.CurrentManagedThreadId;
            try
            {
                var error = Failure(() => ReplayTransportBatchIndex.Read(streams.Select(s => new ReplayTransportSource(s, s.Length)).ToArray(), 60, true, 2, () =>
                {
                    Require(Environment.CurrentManagedThreadId == owner, "Drain callback escaped its owner.");
                    if (started.CurrentCount == 0) { release.Set(); throw primary; }
                }, timing));
                Require(ReferenceEquals(error, primary) && timing.Joined && timing.PeakActive == 2 && streams.All(s => s.Busy == 0 && s.CanRead),
                    "Owner failure was replaced, active readers escaped or caller ownership was lost.");
            }
            finally { release.Set(); foreach (var stream in streams) stream.Dispose(); }
        }
        using (var shared = new MemoryStream(bytes[0]))
            Require(Failure(() => ReplayTransportBatchIndex.Read([new(shared, shared.Length), new(shared, shared.Length)], 60, true, 2, null, new())) is IOException,
                "Two workers were allowed to race the same stream.");
        {
            var streams = bytes.Select(b => new MemoryStream(b)).ToArray(); var timing = new ReplayTransportBatchTiming();
            try
            {
                var actual = ReplayTransportBatchIndex.Read(streams.Select(s => new ReplayTransportSource(s, s.Length)).ToArray(), 60, true, 4, null, timing, cancellableReads: true);
                Require(actual.Select(i => i.Sha256).SequenceEqual(expected.Select(i => i.Sha256)) && timing.CancellableReads
                    && timing.Joined && !timing.DeadlineExceeded && streams.All(s => s.CanRead),
                    "Cancellable reads changed source evidence or took ownership of caller streams.");
            }
            finally { foreach (var stream in streams) stream.Dispose(); }
        }
        using (var stream = new PendingAsyncStream(bytes[0], ignoreCancellation: false))
        {
            var timing = new ReplayTransportBatchTiming(); int checks = 0, owner = Environment.CurrentManagedThreadId;
            var error = Failure(() => ReplayTransportBatchIndex.Read([new(stream, stream.Length)], 60, true, 1, () =>
            {
                Require(Environment.CurrentManagedThreadId == owner, "Async source invoked an external guard on a worker.");
                if (++checks == 2) Require(stream.Entered.Task.Wait(TimeSpan.FromSeconds(1)), "Async reader failed to enter.");
            }, timing, cancellableReads: true, readDeadline: TimeSpan.FromMilliseconds(50)));
            var observedToken = stream.ObservedToken;
            Require(error is TimeoutException && timing.DeadlineExceeded && timing.Joined && stream.Pending == 0
                && stream.CanRead && observedToken.CanBeCanceled && observedToken.IsCancellationRequested,
                "Deadline did not cancel/drain pending asynchronous I/O before returning caller ownership.");
        }
        using (var pending = new PendingAsyncStream(bytes[0], ignoreCancellation: false))
        {
            var original = new IOException("Second async reader failed while its peer was pending.");
            using var fault = new FaultAfterPeerStream(bytes[1], pending.Entered.Task, original);
            var timing = new ReplayTransportBatchTiming(); int owner = Environment.CurrentManagedThreadId;
            var error = Failure(() => ReplayTransportBatchIndex.Read([new(pending, pending.Length), new(fault, fault.Length)], 60, true, 2,
                () => Require(Environment.CurrentManagedThreadId == owner, "Peer failure moved resource checks to an I/O worker."),
                timing, cancellableReads: true));
            Require(ReferenceEquals(error, original) && timing.Joined && !timing.DeadlineExceeded && pending.Pending == 0
                && pending.CanRead && fault.CanRead, "Async worker error did not cancel/join its pending peer or preserve the original error/ownership.");
        }
        using (var stream = new PendingAsyncStream(bytes[0], ignoreCancellation: true))
        {
            var timing = new ReplayTransportBatchTiming(); int checks = 0, drainChecks = 0;
            var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = Task.Run(async () =>
            {
                try
                {
                    await drained.Task.WaitAsync(TimeSpan.FromSeconds(2));
                    Require(stream.Pending == 1 && stream.CanRead, "Uncooperative read was closed/abandoned before actual completion.");
                }
                finally { stream.Release.TrySetResult(0); }
            });
            Exception error;
            try
            {
                error = Failure(() => ReplayTransportBatchIndex.Read([new(stream, stream.Length)], 60, true, 1, () =>
                {
                    if (++checks == 2) Require(stream.Entered.Task.Wait(TimeSpan.FromSeconds(1)), "Uncooperative reader failed to enter.");
                    if (timing.DeadlineExceeded && stream.Pending == 1 && ++drainChecks >= 2) drained.TrySetResult();
                }, timing, cancellableReads: true, readDeadline: TimeSpan.FromMilliseconds(50)));
            }
            finally { stream.Release.TrySetResult(0); release.GetAwaiter().GetResult(); }
            Require(error is TimeoutException && timing.Joined && timing.DeadlineExceeded && drainChecks >= 2
                && stream.Pending == 0 && stream.CanRead && timing.DrainError != null,
                "Expired deadline skipped owner resource checks or released an unjoined async reader.");
        }
        Console.WriteLine("Passed: experimental bounded batch indexing preserves all source evidence/order, owner-only guards, joined cancellation, unsupported/damage precedence and caller stream ownership; non-playable fixtures only.");
    }
}
