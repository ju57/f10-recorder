using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace F10Recorder;

// Artifact I/O must never delay a fresh native storage observation. A full or
// failed queue invalidates the experiment; no row is dropped to preserve a pass.
internal sealed class BoundedCalibrationTrace : IDisposable
{
    const int MaximumRowBytes = 8192;
    readonly long maximumBytes;
    readonly BlockingCollection<byte[]> rows;
    readonly int capacity;
    readonly Thread writer;
    readonly ManualResetEventSlim ready = new();
    string failure;
    long enqueued, written, bytes, persistedBytes;
    int maximumPending, completed, disposed;
    double maximumWriteMilliseconds;
    internal sealed record TraceReceipt(long enqueued, long written, long bytes, long persistedBytes, int maximumPending,
        double maximumWriteMilliseconds, string failure, bool writerAbsent, int capacity, int maximumRowBytes, long maximumBytes, bool final);
    TraceReceipt finalReceipt;
    internal string Failure => Volatile.Read(ref failure);
    internal double MaximumWriteMilliseconds => Volatile.Read(ref maximumWriteMilliseconds);
    internal object Receipt => Volatile.Read(ref finalReceipt) ?? Snapshot(false);
    TraceReceipt Snapshot(bool final) => new(Interlocked.Read(ref enqueued), Interlocked.Read(ref written),
        Interlocked.Read(ref bytes), Interlocked.Read(ref persistedBytes), maximumPending,
        MaximumWriteMilliseconds, Failure, !writer.IsAlive, capacity, MaximumRowBytes, maximumBytes, final);
    void FreezeReceipt() => Interlocked.CompareExchange(ref finalReceipt, Snapshot(true), null);

    internal BoundedCalibrationTrace(string path, Func<Stream> open = null, int capacity = 1024, long maximumBytes = 64L * 1024 * 1024)
    {
        if (capacity is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (maximumBytes is < 1 or > 3221225472L) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        this.capacity = capacity;
        this.maximumBytes = maximumBytes;
        rows = new(capacity);
        writer = new Thread(() =>
        {
            try
            {
                using var output = open?.Invoke() ?? new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                ready.Set();
                foreach (byte[] row in rows.GetConsumingEnumerable())
                {
                    var timer = Stopwatch.StartNew();
                    output.Write(row); output.Flush();
                    maximumWriteMilliseconds = Math.Max(maximumWriteMilliseconds, timer.Elapsed.TotalMilliseconds);
                    Interlocked.Add(ref persistedBytes, row.Length); Interlocked.Increment(ref written);
                }
            }
            catch (Exception error) { Interlocked.CompareExchange(ref failure, error.Message, null); }
            finally { ready.Set(); }
        }) { IsBackground = true, Name = "bounded-calibration-trace-writer" };
        writer.Start();
        if (!ready.Wait(5000) || Failure != null)
        {
            Complete();
            if (!writer.Join(3000)) throw new IOException("Failed trace writer initialization did not stop within three seconds.");
            rows.Dispose(); ready.Dispose();
            throw new IOException(Failure ?? "Trace writer initialization timed out.");
        }
    }

    internal void Append(string row)
    {
        if (Failure != null || Volatile.Read(ref completed) != 0) throw new IOException(Failure ?? "Trace writer is closed.");
        byte[] data = Encoding.UTF8.GetBytes(row + Environment.NewLine);
        if (data.Length > MaximumRowBytes || Interlocked.Read(ref bytes) + data.Length > maximumBytes)
            throw Fail("Calibration trace exceeded its admitted row/total byte limit.");
        if (!rows.TryAdd(data)) throw Fail("Calibration trace writer queue is full; no observation row may be dropped.");
        Interlocked.Add(ref bytes, data.Length); Interlocked.Increment(ref enqueued);
        maximumPending = Math.Max(maximumPending, rows.Count);
        if (Failure != null) throw new IOException(Failure);
    }
    IOException Fail(string message)
    {
        Interlocked.CompareExchange(ref failure, message, null);
        return new IOException(Failure);
    }
    void Complete() { if (Interlocked.Exchange(ref completed, 1) == 0) rows.CompleteAdding(); }
    internal void CompleteAndCheck()
    {
        Complete();
        if (!writer.Join(3000)) throw Fail("Calibration trace writer did not drain within three seconds.");
        if (Failure != null || Interlocked.Read(ref enqueued) != Interlocked.Read(ref written)
            || Interlocked.Read(ref bytes) != Interlocked.Read(ref persistedBytes))
        {
            var error = Fail("Calibration trace persistence is incomplete.");
            FreezeReceipt();
            throw error;
        }
        FreezeReceipt();
    }
    public void Dispose()
    {
        if (Volatile.Read(ref disposed) != 0) return;
        try { CompleteAndCheck(); }
        finally
        {
            if (!writer.IsAlive && Interlocked.Exchange(ref disposed, 1) == 0) { rows.Dispose(); ready.Dispose(); }
        }
    }
}
