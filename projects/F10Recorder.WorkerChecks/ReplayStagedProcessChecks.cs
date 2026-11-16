using System.Diagnostics;
using System.Security.Cryptography;

namespace F10Recorder;

internal static class ReplayStagedProcessChecks
{
    static void Need(bool ok, string text) { if (!ok) throw new IOException(text); }
    static void Reject(Action action, string text)
    { try { action(); } catch (IOException) { return; } throw new IOException(text); }
    sealed class Source(byte[] bytes) : MemoryStream(bytes, false)
    {
        internal bool Closed;
        protected override void Dispose(bool disposing) { Closed = true; base.Dispose(disposing); }
    }
    sealed class Clock : TimeProvider
    {
        internal long Ticks;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Ticks;
    }
    sealed class Sink : MemoryStream
    {
        internal readonly TaskCompletionSource Closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource WriteEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleaseWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Block;
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WriteEntered.TrySetResult();
            if (Block) await ReleaseWrite.Task.ConfigureAwait(false); // Explicitly ignores cancellation until exact fake-child abort.
            await base.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        protected override void Dispose(bool disposing) { Closed.TrySetResult(); base.Dispose(disposing); }
    }
    static ReplayExportProcessReceipt Receipt(bool stopped = true) => new()
    { ProducerAbsent = stopped, ActualExitObserved = stopped, ExitCode = stopped ? 0 : null, CaptureComplete = stopped, ResourcesFinal = stopped };
    static ReplayStagedProcess.Launcher Transport(Sink sink, Action readyObserved = null) => (guard, ready, _) => {
        try
        {
            guard(); ready(sink, () => { sink.ReleaseWrite.TrySetException(new IOException("Exact fake child stopped.")); sink.Dispose(); }); readyObserved?.Invoke();
            while (!sink.Closed.Task.IsCompleted) { guard(); Thread.Sleep(1); }
            return Receipt();
        }
        catch (Exception error) { return new() { ProducerAbsent = true, Failure = "launch-failed", Error = error.Message }; }
    };
    static void WaitUntil(ReplayStagedProcess operation, Func<bool> done)
    {
        var clock = Stopwatch.StartNew();
        while (!done()) { operation.Pump(); Need(clock.ElapsedMilliseconds < 1000, "Fake operation did not progress."); Thread.Sleep(1); }
    }
    internal static void Run()
    {
        var tailClock = new Clock(); int tailPumps = 0;
        var neverReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Reject(() => ReplaySavePreparation.WaitForTail(neverReady.Task, tailClock, 0, () => { tailPumps++; tailClock.Ticks += 1000; }, () => "Controlled tail."), "Pending notification outlived original five-second tail budget.");
        Need(tailPumps == 5 && !neverReady.Task.IsCompleted, "Tail expiry reset its original clock or completed the borrowed task.");
        var projectedInterval = new ReplayInterval(.25, 1.25, 300);
        ReplayTransportInput Indexed(int ordinal) => new(ordinal, ordinal + 1,
            new(11280, "metadata-source", Enumerable.Range(0, 60).Select(p => new ReplayTransportPicture(p * 188, ordinal * 90000L + p * 1500, p == 0, 1)).ToArray(), [], [], new byte[] { 1 }, new byte[] { 2 }));
        var wholeInputs = new[] { Indexed(0), Indexed(1) };
        var wholePlan = ReplayHybridPlan.Create(wholeInputs, projectedInterval, 60, false);
        var projected = ReplayBoundaryProjection.Create(0, wholeInputs[0], wholeInputs[0].Index, projectedInterval, 60);
        ReplaySavePreparation.RequireFinalBoundary(projected, wholePlan, wholeInputs[0], projectedInterval, 60);
        foreach (var changed in new[] { wholeInputs[0].Index with { Sha256 = "changed" }, wholeInputs[0].Index with { Bytes = 11281 }, wholeInputs[0].Index with { Pps = new byte[] { 3 } } })
            Reject(() => ReplaySavePreparation.RequireFinalBoundary(projected, wholePlan, wholeInputs[0] with { Index = changed }, projectedInterval, 60), "Final reindex changed source while retaining plan parts.");
        var extent = new ReplayFileExtent("volume", "id", 4096, 188, 4096);
        var listed = new ReplayStorage.DirectoryEntry("boundary", FileAttributes.Normal, extent);
        var observed = new ReplayGuardDirectory.Observation(extent, 1, (uint)FileAttributes.Normal);
        ReplaySavePreparation.RequireBoundary(listed, observed);
        foreach (var attributes in new[] { FileAttributes.Directory, FileAttributes.ReparsePoint, FileAttributes.Compressed, FileAttributes.SparseFile })
        {
            Reject(() => ReplaySavePreparation.RequireBoundary(listed with { Attributes = attributes }, observed), "Listed unsupported boundary attributes accepted.");
            Reject(() => ReplaySavePreparation.RequireBoundary(listed, observed with { Attributes = (uint)attributes }), "Held unsupported boundary attributes accepted.");
        }
        Reject(() => ReplaySavePreparation.RequireBoundary(listed with { Extent = extent with { Identity = "wrong" } }, observed), "Wrong boundary identity accepted.");
        Reject(() => ReplaySavePreparation.RequireBoundary(listed, observed with { Links = 2 }), "Boundary hardlink alias accepted.");
        Reject(() => ReplaySavePreparation.RequireBoundary(listed, observed with { Extent = extent with { AllocatedBytes = ReplaySaveExport.BoundaryLimit + 1 } }), "Boundary growth accepted.");
        int owner = Environment.CurrentManagedThreadId, launches = 0;
        Reject(() => new ReplayStagedProcess((_, _, _) => { launches++; return Receipt(); }, () => throw new IOException("Not accepted."), () => { }, 1000, new object()),
            "Rejected admission launched preparation.");
        Need(launches == 0, "Rejected Save performed launch work.");
        byte[] bytes = Enumerable.Range(0, 1024).Select(n => (byte)n).ToArray(); string hash = Convert.ToHexString(SHA256.HashData(bytes));
        using (var source = new Source(bytes))
        using (var sink = new Sink())
        using (var operation = new ReplayStagedProcess(Transport(sink), () => { }, () => Need(Environment.CurrentManagedThreadId == owner, "Guard ran off owner."), 3000, new object()))
        {
            WaitUntil(operation, () => operation.Timing.Ready != 0);
            Need(sink.Length == 0 && !source.Closed && !operation.Stopped, "Preparation consumed media or claimed closure before feed.");
            int verified = 0; operation.Feed(source, bytes.Length, hash, () => { verified++; Need(sink.Length == 0, "Bytes preceded protected candidate verification."); });
            var receipt = operation.Join();
            Need(receipt.Success && operation.Stopped && operation.Timing.FeedJoined && operation.Timing.FedBytes == bytes.Length
                && operation.Timing.FeedSha256 == hash && verified == 2 && !source.Closed && operation.Timing.OwnerCallbacks > 0,
                "Normal staged source/guard/feed/child joins failed.");
        }
        using (var source = new Source(bytes))
        using (var sink = new Sink())
        using (var operation = new ReplayStagedProcess(Transport(sink), () => { }, () => { }, 3000, new object()))
        {
            Reject(() => operation.Feed(source, bytes.Length, hash, () => throw new IOException("Changed protected candidate.")), "Unverified candidate fed media.");
            int retryChecks = 0;
            Reject(() => operation.Feed(source, bytes.Length, hash, () => retryChecks++), "Healthy retry repaired a rejected candidate.");
            Need(retryChecks == 0, "Failed preparation performed a healthy retry check.");
            Need(operation.Timing.FedBytes == 0 && source.Position == 0, "Candidate rejection read/fed data.");
            operation.AbortAndJoin(); Need(operation.Stopped, "Rejected candidate left a producer.");
            Reject(() => operation.Join(), "An aborted operation certified success.");
        }
        using (var source = new Source(bytes))
        using (var sink = new Sink())
        using (var operation = new ReplayStagedProcess(Transport(sink), () => { }, () => { }, 3000, new object()))
        {
            int checks = 0;
            Reject(() => operation.Feed(source, bytes.Length, hash, () => { if (++checks == 2) throw new IOException("Candidate changed while waiting for launch."); }), "Changed second check released media.");
            Need(checks == 2 && operation.Timing.FedBytes == 0 && source.Position == 0, "Second-check failure read/fed data.");
            Reject(() => operation.Feed(source, bytes.Length, hash, () => { }), "Second-check failure repaired by healthy retry.");
            operation.AbortAndJoin(); Need(operation.Stopped, "Second-check rejection left a producer.");
        }
        using (var source = new Source(bytes))
        using (var sink = new Sink())
        using (var operation = new ReplayStagedProcess(Transport(sink), () => { }, () => { }, 3000, new object()))
        {
            operation.Feed(source, bytes.Length, new string('0', 64), () => { });
            Reject(() => operation.Join(), "Changed source hash certified a boundary.");
            Need(operation.Stopped && operation.Timing.FeedJoined && !source.Closed, "Hash failure escaped joined source ownership.");
        }
        using (var source = new Source(bytes))
        using (var sink = new Sink { Block = true })
        using (var operation = new ReplayStagedProcess(Transport(sink), () => { }, () => { }, 3000, new object()))
        {
            operation.Feed(source, bytes.Length, hash, () => { }); WaitUntil(operation, () => sink.WriteEntered.Task.IsCompleted);
            Need(!operation.Stopped && !source.Closed, "Blocked pipe released a borrowed input.");
            operation.AbortAndJoin();
            Need(operation.Stopped && operation.Timing.FeedJoined && !source.Closed, "Pipe cancellation failed to stop/join before release.");
            Reject(() => operation.Join(), "Canceled partial feed certified a boundary.");
        }
        int lateChecks = 0;
        using (var sink = new Sink())
        using (var operation = new ReplayStagedProcess(Transport(sink), () => { }, () => lateChecks++, 3000, new object()))
        {
            Need(SpinWait.SpinUntil(() => operation.Timing.GuardRequests > 0, 1000), "Guard transport did not request owner.");
            Thread.Sleep(530);
            Reject(operation.Pump, "Late owner callback renewed expired evidence.");
            operation.AbortAndJoin(); Need(lateChecks == 0 && operation.Stopped, "Expired callback performed a healthy observation.");
        }
        using (var sink = new Sink())
        using (var operation = new ReplayStagedProcess(Transport(sink), () => { }, () => { }, 3000, new object()))
        {
            var wrongThread = Task.Run(() => { Reject(operation.Pump, "Foreign thread serviced native guard."); }); wrongThread.GetAwaiter().GetResult();
            operation.AbortAndJoin(); Need(operation.Stopped, "Wrong-thread control escaped cleanup.");
        }
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var operation = new ReplayStagedProcess((_, _, _) => { entered.TrySetResult(); release.Task.GetAwaiter().GetResult(); return Receipt(); },
            () => { }, () => { }, 3000, new object(), cleanupTimeoutMilliseconds: 30))
        {
            entered.Task.GetAwaiter().GetResult(); Reject(operation.AbortAndJoin, "Pending launch released uncertain owner.");
            Need(operation.RetainedForChecks && !operation.Stopped, "Unjoined preparation was not strongly retained.");
            release.TrySetResult(); WaitUntilCleanup(operation, () => operation.Stopped);
            Reject(() => operation.Join(), "Later healthy exit repaired a failed preparation.");
        }
        using (var readySink = new Sink())
        using (var operation = new ReplayStagedProcess(Transport(readySink), () => { }, () => { }, 3000, new object()))
        {
            operation.WaitForReady();
            Need(operation.Timing.Ready > 0 && operation.Timing.FedBytes == 0 && operation.Timing.OwnerCallbacks > 0,
                "Readiness wait skipped owner guards or released input.");
            operation.AbortAndJoin(); Need(operation.Stopped, "Ready producer did not join.");
        }
        using (var operation = new ReplayStagedProcess((_, _, _) => Receipt(), () => { }, () => { }, 1000, new object()))
            Reject(operation.WaitForReady, "An exited producer was treated as ready.");
        Console.WriteLine("Passed: staged accepted-only preparation, original-owner guards, readiness/early-exit checks, withheld verified input, exact bytes/hash, blocked pipe cancellation, actual simulated joins, expiry, wrong-thread and uncertain-launch retention controls; no child process/native API/media/device/capture.");
    }
    static void WaitUntilCleanup(ReplayStagedProcess operation, Func<bool> done)
    {
        var clock = Stopwatch.StartNew();
        while (!done()) { Need(clock.ElapsedMilliseconds < 1000, "Fake pending launch did not finish."); Thread.Sleep(1); }
        operation.AbortAndJoin();
    }
}
