using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;

namespace F10Recorder;

internal sealed class ReplayExportProcessReceipt
{
    public int? ProcessId { get; internal set; }
    public DateTime? ProcessStartedUtc { get; internal set; }
    public int? ExitCode { get; internal set; }
    public bool ProducerAbsent { get; internal set; } = true;
    public bool ActualExitObserved { get; internal set; }
    public bool TimedOut { get; internal set; }
    public bool OutputLimitExceeded { get; internal set; }
    public bool CaptureComplete { get; internal set; }
    public int CapturedBytes { get; internal set; }
    public string StandardOutput { get; internal set; } = "";
    public string StandardError { get; internal set; } = "";
    public long ElapsedMilliseconds { get; internal set; }
    public string Failure { get; internal set; }
    public string Error { get; internal set; }
    public bool Success => Failure == null && ProducerAbsent && ActualExitObserved && ExitCode == 0 && CaptureComplete;
}

// Only managed, verified media-engine tools enter this route. It owns the exact
// child handle, bounded diagnostics and the executable lease through child exit.
internal static class ReplayExportProcess
{
    const int MaximumOutputBytes = 2097152;
    const int StopWaitMilliseconds = 5000;

    internal static ReplayExportProcessReceipt Run(string executable, IReadOnlyList<string> arguments, string workingDirectory,
        Native.ProcessJob job, Action<Process> started = null, Action<bool> producerStopped = null,
        int timeoutMilliseconds = 120000, int outputLimitBytes = MaximumOutputBytes, Action checkStorage = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (timeoutMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
        if (outputLimitBytes <= 0 || outputLimitBytes > MaximumOutputBytes) throw new ArgumentOutOfRangeException(nameof(outputLimitBytes));
        var receipt = new ReplayExportProcessReceipt();
        var elapsed = Stopwatch.StartNew();
        Process process = null;
        MediaDependency.ExecutableLease lease = null;
        var streams = new Stream[2];
        var reads = new Task<int>[2];
        var buffers = new[] { new byte[4096], new byte[4096] };
        var ended = new bool[2];
        using var stdout = new MemoryStream();
        using var stderr = new MemoryStream();
        using var cancellation = new CancellationTokenSource();
        void ObservedWork(Action work)
        {
            if (checkStorage == null) { work(); return; }
            // CreateProcess, executable verification and durable producer writes
            // can block while the output grows. Keep the SAME real storage guard
            // running on this owner thread instead of spending its freshness
            // window waiting for lifecycle work and then a competing cache lock.
            var pendingWork = Task.Run(work);
            Exception failure = null;
            try
            {
                while (!pendingWork.IsCompleted)
                {
                    Thread.Sleep(25);
                    if (!pendingWork.IsCompleted)
                    {
                        checkStorage();
                        if (elapsed.ElapsedMilliseconds >= timeoutMilliseconds)
                        {
                            receipt.TimedOut = true;
                            throw new IOException("Recording-engine lifecycle work exceeded its deadline.");
                        }
                    }
                }
            }
            catch (Exception error) { failure = error; }
            // Always join, including guard failure. A launch may already own a
            // child: cleanup needs its actual handle, never an orphaned Task/PID.
            try { pendingWork.GetAwaiter().GetResult(); }
            catch (Exception error) { failure ??= error; }
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
            checkStorage();
        }
        try
        {
            checkStorage?.Invoke();
            ObservedWork(() => lease = MediaDependency.OpenVerifiedExecutable(executable));
            // Revoke closed-output claims before CreateProcess can create output.
            ObservedWork(() => producerStopped?.Invoke(false));
            receipt.ProducerAbsent = false;
            ObservedWork(() =>
            {
                process = CaptureSession.StartVerifiedProcess(lease, arguments)
                    ?? throw new IOException("The recording-engine process did not start.");
                receipt.ProcessId = process.Id;
                receipt.ProcessStartedUtc = process.StartTime.ToUniversalTime();
                streams[0] = process.StandardOutput.BaseStream;
                streams[1] = process.StandardError.BaseStream;
                job?.Add(process);
            });
            ObservedWork(() => started?.Invoke(process));
            var logs = new[] { stdout, stderr };
            long storageChecked = Stopwatch.GetTimestamp();
            while (true)
            {
                if (Stopwatch.GetElapsedTime(storageChecked).TotalMilliseconds >= 200)
                {
                    checkStorage?.Invoke();
                    storageChecked = Stopwatch.GetTimestamp();
                }
                for (int i = 0; i < streams.Length; i++)
                {
                    if (ended[i]) continue;
                    reads[i] ??= streams[i].ReadAsync(buffers[i], 0, buffers[i].Length, cancellation.Token);
                    if (!reads[i].IsCompleted) continue;
                    int count = reads[i].GetAwaiter().GetResult();
                    reads[i] = null;
                    if (count == 0) { ended[i] = true; continue; }
                    int keep = Math.Min(count, outputLimitBytes - receipt.CapturedBytes);
                    logs[i].Write(buffers[i], 0, keep);
                    receipt.CapturedBytes += keep;
                    if (keep != count)
                    {
                        receipt.OutputLimitExceeded = true;
                        receipt.Failure = "output-limit";
                        receipt.Error = "Recording-engine diagnostics exceeded the combined byte limit.";
                        break;
                    }
                }
                if (receipt.Failure != null) break;
                if (ended[0] && ended[1] && process.WaitForExit(0))
                {
                    receipt.CaptureComplete = true;
                    break;
                }
                if (elapsed.ElapsedMilliseconds >= timeoutMilliseconds)
                {
                    receipt.TimedOut = true;
                    receipt.Failure = "timeout";
                    receipt.Error = "The recording-engine process or diagnostic drains exceeded the deadline.";
                    break;
                }
                Thread.Sleep(5);
            }
        }
        catch (Exception error) when (ExpectedFailure(error))
        {
            receipt.Failure = receipt.TimedOut ? "timeout" : process == null ? "launch-failed" : "process-failed";
            receipt.Error = error.Message;
        }
        finally
        {
            if (process != null)
            {
                try
                {
                    if (!process.WaitForExit(0))
                    {
                        process.Kill(); // Exact owned child only; never a PID/name lookup.
                        receipt.ActualExitObserved = process.WaitForExit(StopWaitMilliseconds);
                    }
                    else receipt.ActualExitObserved = true;
                    if (receipt.ActualExitObserved) receipt.ExitCode = process.ExitCode;
                }
                catch (Exception error) when (ExpectedFailure(error))
                {
                    receipt.Failure ??= "process-failed";
                    receipt.Error = JoinError(receipt.Error, "Producer exit observation failed: " + error.Message);
                    // A kill can race ordinary exit; ask the same owned handle once more.
                    try
                    {
                        receipt.ActualExitObserved = process.WaitForExit(0);
                        if (receipt.ActualExitObserved) receipt.ExitCode = process.ExitCode;
                    }
                    catch (Exception observation) when (ExpectedFailure(observation)) { }
                }
                receipt.ProducerAbsent = receipt.ActualExitObserved;
                if (!receipt.ProducerAbsent)
                {
                    receipt.Failure = "producer-absence-unverified";
                    receipt.Error = JoinError(receipt.Error, "The owned recording-engine process did not confirm exit within five seconds.");
                }
            }
            else receipt.ProducerAbsent = true;

            // Cancel incomplete pipe reads without awaiting an inherited/open pipe.
            cancellation.Cancel();
            for (int i = 0; i < streams.Length; i++)
            {
                try { streams[i]?.Dispose(); }
                catch (IOException) { }
                if (reads[i] != null)
                    _ = reads[i].ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            if (receipt.ProducerAbsent)
            {
                process?.Dispose();
                lease?.Dispose();
            }
            else RetainUntilExit(process, lease);
            try { producerStopped?.Invoke(receipt.ProducerAbsent); }
            catch (Exception error) when (ExpectedFailure(error))
            {
                receipt.Failure ??= "process-failed";
                receipt.Error = JoinError(receipt.Error, "Producer-state callback failed: " + error.Message);
            }
            receipt.StandardOutput = Encoding.UTF8.GetString(stdout.GetBuffer(), 0, checked((int)stdout.Length));
            receipt.StandardError = Encoding.UTF8.GetString(stderr.GetBuffer(), 0, checked((int)stderr.Length));
            receipt.ElapsedMilliseconds = elapsed.ElapsedMilliseconds;
        }
        if (receipt.Failure == null && receipt.ExitCode != 0)
        {
            receipt.Failure = "exit-nonzero";
            receipt.Error = "The recording-engine process exited with code " + receipt.ExitCode + ".";
        }
        if (receipt.Failure == null && !receipt.CaptureComplete)
        {
            receipt.Failure = "capture-incomplete";
            receipt.Error = "Recording-engine diagnostics were not completely drained.";
        }
        return receipt;
    }

    static bool ExpectedFailure(Exception error) => error is IOException or UnauthorizedAccessException or Win32Exception
        or InvalidOperationException or ArgumentException or NotSupportedException or OperationCanceledException;
    static string JoinError(string before, string next) => string.IsNullOrEmpty(before) ? next : before + " " + next;

    // An unconfirmed stop must keep the verified file pinned. Keep a strong root
    // even if event registration fails; releasing the lease requires owned exit
    // evidence. No delayed callback may overwrite a subsequent export's state.
    static readonly object pendingGate = new();
    static readonly Dictionary<Process, IDisposable> pending = new();
    static void RetainUntilExit(Process process, IDisposable lease)
    {
        lock (pendingGate) pending.Add(process, lease);
        void ReleaseIfExited(object sender, EventArgs args)
        {
            lock (pendingGate)
            {
                if (!pending.ContainsKey(process)) return;
                try { if (!process.WaitForExit(0)) return; }
                catch (Exception error) when (ExpectedFailure(error)) { return; }
                pending.Remove(process);
                try { process.Dispose(); }
                catch (Exception error) when (ExpectedFailure(error)) { }
                try { lease?.Dispose(); }
                catch (Exception error) when (ExpectedFailure(error)) { }
            }
        }
        try
        {
            process.Exited += ReleaseIfExited;
            process.EnableRaisingEvents = true;
            ReleaseIfExited(null, EventArgs.Empty);
        }
        catch (Exception error) when (ExpectedFailure(error)) { }
    }
}
