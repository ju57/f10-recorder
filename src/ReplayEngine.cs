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
        readonly string directory, manifest;
        readonly object gate = new object();
        readonly HashSet<string> pinned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly Native.ProcessJob job = new Native.ProcessJob();
        readonly Stopwatch clock = Stopwatch.StartNew();
        Process process;
        LoopbackAudio audio;
        NamedPipeServerStream pipe;
        double progress, progressAt;
        double lastSegmentEnd, lastSegmentAt;
        long encodedFrames, reportedDrops, reportedDuplicates, cacheBytes;
        double encodingSpeed, metricsAt, cpuSeconds, cpuPercent;
        volatile bool disposed;
        public string ErrorTail = "";
        public string DirectoryPath { get { return directory; } }
        internal int ProcessId { get { return process == null ? 0 : process.Id; } }
        public bool HasExited { get { return process == null || process.HasExited; } }
        public double Position { get { lock (gate) return progress > 0 ? progress + Math.Min(0.5, clock.Elapsed.TotalSeconds - progressAt) : 0; } }
        public double BufferedSeconds { get { lock (gate) { var list = ReadSegments(); return list.Count == 0 ? 0 : Math.Min(settings.ReplayMinutes * 60, list.Last().End - list.First().Start); } } }
        public bool Healthy
        {
            get { return !HasExited && (audio == null || audio.Failure == null) && clock.Elapsed.TotalSeconds - lastSegmentAt < 15 && clock.Elapsed.TotalSeconds - progressAt < 15; }
        }
        public CaptureSession(Settings config, string root, bool synthetic, Func<bool> cancelled = null)
        {
            settings = config;
            directory = Path.Combine(root, "session-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            manifest = Path.Combine(directory, "segments.csv");
            try
            {
                var args = new List<string> { "-hide_banner", "-loglevel", "warning", "-y", "-stats_period", "0.25", "-progress", "pipe:1" };
                if (synthetic) args.AddRange(new[] { "-re", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=" + settings.FramesPerSecond });
                // ddagrab has no background buffer. A lavfi demuxer can leave it
                // unpolled while raw audio is read, losing changing desktop frames.
                else args.AddRange(new[] { "-filter_complex", "ddagrab=framerate=" + settings.FramesPerSecond + ":output_idx=" + (settings.DisplayIndex < 0 ? Native.PrimaryOutputIndex() : settings.DisplayIndex) + ":draw_mouse=1[video]" });
                if (synthetic) args.AddRange(new[] { "-re", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000" });
                else if (settings.SystemAudio)
                {
                    audio = new LoopbackAudio();
                    string name = "F10Recorder-" + Guid.NewGuid().ToString("N");
                    pipe = new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 65536, 65536);
                    args.AddRange(new[] { "-thread_queue_size", "1024", "-f", audio.FfmpegFormat, "-ar", audio.SampleRate.ToString(), "-ac", audio.Channels.ToString(), "-i", @"\\.\pipe\" + name });
                }
                args.AddRange(new[] { "-map", synthetic ? "0:v:0" : "[video]" });
                if (synthetic || settings.SystemAudio) args.AddRange(new[] { "-map", synthetic ? "1:a:0" : "0:a:0", "-af", "aresample=async=1:first_pts=0", "-ac", "2", "-ar", "48000", "-c:a", "aac", "-b:a", "192k" });
                else args.Add("-an");
                args.AddRange(new[] { "-c:v", settings.VideoCodec, "-preset", "p3", "-tune", "ll", "-rc", "cbr", "-b:v", settings.BitrateMbps + "M", "-maxrate", settings.BitrateMbps + "M", "-bufsize", (settings.BitrateMbps * 2) + "M", "-g", settings.FramesPerSecond.ToString(), "-bf", "0", "-forced-idr", "1", "-force_key_frames", "expr:gte(t,n_forced*1)", "-flags", "+cgop", "-f", "segment", "-segment_time", "1", "-segment_format", settings.SegmentFormat, "-reset_timestamps", "1", "-segment_list", manifest, "-segment_list_type", "csv", "-segment_list_size", (settings.ReplayMinutes * 60 + 20).ToString(), Path.Combine(directory, "chunk-%09d" + settings.SegmentExtension) });
                var argumentProbe = RecorderRuntime.CaptureArgumentProbe;
                if (argumentProbe != null) args = argumentProbe(args);
                process = StartProcess(settings.FfmpegPath, args, directory);
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
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) { lock (gate) { ErrorTail = (ErrorTail + "\n" + e.Data); if (ErrorTail.Length > 3000) ErrorTail = ErrorTail.Substring(ErrorTail.Length - 3000); } } };
                process.BeginOutputReadLine(); process.BeginErrorReadLine();
                if (pipe != null)
                {
                    var connected = pipe.BeginWaitForConnection(null, null);
                    var timeout = Stopwatch.StartNew();
                    while (!connected.AsyncWaitHandle.WaitOne(100))
                    {
                        if (cancelled != null && cancelled()) throw new OperationCanceledException();
                        if (process.HasExited || timeout.Elapsed.TotalSeconds > 10) throw new IOException("FFmpeg did not connect to system audio. " + ErrorTail);
                    }
                    pipe.EndWaitForConnection(connected); connected.AsyncWaitHandle.Close();
                    audio.Start(pipe);
                }
                Log.Write("Capture started; PID " + process.Id + "; " + directory);
            }
            catch { Dispose(); throw; }
        }
        List<Segment> ReadSegments() { try { return ReplaySelection.Read(manifest, directory); } catch (IOException) { return new List<Segment>(); } }
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
                    result.CaptureProcessId = process.Id; result.CaptureProgressSeconds = progress;
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
                        if (available.Count > 0 && available.Last().End >= end)
                        {
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
                    using (Process mux = StartProcess(settings.FfmpegPath, arguments, snapshot))
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
            return Process.Start(info);
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
            if (process != null)
            {
                try { if (!process.HasExited) { process.StandardInput.WriteLine("q"); process.StandardInput.Flush(); if (!process.WaitForExit(1500)) process.Kill(); } } catch (InvalidOperationException) { } catch (IOException) { }
            }
            if (audio != null) audio.Dispose();
            if (pipe != null) pipe.Dispose();
            job.Dispose();
            if (process != null) process.Dispose();
        }
    }
    public sealed class ReplayEngine : IDisposable
    {
        readonly Settings settings;
        readonly string cache;
        readonly object gate = new object();
        readonly Thread worker;
        readonly bool synthetic;
        volatile bool closing, paused, restart;
        CaptureSession session;
        int saving;
        int recoveries;
        double lastSaveMilliseconds;
        long lastSaveBytes;
        bool lastSaveSucceeded;
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
        public event Action<string, bool> SaveFinished;
        public event Action SaveStarted;
        public ReplayEngine(Settings config, string dataDirectory, bool startPaused = false) : this(config, dataDirectory, startPaused, false) { }
        internal ReplayEngine(Settings config, string dataDirectory, bool startPaused, bool syntheticSource)
        {
            settings = config; paused = startPaused; synthetic = syntheticSource; cache = Path.Combine(dataDirectory, "buffer");
            Directory.CreateDirectory(cache);
            // Only this application's disposable cache. Saved clips live elsewhere.
            foreach (string old in Directory.GetDirectories(cache, "session-*")) CaptureSession.TryDeleteDirectory(old);
            worker = new Thread(Run) { IsBackground = true, Name = "Capture supervisor" }; worker.Start();
        }
        void State(string message, bool ready) { var callback = StateChanged; if (callback != null) callback(message, ready); }
        public void Restart() { restart = true; }
        public void Pause(bool value) { paused = value; restart = true; }
        internal int CaptureProcessId { get { lock (gate) return session == null ? 0 : session.ProcessId; } }
        void Run()
        {
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
        public void Save(string applicationName = null)
        {
            if (closing) { NotifySave("Recorder is stopping.", false); return; }
            if (paused) { NotifySave("Replay recording is paused. Resume recording before saving.", false); return; }
            if (Interlocked.CompareExchange(ref saving, 1, 0) != 0) { NotifySave("A replay is already being saved.", false); return; }
            CaptureSession current; double end;
            lock (gate) { current = session; end = current == null ? 0 : current.Position; }
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
