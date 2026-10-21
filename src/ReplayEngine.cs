using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace F10Recorder
{
    public sealed class CaptureSession : IDisposable
    {
        readonly Settings settings;
        readonly int captureRateLimit;
        readonly bool timestampedAudio, recordAudio;
        readonly string directory, manifest;
        readonly object gate = new object();
        readonly HashSet<string> pinned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly Native.ProcessJob job = new Native.ProcessJob();
        readonly Stopwatch clock = Stopwatch.StartNew();
        Process process;
        LoopbackAudio audio;
        NamedPipeServerStream pipe;
        double progress, progressAt;
        ReplaySaveClock saveClock;
        double lastSegmentEnd, lastSegmentAt;
        long encodedFrames, reportedDrops, reportedDuplicates, cacheBytes;
        double encodingSpeed, metricsAt, cpuSeconds, cpuPercent;
        volatile bool disposed;
        volatile bool stopped;
        public string ErrorTail = "";
        public string DirectoryPath { get { return directory; } }
        internal int ProcessId { get { return process == null || stopped || process.HasExited ? 0 : process.Id; } }
        public bool HasExited { get { return process == null || process.HasExited; } }
        public double Position { get { lock (gate) return progress > 0 ? progress + Math.Min(0.5, clock.Elapsed.TotalSeconds - progressAt) : 0; } }
        internal double SaveCutoffAt(long triggerQpc)
        {
            lock (gate)
            {
                double cutoff = saveClock == null ? Position : saveClock.PositionAt(triggerQpc);
                Log.Write("Replay save trigger: QPC=" + triggerQpc + "; session=" + directory + "; cutoff=" + cutoff.ToString("R", CultureInfo.InvariantCulture)
                    + "; timeline=" + (saveClock == null ? "reported-progress" : "media-clock")
                    + "; epoch=" + (saveClock == null ? 0 : saveClock.EpochQpc) + "; frequency=" + (saveClock == null ? 0 : saveClock.Frequency)
                    + "; reported progress=" + progress.ToString("R", CultureInfo.InvariantCulture));
                return cutoff;
            }
        }
        public double BufferedSeconds { get { lock (gate) { var list = ReadSegments(); return list.Count == 0 ? 0 : Math.Min(settings.ReplayMinutes * 60, list.Last().End - list.First().Start); } } }
        public bool Healthy
        {
            get { return !HasExited && (audio == null || audio.Failure == null) && clock.Elapsed.TotalSeconds - lastSegmentAt < 15 && clock.Elapsed.TotalSeconds - progressAt < 15; }
        }
        public CaptureSession(Settings config, string root, bool synthetic, Func<bool> cancelled = null) : this(config, root, synthetic, cancelled, null) { }
        internal CaptureSession(Settings config, string root, bool synthetic, Func<bool> cancelled, WindowTargetSnapshot target, Func<bool> sourceStillValid = null)
        {
            settings = config;
            timestampedAudio = !synthetic && settings.SystemAudio;
            recordAudio = synthetic || settings.SystemAudio;
            // Leave window acquisition headroom before the downstream output-FPS filter.
            // The requested rate is a ceiling, not a guarantee of fresh pictures.
            captureRateLimit = synthetic ? settings.FramesPerSecond : settings.FramesPerSecond * (settings.CaptureMode == "window" ? 2 : 4);
            directory = Path.Combine(root, "session-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            manifest = Path.Combine(directory, "segments.csv");
            try
            {
                // Resolve/validate the engine and audio format before taking the
                // epoch so dependency discovery is not part of media startup.
                string executable = MediaDependency.ResolveExecutable();
                if (settings.CaptureMode == "window" && !synthetic && (target == null || !target.Available)) throw new InvalidOperationException("A validated window target is required; monitor fallback is disabled.");
                if (!synthetic && settings.SystemAudio) audio = new LoopbackAudio();
                MediaClock mediaClock = audio == null ? null : MediaClock.Capture();
                if (mediaClock != null) saveClock = new ReplaySaveClock(mediaClock);
                if (mediaClock != null) Log.Write("Media clock: QPC=" + mediaClock.QpcEpoch + "; frequency=" + mediaClock.QpcFrequency + "; UTC microseconds=" + mediaClock.UnixMicroseconds + "; bridge bracket ticks=" + mediaClock.BridgeUncertaintyTicks);
                var args = new List<string> { "-hide_banner", "-loglevel", "warning", "-y", "-stats_period", "0.25", "-progress", "pipe:1" };
                string source = settings.CaptureMode == "window" && !synthetic ? WindowVideoSource(target, captureRateLimit)
                    : "ddagrab=framerate=" + captureRateLimit + ":output_idx=" + (synthetic ? 0 : settings.DisplayIndex < 0 ? Native.PrimaryOutputIndex() : settings.DisplayIndex) + ":draw_mouse=1";
                if (synthetic) args.AddRange(new[] { "-re", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=" + settings.FramesPerSecond });
                // ddagrab's rate is a polling ceiling, not a guaranteed output clock.
                // Allow acquisition headroom, then pace the GPU frames in the graph.
                // Output-only CFR resampling still showed intermittent motion loss.
                // Audio/video use independent input queues but still share the
                // FFmpeg scheduler; delayed audio can throttle video acquisition.
                else if (mediaClock == null) args.AddRange(new[] { "-filter_complex", source + ",fps=fps=" + settings.FramesPerSecond + ":round=near[video]" });
                if (synthetic) args.AddRange(new[] { "-re", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000" });
                else if (settings.SystemAudio)
                {
                    string name = "F10Recorder-" + Guid.NewGuid().ToString("N");
                    pipe = AudioPipe.Create(name);
                    args.AddRange(new[] { "-copyts", "-thread_queue_size", "64", "-probesize", "32", "-analyzeduration", "0", "-f", "matroska", "-i", @"\\.\pipe\" + name });
                    // A separate video input queues frames while the audio jitter
                    // buffer delays delivery, subject to shared scheduler limits.
                    // gfxcapture 7fd2be97b9 subtracts its first SystemRelativeTime.
                    // Re-anchor that relative timeline once at first delivery;
                    // its unknown first-frame delivery delay needs live A/V qualification.
                    args.AddRange(new[] { "-thread_queue_size", "32", "-f", "lavfi", "-i", source + "," + mediaClock.VideoTimestampFilter + ",fps=fps=" + settings.FramesPerSecond + ":round=near" });
                }
                args.AddRange(new[] { "-map", synthetic ? "0:v:0" : mediaClock == null ? "[video]" : "1:v:0" });
                if (synthetic || settings.SystemAudio) args.AddRange(new[] { "-map", synthetic ? "1:a:0" : "0:a:0", "-af", synthetic ? "aresample=async=1:first_pts=0" : AudioPacketClock.ResamplerFilter, "-ac", "2", "-ar", "48000", "-c:a", "aac", "-b:a", "192k" });
                else args.Add("-an");
                if (!synthetic && mediaClock == null) args.AddRange(new[] { "-r", settings.FramesPerSecond.ToString(), "-fps_mode", "cfr" });
                else if (mediaClock != null) args.AddRange(new[] { "-fps_mode", "passthrough" });
                args.AddRange(new[] { "-c:v", settings.VideoCodec, "-preset", "p3", "-tune", "ll", "-rc", "cbr", "-b:v", settings.BitrateMbps + "M", "-maxrate", settings.BitrateMbps + "M", "-bufsize", (settings.BitrateMbps * 2) + "M", "-g", settings.FramesPerSecond.ToString(), "-bf", "0", "-forced-idr", "1", "-force_key_frames", "expr:gte(t,n_forced*1)", "-flags", "+cgop", "-f", "segment", "-segment_time", "1", "-segment_format", settings.SegmentFormat, "-reset_timestamps", "1", "-segment_list", manifest, "-segment_list_type", "csv", "-segment_list_size", (settings.ReplayMinutes * 60 + 20).ToString(), Path.Combine(directory, "chunk-%09d" + settings.SegmentExtension) });
                var argumentProbe = RecorderRuntime.CaptureArgumentProbe;
                if (argumentProbe != null) args = argumentProbe(args);
                if (!synthetic && settings.CaptureMode == "window") Log.Write("Experimental window capture arguments: " + string.Join(" ", args.Select(Quote)));
                if (sourceStillValid != null && !sourceStillValid()) throw new OperationCanceledException("The selected window changed before capture could start.");
                process = StartProcess(executable, args, directory);
                job.Add(process);
                process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    if (e.Data.StartsWith("out_time_us="))
                    {
                        long value;
                        if (long.TryParse(e.Data.Substring(12), out value)) lock (gate) { double next = value / 1000000.0; if (next > progress) { progress = next; progressAt = clock.Elapsed.TotalSeconds; } }
                    }
                    else
                    {
                        int split = e.Data.IndexOf('='); if (split <= 0) return;
                        string key = e.Data.Substring(0, split), value = e.Data.Substring(split + 1);
                        long count; double speed;
                        lock (gate)
                        {
                            if (key == "frame" && long.TryParse(value, out count)) encodedFrames = count;
                            else if (key == "drop_frames" && long.TryParse(value, out count)) reportedDrops = count;
                            else if (key == "dup_frames" && long.TryParse(value, out count)) reportedDuplicates = count;
                            else if (key == "speed" && double.TryParse(value.TrimEnd('x'), NumberStyles.Float, CultureInfo.InvariantCulture, out speed)) encodingSpeed = speed;
                        }
                    }
                };
                bool videoBridgeLogged = false;
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    double bridge;
                    string bridgeText = Regex.Match(e.Data, @"(?:^|\]\s+)([0-9]{15,17}(?:\.0+)?)$").Groups[1].Value;
                    if (mediaClock != null && !videoBridgeLogged && double.TryParse(bridgeText, NumberStyles.Float, CultureInfo.InvariantCulture, out bridge) && bridge > 1000000000000000)
                    {
                        videoBridgeLogged = true;
                        Log.Write("Video first-frame RTC microseconds=" + bridge.ToString("F0", CultureInfo.InvariantCulture) + (settings.CaptureMode == "window" ? "; experimental delivery anchor offset ms=" : "; acquisition offset ms=") + ((bridge - mediaClock.UnixMicroseconds) / 1000).ToString("F3", CultureInfo.InvariantCulture));
                        return;
                    }
                    lock (gate) { ErrorTail = (ErrorTail + "\n" + e.Data); if (ErrorTail.Length > 3000) ErrorTail = ErrorTail.Substring(ErrorTail.Length - 3000); }
                };
                process.BeginOutputReadLine(); process.BeginErrorReadLine();
                if (pipe != null)
                {
                    AudioPipe.Accept(pipe, process, cancelled, 10000);
                    audio.Start(pipe, mediaClock);
                }
                Log.Write("Capture started; PID " + process.Id + "; " + directory);
            }
            catch { Dispose(); throw; }
        }
        internal static string WindowVideoSource(WindowTargetSnapshot target, int rate)
        {
            if (target == null || !target.Available) throw new InvalidOperationException("A live selected window is required.");
            return "gfxcapture=hwnd=" + target.Handle.ToString(CultureInfo.InvariantCulture) + ":capture_cursor=1:capture_border=0:width=-2:height=-2:resize_mode=scale_aspect:max_framerate=" + rate.ToString(CultureInfo.InvariantCulture);
        }
        internal void StopCapture()
        {
            if (stopped) return;
            // Freeze the source session at stop request, before process draining.
            lock (gate) if (saveClock != null) saveClock.StopAt(Stopwatch.GetTimestamp());
            if (process != null)
            {
                try { if (!process.HasExited) { process.StandardInput.WriteLine("q"); process.StandardInput.Flush(); if (!process.WaitForExit(1500)) { process.Kill(); process.WaitForExit(5000); } } }
                catch (InvalidOperationException) { } catch (IOException) { }
            }
            if (audio != null) audio.Dispose();
            if (pipe != null) pipe.Dispose();
            stopped = true;
        }
        List<Segment> ReadSegments()
        {
            try
            {
                var segments = ReplaySelection.Read(manifest, directory);
                // The initial GOP is pre-roll: the audio client and video device
                // become available at different times. Only advertise readiness
                // and export after the next closed GOP has both streams running.
                if (timestampedAudio) segments.RemoveAll(s => Path.GetFileName(s.Path) == "chunk-000000000" + settings.SegmentExtension);
                return segments;
            }
            catch (IOException) { return new List<Segment>(); }
        }
        public void Maintain()
        {
            var probe = RecorderRuntime.CaptureHealthProbe;
            string controlledFailure = probe == null ? null : probe();
            if (controlledFailure != null) throw new IOException(controlledFailure);
            lock (gate)
            {
                var segments = ReadSegments();
                if (segments.Count == 0) return;
                double newest = segments.Last().End;
                if (newest < lastSegmentEnd) return; // Segment-list rewrites can be observed partway through.
                if (newest > lastSegmentEnd) { lastSegmentEnd = newest; lastSegmentAt = clock.Elapsed.TotalSeconds; }
                double threshold = newest - settings.ReplayMinutes * 60 - 10;
                // Delete only known older sequence numbers. Newer chunks missing from a partial CSV read stay safe.
                string oldestRetained = segments.First(s => s.End >= threshold).Path;
                foreach (string path in Directory.GetFiles(directory, "chunk-*" + settings.SegmentExtension))
                    if (StringComparer.OrdinalIgnoreCase.Compare(path, oldestRetained) < 0 && !pinned.Contains(path)) TryDeleteFile(path);
                long limit = (long)((settings.ReplayMinutes * 60 + 30) * (settings.BitrateMbps * 1000000L + 192000) / 8 * 1.5);
                if (Directory.GetFiles(directory, "chunk-*" + settings.SegmentExtension).Sum(p => new FileInfo(p).Length) > limit) throw new IOException("Replay cache exceeded its byte limit.");
                if (audio != null && audio.DefaultEndpointChanged()) throw new IOException("Default audio output changed; rebuilding replay buffer.");
            }
        }
        internal RecorderMetrics Metrics
        {
            get
            {
                lock (gate)
                {
                    var result = new RecorderMetrics();
                    if (disposed || process == null) return result;
                    result.CaptureProcessId = ProcessId; result.CaptureProgressSeconds = progress;
                    result.ConfiguredCaptureRateLimit = captureRateLimit; result.ConfiguredOutputFramesPerSecond = settings.FramesPerSecond;
                    result.EncodedFrames = encodedFrames; result.EncoderReportedDrops = reportedDrops; result.EncoderReportedDuplicates = reportedDuplicates; result.EncodingSpeed = encodingSpeed;
                    var segments = ReadSegments(); result.BufferedSeconds = segments.Count == 0 ? 0 : Math.Min(settings.ReplayMinutes * 60, segments.Last().End - segments.First().Start);
                    double now = clock.Elapsed.TotalSeconds;
                    try
                    {
                        process.Refresh(); result.CaptureWorkingSetBytes = process.WorkingSet64;
                        if (now - metricsAt >= 1)
                        {
                            double total = process.TotalProcessorTime.TotalSeconds;
                            cpuPercent = (total - cpuSeconds) / (now - metricsAt) / Environment.ProcessorCount * 100;
                            metricsAt = now; cpuSeconds = total;
                            cacheBytes = Directory.GetFiles(directory, "chunk-*" + settings.SegmentExtension).Sum(p => { try { return new FileInfo(p).Length; } catch (IOException) { return 0L; } });
                        }
                    }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                    catch (IOException) { }
                    result.CaptureCpuPercent = cpuPercent; result.CacheBytes = cacheBytes;
                    return result;
                }
            }
        }
        public string Save(double end, string saveRoot)
        {
            string snapshot = Path.Combine(directory, "save-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(snapshot);
            try
            {
                List<Segment> selection = null;
                var wait = Stopwatch.StartNew();
                while (wait.Elapsed.TotalSeconds < 5)
                {
                    lock (gate)
                    {
                        if (disposed) throw new IOException("Capture restarted before the replay could be saved.");
                        var available = ReadSegments();
                        if (available.Count > 0 && (available.Last().End >= end || stopped))
                        {
                            if (stopped) end = Math.Min(end, available.Last().End);
                            selection = ReplaySelection.Select(available, end, settings.ReplayMinutes * 60);
                            if (selection.Count == 0) throw new IOException("No replay frames are available yet.");
                            foreach (var segment in selection) pinned.Add(segment.Path);
                            break;
                        }
                    }
                    Thread.Sleep(100);
                }
                if (selection == null) throw new IOException("Capture did not finish the current segment in time. " + ErrorTail);
                var originals = selection.Select(s => s.Path).ToArray();
                try
                {
                    // Pruning honors the pins. Never hold the progress/maintenance lock during disk copies.
                    for (int i = 0; i < selection.Count; i++)
                    {
                        string copy = Path.Combine(snapshot, i.ToString("D6") + settings.SegmentExtension);
                        if (!Native.CreateHardLink(copy, originals[i], IntPtr.Zero)) File.Copy(originals[i], copy);
                        selection[i].Path = copy;
                    }
                }
                finally { lock (gate) foreach (string original in originals) pinned.Remove(original); }
                Directory.CreateDirectory(saveRoot);
                string final = Path.Combine(saveRoot, "Replay-" + DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss-fff") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".mp4");
                string partial = final + ".partial.mp4";
                string concat = Path.Combine(snapshot, "concat.txt");
                // TS duration probes include AAC packet padding; authoritative video boundaries prevent per-segment drift.
                File.WriteAllLines(concat, selection.SelectMany(s => new[] { "file '" + Path.GetFileName(s.Path) + "'", "duration " + (s.End - s.Start).ToString("0.000000", CultureInfo.InvariantCulture) }), new UTF8Encoding(false));
                var arguments = new List<string> { "-hide_banner", "-loglevel", "warning", "-y", "-f", "concat", "-safe", "1", "-i", concat, "-t", (end - selection[0].Start).ToString("0.000000", CultureInfo.InvariantCulture), "-c", "copy", "-avoid_negative_ts", "make_zero", "-movflags", "+faststart" };
                arguments.AddRange(new[] { "-map", "0:v:0" });
                if (recordAudio) arguments.AddRange(new[] { "-map", "0:a:0" });
                if (settings.VideoCodec == "hevc_nvenc") arguments.AddRange(new[] { "-tag:v", "hvc1" });
                if (settings.VideoCodec == "av1_nvenc")
                {
                    // Matroska stores millisecond timestamps. Restore the constant-rate capture clock when remuxing to MP4.
                    string interval = "1/(" + settings.FramesPerSecond + "*TB)";
                    arguments.AddRange(new[] { "-bsf:v", "setts=time_base=1/48000:prescale=1:pts=STARTPTS+N*" + interval + ":dts=STARTDTS+N*" + interval + ":duration=" + interval, "-video_track_timescale", "48000" });
                }
                arguments.Add(partial);
                try
                {
                    using (Process mux = StartProcess(MediaDependency.ResolveExecutable(), arguments, snapshot))
                    {
                        job.Add(mux);
                        Task<string> errors = mux.StandardError.ReadToEndAsync();
                        mux.StandardOutput.ReadToEndAsync();
                        if (!mux.WaitForExit(120000)) { mux.Kill(); throw new TimeoutException("Replay save timed out."); }
                        string error = errors.Result;
                        if (mux.ExitCode != 0 || !File.Exists(partial) || new FileInfo(partial).Length < 1000) throw new IOException("Replay save failed: " + error);
                    }
                    File.Move(partial, final);
                }
                finally { TryDeleteFile(partial); }
                Log.Write("Saved " + final + "; requested end=" + end.ToString(CultureInfo.InvariantCulture));
                return final;
            }
            finally { TryDeleteDirectory(snapshot); }
        }
        public static Process StartProcess(string executable, IEnumerable<string> args, string workDirectory)
        {
            var info = new ProcessStartInfo(executable, string.Join(" ", args.Select(Quote))) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true, WorkingDirectory = workDirectory };
            // Every FFmpeg sink authenticates bytes and retains a read-only lease
            // through CreateProcess, including callers that retained an older path.
            using (MediaDependency.OpenVerifiedExecutable(executable))
            {
                info.WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable));
                info.EnvironmentVariables["PATH"] = Environment.SystemDirectory;
                return Process.Start(info);
            }
        }
        public static string Quote(string argument)
        {
            // CommandLineToArgvW quoting, including trailing backslashes before the closing quote.
            return "\"" + Regex.Replace(argument, "(\\\\*)\"", "$1$1\\\"").Replace("\0", "") + new string('\\', argument.Reverse().TakeWhile(c => c == '\\').Count()) + "\"";
        }
        internal static void TryDeleteFile(string path) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        internal static void TryDeleteDirectory(string path) { try { Directory.Delete(path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        public void Dispose()
        {
            lock (gate) { if (disposed) return; disposed = true; }
            StopCapture();
            job.Dispose();
            if (process != null) process.Dispose();
            if (!string.IsNullOrWhiteSpace(ErrorTail)) Log.Write("Capture diagnostics: " + ErrorTail);
        }
    }
    public sealed class ReplayEngine : IDisposable
    {
        readonly Settings settings;
        readonly string cache;
        readonly object gate = new object();
        readonly Thread worker;
        readonly bool synthetic;
        readonly Func<WindowTargetSnapshot> windowTarget;
        volatile bool closing, paused, restart;
        CaptureSession session;
        int saving;
        int recoveries;
        double lastSaveMilliseconds;
        long lastSaveBytes;
        bool lastSaveSucceeded;
        internal bool CanSave { get { lock (gate) return !paused && !closing && session != null && session.BufferedSeconds > 0; } }
        internal RecorderMetrics Metrics
        {
            get
            {
                lock (gate)
                {
                    var result = session == null ? new RecorderMetrics() : session.Metrics;
                    result.SampledUtc = DateTime.UtcNow.ToString("o");
                    result.Recoveries = recoveries; result.LastSaveMilliseconds = lastSaveMilliseconds;
                    result.LastSaveBytes = lastSaveBytes; result.LastSaveSucceeded = lastSaveSucceeded;
                    return result;
                }
            }
        }
        internal bool IsSaving { get { return Volatile.Read(ref saving) != 0; } }
        public event Action<string, bool> StateChanged;
        internal event Action<CapturePhase, string, bool> CaptureStateChanged;
        public event Action<string, bool> SaveFinished;
        public event Action SaveStarted;
        public ReplayEngine(Settings config, string dataDirectory, bool startPaused = false) : this(config, dataDirectory, startPaused, false) { }
        internal ReplayEngine(Settings config, string dataDirectory, bool startPaused, bool syntheticSource) : this(config, dataDirectory, startPaused, syntheticSource, null) { }
        internal ReplayEngine(Settings config, string dataDirectory, bool startPaused, bool syntheticSource, Func<WindowTargetSnapshot> targetProvider)
        {
            windowTarget = targetProvider;
            settings = config; paused = startPaused; synthetic = syntheticSource; cache = Path.Combine(dataDirectory, "buffer");
            Directory.CreateDirectory(cache);
            // Only this application's disposable cache. Saved clips live elsewhere.
            foreach (string old in Directory.GetDirectories(cache, "session-*")) CaptureSession.TryDeleteDirectory(old);
            worker = new Thread(Run) { IsBackground = true, Name = "Capture supervisor" }; worker.Start();
        }
        void State(string message, bool ready) { State(ready ? CapturePhase.Buffering : paused ? CapturePhase.Paused : message.StartsWith("Recovering:") ? CapturePhase.Recovering : CapturePhase.Starting, message, ready); }
        void State(CapturePhase next, string message, bool ready) { var typed = CaptureStateChanged; if (typed != null) typed(next, message, ready); var callback = StateChanged; if (callback != null) callback(message, ready); }
        public void Restart() { restart = true; }
        internal bool RestartPending { get { return restart; } }
        public void Pause(bool value) { paused = value; restart = true; }
        internal int CaptureProcessId { get { lock (gate) return session == null ? 0 : session.ProcessId; } }
        void Run()
        {
            if (!synthetic && settings.CaptureMode == "window") { RunWindow(); return; }
            int failures = 0;
            while (!closing)
            {
                CaptureSession current = null;
                try
                {
                    if (paused) { State("Paused", false); Thread.Sleep(250); continue; }
                    restart = false;
                    settings.Validate();
                    State("Starting replay buffer…", false);
                    current = new CaptureSession(settings, cache, synthetic, delegate { return closing || paused || restart; });
                    lock (gate) session = current;
                    while (!closing && !restart && !paused)
                    {
                        current.Maintain();
                        if (!current.Healthy) throw new IOException("Capture stopped advancing. " + current.ErrorTail);
                        double buffered = current.BufferedSeconds;
                        if (buffered > 0) { failures = 0; State("Buffering · " + Math.Min(settings.ReplayMinutes * 60, (int)buffered) + "s · " + settings.HotkeyText + " to save", true); }
                        Thread.Sleep(500);
                    }
                }
                catch (Exception ex) { failures++; lock (gate) recoveries++; Log.Write(ex.ToString()); State("Recovering: " + ex.Message.Split('\n')[0], false); }
                finally
                {
                    lock (gate) session = null;
                    while (Volatile.Read(ref saving) != 0 && !closing) Thread.Sleep(100);
                    if (current != null) { current.Dispose(); CaptureSession.TryDeleteDirectory(current.DirectoryPath); }
                }
                int delay = failures == 0 ? 0 : Math.Min(30, 1 << Math.Min(5, failures));
                for (int i = 0; i < delay * 10 && !closing && !restart; i++) Thread.Sleep(100);
            }
        }
        // Source loss retains only this session's completed footage. A new source
        // session starts a new buffer; discontinuous timelines are never joined.
        void RunWindow()
        {
            CaptureSession current = null;
            long generation = -1;
            bool capturing = false;
            int failures = 0;
            try
            {
                while (!closing)
                {
                    try
                    {
                        var target = windowTarget == null ? new WindowTargetSnapshot { Phase = CapturePhase.WaitingForTarget, Reason = "Window capture is available only through the independent Windows worker." } : windowTarget();
                        if (paused || restart || !target.Available || (capturing && target.Generation != generation))
                        {
                            State(paused ? CapturePhase.Paused : target.Available ? CapturePhase.Starting : target.Phase, paused ? "Paused" : target.Reason, false);
                            if (capturing) { current.StopCapture(); capturing = false; }
                            if (paused || restart)
                            {
                                lock (gate) session = null;
                                while (IsSaving && !closing) Thread.Sleep(50);
                                if (current != null) { current.Dispose(); CaptureSession.TryDeleteDirectory(current.DirectoryPath); current = null; }
                                restart = false;
                            }
                            if (paused || !target.Available) { Thread.Sleep(250); continue; }
                        }
                        if (!capturing)
                        {
                            lock (gate) session = null;
                            while (IsSaving && !closing) Thread.Sleep(50);
                            if (closing) break;
                            if (current != null) { current.Dispose(); CaptureSession.TryDeleteDirectory(current.DirectoryPath); current = null; }
                            settings.Validate(); State(CapturePhase.Starting, target.Reason, false);
                            var expected = target;
                            current = new CaptureSession(settings, cache, false, delegate { return closing || paused || restart; }, target,
                                delegate { var latest = windowTarget(); return latest.Available && latest.Generation == expected.Generation; });
                            var verified = windowTarget();
                            if (!verified.Available || verified.Generation != expected.Generation) { current.StopCapture(); continue; }
                            generation = target.Generation; capturing = true;
                            lock (gate) session = current;
                        }
                        current.Maintain();
                        if (!current.Healthy) throw new IOException("Window capture stopped advancing. " + current.ErrorTail);
                        double buffered = current.BufferedSeconds;
                        if (buffered > 0) { failures = 0; State(CapturePhase.Buffering, "Buffering · " + (int)buffered + "s · " + target.ApplicationName, true); }
                        Thread.Sleep(250);
                    }
                    catch (Exception ex)
                    {
                        State(CapturePhase.Recovering, ex.Message.Split('\n')[0], false);
                        if (current != null) current.StopCapture(); capturing = false;
                        lock (gate) recoveries++;
                        Log.Write(ex.ToString()); failures++;
                        int delay = Math.Min(30, 1 << Math.Min(5, failures));
                        for (int i = 0; i < delay * 10 && !closing && !paused && !restart; i++) Thread.Sleep(100);
                    }
                }
            }
            finally
            {
                lock (gate) session = null;
                while (IsSaving && !closing) Thread.Sleep(50);
                if (current != null) { current.Dispose(); CaptureSession.TryDeleteDirectory(current.DirectoryPath); }
            }
        }
        public void Save(string applicationName = null)
        {
            long triggerQpc = Stopwatch.GetTimestamp();
            if (closing) { NotifySave("Recorder is stopping.", false); return; }
            if (paused) { NotifySave("Replay recording is paused. Resume recording before saving.", false); return; }
            if (Interlocked.CompareExchange(ref saving, 1, 0) != 0) { NotifySave("A replay is already being saved.", false); return; }
            CaptureSession current; double end;
            lock (gate) { current = session; end = current == null ? 0 : current.SaveCutoffAt(triggerQpc); }
            if (current == null || end <= 0) { Interlocked.Exchange(ref saving, 0); NotifySave("The replay buffer is warming up or recovering.", false); return; }
            var started = SaveStarted; if (started != null) started();
            Task.Run(delegate
            {
                var elapsed = Stopwatch.StartNew();
                try
                {
                    string file = current.Save(end, ApplicationFolders.Destination(settings.OutputDirectory, applicationName));
                    lock (gate) { lastSaveMilliseconds = elapsed.Elapsed.TotalMilliseconds; lastSaveBytes = new FileInfo(file).Length; lastSaveSucceeded = true; }
                    NotifySave(file, true);
                }
                catch (Exception ex) { lock (gate) { lastSaveMilliseconds = elapsed.Elapsed.TotalMilliseconds; lastSaveBytes = 0; lastSaveSucceeded = false; } Log.Write(ex.ToString()); NotifySave(ex.Message, false); }
                finally { Interlocked.Exchange(ref saving, 0); }
            });
        }
        void NotifySave(string message, bool success) { var callback = SaveFinished; if (callback != null) callback(message, success); }
        public void Dispose() { closing = true; if (!worker.Join(15000)) throw new TimeoutException("Capture cleanup is still running; a replacement recorder was not started."); }
    }
}
