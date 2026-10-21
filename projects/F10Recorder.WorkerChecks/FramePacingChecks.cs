using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace F10Recorder;

internal static class FramePacingChecks
{
    static bool SameTrace(string original, string retained)
    {
        if (!File.Exists(retained)) return false;
        using var left = File.OpenRead(original); using var right = File.OpenRead(retained);
        return System.Security.Cryptography.SHA256.HashData(left).SequenceEqual(System.Security.Cryptography.SHA256.HashData(right));
    }
    sealed class EvidenceFiles(string evidence, Dictionary<string, object> report) : ReplayFiles
    {
        internal override void DeleteDirectory(string path)
        {
            if (Directory.Exists(path))
                foreach (string trace in Directory.GetFiles(path, "ffmpeg-*.log"))
                    if (!SameTrace(trace, Path.Combine(evidence, Path.GetFileName(trace))))
                    {
                        report["retainedSessionDiagnostics"] = path;
                        throw new IOException("Uncopied or unverified diagnostic trace retained in " + path);
                    }
            base.DeleteDirectory(path);
        }
    }
    internal static int Run(string data, int seconds, int fps, string mode, bool audio, string variant, int ceiling, int sourceFps, string codec)
    {
        data = Path.GetFullPath(data); Directory.CreateDirectory(data); RecorderHost.Initialize(data);
        var report = new Dictionary<string, object> { ["sourceMode"] = mode, ["configuredFps"] = fps, ["configuredSourceFps"] = sourceFps, ["videoCodec"] = codec, ["systemAudio"] = audio, ["variant"] = variant };
        RecorderRuntime.CaptureArgumentProbe = args =>
        {
            int graph = args.IndexOf("-filter_complex");
            Require(graph >= 0 && args[graph + 1].EndsWith("[video]"), "Expected production desktop source graph.");
            // Reproduce the pre-fix policy before applying diagnostic overrides.
            // baseline/trace-production retain the shipping argument list exactly.
            if (variant is not "baseline" and not "trace-production")
            {
                args[graph + 1] = Regex.Replace(args[graph + 1], @"framerate=\d+", "framerate=" + fps);
                args[graph + 1] = Regex.Replace(args[graph + 1], @",fps=fps=\d+:round=near", "");
                foreach (string option in new[] { "-r", "-fps_mode" })
                {
                    int index = args.IndexOf(option); if (index >= 0) args.RemoveRange(index, 2);
                }
            }
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
            else if (variant is "wgc" or "wgc-native")
            {
                int rate = variant == "wgc" ? fps * 4 : fps;
                args[graph + 1] = "gfxcapture=monitor_idx=0:max_framerate=" + rate + ":display_border=0:capture_cursor=1,fps=fps=" + fps + ":round=near[video]";
            }
            else if (variant == "output-clock-only") args.InsertRange(args.Count - 1, ["-r", fps.ToString(), "-fps_mode", "cfr"]);
            else if (variant == "output-only")
            {
                args[graph + 1] = args[graph + 1].Replace("framerate=" + fps + ":", "framerate=" + (fps * 4) + ":");
                args.InsertRange(args.Count - 1, ["-r", fps.ToString(), "-fps_mode", "cfr"]);
            }
            else if (variant is "threads-one" or "event-source" or "trace-production")
            {
                if (variant == "threads-one") args.InsertRange(0, ["-filter_complex_threads", "1"]);
                if (variant == "event-source") args[graph + 1] = args[graph + 1].Replace(":draw_mouse=1", ":draw_mouse=1:dup_frames=0");
                if (variant == "trace-production") { args[args.IndexOf("-loglevel") + 1] = "verbose"; args.InsertRange(0, ["-debug_ts", "-report"]); }
            }
            else if (variant is "filter-source" or "filter-headroom" or "filter-oversample" or "filter-fps" or "filter-fast" or "debug-timestamps")
            {
                if (variant == "debug-timestamps") { args[args.IndexOf("-loglevel") + 1] = "verbose"; args.InsertRange(0, ["-debug_ts", "-report"]); }
                else
                {
                    string source = args[graph + 1].Replace("[video]", "");
                    if (variant == "filter-oversample") { source = source.Replace("framerate=" + fps + ":", "framerate=" + (fps * 2) + ":"); args.InsertRange(args.Count - 1, ["-r", fps.ToString()]); }
                    if (variant == "filter-fast") { source = source.Replace("framerate=" + fps + ":", "framerate=1000:"); args.InsertRange(args.Count - 1, ["-r", fps.ToString()]); }
                    if (variant == "filter-fps")
                    {
                        int rate = ceiling == 0 ? fps * 2 : ceiling;
                        Require(rate >= fps && rate <= 1000, "Invalid source ceiling.");
                        source = source.Replace("framerate=" + fps + ":", "framerate=" + rate + ":") + ",fps=fps=" + fps + ":round=near";
                        args.InsertRange(args.Count - 1, ["-r", fps.ToString(), "-fps_mode", "cfr"]);
                    }
                    if (variant == "filter-headroom") { Require(ceiling >= fps && ceiling <= 1000, "Invalid source ceiling."); source = source.Replace("framerate=" + fps + ":", "framerate=" + ceiling + ":"); args.InsertRange(args.Count - 1, ["-r", fps.ToString()]); }
                    args[graph + 1] = source + "[video]";
                }
            }
            else Require(variant is "baseline" or "before-policy" or "legacy-input", "Unknown pacing variant.");
            report["captureArguments"] = args.ToArray(); return args;
        };
        Process fixture = null; CaptureSession capture = null;
        var replays = new ReplayCache(new EvidenceFiles(data, report)); ReplaySessionId sessionId = default;
        RecorderMetrics CaptureMetrics()
        {
            var metrics = capture.Metrics; var buffer = replays.GetStatus(sessionId);
            metrics.BufferedSeconds = buffer.BufferedSeconds; metrics.CacheBytes = buffer.CacheBytes;
            return metrics;
        }
        void RetainDiagnostics()
        {
            if (capture == null || variant is not ("debug-timestamps" or "trace-production")) return;
            replays.StopAcquisitionAsync(sessionId).GetAwaiter().GetResult();
            foreach (string trace in Directory.GetFiles(capture.DirectoryPath, "ffmpeg-*.log"))
            {
                string retained = Path.Combine(data, Path.GetFileName(trace));
                if (!File.Exists(retained)) File.Copy(trace, retained);
                Require(SameTrace(trace, retained), "Retained diagnostic trace differs from the original.");
                report["timestampTrace"] = retained;
            }
        }
        try
        {
            var info = new ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = false, CreateNoWindow = true };
            foreach (string value in new[] { "--pacing-fixture", data, sourceFps.ToString(), mode }) info.ArgumentList.Add(value);
            fixture = Process.Start(info);
            var wait = Stopwatch.StartNew();
            while (!File.Exists(Path.Combine(data, "pacing-ready.json")) && !fixture.HasExited && wait.Elapsed.TotalSeconds < 10) Thread.Sleep(100);
            Require(!fixture.HasExited && File.Exists(Path.Combine(data, "pacing-ready.json")), "Controlled frame source did not become ready.");
            var settings = new Settings { ReplayMinutes = 1, FramesPerSecond = fps, BitrateMbps = 20, VideoCodec = codec, SystemAudio = audio, OutputDirectory = Path.Combine(data, "clips") };
            report["mediaEngine"] = MediaDependency.ResolveExecutable(); report["fixture"] = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(data, "pacing-ready.json")));
            capture = new CaptureSession(settings, Path.Combine(data, "buffer"), false);
            sessionId = replays.RegisterSession(capture);
            wait.Restart(); var samples = new List<RecorderMetrics>();
            while (wait.Elapsed.TotalSeconds < seconds)
            {
                Require(!fixture.HasExited, "Controlled source closed during capture.");
                replays.Maintain(sessionId); Require(capture.Healthy, capture.ErrorTail); samples.Add(CaptureMetrics()); Thread.Sleep(200);
            }
            var save = replays.TrySave(sessionId, Stopwatch.GetTimestamp(), settings.OutputDirectory);
            Require(save.Accepted, save.Rejection); var result = save.Completion.GetAwaiter().GetResult();
            Require(result.Succeeded, result.Error); string clip = result.File;
            report["clip"] = clip; report["samples"] = samples; report["finalMetrics"] = CaptureMetrics();
            report["encoderErrorTail"] = capture.ErrorTail;
            RetainDiagnostics();
            replays.RetireSessionAsync(sessionId).GetAwaiter().GetResult(); capture = null;
            fixture.CloseMainWindow(); Require(fixture.WaitForExit(5000), "Source did not close cleanly.");
            if (mode == "dxgi-static")
            {
                var media = InspectMedia(data, clip, (string)report["mediaEngine"], audio, fps);
                Require(media.Duration >= seconds - 3, "Static desktop video failed to advance for the capture duration.");
                report["analysis"] = new { acceptance = "passed-static-health", fullDecode = "passed", videoDurationSeconds = media.Duration, startBoundarySkewSeconds = media.Start, endBoundarySkewSeconds = media.End, meaning = "GPU fixture stopped presenting after one second. Capture health and completed segments must continue with unchanged imagery." };
            }
            else report["analysis"] = Analyze(data, clip, (string)report["mediaEngine"], fps, audio);
            report["result"] = "measured";
        }
        catch (Exception ex) { report["result"] = "failed"; report["error"] = ex.ToString(); }
        finally
        {
            RecorderRuntime.CaptureArgumentProbe = null;
            try { RetainDiagnostics(); } catch (Exception ex) { report["traceRetentionError"] = ex.ToString(); }
            replays.Dispose();
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
    static (double? Start, double? End, double Duration) InspectMedia(string data, string clip, string engine, bool audio, int fps)
    {
        string probe = Path.Combine(Path.GetDirectoryName(engine), "ffprobe.exe");
        RunTool(engine, data, "-hide_banner", "-v", "error", "-xerror", "-i", clip, "-map", "0", "-fps_mode", "passthrough", "-f", "null", "-");
        string streamJson = RunTool(probe, data, "-v", "error", "-show_entries", "format=duration:stream=codec_type,codec_name,start_time,duration,r_frame_rate", "-of", "json", clip);
        File.WriteAllText(Path.Combine(data, "output-streams.json"), streamJson);
        using var streams = JsonDocument.Parse(streamJson);
        var videoStreams = streams.RootElement.GetProperty("streams").EnumerateArray().Where(s => s.GetProperty("codec_type").GetString() == "video").ToArray();
        var audioStreams = streams.RootElement.GetProperty("streams").EnumerateArray().Where(s => s.GetProperty("codec_type").GetString() == "audio").ToArray();
        Require(videoStreams.Length == 1 && audioStreams.Length == (audio ? 1 : 0), "Unexpected video/audio stream count.");
        double Number(JsonElement s, string name) => double.Parse(s.GetProperty(name).GetString(), CultureInfo.InvariantCulture);
        var rate = videoStreams[0].GetProperty("r_frame_rate").GetString().Split('/');
        Require(Math.Abs(double.Parse(rate[0], CultureInfo.InvariantCulture) / double.Parse(rate[1], CultureInfo.InvariantCulture) - fps) < .001, "Encoded frame rate differs from the configured rate.");
        double startSkew = 0, endSkew = 0;
        if (audio)
        {
            startSkew = Math.Abs(Number(videoStreams[0], "start_time") - Number(audioStreams[0], "start_time"));
            endSkew = Math.Abs(Number(videoStreams[0], "start_time") + Number(videoStreams[0], "duration") - Number(audioStreams[0], "start_time") - Number(audioStreams[0], "duration"));
            Require(startSkew <= .15 && endSkew <= .15, "Audio/video timestamp boundary skew exceeds 150ms.");
        }
        return (audio ? startSkew : null, audio ? endSkew : null, Number(videoStreams[0], "duration"));
    }
    internal static object Analyze(string data, string clip, string engine, int fps, bool audio)
    {
        // Preserve the decoded evidence for every saved window in a long soak.
        string evidence = Path.Combine(data, "pacing-analysis", Path.GetFileNameWithoutExtension(clip));
        Directory.CreateDirectory(evidence);
        string raw = Path.Combine(evidence, "barcode.gray");
        RunTool(engine, evidence, "-hide_banner", "-v", "error", "-y", "-i", clip, "-vf", "crop=768:64:32:32,scale=32:2:flags=neighbor,format=gray", "-fps_mode", "passthrough", "-pix_fmt", "gray", "-f", "rawvideo", raw);
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
        File.WriteAllLines(Path.Combine(evidence, "decoded-frame-ids.csv"), new[] { "output_frame,source_id" }.Concat(ids.Select((id, index) => $"{index},{id}")));
        Require(invalid == 0 && ids.Count > fps * 3, "Source IDs did not decode reliably.");
        int minId = ids.Min(), maxId = ids.Max();
        var rendered = File.ReadLines(Path.Combine(data, "render.csv")).Skip(1).Select(line => line.Split(',')).ToDictionary(row => int.Parse(row[0]), row => long.Parse(row[2]));
        using var ready = JsonDocument.Parse(File.ReadAllText(Path.Combine(data, "pacing-ready.json")));
        double frequency = ready.RootElement.GetProperty("qpc_frequency").GetDouble();
        int requestedSourceFps = ready.RootElement.GetProperty("fps").GetInt32();
        var source = rendered.Where(pair => pair.Key >= minId && pair.Key <= maxId).OrderBy(pair => pair.Key).ToArray();
        Require(source.Length > 2 && ids.All(id => rendered.ContainsKey(id)), "Captured IDs are missing from the source commit log.");
        var intervals = source.Skip(1).Select((pair, index) => (pair.Value - source[index].Value) * 1000 / frequency).ToArray();
        var referenceRepeats = new List<double>();
        for (int phase = 0; phase < 100; phase++)
        {
            var bins = source.Select(pair => (long)Math.Floor((pair.Value - source[0].Value) * fps / frequency + phase / 100d + .5)).ToArray();
            long count = bins.Last() - bins.First() + 1;
            referenceRepeats.Add((count - bins.Distinct().Count()) * 100d / count);
        }
        int repeated = 0, backwards = 0, skipped = 0, run = 1; var holds = new List<double>();
        for (int index = 1; index < ids.Count; index++)
        {
            if (ids[index] == ids[index - 1]) { repeated++; run++; }
            else { holds.Add(run * 1000d / fps); run = 1; if (ids[index] < ids[index - 1]) backwards++; else skipped += ids[index] - ids[index - 1] - 1; }
        }
        holds.Add(run * 1000d / fps);
        string probe = Path.Combine(Path.GetDirectoryName(engine), "ffprobe.exe");
        var media = InspectMedia(evidence, clip, engine, audio, fps);
        string timestampJson = RunTool(probe, evidence, "-v", "error", "-select_streams", "v:0", "-show_frames", "-show_entries", "frame=best_effort_timestamp_time", "-of", "json", clip);
        File.WriteAllText(Path.Combine(evidence, "output-timestamps.json"), timestampJson);
        using var timestamps = JsonDocument.Parse(timestampJson);
        var times = timestamps.RootElement.GetProperty("frames").EnumerateArray().Select(f => double.Parse(f.GetProperty("best_effort_timestamp_time").GetString(), CultureInfo.InvariantCulture)).ToArray();
        Require(times.Length == ids.Count, "Frame IDs and output timestamps do not align.");
        var deltas = times.Skip(1).Select((time, index) => (time - times[index]) * 1000).ToArray();
        Require(Math.Abs(Percentile(deltas, .5) - 1000d / fps) < .25, "Output frame rate differs from the configured rate.");
        double sourceFps = (source.Length - 1) * frequency / (source.Last().Value - source.First().Value);
        double uniqueFps = ids.Distinct().Count() / (times.Last() - times.First() + 1d / fps);
        double repeatPercent = repeated * 100d / ids.Count;
        object presentAudit = null; bool acknowledged = true;
        if (ready.RootElement.GetProperty("mode").GetString() == "dxgi")
        {
            var presents = File.ReadLines(Path.Combine(data, "dxgi-present.csv")).Skip(1).Select(line => line.Split(','))
                .Where(row => int.Parse(row[2]) == 0 && long.Parse(row[3]) >= minId && long.Parse(row[3]) <= maxId && long.Parse(row[6]) > 0)
                .Select(row => new { Count = long.Parse(row[3]), Qpc = long.Parse(row[6]) }).GroupBy(row => row.Count).Select(group => group.First()).OrderBy(row => row.Count).ToArray();
            double ackRate = presents.Length > 1 ? (presents.Last().Count - presents.First().Count) * frequency / (presents.Last().Qpc - presents.First().Qpc) : 0;
            double coverage = presents.Length * 100d / source.Length;
            acknowledged = ackRate >= requestedSourceFps * .98 && coverage >= 95;
            presentAudit = new { acknowledgedPresentFps = ackRate, acknowledgedCountCoveragePercent = coverage, acknowledgedPresentCount = presents.Length, qualified = acknowledged, meaning = "DXGI GetFrameStatistics PresentCount and SyncQPCTime corroborate displayed source cadence. Zero/uninitialized statistics are excluded. This is a controlled swap chain, not a real game's benchmark." };
        }
        bool sourceQualified = acknowledged && sourceFps >= requestedSourceFps * .98 && requestedSourceFps >= fps && Percentile(intervals, .95) <= 1100d / requestedSourceFps;
        bool capturePassed = backwards == 0 && deltas.All(d => d > 0) && repeatPercent <= 5 && uniqueFps >= fps * .95 && Percentile(holds, .95) <= 2000d / fps;
        return new
        {
            acceptance = sourceQualified ? (capturePassed ? "passed" : "failed") : "inconclusive-source",
            sourceQualified,
            validationVersion = 2, displayPresentation = presentAudit,
            evidenceDirectory = evidence,
            thresholds = new { sourceMinimumFps = requestedSourceFps * .98, sourceP95MaximumMs = 1100d / requestedSourceFps, repeatMaximumPercent = 5, uniqueMinimumFps = fps * .95, motionHoldP95MaximumMs = 2000d / fps, timestampBoundaryMaximumSeconds = .15 },
            fullDecode = "passed", startBoundarySkewSeconds = media.Start, endBoundarySkewSeconds = media.End,
            idealCfrRepeatMinimumPercent = referenceRepeats.Min(), idealCfrRepeatMaximumPercent = referenceRepeats.Max(),
            decodedFrames = ids.Count, invalidBarcodeFrames = invalid, backwardsIds = backwards,
            sourceFramesInCapturedRange = source.Length, distinctCapturedIds = ids.Distinct().Count(), omittedSourceIds = skipped,
            consecutiveRepeatedIds = repeated, repeatedOutputPercent = repeatPercent,
            observedSourceFps = sourceFps,
            uniqueOutputFps = uniqueFps,
            sourceIntervalP50Ms = Percentile(intervals, .5), sourceIntervalP95Ms = Percentile(intervals, .95),
            motionHoldP50Ms = Percentile(holds, .5), motionHoldP95Ms = Percentile(holds, .95), motionHoldMaxMs = holds.Max(),
            outputIntervalP50Ms = Percentile(deltas, .5), outputIntervalP95Ms = Percentile(deltas, .95),
            nonIncreasingOutputTimestamps = deltas.Count(delta => delta <= 0),
            meaning = "Source commit log is GDI/DwmFlush or DXGI Present return depending on fixture mode. DXGI frame statistics are saved separately. The ideal CFR repeat range resamples the logged commits over 100 clock phases; it illustrates source/clock aliasing, not exact visible GDI frame times. Repeated IDs prove repeated controlled imagery; omissions alone do not establish capture faults."
        };
    }
}
