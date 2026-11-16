using System.Buffers;

namespace F10Recorder;

internal static class ReplayDirectoryBufferChecks
{
    sealed class Pool : ArrayPool<byte>
    {
        internal readonly byte[] Data = new byte[ReplayDirectoryBuffer.Size + 4096];
        internal int Rents, Returns; internal bool Clear;
        public override byte[] Rent(int minimumLength)
        { if (minimumLength != ReplayDirectoryBuffer.Size) throw new IOException("Unexpected query size."); Rents++; Array.Fill(Data, (byte)0x5a); return Data; }
        public override void Return(byte[] array, bool clearArray = false)
        { if (!ReferenceEquals(array, Data)) throw new IOException("Foreign buffer returned."); Returns++; Clear = clearArray; if (clearArray) Array.Clear(array); }
    }
    internal static int Run()
    {
        int checks = 0;
        void Require(bool condition) { if (!condition) throw new IOException("Directory buffer control failed at " + checks); checks++; }
        var pool = new Pool(); var lease = new ReplayDirectoryBuffer(pool);
        Require(pool.Rents == 1 && lease.Bytes.Length > ReplayDirectoryBuffer.Size);
        lease.Prepare(); Require(lease.Bytes.AsSpan(0, ReplayDirectoryBuffer.Size).IndexOfAnyExcept((byte)0) < 0);
        Require(lease.Bytes[ReplayDirectoryBuffer.Size] == 0x5a); // Larger backing is outside query/parser bounds.
        lease.Bytes[0] = 255; lease.Prepare(); Require(lease.Bytes[0] == 0);
        lease.Bytes[0] = 255; lease.Dispose(); Require(pool.Returns == 1 && pool.Clear && pool.Data.All(b => b == 0));
        lease.Dispose(); Require(pool.Returns == 1);
        try { lease.Prepare(); throw new Exception("Disposed buffer remained usable."); }
        catch (ObjectDisposedException) { checks++; }
        try { using var failing = new ReplayDirectoryBuffer(pool); throw new IOException("Expected consumer error."); }
        catch (IOException error) when (error.Message == "Expected consumer error.") { Require(pool.Returns == 2 && pool.Clear); }
        Console.WriteLine(checks + " pure directory-buffer controls passed; no native enumeration, filesystem or capture.");
        return 0;
    }
}
