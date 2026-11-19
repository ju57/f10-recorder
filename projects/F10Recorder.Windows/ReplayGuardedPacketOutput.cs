using System.Diagnostics;

namespace F10Recorder;

// Packet readers borrow the Save owner's storage guard. Keep their owner
// responsive while Windows is extending, writing or flushing the main file.
internal sealed class ReplayGuardedPacketOutput(Stream destination, Action check) : Stream
{
    internal const int Capacity = 1024 * 1024;
    readonly byte[] buffer = new byte[Capacity];
    int used;
    Task pending;
    bool failed;
    internal bool Joined => pending == null || pending.IsCompleted;

    void Run(Func<CancellationToken, Task> operation)
    {
        if (failed) throw new IOException("Packet output has already failed.");
        using var cancellation = new CancellationTokenSource();
        long started = Stopwatch.GetTimestamp();
        // Even issuing an overlapped write can synchronously stall in the OS.
        var token = cancellation.Token;
        pending = Task.Run(() => operation(token));
        try
        {
            while (!pending.IsCompleted)
            {
                if (((IAsyncResult)pending).AsyncWaitHandle.WaitOne(25)) break;
                check();
                if (Stopwatch.GetElapsedTime(started).TotalSeconds >= 30)
                    throw new IOException("Packet output I/O deadline exceeded.");
            }
            pending.GetAwaiter().GetResult();
            pending = null;
        }
        catch
        {
            failed = true;
            cancellation.Cancel();
            // An unjoined write keeps the exact stream, buffer and native
            // creator owned. The mux must never close or publish that output.
            if (!pending.IsCompleted) ((IAsyncResult)pending).AsyncWaitHandle.WaitOne(2000);
            if (pending.IsFaulted) _ = pending.Exception;
            throw;
        }
    }
    void Drain()
    {
        if (used == 0) return;
        int count = used;
        Run(token => destination.WriteAsync(buffer.AsMemory(0, count), token).AsTask());
        used = 0;
    }
    public override void Write(ReadOnlySpan<byte> bytes)
    {
        while (!bytes.IsEmpty)
        {
            int count = Math.Min(bytes.Length, buffer.Length - used);
            bytes[..count].CopyTo(buffer.AsSpan(used));
            used += count;
            bytes = bytes[count..];
            if (used == buffer.Length) Drain();
        }
    }
    public override void Write(byte[] bytes, int offset, int count) => Write(bytes.AsSpan(offset, count));
    public override void Flush() { Drain(); Run(destination.FlushAsync); }
    public override long Position
    {
        get => checked(destination.Position + used);
        set { Drain(); destination.Position = value; }
    }
    public override long Length => Math.Max(destination.Length, Position);
    public override long Seek(long offset, SeekOrigin origin) { Drain(); return destination.Seek(offset, origin); }
    public override bool CanRead => false;
    public override bool CanWrite => destination.CanWrite;
    public override bool CanSeek => destination.CanSeek;
    public override int Read(byte[] bytes, int offset, int count) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
