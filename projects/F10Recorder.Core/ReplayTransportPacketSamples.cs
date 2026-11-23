using System.Runtime.CompilerServices;
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;

namespace F10Recorder;

internal delegate void ReplayVideoSampleConsumer(ReplayTransportPicture picture, ReadOnlySpan<byte> payload);
internal delegate void ReplayVideoAnnexConsumer(ReplayTransportPicture picture, ReadOnlySpan<byte> annex, Action throttledCheck);
internal delegate void ReplayAudioSampleConsumer(ReplayTransportAudioFrame frame, ReadOnlySpan<byte> payload);
internal sealed class ReplayPacketExtractionTiming
{
    public double ReadMilliseconds { get; internal set; }
    public double HashMilliseconds { get; internal set; }
    public double PayloadLoopMilliseconds { get; internal set; }
    public double AvccMilliseconds { get; internal set; }
    public double VideoConsumerMilliseconds { get; internal set; }
    public double AudioConsumerMilliseconds { get; internal set; }
    public double GuardMilliseconds { get; internal set; }
    public long ReadBytes { get; internal set; }
    public long DirectAvccBytes { get; internal set; }
    public long IntermediateVideoBufferBytes { get; internal set; }
    public int Reads { get; internal set; }
}

internal sealed class ReplayPacketSampleWorkspace : IDisposable
{
    // One request, one serial reader at a time. Borrowed spans must be consumed
    // before their callback returns; never share/reenter this workspace.
    internal readonly MemoryStream Video = new(), Audio = new();
    byte[] coded;
    bool poisoned, disposed;
    internal long CapacityBytes => Video.Capacity + (long)Audio.Capacity + (coded?.Length ?? 0);
    internal int CodedCapacityBytes => coded?.Length ?? 0;
    internal void Begin()
    {
        if (poisoned || disposed) throw new IOException("Packet sample workspace cannot resume after failure or disposal.");
        Video.SetLength(0); Audio.SetLength(0);
    }
    internal void Abort() => poisoned = true;
    internal Span<byte> Coded(int bytes)
    {
        if (bytes is < 1 or > 16777216) throw new IOException("Packet sample workspace extent.");
        if (coded == null || coded.Length < bytes)
        {
            byte[] next = ArrayPool<byte>.Shared.Rent(bytes);
            if (coded != null) ArrayPool<byte>.Shared.Return(coded);
            coded = next;
        }
        return coded.AsSpan(0, bytes);
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; Video.Dispose(); Audio.Dispose();
        if (coded != null) { ArrayPool<byte>.Shared.Return(coded); coded = null; }
    }
}

