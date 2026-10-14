using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace F10Recorder;

internal static class FramePacingChecks
{
    internal static int Run(string data, int seconds, int fps, string mode, bool audio, string variant, int ceiling, int sourceFps, string codec)
    {
        data = Path.GetFullPath(data); Directory.CreateDirectory(data); WindowsRecorder.Initialize(data);
        var report = new Dictionary<string, object> { ["sourceMode"] = mode, ["configuredFps"] = fps, ["configuredSourceFps"] = sourceFps, ["videoCodec"] = codec, ["systemAudio"] = audio, ["variant"] = variant };
        RecorderRuntime.CaptureArgumentProbe = args =>
        {
            int graph = args.IndexOf("-filter_complex");
            Require(graph >= 0 && args[graph + 1].EndsWith("[video]"), "Expected production desktop source graph.");
            if (variant is "legacy-input" or "small-read" or "low-probe" or "direct-audio" or "packet-audio" or "no-video-queue" or "debug-timestamps")
            {
                string source = args[graph + 1].Replace("[video]", "");
                args.RemoveRange(graph, 2);
                args.InsertRange(graph, ["-thread_queue_size", "512", "-f", "lavfi", "-i", source]);
                args[args.IndexOf("[video]")] = "0:v:0";
                if (audio) args[args.IndexOf("0:a:0")] = "1:a:0";
            }
            int audioInput = args.FindIndex(value => value.StartsWith(@"\\.\pipe\"));
            if (variant is "small-read" or "low-probe" or "direct-audio" or "packet-audio")
            {
                Require(audioInput > 0, "Audio variant requires real system audio.");
                var options = new List<string>();
                if (variant is "small-read" or "direct-audio") options.AddRange(["-blocksize", "4096"]);
                if (variant is "low-probe" or "direct-audio") options.AddRange(["-probesize", "32", "-analyzeduration", "0"]);
                if (variant == "direct-audio") options.AddRange(["-avioflags", "direct"]);
                if (variant == "packet-audio") options.AddRange(["-pkt_size", "4096", "-avioflags", "direct", "-probesize", "32", "-analyzeduration", "0"]);
                args.InsertRange(audioInput - 1, options);
            }
            else if (variant == "no-video-queue")
            {
                int queue = args.IndexOf("-thread_queue_size"); args.RemoveRange(queue, 2);
            }
            else if (variant is "filter-source" or "filter-headroom" or "filter-oversample" or "filter-fps" or "filter-fast" or "debug-timestamps")
            {
                if (variant == "debug-timestamps") { args[args.IndexOf("-loglevel") + 1] = "verbose"; args.InsertRange(0, ["-debug_ts", "-report"]); }
                else
                {
                    string source = args[graph + 1].Replace("[video]", "");
                    if (variant == "filter-oversample") { source = source.Replace("framerate=" + fps + ":", "framerate=" + (fps * 2) + ":"); args.InsertRange(args.Count - 1, ["-r", fps.ToString()]); }
                    if (variant == "filter-fast") { source = source.Replace("framerate=" + fps + ":", "framerate=1000:"); args.InsertRange(args.Count - 1, ["-r", fps.ToString()]); }
                    if (variant == "filter-fps") source = source.Replace("framerate=" + fps + ":", "framerate=" + (fps * 2) + ":") + ",fps=fps=" + fps + ":round=near";
                    if (variant == "filter-headroom") { Require(ceiling >= fps && ceiling <= 1000, "Invalid source ceiling."); source = source.Replace("framerate=" + fps + ":", "framerate=" + ceiling + ":"); args.InsertRange(args.Count - 1, ["-r", fps.ToString()]); }
                    args[graph + 1] = source + "[video]";
                }
            }
            else Require(variant is "baseline" or "legacy-input", "Unknown pacing variant.");
            report["captureArguments"] = args.ToArray(); return args;
        };
        Process fixture = null; CaptureSession capture = null;
        try
        {
            var info = new ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = false, CreateNoWindow = true };
            foreach (string value in new[] { "--pacing-fixture", data, sourceFps.ToString(), mode }) info.ArgumentList.Add(value);
            fixture = Process.Start(info);
            var wait = Stopwatch.StartNew();
            while (!File.Exists(Path.Combine(data, "pacing-ready.json")) && !fixture.HasExited && wait.Elapsed.TotalSeconds < 10) Thread.Sleep(100);
            Require(!fixture.HasExited && File.Exists(Path.Combine(data, "pacing-ready.json")), "Controlled frame source did not become ready.");
            var settings = new Settings { ReplayMinutes = 1, FramesPerSecond = fps, BitrateMbps = 20, VideoCodec = codec, SystemAudio = audio, OutputDirectory = Path.Combine(data, "clips") };
            report["mediaEngine"] = settings.FfmpegPath; report["fixture"] = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(data, "pacing-ready.json")));
            capture = new CaptureSession(settings, Path.Combine(data, "buffer"), false);
            wait.Restart(); var samples = new List<RecorderMetrics>();
            while (wait.Elapsed.TotalSeconds < seconds)
            {
                Require(!fixture.HasExited, "Controlled source closed during capture.");
                capture.Maintain(); Require(capture.Healthy, capture.ErrorTail); samples.Add(capture.Metrics); Thread.Sleep(200);
            }
            string clip = capture.Save(capture.Position, settings.OutputDirectory);
            report["clip"] = clip; report["samples"] = samples; report["finalMetrics"] = capture.Metrics;
            report["encoderErrorTail"] = capture.ErrorTail;
            if (variant == "debug-timestamps") report["timestampTrace"] = Directory.GetFiles(capture.DirectoryPath, "ffmpeg-*.log").Single();
            capture.Dispose(); capture = null;
            fixture.CloseMainWindow(); Require(fixture.WaitForExit(5000), "Source did not close cleanly.");
            report["analysis"] = Analyze(data, clip, (string)report["mediaEngine"], fps, audio);
            report["result"] = "measured";
        }
        catch (Exception ex) { report["result"] = "failed"; report["error"] = ex.ToString(); }
        finally
        {
            RecorderRuntime.CaptureArgumentProbe = null;
            capture?.Dispose();
            if (fixture != null) { if (!fixture.HasExited) fixture.Kill(); fixture.Dispose(); }
            File.WriteAllText(Path.Combine(data, "pacing.json"), JsonSerializer.Serialize(report, WorkerProtocol.Json));
        }
        return (string)report["result"] == "measured" ? 0 : 1;
    }
    static void Require(bool value, string message) { if (!value) throw new IOException(message); }
    static string RunTool(string executable, string directory, params string[] args)
    {
        using var process = CaptureSession.StartProcess(executable, args, directory);
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120000)) { process.Kill(entireProcessTree: true); process.WaitForExit(); throw new IOException("Media inspection timed out."); }
        string error = errors.GetAwaiter().GetResult();
        Require(process.ExitCode == 0 && string.IsNullOrWhiteSpace(error), "Media inspection failed: " + error); return output.GetAwaiter().GetResult();
    }
    static double Percentile(IEnumerable<double> values, double fraction)
    {
        var sorted = values.Order().ToArray(); return sorted.Length == 0 ? 0 : sorted[(int)Math.Ceiling((sorted.Length - 1) * fraction)];
    }
    static object Analyze(string data, string clip, string engine, int fps, bool audio)
    {
        string raw = Path.Combine(data, "barcode.gray");
        RunTool(engine, data, "-hide_banner", "-v", "error", "-y", "-i", clip, "-vf", "crop=768:64:32:32,scale=32:2:flags=neighbor,format=gray", "-fps_mode", "passthrough", "-pix_fmt", "gray", "-f", "rawvideo", raw);
        var bytes = File.ReadAllBytes(raw); Require(bytes.Length % 64 == 0, "Barcode frame size is invalid.");
        var ids = new List<int>(); int invalid = 0;
        for (int offset = 0; offset < bytes.Length; offset += 64)
        {
            uint word = 0; bool valid = true;
            for (int bit = 0; bit < 32; bit++)
            {
                bool value = bytes[offset + bit] > 128;
                if (value == (bytes[offset + 32 + bit] > 128)) valid = false;
                if (value) word |= 1u << bit;
            }
            uint id = word & 0xFFFFFF, checksum = ((id & 255) ^ ((id >> 8) & 255) ^ ((id >> 16) & 255) ^ 0xA7) & 255;
            if (!valid || checksum != word >> 24) invalid++;
            ids.Add(valid && checksum == word >> 24 ? (int)id : -1);
        }
        File.WriteAllLines(Path.Combine(data, "decoded-frame-ids.csv"), new[] { "output_frame,source_id" }.Concat(ids.Select((id, index) => $"{index},{id}")));
        Require(invalid == 0 && ids.Count > fps * 3, "Source IDs did not decode reliably.");
        var rendered = File.ReadLines(Path.Combine(data, "render.csv")).Skip(1).Select(line => line.Split(',')).ToDictionary(row => int.Parse(row[0]), row => long.Parse(row[2]));
        using var ready = JsonDocument.Parse(File.ReadAllText(Path.Combine(data, "pacing-ready.json")));
        double frequency = ready.RootElement.GetProperty("qpc_frequency").GetDouble();
        int requestedSourceFps = ready.RootElement.GetProperty("fps").GetInt32();
        var source = rendered.Where(pair => pair.Key >= ids.Min() && pair.Key <= ids.Max()).OrderBy(pair => pair.Key).ToArray();
        Require(source.Length > 2 && ids.All(id => rendered.ContainsKey(id)), "Captured IDs are missing from the compositor-flush log.");
        var intervals = source.Skip(1).Select((pair, index) => (pair.Value - source[index].Value) * 1000 / frequency).ToArray();
        int repeated = 0, backwards = 0, skipped = 0, run = 1; var holds = new List<double>();
        for (int index = 1; index < ids.Count; index++)
        {
            if (ids[index] == ids[index - 1]) { repeated++; run++; }
            else { holds.Add(run * 1000d / fps); run = 1; if (ids[index] < ids[index - 1]) backwards++; else skipped += ids[index] - ids[index - 1] - 1; }
        }
        holds.Add(run * 1000d / fps);
        string probe = Path.Combine(Path.GetDirectoryName(engine), "ffprobe.exe");
        RunTool(engine, data, "-hide_banner", "-v", "error", "-xerror", "-i", clip, "-map", "0", "-fps_mode", "passthrough", "-f", "null", "-");
        string streamJson = RunTool(probe, data, "-v", "error", "-show_entries", "format=duration:stream=codec_type,codec_name,start_time,duration,r_frame_rate", "-of", "json", clip);
        File.WriteAllText(Path.Combine(data, "output-streams.json"), streamJson);
        using var streams = JsonDocument.Parse(streamJson);
        var videoStreams = streams.RootElement.GetProperty("streams").EnumerateArray().Where(s => s.GetProperty("codec_type").GetString() == "video").ToArray();
        var audioStreams = streams.RootElement.GetProperty("streams").EnumerateArray().Where(s => s.GetProperty("codec_type").GetString() == "audio").ToArray();
        Require(videoStreams.Length == 1 && audioStreams.Length == (audio ? 1 : 0), "Unexpected video/audio stream count.");
        double startSkew = 0, endSkew = 0;
        if (audio)
        {
            double Number(JsonElement s, string name) => double.Parse(s.GetProperty(name).GetString(), CultureInfo.InvariantCulture);
            startSkew = Math.Abs(Number(videoStreams[0], "start_time") - Number(audioStreams[0], "start_time"));
            endSkew = Math.Abs(Number(videoStreams[0], "start_time") + Number(videoStreams[0], "duration") - Number(audioStreams[0], "start_time") - Number(audioStreams[0], "duration"));
            Require(startSkew <= .15 && endSkew <= .15, "Audio/video timestamp boundary skew exceeds 150ms.");
        }
        string timestampJson = RunTool(probe, data, "-v", "error", "-select_streams", "v:0", "-show_frames", "-show_entries", "frame=best_effort_timestamp_time", "-of", "json", clip);
        File.WriteAllText(Path.Combine(data, "output-timestamps.json"), timestampJson);
        using var timestamps = JsonDocument.Parse(timestampJson);
        var times = timestamps.RootElement.GetProperty("frames").EnumerateArray().Select(f => double.Parse(f.GetProperty("best_effort_timestamp_time").GetString(), CultureInfo.InvariantCulture)).ToArray();
        Require(times.Length == ids.Count, "Frame IDs and output timestamps do not align.");
        var deltas = times.Skip(1).Select((time, index) => (time - times[index]) * 1000).ToArray();
        Require(Math.Abs(Percentile(deltas, .5) - 1000d / fps) < .25, "Output frame rate differs from the configured rate.");
        double sourceFps = (source.Length - 1) * frequency / (source.Last().Value - source.First().Value);
        double uniqueFps = ids.Distinct().Count() / (times.Last() - times.First() + 1d / fps);
        double repeatPercent = repeated * 100d / ids.Count;
        bool sourceQualified = sourceFps >= requestedSourceFps * .98 && requestedSourceFps >= fps && Percentile(intervals, .95) <= 1500d / requestedSourceFps;
        bool capturePassed = backwards == 0 && deltas.All(d => d > 0) && repeatPercent <= 5 && uniqueFps >= fps * .95 && Percentile(holds, .95) <= 2000d / fps;
        return new
        {
            acceptance = sourceQualified ? (capturePassed ? "passed" : "failed") : "inconclusive-source",
            sourceQualified,
            thresholds = new { sourceMinimumFps = requestedSourceFps * .98, sourceP95MaximumMs = 1500d / requestedSourceFps, repeatMaximumPercent = 5, uniqueMinimumFps = fps * .95, motionHoldP95MaximumMs = 2000d / fps, timestampBoundaryMaximumSeconds = .15 },
            fullDecode = "passed", startBoundarySkewSeconds = audio ? (double?)startSkew : null, endBoundarySkewSeconds = audio ? (double?)endSkew : null,
            decodedFrames = ids.Count, invalidBarcodeFrames = invalid, backwardsIds = backwards,
            sourceFramesInCapturedRange = source.Length, distinctCapturedIds = ids.Distinct().Count(), omittedSourceIds = skipped,
            consecutiveRepeatedIds = repeated, repeatedOutputPercent = repeatPercent,
            observedSourceFps = sourceFps,
            uniqueOutputFps = uniqueFps,
            sourceIntervalP50Ms = Percentile(intervals, .5), sourceIntervalP95Ms = Percentile(intervals, .95),
            motionHoldP50Ms = Percentile(holds, .5), motionHoldP95Ms = Percentile(holds, .95), motionHoldMaxMs = holds.Max(),
            outputIntervalP50Ms = Percentile(deltas, .5), outputIntervalP95Ms = Percentile(deltas, .95),
            nonIncreasingOutputTimestamps = deltas.Count(delta => delta <= 0),
            meaning = "Source log records GDI paints followed by DwmFlush, not instrumented game presents. Repeated IDs prove repeated controlled imagery; omitted IDs are between captured endpoints and are not automatically a pipeline fault when clocks differ."
        };
    }
}
