using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using F10Recorder;

internal static class ReplayMp4Checks
{
    internal static byte[] PacketMuxFixture() => Fixture();
    internal static void RunFixtures(string planPath, string expectedSha)
    {
        byte[] raw = File.ReadAllBytes(planPath);
        Require(raw.Length <= 65536 && Convert.ToHexString(SHA256.HashData(raw)).Equals(expectedSha, StringComparison.OrdinalIgnoreCase), "MP4 file-only plan changed.");
        using var plan = JsonDocument.Parse(raw); var p = plan.RootElement;
        string root = Path.GetFullPath(p.GetProperty("root").GetString());
        Require(p.GetProperty("kind").GetString() == "issue83-managed-mp4-evidence-v1" && !Directory.Exists(root)
            && root.StartsWith(Path.GetFullPath("artifacts/issue83") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && DateTimeOffset.UtcNow.AddSeconds(60) < p.GetProperty("expires_utc").GetDateTimeOffset(), "Fresh bounded MP4 file-only admission required.");
        var bindings = p.GetProperty("bindings").EnumerateObject().ToArray();
        Require(p.GetProperty("cases").GetArrayLength() is > 0 and <= 8 && bindings.Length <= 256 && bindings.Any(b => Path.GetFullPath(b.Name).Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
            && bindings.Any(b => Path.GetFullPath(b.Name).Equals(typeof(ReplayMp4Checks).Assembly.Location, StringComparison.OrdinalIgnoreCase))
            && bindings.Any(b => Path.GetFullPath(b.Name).Equals(typeof(ReplayMp4Reader).Assembly.Location, StringComparison.OrdinalIgnoreCase)), "Current helper/runtime must be bound.");
        var clock = Stopwatch.StartNew(); long lastGuard = 0, maximumRam = 0;
        void Guard()
        {
            Require(clock.Elapsed.TotalSeconds < 45, "MP4 file-only deadline.");
            if (Stopwatch.GetElapsedTime(lastGuard).TotalMilliseconds < 50) return;
            lastGuard = Stopwatch.GetTimestamp(); using var process = Process.GetCurrentProcess();
            maximumRam = Math.Max(maximumRam, process.WorkingSet64);
            Require(maximumRam < 268435456 && new DriveInfo(Path.GetPathRoot(root)).AvailableFreeSpace >= 60000000000L, "MP4 file-only resource/free-space bound.");
        }
        Directory.CreateDirectory(root); File.WriteAllBytes(Path.Combine(root, "admitted-plan.json"), raw);
        var streams = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase); var rows = new List<object>(); string failure = null;
        try
        {
            long admittedBytes = 0;
            byte[] hashBuffer = new byte[65536];
            foreach (var binding in bindings)
            {
                Guard(); var source = new FileStream(binding.Name, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
                streams.Add(Path.GetFullPath(binding.Name), source);
                admittedBytes = checked(admittedBytes + source.Length); Require(admittedBytes <= 1000000000, "MP4 file-only input-byte cap.");
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                int read; while ((read = source.Read(hashBuffer)) != 0) { hash.AppendData(hashBuffer, 0, read); Guard(); }
                Require(Convert.ToHexString(hash.GetHashAndReset()).Equals(binding.Value.GetString(), StringComparison.OrdinalIgnoreCase), "Bound MP4 evidence input changed.");
            }
            foreach (var item in p.GetProperty("cases").EnumerateArray())
            {
                Guard(); string path = item.GetProperty("path").GetString(), label = item.GetProperty("label").GetString(); double seconds = item.GetProperty("seconds").GetDouble();
                var source = streams[Path.GetFullPath(path)]; long at = Stopwatch.GetTimestamp(); ReplayMp4Index evidence = null; string error = null;
                try { evidence = ReplayMp4Reader.Read(source, source.Length, new(0, seconds, (int)seconds), 60, true, Guard); }
                catch (Exception ex) when (ex is IOException or NotSupportedException) { error = ex.ToString(); }
                bool accepted = evidence != null;
                rows.Add(new { label, accepted, error, seconds = Stopwatch.GetElapsedTime(at).TotalSeconds,
                    tracks = evidence?.Tracks.Select(t => new { t.Kind, t.Codec, t.Timescale, t.MediaStart, t.DelaySeconds, t.PresentationSeconds, t.Width, t.Height,
                        configuration = Convert.ToHexString(t.Configuration), sps = t.Sps == null ? null : Convert.ToHexString(t.Sps), pps = t.Pps == null ? null : Convert.ToHexString(t.Pps),
                        samples = t.Samples.Count, sampleEvidenceSha256 = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(string.Join("", t.Samples.Select(s => s.CodedSha256))))) }) });
                Require(accepted == item.GetProperty("accepted").GetBoolean(), "Frozen MP4 file-only expectation disagrees: " + label);
            }
        }
        catch (Exception ex) { failure = ex.ToString(); throw; }
        finally
        {
            foreach (var source in streams.Values) source.Dispose();
            string report = JsonSerializer.Serialize(new { status = failure == null ? "passed-file-only-evidence" : "failed", error = failure,
                process_id = Environment.ProcessId, executable = Environment.ProcessPath, cases = rows, maximum_observed_working_set = maximumRam,
                seconds_including_input_hashes = clock.Elapsed.TotalSeconds, capture = false, publication = false, media_processes = 0,
                limitation = "Warm file-only evidence with source admission hashes; no decoded correctness, final source-proof comparison, normal publication or Saved acceptance." });
            Require(Encoding.UTF8.GetByteCount(report) < 1048576, "MP4 report bound."); File.WriteAllText(Path.Combine(root, "outcome.json"), report);
        }
    }
    static void Require(bool value, string why) { if (!value) throw new Exception(why); }
    static void Reject(Action action, string why)
    { try { action(); } catch (IOException) { return; } catch (NotSupportedException) { return; } throw new Exception(why); }
    internal static void Run()
    {
        foreach (bool includeVideoPayloadDigest in new[] { true, false }) RunMode(includeVideoPayloadDigest);
    }
    static void RunMode(bool includeVideoPayloadDigest)
    {
        // Tiny deliberately non-playable coded bytes. These fixtures establish
        // packet/configuration/clock evidence, never decoded media correctness.
        var interval = new ReplayInterval(0, 1, 1);
        ReplayMp4Index Read(byte[] bytes) => ReplayMp4Reader.Read(new MemoryStream(bytes), bytes.Length, interval, 60, true,
            includeVideoPayloadDigest: includeVideoPayloadDigest);
        var bytes = Fixture(); var observed = Read(bytes);
        double minimumStart = 203d / 60;
        Require(ReplayMp4Reader.Read(new MemoryStream(bytes), bytes.Length, new(minimumStart, minimumStart + 1, 1), 60, true,
            includeVideoPayloadDigest: includeVideoPayloadDigest).Tracks.Count == 2,
            "Closed MP4 minimum interval was lost to floating-point subtraction.");
        var video = observed.Tracks.Single(t => t.Kind == "vide"); var audio = observed.Tracks.Single(t => t.Kind == "soun");
        Require(video.Samples.All(s => includeVideoPayloadDigest ? s.PayloadSha256?.Length == 64 : s.PayloadSha256 == null)
            && audio.Samples.All(s => s.PayloadSha256?.Length == 64), "Optional video diagnostic digest affected required AAC proof or claimed uncomputed evidence.");
        var rich = ReplayMp4Reader.Read(new MemoryStream(bytes), bytes.Length, interval, 60, true);
        Require(video.Samples.Select(s => s with { PayloadSha256 = null }).SequenceEqual(rich.Tracks[0].Samples.Select(s => s with { PayloadSha256 = null }))
            && audio.Samples.SequenceEqual(rich.Tracks[1].Samples), "Optional diagnostic mode changed normal source-proof evidence.");
        Require(video.Samples.Count == 60 && audio.Samples.Count == 48 && audio.MediaStart == 1024
            && video.Samples[0].Idr && video.Samples.Skip(1).All(s => !s.Idr)
            && video.Samples.All(s => s.Duration == 800 && s.CodedSha256.Length == 64), "MP4 evidence lost physical sample counts, clocks or coded identities.");
        Require(video.Samples[0].CodedSha256 == Convert.ToHexString(SHA256.HashData(Picture(0))), "MP4 picture hash did not cover the independently constructed VCL.");
        Require(video.Samples[0].PacketSha256 == ReplayTransportChecks.PacketHashForTest(new byte[] { 6, 5, 1, 0xab, 0x80 }, Picture(0)),
            "Source and final MP4 evidence must use the same independently framed full-NAL proof.");
        byte[] frozenSps = video.Sps.ToArray();
        var expectedPictures = video.Samples.Select(s => new ReplayExpectedPicture(s.Idr, s.CodedSha256, s.PacketSha256)).ToArray();
        var expectedAudio = audio.Samples.Select(s => new ReplayExpectedAudio(s.Duration, s.PayloadSha256)).ToArray();
        // This baseline-derived oracle tests the comparison only. Product
        // expectation construction MUST use pre-mux protected inputs instead.
        var proof = new ReplayMp4Expectation(60, 0, frozenSps, video.Pps, expectedPictures, 1024, expectedAudio);
        var sameProof = new ReplayMp4Expectation(60, 0, frozenSps.ToArray(), video.Pps.ToArray(), expectedPictures.ToArray(), 1024, expectedAudio.ToArray());
        string fingerprint = proof.Fingerprint;
        Require(fingerprint.Length == 64 && fingerprint == sameProof.Fingerprint,
            "Equivalent frozen pre-mux evidence has no stable fingerprint.");
        string ChangedProof(ReplayExpectedPicture[] pictures = null, byte[] sps = null, long priming = 1024,
            ReplayExpectedAudio[] packets = null, double first = 0) =>
            new ReplayMp4Expectation(60, first, sps ?? frozenSps, video.Pps, pictures ?? expectedPictures, priming, packets ?? expectedAudio).Fingerprint;
        var differentPictures = expectedPictures.ToArray(); differentPictures[1] = differentPictures[1] with { PacketSha256 = new string('A', 64) };
        var differentAudio = expectedAudio.ToArray(); differentAudio[1] = differentAudio[1] with { Duration = 1023 };
        var differentSps = frozenSps.ToArray(); differentSps[^1] ^= 1;
        Require(new[] { ChangedProof(pictures: differentPictures), ChangedProof(sps: differentSps), ChangedProof(priming: 1023),
            ChangedProof(packets: differentAudio), ChangedProof(first: 1d / 60) }.All(h => h != fingerprint),
            "Source proof fingerprint omitted packet, configuration or presentation-clock evidence.");
        frozenSps[0] ^= 1; expectedPictures[0] = null; expectedAudio[0] = null;
        Require(proof.Verify(observed) == new ReplayIntegrityResult(60, 48) && proof.Fingerprint == fingerprint,
            "Frozen proof aliases mutable caller arrays or mislabels coded counts.");
        foreach (string layout in new[] { "co64", "extended-mdat", "version1" })
            Require(Read(Fixture(layout)).Tracks[0].Samples.Select(s => s.CodedSha256).SequenceEqual(video.Samples.Select(s => s.CodedSha256)),
                "Supported 64-bit layout changed coded evidence: " + layout);
        using (var shortReads = new ShortReads(bytes))
            Require(ReplayMp4Reader.Read(shortReads, bytes.Length, interval, 60, true,
                includeVideoPayloadDigest: includeVideoPayloadDigest).Tracks[0].Samples.SequenceEqual(video.Samples), "Split MP4 reads changed coded evidence.");
        var deep = bytes.ToArray(); deep[video.Samples[10].Offset + 9 + 4 + 70] ^= 1;
        Require(Read(deep).Tracks[0].Samples[10].CodedSha256 != video.Samples[10].CodedSha256,
            "MP4 evidence ignored deep bytes outside the picture header.");
        Reject(() => proof.Verify(Read(deep)), "Source proof accepted altered deep compressed picture bytes.");
        var sei = bytes.ToArray(); sei[video.Samples[10].Offset + 7] ^= 1;
        var seiEvidence = Read(sei).Tracks[0].Samples[10];
        Require(seiEvidence.CodedSha256 == video.Samples[10].CodedSha256 && seiEvidence.PacketSha256 != video.Samples[10].PacketSha256
            && (!includeVideoPayloadDigest || seiEvidence.PayloadSha256 != video.Samples[10].PayloadSha256),
            "Non-VCL metadata changes must be visible in complete sample evidence, independently of coded picture identity.");
        Reject(() => proof.Verify(Read(sei)), "Source proof ignored altered non-VCL metadata.");
        var audioDamage = bytes.ToArray(); audioDamage[audio.Samples[12].Offset + 1] ^= 1;
        Reject(() => proof.Verify(Read(audioDamage)), "Source proof accepted altered AAC bytes.");
        var alteredPriming = observed with { Tracks = [video, audio with { MediaStart = 1025 }] };
        Reject(() => proof.Verify(alteredPriming), "Source proof accepted one-sample AAC priming drift.");
        Reject(() => proof.Verify(observed with { Tracks = [video with { Samples = video.Samples.Take(59).ToArray() }, audio] }), "Source proof tolerated a missing physical picture.");
        var shortenedTail = Read(Fixture("short-video-edit"));
        Require(shortenedTail.Tracks[0].PresentationTicks == 999, "Tail-edit control did not reach the intended presentation difference.");
        Reject(() => proof.Verify(shortenedTail), "Source proof accepted a shortened edit hiding the expected video tail.");
        foreach (string fault in new[] { "count", "duration", "drift", "hidden-head", "truncated-extent", "overlap", "external-reference", "wrong-sync", "nal-length", "duplicate-moov", "missing-audio",
            "movie-duration", "disabled-track", "track-duration", "duplicate-id", "transform", "dimensions", "avcc-profile", "forbidden-sps", "nal-flood", "unindexed-tail", "aac-config", "short-all-edits" })
            Reject(() => Read(Fixture(fault)), "MP4 inspection accepted " + fault);
        Reject(() => Read(bytes[..^1]), "Truncated closed MP4 was accepted.");
        using (var changed = new ChangedLength(bytes))
            Reject(() => ReplayMp4Reader.Read(changed, bytes.Length, interval, 60, true,
                includeVideoPayloadDigest: includeVideoPayloadDigest), "Changed closed output extent was accepted.");
        int checks = 0;
        try { ReplayMp4Reader.Read(new MemoryStream(bytes), bytes.Length, interval, 60, true,
            () => { if (++checks == 2) throw new OperationCanceledException(); }, includeVideoPayloadDigest: includeVideoPayloadDigest); throw new Exception("MP4 inspection swallowed cancellation."); }
        catch (OperationCanceledException) { }
        Console.WriteLine("Passed: bounded closed-MP4 sample/configuration evidence, split reads, deep hashes, AAC edits, truncation, drift, hidden pictures and guard cancellation; video raw diagnostic=" + includeVideoPayloadDigest + "; no media decode/publication.");
    }
    static byte[] Picture(int n) => (n == 0 ? new byte[] { 0x65, 0xb8, 0x60 } : new byte[] { 0x41, (byte)(0xe1 | (n % 16 << 1)) })
        .Concat(Enumerable.Repeat((byte)0xab, 80)).ToArray();
    static byte[] U32(uint n) { byte[] b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, n); return b; }
    static byte[] U64(ulong n) { byte[] b = new byte[8]; BinaryPrimitives.WriteUInt64BigEndian(b, n); return b; }
    static byte[] Box(string kind, params byte[][] parts)
    { var bytes = parts.SelectMany(b => b).ToArray(); return U32(checked((uint)bytes.Length + 8)).Concat(Encoding.ASCII.GetBytes(kind)).Concat(bytes).ToArray(); }
    static byte[] Table(string name, params uint[] values) => Box(name, new byte[4], values.SelectMany(U32).ToArray());
    static byte[] Fixture(string fault = "")
    {
        byte[] ts = ReplayTransportChecks.Fixture(); var source = ReplayTransportReader.Read(new MemoryStream(ts), ts.Length, 60, true);
        var pictures = Enumerable.Range(0, 60).Select(n => { var nal = Picture(n); var meta = new byte[] { 0, 0, 0, 5, 6, 5, 1, 0xab, 0x80 };
            if (fault == "nal-flood") meta = Enumerable.Range(0, 65).SelectMany(_ => meta).ToArray();
            return meta.Concat(U32((uint)nal.Length)).Concat(nal).ToArray(); }).ToArray();
        var audio = Enumerable.Range(0, 48).Select(n => new byte[] { 0x11, (byte)n, 0x80 }).ToArray();
        if (fault == "nal-length") pictures[10][0] = 0x7f;
        byte[] payload = pictures.SelectMany(b => b).Concat(audio.SelectMany(b => b)).Concat(fault == "unindexed-tail" ? new byte[1] : []).ToArray();
        byte[] ftyp = Box("ftyp", Encoding.ASCII.GetBytes("isom"), U32(0), Encoding.ASCII.GetBytes("isomiso2avc1mp41"));
        byte[] Clock(uint scale, uint duration)
        {
            if (fault == "version1") { byte[] v1 = new byte[36]; v1[0] = 1; U32(scale).CopyTo(v1, 20); U64(duration).CopyTo(v1, 24); return v1; }
            byte[] b = new byte[24]; U32(scale).CopyTo(b, 12); U32(duration).CopyTo(b, 16); return b;
        }
        void Matrix(byte[] bytes, int at) { U32(65536).CopyTo(bytes, at); U32(65536).CopyTo(bytes, at + 16); U32(0x40000000).CopyTo(bytes, at + 32); }
        byte[] Track(bool video, uint offset)
        {
            var samples = video ? pictures : audio;
            uint duration = video ? 48000u : 49024u;
            byte[] body = new byte[video ? 78 : 28]; body[7] = 1;
            if (video) { BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(24), 320); BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(26), 192); }
            else { body[17] = 2; body[19] = 16; U32(48000u << 16).CopyTo(body, 24); }
            byte[] configuration = video
                ? Box("avcC", new byte[] { 1, 0x42, 0, 31, 255, 225, (byte)(source.Sps.Length >> 8), (byte)source.Sps.Length }, source.Sps.ToArray(),
                    new byte[] { 1, (byte)(source.Pps.Length >> 8), (byte)source.Pps.Length }, source.Pps.ToArray())
                : Box("esds", new byte[4], Descriptor(3, new byte[] { 0, 2, 0 },
                    Descriptor(4, new byte[] { 0x40, 0x15, 0, 0, 0 }, U32(192000), U32(192000),
                        Descriptor(5, new byte[] { fault == "aac-config" ? (byte)0x12 : (byte)0x11, 0x90 })), Descriptor(6, new byte[] { 2 })));
            if (video && fault == "dimensions") BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(24), 640);
            if (video && fault == "avcc-profile") configuration[9] ^= 1;
            if (video && fault == "forbidden-sps") configuration[16] |= 128;
            byte[] description = Box("stsd", new byte[4], U32(1), Box(video ? "avc1" : "mp4a", body, configuration));
            var tables = new List<byte[]> { description, Table("stsz", [0, (uint)samples.Length, .. samples.Select(b => (uint)b.Length)]),
                video ? Table("stts", 1, 60, fault == "duration" ? 801u : 800u) : Table("stts", 2, 47, 1024, 1, 896),
                Table("stsc", 1, 1, (uint)samples.Length, 1), fault == "co64" ? Box("co64", new byte[4], U32(1), U64(offset)) : Table("stco", 1, offset) };
            if (video) tables.Add(Table("stss", 1, fault == "wrong-sync" ? 2u : 1u));
            if (video && fault == "count") tables[1] = Table("stsz", [0, 59, .. samples.Take(59).Select(b => (uint)b.Length)]);
            if (video && fault == "drift") { tables[2] = Table("stts", 2, 30, 800, 30, 801); duration += 30; }
            byte[] hdlr = new byte[24]; Encoding.ASCII.GetBytes(video ? "vide" : "soun").CopyTo(hdlr, 8);
            byte[] edit = Table("elst", 1, 1000, video ? (fault == "hidden-head" ? 1u : 0u) : 1024u, 0x00010000);
            if (fault == "short-all-edits" || video && fault == "short-video-edit") edit = Table("elst", 1, 999, video ? 0u : 1024u, 0x00010000);
            if (fault == "version1") edit = Box("elst", new byte[] { 1, 0, 0, 0 }, U32(1), U64(1000), U64(video ? 0u : 1024u), U32(65536));
            var reference = Table("dref", 1, 12, 0x75726c20, fault == "external-reference" ? 0u : 1u);
            byte[] trackHeader = new byte[84]; trackHeader[3] = 3; U32(video ? 1u : 2u).CopyTo(trackHeader, 12); U32(1000).CopyTo(trackHeader, 20);
            if (!video) trackHeader[36] = 1;
            Matrix(trackHeader, 40); U32(video ? 320u << 16 : 0).CopyTo(trackHeader, 76); U32(video ? 192u << 16 : 0).CopyTo(trackHeader, 80);
            if (video && fault == "disabled-track") trackHeader[3] = 2;
            if (video && fault == "track-duration") U32(1200).CopyTo(trackHeader, 20);
            if (!video && fault == "duplicate-id") U32(1).CopyTo(trackHeader, 12);
            if (video && fault == "transform") trackHeader[40] = 1;
            if (fault == "short-all-edits" || video && fault == "short-video-edit") U32(999).CopyTo(trackHeader, 20);
            if (fault == "version1")
            {
                trackHeader = new byte[96]; trackHeader[0] = 1; trackHeader[3] = 3; U32(video ? 1u : 2u).CopyTo(trackHeader, 20); U64(1000).CopyTo(trackHeader, 28);
                if (!video) trackHeader[48] = 1;
                Matrix(trackHeader, 52); U32(video ? 320u << 16 : 0).CopyTo(trackHeader, 88); U32(video ? 192u << 16 : 0).CopyTo(trackHeader, 92);
            }
            return Box("trak", Box("tkhd", trackHeader), Box("edts", edit), Box("mdia", Box("mdhd", Clock(48000, duration)), Box("hdlr", hdlr),
                Box("minf", Box("dinf", reference), Box("stbl", tables.ToArray()))));
        }
        byte[] Movie(uint start)
        {
            var tracks = new List<byte[]> { Track(true, fault == "truncated-extent" ? uint.MaxValue : start) };
            if (fault != "missing-audio") tracks.Add(Track(false, fault == "overlap" ? start : start + (uint)pictures.Sum(p => p.Length)));
            byte[] movieHeader = new byte[100]; Clock(1000, 1000).CopyTo(movieHeader, 0); U32(65536).CopyTo(movieHeader, 20); movieHeader[24] = 1; Matrix(movieHeader, 36); U32(3).CopyTo(movieHeader, 96);
            if (fault == "version1")
            { movieHeader = new byte[112]; Clock(1000, 1000).CopyTo(movieHeader, 0); U32(65536).CopyTo(movieHeader, 32); movieHeader[36] = 1; Matrix(movieHeader, 48); U32(3).CopyTo(movieHeader, 108); }
            if (fault == "movie-duration") U32(1200).CopyTo(movieHeader, 16);
            if (fault == "short-all-edits") U32(999).CopyTo(movieHeader, 16);
            return Box("moov", [Box("mvhd", movieHeader), .. tracks]);
        }
        byte[] moov = Movie(0); moov = Movie((uint)(ftyp.Length + moov.Length + (fault == "extended-mdat" ? 16 : 8)));
        byte[] mdat = fault == "extended-mdat" ? U32(1).Concat(Encoding.ASCII.GetBytes("mdat")).Concat(U64((ulong)payload.Length + 16)).Concat(payload).ToArray() : Box("mdat", payload);
        return ftyp.Concat(moov).Concat(fault == "duplicate-moov" ? moov : []).Concat(mdat).ToArray();
    }
    static byte[] Descriptor(byte tag, params byte[][] parts)
    { byte[] raw = parts.SelectMany(b => b).ToArray(); return new byte[] { tag, checked((byte)raw.Length) }.Concat(raw).ToArray(); }
    sealed class ShortReads(byte[] bytes) : MemoryStream(bytes)
    { public override int Read(Span<byte> value) => base.Read(value[..Math.Min(value.Length, 1)]); }
    sealed class ChangedLength(byte[] bytes) : MemoryStream(bytes)
    { int reads; public override long Length => base.Length + (++reads == 1 ? 0 : 1); }
}
