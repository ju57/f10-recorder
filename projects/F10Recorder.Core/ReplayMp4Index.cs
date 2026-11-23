using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace F10Recorder;

internal sealed record ReplayMp4Sample(long Offset, int Bytes, long Dts, long Duration, bool Idr,
    string CodedSha256, string PayloadSha256, string PacketSha256);
internal sealed record ReplayMp4Track(string Kind, string Codec, uint Timescale, long MediaStart,
    double DelaySeconds, double PresentationSeconds, int Width, int Height, byte[] Configuration,
    byte[] Sps, byte[] Pps, IReadOnlyList<ReplayMp4Sample> Samples, uint Id, long MovieDuration,
    uint MovieTimescale, long PresentationTicks);
internal sealed record ReplayMp4Index(long Bytes, IReadOnlyList<ReplayMp4Track> Tracks);
internal sealed class ReplayMp4ReadTiming
{
    public double MetadataReadMilliseconds { get; internal set; }
    public double SampleReadMilliseconds { get; internal set; }
    public double NalHashMilliseconds { get; internal set; }
    public double SampleHashMilliseconds { get; internal set; }
    public double GuardMilliseconds { get; internal set; }
    public long MetadataBytes { get; internal set; }
    public long SampleBytes { get; internal set; }
}

// Closed-file evidence only. This is NOT a decoder or a publication decision.
// A normal-save gate must also compare ordered sample/configuration evidence
// with the frozen exporter/source expectation, under an immutable file lease.
internal static class ReplayMp4Reader
{
    const int MaximumMetadataBytes = 4 * 1024 * 1024, MaximumSampleBytes = 16 * 1024 * 1024;
    const int MaximumSamples = 216008;
    readonly record struct Box(string Kind, long Data, long End);

    internal static ReplayMp4Index Read(Stream stream, long expectedBytes, ReplayInterval interval,
        int fps, bool audio, Action check = null, ReplayMp4ReadTiming timing = null, bool includeVideoPayloadDigest = true, ReplayPacketReadWork packetWork = null)
    {
        if (!stream.CanRead || !stream.CanSeek || expectedBytes <= 0 || expectedBytes > 64L * 1024 * 1024 * 1024
            || stream.Length != expectedBytes || fps is < 1 or > 120 || interval == null
            || !double.IsFinite(interval.Seconds) || interval.Seconds < 1 - 1e-9 || interval.Seconds > 1800.000001)
            throw new IOException("Invalid bounded closed MP4 inspection.");
        if(packetWork!=null&&Math.Ceiling(interval.Seconds*fps)+1+(audio?Math.Ceiling((Math.Round(interval.Seconds*48000)+8192)/1024):0)>32768)packetWork=null;
        try { return new Reader(stream, expectedBytes, interval, fps, audio, check, timing, includeVideoPayloadDigest, packetWork).Read(); }
        catch (Exception ex) when (ex is OverflowException or ArgumentException or IndexOutOfRangeException)
        { throw new IOException("Invalid MP4 table or extent.", ex); }
    }

