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
        readonly ReplayManifestStream manifestStream;
        readonly TaskCompletionSource diagnosticsCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool stdoutStarted, stderrStarted;
        readonly object gate = new object();
        readonly object producerGate = new object();
        volatile bool captureProducerAbsent = true;
        internal bool CaptureProducerAbsent => captureProducerAbsent && (audio == null || audio.ProducerAbsent);
        readonly Native.ProcessJob job = new Native.ProcessJob(recoverable: true);
        volatile bool producerLaunchSealed;
        internal ReplayProducerProof ProducerProof => ReplayProducerProof.Current(job.Name, producerLaunchSealed);
        ReplayProducerProof IReplaySessionSource.ProducerProof => ProducerProof;
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
        internal ReplayReferenceModel OutputCalibration;
        internal bool ObserveOutputDemand;
        string outputCalibrationReason = "Output/finalization calibration is not available for this recording profile.";
        internal ReplayOutputSelection LastOutputSelection;
        internal ReplayOutputDemand LastOutputDemand;
        internal ReplayExportProcessReceipt LastExport;
        internal string ReferenceScope { get; private set; }
        internal string[] CaptureArguments { get; private set; }
        internal string[] ExportArguments { get; private set; }
        volatile bool exportProducerStopped = true;
        string IReplaySessionSource.OutputDemandIdentity => ObserveOutputDemand || OutputCalibration != null ? ReplayReferenceModel.ExporterIdentity : null;
        bool IReplaySessionSource.OutputProducerStopped => exportProducerStopped;
        ReplayOutputDemand IReplaySessionSource.OutputDemandStatus => LastOutputDemand ?? ReplayOutputDemand.Unmeasured(outputCalibrationReason);
        ReplayOutputDemand IReplaySessionSource.EstimateOutput(ReplayOutputSelection selection)
        {
            LastOutputSelection = selection;
            var qualification = ObserveOutputDemand
                ? new ReplayCalibrationAvailability(OutputCalibration, outputCalibrationReason) : ReplayCalibrationCatalog.Load();
            if (!ObserveOutputDemand && OutputCalibration != null && qualification.Model?.Revision != OutputCalibration.Revision)
                return LastOutputDemand = ReplayOutputDemand.Unmeasured(qualification.Reason
                    ?? "Reference calibration changed after this capture session started.");
            return LastOutputDemand = qualification.Estimate(selection, ReferenceScope, directory);
        }
        ReplaySessionInfo IReplaySessionSource.ReplayInfo { get { return replayInfo; } }
        bool IReplaySessionSource.Stopped { get { return stopped; } }
        string IReplaySessionSource.Diagnostics { get { return ErrorTail; } }
        double IReplaySessionSource.SaveCutoffAt(long trigger) { return SaveCutoffAt(trigger); }
        void IReplaySessionSource.FreezeAt(long timestamp) { FreezeAt(timestamp); }
        void IReplaySessionSource.StopCapture() { StopCapture(); }
        void IReplaySessionSource.CheckCaptureHealth(double end) { CheckCaptureHealth(end); }
        List<Segment> IReplaySessionSource.ReadSegments() => manifestStream.Read();
        public string ErrorTail = "";
        public string DirectoryPath { get { return directory; } }
        internal int ProcessId { get { return process == null || stopped || process.HasExited ? 0 : process.Id; } }
        public bool HasExited { get { return process == null || process.HasExited; } }
        internal int? CaptureExitCode => process != null && process.HasExited ? process.ExitCode : null;
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
            get { return ReadHealth().Healthy; }
        }
        internal CaptureHealthSnapshot ReadHealth()
        {
            lock (gate)
            {
                double now = clock.Elapsed.TotalSeconds;
                bool exited = HasExited;
                string failure = audio?.Failure?.ToString();
                if (failure?.Length > 3000) failure = failure.Substring(0, 3000);
                return new CaptureHealthSnapshot(exited, exited ? CaptureExitCode : null, failure,
                    now, progress, now - progressAt, lastSegmentEnd, now - lastSegmentAt, encodedFrames, ErrorTail);
            }
        }
        public CaptureSession(Settings config, string root, bool synthetic, Func<bool> cancelled = null) : this(config, root, synthetic, cancelled, null) { }
        internal CaptureSession(Settings config, string root, bool synthetic, Func<bool> cancelled, WindowTargetSnapshot target, Func<bool> sourceStillValid = null, Action<CaptureSession> beforeLaunch = null, Action<CaptureSession> afterLaunch = null)
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
            manifestStream = new ReplayManifestStream(directory, settings.SegmentExtension, settings.ReplayMinutes * 60 + 20);
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
                    args.AddRange(new[] { "-copyts", "-thread_queue_size", "64", "-probesize", "32", "-analyzeduration", "0" });
                    // The two-channel PCM bridge has a known stereo layout.
                    // Declare it instead of asking FFmpeg to guess and warn.
                    if (audio.Channels == 2) args.AddRange(new[] { "-ch_layout", "stereo" });
                    args.AddRange(new[] { "-f", "matroska", "-i", @"\\.\pipe\" + name });
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
                args.AddRange(new[] { "-c:v", settings.VideoCodec, "-preset", "p3", "-tune", "ll", "-rc", "cbr", "-b:v", settings.BitrateMbps + "M", "-maxrate", settings.BitrateMbps + "M", "-bufsize", (settings.BitrateMbps * 2) + "M", "-g", settings.FramesPerSecond.ToString(), "-bf", "0", "-forced-idr", "1", "-force_key_frames", "expr:gte(t,n_forced*1)", "-flags", "+cgop", "-f", "segment", "-segment_time", "1", "-segment_format", settings.SegmentFormat, "-reset_timestamps", "1" });
                args.AddRange(ReplayManifestStream.OutputArguments());
                args.Add(Path.Combine(directory, "chunk-%09d" + settings.SegmentExtension));
                var argumentProbe = RecorderRuntime.CaptureArgumentProbe;
                if (argumentProbe != null) args = argumentProbe(args);
                CaptureArguments = args.ToArray();
                RecorderRuntime.CaptureArgumentObserver?.Invoke(CaptureArguments);
                if (!synthetic && argumentProbe == null && ReplayReferenceModel.ReferenceSettings(settings))
                {
                    var bounds = Native.DisplayBounds(IntPtr.Zero);
                    if (bounds.Width == 1920 && bounds.Height == 1080)
                    {
                        var normalized = args.Select(a => a.Replace(directory, "<session>")
                            .Replace(mediaClock.VideoTimestampFilter, "<common-clock-filter>"))
                            .Select(a => a.StartsWith(@"\\.\pipe\F10Recorder-", StringComparison.Ordinal) ? "<audio-pipe>" : a).ToArray();
                        ReferenceScope = ReplayReferenceModel.Hash(new { arguments = normalized, width = bounds.Width, height = bounds.Height,
                            endpoint = audio.EndpointId, audio.SampleRate, audio.Channels, audio.FfmpegFormat,
                            exporter = ReplayReferenceModel.ExporterIdentity });
                    }
                }
                if (!synthetic && settings.CaptureMode == "window") Log.Write("Experimental window capture arguments: " + string.Join(" ", args.Select(Quote)));
                if (ReferenceScope != null)
                {
                    var qualification = ReplayCalibrationCatalog.Load();
                    OutputCalibration = qualification.Model;
                    outputCalibrationReason = qualification.Reason ?? "Waiting for a closed reference replay selection.";
                }
                else outputCalibrationReason = "This recording profile is outside the measured reference envelope.";
                if (sourceStillValid != null && !sourceStillValid()) throw new OperationCanceledException("The selected window changed before capture could start.");
                beforeLaunch?.Invoke(this);
                lock (producerGate)
                {
                    if (stopped || cancelled?.Invoke() == true) throw new OperationCanceledException("Capture was cancelled before acquisition.");
                    // Unknown creation outcomes retain the guard and growth claim.
                    captureProducerAbsent = false;
                    process = StartProcess(executable, args, directory);
                    job.Add(process);
                }
                process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data == null) { manifestStream.Complete(); return; }
                    if (manifestStream.Accept(e.Data)) return;
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
                    if (e.Data == null) { diagnosticsCompleted.TrySetResult(); return; }
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
                process.BeginOutputReadLine(); stdoutStarted = true; process.BeginErrorReadLine(); stderrStarted = true;
                if (pipe != null)
                {
                    AudioPipe.Accept(pipe, process, cancelled, 10000);
                    lock (producerGate)
                    {
                        if (stopped || cancelled?.Invoke() == true) throw new OperationCanceledException("Capture was cancelled before audio acquisition.");
                        audio.Start(pipe, mediaClock);
                    }
                }
                producerLaunchSealed = true;
                afterLaunch?.Invoke(this);
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
            lock (producerGate)
            {
                if (stopped) return;
                FreezeAt(Stopwatch.GetTimestamp());
                bool absent = captureProducerAbsent;
                Exception drainFailure = null;
                if (process != null)
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.StandardInput.WriteLine("q"); process.StandardInput.Flush();
                            if (!process.WaitForExit(1500)) { process.Kill(); absent = process.WaitForExit(5000); }
                            else absent = true;
                        }
                        else absent = true;
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
                    {
                        // Failure to request a graceful stop is not proof of exit.
                        try { if (!process.HasExited) process.Kill(); absent = process.WaitForExit(5000); }
                        catch (Exception failure) when (failure is InvalidOperationException or IOException or System.ComponentModel.Win32Exception) { absent = false; }
                    }
                }
                if (absent && stdoutStarted)
                {
                    try { manifestStream.WaitForCompletion(2000); }
                    catch (IOException ex) { drainFailure = ex; }
                }
                if (absent && stderrStarted && !diagnosticsCompleted.Task.Wait(2000))
                    drainFailure ??= new IOException("Capture diagnostics did not drain before the shutdown deadline.");
                try { audio?.Dispose(); pipe?.Dispose(); }
                catch { absent = false; throw; }
                finally { captureProducerAbsent = absent; stopped = true; }
                if (drainFailure != null) throw drainFailure;
            }
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
            manifestStream.CheckHealthy();
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
            => Export(arguments, snapshot, null);
        void IReplaySessionSource.Export(IReadOnlyList<string> arguments, string snapshot, Action<ReplayProducerProof> producerChanged)
            => Export(arguments, snapshot, producerChanged);
        void IReplaySessionSource.Export(IReadOnlyList<string> arguments, string snapshot, Action<ReplayProducerProof> producerChanged, Action checkStorage)
            => Export(arguments, snapshot, producerChanged, checkStorage);
        void Export(IReadOnlyList<string> arguments, string snapshot, Action<ReplayProducerProof> producerChanged, Action checkStorage = null)
        {
            ExportArguments = arguments.ToArray();
            RecorderRuntime.ExportArgumentObserver?.Invoke(ExportArguments);
            LastExport = ReplayExportProcess.Run(MediaDependency.ExecutablePath, arguments, snapshot, job,
                started: _ =>
                {
                    producerLaunchSealed = true; // The native job owns this child now.
                    producerChanged?.Invoke(ProducerProof);
                },
                producerStopped: absent =>
                {
                    if (!absent)
                    {
                        // Maintenance reads this same proof; it must never
                        // reseal an export still between CreateProcess and Add.
                        producerLaunchSealed = false;
                        producerChanged?.Invoke(ProducerProof);
                    }
                    exportProducerStopped = absent;
                }, checkStorage: checkStorage);
            if (!LastExport.Success)
            {
                if (LastExport.ProducerAbsent)
                    Log.Write(FormattableString.Invariant($"Replay export guard invocation: work={LastExport.LastStorageCheckWorkMilliseconds:F3}ms; preceding idle={LastExport.LastStorageCheckIdleMilliseconds:F3}ms; producer absent=True."));
                throw new IOException("Replay save failed (" + LastExport.Failure + "): "
                    + LastExport.Error + " " + LastExport.StandardError[..Math.Min(4096, LastExport.StandardError.Length)]);
            }
        }
        public static Process StartProcess(string executable, IEnumerable<string> args, string workDirectory)
        {
            // Every FFmpeg sink authenticates bytes and retains a read-only lease
            // through CreateProcess, including callers that retained an older path.
            using (var lease = MediaDependency.OpenVerifiedExecutable(executable))
                return StartVerifiedProcess(lease, args);
        }
        internal static Process StartVerifiedProcess(MediaDependency.ExecutableLease lease, IEnumerable<string> args)
        {
            ArgumentNullException.ThrowIfNull(lease);
            var info = new ProcessStartInfo(lease.PathName, string.Join(" ", args.Select(Quote))) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true,
                WorkingDirectory = Path.GetDirectoryName(lease.PathName) };
            info.EnvironmentVariables["PATH"] = Environment.SystemDirectory;
            return lease.Start(info);
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
            try { StopCapture(); }
            finally
            {
                job.Dispose();
                lock (producerGate)
                {
                    // Latch evidence while the owned handle is still valid. No caller
                    // queries a disposed Process as a substitute for an exit receipt.
                    if (!captureProducerAbsent && process != null)
                    {
                        try { captureProducerAbsent = process.WaitForExit(5000); }
                        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                    }
                    if (process != null) process.Dispose();
                }
                if (!string.IsNullOrWhiteSpace(ErrorTail)) Log.Write("Capture diagnostics: " + ErrorTail);
            }
        }
    }
    public sealed class ReplayEngine : IDisposable
    {
        readonly Settings settings;
        readonly string cache;
        readonly IDisposable cacheLease;
        readonly object gate = new object();
        readonly Thread worker;
        readonly bool synthetic;
        readonly Func<WindowTargetSnapshot> windowTarget;
        readonly CaptureLifecycle capture;
        CaptureSession session;
        readonly ReplayCache replays;
        readonly ReplayStorageCoordinator storage;
        CaptureSession storageSession;
        volatile bool draining;
        volatile bool resumeStorage;
        int preserveForStorage;
        internal bool StorageHeld => storage.Hold.Held;
        internal bool Paused => capture.Paused;
        internal event Action<string> StorageWarning;
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
                    storage.OutputDemand.WriteMetrics(result);
                    result.ReplayStorageError = ReplayOutputDemand.Bounded(storage.StorageError, 512);
                    this.storage.WriteMetrics(result);
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
        internal ReplayEngine(Settings config, string dataDirectory, bool startPaused, bool syntheticSource, Func<WindowTargetSnapshot> targetProvider, ReplayStorageCoordinator storageCoordinator = null, bool explicitStorageResume = false)
        {
            windowTarget = targetProvider;
            storage = storageCoordinator ?? new ReplayStorageCoordinator(new ReplayStorageStopLatch(dataDirectory),
                new ReplayVolumeReservations(ProfileOwnership.RootDirectory, ReplayStorageSafety.MinimumBytes));
            resumeStorage = explicitStorageResume;
            storage.Warning += ForwardStorageWarning;
            settings = config; capture = new CaptureLifecycle(startPaused || !explicitStorageResume && storage.Hold.Held); synthetic = syntheticSource; cache = ReplayCacheLocations.Current(config, dataDirectory);
            Directory.CreateDirectory(cache);
            cacheLease = new TrustedStorage.ProfileDirectoryLease(cache);
            cache = ((TrustedStorage.ProfileDirectoryLease)cacheLease).DirectoryPath;
            // Old-session and failed-cleanup material still consumes this profile's
            // budget. Startup must not erase it merely to admit new capture.
            try
            {
                replays = new ReplayCache(cacheDirectory: cache, cacheBudgetBytes: config.ReplayCacheBudgetBytes, retainedDirectories: ReplayCacheLocations.Roots(config, dataDirectory),
                    volumeReservations: new ReplayVolumeReservations(ProfileOwnership.RootDirectory, ReplayStorageSafety.MinimumBytes));
                worker = new Thread(Run) { IsBackground = true, Name = "Capture supervisor" }; worker.Start();
            }
            catch { cacheLease.Dispose(); throw; }
        }
        public void Restart() => capture.Restart();
        public void Pause(bool value)
        {
            if (!value && capture.Paused) RequireResumeReady();
            if (!value && storage.Hold.Held) { RequireStorageResume(); resumeStorage = true; }
            capture.Pause(value);
        }
        internal void RequireResumeReady()
        {
            if (draining || IsSaving) throw new IOException("Wait for capture and accepted replay saves to finish stopping.");
            if (storageSession != null)
            {
                if (!storageSession.CaptureProducerAbsent) throw new IOException("Wait for capture producer absence to be confirmed.");
                CompleteStorageCapture();
            }
        }
        internal void RequireStorageResume()
        {
            RequireResumeReady();
            storage.RequireResume(cache, settings.OutputDirectory);
        }
        void ForwardStorageWarning(string reason) => StorageWarning?.Invoke(reason);
        void BeforeCaptureLaunch(CaptureSession source)
        {
            storageSession = source;
            bool resume = resumeStorage; resumeStorage = false;
            Interlocked.Exchange(ref preserveForStorage, 0);
            storage.BeginCapture(source.ReferenceScope, source.DirectoryPath, settings.OutputDirectory, () =>
            {
                // Invalidate readiness before metadata/persistence. Request the
                // actual stop on a separate thread before writing the cause.
                capture.Pause(true);
                Interlocked.Exchange(ref preserveForStorage, 1);
                _ = Task.Run(() =>
                {
                    try { source.StopCapture(); }
                    catch (Exception ex) { Log.Write("Storage stop could not confirm capture closure: " + ex); }
                });
            }, resume, source.ProducerProof);
            try { replays.ValidateBudget(settings.ReplayCacheBudgetBytes); }
            catch (ReplayCacheBudgetExceededException ex) { storage.StopForStorageFailure(ex.Message); throw; }
            storage.StartSupervision();
        }
        void AfterCaptureLaunch(CaptureSession source) => storage.SealProducer(source.ProducerProof);
        void PreserveStorageSession(ReplaySessionId? id)
        {
            if (id.HasValue && (storage.Stopped || Volatile.Read(ref preserveForStorage) != 0)) replays.PreserveSession(id.Value);
        }
        void CompleteStorageCapture()
        {
            if (storageSession == null) return;
            bool absent = storageSession.CaptureProducerAbsent;
            storage.CompleteCapture(absent);
            if (storage.Stopped || storage.Hold.Held) capture.Pause(true);
            // Retain the latched process proof and actual audio-thread observer
            // while closure is unknown; a later explicit Resume may recheck it.
            if (absent) storageSession = null;
        }
        void RetireCapture(ReplaySessionId? id)
        {
            draining = true;
            try
            {
                if (id.HasValue)
                {
                    storage.StopAndPreserve(replays, id.Value,
                        () => storageSession == null || storageSession.CaptureProducerAbsent,
                        () => Volatile.Read(ref preserveForStorage) != 0);
                    CompleteStorageCapture();
                    // Supervision is now joined; no late poll can change this
                    // retention decision while cache retirement drains exports.
                    PreserveStorageSession(id);
                    replays.RetireSessionAsync(id.Value).GetAwaiter().GetResult();
                }
                else storageSession?.Dispose();
            }
            finally { CompleteStorageCapture(); draining = false; }
        }
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
                    current = new CaptureSession(settings, cache, synthetic, () => !capture.IsCurrent(attempt), null, beforeLaunch: BeforeCaptureLaunch, afterLaunch: AfterCaptureLaunch);
                    currentReplay = replays.RegisterSession(current);
                    PreserveStorageSession(currentReplay);
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
                    if (ex is ReplayCacheBudgetExceededException) storage.StopForStorageFailure(ex.Message);
                    failures++; lock (gate) recoveries++; Log.Write(ex.ToString());
                    capture.Publish(attempt, CapturePhase.Recovering, ex.Message.Split('\n')[0], false);
                }
                finally
                {
                    capture.EndAttempt(attempt);
                    lock (gate) { session = null; sessionId = null; }
                    RetireCapture(currentReplay);
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
                            if (capturing) { PreserveStorageSession(currentReplay); replays.StopAcquisitionAsync(currentReplay.Value).GetAwaiter().GetResult(); CompleteStorageCapture(); capturing = false; }
                            if (capture.Paused || capture.RestartPending)
                            {
                                lock (gate) { session = null; sessionId = null; }
                                if (currentReplay.HasValue) { RetireCapture(currentReplay); current = null; currentReplay = null; }
                                capture.CompleteReset(reset);
                                if (!target.Available) capture.SourceUnavailable(target.Phase, target.Reason);
                            }
                            if (capture.Paused || !target.Available) { Thread.Sleep(250); continue; }
                        }
                        if (!capturing)
                        {
                            lock (gate) { session = null; sessionId = null; }
                            if (capture.Closing) break;
                            if (currentReplay.HasValue) { RetireCapture(currentReplay); current = null; currentReplay = null; }
                            if (!capture.TryBeginAttempt(target.Reason, out attempt)) continue;
                            settings.Validate();
                            var expected = target;
                            var starting = attempt;
                            current = new CaptureSession(settings, cache, false, () => !capture.IsCurrent(starting), target,
                                delegate { var latest = windowTarget(); return latest.Available && latest.Generation == expected.Generation; }, BeforeCaptureLaunch, AfterCaptureLaunch);
                            currentReplay = replays.RegisterSession(current);
                            PreserveStorageSession(currentReplay);
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
                        if (ex is ReplayCacheBudgetExceededException) storage.StopForStorageFailure(ex.Message);
                        capture.Publish(attempt, CapturePhase.Recovering, ex.Message.Split('\n')[0], false);
                        capture.EndAttempt(attempt);
                        draining = true;
                        try
                        {
                            PreserveStorageSession(currentReplay);
                            if (currentReplay.HasValue) replays.StopAcquisitionAsync(currentReplay.Value).GetAwaiter().GetResult();
                            else storageSession?.Dispose();
                        }
                        finally { CompleteStorageCapture(); draining = false; }
                        capturing = false;
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
                RetireCapture(currentReplay);
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
            try { replays.CloseAsync().GetAwaiter().GetResult(); }
            finally { storage.Warning -= ForwardStorageWarning; cacheLease.Dispose(); }
        }
    }
}
