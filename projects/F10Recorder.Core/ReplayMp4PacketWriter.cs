using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

namespace F10Recorder;

internal enum ReplayMp4WriterState { Active, Completed, Poisoned }
internal sealed class ReplayMp4WriteTiming
{
    public double WriteMilliseconds { get; internal set; }
    public double FlushMilliseconds { get; internal set; }
    public double MetadataMilliseconds { get; internal set; }
    public double GuardMilliseconds { get; internal set; }
    public long WrittenBytes { get; internal set; }
    public int Writes { get; internal set; }
}
// Unwired packet-mux candidate for finite comparisons. No decoding or success
// decision occurs here. The caller supplies protected samples, retains their
// independent pre-mux proof and validates the entire closed output separately.
internal sealed class ReplayMp4PacketWriter
{
    const uint MediaScale = 48000, MovieScale = 720000;
    sealed record Sample(long Offset, int Bytes, uint Duration, bool Idr);
    readonly Stream output;
    readonly Action check;
    readonly ReplayMp4WriteTiming timing;
    readonly int fps, expectedPictures, reserve;
    readonly long firstTicks, audioPriming, logicalSamples, movieOffset, mediaOffset;
    readonly byte[] sps, pps;
    readonly ReplayExpectedAudio[] expectedAudio;
    readonly List<Sample> video = [], audio = [];
    readonly int width, height;
    // Completed means final metadata and Flush succeeded. The caller still
    // owns stream disposal and must attest closure before validating output.
    internal ReplayMp4WriterState State { get; private set; }
    bool Finished => State != ReplayMp4WriterState.Active;
    long checkedAt;
    void Check(bool force = false)
    {
        if (!force && Stopwatch.GetElapsedTime(checkedAt).TotalMilliseconds < 100) return;
        long at = timing == null ? 0 : Stopwatch.GetTimestamp();
        try { check?.Invoke(); }
        finally { if (timing != null) timing.GuardMilliseconds += Stopwatch.GetElapsedTime(at).TotalMilliseconds; }
        checkedAt = Stopwatch.GetTimestamp();
    }
    IOException Failure(string reason) { State = ReplayMp4WriterState.Poisoned; return new(reason); }
    internal void Abort() => State = ReplayMp4WriterState.Poisoned;

