using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace F10Recorder;

// Finite generated-picture checks through ReplayCache, its real snapshot and
// publication path, and the production export/validation commands. No devices.
internal static class ReplayPrecisionChecks
{
    static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    static void Require(bool value, string message) { if (!value) throw new IOException(message); }
    static string Number(double value) => value.ToString("0.#########", CultureInfo.InvariantCulture);

    sealed class Source(string directory, string codec, int fps, int seconds,
        Func<IReadOnlyList<string>, ReplayExportProcessReceipt> run, bool audio = true, bool excludeInitial = false) : IReplaySessionSource
    {
        public ReplaySessionInfo ReplayInfo { get; } = new(directory, Path.Combine(directory, "segments.csv"), seconds, 5, fps,
            codec, codec == "av1_nvenc" ? ".mkv" : ".ts", excludeInitial, audio);
        public bool Stopped => true;
        public string Diagnostics => "Generated absolute picture IDs and synthetic tone; no live capture";
        bool producerAbsent = true;
        public bool OutputProducerStopped => producerAbsent;
        public double SaveCutoffAt(long trigger) => trigger / 1_000_000.0;
        public void FreezeAt(long timestamp) { }
        public void StopCapture() { }
        public void CheckCaptureHealth(double end) { }
        ReplayExportProcessReceipt Invoke(IReadOnlyList<string> arguments)
        {
            producerAbsent = false;
            var receipt = run(arguments);
            producerAbsent = receipt.ProducerAbsent;
            return receipt;
        }
        public void Export(IReadOnlyList<string> arguments, string snapshot) => _ = Invoke(arguments);
        public ReplayValidation ValidateOutput(string path, ReplayInterval interval, Action<ReplayProducerProof> changed, Action check)
            => ReplayExactExport.ReadValidation(Invoke(ReplayExactExport.DecodeArguments(path, audio, fps)), interval, fps);
        public void Dispose() { } // Route owns completed source files and job.
    }

