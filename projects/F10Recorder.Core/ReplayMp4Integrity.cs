using System.Security.Cryptography;
using System.Text.Json;

namespace F10Recorder;

internal sealed record ReplayExpectedPicture(bool Idr, string CodedSha256, string PacketSha256);
internal sealed record ReplayExpectedAudio(long Duration, string PayloadSha256);
internal sealed record ReplayIntegrityResult(long CodedPictures, long AudioPackets);

// Ephemeral per-export proof, constructed from protected original or newly
// encoded inputs BEFORE final muxing. Never derive it from the final file that
// it is supposed to check. It carries no claim that those inputs fully decode.
internal sealed class ReplayMp4Expectation
{
    internal readonly int FramesPerSecond;
    internal readonly double FirstVideoSeconds;
    internal readonly long AudioPrimingSamples;
    internal readonly bool Audio;
    internal readonly string Fingerprint;
    readonly byte[] sps, pps;
    readonly ReplayExpectedPicture[] pictures;
    readonly ReplayExpectedAudio[] audio;

    internal ReplayMp4Expectation(int framesPerSecond, double firstVideoSeconds, ReadOnlySpan<byte> sps,
        ReadOnlySpan<byte> pps, IEnumerable<ReplayExpectedPicture> pictures,
        long audioPrimingSamples = 0, IEnumerable<ReplayExpectedAudio> audio = null)
    {
        if (framesPerSecond is < 1 or > 120 || !double.IsFinite(firstVideoSeconds) || firstVideoSeconds < 0
            || firstVideoSeconds > 1d / framesPerSecond + .000022 || sps.IsEmpty || pps.IsEmpty)
            throw new IOException("Incomplete frozen replay output expectation.");
        FramesPerSecond = framesPerSecond; FirstVideoSeconds = firstVideoSeconds;
        this.sps = sps.ToArray(); this.pps = pps.ToArray();
        this.pictures = pictures?.Take(216001).ToArray() ?? throw new IOException("Missing replay picture proof.");
        this.audio = audio?.Take(84401).ToArray() ?? [];
        Audio = audio != null; AudioPrimingSamples = audioPrimingSamples;
        if (this.pictures.Length < framesPerSecond || this.pictures.Length > 216000
            || this.pictures[0]?.Idr != true || this.pictures.Any(p => p == null || !Digest(p.CodedSha256) || !Digest(p.PacketSha256))
            || Audio && (this.audio.Length == 0 || this.audio.Length > 84400 || audioPrimingSamples is < 0 or > 8192
                || this.audio.Any(a => a == null || a.Duration is < 1 or > 1024 || !Digest(a.PayloadSha256)))
            || !Audio && audioPrimingSamples != 0)
            throw new IOException("Missing, unbounded or malformed replay coded-byte proof.");
        Fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = "replay-source-expectation-v1", framesPerSecond, firstVideoSeconds,
            sps = this.sps, pps = this.pps, pictures = this.pictures, Audio, audioPrimingSamples, audio = this.audio
        })));
    }
    static bool Digest(string text) => text?.Length == 64 && text.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');

    internal ReplayIntegrityResult Verify(ReplayMp4Index output, Action check = null)
    {
        check?.Invoke();
        if (output?.Tracks == null || output.Tracks.Count != (Audio ? 2 : 1)
            || output.Tracks.Count(t => t.Kind == "vide") != 1 || output.Tracks.Count(t => t.Kind == "soun") != (Audio ? 1 : 0))
            throw new IOException("Final MP4 enabled tracks disagree with the frozen source proof.");
        var video = output.Tracks.Single(t => t.Kind == "vide");
        VerifyVideo(video, check);
        if (Audio)
        {
            var track = output.Tracks.Single(t => t.Kind == "soun");
            long expectedSamples = audio.Sum(a => a.Duration) - AudioPrimingSamples;
            long presentationTicks = track.MovieTimescale == 0 ? -1 : checked((expectedSamples * track.MovieTimescale + 47999) / 48000);
            if (track.Codec != "mp4a" || track.Timescale != 48000 || track.MovieTimescale == 0 || track.Samples.Count != audio.Length || track.MediaStart != AudioPrimingSamples || track.DelaySeconds != 0
                || expectedSamples <= 0 || track.PresentationTicks != presentationTicks)
                throw new IOException("Final MP4 AAC priming, source boundary or packet count changed.");
            long dts = 0;
            for (int n = 0; n < audio.Length; n++)
            {
                if ((n & 255) == 0) check?.Invoke();
                if (track.Samples[n].PayloadSha256 != audio[n].PayloadSha256 || track.Samples[n].Duration != audio[n].Duration || track.Samples[n].Dts != dts)
                    throw new IOException("Final MP4 AAC coded bytes or sample clock changed at packet " + n + ".");
                dts = checked(dts + audio[n].Duration);
            }
        }
        check?.Invoke(); return new(pictures.Length, audio.Length);
    }

    // Separately scoped for qualification attribution. This never certifies
    // the enabled audio track or replaces Verify for normal publication.
    internal long VerifyVideo(ReplayMp4Track video, Action check = null)
    {
        if (video == null || video.Kind != "vide" || video.Codec != "avc1" || video.Timescale == 0 || video.MovieTimescale == 0)
            throw new IOException("Missing final MP4 video evidence.");
        long expectedTicks = video.MovieTimescale == 0 ? -1 : checked((pictures.Length * (long)video.MovieTimescale + FramesPerSecond - 1) / FramesPerSecond);
        if (!video.Sps.AsSpan().SequenceEqual(sps) || !video.Pps.AsSpan().SequenceEqual(pps)
            || video.MediaStart != 0 || video.Samples.Count != pictures.Length
            || video.PresentationTicks != expectedTicks
            || Math.Abs(video.DelaySeconds - FirstVideoSeconds) > 1d / 48000 + .000001)
            throw new IOException("Final MP4 video configuration, source boundary or picture count changed.");
        for (int n = 0; n < pictures.Length; n++)
        {
            if ((n & 255) == 0) check?.Invoke();
            var actual = video.Samples[n]; var expected = pictures[n];
            if (actual.Idr != expected.Idr || actual.CodedSha256 != expected.CodedSha256 || actual.PacketSha256 != expected.PacketSha256
                || Math.Abs(actual.Dts / (double)video.Timescale - n / (double)FramesPerSecond) > .000022
                || Math.Abs(actual.Duration / (double)video.Timescale - 1d / FramesPerSecond) > .000022)
                throw new IOException("Final MP4 coded picture/metadata order or source bytes changed at picture " + n + ".");
        }
        check?.Invoke(); return pictures.Length;
    }
}
