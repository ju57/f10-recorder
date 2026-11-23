using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace F10Recorder;

internal sealed class ReplayStagedProcessTiming
{
    [JsonInclude] public long Started, Ready, FeedAuthorized, FeedStarted, FeedCompleted, RunCompleted, Joined;
    [JsonInclude] public long FedBytes;
    [JsonInclude] public int GuardRequests, OwnerCallbacks;
    [JsonInclude] public double MaximumGuardRoundTripMilliseconds;
    [JsonInclude] public string FeedSha256, Error;
    [JsonInclude] public bool Aborted, FeedJoined;
}

// A single accepted Save owns this operation. The verified existing launcher
// owns process/diagnostic IO on its worker; native/storage callbacks are relayed
// to the original Save owner. No worker performs a Cache/native-scope callback.
// Callers retain all source/native capabilities until Stopped and actual joins.
internal sealed class ReplayStagedProcess : IDisposable
{
    internal delegate ReplayExportProcessReceipt Launcher(Action guard, Action<Stream, Action> ready, IDisposable retainedInput);
    sealed class GuardRequest
    {
        internal readonly long Started = Stopwatch.GetTimestamp();
        internal readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    // The normal launcher borrows inputs. On unknown child exit it retains this
    // passive strong root; an exit callback must never dispose owner-thread
    // native capabilities or a still-running feeder on a background thread.
    sealed class Retention(ReplayStagedProcess owner) : IDisposable
    {
        readonly ReplayStagedProcess strongOwner = owner;
        public void Dispose() { GC.KeepAlive(strongOwner); }
    }
    static readonly List<ReplayStagedProcess> uncertain = [];
    readonly int ownerThread = Environment.CurrentManagedThreadId;
    readonly Action check;
    readonly int deadlineMilliseconds;
    readonly int cleanupMilliseconds;
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly ConcurrentQueue<GuardRequest> guards = new();
    readonly TaskCompletionSource<Stream> input = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly CancellationTokenSource feedCancellation = new();
    readonly Task<ReplayExportProcessReceipt> execution;
    readonly object failureGate = new();
    readonly List<object> borrowed = [];
    volatile Task feeder;
    Action stopChild;
    Exception failure;
    volatile bool feeding;
    bool disposed, pumping, joinedSuccessfully;
    internal readonly ReplayStagedProcessTiming Timing = new();

    internal ReplayStagedProcess(string executable, IReadOnlyList<string> arguments, string directory, Native.ProcessJob job,
        Action requireAccepted, Action guard, Action<Process> started, Action<bool> producerStopped,
        object retainedOwner, int timeoutMilliseconds = 20000, int outputLimitBytes = 65536)
        : this((check, ready, retained) => ReplayExportProcess.Run(executable, arguments, directory, job,
            started: process => { started?.Invoke(process); ready(process.StandardInput.BaseStream, () => { if (!process.HasExited) process.Kill(); }); },
            producerStopped: producerStopped, timeoutMilliseconds: timeoutMilliseconds, outputLimitBytes: outputLimitBytes,
            checkStorage: check, inputLease: retained), requireAccepted, guard, timeoutMilliseconds, retainedOwner) { }

    // Injectable lifecycle transport permits failure/ownership controls without
    // starting a process. The normal constructor always uses the real launcher.
    internal ReplayStagedProcess(Launcher launch, Action requireAccepted, Action guard, int timeoutMilliseconds, object retainedOwner, int cleanupTimeoutMilliseconds = 10000)
    {
        ArgumentNullException.ThrowIfNull(launch); ArgumentNullException.ThrowIfNull(requireAccepted); ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(retainedOwner);
        if (timeoutMilliseconds <= 0 || cleanupTimeoutMilliseconds <= 0 || cleanupTimeoutMilliseconds > 10000) throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
        requireAccepted(); // Must prove the actual accepted request/claim, not a timestamp or a check-mode flag.
        borrowed.Add(retainedOwner);
        check = guard; deadlineMilliseconds = timeoutMilliseconds; cleanupMilliseconds = cleanupTimeoutMilliseconds; Timing.Started = Stopwatch.GetTimestamp();
        execution = Task.Run(() => {
            try { return launch(RequestGuard, (stream, stop) => {
                ArgumentNullException.ThrowIfNull(stream); ArgumentNullException.ThrowIfNull(stop);
                if (Interlocked.CompareExchange(ref stopChild, stop, null) != null) throw new IOException("Staged process became ready twice.");
                Timing.Ready = Stopwatch.GetTimestamp();
                if (!input.TrySetResult(stream)) throw new IOException("Staged input became ready twice.");
            }, new Retention(this)); }
            finally { Timing.RunCompleted = Stopwatch.GetTimestamp(); }
        });
    }

