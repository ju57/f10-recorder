using System.Buffers.Binary;
using System.Security.Cryptography;
using F10Recorder;

internal static class ReplayTransportPacketSampleChecks
{
    sealed class ShortReads(byte[] bytes) : MemoryStream(bytes)
    { public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, 7)); }
    static void Require(bool value, string why) { if (!value) throw new Exception(why); }
    static void Reject(Action action, string why)
    { try { action(); } catch (IOException) { return; } throw new Exception(why); }
    internal static void Run()
    {
        foreach (bool crc in new[] { false, true })
        foreach (int blocks in new[] { 1, 2 })
        {
            byte[] body = Enumerable.Repeat((byte)0xa7, 70000).Concat(new byte[] { 0x80, 0, 0, 0 }).ToArray();
            byte[] source = ReplayTransportChecks.Fixture(codedPayload: body, audioCrc: crc, audioBlocks: blocks);
            var index = ReplayTransportReader.Read(new MemoryStream(source), source.Length, 60, true);
            int pictures = 0, audio = 0;
            void Picture(ReplayTransportPicture expected, ReadOnlySpan<byte> avcc)
            {
                var nals = new List<byte[]>(); int position = 0;
                while (position < avcc.Length)
                {
                    int count = (int)BinaryPrimitives.ReadUInt32BigEndian(avcc[position..]); position += 4;
                    nals.Add(avcc.Slice(position, count).ToArray()); position += count;
                }
                Require(ReplayTransportChecks.PacketHashForTest(nals.ToArray()) == expected.PacketSha256,
                    "Extracted AVC lost a complete canonical NAL, metadata, deep body or order."); pictures++;
            }
            void Audio(ReplayTransportAudioFrame expected, ReadOnlySpan<byte> bytes)
            {
                Require(bytes.Length == expected.Bytes && Convert.ToHexString(SHA256.HashData(bytes)) == expected.PayloadSha256,
                    "Extracted AAC included framing/CRC or changed source payload."); audio++;
            }
            ReplayTransportPacketSamples.Read(new MemoryStream(source), index, Picture, Audio);
            ReplayTransportPacketSamples.Read(new MemoryStream(source), index, null, null);
            Require(pictures == index.Pictures.Count && audio == index.AudioFrames.Count, "Sample extractor lost an enabled picture/frame.");
            pictures = audio = 0;
            ReplayTransportPacketSamples.Read(new ShortReads(source), index, Picture, Audio);
            Require(pictures == index.Pictures.Count && audio == index.AudioFrames.Count, "Split reads changed extracted samples.");
            var damaged = source.ToArray(); damaged[^1] ^= 1;
            Reject(() => ReplayTransportPacketSamples.Read(new MemoryStream(damaged), index, null, null), "Extractor accepted changed original bytes outside emitted samples.");
            Reject(() => ReplayTransportPacketSamples.Read(new MemoryStream(source[..^188]), index, null, null), "Extractor accepted a truncated protected source.");
        }
        Reject(() => ReplayTransportPacketSamples.Avcc(new byte[] { 0, 0, 1 }), "Empty AnnexB delimiter was accepted.");
        Reject(() => ReplayTransportPacketSamples.Avcc(new byte[] { 0, 0, 0, 1 }), "Empty four-byte AnnexB delimiter was accepted.");
        Reject(() => ReplayTransportPacketSamples.Avcc(new byte[] { 3, 0, 0, 1, 0x65, 0x80 }), "Leading arbitrary AVC bytes were discarded.");
        byte[] repeatedSource = ReplayTransportChecks.Fixture(codedPayload: Enumerable.Repeat((byte)0xa7, 70000).Append((byte)0x80).ToArray());
        var repeatedIndex = ReplayTransportReader.Read(new MemoryStream(repeatedSource), repeatedSource.Length, 60, true);
        using (var workspace = new ReplayPacketSampleWorkspace())
        {
            void Consume()
            {
                ReplayTransportPacketSamples.Read(new MemoryStream(repeatedSource), repeatedIndex,
                    static (_, payload) => { if (payload.Length == 0) throw new Exception("Empty borrowed picture."); },
                    static (_, payload) => { if (payload.Length == 0) throw new Exception("Empty borrowed AAC."); }, workspace: workspace);
            }
            for (int n = 0; n < 20; n++) Consume();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int n = 0; n < 200; n++) Consume();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Require(allocated < 4_000_000 && workspace.CapacityBytes < 1_000_000,
                "Reusable extraction allocated per-chunk/per-picture bodies: " + allocated + " bytes.");
            Reject(() => ReplayTransportPacketSamples.Read(new MemoryStream(repeatedSource), repeatedIndex,
                static (_, _) => throw new IOException("Injected consumer failure."), null, workspace: workspace), "Consumer failure was ignored.");
            Reject(Consume, "Poisoned packet workspace resumed after a failed callback.");
        }
        using (var worker = new ReplayPacketPrepareWorker())
        {
            int ownerChecks = 0;
            var context = new ReplayPacketWork.Context(CancellationToken.None, 0, () => ownerChecks++, static _ => { });
            using var prepared = worker.Prepare(new(new MemoryStream(repeatedSource), repeatedIndex, true, true), context);
            Require(prepared.SourceHashComplete && prepared.SourceBytes == repeatedIndex.Bytes
                && prepared.Count == repeatedIndex.Pictures.Count + repeatedIndex.AudioFrames.Count,
                "Prepared packet path changed its complete source/count contract.");
            int preparedPictures = 0, preparedAudio = 0;
            for (int ordinal = 0; ordinal < prepared.Count; ordinal++)
            {
                var item = prepared.Event(ordinal); var payload = prepared.Payload(item);
                if (item.Picture != null)
                {
                    var nals = new List<byte[]>(); int position = 0;
                    while (position < payload.Length)
                    {
                        int bytes = (int)BinaryPrimitives.ReadUInt32BigEndian(payload[position..]); position += 4;
                        nals.Add(payload.Slice(position, bytes).ToArray()); position += bytes;
                    }
                    Require(position == payload.Length && ReplayTransportChecks.PacketHashForTest(nals.ToArray()) == item.Picture.PacketSha256,
                        "Direct prepared AVC conversion changed a coded packet.");
                    preparedPictures++;
                }
                else
                {
                    Require(item.Audio != null && payload.Length == item.Audio.Bytes
                        && Convert.ToHexString(SHA256.HashData(payload)) == item.Audio.PayloadSha256,
                        "Prepared AAC payload changed.");
                    preparedAudio++;
                }
            }
            Require(preparedPictures == repeatedIndex.Pictures.Count && preparedAudio == repeatedIndex.AudioFrames.Count,
                "Prepared packet payload verification lost a video or audio sample.");
            Require(worker.PeakIntermediateVideoBytes == 0,
                "Prepared packet path still converts AVC into an intermediate coded buffer before copying it into the owned result: "
                + worker.PeakIntermediateVideoBytes + " bytes.");
            Require(ownerChecks == 2,
                "Direct AVCC conversion bypassed or amplified the parser's two forced owner guards: " + ownerChecks + " callbacks.");
        }
        Console.WriteLine("Passed: experimental indexed TS sample extraction preserves all AVC/AAC digests, grouped frames and CRC removal; full-input changes/truncation reject. Non-playable fixtures only.");
    }
}