    internal static int Run(string planFile, string planHash)
    {
        Require(new FileInfo(planFile).Length <= 65536 && Hash(planFile) == planHash, "Precision plan changed.");
        using var document = JsonDocument.Parse(File.ReadAllText(planFile)); var plan = document.RootElement;
        string mode = plan.GetProperty("mode").GetString();
        Require(mode is "short" or "windows" or "rates" or "clock" or "integration" or "corrupt" or "drift" && plan.GetProperty("kind").GetString() == "issue14-generated-precision-v1", "Unknown precision plan.");
        foreach (var f in plan.GetProperty("files").EnumerateObject()) Require(Hash(f.Name) == f.Value.GetString(), "Precision input changed: " + f.Name);
        Require(plan.GetProperty("files").TryGetProperty(Path.GetFullPath(typeof(ReplayPrecisionChecks).Assembly.Location), out _)
            && plan.GetProperty("files").TryGetProperty(Path.GetFullPath(typeof(CaptureSession).Assembly.Location), out _)
            && plan.GetProperty("files").TryGetProperty(Path.GetFullPath(typeof(Settings).Assembly.Location), out _), "All consumer assemblies must be bound.");
        string root = Path.GetFullPath(plan.GetProperty("root").GetString());
        string accountingRoot = Path.GetFullPath(plan.GetProperty("accountingRoot").GetString());
        Require(accountingRoot == Path.GetDirectoryName(root), "Precision accounting must include retained sibling attempts.");
        Require(!Directory.Exists(root) && !File.Exists(root), "Precision checks require a fresh evidence root.");
        MediaDependency.RootDirectory = plan.GetProperty("engineRoot").GetString();
        string engine = MediaDependency.ResolveExecutable();
        Require(plan.GetProperty("files").TryGetProperty(Path.GetFullPath(engine), out _), "Managed engine must be bound.");
        Require(plan.GetProperty("budgetBytes").GetInt64() == 8_000_000_000L && plan.GetProperty("minimumFreeBytes").GetInt64() == 20_000_000_000L
            && plan.GetProperty("maximumChildren").GetInt32() == (mode == "drift" ? 2 : mode is "integration" or "corrupt" ? 3 : mode == "windows" ? 19 : mode == "rates" ? 117 : mode == "clock" ? 26 : 45)
            && plan.GetProperty("deadlineSeconds").GetInt32() == (mode is "integration" or "corrupt" or "drift" ? 45 : mode == "windows" ? 1800 : 600), "Precision resource boundary changed.");
        Directory.CreateDirectory(root); Log.DirectoryPath = root;
        var timer = Stopwatch.StartNew(); var children = new List<ReplayExportProcessReceipt>(); var results = new List<object>();
        using var job = new Native.ProcessJob();
        int deadline = plan.GetProperty("deadlineSeconds").GetInt32(), maximum = plan.GetProperty("maximumChildren").GetInt32();
        void Guard()
        {
            Require(timer.Elapsed.TotalSeconds < deadline, "Precision whole-run deadline exceeded.");
            Require(Directory.EnumerateFiles(accountingRoot, "*", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length) < 8_000_000_000L,
                "Precision evidence budget exceeded.");
            Require(ReplayStorage.ObserveCapacity(root).AvailableBytes >= 20_000_000_000L, "Precision caller-free floor reached.");
        }
        if (mode is "integration" or "corrupt") return ReplayIntegrationChecks.Run(root, Guard, mode == "corrupt");
        ReplayExportProcessReceipt Run(IReadOnlyList<string> arguments) => RunCore(arguments, true);
        ReplayExportProcessReceipt RunCore(IReadOnlyList<string> arguments, bool expectedSuccess)
        {
            Guard(); Require(children.Count < maximum, "Precision child ceiling exceeded.");
            File.WriteAllText(Path.Combine(root, "child-" + children.Count.ToString("D2") + "-arguments.json"), JsonSerializer.Serialize(arguments));
            var receipt = ReplayExportProcess.Run(engine, arguments, root, job,
                timeoutMilliseconds: (int)Math.Min(240_000, (deadline - timer.Elapsed.TotalSeconds) * 1000), checkStorage: Guard);
            children.Add(receipt);
            File.WriteAllText(Path.Combine(root, "children.json"), JsonSerializer.Serialize(children, new JsonSerializerOptions { WriteIndented = true }));
            Require(receipt.Success == expectedSuccess && receipt.ProducerAbsent, "Precision child failed its declared outcome: " + receipt.Failure + " " + receipt.Error + " " + receipt.StandardError);
            return receipt;
        }
        try
        {
            if (mode == "drift")
            {
                string path = Path.Combine(root, "gradual-drift.mp4");
                _ = Run(["-hide_banner", "-loglevel", "warning", "-y", "-f", "lavfi", "-i", "testsrc2=size=320x192:rate=60:duration=1",
                    "-vf", "settb=1/48000,setpts=N*840", "-fps_mode", "passthrough", "-enc_time_base:v", "1:48000",
                    "-c:v", "h264_nvenc", "-preset", "p3", "-tune", "ll", "-b:v", "500k", "-an", "-video_track_timescale", "48000", path]);
                var decoded = Run(ReplayExactExport.DecodeArguments(path, false, 60));
                bool rejected = false;
                try { ReplayExactExport.ReadValidation(decoded, new ReplayInterval(0, 1, 1), 60); } catch (IOException) { rejected = true; }
                Require(rejected, "Gradual PTS drift passed count and individual-step checks without cumulative timing validation.");
                File.WriteAllText(Path.Combine(root, "verification.json"), JsonSerializer.Serialize(new { result = "passed", mode,
                    elapsedSeconds = timer.Elapsed.TotalSeconds, children = children.Count, allChildrenAbsent = children.All(c => c.ProducerAbsent),
                    claim = "Fully decodable60-frame/60FPS file at840ticks per frame (17.5ms) is rejected; count and nominal individual-step tolerance alone are insufficient." }));
                return 0;
            }
            foreach (string codec in mode is "short" or "rates" ? new[] { "h264_nvenc", "hevc_nvenc", "av1_nvenc" } : new[] { "h264_nvenc" })
            foreach (int fps in mode == "rates" ? new[] { 30, 60, 120 } : mode == "clock" ? new[] { 60 } : new[] { 24 })
            foreach (bool withAudio in mode == "clock" ? new[] { true, false } : new[] { true })
            {
                int duration = mode != "windows" ? 9 : 1804;
                string cell = Path.Combine(root, codec + (mode == "rates" ? "-" + fps : mode == "clock" ? withAudio ? "-audio" : "-video" : "")); Directory.CreateDirectory(cell);
                var source = new Source(cell, codec, fps, mode != "windows" ? 60 : 1800, Run, withAudio, mode == "clock");
                var info = source.ReplayInfo;
                // N is the absolute source frame ID; the second row is its
                // complement. Every selected picture is independently checked.
                string bit = "mod(floor(N/pow(2,floor(X/16))),2)";
                string picture = "nullsrc=s=320x192:r=" + fps + ",geq=lum='if(lt(Y,16),16+219*" + bit
                    + ",if(lt(Y,32),235-219*" + bit + ",96))':cb=128:cr=128";
                var generate = new List<string> { "-hide_banner", "-loglevel", "warning", "-y", "-f", "lavfi", "-i", picture };
                if (withAudio) generate.AddRange(["-f", "lavfi", "-i", "sine=frequency=1000:sample_rate=48000"]);
                generate.AddRange(["-t", duration.ToString(), "-map", "0:v:0",
                    "-c:v", codec, "-preset", "p3", "-tune", "ll", "-b:v", "500k", "-g", fps.ToString(), "-bf", "0", "-flags", "+cgop",
                    "-force_key_frames", mode == "clock" ? "expr:gte(t,2+n_forced*1)" : "expr:gte(t,n_forced*1)" ]);
                if (withAudio) generate.AddRange(["-map", "1:a:0", "-c:a", "aac", "-b:a", "64k", "-ar", "48000", "-ac", "2"]);
                if (mode == "clock")
                {
                    generate.AddRange(["-vf", "setpts=PTS+2/TB"]);
                    if (withAudio) generate.AddRange(["-af", "asetpts=PTS+2/TB"]);
                }
                generate.AddRange([
                    "-f", "segment", "-segment_time", "1", "-segment_format", codec == "av1_nvenc" ? "matroska" : "mpegts",
                    "-reset_timestamps", "0", "-avoid_negative_ts", "disabled" ]);
                generate.AddRange(["-segment_format_options", info.Extension == ".ts" ? "mpegts_copyts=1:avoid_negative_ts=disabled" : "avoid_negative_ts=disabled"]);
                generate.AddRange(["-segment_list_type", "csv", "-segment_list", info.Manifest, Path.Combine(cell, "chunk-%09d" + info.Extension)]);
                _ = Run(generate);
                var inputs = ((IReplaySessionSource)source).ReadSegments();
                Require(inputs.Count >= duration - (mode == "clock" ? 3 : 1), "Generated completed source is incomplete.");
                var originalHashes = inputs.ToDictionary(s => s.Path, s => Hash(s.Path));
                var cases = mode == "clock"
                    ? new[] { ("warm-up", 4.25, 300), ("after-idr", 5 + 1.0 / fps, 2), ("fractional", 6 + 0.5 / fps, 2), ("repeat-overlap", 6 + 0.5 / fps, 2) }
                    : mode != "windows"
                    ? new[] { ("warm-up", 1.25, 60), ("after-idr", 3 + 1.0 / fps, 2), ("fractional", 4 + 0.5 / fps, 2), ("repeat-overlap", 4 + 0.5 / fps, 2) }
                    : new[] { ("one-minute", 63 + 0.5 / fps, 60), ("five-minutes", 303 + 0.5 / fps, 300), ("thirty-minutes", 1803 + 0.5 / fps, 1800),
                        ("repeat-overlap", 303 + 0.5 / fps, 300), ("warm-up", 1.25, 1800), ("at-idr", 3.0, 2) };
                foreach (var (name, end, seconds) in cases)
                {
                    var view = new Source(cell, codec, fps, seconds, Run, withAudio, mode == "clock");
                    using var cache = new ReplayCache(cacheDirectory: cell, cacheBudgetBytes: 8_000_000_000);
                    var id = cache.RegisterSession(view);
                    // Keep originals for the independent content oracle and the
                    // next overlapping request; this isolated route never prunes.
                    cache.PreserveSession(id);
                    string destination = Path.Combine(cell, name); Directory.CreateDirectory(destination);
                    var accepted = cache.TrySave(id, (long)Math.Round(end * 1_000_000), destination);
                    Require(accepted.Accepted, "Generated request rejected: " + accepted.Rejection);
                    var saved = accepted.Completion.WaitAsync(TimeSpan.FromSeconds(300)).GetAwaiter().GetResult();
                    Require(saved.Succeeded && saved.Validation?.Kind == "FullDecode", "Generated replay failed: " + saved.Error);
                    string pixels = Path.Combine(destination, "ids.gray");
                    var offline = Stopwatch.StartNew();
                    _ = Run(["-hide_banner", "-loglevel", "error", "-xerror", "-ignore_editlist", "1", "-i", saved.File,
                        "-map", "0:v:0", "-an", "-vf", "crop=320:32:0:0,scale=20:2:flags=area,format=gray", "-fps_mode", "passthrough",
                        "-pix_fmt", "gray", "-f", "rawvideo", pixels]);
                    byte[] raw = File.ReadAllBytes(pixels); Require(raw.Length > 0 && raw.Length % 40 == 0, "Invalid decoded ID geometry.");
                    var ids = new List<int>();
                    for (int n = 0; n < raw.Length / 40; n++)
                    {
                        int value = 0;
                        for (int b = 0; b < 20; b++)
                        {
                            byte a = raw[n * 40 + b], complement = raw[n * 40 + 20 + b];
                            Require((a < 70 || a > 180) && (complement < 70 || complement > 180) && (a > 128) != (complement > 128), "Ambiguous/corrupt picture ID.");
                            if (a > 128) value |= 1 << b;
                        }
                        ids.Add(value);
                    }
                    double expectedEnd = Math.Round(end * 1_000_000) / 1_000_000;
                    double expectedStart = Math.Max(mode == "clock" ? 3 : 0, expectedEnd - seconds);
                    double sourceEpoch = mode == "clock" ? 2 : 0;
                    Require(Math.Abs(saved.Interval.Start - expectedStart) < 0.000001 && Math.Abs(saved.Interval.End - expectedEnd) < 0.000001,
                        "Reported interval differs from the independent request ledger.");
                    int first = (int)Math.Ceiling((expectedStart - sourceEpoch) * fps - 0.00001), last = (int)Math.Ceiling((expectedEnd - sourceEpoch) * fps - 0.00001);
                    bool exactCeiling = ids.SequenceEqual(Enumerable.Range(first, last - first));
                    double startErrorFrames = ids[0] - (expectedStart - sourceEpoch) * fps, endErrorFrames = ids[^1] + 1 - (expectedEnd - sourceEpoch) * fps;
                    double durationErrorFrames = ids.Count - (expectedEnd - expectedStart) * fps;
                    Require(ids.SequenceEqual(Enumerable.Range(ids[0], ids.Count))
                        && Math.Abs(startErrorFrames) <= 1.000001 && Math.Abs(endErrorFrames) <= 1.000001
                        && Math.Abs(durationErrorFrames) <= 1.000001, "Output source IDs exceed one-frame boundary/duration rounding or contain gaps/duplicates: "
                        + $"exact ceiling {first}..{last - 1}; actual {ids[0]}..{ids[^1]} ({ids.Count}).");
                    foreach (var pair in originalHashes) Require(Hash(pair.Key) == pair.Value, "An overlapping save changed its source.");
                    results.Add(new { codec, fps, withAudio, sourceEpoch, name, end, seconds, accepted = true, published = saved.File, saved.Interval, saved.Validation,
                        saveMilliseconds = saved.Milliseconds, offlineContentMilliseconds = offline.Elapsed.TotalMilliseconds,
                        firstId = ids[0], lastId = ids[^1], frames = ids.Count, exactCeilingInterval = exactCeiling,
                        startErrorFrames, endErrorFrames, durationErrorFrames, allIdsEqualSource = true, inputsPreserved = true });
                    File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
                }
            }
            if (mode == "short")
            {
                string corrupt = Path.Combine(root, "corrupt.mp4"); File.WriteAllBytes(corrupt, new byte[2048]);
                var invalid = RunCore(ReplayExactExport.DecodeArguments(corrupt, true, 24), false);
                bool rejected = false;
                try { ReplayExactExport.ReadValidation(invalid, new ReplayInterval(0, 1, 1), 24); } catch (IOException) { rejected = true; }
                Require(rejected, "A corrupt MP4 was accepted by runtime validation.");
            }
            Guard();
            File.WriteAllText(Path.Combine(root, "verification.json"), JsonSerializer.Serialize(new { result = "passed", mode, results,
                elapsedSeconds = timer.Elapsed.TotalSeconds, children = children.Count, allChildrenAbsent = children.All(c => c.ProducerAbsent),
                limits = "Generated320x192sources; no live acquisition, production storage calibration, latency percentile, A/V sync or supported hardware qualification." }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(root, "failure.json"), JsonSerializer.Serialize(new { error = ex.ToString(), results, elapsedSeconds = timer.Elapsed.TotalSeconds, children }));
            Console.Error.WriteLine(ex); return 1;
        }
    }
}