    void Owner()
    {
        if (Environment.CurrentManagedThreadId != ownerThread || disposed) throw new IOException("Staged operation requires its live original Save owner.");
    }
    void Fail(Exception error)
    {
        lock (failureGate) { failure ??= error; Timing.Error ??= ReplayOutputDemand.Bounded(error.ToString(), 8192); }
    }
    void ThrowFailure()
    {
        Exception error; lock (failureGate) error = failure;
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
    }
    void RequestGuard()
    {
        if (Environment.CurrentManagedThreadId == ownerThread) throw new IOException("Launcher guard did not use its worker transport.");
        var request = new GuardRequest(); Interlocked.Increment(ref Timing.GuardRequests); guards.Enqueue(request);
        while (!request.Completion.Task.IsCompleted)
        {
            if (Stopwatch.GetElapsedTime(request.Started).TotalMilliseconds >= 500)
            {
                var error = new IOException("Staged guard owner round trip exceeded500ms; evidence cannot be refreshed by a late callback.");
                Fail(error); request.Completion.TrySetException(error); break;
            }
            Thread.Sleep(2);
        }
        request.Completion.Task.GetAwaiter().GetResult();
    }
    internal void Pump() => Pump(false);
    void Pump(bool cleanup)
    {
        Owner();
        if (pumping) throw new IOException("Reentrant staged owner pump.");
        pumping = true;
        try
        {
            while (guards.TryDequeue(out var request))
            {
                // A timed-out/skipped request never invokes a healthy check and
                // never renews its evidence. The first failure remains sticky.
                if (request.Completion.Task.IsCompleted) continue;
                try
                {
                    if (Stopwatch.GetElapsedTime(request.Started).TotalMilliseconds >= 500) throw new IOException("Staged guard was already expired before observation.");
                    ThrowFailure(); check(); Timing.OwnerCallbacks++;
                    double elapsed = Stopwatch.GetElapsedTime(request.Started).TotalMilliseconds;
                    Timing.MaximumGuardRoundTripMilliseconds = Math.Max(Timing.MaximumGuardRoundTripMilliseconds, elapsed);
                    if (elapsed >= 500) throw new IOException("Staged guard exceeded500ms including actual checking.");
                    request.Completion.TrySetResult();
                }
                catch (Exception error) { Fail(error); request.Completion.TrySetException(error); }
            }
            if (!joinedSuccessfully && clock.ElapsedMilliseconds >= deadlineMilliseconds) Fail(new IOException("Staged process whole operation deadline exceeded."));
            if (feeder?.IsFaulted == true) { try { feeder.GetAwaiter().GetResult(); } catch (Exception error) { Fail(error); } }
            if (execution.IsFaulted) { try { execution.GetAwaiter().GetResult(); } catch (Exception error) { Fail(error); } }
            if (!cleanup) ThrowFailure();
        }
        finally { pumping = false; }
    }

    internal void WaitForReady()
    {
        Owner();
        while (!input.Task.IsCompleted)
        {
            Pump();
            if (execution.IsCompleted) throw new IOException("Boundary process exited before input became ready.");
            check(); Thread.Sleep(2);
        }
        Pump(); input.Task.GetAwaiter().GetResult();
    }

