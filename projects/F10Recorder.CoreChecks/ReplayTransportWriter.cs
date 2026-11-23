using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;

namespace F10Recorder;

internal enum ReplayTransportPackets { All, Program, Video, Audio }

// Builds an unpublished transport input from protected chunks. No picture or
// sample is decoded here. Every source byte is checked against the read index;
// only the clock fields and transport continuity counters can change.
// Any exception poisons this destination AND writer state: callers must abandon
// the entire unpublished assembly, including when the final source hash fails.
// A fallback must start a different clean output and its own producer lifetime.
internal sealed class ReplayTransportWriter(Stream destination)
{
    const int PacketBytes = 188;
    readonly Dictionary<int, int> counters = [];

    internal void Append(Stream source, ReplayTransportIndex index, long shift,
        ReplayTransportPackets selection, Action checkStorage = null, ReplaySaveTiming timing = null)
        => Append(source, index, shift, selection, checkStorage, null, timing);

    internal void AppendVideoPrefix(Stream source, ReplayTransportIndex index, long shift, int pictures, Action checkStorage = null, ReplaySaveTiming timing = null)
    {
        if (pictures <= 0 || pictures > index.Pictures.Count || !index.Pictures[0].Idr)
            throw new IOException("Replay video prefix is not a complete independent picture range.");
        long end = pictures == index.Pictures.Count ? index.Bytes : index.Pictures[pictures].PacketOffset;
        if (end <= index.Pictures[0].PacketOffset || end % PacketBytes != 0 || end > index.Bytes)
            throw new IOException("Replay video prefix is outside its indexed transport packets.");
        Append(source, index, shift, ReplayTransportPackets.Video, checkStorage, end, timing);
    }