    internal ReplayMp4PacketWriter(Stream output, int fps, double firstVideoSeconds, ReadOnlySpan<byte> sps,
        ReadOnlySpan<byte> pps, int pictures, long audioPriming, long logicalSamples,
        IReadOnlyList<ReplayExpectedAudio> audio, Action check = null, ReplayMp4WriteTiming timing = null)
    {
        if (!output.CanWrite || !output.CanSeek || output.Position != 0 || output.Length != 0
            || fps is < 1 or > 120 || MediaScale % fps != 0 || MovieScale % fps != 0
            || pictures < fps || pictures > fps * 1800 || !double.IsFinite(firstVideoSeconds)
            || firstVideoSeconds < 0 || firstVideoSeconds > 1d / fps + .000022
            || audioPriming is < 0 or > 8192 || audio?.Count > 84400)
            throw new IOException("Invalid bounded packet-MP4 construction plan.");
        if (sps.Length is < 4 or > ushort.MaxValue || pps.Length is < 1 or > ushort.MaxValue)
            throw new IOException("Packet-MP4 configuration extent.");
        if (sps[1] is not (66 or 77 or 100)) throw new NotSupportedException("Packet-MP4 AVC profile requires a different configuration writer.");
        this.output = output; this.check = check; this.timing = timing; this.fps = fps; expectedPictures = pictures;
        this.sps = sps.ToArray(); this.pps = pps.ToArray();
        var layout = new ReplayAvcPictureLayout(); layout.Configuration(7, this.sps); layout.Configuration(8, this.pps);
        width = layout.Width; height = layout.Height;
        expectedAudio = audio?.Take(84401).ToArray(); this.audioPriming = audioPriming; this.logicalSamples = logicalSamples;
        if (expectedAudio == null ? audioPriming != 0 || logicalSamples != 0
            : expectedAudio.Length is < 1 or > 84400 || expectedAudio.Any(a => a == null || a.Duration is < 1 or > 1024)
                || expectedAudio.Take(expectedAudio.Length - 1).Any(a => a.Duration != 1024)
                || logicalSamples is < 48000 or > 86400000 || expectedAudio.Sum(a => a.Duration) - audioPriming != logicalSamples)
            throw new IOException("Packet-MP4 AAC sample plan is incomplete.");
        firstTicks = checked((long)Math.Round(firstVideoSeconds * MovieScale));
        reserve = pictures > fps * 300 ? 4 * 1048576 : 1048576;
        Write(Box("ftyp", Encoding.ASCII.GetBytes("isom"), U32(512), Encoding.ASCII.GetBytes("isomiso2avc1mp41")));
        movieOffset = output.Position;
        Write(U32((uint)reserve)); Write(Encoding.ASCII.GetBytes("free"));
        byte[] zeros = new byte[65536]; int left = reserve - 8;
        while (left > 0) { int bytes = Math.Min(left, zeros.Length); Write(zeros.AsSpan(0, bytes)); left -= bytes; }
        mediaOffset = output.Position;
        Write(U32(1)); Write(Encoding.ASCII.GetBytes("mdat")); Write(U64(0));
    }
    void Write(ReadOnlySpan<byte> bytes)
    {
        try
        {
        Check();
        for (int offset = 0; offset < bytes.Length;)
        {
            int count = Math.Min(65536, bytes.Length - offset); long at = timing == null ? 0 : Stopwatch.GetTimestamp();
            output.Write(bytes.Slice(offset, count)); offset += count;
            if (timing != null) { timing.WriteMilliseconds += Stopwatch.GetElapsedTime(at).TotalMilliseconds; timing.WrittenBytes += count; timing.Writes++; }
            Check();
        }
        if (output.Position > 64L * 1024 * 1024 * 1024) throw new IOException("Packet-MP4 output bound.");
        }
        catch { State = ReplayMp4WriterState.Poisoned; throw; }
    }
    internal void Video(ReadOnlySpan<byte> sample, bool idr)
    {
        try
        {
        if (Finished || video.Count >= expectedPictures || sample.Length is < 5 or > 16777216 || video.Count == 0 && !idr)
            throw Failure("Unexpected packet-MP4 picture extent/order.");
        video.Add(new(output.Position, sample.Length, MediaScale / (uint)fps, idr)); Write(sample);
        }
        catch { State = ReplayMp4WriterState.Poisoned; throw; }
    }
    internal void Audio(ReadOnlySpan<byte> sample)
    {
        try
        {
        if (Finished || expectedAudio == null || audio.Count >= expectedAudio.Length || sample.Length is < 1 or > 65536)
            throw Failure("Unexpected packet-MP4 AAC extent/count.");
        audio.Add(new(output.Position, sample.Length, (uint)expectedAudio[audio.Count].Duration, false)); Write(sample);
        }
        catch { State = ReplayMp4WriterState.Poisoned; throw; }
    }
    internal void Complete()
    {
        if (Finished || video.Count != expectedPictures || audio.Count != (expectedAudio?.Length ?? 0))
            throw Failure("Incomplete packet-MP4 enabled samples.");
        State = ReplayMp4WriterState.Poisoned; Check(true);
        long end = output.Position;
        long metadataAt = timing == null ? 0 : Stopwatch.GetTimestamp(); byte[] movie = Movie();
        if (timing != null) timing.MetadataMilliseconds += Stopwatch.GetElapsedTime(metadataAt).TotalMilliseconds;
        if (movie.Length > reserve - 8) throw new IOException("Packet-MP4 movie metadata exceeded its reservation.");
        output.Position = movieOffset; Write(movie);
        Write(U32(checked((uint)(reserve - movie.Length)))); Write(Encoding.ASCII.GetBytes("free"));
        output.Position = mediaOffset + 8; Write(U64(checked((ulong)(end - mediaOffset))));
        output.Position = end; long flushAt = timing == null ? 0 : Stopwatch.GetTimestamp(); output.Flush();
        if (timing != null) timing.FlushMilliseconds += Stopwatch.GetElapsedTime(flushAt).TotalMilliseconds;
        Check(true); State = ReplayMp4WriterState.Completed;
    }
    byte[] Movie()
    {
        uint videoPresentation = checked((uint)((long)video.Count * MovieScale / fps));
        uint videoDuration = checked((uint)(firstTicks + videoPresentation));
        uint audioDuration = checked((uint)(logicalSamples * (MovieScale / MediaScale)));
        byte[] header = new byte[100]; Clock(header, MovieScale, Math.Max(videoDuration, audioDuration));
        Put(header, 20, 65536); header[24] = 1; Matrix(header, 36); Put(header, 96, expectedAudio == null ? 2u : 3u);
        var tracks = new List<byte[]> { Track(true, videoDuration, videoPresentation) };
        if (expectedAudio != null) tracks.Add(Track(false, audioDuration, audioDuration));
        return Box("moov", [Box("mvhd", header), .. tracks]);
    }
    byte[] Track(bool isVideo, uint movieDuration, uint presentation)
    {
        Check(); var samples = isVideo ? video : audio;
        byte[] header = new byte[84]; header[3] = 3; Put(header, 12, isVideo ? 1u : 2u); Put(header, 20, movieDuration);
        if (!isVideo) header[36] = 1;
        Matrix(header, 40); Put(header, 76, isVideo ? (uint)width << 16 : 0); Put(header, 80, isVideo ? (uint)height << 16 : 0);
        byte[] media = new byte[24]; Clock(media, MediaScale, checked((uint)samples.Sum(s => (long)s.Duration))); media[20] = 0x55; media[21] = 0xc4;
        byte[] handler = new byte[25]; Encoding.ASCII.GetBytes(isVideo ? "vide" : "soun").CopyTo(handler, 8);
        byte[] entry = new byte[isVideo ? 78 : 28]; entry[7] = 1;
        byte[] configuration;
        if (isVideo)
        {
            BinaryPrimitives.WriteUInt16BigEndian(entry.AsSpan(24), checked((ushort)width));
            BinaryPrimitives.WriteUInt16BigEndian(entry.AsSpan(26), checked((ushort)height));
            Put(entry, 28, 0x00480000); Put(entry, 32, 0x00480000); entry[41] = 1; entry[75] = 24; entry[76] = 255; entry[77] = 255;
            configuration = Box("avcC", [new byte[] { 1, sps[1], sps[2], sps[3], 255, 225 }, U16((ushort)sps.Length), sps,
                new byte[] { 1 }, U16((ushort)pps.Length), pps,
                .. (sps[1] is 100 or 110 or 122 or 144 ? new[] { new byte[] { 253, 248, 248, 0 } } : Array.Empty<byte[]>())]);
        }
        else
        {
            entry[17] = 2; entry[19] = 16; Put(entry, 24, MediaScale << 16);
            configuration = Box("esds", new byte[4], Descriptor(3, U16(2), new byte[] { 0 },
                Descriptor(4, new byte[] { 0x40, 0x15, 0, 0, 0 }, U32(192000), U32(192000), Descriptor(5, new byte[] { 0x11, 0x90 })),
                Descriptor(6, new byte[] { 2 })));
        }
        var runs = new List<(uint Count, uint Duration)>();
        int position = 0;
        foreach (var sample in samples)
        {
            if ((position++ & 255) == 0) Check();
            if (runs.Count != 0 && runs[^1].Duration == sample.Duration) runs[^1] = (runs[^1].Count + 1, sample.Duration);
            else runs.Add((1, sample.Duration));
        }
        var tables = new List<byte[]> {
            Box("stsd", new byte[4], U32(1), Box(isVideo ? "avc1" : "mp4a", entry, configuration)),
            Box("stts", new byte[4], U32((uint)runs.Count), Join(runs.SelectMany(r => new[] { U32(r.Count), U32(r.Duration) }))),
            Box("stsc", new byte[4], U32(1), U32(1), U32(1), U32(1)),
            Box("stsz", new byte[4], U32(0), U32((uint)samples.Count), Join(samples.Select(s => U32((uint)s.Bytes)))),
            Box("co64", new byte[4], U32((uint)samples.Count), Join(samples.Select(s => U64((ulong)s.Offset))))
        };
        if (isVideo)
        {
            uint[] keys = samples.Select((s, n) => (s, n)).Where(p => p.s.Idr).Select(p => (uint)p.n + 1).ToArray();
            tables.Add(Box("stss", new byte[4], U32((uint)keys.Length), Join(keys.Select(U32))));
        }
        byte[] edits = isVideo && firstTicks != 0
            ? Box("elst", new byte[4], U32(2), U32((uint)firstTicks), U32(uint.MaxValue), U32(65536), U32(presentation), U32(0), U32(65536))
            : Box("elst", new byte[4], U32(1), U32(presentation), U32(isVideo ? 0u : (uint)audioPriming), U32(65536));
        return Box("trak", Box("tkhd", header), Box("edts", edits), Box("mdia", Box("mdhd", media), Box("hdlr", handler),
            Box("minf", Box(isVideo ? "vmhd" : "smhd", isVideo ? new byte[] { 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0 } : new byte[8]),
                Box("dinf", Box("dref", new byte[4], U32(1), Box("url ", new byte[] { 0, 0, 0, 1 }))), Box("stbl", tables.ToArray()))));
    }
    static void Clock(byte[] bytes, uint scale, uint duration) { Put(bytes, 12, scale); Put(bytes, 16, duration); }
    static void Matrix(byte[] bytes, int offset) { Put(bytes, offset, 65536); Put(bytes, offset + 16, 65536); Put(bytes, offset + 32, 0x40000000); }
    static void Put(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset), value);
    static byte[] U16(ushort value) { byte[] bytes = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(bytes, value); return bytes; }
    static byte[] U32(uint value) { byte[] bytes = new byte[4]; Put(bytes, 0, value); return bytes; }
    static byte[] U64(ulong value) { byte[] bytes = new byte[8]; BinaryPrimitives.WriteUInt64BigEndian(bytes, value); return bytes; }
    static byte[] Join(IEnumerable<byte[]> parts)
    { using var stream = new MemoryStream(); foreach (byte[] part in parts) stream.Write(part); return stream.ToArray(); }
    static byte[] Box(string kind, params byte[][] parts)
    { byte[] body = Join(parts); return Join([U32(checked((uint)body.Length + 8)), Encoding.ASCII.GetBytes(kind), body]); }
    static byte[] Descriptor(byte tag, params byte[][] parts)
    { byte[] body = Join(parts); if (body.Length >= 128) throw new IOException("Packet-MP4 descriptor bound."); return Join([new[] { tag, (byte)body.Length }, body]); }
}