    internal void Feed(Stream source, long length, string expectedSha256, Action verifyProtectedCandidate)
    {
        Owner();
        try
        {
        ThrowFailure(); ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(verifyProtectedCandidate);
        if (feeding || Timing.Aborted || length <= 0 || length > 8388608 || !source.CanRead || !source.CanSeek || source.Length != length || source.Position != 0
            || expectedSha256 == null || expectedSha256.Length != 64 || expectedSha256.Any(c => !Uri.IsHexDigit(c)))
            throw new IOException("Staged input requires one bounded immutable independently indexed source.");
        verifyProtectedCandidate();
        while (!input.Task.IsCompleted)
        {
            Pump(); if (execution.IsCompleted) throw new IOException("Boundary process exited before input became ready.");
            Thread.Sleep(2);
        }
        Pump(); verifyProtectedCandidate(); // Recheck after launch waiting, before releasing any byte.
        if (execution.IsCompleted) throw new IOException("Boundary process exited before feed.");
        Stream sink = input.Task.GetAwaiter().GetResult();
        borrowed.Add(source); feeding = true; Timing.FeedAuthorized = Stopwatch.GetTimestamp();
        feeder = Task.Run(async () => {
            byte[] buffer = new byte[65536]; using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Timing.FeedStarted = Stopwatch.GetTimestamp();
            try
            {
                long total = 0;
                while (total < length)
                {
                    int count = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - total)), feedCancellation.Token).ConfigureAwait(false);
                    if (count == 0) throw new IOException("Protected staged input was truncated.");
                    hash.AppendData(buffer, 0, count);
                    await sink.WriteAsync(buffer.AsMemory(0, count), feedCancellation.Token).ConfigureAwait(false);
                    total = checked(total + count); Interlocked.Exchange(ref Timing.FedBytes, total);
                }
                if (source.Length != length) throw new IOException("Protected staged input extent changed.");
                Timing.FeedSha256 = Convert.ToHexString(hash.GetHashAndReset());
                if (!Timing.FeedSha256.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("Staged bytes differ from the protected source index.");
                await sink.FlushAsync(feedCancellation.Token).ConfigureAwait(false);
            }
            finally { sink.Dispose(); Timing.FeedCompleted = Stopwatch.GetTimestamp(); }
        });
        }
        catch (Exception error) { Fail(error); throw; }
    }

    internal bool Stopped => execution.IsCompletedSuccessfully && execution.Result.ProducerAbsent
        && (execution.Result.ActualExitObserved || execution.Result is { ProcessId: null, Failure: "launch-failed" })
        && (!feeding || feeder is { IsCompleted: true });
    internal ReplayExportProcessReceipt ObservedReceipt => execution.IsCompletedSuccessfully ? execution.Result : null;

    internal ReplayExportProcessReceipt Join()
    {
        Owner();
        try
        {
            if (!feeding || Timing.Aborted) throw new IOException("Unfed or aborted staged process cannot certify a boundary.");
            while (!execution.IsCompleted || feeder is { IsCompleted: false }) { Pump(); Thread.Sleep(2); }
            Pump(); feeder.GetAwaiter().GetResult();
            var receipt = execution.GetAwaiter().GetResult();
            if (!Stopped || !receipt.Success) throw new IOException("Staged boundary failed: " + receipt.Error + " " + receipt.StandardError);
            joinedSuccessfully = true;
            Timing.FeedJoined = true; Timing.Joined = Stopwatch.GetTimestamp(); return receipt;
        }
        catch (Exception error)
        {
            Fail(error); try { AbortAndJoin(); } catch (Exception) { }
            throw;
        }
    }
    internal void AbortAndJoin()
    {
        Owner(); Timing.Aborted = true; feedCancellation.Cancel(); var cleanup = Stopwatch.StartNew();
        while (!execution.IsCompleted || feeder is { IsCompleted: false })
        {
            try { Volatile.Read(ref stopChild)?.Invoke(); } catch (Exception error) { Fail(error); }
            Pump(true);
            if (cleanup.ElapsedMilliseconds >= cleanupMilliseconds)
            {
                var error = new IOException("Staged launch/feeder/child closure is uncertain; all owners remain retained.");
                Fail(error); Retain(); throw error;
            }
            Thread.Sleep(2);
        }
        Pump(true);
        try { feeder?.GetAwaiter().GetResult(); } catch (Exception error) { Fail(error); }
        try { execution.GetAwaiter().GetResult(); } catch (Exception error) { Fail(error); }
        if (!Stopped) { var error = new IOException("Staged producer absence is unconfirmed."); Fail(error); Retain(); throw error; }
        Timing.FeedJoined = true; Timing.Joined = Stopwatch.GetTimestamp();
    }
    void Retain() { lock (uncertain) if (!uncertain.Contains(this)) uncertain.Add(this); }
    internal bool RetainedForChecks { get { lock (uncertain) return uncertain.Contains(this); } }
    public void Dispose()
    {
        if (disposed) return;
        Owner();
        try { if (!Stopped || !Timing.FeedJoined) AbortAndJoin(); }
        catch { Retain(); throw; }
        feedCancellation.Dispose(); disposed = true; borrowed.Clear();
    }
}