    void Append(Stream source, ReplayTransportIndex index, long shift, ReplayTransportPackets selection, Action checkStorage, long? videoEnd, ReplaySaveTiming timing)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(index);
        if (!Enum.IsDefined(selection) || !source.CanRead || !destination.CanWrite)
            throw new ArgumentException("Invalid replay transport assembly stream or selection.");
        if (index.Bytes <= 0 || index.Bytes % PacketBytes != 0)
            throw new IOException("Invalid replay transport assembly index.");
        shift %= ReplayTransportReader.TimestampModulus;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(PacketBytes * 1024);
        long bytes = 0, packetOffset = 0, checkedAt = Stopwatch.GetTimestamp();
        int pending = 0, timingPosition = 0;
        void Check()
        {
            if (timing != null) { timing.WriterCheckStarted = Stopwatch.GetTimestamp(); timing.WriterCheckCompleted = null; }
            checkStorage?.Invoke();
            if (timing != null) timing.WriterCheckCompleted = Stopwatch.GetTimestamp();
        }
        void Write(ReadOnlySpan<byte> value)
        {
            long at = timing == null ? 0 : Stopwatch.GetTimestamp();
            try { destination.Write(value); }
            finally
            {
                if (timing != null)
                {
                    double elapsed = Stopwatch.GetElapsedTime(at).TotalMilliseconds;
                    timing.WriterWriteMilliseconds += elapsed;
                    timing.MaximumWriterWriteMilliseconds = Math.Max(timing.MaximumWriterWriteMilliseconds, elapsed);
                }
            }
        }
        try
        {
            Check();
            while (true)
            {
                long at = timing == null ? 0 : Stopwatch.GetTimestamp();
                int read = source.Read(buffer, pending, buffer.Length - pending);
                if (timing != null)
                {
                    double elapsed = Stopwatch.GetElapsedTime(at).TotalMilliseconds;
                    timing.WriterReadMilliseconds += elapsed;
                    timing.MaximumWriterReadMilliseconds = Math.Max(timing.MaximumWriterReadMilliseconds, elapsed);
                }
                if (read == 0) break;
                at = timing == null ? 0 : Stopwatch.GetTimestamp();
                hash.AppendData(buffer, pending, read);
                if (timing != null)
                {
                    double elapsed = Stopwatch.GetElapsedTime(at).TotalMilliseconds;
                    timing.WriterHashMilliseconds += elapsed;
                    timing.MaximumWriterHashMilliseconds = Math.Max(timing.MaximumWriterHashMilliseconds, elapsed);
                }
                bytes = checked(bytes + read);
                if (bytes > index.Bytes) throw new IOException("Protected replay assembly input grew.");
                int count = pending + read, complete = count / PacketBytes * PacketBytes;
                int writing = -1;
                for (int offset = 0; offset < complete; offset += PacketBytes)
                {
                    Span<byte> packet = buffer.AsSpan(offset, PacketBytes);
                    int pid = ((packet[1] & 31) << 8) | packet[2];
                    bool keep = selection switch
                    {
                        ReplayTransportPackets.All => true,
                        ReplayTransportPackets.Program => pid is not (256 or 257),
                        ReplayTransportPackets.Video => pid == 256 && (!videoEnd.HasValue || packetOffset < videoEnd.Value),
                        ReplayTransportPackets.Audio => pid == 257,
                        _ => false
                    };
                    while (timingPosition < index.Timing.Count && index.Timing[timingPosition].Offset < packetOffset + PacketBytes)
                    {
                        var clock = index.Timing[timingPosition++];
                        int relative = checked((int)(clock.Offset - packetOffset));
                        if (relative < 0 || relative + (clock.Pcr ? 6 : 5) > PacketBytes)
                            throw new IOException("Replay transport clock index is outside its packet.");
                        if (keep) Shift(packet[relative..], clock, shift);
                    }
                    if (keep)
                    {
                        if (writing < 0) writing = offset;
                        counters.TryGetValue(pid, out int counter);
                        bool payload = (packet[3] & 16) != 0;
                        // Adaptation-only packets repeat the preceding payload
                        // counter; they do not consume another counter value.
                        packet[3] = (byte)((packet[3] & 0xf0) | ((payload ? counter : counter - 1) & 15));
                        if (payload) counters[pid] = (counter + 1) & 15;
                    }
                    else if (writing >= 0)
                    {
                        Write(buffer.AsSpan(writing, offset - writing)); writing = -1;
                    }
                    packetOffset += PacketBytes;
                    if (Stopwatch.GetElapsedTime(checkedAt).TotalMilliseconds >= 100)
                    { Check(); checkedAt = Stopwatch.GetTimestamp(); }
                }
                // Interior chunks are almost entirely kept. One contiguous
                // write replaces thousands of188-byte stream calls, preserving
                // packet order and every byte already verified above.
                if (writing >= 0) Write(buffer.AsSpan(writing, complete - writing));
                pending = count - complete;
                if (pending != 0) Buffer.BlockCopy(buffer, complete, buffer, 0, pending);
            }
            if (bytes != index.Bytes || pending != 0 || timingPosition != index.Timing.Count
                || !Convert.ToHexString(hash.GetHashAndReset()).Equals(index.Sha256, StringComparison.Ordinal))
                throw new IOException("Protected replay assembly input differs from its indexed bytes.");
            Check();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    static void Shift(Span<byte> bytes, ReplayTransportTiming timing, long shift)
    {
        long value = (timing.Value + shift + ReplayTransportReader.TimestampModulus) % ReplayTransportReader.TimestampModulus;
        if (timing.Pcr)
        {
            bytes[0] = (byte)(value >> 25); bytes[1] = (byte)(value >> 17);
            bytes[2] = (byte)(value >> 9); bytes[3] = (byte)(value >> 1);
            bytes[4] = (byte)((bytes[4] & 127) | ((int)(value & 1) << 7));
        }
        else
        {
            bytes[0] = (byte)((bytes[0] & 0xf1) | ((int)(value >> 29) & 14)); bytes[1] = (byte)(value >> 22);
            bytes[2] = (byte)(((value >> 14) & 254) | 1); bytes[3] = (byte)(value >> 7);
            bytes[4] = (byte)(((value << 1) & 254) | 1);
        }
    }
}