    sealed class Reader(Stream stream, long length, ReplayInterval interval, int fps, bool audio, Action check, ReplayMp4ReadTiming timing, bool includeVideoPayloadDigest, ReplayPacketReadWork packetWork)
    {
        sealed record Deferred(ReplayMp4Track Track,ReplayMp4Sample[] Samples,ReplayAvcPictureLayout Layout,byte[][] Headers);
        readonly List<Deferred> deferred=[];
        long checkedAt;
        void Guard(bool force = false)
        {
            if (force || Stopwatch.GetElapsedTime(checkedAt).TotalMilliseconds >= 100)
            {
                long at = timing == null ? 0 : Stopwatch.GetTimestamp();
                try { check?.Invoke(); }
                finally { if (timing != null) timing.GuardMilliseconds += Stopwatch.GetElapsedTime(at).TotalMilliseconds; }
                checkedAt = Stopwatch.GetTimestamp();
            }
        }
        byte[] Bytes(long at, int count)
        {
            Guard();
            if (at < 0 || count < 0 || count > MaximumMetadataBytes || at > length - count)
                throw new IOException("MP4 metadata read is outside its bound.");
            byte[] bytes = new byte[count]; long started = timing == null ? 0 : Stopwatch.GetTimestamp();
            stream.Position = at; stream.ReadExactly(bytes);
            if (timing != null) { timing.MetadataReadMilliseconds += Stopwatch.GetElapsedTime(started).TotalMilliseconds; timing.MetadataBytes += count; }
            return bytes;
        }
        byte[] Hash(ReadOnlySpan<byte> bytes, bool nal)
        {
            long at = timing == null ? 0 : Stopwatch.GetTimestamp();
            byte[] digest = SHA256.HashData(bytes);
            if (timing != null)
            {
                double ms = Stopwatch.GetElapsedTime(at).TotalMilliseconds;
                if (nal) timing.NalHashMilliseconds += ms; else timing.SampleHashMilliseconds += ms;
            }
            return digest;
        }
        static uint U32(ReadOnlySpan<byte> b, int n = 0) => BinaryPrimitives.ReadUInt32BigEndian(b[n..]);
        static ulong U64(ReadOnlySpan<byte> b, int n = 0) => BinaryPrimitives.ReadUInt64BigEndian(b[n..]);
        static ushort U16(ReadOnlySpan<byte> b, int n = 0) => BinaryPrimitives.ReadUInt16BigEndian(b[n..]);
        static string Text(ReadOnlySpan<byte> b) => Encoding.ASCII.GetString(b);
        static ReadOnlySpan<byte> Descriptor(ReadOnlySpan<byte> bytes, ref int at, int tag)
        {
            if (at >= bytes.Length || bytes[at++] != tag) throw new IOException("Missing MP4 audio descriptor.");
            int length = 0;
            for (int n = 0; n < 4; n++)
            {
                if (at >= bytes.Length) throw new IOException("Incomplete MP4 descriptor length.");
                byte value = bytes[at++]; length = checked((length << 7) | (value & 127));
                if ((value & 128) == 0)
                {
                    if (length > bytes.Length - at) throw new IOException("MP4 descriptor exceeds its parent.");
                    var result = bytes.Slice(at, length); at += length; return result;
                }
            }
            throw new IOException("MP4 descriptor length exceeds its bound.");
        }
        static void AudioConfiguration(byte[] configuration)
        {
            if (configuration.Length < 4 || U32(configuration) != 0) throw new IOException("Invalid MP4 audio descriptor header.");
            int at = 4; var es = Descriptor(configuration, ref at, 3);
            if (at != configuration.Length || es.Length < 3 || U16(es) == 0 || es[2] != 0) throw new IOException("Unsupported MP4 audio ES descriptor.");
            at = 3; var decoder = Descriptor(es, ref at, 4); var sl = Descriptor(es, ref at, 6);
            if (at != es.Length || sl.Length != 1 || sl[0] != 2 || decoder.Length < 13 || decoder[0] != 0x40 || decoder[1] != 0x15)
                throw new IOException("Unsupported MP4 AAC decoder descriptor.");
            at = 13; var asc = Descriptor(decoder, ref at, 5);
            if (at != decoder.Length || asc.Length is not (2 or 5) || asc[0] != 0x11 || asc[1] != 0x90
                || asc.Length == 5 && !asc[2..].SequenceEqual(new byte[] { 0x56, 0xe5, 0 }))
                throw new NotSupportedException("MP4 AAC must be 48kHz stereo LC with 1024-sample frames and no SBR.");
        }
        List<Box> Boxes(long start, long end)
        {
            var result = new List<Box>();
            while (start < end)
            {
                if (result.Count >= 256 || end - start < 8) throw new IOException("MP4 box header/count bound.");
                var raw = Bytes(start, 8); long size = U32(raw), header = 8;
                if (size == 1) { size = checked((long)U64(Bytes(start + 8, 8))); header = 16; }
                else if (size == 0) size = end - start;
                if (size < header || size > end - start) throw new IOException("MP4 box extends outside its closed parent.");
                result.Add(new(Text(raw.AsSpan(4, 4)), start + header, start + size)); start += size;
            }
            return result;
        }
        List<Box> Children(Box box) => Boxes(box.Data, box.End);
        static Box One(IEnumerable<Box> boxes, string kind)
        {
            var found = boxes.Where(b => b.Kind == kind).ToArray();
            if (found.Length != 1) throw new IOException("MP4 requires exactly one " + kind + ".");
            return found[0];
        }
        byte[] Payload(Box box) => Bytes(box.Data, checked((int)(box.End - box.Data)));
        uint[][] Rows(Box box, int width)
        {
            var raw = Payload(box);
            if (raw.Length < 8 || U32(raw) != 0) throw new IOException("Unsupported MP4 table header.");
            int count = checked((int)U32(raw, 4));
            if (count > MaximumSamples || raw.Length != 8L + 4L * width * count) throw new IOException("MP4 table count/extent.");
            var rows = new uint[count][];
            for (int n = 0; n < count; n++) { rows[n] = new uint[width]; for (int k = 0; k < width; k++) rows[n][k] = U32(raw, 8 + (n * width + k) * 4); }
            return rows;
        }
        static (uint Scale, long Duration) Clock(byte[] raw)
        {
            if (raw.Length < 20 || raw[0] is not (0 or 1) || (U32(raw) & 0xffffff) != 0) throw new IOException("Invalid MP4 media clock.");
            int at = raw[0] == 0 ? 12 : 20;
            uint scale = U32(raw, at);
            long duration = raw[0] == 0 ? U32(raw, at + 4) : checked((long)U64(raw, at + 4));
            if (scale == 0 || duration <= 0) throw new IOException("Empty MP4 media clock.");
            return (scale, duration);
        }
        static void IdentityMatrix(byte[] raw, int at)
        {
            for (int n = 0; n < 9; n++)
                if (U32(raw, at + n * 4) != (n is 0 or 4 ? 65536u : n == 8 ? 0x40000000u : 0u))
                    throw new NotSupportedException("MP4 presentation transforms require a different checker.");
        }
        (uint Id, long Duration) TrackHeader(List<Box> boxes, bool video, int width, int height, long expectedTicks)
        {
            byte[] raw = Payload(One(boxes, "tkhd"));
            if (raw.Length < 4 || raw[0] is not (0 or 1) || raw.Length != (raw[0] == 0 ? 84 : 96)) throw new IOException("Invalid MP4 track header.");
            uint flags = U32(raw) & 0xffffff;
            if ((flags & 3) != 3 || (flags & ~7u) != 0) throw new IOException("MP4 track is disabled or has unsupported presentation flags.");
            int shift = raw[0] == 0 ? 0 : 12;
            uint id = U32(raw, raw[0] == 0 ? 12 : 20);
            long duration = raw[0] == 0 ? U32(raw, 20) : checked((long)U64(raw, 28));
            if (id == 0 || duration <= 0 || duration != expectedTicks
                || U16(raw, 32 + shift) != 0 || U16(raw, 36 + shift) != (video ? 0 : 256)
                || U16(raw, 34 + shift) > (video ? 0 : 1)
                || U32(raw, 76 + shift) != (uint)width << 16 || U32(raw, 80 + shift) != (uint)height << 16)
                throw new IOException("MP4 track presentation disagrees with its media/edit evidence.");
            IdentityMatrix(raw, 40 + shift); return (id, duration);
        }
        (double Delay, long Start, double Seconds, long DelayTicks, long PresentationTicks) Edits(List<Box> track, uint movieScale, uint scale)
        {
            var editBoxes = track.Where(b => b.Kind == "edts").ToArray();
            if (editBoxes.Length != 1) throw new NotSupportedException("MP4 inspection requires explicit supported edit semantics.");
            var raw = Payload(One(Children(editBoxes[0]), "elst"));
            if (raw.Length < 8 || raw[0] is not (0 or 1) || (U32(raw) & 0xffffff) != 0) throw new IOException("Invalid MP4 edit header.");
            int count = checked((int)U32(raw, 4)), width = raw[0] == 0 ? 12 : 20;
            if (count is < 1 or > 2 || raw.Length != 8 + count * width) throw new IOException("Unsupported MP4 edit count/extent.");
            double delay = 0, seconds = 0; long start = -1, delayTicks = 0, presentationTicks = 0;
            for (int n = 0; n < count; n++)
            {
                int at = 8 + n * width;
                long duration = raw[0] == 0 ? U32(raw, at) : checked((long)U64(raw, at));
                long media = raw[0] == 0 ? unchecked((int)U32(raw, at + 4)) : unchecked((long)U64(raw, at + 8));
                if (U32(raw, at + width - 4) != 0x00010000 || duration <= 0) throw new IOException("Invalid MP4 edit rate/duration.");
                if (media == -1 && n == 0 && count == 2) { delay = duration / (double)movieScale; delayTicks = duration; }
                else if (media >= 0 && n == count - 1) { start = media; seconds = duration / (double)movieScale; presentationTicks = duration; }
                else throw new IOException("Unsupported MP4 empty/disjoint edits.");
            }
            if (start < 0 || delay > 1d / fps + 1d / scale || Math.Abs(seconds - interval.Seconds) > 1d / fps + 1d / scale)
                throw new IOException("MP4 edits disagree with the frozen replay interval.");
            return (delay, start, seconds, delayTicks, presentationTicks);
        }
        internal ReplayMp4Index Read()
        {
            Guard(true);
            var top = Boxes(0, length);
            if (top.Any(b => b.Kind is not ("ftyp" or "moov" or "mdat" or "free" or "wide")))
                throw new NotSupportedException("Unsupported MP4 top-level layout.");
            var brands = Payload(One(top, "ftyp"));
            if (brands.Length < 12 || brands.Length % 4 != 0 || Text(brands.AsSpan(0, 4)) is not ("isom" or "iso2" or "mp41" or "mp42"))
                throw new NotSupportedException("Unsupported MP4 file type.");
            var movie = One(top, "moov"); var data = One(top, "mdat");
            if (movie.End - movie.Data > MaximumMetadataBytes) throw new IOException("MP4 metadata bound.");
            var children = Children(movie); var movieHeader = Payload(One(children, "mvhd")); var clock = Clock(movieHeader);
            int movieShift = movieHeader[0] == 0 ? 0 : 12;
            if (movieHeader.Length != 100 + movieShift || U32(movieHeader, 20 + movieShift) != 65536 || U16(movieHeader, 24 + movieShift) != 256)
                throw new IOException("Invalid MP4 movie presentation header.");
            IdentityMatrix(movieHeader, 36 + movieShift);
            var tracks = children.Where(b => b.Kind == "trak").ToArray();
            if (tracks.Length != (audio ? 2 : 1)) throw new IOException("MP4 enabled-track count mismatch.");
            var result = tracks.Select(t => Track(t, clock.Scale, data)).ToArray();
            if (result.Count(t => t.Kind == "vide") != 1 || result.Count(t => t.Kind == "soun") != (audio ? 1 : 0))
                throw new IOException("MP4 enabled-track kind mismatch.");
            if (result.Select(t => t.Id).Distinct().Count() != tracks.Length || clock.Duration != result.Max(t => t.MovieDuration)
                || U32(movieHeader, movieHeader.Length - 4) <= result.Max(t => t.Id))
                throw new IOException("MP4 movie duration or track identities disagree with track evidence.");
            var extents = result.SelectMany(t => t.Samples).OrderBy(s => s.Offset).ToArray();
            if (extents[0].Offset != data.Data || extents[^1].Offset + extents[^1].Bytes != data.End)
                throw new IOException("MP4 media data contains unindexed leading/trailing bytes.");
            for (int n = 1; n < extents.Length; n++)
                if (extents[n].Offset != extents[n - 1].Offset + extents[n - 1].Bytes) throw new IOException("MP4 samples overlap or leave unindexed media bytes.");
            if(packetWork!=null)
            {
                var jobs=deferred.SelectMany((plan,track)=>plan.Samples.Select((sample,ordinal)=>new ReplayPacketReadSample(track,ordinal,sample,plan.Track.Kind=="vide",plan.Track.Sps,plan.Track.Pps))).ToArray();
                if(!ReplayPacketReadWork.Eligible(jobs))
                {
                    // No IO workers started. Drop deferred evidence before the
                    // conservative parse; its allocations remain in Save costs.
                    deferred.Clear();jobs=[];result=[];extents=[];
                    return new Reader(stream,length,interval,fps,audio,check,timing,includeVideoPayloadDigest,null).Read();
                }
                // Structural coverage above is checked BEFORE parallel reads.
                // Guard dispatch always calls the real owner callback; the
                // worker's callback is not replaced by a timer/cached result.
                void OwnerGuard(){long at=Stopwatch.GetTimestamp();try{check?.Invoke();}finally{if(timing!=null)timing.GuardMilliseconds+=Stopwatch.GetElapsedTime(at).TotalMilliseconds;}}
                packetWork.Run(jobs,row=>{var plan=deferred[row.Source.Track];plan.Samples[row.Source.Ordinal]=row.Sample;plan.Headers[row.Source.Ordinal]=row.PictureHeader;},OwnerGuard,includeVideoPayloadDigest);
                if(!packetWork.Joined)throw new IOException("Closed checked reads did not join.");
                foreach(var plan in deferred)if(plan.Layout!=null)for(int n=0;n<plan.Samples.Length;n++){Guard();plan.Layout.Picture(plan.Headers[n]??throw new IOException("Missing checked AVC header."));}
                if(timing!=null){timing.SampleBytes+=packetWork.ReadBytes;timing.SampleReadMilliseconds+=packetWork.ReadMilliseconds;timing.NalHashMilliseconds+=packetWork.NalHashMilliseconds;timing.SampleHashMilliseconds+=packetWork.SampleHashMilliseconds;}
            }
            Guard(true);
            if (stream.Length != length) throw new IOException("Closed MP4 extent changed during inspection.");
            return new(length, result);
        }
        ReplayMp4Track Track(Box track, uint movieScale, Box data)
        {
            var children = Children(track); var media = Children(One(children, "mdia"));
            var handler = Payload(One(media, "hdlr"));
            if (handler.Length < 24 || U32(handler) != 0 || U32(handler, 4) != 0) throw new IOException("Incomplete MP4 handler.");
            string kind = Text(handler.AsSpan(8, 4)); bool video = kind == "vide";
            if (!video && kind != "soun") throw new IOException("Unexpected MP4 track.");
            var mediaHeader = Payload(One(media, "mdhd"));
            if (mediaHeader.Length < 4 || mediaHeader.Length != (mediaHeader[0] == 0 ? 24 : 36)) throw new IOException("Incomplete MP4 media header.");
            var clock = Clock(mediaHeader); var edit = Edits(children, movieScale, clock.Scale);
            if (video && edit.Start != 0 || !video && (clock.Scale != 48000 || edit.Start is < 0 or > 8192 || edit.Delay > 1d / 48000))
                throw new IOException("MP4 edit hides replay pictures or has unsupported AAC priming.");
            var info = Children(One(media, "minf"));
            var headers = info.Where(box => box.Kind is "vmhd" or "smhd" or "hmhd" or "nmhd" or "sthd" or "gmhd").ToArray();
            if (headers.Length != 1 || headers[0].Kind != (video ? "vmhd" : "smhd")
                || headers[0].End - headers[0].Data != (video ? 12 : 8))
                throw new IOException("Unexpected MP4 media information header.");
            var fields = Payload(headers[0]);
            if (U32(fields) != (video ? 1u : 0u) || fields.AsSpan(4).ContainsAnyExcept((byte)0))
                throw new IOException("Unsupported MP4 media header flags, graphics mode, colour or sound balance.");
            var references = Payload(One(Children(One(info, "dinf")), "dref"));
            if (references.Length != 20 || U32(references) != 0 || U32(references, 4) != 1 || U32(references, 8) != 12
                || Text(references.AsSpan(12, 4)) != "url " || U32(references, 16) != 1)
                throw new IOException("MP4 samples must use one self-contained data reference.");
            var tables = Children(One(info, "stbl")); var descriptionBox = One(tables, "stsd"); var description = Payload(descriptionBox);
            if (description.Length < 24 || U32(description) != 0 || U32(description, 4) != 1 || U32(description, 8) != description.Length - 8
                || U16(description, 22) != 1) throw new IOException("Invalid MP4 sample description/reference.");
            string codec = Text(description.AsSpan(12, 4));
            if (codec != (video ? "avc1" : "mp4a")) throw new NotSupportedException("Unsupported MP4 codec.");
            int baseBytes = video ? 86 : 36;
            var configurationBox = One(Boxes(descriptionBox.Data + 8 + baseBytes, descriptionBox.End), video ? "avcC" : "esds");
            byte[] configuration = Payload(configurationBox), sps = null, pps = null;
            var layout = video ? new ReplayAvcPictureLayout() : null;
            if (video)
            {
                if (configuration.Length < 11 || configuration[0] != 1 || configuration[4] != 255 || configuration[5] != 225)
                    throw new IOException("Unsupported MP4 AVC configuration.");
                int count = U16(configuration, 6), at = 8;
                sps = configuration.AsSpan(at, count).ToArray(); at += count;
                if (configuration[at++] != 1) throw new IOException("MP4 requires one AVC PPS.");
                count = U16(configuration, at); at += 2; pps = configuration.AsSpan(at, count).ToArray(); at += count;
                if (at != configuration.Length && !(configuration.Length - at == 4 && configuration[at] == 253
                    && configuration[at + 1] == 248 && configuration[at + 2] == 248 && configuration[at + 3] == 0))
                    throw new IOException("Unsupported MP4 AVC configuration extension.");
                if (sps.Length < 4 || pps.Length == 0 || (sps[0] & 31) != 7 || (pps[0] & 31) != 8
                    || !configuration.AsSpan(1, 3).SequenceEqual(sps.AsSpan(1, 3)))
                    throw new IOException("Missing MP4 AVC configuration.");
                layout.Configuration(7, sps); layout.Configuration(8, pps);
            }
            else AudioConfiguration(configuration);
            int width = video ? U16(description, 40) : 0, height = video ? U16(description, 42) : 0;
            if (video && (width != layout.Width || height != layout.Height) || !video && (U16(description, 24) != 0 || U16(description, 32) != 2 || U32(description, 40) != 48000u << 16))
                throw new IOException("Unsupported MP4 video dimensions or AAC layout.");
            var header = TrackHeader(children, video, width, height, checked(edit.DelayTicks + edit.PresentationTicks));
            var sizeTable = Payload(One(tables, "stsz")); int fixedSize = checked((int)U32(sizeTable, 4)), samples = checked((int)U32(sizeTable, 8));
            if (samples < 1 || samples > MaximumSamples || U32(sizeTable) != 0 || sizeTable.Length != 12L + (fixedSize == 0 ? 4L * samples : 0)) throw new IOException("MP4 sample count/size extent.");
            if (video && (samples < fps || Math.Abs(samples - interval.Seconds * fps) > 1.000001)) throw new IOException("MP4 coded-picture count mismatch.");
            // Use the exporter's explicit nearest physical-sample endpoint.
            // Rounding only after division would invent an extra AAC packet
            // for a fractional request just beyond an exact packet boundary.
            long logicalSamples = checked((long)Math.Round(interval.Seconds * 48000));
            if (!video && samples != checked((logicalSamples + edit.Start + 1023) / 1024)) throw new IOException("MP4 AAC packet count disagrees with requested samples/priming.");
            var sizes = new int[samples]; for (int n = 0; n < samples; n++) sizes[n] = fixedSize == 0 ? checked((int)U32(sizeTable, 12 + n * 4)) : fixedSize;
            if (sizes.Any(n => n <= 0 || n > (video ? MaximumSampleBytes : 65536))) throw new IOException("MP4 sample size bound.");
            var durations = new long[samples]; int position = 0;
            foreach (var row in Rows(One(tables, "stts"), 2))
            {
                if (row[0] == 0 || row[1] == 0 || row[0] > samples - position) throw new IOException("MP4 time/sample count mismatch.");
                for (int n = 0; n < row[0]; n++) durations[position++] = row[1];
            }
            if (position != samples || durations.Sum() != clock.Duration) throw new IOException("MP4 duration table disagrees with media header.");
            var composition = tables.Where(b => b.Kind == "ctts").ToArray();
            var compositionRows = composition.Length == 1 ? Rows(composition[0], 2) : [];
            if (composition.Length > 1 || composition.Length == 1 && (compositionRows.Sum(r => (long)r[0]) != samples || compositionRows.Any(r => r[0] == 0 || r[1] != 0)))
                throw new NotSupportedException("MP4 reordered pictures require a different checker.");
            var small = tables.Where(b => b.Kind == "stco").ToArray(); var large = tables.Where(b => b.Kind == "co64").ToArray();
            if (small.Length + large.Length != 1) throw new IOException("MP4 requires exactly one chunk-offset table.");
            var offsetsRaw = Payload(small.Length == 1 ? small[0] : large[0]); int chunks = checked((int)U32(offsetsRaw, 4)), chunkWidth = small.Length == 1 ? 4 : 8;
            if (chunks < 1 || chunks > MaximumSamples || U32(offsetsRaw) != 0 || offsetsRaw.Length != 8L + chunks * (long)chunkWidth) throw new IOException("MP4 chunk-offset count/extent.");
            var mapping = Rows(One(tables, "stsc"), 3);
            if (mapping.Length == 0 || mapping[0][0] != 1) throw new IOException("MP4 first chunk mapping is missing.");
            for (int n = 0; n < mapping.Length; n++)
                if (mapping[n][0] > chunks || mapping[n][1] == 0 || mapping[n][2] != 1 || n > 0 && mapping[n][0] <= mapping[n - 1][0]) throw new IOException("MP4 chunk mapping is invalid.");
            var positions = new long[samples]; int index = 0; position = 0;
            for (int chunk = 1; chunk <= chunks; chunk++)
            {
                while (index + 1 < mapping.Length && mapping[index + 1][0] <= chunk) index++;
                long offset = small.Length == 1 ? U32(offsetsRaw, 8 + (chunk - 1) * 4) : checked((long)U64(offsetsRaw, 8 + (chunk - 1) * 8));
                for (int n = 0; n < mapping[index][1]; n++)
                {
                    if (position >= samples || offset < data.Data || offset > data.End - sizes[position]) throw new IOException("MP4 sample extends outside media data.");
                    positions[position] = offset; offset = checked(offset + sizes[position++]);
                }
            }
            if (position != samples) throw new IOException("Missing MP4 samples in chunk mapping.");
            var sync = tables.Where(b => b.Kind == "stss").ToArray(); var keys = new HashSet<int>();
            if (sync.Length > 1) throw new IOException("Duplicate MP4 sync table.");
            if (sync.Length == 1)
            {
                int previous = -1;
                foreach (var row in Rows(sync[0], 1))
                { int key = checked((int)row[0] - 1); if (key <= previous || key >= samples || !keys.Add(key)) throw new IOException("Invalid MP4 sync sample."); previous = key; }
            }
            if(packetWork!=null)
            {
                long stamp=0;var metadata=new ReplayMp4Sample[samples];
                for(int n=0;n<samples;n++)
                {
                    Guard();
                    if(video&&(Math.Abs(stamp/(double)clock.Scale-n/(double)fps)>.000022||Math.Abs(durations[n]/(double)clock.Scale-1d/fps)>.0011))throw new IOException("MP4 cumulative picture timing/cadence drift.");
                    if(!video&&(durations[n]>1024||n<samples-1&&durations[n]!=1024))throw new IOException("MP4 AAC sample clock is irregular.");
                    metadata[n]=new(positions[n],sizes[n],stamp,durations[n],video&&(sync.Length==0||keys.Contains(n)),null,null,null);stamp=checked(stamp+durations[n]);
                }
                double endpointTolerance=1d/clock.Scale+.000001;
                if(video&&(edit.Delay < -endpointTolerance||Math.Abs(edit.Delay+stamp/(double)clock.Scale-interval.Seconds)>1d/fps+endpointTolerance)
                    ||!video&&(Math.Abs(edit.Seconds-interval.Seconds)>endpointTolerance||Math.Abs((stamp-edit.Start)/(double)clock.Scale-interval.Seconds)>endpointTolerance))throw new IOException("MP4 presentation endpoints disagree with requested footage.");
                var trackResult=new ReplayMp4Track(kind,codec,clock.Scale,edit.Start,edit.Delay,edit.Seconds,width,height,configuration,sps,pps,Array.AsReadOnly(metadata),header.Id,header.Duration,movieScale,edit.PresentationTicks);
                deferred.Add(new(trackResult,metadata,layout,new byte[samples][]));return trackResult;
            }
            using var packetHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(sizes.Max()); var evidence = new List<ReplayMp4Sample>(samples); long dts = 0;
            try
            {
                for (int n = 0; n < samples; n++)
                {
                    Guard(); long readAt = timing == null ? 0 : Stopwatch.GetTimestamp();
                    stream.Position = positions[n]; stream.ReadExactly(buffer.AsSpan(0, sizes[n]));
                    if (timing != null) { timing.SampleReadMilliseconds += Stopwatch.GetElapsedTime(readAt).TotalMilliseconds; timing.SampleBytes += sizes[n]; }
                    var bytes = buffer.AsSpan(0, sizes[n]); bool idr = false; string hash, packet;
                    if (video)
                    {
                        int at = 0, pictures = 0, nals = 0, auds = 0; hash = null;
                        while (at < bytes.Length)
                        {
                            Guard();
                            if (++nals > 64) throw new NotSupportedException("MP4 AVC NAL count requires a different checker.");
                            if (bytes.Length - at < 5) throw new IOException("Incomplete MP4 AVC length/header.");
                            int size = checked((int)U32(bytes, at)); at += 4;
                            if (size <= 0 || size > bytes.Length - at) throw new IOException("MP4 AVC NAL extent.");
                            var nal = bytes.Slice(at, size); at += size; int type = nal[0] & 31;
                            if ((nal[0] & 128) != 0 || type is not (1 or 5 or 6 or 7 or 8 or 9 or 12)) throw new IOException("Unsupported MP4 AVC NAL.");
                            if (type is 6 or 9 or 12 && (nal[0] & 96) != 0) throw new IOException("MP4 AVC metadata has invalid reference flags.");
                            if (pictures != 0 && type is 6 or 7 or 8 or 9) throw new IOException("MP4 AVC metadata follows its coded picture.");
                            if (type == 9 && (++auds != 1 || nals != 1 || nal.Length != 2 || (nal[1] & 31) != 16)) throw new IOException("Invalid MP4 AVC access-unit delimiter.");
                            if (type is 1 or 5)
                            {
                                if (++pictures != 1) throw new IOException("MP4 sample contains more than one picture.");
                                layout.Picture(nal[..Math.Min(64, nal.Length)].ToArray()); idr = type == 5;
                            }
                            else if (type is 7 or 8 && !nal.SequenceEqual(type == 7 ? sps : pps)) throw new IOException("In-band MP4 AVC configuration changed.");
                            int trimmed = 0;
                            while (!nal.IsEmpty && nal[^1] == 0) { nal = nal[..^1]; if ((++trimmed & 65535) == 0) Guard(true); }
                            byte[] digest = Hash(nal, true);
                            ReplayCodedPacketHash.Append(packetHash, nal.Length, digest);
                            if (type is 1 or 5) hash = Convert.ToHexString(digest);
                        }
                        packet = Convert.ToHexString(packetHash.GetHashAndReset());
                        if (pictures != 1 || idr != (sync.Length == 0 || keys.Contains(n))) throw new IOException("MP4 sync flags disagree with physical AVC pictures.");
                        if (Math.Abs(dts / (double)clock.Scale - n / (double)fps) > .000022 || Math.Abs(durations[n] / (double)clock.Scale - 1d / fps) > .0011)
                            throw new IOException("MP4 cumulative picture timing/cadence drift.");
                    }
                    else
                    {
                        if (durations[n] > 1024 || n < samples - 1 && durations[n] != 1024) throw new IOException("MP4 AAC sample clock is irregular.");
                        hash = Convert.ToHexString(Hash(bytes, false));
                        packet = hash;
                    }
                    // Optional raw VIDEO sample digest is diagnostic only:
                    // normal proof compares the independently source-bound
                    // VCL and ordered all-NAL digests, configuration and clocks.
                    // Framing/extent checks still run; AAC raw digest is required.
                    evidence.Add(new(positions[n], sizes[n], dts, durations[n], idr, hash,
                        video ? includeVideoPayloadDigest ? Convert.ToHexString(Hash(bytes, false)) : null : hash, packet)); dts = checked(dts + durations[n]);
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
            double tolerance = 1d / clock.Scale + .000001;
            if (video && (edit.Delay < -tolerance || Math.Abs(edit.Delay + dts / (double)clock.Scale - interval.Seconds) > 1d / fps + tolerance)
                || !video && (Math.Abs(edit.Seconds - interval.Seconds) > tolerance || Math.Abs((dts - edit.Start) / (double)clock.Scale - interval.Seconds) > tolerance))
                throw new IOException("MP4 presentation endpoints disagree with requested footage.");
            return new(kind, codec, clock.Scale, edit.Start, edit.Delay, edit.Seconds, width, height, configuration, sps, pps, evidence.AsReadOnly(), header.Id, header.Duration,
                movieScale, edit.PresentationTicks);
        }
    }
}
