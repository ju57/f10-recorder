using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace F10Recorder
{
    public sealed class CaptureSession : IDisposable, IReplaySessionSource
    {
        readonly Settings settings;
        readonly int captureRateLimit;
        readonly bool timestampedAudio, recordAudio;
        readonly string directory, manifest;
        readonly object gate = new object();
        readonly Native.ProcessJob job = new Native.ProcessJob();
        readonly Stopwatch clock = Stopwatch.StartNew();
        Process process;
        LoopbackAudio audio;
        NamedPipeServerStream pipe;
        double progress, progressAt;
        ReplaySaveClock saveClock;
        double lastSegmentEnd, lastSegmentAt;
        long encodedFrames, reportedDrops, reportedDuplicates;
        double encodingSpeed, metricsAt, cpuSeconds, cpuPercent;
        volatile bool disposed;
        volatile bool stopped;
        readonly ReplaySessionInfo replayInfo;
        double? stoppedPosition;
        ReplaySessionInfo IReplaySessionSource.ReplayInfo { get { return replayInfo; } }
        bool IReplaySessionSource.Stopped { get { return stopped; } }
        string IReplaySessionSource.Diagnostics { get { return ErrorTail; } }
        double IReplaySessionSource.SaveCutoffAt(long trigger) { return SaveCutoffAt(trigger); }
        void IReplaySessionSource.FreezeAt(long timestamp) { FreezeAt(timestamp); }
        void IReplaySessionSource.StopCapture() { StopCapture(); }
        void IReplaySessionSource.CheckCaptureHealth(double end) { CheckCaptureHealth(end); }
        public string ErrorTail = "";
        public string DirectoryPath { get { return directory; } }
        internal int ProcessId { get { return process == null || stopped || process.HasExited ? 0 : process.Id; } }
        public bool HasExited { get { return process == null || process.HasExited; } }
        public double Position { get { lock (gate) return progress > 0 ? progress + Math.Min(0.5, clock.Elapsed.TotalSeconds - progressAt) : 0; } }
        internal double SaveCutoffAt(long triggerQpc)
        {
            lock (gate)
            {
                double cutoff = saveClock == null ? stoppedPosition ?? Position : saveClock.PositionAt(triggerQpc);
                Log.Write("Replay save trigger: QPC=" + triggerQpc + "; session=" + directory + "; cutoff=" + cutoff.ToString("R", CultureInfo.InvariantCulture)
                    + "; timeline=" + (saveClock == null ? "reported-progress" : "media-clock")
                    + "; epoch=" + (saveClock == null ? 0 : saveClock.EpochQpc) + "; frequency=" + (saveClock == null ? 0 : saveClock.Frequency)
                    + "; reported progress=" + progress.ToString("R", CultureInfo.InvariantCulture));
                return cutoff;
            }
        }
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
            replayInfo = new ReplaySessionInfo(directory, manifest, settings.ReplayMinutes * 60, settings.BitrateMbps,
                settings.FramesPerSecond, settings.VideoCodec, settings.SegmentExtension, timestampedAudio, recordAudio);
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
            FreezeAt(Stopwatch.GetTimestamp());
            if (process != null)
            {
                try { if (!process.HasExited) { process.StandardInput.WriteLine("q"); process.StandardInput.Flush(); if (!process.WaitForExit(1500)) { process.Kill(); process.WaitForExit(5000); } } }
                catch (InvalidOperationException) { } catch (IOException) { }
            }
            if (audio != null) audio.Dispose();
            if (pipe != null) pipe.Dispose();
            stopped = true;
        }
        void FreezeAt(long timestamp)
        {
            lock (gate)
            {
                if (saveClock != null) saveClock.StopAt(timestamp);
                else if (!stoppedPosition.HasValue) stoppedPosition = Position;
            }
        }
        internal void CheckCaptureHealth(double completedEnd)
        {
            var probe = RecorderRuntime.CaptureHealthProbe;
            string controlledFailure = probe == null ? null : probe();
            if (controlledFailure != null) throw new IOException(controlledFailure);
            lock (gate)
            {
                if (completedEnd > lastSegmentEnd) { lastSegmentEnd = completedEnd; lastSegmentAt = clock.Elapsed.TotalSeconds; }
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
                    double now = clock.Elapsed.TotalSeconds;
                    try
                    {
                        process.Refresh(); result.CaptureWorkingSetBytes = process.WorkingSet64;
                        if (now - metricsAt >= 1)
                        {
                            double total = process.TotalProcessorTime.TotalSeconds;
                            cpuPercent = (total - cpuSeconds) / (now - metricsAt) / Environment.ProcessorCount * 100;
                            metricsAt = now; cpuSeconds = total;
                        }
                    }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                    catch (IOException) { }
                    result.CaptureCpuPercent = cpuPercent;
                    return result;
                }
            }
        }
        void IReplaySessionSource.Export(IReadOnlyList<string> arguments, string snapshot)
        {
            using (Process mux = StartProcess(MediaDependency.ResolveExecutable(), arguments, snapshot))
            {
                job.Add(mux);
                Task<string> errors = mux.StandardError.ReadToEndAsync();
                Task<string> output = mux.StandardOutput.ReadToEndAsync();
                if (!mux.WaitForExit(120000)) { mux.Kill(); mux.WaitForExit(); throw new TimeoutException("Replay save timed out."); }
                string error = errors.GetAwaiter().GetResult();
                output.GetAwaiter().GetResult();
                if (mux.ExitCode != 0) throw new IOException("Replay save failed: " + error);
            }
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
        readonly CaptureLifecycle capture;
        CaptureSession session;
        readonly ReplayCache replays;
        readonly ReplaySaveNotifications saveNotifications = new ReplaySaveNotifications();
        ReplaySessionId? sessionId;
        int recoveries;
        internal bool CanSave { get { lock (gate) return !capture.Paused && !capture.Closing && sessionId.HasValue && replays.GetStatus(sessionId.Value).BufferedSeconds > 0; } }
        internal RecorderMetrics Metrics
        {
            get
            {
                lock (gate)
                {
                    var result = session == null ? new RecorderMetrics() : session.Metrics;
                    result.SampledUtc = DateTime.UtcNow.ToString("o");
                    var storage = replays.Status;
                    var save = storage.LastSave;
                    result.Recoveries = recoveries; result.LastSaveMilliseconds = save == null ? 0 : save.Milliseconds;
                    result.LastSaveBytes = save == null ? 0 : save.Bytes; result.LastSaveSucceeded = save != null && save.Succeeded;
                    if (sessionId.HasValue)
                    {
                        var buffer = replays.GetStatus(sessionId.Value);
                        result.BufferedSeconds = buffer.BufferedSeconds;
                    }
                    result.CacheBytes = storage.CacheBytes ?? -1; // Unknown inventory is not an empty cache.
                    return result;
                }
            }
        }
        internal bool IsSaving { get { return replays.IsSaving; } }
        internal void ValidateReplayCacheBudget(long proposedBudgetBytes) => replays.ValidateBudget(proposedBudgetBytes);
        internal event Action CaptureStatusChanged
        {
            add { capture.CaptureStatusChanged += value; }
            remove { capture.CaptureStatusChanged -= value; }
        }
        internal CaptureStatus ReadCaptureStatus() => capture.ReadCaptureStatus();
        public event Action<string, bool> SaveFinished;
        public event Action SaveStarted;
        public ReplayEngine(Settings config, string dataDirectory, bool startPaused = false) : this(config, dataDirectory, startPaused, false) { }
        internal ReplayEngine(Settings config, string dataDirectory, bool startPaused, bool syntheticSource) : this(config, dataDirectory, startPaused, syntheticSource, null) { }
        internal ReplayEngine(Settings config, string dataDirectory, bool startPaused, bool syntheticSource, Func<WindowTargetSnapshot> targetProvider)
        {
            windowTarget = targetProvider;
            settings = config; capture = new CaptureLifecycle(startPaused); synthetic = syntheticSource; cache = Path.Combine(dataDirectory, "buffer");
            Directory.CreateDirectory(cache);
            // Old-session and failed-cleanup material still consumes this profile's
            // budget. Startup must not erase it merely to admit new capture.
            replays = new ReplayCache(cacheDirectory: cache, cacheBudgetBytes: config.ReplayCacheBudgetBytes);
            worker = new Thread(Run) { IsBackground = true, Name = "Capture supervisor" }; worker.Start();
        }
        public void Restart() => capture.Restart();
        public void Pause(bool value) => capture.Pause(value);
        internal int CaptureProcessId { get { lock (gate) return session == null ? 0 : session.ProcessId; } }
        void Run()
        {
            if (!synthetic && settings.CaptureMode == "window") { RunWindow(); return; }
            int failures = 0;
            while (!capture.Closing)
            {
                if (!capture.TryBeginAttempt("Starting replay buffer…", out var attempt)) { Thread.Sleep(250); continue; }
                CaptureSession current = null; ReplaySessionId? currentReplay = null;
                try
                {
                    settings.Validate();
                    current = new CaptureSession(settings, cache, synthetic, () => !capture.IsCurrent(attempt));
                    currentReplay = replays.RegisterSession(current);
                    var observed = current;
                    if (!capture.AttachProcess(attempt, () => observed.ProcessId != 0)) continue;
                    lock (gate) { session = current; sessionId = currentReplay; }
                    while (capture.IsCurrent(attempt))
                    {
                        replays.Maintain(currentReplay.Value);
                        if (!current.Healthy) throw new IOException("Capture stopped advancing. " + current.ErrorTail);
                        double buffered = replays.GetStatus(currentReplay.Value).BufferedSeconds;
                        if (buffered > 0) { failures = 0; capture.Publish(attempt, CapturePhase.Buffering, "Buffering · " + Math.Min(settings.ReplayMinutes * 60, (int)buffered) + "s · " + settings.HotkeyText + " to save", true); }
                        Thread.Sleep(500);
                    }
                }
                catch (Exception ex)
                {
                    failures++; lock (gate) recoveries++; Log.Write(ex.ToString());
                    capture.Publish(attempt, CapturePhase.Recovering, ex.Message.Split('\n')[0], false);
                }
                finally
                {
                    capture.EndAttempt(attempt);
                    lock (gate) { session = null; sessionId = null; }
                    if (currentReplay.HasValue) replays.RetireSessionAsync(currentReplay.Value).GetAwaiter().GetResult();
                }
                int delay = failures == 0 ? 0 : Math.Min(30, 1 << Math.Min(5, failures));
                for (int i = 0; i < delay * 10 && !capture.Closing && !capture.RestartPending; i++) Thread.Sleep(100);
            }
        }
        // Source loss retains only this session's completed footage. A new source
        // session starts a new buffer; discontinuous timelines are never joined.
        void RunWindow()
        {
            CaptureSession current = null; ReplaySessionId? currentReplay = null;
            CaptureAttempt attempt = default;
            long generation = -1;
            bool capturing = false;
            int failures = 0;
            try
            {
                while (!capture.Closing)
                {
                    try
                    {
                        long reset = capture.ResetRequest;
                        var target = windowTarget == null ? new WindowTargetSnapshot { Phase = CapturePhase.WaitingForTarget, Reason = "Window capture is available only through the independent Windows worker." } : windowTarget();
                        if (capture.Paused || capture.RestartPending || !target.Available || (capturing && target.Generation != generation))
                        {
                            capture.SourceUnavailable(target.Available ? CapturePhase.Starting : target.Phase, target.Reason);
                            if (capturing) { replays.StopAcquisitionAsync(currentReplay.Value).GetAwaiter().GetResult(); capturing = false; }
                            if (capture.Paused || capture.RestartPending)
                            {
                                lock (gate) { session = null; sessionId = null; }
                                if (currentReplay.HasValue) { replays.RetireSessionAsync(currentReplay.Value).GetAwaiter().GetResult(); current = null; currentReplay = null; }
                                capture.CompleteReset(reset);
                                if (!target.Available) capture.SourceUnavailable(target.Phase, target.Reason);
                            }
                            if (capture.Paused || !target.Available) { Thread.Sleep(250); continue; }
                        }
                        if (!capturing)
                        {
                            lock (gate) { session = null; sessionId = null; }
                            if (capture.Closing) break;
                            if (currentReplay.HasValue) { replays.RetireSessionAsync(currentReplay.Value).GetAwaiter().GetResult(); current = null; currentReplay = null; }
                            if (!capture.TryBeginAttempt(target.Reason, out attempt)) continue;
                            settings.Validate();
                            var expected = target;
                            var starting = attempt;
                            current = new CaptureSession(settings, cache, false, () => !capture.IsCurrent(starting), target,
                                delegate { var latest = windowTarget(); return latest.Available && latest.Generation == expected.Generation; });
                            currentReplay = replays.RegisterSession(current);
                            var verified = windowTarget();
                            if (!verified.Available || verified.Generation != expected.Generation)
                            {
                                capture.SourceUnavailable(verified.Available ? CapturePhase.Starting : verified.Phase, verified.Reason);
                                replays.StopAcquisitionAsync(currentReplay.Value).GetAwaiter().GetResult(); continue;
                            }
                            var observed = current;
                            if (!capture.AttachProcess(attempt, () => observed.ProcessId != 0)) continue;
                            generation = target.Generation; capturing = true;
                            lock (gate) { session = current; sessionId = currentReplay; }
                        }
                        replays.Maintain(currentReplay.Value);
                        if (!current.Healthy) throw new IOException("Window capture stopped advancing. " + current.ErrorTail);
                        double buffered = replays.GetStatus(currentReplay.Value).BufferedSeconds;
                        if (buffered > 0) { failures = 0; capture.Publish(attempt, CapturePhase.Buffering, "Buffering · " + (int)buffered + "s · " + target.ApplicationName, true); }
                        Thread.Sleep(250);
                    }
                    catch (Exception ex)
                    {
                        capture.Publish(attempt, CapturePhase.Recovering, ex.Message.Split('\n')[0], false);
                        capture.EndAttempt(attempt);
                        if (current != null) replays.StopAcquisitionAsync(currentReplay.Value).GetAwaiter().GetResult(); capturing = false;
                        lock (gate) recoveries++;
                        Log.Write(ex.ToString()); failures++;
                        int delay = Math.Min(30, 1 << Math.Min(5, failures));
                        for (int i = 0; i < delay * 10 && !capture.Closing && !capture.Paused && !capture.RestartPending; i++) Thread.Sleep(100);
                    }
                }
            }
            finally
            {
                capture.EndAttempt(attempt);
                lock (gate) { session = null; sessionId = null; }
                if (currentReplay.HasValue) replays.RetireSessionAsync(currentReplay.Value).GetAwaiter().GetResult();
            }
        }
        public void Save(string applicationName = null)
        {
            long triggerQpc = Stopwatch.GetTimestamp();
            SaveSubmission submission;
            try
            {
                lock (gate)
                {
                    if (capture.Closing) submission = new SaveSubmission("Recorder is stopping.", null);
                    else if (capture.Paused) submission = new SaveSubmission("Replay recording is paused. Resume recording before saving.", null);
                    else if (!sessionId.HasValue) submission = new SaveSubmission("The replay buffer is warming up or recovering.", null);
                    else submission = replays.TrySave(sessionId.Value, triggerQpc, ApplicationFolders.Destination(settings.OutputDirectory, applicationName));
                    // Preserve admission order even when consecutive Save calls
                    // arrive on different threads and the first export is fast.
                    if (submission.Accepted) _ = saveNotifications.Observe(submission.Completion, SaveStarted, SaveFinished);
                }
            }
            catch (Exception ex) { NotifySave(ex.Message, false); return; }
            if (!submission.Accepted) { NotifySave(submission.Rejection, false); return; }
        }
        void NotifySave(string message, bool success) { ReplaySaveNotifications.Finish(SaveFinished, message, success); }
        public void Dispose()
        {
            capture.Close();
            // Keep the worker's protected profile lease until retirement actually
            // completes. Export has its own timeout; elapsed join time is not cleanup.
            worker.Join();
            replays.CloseAsync().GetAwaiter().GetResult();
        }
    }
}
