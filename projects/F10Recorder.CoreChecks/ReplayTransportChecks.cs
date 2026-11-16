using System.Security.Cryptography;
using System.Diagnostics;
using System.Text.Json;
using System.Buffers.Binary;
using F10Recorder;

internal static class ReplayTransportChecks
{
    internal static void RunFixtures(string planPath, string expectedPlanHash)
    {
        byte[] planBytes = File.ReadAllBytes(planPath);
        Require(planBytes.Length <= 65536 && Convert.ToHexString(SHA256.HashData(planBytes)).Equals(expectedPlanHash, StringComparison.OrdinalIgnoreCase), "Frozen file-only plan changed.");
        using var document = JsonDocument.Parse(planBytes); var plan = document.RootElement;
        Require(plan.GetProperty("kind").GetString() == "transport-index-file-only-v1", "Wrong file-only plan.");
        var limits = plan.GetProperty("bounds");
        Require(limits.GetProperty("seconds").GetDouble() is > 0 and <= 30 && limits.GetProperty("source_bytes").GetInt64() is > 0 and <= 32000000, "File-only bounds are invalid.");
        string root = Path.GetFullPath(plan.GetProperty("output").GetString());
        Require(!Directory.Exists(root) || !Directory.EnumerateFileSystemEntries(root).Any(), "Preserve previous file-only attempt.");
        Directory.CreateDirectory(root);
        foreach (var binding in plan.GetProperty("files").EnumerateObject())
            Require(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(binding.Name))).Equals(binding.Value.GetString(), StringComparison.OrdinalIgnoreCase), "Bound file changed: " + binding.Name);
        var sources = plan.GetProperty("sources").EnumerateArray().ToArray();
        Require(sources.Length is > 0 and <= 10, "File-only source count is invalid.");
        var clock = Stopwatch.StartNew(); var cpu = Process.GetCurrentProcess().TotalProcessorTime;
        long total = 0; var receipts = new List<object>();
        try
        {
            foreach (var entry in sources)
            {
                long length = entry.GetProperty("bytes").GetInt64(); total = checked(total + length);
                Require(length > 0 && total <= limits.GetProperty("source_bytes").GetInt64(), "File-only source-byte bound.");
                string path = entry.GetProperty("path").GetString();
                using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
                Require(source.Length == length, "Protected source size changed.");
                var result = ReplayTransportReader.Read(source, length, 60, true, () => Require(clock.Elapsed.TotalSeconds <= limits.GetProperty("seconds").GetDouble(), "File-only time bound."));
                Require(result.Sha256.Equals(entry.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase), "Protected source hash changed.");
                Require(result.Pictures.Count == entry.GetProperty("pictures").GetInt32()
                    && result.Pictures[0].Pts == entry.GetProperty("first_pts").GetInt64(), "Actual source picture metadata differs from the frozen expectation.");
                receipts.Add(new { path, result.Bytes, result.Sha256, pictures = result.Pictures.Count,
                    firstPts = result.Pictures[0].Pts, lastPts = result.Pictures[^1].Pts, idrs = result.Pictures.Count(p => p.Idr),
                    audioPes = result.Audio.Count, audioSamples = result.Audio.Sum(p => p.Samples),
                    firstAudioPts = result.Audio.Count == 0 ? (long?)null : result.Audio[0].Pts,
                    lastAudioEndTicks = result.Audio.Count == 0 ? (long?)null : checked(result.Audio[^1].Pts + result.Audio[^1].Samples * 90000L / 48000),
                    audioSpans = result.Audio.Take(128).Select(p => new { p.Pts, p.Samples,
                        durationTicks = checked(p.Samples * 90000L / 48000) }),
                    audioSpansOmitted = Math.Max(0, result.Audio.Count - 128),
                    sps = Convert.ToHexString(result.Sps.Span), pps = Convert.ToHexString(result.Pps.Span) });
            }
            File.WriteAllText(Path.Combine(root, "receipt.json"), JsonSerializer.Serialize(new { status = "passed", expectedPlanHash, sources = receipts,
                bytes = total, seconds = clock.Elapsed.TotalSeconds, cpuSeconds = (Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalSeconds,
                metadataOnly = true, noDecodingOrPublication = true }));
            Console.WriteLine("Passed: bounded retained-source metadata check; no decoder, capture or publication.");
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(root, "failure.json"), JsonSerializer.Serialize(new { status = "failed", expectedPlanHash, sources = receipts, error = ex.ToString(), seconds = clock.Elapsed.TotalSeconds }));
            throw;
        }
    }

    static void Require(bool value, string why) { if (!value) throw new Exception(why); }
    static void Reject<T>(Action action, string why) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception(why);
    }

    internal static void Run()
    {
        // These tiny synthetic bytes exercise container metadata, not media
        // decoding. Their dummy H264/AAC payloads are deliberately not a valid
        // playback fixture or a substitute for the full output validator.
        byte[] good = Fixture(); int checks = 0;
        ReplayTransportIndex Read(byte[] bytes, long? length = null) => ReplayTransportReader.Read(new MemoryStream(bytes), length ?? bytes.Length, 60, true, () => checks++);
        var result = Read(good);
        Require(result.Bytes == good.Length && result.Sha256 == Convert.ToHexString(SHA256.HashData(good)), "Transport hash/length must cover exactly the original bytes.");
        Require(result.Pictures.Count == 2 && result.Pictures[0].Idr && !result.Pictures[1].Idr
            && result.Pictures[0].Pts == 253500 && result.Pictures[1].Pts == 255000, "Actual picture clocks and IDRs must be indexed.");
        Require(result.Pictures[0].CodedSha256 == Convert.ToHexString(SHA256.HashData(new byte[] { 0x65, 0xb8, 0x60 }))
            && result.Pictures[1].CodedSha256 == Convert.ToHexString(SHA256.HashData(new byte[] { 0x41, 0xe3 })),
            "Source picture evidence must hash the complete coded VCL, independently of AnnexB framing.");
        Require(result.Pictures[0].PacketSha256 == PacketHashForTest(new byte[] { 9, 0xf0 }, result.Sps.ToArray(), result.Pps.ToArray(), new byte[] { 0x65, 0xb8, 0x60 }),
            "Source packet proof must bind every ordered NAL, including access-unit and codec metadata.");
        byte[] codedBody = Enumerable.Repeat((byte)0xa7, 70000).ToArray();
        new byte[] { 0, 0, 3, 2 }.CopyTo(codedBody, 65530); codedBody[^1] = 0x80;
        byte[] longFixture = Fixture(codedPayload: codedBody.Concat(new byte[3]).ToArray());
        var longIndex = Read(longFixture);
        string expectedCoded = Convert.ToHexString(SHA256.HashData(new byte[] { 0x65, 0xb8, 0x60 }.Concat(codedBody).ToArray()));
        Require(longIndex.Pictures[0].CodedSha256 == expectedCoded, "Picture evidence missed the deep body, emulation-prevention bytes, hash-buffer boundary or canonical trailing zeros.");
        using (var shortCoded = new ShortReads(longFixture))
            Require(ReplayTransportReader.Read(shortCoded, longFixture.Length, 60, true).Pictures.SequenceEqual(longIndex.Pictures),
                "Split source reads changed complete picture evidence.");
        codedBody[66000] ^= 1;
        Require(Read(Fixture(codedPayload: codedBody.Concat(new byte[3]).ToArray())).Pictures[0].CodedSha256 != expectedCoded,
            "Deep picture corruption outside the parsed header did not change source-byte evidence.");
        int deferredChecks = 0;
        byte[] zeroBody = new byte[70001]; zeroBody[^1] = 0x80;
        byte[] zeroFixture = Fixture(codedPayload: zeroBody);
        var zeroIndex = ReplayTransportReader.Read(new MemoryStream(zeroFixture), zeroFixture.Length, 60, true, () => deferredChecks++);
        Require(deferredChecks >= 4 && zeroIndex.Pictures[0].CodedSha256 == Convert.ToHexString(SHA256.HashData(new byte[] { 0x65, 0xb8, 0x60 }.Concat(zeroBody).ToArray())),
            "Committing a long deferred zero run must reach the storage/deadline guard and preserve its coded bytes.");
        int stoppedChecks = 0;
        Reject<OperationCanceledException>(() => ReplayTransportReader.Read(new MemoryStream(zeroFixture), zeroFixture.Length, 60, true,
            () => { if (++stoppedChecks == 2) throw new OperationCanceledException(); }),
            "Deferred picture hashing swallowed a guard failure.");
        Require(result.Audio.Count == 2 && result.Audio.All(x => x.Samples == 1024)
            && result.Audio[0].Pts == 251580 && result.Audio[1].Pts == 253500, "ADTS frames spanning TS packets must retain exact AAC coverage.");
        Require(result.AudioFrames.Count == 2 && result.AudioFrames[0].Pts == 251580 && result.AudioFrames[1].Pts == 253500
            && result.AudioFrames.All(f => f.Bytes == 193 && f.PayloadSha256 == Convert.ToHexString(SHA256.HashData(new byte[193]))),
            "AAC evidence must bind each complete raw coded frame, excluding transport/ADTS headers.");
        var crcAudio = Read(Fixture(audioCrc: true));
        Require(crcAudio.AudioFrames.SequenceEqual(result.AudioFrames), "AAC frame proof included ADTS CRC/header bytes in the raw coded payload.");
        var groupedAudio = Read(Fixture(audioBlocks: 2));
        Require(groupedAudio.AudioFrames.Select(f => f.Pts).SequenceEqual(new long[] { 251580, 253500, 255420, 257340 })
            && groupedAudio.Audio.All(p => p.Samples == 2048), "Grouped ADTS frames lost per-frame clock/order across PES boundaries.");
        Require(result.Sps.Span[..4].SequenceEqual(new byte[] { 0x67, 0x42, 0, 0x1f })
            && result.Pps.Span[0] == 0x68 && checks >= 2, "Configured independent chunks and cooperative checks are required.");
        using (var shortReads = new ShortReads(good))
        {
            var split = ReplayTransportReader.Read(shortReads, good.Length, 60, true);
            Require(split.Sha256 == result.Sha256 && split.Pictures.SequenceEqual(result.Pictures)
                && split.Audio.SequenceEqual(result.Audio) && split.AudioFrames.SequenceEqual(result.AudioFrames), "Arbitrary stream read boundaries cannot alter source indexing.");
        }
        Reject<IOException>(() => Read(good[..^1], good.Length), "Truncated final TS packet was accepted.");
        Reject<IOException>(() => Read(good.Concat(new byte[188]).ToArray(), good.Length), "Unexpected source growth was accepted.");
        var damaged = good.ToArray(); damaged[1] |= 0x80;
        Reject<IOException>(() => Read(damaged), "Transport error flag was accepted.");
        damaged = good.ToArray(); damaged[3] |= 0xc0;
        Reject<IOException>(() => Read(damaged), "Scrambled transport was accepted.");
        damaged = good.ToArray(); damaged[4] = 184;
        Reject<IOException>(() => Read(damaged), "Oversized adaptation field was accepted.");
        damaged = good.ToArray(); int psiStart = 5 + damaged[4]; damaged[psiStart + 16] ^= 1;
        Reject<IOException>(() => Read(damaged), "PAT CRC corruption was accepted.");
        damaged = good.ToArray(); int second = checked((int)result.Pictures[1].PacketOffset);
        damaged[second + 3] = (byte)((damaged[second + 3] & 0xf0) | 7);
        Reject<IOException>(() => Read(damaged), "A lost video transport packet was accepted.");
        damaged = good.ToArray(); int audioStart = PayloadStart(damaged, (int)result.Audio[0].PacketOffset) + 14;
        damaged[audioStart + 7 + 100] ^= 1;
        Require(Read(damaged).AudioFrames[0].PayloadSha256 != result.AudioFrames[0].PayloadSha256,
            "AAC coded-byte proof ignored deep payload changes.");
        damaged = good.ToArray();
        damaged[audioStart] = 254;
        Reject<IOException>(() => Read(damaged), "Corrupt ADTS sync was treated as a legal unsupported codec.");
        damaged = good.ToArray(); damaged[PayloadStart(damaged, (int)result.Pictures[0].PacketOffset) + 6] |= 0x10;
        Reject<IOException>(() => Read(damaged), "PES-level scrambling was accepted.");
        foreach (int length in new[] { 7, 8, 200 })
        {
            damaged = good.ToArray(); int pes = PayloadStart(damaged, (int)result.Pictures[0].PacketOffset);
            damaged[pes + 4] = 0; damaged[pes + 5] = (byte)length;
            Reject<IOException>(() => Read(damaged), "An inconsistent nonzero video PES length was accepted.");
        }
        Reject<IOException>(() => Read(EditPsi(good, 0, s => s[1] &= 127)), "CRC-valid invalid section syntax was accepted.");
        Reject<IOException>(() => Read(EditPsi(good, 0, s => s[10] &= 31)), "CRC-valid invalid PAT reserved bits were accepted.");
        Reject<IOException>(() => Read(EditPsi(good, 4096, s => s[4] = 2)), "PMT referencing a different PAT program was accepted.");
        Reject<NotSupportedException>(() => Read(EditPsi(good, 0, s => s[5] &= 254)), "A future PAT was silently treated as current.");
        Reject<IOException>(() => Read(Fixture(firstIdr: false)), "A dependent chunk head was accepted.");
        Reject<IOException>(() => Read(Fixture(audioFrameLength: 201)), "An incomplete AAC frame was accepted.");
        Reject<IOException>(() => Read(Fixture(audioGap: true)), "A source AAC gap was accepted.");
        Reject<NotSupportedException>(() => Read(Fixture(videoGap: true)), "Irregular video clock was silently normalized.");
        Reject<NotSupportedException>(() => Read(Fixture(changeSps: true)), "Changed H264 configuration was silently spliced.");
        foreach (string layout in new[] { "two-pictures", "two-auds", "multislice", "field", "extension", "missing-aud", "continued-slice" })
            Reject<NotSupportedException>(() => Read(Fixture(layout: layout)), "Unsupported AVC layout was treated as one physical picture: " + layout);
        Reject<IOException>(() => Read(Fixture(layout: "wrong-pps")), "Unavailable picture parameter set was accepted.");
        Reject<IOException>(() => Read(Fixture(layout: "idr-frame-number")), "Invalid IDR frame number was accepted.");
        Reject<IOException>(() => Read(Fixture(layout: "reserved-bipred")), "Reserved PPS biprediction value was accepted.");
        using var assembled = new MemoryStream();
        var writer = new ReplayTransportWriter(assembled);
        writer.Append(new ShortReads(good), result, 180000, ReplayTransportPackets.All);
        byte[] shifted = assembled.ToArray(); var shiftedIndex = Read(shifted);
        Require(shiftedIndex.Pictures.Select(p => p.CodedSha256).SequenceEqual(result.Pictures.Select(p => p.CodedSha256)),
            "Transport clock/continuity changes must preserve every source coded-picture identity.");
        Require(shiftedIndex.Pictures.Select(p => p.PacketSha256).SequenceEqual(result.Pictures.Select(p => p.PacketSha256)),
            "Transport clock changes altered complete coded packet proof.");
        Require(shiftedIndex.Pictures.Select(p => p.Pts).SequenceEqual(result.Pictures.Select(p => p.Pts + 180000))
            && shiftedIndex.Audio.Select(a => a.Pts).SequenceEqual(result.Audio.Select(a => a.Pts + 180000)), "Assembly must shift audio and video by the same acquisition clock.");
        var allowedChanges = result.Timing.SelectMany(t => Enumerable.Range((int)t.Offset, 5)).ToHashSet();
        for (int n = 3; n < good.Length; n += 188) allowedChanges.Add(n);
        Require(good.Length == shifted.Length && Enumerable.Range(0, good.Length).All(n => allowedChanges.Contains(n) || good[n] == shifted[n]),
            "Assembly changed coded payload or program/configuration bytes.");
        using var wrapped = new MemoryStream();
        new ReplayTransportWriter(wrapped).Append(new MemoryStream(good), result, -300000, ReplayTransportPackets.All);
        var wrappedIndex = Read(wrapped.ToArray());
        Require(wrappedIndex.Pictures[0].Pts == ReplayTransportReader.TimestampModulus + 253500 - 300000,
            "Negative timestamp shifts must wrap the 33-bit transport clock.");
        using var filtered = new MemoryStream();
        new ReplayTransportWriter(filtered).Append(new MemoryStream(good), result, 0, ReplayTransportPackets.Program);
        byte[] program = filtered.ToArray();
        Require(program.Length == 376 && ((program[1] & 31) << 8 | program[2]) == 0
            && ((program[189] & 31) << 8 | program[190]) == 4096, "Boundary assembly must retain original program metadata without source video/audio.");
        using var prefix = new MemoryStream();
        var prefixWriter = new ReplayTransportWriter(prefix);
        prefixWriter.Append(new ShortReads(good), result, 180000, ReplayTransportPackets.Program);
        prefixWriter.AppendVideoPrefix(new ShortReads(good), result, 180000, 1);
        prefixWriter.Append(new ShortReads(good), result, 180000, ReplayTransportPackets.Audio);
        var prefixIndex = Read(prefix.ToArray());
        Require(prefixIndex.Pictures.Count == 1 && prefixIndex.Pictures[0].Pts == result.Pictures[0].Pts + 180000
            && prefixIndex.Sps.Span.SequenceEqual(result.Sps.Span) && prefixIndex.Pps.Span.SequenceEqual(result.Pps.Span)
            && prefixIndex.Audio.Select(a => a.Pts).SequenceEqual(result.Audio.Select(a => a.Pts + 180000)),
            "An independent video prefix must retain precisely the selected complete picture, configuration and original audio clock.");
        using var fullVideo = new MemoryStream(); using var fullPrefix = new MemoryStream();
        new ReplayTransportWriter(fullVideo).Append(new ShortReads(good), result, 0, ReplayTransportPackets.Video);
        new ReplayTransportWriter(fullPrefix).AppendVideoPrefix(new ShortReads(good), result, 0, result.Pictures.Count);
        Require(fullVideo.ToArray().SequenceEqual(fullPrefix.ToArray()), "A complete video prefix changed coded transport bytes.");
        foreach (int count in new[] { 0, result.Pictures.Count + 1 })
            Reject<IOException>(() => new ReplayTransportWriter(new MemoryStream()).AppendVideoPrefix(new MemoryStream(good), result, 0, count),
                "An invalid prefix picture count was accepted.");
        var dependent = result with { Pictures = result.Pictures.Select((p, n) => n == 0 ? p with { Idr = false } : p).ToArray() };
        Reject<IOException>(() => new ReplayTransportWriter(new MemoryStream()).AppendVideoPrefix(new MemoryStream(good), dependent, 0, 1),
            "A dependent video prefix was accepted.");
        damaged = good.ToArray(); damaged[checked((int)result.Pictures[1].PacketOffset) + 100] ^= 1;
        Reject<IOException>(() => new ReplayTransportWriter(new MemoryStream()).AppendVideoPrefix(new MemoryStream(damaged), result, 0, 1),
            "A discarded tail changed after indexing without poisoning the entire unpublished prefix.");
        damaged = good.ToArray(); damaged[^1] ^= 1;
        Reject<IOException>(() => new ReplayTransportWriter(new MemoryStream()).Append(new MemoryStream(damaged), result, 0, ReplayTransportPackets.All),
            "Assembly accepted bytes changed after indexing.");
        Console.WriteLine("Passed: one-pass transport hash, split reads, actual clocks/IDRs/AAC coverage, corruption rejection and clock-only protected assembly.");
    }

    static int PayloadStart(byte[] bytes, int packet) => packet + ((bytes[packet + 3] & 32) != 0 ? 5 + bytes[packet + 4] : 4);
    internal static string PacketHashForTest(params byte[][] nals)
    {
        byte[] framed = nals.SelectMany(nal => { byte[] prefix = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(prefix, (uint)nal.Length); return prefix.Concat(SHA256.HashData(nal)); }).ToArray();
        return Convert.ToHexString(SHA256.HashData(framed));
    }
    static byte[] EditPsi(byte[] input, int pid, Action<byte[]> edit)
    {
        byte[] output = input.ToArray();
        int packet = Enumerable.Range(0, input.Length / 188).Select(n => n * 188).First(n => (((input[n + 1] & 31) << 8) | input[n + 2]) == pid);
        int payload = PayloadStart(input, packet), start = payload + input[payload] + 1;
        int length = 3 + (((input[start + 1] & 15) << 8) | input[start + 2]);
        byte[] section = input.AsSpan(start, length).ToArray(); edit(section); uint crc = uint.MaxValue;
        foreach (byte value in section[..^4])
        {
            crc ^= (uint)value << 24;
            for (int bit = 0; bit < 8; bit++) crc = (crc << 1) ^ ((crc & 0x80000000) != 0 ? 0x04c11db7u : 0);
        }
        section[^4] = (byte)(crc >> 24); section[^3] = (byte)(crc >> 16); section[^2] = (byte)(crc >> 8); section[^1] = (byte)crc;
        section.CopyTo(output, start); return output;
    }

    // Structural coded-byte fixture, not decodable-picture qualification.
    internal static byte[] VideoSequence(int frames,long firstPts,bool differentConfiguration=false)
    {
        if(frames is <1 or >120 || firstPts<63000)throw new ArgumentOutOfRangeException(nameof(frames));
        byte[] seed=Fixture();
        var packets=Enumerable.Range(0,seed.Length/188).Select(n=>seed.AsSpan(n*188,188).ToArray()).ToArray();
        int Pid(byte[] p)=>((p[1]&31)<<8)|p[2];
        var video=packets.Where(p=>Pid(p)==256).ToArray();
        if(video.Length!=2)throw new IOException("Video fixture template changed.");
        using var output=new MemoryStream();
        foreach(var original in packets.Where(p=>Pid(p) is 0 or 4096))
        {
            byte[] packet=original;
            if(Pid(packet)==4096)
            {
                byte[] section=[2,0xb0,0x12,0,1,0xc1,0,0,0xe1,0,0xf0,0,27,0xe1,0,0xf0,0];
                uint crc=uint.MaxValue;
                foreach(byte value in section)
                {crc^=(uint)value<<24;for(int bit=0;bit<8;bit++)crc=(crc<<1)^((crc&0x80000000)!=0?0x04c11db7u:0);}
                byte[] payload=new byte[]{0}.Concat(section).Concat(new[]{(byte)(crc>>24),(byte)(crc>>16),(byte)(crc>>8),(byte)crc}).ToArray();
                packet=Enumerable.Repeat((byte)255,188).ToArray();
                packet[0]=0x47;packet[1]=0x50;packet[2]=0;packet[3]=0x30;packet[4]=(byte)(183-payload.Length);packet[5]=0;
                payload.CopyTo(packet,5+packet[4]);
            }
            output.Write(packet);
        }
        for(int n=0;n<frames;n++)
        {
            byte[] packet=video[n==0?0:1].ToArray();packet[3]=(byte)((packet[3]&0xf0)|(n&15));
            int start=(packet[3]&0x20)!=0?5+packet[4]:4;
            long pts=firstPts+n*1500;
            byte[] stamp=[(byte)(0x21|((pts>>29)&14)),(byte)(pts>>22),(byte)(((pts>>14)&254)|1),(byte)(pts>>7),(byte)(((pts<<1)&254)|1)];
            stamp.CopyTo(packet,start+9);
            if((packet[3]&0x20)!=0 && packet[4]>=7 && (packet[5]&0x10)!=0)
            {
                long pcr=pts-63000;packet[6]=(byte)(pcr>>25);packet[7]=(byte)(pcr>>17);packet[8]=(byte)(pcr>>9);
                packet[9]=(byte)(pcr>>1);packet[10]=(byte)(((pcr&1)<<7)|0x7e);packet[11]=0;
            }
            if(n==0 && differentConfiguration)
                for(int at=start;at+6<packet.Length;at++)
                    if(packet[at]==0 && packet[at+1]==0 && packet[at+2]==1 && packet[at+3]==0x67){packet[at+6]=32;break;}
            if(n!=0)
                for(int at=start;at+4<packet.Length;at++)
                    if(packet[at]==0 && packet[at+1]==0 && packet[at+2]==1 && packet[at+3]==0x41)
                    {packet[at+4]=(byte)(0xe0|((n&15)<<1)|1);break;}
            output.Write(packet);
        }
        return output.ToArray();
    }

    internal static byte[] Fixture(bool firstIdr = true, int audioFrameLength = 200, bool audioGap = false, bool videoGap = false, bool changeSps = false, string layout = "single", byte[] codedPayload = null, bool audioCrc = false, int audioBlocks = 1)
    {
        using var output = new MemoryStream(); var counters = new Dictionary<int, int>();
        void Packetize(int pid, byte[] payload, long? pcr = null)
        {
            int offset = 0;
            while (offset < payload.Length)
            {
                byte[] packet = Enumerable.Repeat((byte)255, 188).ToArray();
                bool first = offset == 0; int maximum = first && pcr.HasValue ? 176 : 184;
                int count = Math.Min(maximum, payload.Length - offset); bool adaptation = count < 184;
                packet[0] = 0x47; packet[1] = (byte)((pid >> 8) | (first ? 0x40 : 0)); packet[2] = (byte)pid;
                counters.TryGetValue(pid, out int counter); counters[pid] = (counter + 1) & 15;
                packet[3] = (byte)((adaptation ? 0x30 : 0x10) | counter);
                int begin = 4;
                if (adaptation)
                {
                    int length = 183 - count; packet[4] = (byte)length; begin = 5 + length;
                    if (length != 0)
                    {
                        packet[5] = (byte)(first && pcr.HasValue ? 0x10 : 0);
                        if (first && pcr.HasValue)
                        {
                            long value = pcr.Value;
                            packet[6] = (byte)(value >> 25); packet[7] = (byte)(value >> 17); packet[8] = (byte)(value >> 9);
                            packet[9] = (byte)(value >> 1); packet[10] = (byte)(((value & 1) << 7) | 0x7e); packet[11] = 0;
                        }
                    }
                }
                payload.AsSpan(offset, count).CopyTo(packet.AsSpan(begin)); output.Write(packet); offset += count;
            }
        }
        static byte[] Section(byte[] bytes)
        {
            uint crc = uint.MaxValue;
            foreach (byte value in bytes)
            {
                crc ^= (uint)value << 24;
                for (int bit = 0; bit < 8; bit++) crc = (crc << 1) ^ ((crc & 0x80000000) != 0 ? 0x04c11db7u : 0);
            }
            return new byte[] { 0 }.Concat(bytes).Concat(new[] { (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc }).ToArray();
        }
        Packetize(0, Section([0, 0xb0, 0x0d, 0, 1, 0xc1, 0, 0, 0, 1, 0xf0, 0]));
        Packetize(4096, Section([2, 0xb0, 0x17, 0, 1, 0xc1, 0, 0, 0xe1, 0, 0xf0, 0, 27, 0xe1, 0, 0xf0, 0, 15, 0xe1, 1, 0xf0, 0]));
        static byte[] Pes(int stream, long pts, byte[] payload)
        {
            int length = stream == 0xe0 ? 0 : payload.Length + 8;
            byte[] header = [0, 0, 1, (byte)stream, (byte)(length >> 8), (byte)length, 0x80, 0x80, 5,
                (byte)(0x21 | ((pts >> 29) & 14)), (byte)(pts >> 22), (byte)(((pts >> 14) & 254) | 1), (byte)(pts >> 7), (byte)(((pts << 1) & 254) | 1)];
            return header.Concat(payload).ToArray();
        }
        static string Ue(int value)
        { string code = Convert.ToString(value + 1, 2); return new string('0', code.Length - 1) + code; }
        static byte[] Bits(string value)
        {
            value = value.PadRight((value.Length + 7) / 8 * 8, '0');
            return Enumerable.Range(0, value.Length / 8).Select(n => Convert.ToByte(value.Substring(n * 8, 8), 2)).ToArray();
        }
        byte[] Video(bool idr, bool sps, bool changed)
        {
            var bytes = new List<byte>();
            void Nal(int type, byte[] rbsp) { bytes.AddRange(new byte[] { 0, 0, 1, (byte)type }); bytes.AddRange(rbsp); }
            if (layout != "missing-aud") Nal(9, [0xf0]);
            if (sps)
            {
                Nal(0x67, new byte[] { 0x42, 0, (byte)(changed ? 32 : 31) }.Concat(Bits(Ue(0) + Ue(0) + Ue(2) + Ue(1) + "0" + Ue(19) + Ue(11)
                    + (layout == "field" ? "0" : "1") + "1001")).ToArray());
                Nal(0x68, Bits(Ue(0) + Ue(0) + "00" + Ue(0) + Ue(0) + Ue(0) + (layout == "reserved-bipred" ? "011" : "000") + Ue(0) + Ue(0) + Ue(0) + "1001"));
            }
            byte[] slice = Bits(Ue(layout == "continued-slice" ? 1 : 0) + Ue(idr ? 2 : 0) + Ue(layout == "wrong-pps" ? 1 : 0)
                + (layout == "idr-frame-number" || !idr ? "0001" : "0000") + (idr ? Ue(0) : "") + "1");
            Nal(layout == "extension" ? 0x74 : idr ? 0x65 : 0x41, slice.Concat(codedPayload ?? []).ToArray());
            if (layout == "two-auds") Nal(9, [0xf0]);
            if (layout is "two-pictures" or "two-auds" or "multislice")
                Nal(idr ? 0x65 : 0x41, layout == "multislice" ? Bits(Ue(10) + Ue(idr ? 2 : 0) + Ue(0) + "0000" + Ue(0) + "1") : slice);
            return bytes.ToArray();
        }
        byte[] audio = new byte[audioCrc ? 202 : 200]; int declaredAudioLength = audioFrameLength + (audioCrc ? 2 : 0);
        new byte[] { 255, audioCrc ? (byte)0xf0 : (byte)0xf1, 0x4c, (byte)(0x80 | (declaredAudioLength >> 11)), (byte)(declaredAudioLength >> 3), (byte)(((declaredAudioLength & 7) << 5) | 31), 0xfc }.CopyTo(audio, 0);
        if (audioCrc) { audio[7] = 0x12; audio[8] = 0x34; }
        audio = Enumerable.Range(0, audioBlocks).SelectMany(_ => audio).ToArray();
        Packetize(256, Pes(0xe0, 253500, Video(firstIdr, true, false)), 190500);
        Packetize(257, Pes(0xc0, 251580, audio));
        Packetize(256, Pes(0xe0, videoGap ? 255001 : 255000, Video(false, changeSps, changeSps)), 192000);
        Packetize(257, Pes(0xc0, 251580 + audioBlocks * 1920 + (audioGap ? 1 : 0), audio));
        return output.ToArray();
    }

    sealed class ShortReads(byte[] bytes) : MemoryStream(bytes)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, 1));
    }
}
