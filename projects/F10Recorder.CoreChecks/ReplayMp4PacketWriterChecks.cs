using F10Recorder;
using System.Buffers.Binary;

internal static class ReplayMp4PacketWriterChecks
{
    // A legal extended-size free box places a small real MP4 body above4GiB.
    // No large allocation/file/IO is needed; reading the skipped free payload
    // is an error, so the real parser must use64-bit box/chunk offsets.
    sealed class LargeOffsetStream : Stream
    {
        internal const long Prefix = 5_000_000_000L;
        readonly MemoryStream body;
        readonly byte[] header = new byte[16];
        long position;
        internal LargeOffsetStream(byte[] bytes)
        {
            body=new(bytes,false);
            BinaryPrimitives.WriteUInt32BigEndian(header, 1);
            "free"u8.CopyTo(header.AsSpan(4));
            BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(8), Prefix);
        }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => Prefix + body.Length;
        public override long Position { get => position; set { if(value<0)throw new IOException("Negative offset.");position=value; } }
        public override int Read(byte[] bytes,int offset,int count) => Read(bytes.AsSpan(offset,count));
        public override int Read(Span<byte> bytes)
        {
            if(position<Prefix)
            {
                if(position>=header.Length)throw new IOException("Parser read skipped free payload.");
                int count=Math.Min(bytes.Length,header.Length-(int)position);
                header.AsSpan((int)position,count).CopyTo(bytes);position+=count;return count;
            }
            body.Position=position-Prefix;int read=body.Read(bytes);position+=read;return read;
        }
        public override void Write(byte[] bytes,int offset,int count) => Write(bytes.AsSpan(offset,count));
        public override void Write(ReadOnlySpan<byte> bytes) => throw new NotSupportedException();
        public override long Seek(long offset,SeekOrigin origin) => Position=checked(offset+(origin==SeekOrigin.Begin?0:origin==SeekOrigin.Current?position:Length));
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Flush() => body.Flush();
        protected override void Dispose(bool disposing) { if(disposing)body.Dispose();base.Dispose(disposing); }
    }
    sealed class FaultStream : MemoryStream
    {
        internal string Fault;
        public override long Position
        {
            get => Fault == "position" ? throw new IOException("Injected position failure.") : base.Position;
            set { if (Fault == "seek") throw new IOException("Injected seek failure."); base.Position = value; }
        }
        public override void Write(ReadOnlySpan<byte> bytes)
        { if (Fault == "write") throw new IOException("Injected write failure."); base.Write(bytes); }
        public override void Flush()
        { if (Fault == "flush") throw new IOException("Injected flush failure."); base.Flush(); }
    }
    static void Require(bool value, string why) { if (!value) throw new Exception(why); }
    static void Reject(Action action, string why)
    { try { action(); } catch (IOException) { return; } throw new Exception(why); }
    internal static void Run()
    {
        byte[] original = ReplayMp4Checks.PacketMuxFixture();
        var interval = new ReplayInterval(0, 1, 1);
        var source = ReplayMp4Reader.Read(new MemoryStream(original), original.Length, interval, 60, true);
        var video = source.Tracks.Single(t => t.Kind == "vide"); var audio = source.Tracks.Single(t => t.Kind == "soun");
        var expectedVideo = video.Samples.Select(s => new ReplayExpectedPicture(s.Idr, s.CodedSha256, s.PacketSha256)).ToArray();
        var expectedAudio = audio.Samples.Select(s => new ReplayExpectedAudio(s.Duration, s.PayloadSha256)).ToArray();
        {
            const int pictures=108000, logicalSamples=86400000, packets=84376;
            using var output=new MemoryStream();
            var audioPlan=Enumerable.Repeat(new ReplayExpectedAudio(1024,audio.Samples[0].PayloadSha256),packets).ToArray();
            var mux=new ReplayMp4PacketWriter(output,60,.0096208333333333,video.Sps,video.Pps,pictures,1024,logicalSamples,audioPlan);
            for(int n=0;n<pictures;n++)
            {
                var sample=video.Samples[n%video.Samples.Count];
                mux.Video(original.AsSpan((int)sample.Offset,sample.Bytes),sample.Idr);
            }
            var firstAudio=audio.Samples[0];
            for(int n=0;n<packets;n++)mux.Audio(original.AsSpan((int)firstAudio.Offset,firstAudio.Bytes));
            mux.Complete();
            byte[] bytes=output.ToArray();int tables=0;
            // Independently relocate every generated co64 entry behind a legal
            // virtual5GB free box. Native qualification tests a real large mdat.
            for(int at=0;at<bytes.Length-16;at++)
                if(bytes.AsSpan(at+4,4).SequenceEqual("co64"u8))
                {
                    int count=checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at+12)));
                    Require(count is pictures or packets,"Unexpected generated chunk table.");
                    Require(BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at))==16L+8L*count,"Generated co64 extent changed.");
                    for(int n=0;n<count;n++)
                    {
                        var cell=bytes.AsSpan(at+16+8*n,8);
                        BinaryPrimitives.WriteUInt64BigEndian(cell,checked(BinaryPrimitives.ReadUInt64BigEndian(cell)+(ulong)LargeOffsetStream.Prefix));
                    }
                    tables++;at+=16+8*count-1;
                }
            Require(tables==2,"Missing generated co64 tables.");
            using var relocated=new LargeOffsetStream(bytes);
            var observed=ReplayMp4Reader.Read(relocated,relocated.Length,new(0,1800,1800),60,true);
            var proof=new ReplayMp4Expectation(60,.0096208333333333,video.Sps,video.Pps,
                Enumerable.Range(0,pictures).Select(n=>expectedVideo[n%expectedVideo.Length]).ToArray(),1024,audioPlan);
            Require(proof.Verify(observed)==new ReplayIntegrityResult(pictures,packets),"Thirty-minute tables, clocks, complete coded proof or64-bit offsets changed.");
            Require(observed.Tracks.All(t=>t.Samples.All(s=>s.Offset>uint.MaxValue)),"Large co64 offsets were narrowed.");
            Require(mux.State==ReplayMp4WriterState.Completed,"Thirty-minute metadata exceeded the existing reservation.");
            Console.WriteLine("Passed: generated108000-picture/84376-AAC thirty-minute writer tables, complete reader/source proof and virtual5GB/co64 offsets; no large file or media decoding.");
        }
        foreach (bool enabledAudio in new[] { false, true })
        foreach (double phase in new[] { 0d, .0096208333333333 })
        {
            using var output = new MemoryStream();
            var mux = new ReplayMp4PacketWriter(output, 60, phase, video.Sps, video.Pps, 60,
                enabledAudio ? 1024 : 0, enabledAudio ? 48000 : 0, enabledAudio ? expectedAudio : null);
            foreach (var track in source.Tracks.Where(t => enabledAudio || t.Kind == "vide"))
                foreach (var sample in track.Samples)
                {
                    var bytes = original.AsSpan((int)sample.Offset, sample.Bytes);
                    if (track.Kind == "vide") mux.Video(bytes, sample.Idr); else mux.Audio(bytes);
                }
            mux.Complete(); long length = output.Length;
            Require(mux.State == ReplayMp4WriterState.Completed, "Completed packet mux did not attest finalized/flushed metadata.");
            var observed = ReplayMp4Reader.Read(output, length, interval, 60, enabledAudio);
            var proof = new ReplayMp4Expectation(60, phase, video.Sps, video.Pps, expectedVideo,
                enabledAudio ? 1024 : 0, enabledAudio ? expectedAudio : null);
            Require(proof.Verify(observed) == new ReplayIntegrityResult(60, enabledAudio ? 48 : 0), "Packet mux changed independently indexed samples, configuration, priming or clocks.");
            Reject(mux.Complete, "Repeated finalization was accepted.");
            Reject(() => mux.Video(original.AsSpan((int)video.Samples[0].Offset, video.Samples[0].Bytes), true), "Finalized packet mux accepted another picture.");
            Require(output.Length == length, "Rejected mutation changed a finalized file.");
        }
        using (var output = new MemoryStream())
        {
            var mux = new ReplayMp4PacketWriter(output, 60, 0, video.Sps, video.Pps, 60, 0, 0, null);
            Reject(mux.Complete, "Missing enabled samples were finalized.");
            Require(mux.State == ReplayMp4WriterState.Poisoned, "Failed packet mux looked successfully complete.");
            Reject(() => mux.Video(original.AsSpan((int)video.Samples[0].Offset, video.Samples[0].Bytes), true), "A failed packet mux was reused.");
        }
        foreach (string fault in new[] { "position", "write", "seek", "flush", "guard" })
        {
            using var output = new FaultStream(); bool failGuard = false;
            var mux = new ReplayMp4PacketWriter(output, 60, 0, video.Sps, video.Pps, 60, 0, 0, null,
                () => { if (failGuard) throw new IOException("Injected guard failure."); });
            if (fault is "position" or "write")
            {
                output.Fault = fault;
                Reject(() => mux.Video(original.AsSpan((int)video.Samples[0].Offset, video.Samples[0].Bytes), true), "Stream mutation fault was ignored: " + fault);
            }
            else
            {
                foreach (var sample in video.Samples) mux.Video(original.AsSpan((int)sample.Offset, sample.Bytes), sample.Idr);
                output.Fault = fault; failGuard = fault == "guard";
                Reject(mux.Complete, "Finalization fault was ignored: " + fault);
            }
            Require(mux.State == ReplayMp4WriterState.Poisoned, "A stream/guard failure was mistaken for success: " + fault);
            output.Fault = null; failGuard = false;
            Reject(mux.Complete, "A poisoned writer was resumed after infrastructure recovered: " + fault);
        }
        using (var output = new MemoryStream())
        {
            var malformed = expectedAudio.ToArray(); malformed[1] = malformed[1] with { Duration = 1023 }; malformed[^1] = malformed[^1] with { Duration = malformed[^1].Duration + 1 };
            Reject(() => new ReplayMp4PacketWriter(output, 60, 0, video.Sps, video.Pps, 60, 1024, 48000, malformed), "An irregular interior AAC duration was admitted before writing.");
            Require(output.Length == 0, "Invalid audio plan wrote an output header.");
            Reject(() => new ReplayMp4PacketWriter(output, 60, 0, new byte[65536], video.Pps, 60, 0, 0, null), "Unbounded codec configuration was admitted.");
            Require(output.Length == 0, "Invalid configuration wrote an output header.");
        }
        Console.WriteLine("Passed: experimental packet-MP4 writer preserves independently indexed coded samples/configuration/edits; missing samples and post-failure reuse reject. Non-playable fixtures only.");
    }
}
