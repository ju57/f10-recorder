using System.Runtime.CompilerServices;
using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;

namespace F10Recorder;

internal sealed record ReplayTransportPicture(long PacketOffset, long Pts, bool Idr, int Slices,
    string CodedSha256 = null, string PacketSha256 = null);
internal sealed record ReplayTransportAudio(long PacketOffset, long Pts, int Samples);
internal sealed record ReplayTransportAudioFrame(long Pts, int Bytes, string PayloadSha256);
internal sealed record ReplayTransportTiming(long Offset, long Value, bool Pcr);
internal sealed record ReplayTransportIndex(long Bytes, string Sha256,
    IReadOnlyList<ReplayTransportPicture> Pictures, IReadOnlyList<ReplayTransportAudio> Audio,
    IReadOnlyList<ReplayTransportTiming> Timing, ReadOnlyMemory<byte> Sps, ReadOnlyMemory<byte> Pps,
    IReadOnlyList<ReplayTransportAudioFrame> AudioFrames = null);

// This never certifies decoded pictures/samples or permits publication. VCL
// hashes bind coded source bytes for the proposed closed-output integrity gate;
// they do not prove the source bitstream can decode. The normal publication
// gate compares the final output with source proof; qualification fully decodes.
// One protected read computes SHA and indexes TS/PES/H264/AAC together. The
// caller owns the immutable source lease, storage checks and export lifetime.
internal static class ReplayTransportReader
{
    internal const long TimestampModulus = 1L << 33;
    const int PacketBytes = 188, MaximumRecords = 65536;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static ReplayTransportIndex Read(Stream source, long expectedBytes, int fps,
        bool audio, Action checkStorage = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead || expectedBytes <= 0 || expectedBytes % PacketBytes != 0 || expectedBytes > 536870912)
            throw new IOException("Replay transport input is not a bounded complete TS chunk.");
        if (fps <= 0 || 90000 % fps != 0) throw new NotSupportedException("Replay transport frame clock is unsupported.");
        using var scanner = new Scanner(fps, audio, checkStorage);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(PacketBytes * 1024);
        long bytes = 0, packetOffset = 0, checkedAt = Stopwatch.GetTimestamp();
        int pending = 0;
        try
        {
            checkStorage?.Invoke();
            while (true)
            {
                int read = source.Read(buffer, pending, buffer.Length - pending);
                if (read == 0) break;
                hash.AppendData(buffer, pending, read);
                bytes = checked(bytes + read);
                if (bytes > expectedBytes) throw new IOException("Protected replay transport input grew.");
                int count = pending + read, complete = count / PacketBytes * PacketBytes;
                for (int offset = 0; offset < complete; offset += PacketBytes)
                {
                    scanner.Packet(buffer.AsSpan(offset, PacketBytes), packetOffset);
                    packetOffset += PacketBytes;
                    if (Stopwatch.GetElapsedTime(checkedAt).TotalMilliseconds >= 100)
                    {
                        checkStorage?.Invoke(); checkedAt = Stopwatch.GetTimestamp();
                    }
                }
                pending = count - complete;
                if (pending != 0) Buffer.BlockCopy(buffer, complete, buffer, 0, pending);
            }
            if (bytes != expectedBytes || pending != 0) throw new IOException("Protected replay transport input is truncated.");
            scanner.Complete(); checkStorage?.Invoke();
            return new(bytes, Convert.ToHexString(hash.GetHashAndReset()), scanner.Pictures.ToArray(),
                scanner.Audio.ToArray(), scanner.Timing.ToArray(), scanner.Sps, scanner.Pps, scanner.AudioFrames.ToArray());
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    static long Pts(ReadOnlySpan<byte> value)
    {
        if (value.Length < 5 || (value[0] & 1) == 0 || (value[2] & 1) == 0 || (value[4] & 1) == 0)
            throw new IOException("Replay PES timestamp markers are invalid.");
        return ((long)(value[0] >> 1 & 7) << 30) | ((long)value[1] << 22)
            | ((long)(value[2] >> 1) << 15) | ((long)value[3] << 7) | (uint)(value[4] >> 1);
    }

    sealed class Scanner : IDisposable
    {
        readonly int step;
        readonly bool hasAudio;
        readonly Dictionary<int, int> counters = [];
        readonly NalScanner nals;
        readonly ReplayAvcPictureLayout layout = new();
        readonly AdtsScanner adts;
        bool pat, pmt, videoActive, audioActive;
        int program = -1;
        long videoOffset, videoPts, audioOffset, audioPts;
        int pcrs, audioPesBytes, audioPesExpected, videoPesBytes, videoPesExpected;
        internal readonly List<ReplayTransportPicture> Pictures = [];
        internal readonly List<ReplayTransportAudio> Audio = [];
        internal readonly List<ReplayTransportAudioFrame> AudioFrames = [];
        internal readonly List<ReplayTransportTiming> Timing = [];
        internal byte[] Sps, Pps;

        internal Scanner(int fps, bool audio, Action checkStorage)
        {
            step = 90000 / fps; hasAudio = audio;
            adts = new AdtsScanner((number, bytes, digest) =>
            {
                if (AudioFrames.Count >= MaximumRecords) throw new IOException("Replay AAC frame evidence exceeds its bound.");
                AudioFrames.Add(new((audioPts + number * 1920L) % TimestampModulus, bytes, digest));
            });
            nals = new NalScanner((type, bytes) =>
            {
                byte[] previous = type == 7 ? Sps : Pps;
                if (previous != null && !previous.AsSpan().SequenceEqual(bytes))
                    throw new NotSupportedException("Replay H264 parameter-set changes require the correctness-first exporter.");
                layout.Configuration(type, bytes);
                if (type == 7) Sps = bytes; else Pps = bytes;
            }, bytes => layout.Picture(bytes), checkStorage);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        internal void Packet(ReadOnlySpan<byte> packet, long offset)
        {
            if (packet[0] != 0x47 || (packet[1] & 0x80) != 0 || (packet[3] & 0xc0) != 0)
                throw new IOException("Replay TS synchronization, error or scrambling flags are invalid.");
            int pid = ((packet[1] & 31) << 8) | packet[2];
            if (pid is not (0 or 17 or 256 or 257 or 4096 or 8191))
                throw new NotSupportedException("Replay TS PID layout requires the correctness-first exporter.");
            int control = packet[3] >> 4 & 3;
            if (control == 0) throw new IOException("Replay TS payload control is invalid.");
            bool payload = (control & 1) != 0, adaptation = (control & 2) != 0;
            int start = 4; bool discontinuity = false;
            if (adaptation)
            {
                int length = packet[4]; start += length + 1;
                if (start > PacketBytes) throw new IOException("Replay TS adaptation field is truncated.");
                if (length != 0)
                {
                    discontinuity = (packet[5] & 0x80) != 0;
                    if ((packet[5] & 15) != 0) throw new NotSupportedException("Replay extended adaptation clocks require the existing exporter.");
                    if ((packet[5] & 0x10) != 0)
                    {
                        if (length < 7 || (packet[10] & 0x7e) != 0x7e || ((packet[10] & 1) << 8 | packet[11]) > 299)
                            throw new IOException("Replay PCR field is invalid.");
                        if (pid != 256) throw new NotSupportedException("Replay PCR outside the declared video clock PID is unsupported.");
                        long clock = ((long)packet[6] << 25) | ((long)packet[7] << 17)
                            | ((long)packet[8] << 9) | ((long)packet[9] << 1) | (uint)(packet[10] >> 7);
                        AddTiming(new(offset + 6, clock, true)); pcrs++;
                    }
                }
            }
            if (discontinuity) counters.Remove(pid);
            if (pid != 8191 && payload)
            {
                int counter = packet[3] & 15;
                if (counters.TryGetValue(pid, out int expected) && counter != expected)
                    throw new IOException("Replay TS continuity is broken.");
                counters[pid] = (counter + 1) & 15;
            }
            if (!payload || start == PacketBytes) return;
            bool begin = (packet[1] & 0x40) != 0;
            ReadOnlySpan<byte> data = packet[start..];
            if (pid is 0 or 4096)
            {
                if (begin) Psi(pid, data);
                return;
            }
            if (pid is not (256 or 257)) return;
            if (pid == 257 && !hasAudio) throw new IOException("Unexpected replay audio stream.");
            if (begin)
            {
                if (pid == 256) CompleteVideo(); else CompleteAudio();
                if (data.Length < 14 || data[0] != 0 || data[1] != 0 || data[2] != 1 || (data[6] & 0xc0) != 0x80
                    || (data[6] & 0x30) != 0 || data[3] != (pid == 256 ? 0xe0 : 0xc0))
                    throw new IOException("Replay PES header is invalid or split.");
                int flags = data[7] >> 6, headerBytes = 9 + data[8];
                if (flags is not (2 or 3) || headerBytes > data.Length || headerBytes < (flags == 3 ? 19 : 14))
                    throw new IOException("Replay PES timing header is incomplete.");
                if ((data[9] >> 4) != flags || flags == 3 && (data[14] >> 4) != 1)
                    throw new IOException("Replay PES timestamp prefix is invalid.");
                if ((data[7] & 63) != 0 || headerBytes != (flags == 3 ? 19 : 14))
                    throw new NotSupportedException("Replay extended PES headers require the existing exporter.");
                long pts = Pts(data[9..]); AddTiming(new(offset + start + 9, pts, false));
                if (flags == 3)
                {
                    long dts = Pts(data[14..]); AddTiming(new(offset + start + 14, dts, false));
                    if (dts != pts) throw new NotSupportedException("Replay B-picture timing requires the correctness-first exporter.");
                }
                if (pid == 256)
                {
                    videoActive = true; videoOffset = offset; videoPts = pts;
                    int pesLength = (data[4] << 8) | data[5];
                    if (pesLength != 0 && pesLength < headerBytes - 6) throw new IOException("Replay video PES length is invalid.");
                    videoPesExpected = pesLength == 0 ? -1 : pesLength - (headerBytes - 6);
                    videoPesBytes = 0;
                }
                else
                {
                    int pesLength = (data[4] << 8) | data[5];
                    if (pesLength == 0 || pesLength < headerBytes - 6) throw new IOException("Replay AAC PES length is invalid.");
                    audioActive = true; audioOffset = offset; audioPts = pts;
                    audioPesExpected = pesLength - (headerBytes - 6); audioPesBytes = 0;
                }
                data = data[headerBytes..];
            }
            if (pid == 256)
            {
                if (!videoActive) throw new IOException("Replay video begins inside a PES packet.");
                videoPesBytes = checked(videoPesBytes + data.Length);
                if (videoPesExpected >= 0 && videoPesBytes > videoPesExpected) throw new IOException("Replay video PES exceeds its declared payload.");
                nals.Feed(data);
            }
            else
            {
                if (!audioActive) throw new IOException("Replay audio begins inside a PES packet.");
                audioPesBytes = checked(audioPesBytes + data.Length);
                if (audioPesBytes > audioPesExpected) throw new IOException("Replay AAC PES exceeds its declared payload.");
                adts.Feed(data);
            }
        }

        void AddTiming(ReplayTransportTiming timing)
        {
            if (Timing.Count >= MaximumRecords) throw new IOException("Replay TS timing index exceeds its bound.");
            Timing.Add(timing);
        }

        void CompleteVideo()
        {
            if (!videoActive) return;
            if (videoPesExpected >= 0 && videoPesBytes != videoPesExpected) throw new IOException("Replay video PES is incomplete.");
            nals.Complete();
            if (Pictures.Count == 0 && (!nals.Idr || Sps == null || Pps == null))
                throw new IOException("Replay chunk does not start with an independent configured IDR.");
            if (Pictures.Count >= 14400) throw new IOException("Replay picture index exceeds its bound.");
            if (Pictures.Count != 0 && ((videoPts - Pictures[^1].Pts + TimestampModulus) % TimestampModulus) != step)
                throw new NotSupportedException("Replay picture clock requires the correctness-first exporter.");
            Pictures.Add(new(videoOffset, videoPts, nals.Idr, nals.Slices, nals.CodedSha256, nals.PacketSha256));
            nals.Reset(); videoActive = false;
        }

        public void Dispose() => nals.Dispose();

        void CompleteAudio()
        {
            if (!audioActive) return;
            if (audioPesBytes != audioPesExpected) throw new IOException("Replay AAC PES is incomplete.");
            int samples = adts.Complete();
            if (Audio.Count >= 14400) throw new IOException("Replay audio index exceeds its bound.");
            if (Audio.Count != 0)
            {
                long expected = (Audio[^1].Pts + Audio[^1].Samples * 90000L / 48000) % TimestampModulus;
                if (audioPts != expected) throw new IOException("Replay AAC sample clock has a gap or overlap.");
            }
            Audio.Add(new(audioOffset, audioPts, samples)); audioActive = false;
        }

        internal void Complete()
        {
            CompleteVideo(); CompleteAudio();
            if (!pat || !pmt || Pictures.Count == 0 || pcrs == 0 || hasAudio && Audio.Count == 0)
                throw new IOException("Replay TS is missing complete program, clock, video or audio information.");
        }

        void Psi(int pid, ReadOnlySpan<byte> payload)
        {
            int start = payload[0] + 1;
            if (start + 3 > payload.Length) throw new IOException("Replay PSI pointer is invalid.");
            var section = payload[start..]; int length = 3 + (((section[1] & 15) << 8) | section[2]);
            if (length > section.Length) throw new NotSupportedException("Multi-packet replay PSI requires the correctness-first exporter.");
            section = section[..length];
            if (section.Length < 12 || Crc(section) != 0) throw new IOException("Replay PSI CRC is invalid.");
            if ((section[1] & 0xf0) != 0xb0 || (section[5] & 0xc0) != 0xc0 || section[6] > section[7])
                throw new IOException("Replay PSI syntax/reserved/section fields are invalid.");
            if ((section[5] & 1) == 0 || section[6] != 0 || section[7] != 0)
                throw new NotSupportedException("Replay future or multisection program tables require the existing exporter.");
            if (pid == 0)
            {
                if (section[0] != 0 || section.Length != 16 || (section[8] == 0 && section[9] == 0)
                    || (((section[10] & 31) << 8) | section[11]) != 4096)
                    throw new NotSupportedException("Replay PAT program layout is unsupported.");
                if ((section[10] & 0xe0) != 0xe0) throw new IOException("Replay PAT reserved PID bits are invalid.");
                int identity = (section[8] << 8) | section[9];
                if (program >= 0 && program != identity) throw new NotSupportedException("Replay program identity changed.");
                program = identity;
                pat = true; return;
            }
            if (!pat || ((section[3] << 8) | section[4]) != program) throw new IOException("Replay PMT does not match its current PAT program.");
            if (section[0] != 2 || section.Length < 16 || (((section[8] & 31) << 8) | section[9]) != 256)
                throw new NotSupportedException("Replay PMT clock layout is unsupported.");
            if ((section[8] & 0xe0) != 0xe0 || (section[10] & 0xf0) != 0xf0) throw new IOException("Replay PMT reserved bits are invalid.");
            int position = 12 + (((section[10] & 15) << 8) | section[11]);
            bool video = false, audio = false;
            while (position < section.Length - 4)
            {
                if (position + 5 > section.Length - 4) throw new IOException("Replay PMT stream descriptor is truncated.");
                if ((section[position + 1] & 0xe0) != 0xe0 || (section[position + 3] & 0xf0) != 0xf0)
                    throw new IOException("Replay PMT stream reserved bits are invalid.");
                int identity = ((section[position + 1] & 31) << 8) | section[position + 2];
                if (section[position] == 27 && identity == 256 && !video) video = true;
                else if (section[position] == 15 && identity == 257 && !audio && hasAudio) audio = true;
                else throw new NotSupportedException("Replay PMT codec layout is unsupported.");
                position += 5 + (((section[position + 3] & 15) << 8) | section[position + 4]);
            }
            if (position != section.Length - 4 || !video || audio != hasAudio) throw new IOException("Replay PMT stream coverage is incomplete.");
            pmt = true;
        }

        static uint Crc(ReadOnlySpan<byte> bytes)
        {
            uint crc = uint.MaxValue;
            foreach (byte value in bytes)
            {
                crc ^= (uint)value << 24;
                for (int bit = 0; bit < 8; bit++) crc = (crc << 1) ^ ((crc & 0x80000000) != 0 ? 0x04c11db7u : 0);
            }
            return crc;
        }
    }

    sealed class NalScanner(Action<int, byte[]> configuration, Action<byte[]> picture, Action checkStorage) : IDisposable
    {
        int zeros, type = -1;
        bool prefix, aud;
        readonly List<byte> parameter = [];
        readonly List<byte> header = [];
        readonly IncrementalHash codedHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        readonly IncrementalHash packetHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        readonly byte[] codedBuffer = ArrayPool<byte>.Shared.Rent(65536);
        int codedPending;
        long hashCheckedAt = Stopwatch.GetTimestamp();
        internal string CodedSha256;
        internal string PacketSha256;
        long nalBytes;
        internal bool Idr;
        internal int Slices;
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        internal void Feed(ReadOnlySpan<byte> bytes)
        {
            int position = 0;
            while (position < bytes.Length)
            {
                if (type >= 0 && type is not (7 or 8 or 9) && (type is not (1 or 5) || header.Count >= 64) && zeros == 0)
                {
                    int next = bytes[position..].IndexOf((byte)0);
                    Hash(bytes.Slice(position, next < 0 ? bytes.Length - position : next));
                    if (next < 0) return;
                    position += next;
                }
                byte value = bytes[position++];
                if (value == 0) { zeros++; continue; }
                if (value == 1 && zeros >= 2) { FinishNal(); type = -1; prefix = true; zeros = 0; continue; }
                if (type < 0)
                {
                    if (!prefix || (value & 0x80) != 0 || (value & 31) == 0) throw new IOException("Replay H264 AnnexB header is invalid.");
                    type = value & 31; prefix = false;
                    if (type is 6 or 9 or 12 && (value & 0x60) != 0) throw new IOException("Replay AVC non-reference metadata has invalid reference flags.");
                    if (!aud && type != 9) throw new NotSupportedException("Replay AVC PES must begin with an access-unit delimiter.");
                    if (type == 9)
                    {
                        if (aud) throw new NotSupportedException("Replay AVC PES contains more than one access unit.");
                        aud = true;
                    }
                    if (type is 1 or 5)
                    {
                        if (Slices != 0) throw new NotSupportedException("Replay AVC multiple slices/pictures per PES require the existing exporter.");
                        bool idr = type == 5;
                        Idr = idr; Slices++;
                    }
                    else if (type is not (6 or 7 or 8 or 9 or 12)) throw new NotSupportedException("Replay AVC extension/end/partition NAL layout requires the existing exporter.");
                    if (Slices != 0 && type is 6 or 7 or 8) throw new NotSupportedException("Replay AVC metadata after a picture requires the existing exporter.");
                    if (type is 7 or 8) parameter.Add(value);
                    if (type is 1 or 5 or 9) header.Add(value);
                    HashByte(value);
                }
                else if (type is 7 or 8)
                {
                    if (parameter.Count + zeros + 1 > 65536) throw new IOException("Replay H264 configuration exceeds its bound.");
                    for (int n = 0; n < zeros; n++) parameter.Add(0);
                    parameter.Add(value);
                    HashZeros(zeros); HashByte(value);
                }
                else if (type is 1 or 5 or 9)
                {
                    for (int n = 0; n < zeros && header.Count < 64; n++) header.Add(0);
                    if (header.Count < 64) header.Add(value);
                    HashZeros(zeros); HashByte(value);
                }
                else { HashZeros(zeros); HashByte(value); }
                zeros = 0;
            }
        }
        void FinishNal()
        {
            if (type is 7 or 8) { configuration(type, parameter.ToArray()); parameter.Clear(); }
            if (type is 1 or 5)
            {
                picture(header.ToArray());
            }
            if (type >= 0)
            {
                FlushHash();
                byte[] digest = codedHash.GetHashAndReset();
                ReplayCodedPacketHash.Append(packetHash, nalBytes, digest); nalBytes = 0;
                if (type is 1 or 5) CodedSha256 = Convert.ToHexString(digest);
            }
            if (type == 9 && (header.Count != 2 || (header[1] & 31) != 16)) throw new IOException("Replay AVC access-unit delimiter is invalid.");
            header.Clear();
        }
        internal void Complete()
        {
            FinishNal();
            if (!aud || Slices != 1 || prefix) throw new IOException("Replay PES has no complete supported primary picture.");
            PacketSha256 = Convert.ToHexString(packetHash.GetHashAndReset());
        }
        // AnnexB start codes and trailing_zero_8bits are container framing, not
        // VCL bytes. Pending zeros are committed only when followed by a coded
        // nonzero byte. The final MP4 comparison must use this same canonical
        // trailing-zero rule; it may not hash only the parsed picture header.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void Hash(ReadOnlySpan<byte> bytes)
        {
            nalBytes = checked(nalBytes + bytes.Length);
            while (!bytes.IsEmpty)
            {
                int count = Math.Min(bytes.Length, codedBuffer.Length - codedPending);
                bytes[..count].CopyTo(codedBuffer.AsSpan(codedPending));
                codedPending += count; bytes = bytes[count..];
                if (codedPending == codedBuffer.Length) FlushHash();
            }
        }
        void HashByte(byte value)
        {
            nalBytes++;
            codedBuffer[codedPending++] = value;
            if (codedPending == codedBuffer.Length) FlushHash();
        }
        void HashZeros(int count)
        {
            nalBytes = checked(nalBytes + count);
            // A deferred run can span many TS packets. Do not commit a large
            // run inside one Packet call without reaching the caller's guard.
            if (count >= codedBuffer.Length) { checkStorage?.Invoke(); hashCheckedAt = Stopwatch.GetTimestamp(); }
            while (count > 0)
            {
                int take = Math.Min(count, codedBuffer.Length - codedPending);
                codedBuffer.AsSpan(codedPending, take).Clear();
                codedPending += take; count -= take;
                if (codedPending == codedBuffer.Length) FlushHash();
            }
        }
        void FlushHash()
        {
            if (codedPending == 0) return;
            codedHash.AppendData(codedBuffer, 0, codedPending); codedPending = 0;
            if (Stopwatch.GetElapsedTime(hashCheckedAt).TotalMilliseconds >= 100)
            { checkStorage?.Invoke(); hashCheckedAt = Stopwatch.GetTimestamp(); }
        }
        internal void Reset() { zeros = 0; type = -1; prefix = false; aud = false; Idr = false; Slices = 0; CodedSha256 = null; PacketSha256 = null; parameter.Clear(); header.Clear(); }
        public void Dispose() { codedHash.Dispose(); packetHash.Dispose(); ArrayPool<byte>.Shared.Return(codedBuffer); }
    }

    sealed class AdtsScanner(Action<int, int, string> frame)
    {
        readonly byte[] header = new byte[7];
        readonly byte[] coded = new byte[8192];
        int headerBytes, remaining, samples, codedBytes, crcBytes;
        internal void Feed(ReadOnlySpan<byte> data)
        {
            while (!data.IsEmpty)
            {
                if (remaining != 0)
                {
                    int count = Math.Min(remaining, data.Length), skip = Math.Min(crcBytes, count);
                    data.Slice(skip, count - skip).CopyTo(coded.AsSpan(codedBytes)); codedBytes += count - skip; crcBytes -= skip;
                    remaining -= count; data = data[count..];
                    if (remaining == 0)
                    {
                        if (codedBytes == 0) throw new IOException("Replay AAC frame has no coded payload.");
                        frame(samples / 1024 - 1, codedBytes, Convert.ToHexString(SHA256.HashData(coded.AsSpan(0, codedBytes))));
                        codedBytes = 0;
                    }
                    continue;
                }
                int copy = Math.Min(7 - headerBytes, data.Length);
                data[..copy].CopyTo(header.AsSpan(headerBytes)); headerBytes += copy; data = data[copy..];
                if (headerBytes != 7) continue;
                if (header[0] != 255 || (header[1] & 0xf6) != 0xf0 || ((header[2] >> 2) & 15) is 13 or 14 or 15)
                    throw new IOException("Replay ADTS synchronization, layer or sample-rate index is invalid.");
                if (header[2] >> 6 != 1 || ((header[2] >> 2) & 15) != 3
                    || (((header[2] & 1) << 2) | (header[3] >> 6)) != 2 || (header[6] & 3) != 0)
                    throw new NotSupportedException("Replay audio is not single-block AAC-LC48k stereo.");
                int length = ((header[3] & 3) << 11) | (header[4] << 3) | (header[5] >> 5);
                crcBytes = (header[1] & 1) != 0 ? 0 : 2;
                if (length <= 7 + crcBytes) throw new IOException("Replay ADTS length is invalid.");
                remaining = length - 7; headerBytes = 0; samples = checked(samples + 1024);
            }
        }
        internal int Complete()
        {
            if (headerBytes != 0 || remaining != 0 || samples == 0) throw new IOException("Replay AAC frame is incomplete across its PES boundary.");
            int result = samples; samples = 0; return result;
        }
    }
}
