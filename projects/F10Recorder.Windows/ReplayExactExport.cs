using System.Globalization;
using System.Text.RegularExpressions;

namespace F10Recorder;

internal sealed record ReplayValidation(string Kind, long Frames, double Milliseconds);

// Correctness-first candidate from the precision experiment: decode the closed
// input interval, trim actual pictures/samples, then encode a new independent MP4.
// No edit-list preroll, synthetic history or altered duration metadata is used.
internal static class ReplayExactExport
{
    internal const string Recipe = "replay-exact-reencode-mp4-v1";
    const double TimestampTolerance = 1.0 / 48000 + 0.000001;
    static string Number(double value) => value.ToString("0.#########", CultureInfo.InvariantCulture);
    internal static List<string> Arguments(ReplaySessionInfo info, ReplayInterval interval,
        double inputStart, string concat, string partial)
    {
        double start = interval.Start - inputStart, end = interval.End - inputStart;
        var args = new List<string> { "-hide_banner", "-loglevel", "warning", "-xerror", "-y", "-copyts",
            "-f", "concat", "-safe", "1", "-protocol_whitelist", "file", "-segment_time_metadata", "1", "-i", concat,
            "-map", "0:v:0", "-vf", "select=concatdec_select,settb=1/48000,setpts=round((PTS*TB+" + Number(inputStart) + ")*"
                + info.FramesPerSecond + ")*48000/" + info.FramesPerSecond + ",trim=start=" + Number(interval.Start) + ":end=" + Number(interval.End)
                + ",setpts=PTS-" + Number(interval.Start) + "/TB",
            "-fps_mode", "passthrough", "-enc_time_base:v", "1:48000", "-video_track_timescale", "48000",
            "-c:v", info.Codec, "-preset", "p3", "-tune", "ll", "-rc", "cbr",
            "-b:v", info.BitrateMbps + "M", "-maxrate", info.BitrateMbps + "M", "-bufsize", (info.BitrateMbps * 2) + "M",
            "-g", info.FramesPerSecond.ToString(CultureInfo.InvariantCulture), "-bf", "0" };
        if (info.RecordAudio) args.AddRange(["-map", "0:a:0", "-af",
            "aselect=concatdec_select,atrim=start=" + Number(start) + ":end=" + Number(end) + ",asetpts=PTS-" + Number(start) + "/TB",
            "-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-ac", "2"]);
        else args.Add("-an");
        if (info.Codec == "hevc_nvenc") args.AddRange(["-tag:v", "hvc1"]);
        args.AddRange(["-movflags", "+faststart", partial]);
        return args;
    }

    internal static IReadOnlyList<string> DecodeArguments(string path, bool audio, int fps) => [
        "-hide_banner", "-loglevel", "info", "-xerror", "-err_detect", "explode", "-copyts",
        "-progress", "pipe:1", "-nostats", "-i", path,
        "-filter_complex", "[0:v:0]split[full][timing];[timing]select='eq(n,0)+gt(abs((pts-prev_pts)*TB-"
            + Number(1.0 / fps) + "),0.0011)+gt(abs((pts-start_pts)*TB-n/" + fps
            + "),0.000022)',showinfo,nullsink", "-map", "[full]",
        .. (audio ? new[] { "-map", "0:a:0" } : new[] { "-an" }),
        "-fps_mode", "passthrough", "-f", "null", "-" ];

    internal static ReplayValidation ReadValidation(ReplayExportProcessReceipt receipt, ReplayInterval interval, int fps)
    {
        var frames = Regex.Matches(receipt.StandardOutput, @"(?m)^frame=(\d+)\s*$");
        var timing = Regex.Matches(receipt.StandardError, @"\bn:\s*\d+\s+pts:\s*-?\d+\s+pts_time:([-0-9.e+]+)");
        if (!receipt.Success || !receipt.StandardOutput.Contains("progress=end") || frames.Count == 0
            || !long.TryParse(frames[^1].Groups[1].Value, out long count) || count < fps
            || Math.Abs(count - interval.Seconds * fps) > 1.000001 || timing.Count != 1
            || !double.TryParse(timing[0].Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double first)
            || first < -TimestampTolerance || first > 1.0 / fps + TimestampTolerance
            || Math.Abs(first + count / (double)fps - interval.Seconds) > 1.0 / fps + TimestampTolerance)
            // The complete receipt remains on CaptureSession. Public errors also
            // enter the worker's bounded event history and JSON control reply.
            //1024chars keeps even64 worst-case escaped events below the wire cap.
            throw new IOException(ReplayOutputDemand.Bounded("Replay MP4 failed full decode or frame-duration validation: "
                + receipt.Error + " " + receipt.StandardError, 1024));
        return new("FullDecode", count, receipt.ElapsedMilliseconds);
    }

    // Finite deadline scales with the selected workload; it is not a latency claim.
    internal static int Deadline(ReplayInterval interval) => checked((int)Math.Min(3_600_000,
        Math.Max(120_000, Math.Ceiling(interval.Seconds * 2000))));
}
