using System.Buffers;

namespace F10Recorder;

// Scratch memory only: every native query still reads fresh directory data.
// Parsing always uses Size, even if the pool supplies a larger backing array.
internal sealed class ReplayDirectoryBuffer : IDisposable
{
    internal const int Size = 65536;
    readonly ArrayPool<byte> pool;
    byte[] buffer;
    internal ReplayDirectoryBuffer(ArrayPool<byte> pool = null)
    { this.pool = pool ?? ArrayPool<byte>.Shared; buffer = this.pool.Rent(Size); }
    internal byte[] Bytes => buffer ?? throw new ObjectDisposedException(nameof(ReplayDirectoryBuffer));
    internal void Prepare() => Bytes.AsSpan(0, Size).Clear();
    public void Dispose()
    {
        var returned = Interlocked.Exchange(ref buffer, null);
        if (returned != null) pool.Return(returned, clearArray: true);
    }
}