// Experimental second pass over an already indexed, immutable TS chunk. This
// only extracts its bounded physical samples; every original byte is rehashed.
// A callback/write/hash failure poisons the caller's whole unpublished output.
internal static class ReplayTransportPacketSamples
{
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void Read(Stream source, ReplayTransportIndex index,
        ReplayVideoSampleConsumer video, ReplayAudioSampleConsumer audio, Action check = null, ReplayPacketSampleWorkspace workspace = null,
        ReplayPacketExtractionTiming timing = null, ReplayVideoAnnexConsumer annexVideo = null)
    {
        if (!source.CanRead || index.Bytes <= 0 || index.Bytes > 536870912 || index.Bytes % 188 != 0
            || index.Pictures.Count is < 1 or > 14400 || index.AudioFrames?.Count > 65536 || video != null && annexVideo != null)
            throw new IOException("Invalid indexed packet-sample input.");
        bool ownsWorkspace = workspace == null; workspace ??= new ReplayPacketSampleWorkspace();
        try
        {
        workspace.Begin();
        var videoPes = workspace.Video; var audioPes = workspace.Audio;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int pictures = 0, frames = 0; long videoOffset = -1, total = 0, offset = 0; bool audioActive = false;
        long checkedAt = 0;
        void Check(bool force = false)
        {
            if (force || Stopwatch.GetElapsedTime(checkedAt).TotalMilliseconds >= 100)
            {
                long at = timing == null ? 0 : Stopwatch.GetTimestamp();
                try { check?.Invoke(); }
                finally { if (timing != null) timing.GuardMilliseconds += Stopwatch.GetElapsedTime(at).TotalMilliseconds; }
                checkedAt = Stopwatch.GetTimestamp();
            }
        }
        void FinishVideo()
        {
            if (videoOffset < 0) return;
            if (pictures >= index.Pictures.Count || index.Pictures[pictures].PacketOffset != videoOffset)
                throw new IOException("Packet-sample picture association changed.");
            var expected = index.Pictures[pictures++];
            var annex = videoPes.GetBuffer().AsSpan(0, (int)videoPes.Length);
            if (annexVideo != null)
            {
                long convertAt = timing == null ? 0 : Stopwatch.GetTimestamp(); annexVideo(expected, annex, () => Check());
                if (timing != null) timing.AvccMilliseconds += Stopwatch.GetElapsedTime(convertAt).TotalMilliseconds;
            }
            else if (video != null)
            {
                long convertAt = timing == null ? 0 : Stopwatch.GetTimestamp();
                Span<(int Start, int Length)> extents = stackalloc (int, int)[64];
                int count = Layout(annex, extents, () => Check(), out int totalBytes);
                var converted = workspace.Coded(totalBytes); Copy(annex, extents[..count], converted);
                if (timing != null) timing.AvccMilliseconds += Stopwatch.GetElapsedTime(convertAt).TotalMilliseconds;
                // Borrowed only for this synchronous callback. Consumers must
                // copy explicitly if they need to retain any coded body.
                long consumeAt = timing == null ? 0 : Stopwatch.GetTimestamp(); video(expected, converted);
                if (timing != null) timing.VideoConsumerMilliseconds += Stopwatch.GetElapsedTime(consumeAt).TotalMilliseconds;
            }
            videoPes.SetLength(0);
        }
        void FinishAudio()
        {
            var bytes = audioPes.GetBuffer().AsSpan(0, (int)audioPes.Length); int position = 0;
            while (position < bytes.Length)
            {
                Check();
                if (bytes.Length - position < 7 || bytes[position] != 255 || (bytes[position + 1] & 0xf6) != 0xf0)
                    throw new IOException("Packet-sample ADTS framing changed.");
                int header = (bytes[position + 1] & 1) != 0 ? 7 : 9;
                int length = ((bytes[position + 3] & 3) << 11) | (bytes[position + 4] << 3) | (bytes[position + 5] >> 5);
                if (length <= header || length > bytes.Length - position || index.AudioFrames == null || frames >= index.AudioFrames.Count
                    || index.AudioFrames[frames].Bytes != length - header)
                    throw new IOException("Packet-sample AAC extent/count changed.");
                var expected = index.AudioFrames[frames++]; long consumeAt = timing == null ? 0 : Stopwatch.GetTimestamp();
                audio?.Invoke(expected, bytes.Slice(position + header, length - header));
                if (timing != null) timing.AudioConsumerMilliseconds += Stopwatch.GetElapsedTime(consumeAt).TotalMilliseconds;
                position += length;
            }
            audioPes.SetLength(0);
        }
        byte[] buffer = ArrayPool<byte>.Shared.Rent(188 * 1024); int pending = 0;
        try
        {
            Check(true);
            while (true)
            {
                long readAt = timing == null ? 0 : Stopwatch.GetTimestamp();
                int read = source.Read(buffer, pending, buffer.Length - pending);
                if (timing != null) { timing.ReadMilliseconds += Stopwatch.GetElapsedTime(readAt).TotalMilliseconds; timing.ReadBytes += read; timing.Reads++; }
                Check();
                if (read == 0) break;
                long hashAt = timing == null ? 0 : Stopwatch.GetTimestamp(); hash.AppendData(buffer, pending, read);
                if (timing != null) timing.HashMilliseconds += Stopwatch.GetElapsedTime(hashAt).TotalMilliseconds;
                total = checked(total + read);
                if (total > index.Bytes) throw new IOException("Packet-sample protected source grew.");
                int count = pending + read, complete = count / 188 * 188;
                long loopAt = timing == null ? 0 : Stopwatch.GetTimestamp();
                for (int at = 0; at < complete; at += 188, offset += 188)
                {
                    var packet = buffer.AsSpan(at, 188);
                    if (packet[0] != 0x47 || (packet[1] & 128) != 0 || (packet[3] & 192) != 0)
                        throw new IOException("Packet-sample TS header changed.");
                    int control = packet[3] >> 4 & 3, start = 4;
                    if (control == 0) throw new IOException("Packet-sample TS adaptation control.");
                    if ((control & 2) != 0) start += 1 + packet[4];
                    if (start > 188) throw new IOException("Packet-sample adaptation extent.");
                    if ((control & 1) == 0 || start == 188) continue;
                    int pid = ((packet[1] & 31) << 8) | packet[2];
                    if (pid is not (256 or 257)) continue;
                    var payload = packet[start..];
                    var target = pid == 256 ? videoPes : audioPes;
                    if ((packet[1] & 64) != 0)
                    {
                        if (pid == 256) { FinishVideo(); videoOffset = offset; } else { FinishAudio(); audioActive = true; }
                        if (payload.Length < 14 || payload[0] != 0 || payload[1] != 0 || payload[2] != 1)
                            throw new IOException("Packet-sample PES header changed.");
                        int header = 9 + payload[8];
                        if (header > payload.Length || header < 14) throw new IOException("Packet-sample PES header extent.");
                        payload = payload[header..];
                    }
                    else if (pid == 256 && videoOffset < 0) throw new IOException("Packet-sample starts inside a video PES.");
                    else if (pid == 257 && !audioActive) throw new IOException("Packet-sample starts inside an audio PES.");
                    if (target.Length + payload.Length > (pid == 256 ? 16777216 : 65536)) throw new IOException("Packet-sample PES exceeds its bound.");
                    target.Write(payload);
                }
                if (timing != null) timing.PayloadLoopMilliseconds += Stopwatch.GetElapsedTime(loopAt).TotalMilliseconds;
                pending = count - complete;
                if (pending != 0) Buffer.BlockCopy(buffer, complete, buffer, 0, pending);
                Check();
            }
            if (pending != 0 || total != index.Bytes) throw new IOException("Packet-sample protected source is truncated.");
            FinishVideo(); FinishAudio();
            if (pictures != index.Pictures.Count || frames != (index.AudioFrames?.Count ?? 0)
                || Convert.ToHexString(hash.GetHashAndReset()) != index.Sha256)
                throw new IOException("Packet-sample source counts or complete source hash changed.");
            Check(true);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        }
        catch { workspace.Abort(); throw; }
        finally { if (ownsWorkspace) workspace.Dispose(); }
    }
    internal static byte[] Avcc(ReadOnlySpan<byte> annex, Action check = null)
    {
        Span<(int Start, int Length)> extents = stackalloc (int, int)[64];
        int count = Layout(annex, extents, check, out int total);
        byte[] result = new byte[total]; Copy(annex, extents[..count], result); return result;
    }
    internal static int WriteAvcc(ReadOnlySpan<byte> annex, Span<byte> destination, Action check = null)
    {
        Span<(int Start, int Length)> extents = stackalloc (int, int)[64];
        int count = Layout(annex, extents, check, out int total);
        if (destination.Length < total) throw new IOException("Prepared AVCC destination is too small.");
        Copy(annex, extents[..count], destination[..total]); return total;
    }
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static int Layout(ReadOnlySpan<byte> annex, Span<(int Start, int Length)> extents, Action check, out int total)
    {
        if (annex.Length is < 4 or > 16777216) throw new IOException("Bounded AVC access unit required.");
        ReadOnlySpan<byte> prefix = [0, 0, 1];
        int first = annex.IndexOf(prefix);
        if (first < 0 || first + 3 >= annex.Length || annex[..first].IndexOfAnyExcept((byte)0) >= 0) throw new IOException("AVC access unit has no leading AnnexB delimiter.");
        int count = 0, start = first + 3; total = 0;
        while (start < annex.Length)
        {
            check?.Invoke(); int next = annex[start..].IndexOf(prefix), end = next < 0 ? annex.Length : start + next;
            while (end > start && annex[end - 1] == 0) { end--; if ((end & 65535) == 0) check?.Invoke(); }
            if (end <= start || count >= extents.Length) throw new IOException("AVC NAL extent/count bound.");
            extents[count++] = (start, end - start); total = checked(total + 4 + end - start);
            if (next < 0) break;
            start += next + 3;
            if (start == annex.Length) throw new IOException("AVC access unit ends inside a delimiter.");
        }
        if (total > 16777216) throw new IOException("AVCC sample exceeds its bound.");
        return count;
    }
    static void Copy(ReadOnlySpan<byte> annex, ReadOnlySpan<(int Start, int Length)> extents, Span<byte> result)
    {
        int position = 0;
        foreach (var extent in extents)
        {
            BinaryPrimitives.WriteUInt32BigEndian(result[position..], (uint)extent.Length); position += 4;
            annex.Slice(extent.Start, extent.Length).CopyTo(result[position..]); position += extent.Length;
        }
    }
}
