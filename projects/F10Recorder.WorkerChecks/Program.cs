using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;

namespace F10Recorder;

internal static class WorkerChecks
{
    static string data, desktop;
    static readonly Dictionary<string, object> report = new();
    static readonly List<string> clips = [];
    static readonly List<RecorderMetrics> samples = [];
    static readonly List<object> saves = [];
    [STAThread]
    static int Main(string[] args)
    {
        if (args[0] == "--worker")
        {
            string fault = Path.Combine(args[1], "controlled-health-failure.txt");
            RecorderRuntime.CaptureHealthProbe = () =>
            {
                if (!File.Exists(fault)) return null;
                string error = File.ReadAllText(fault); File.Delete(fault); return error;
            };
            return RecorderWorker.Run(args[1], false, args[2], true, _ => { }, (_, folder) => ScreenshotCapture.CaptureSource("testsrc2=size=640x360:rate=1", folder, false));
        }
        data = Path.GetFullPath(args[0]); desktop = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(data); WindowsRecorder.Initialize(data);
        var settings = new Settings { ReplayMinutes = 1, FramesPerSecond = 30, BitrateMbps = 5, OutputDirectory = Path.Combine(data, "clips"), ScreenshotDirectory = Path.Combine(data, "screenshots"), SaveKey = 121, SaveModifiers = 7, OverlayKey = 122, OverlayModifiers = 7, ScreenshotKey = 123, ScreenshotModifiers = 7 };
        settings.Save(Path.Combine(data, "settings.json"));
        report["mediaEngine"] = MediaDependency.ResolveExecutable();
        report["captureSource"] = "Synthetic 640x360/30 testsrc2 and 440Hz/48kHz tone; no desktop recording";
        try { Check(int.Parse(args[2])).GetAwaiter().GetResult(); report["result"] = "passed"; }
        catch (Exception ex) { report["result"] = "failed"; report["error"] = ex.ToString(); }
        File.WriteAllText(Path.Combine(data, "verification.json"), JsonSerializer.Serialize(report, WorkerProtocol.Json));
        return (string)report["result"] == "passed" ? 0 : 1;
    }
    static Process Launch(string executable, params string[] args)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in args) info.ArgumentList.Add(arg);
        return Process.Start(info) ?? throw new IOException("Test process did not start.");
    }
    static Task<WorkerReply> Call(string command = "status", bool value = false, Settings settings = null, long after = -1) => WorkerProtocol.CallAsync(data, new WorkerRequest { Command = command, Value = value, Settings = settings, AfterEvent = after });
    static void Require(bool condition, string error) { if (!condition) throw new Exception(error); }
    static async Task<WorkerStatus> Until(Func<WorkerStatus, bool> predicate, string error, int seconds = 30)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            var reply = await Call(); Require(reply.Error == null, reply.Error);
            if (predicate(reply.Status)) return reply.Status;
            await Task.Delay(100);
        }
        throw new TimeoutException(error);
    }
    static async Task UntilUi(Process ui, int workerPid)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < 20)
        {
            if (ui.HasExited) throw new Exception("Production UI exited unexpectedly.");
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(data, "status.json")));
                if (json.RootElement.GetProperty("WorkerProcessId").GetInt32() == workerPid && json.RootElement.GetProperty("UiProcessId").GetInt32() == ui.Id) return;
            }
            catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException) { }
            await Task.Delay(100);
        }
        throw new TimeoutException("Production UI did not attach to the worker.");
    }
    static async Task<WorkerEvent> Save(bool expected, bool hotkeyDispatch = false)
    {
        long sequence = (await Call()).Status.Sequence;
        if (hotkeyDispatch)
            Require(Native.PostMessage(Native.FindWindow(null, WorkerIdentity.ControlTitle(data)), 0x0312, new IntPtr(1), IntPtr.Zero), "Headless worker hotkey dispatch failed.");
        else { var begin = await Call("save"); Require(begin.Error == null, begin.Error); }
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < 20)
        {
            var response = await Call(after: sequence);
            var completed = response.Status.Events.FirstOrDefault(e => e.Kind == "saved");
            if (completed != null)
            {
                Require(completed.Success == expected, "Unexpected replay save result: " + completed.Message);
                saves.Add(new { success = completed.Success, encoder_save_ms = response.Status.Metrics.LastSaveMilliseconds, command_to_completion_ms = clock.Elapsed.TotalMilliseconds, bytes = response.Status.Metrics.LastSaveBytes });
                if (expected) { Require(File.Exists(completed.Message), "Successful replay file is missing."); clips.Add(completed.Message); }
                return completed;
            }
            await Task.Delay(100);
        }
        throw new TimeoutException("Replay completion event did not arrive.");
    }
    static Settings Copy(Settings config) => JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(config, WorkerProtocol.Json), WorkerProtocol.Json);
    static async Task Check(int soakSeconds)
    {
        Process worker = null, ui = null;
        try
        {
            worker = Launch(Environment.ProcessPath, "--worker", data, desktop);
            var initial = await Until(s => s.Ready && s.Metrics.CaptureProgressSeconds >= 5, "Worker did not begin buffering.");
            Require(initial.WorkerProcessId == worker.Id, "Unexpected worker ownership.");
            Require(initial.SaveRegistered && initial.OverlayRegistered && initial.ScreenshotRegistered, "Worker did not own all shortcuts.");
            ui = Launch(desktop, "--data-dir", data, "--settings"); await UntilUi(ui, worker.Id);
            int capture = initial.Metrics.CaptureProcessId;
            double progress = (await Call()).Status.Metrics.CaptureProgressSeconds;
            ui.Kill(); await ui.WaitForExitAsync(); ui.Dispose(); ui = null;
            await Task.Delay(2500);
            var survived = (await Call()).Status;
            Require(survived.Ready && survived.Metrics.CaptureProcessId == capture && survived.Metrics.CaptureProgressSeconds > progress, "UI crash interrupted capture.");
            await Save(true, true); report["uiCrashAndHeadlessSave"] = true;
            ui = Launch(desktop, "--data-dir", data); await UntilUi(ui, worker.Id);
            Require((await Call()).Status.Metrics.CaptureProcessId == capture, "UI reattachment rebuilt the buffer."); report["uiReattachmentPreservesBuffer"] = true;
            using (var command = Launch(desktop, "--data-dir", data, "--overlay"))
                await command.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(500);
            Require((await Call("screenshot")).Error == null, "Remote screenshot command failed.");
            var screenshot = await Until(s => s.LastScreenshot != null && !s.ScreenshotInProgress, "Cross-process screenshot preparation did not complete.");
            Require(File.Exists(screenshot.LastScreenshot), "Synthetic screenshot is missing.");
            using (var uiStatus = JsonDocument.Parse(File.ReadAllText(Path.Combine(data, "status.json"))))
                Require(!uiStatus.RootElement.GetProperty("OverlayVisible").GetBoolean(), "Screenshot preparation left the UI overlay visible.");
            report["remoteScreenshotPreparation"] = screenshot.LastScreenshot;

            using (var pipe = new NamedPipeClientStream(".", WorkerIdentity.PipeName(data), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
            {
                await pipe.ConnectAsync(2000);
                byte[] oversized = BitConverter.GetBytes(WorkerProtocol.MaxBytes + 1); await pipe.WriteAsync(oversized);
                using var limit = new CancellationTokenSource(3000); byte[] one = new byte[1];
                Require(await pipe.ReadAsync(one, limit.Token) == 0, "Oversized request was not rejected.");
            }
            var wrongVersion = await WorkerProtocol.CallAsync(data, new WorkerRequest { Version = 2 });
            Require(wrongVersion.Error != null && (await Call()).Status.Ready, "Bad protocol request affected capture."); report["boundedProtocolRejection"] = true;

            var original = Copy((await Call()).Status.Config);
            var invalid = Copy(original); invalid.VideoCodec = "invalid";
            Require((await Call("apply", settings: invalid)).Error != null && (await Call()).Status.Config.VideoCodec == original.VideoCodec, "Invalid settings changed live capture.");
            Require(Settings.Load(Path.Combine(data, "settings.json")).VideoCodec == original.VideoCodec, "Invalid settings changed persisted capture."); report["invalidApplyRollback"] = true;

            var blocked = Copy(original); blocked.OutputDirectory = Path.Combine(data, "blocked-output"); File.WriteAllText(blocked.OutputDirectory, "Controlled non-directory output target");
            Require((await Call("apply", settings: blocked)).Error == null, "Controlled output settings did not apply.");
            await Until(s => s.Ready && s.Metrics.CaptureProgressSeconds > 4, "Capture did not recover after settings change.");
            await Save(false); Require((await Call()).Status.Ready, "Output failure stopped buffering.");
            Require((await Call("apply", settings: original)).Error == null, "Output settings did not restore.");
            await Until(s => s.Ready && s.Metrics.CaptureProgressSeconds > 4, "Restored capture did not become ready."); await Save(true);
            report["unwritableOutputAndRecovery"] = "Non-directory destination failure; actual volume exhaustion not simulated";

            var before = (await Call()).Status;
            using (var encoder = Process.GetProcessById(before.Metrics.CaptureProcessId))
            {
                Require(Path.GetFullPath(encoder.MainModule.FileName).Equals(Path.GetFullPath((string)report["mediaEngine"]), StringComparison.OrdinalIgnoreCase), "Unexpected encoder process identity.");
                encoder.Kill();
            }
            await Until(s => s.Ready && s.Metrics.CaptureProcessId != before.Metrics.CaptureProcessId, "Encoder crash did not recover."); report["encoderCrashRecovery"] = true;
            before = (await Call()).Status;
            Native.PostMessage(Native.FindWindow(null, WorkerIdentity.ControlTitle(data)), 0x007E, IntPtr.Zero, IntPtr.Zero);
            await Until(s => s.Ready && s.Metrics.CaptureProcessId != before.Metrics.CaptureProcessId, "Display-change message did not recover capture."); report["displayChangeMessageRecovery"] = true;
            before = (await Call()).Status;
            File.WriteAllText(Path.Combine(data, "controlled-health-failure.txt"), "Controlled audio endpoint change at capture-health boundary.");
            await Until(s => s.Ready && s.Metrics.CaptureProcessId != before.Metrics.CaptureProcessId && s.Metrics.Recoveries > before.Metrics.Recoveries, "Controlled audio-health failure did not recover capture.");
            report["audioHealthRecovery"] = "Injected health-boundary failure; physical audio endpoint was unchanged";

            var soak = Stopwatch.StartNew(); int nextSave = 15;
            while (soak.Elapsed.TotalSeconds < soakSeconds)
            {
                var snapshot = (await Call()).Status;
                Require(snapshot.Ready && snapshot.Metrics.CaptureProcessId != 0, "Soak capture stopped advancing.");
                samples.Add(snapshot.Metrics);
                if (soak.Elapsed.TotalSeconds >= nextSave) { await Save(true); nextSave += 30; }
                await Task.Delay(1000);
            }
            await Save(true);
            Require(samples.Any(s => s.EncodedFrames > 0 && s.CacheBytes > 0 && s.CaptureWorkingSetBytes > 0), "Capture measurements were not populated.");
            double bound = original.BitrateMbps * 1000000d / 8 * (original.ReplayMinutes * 60 + 15) * 1.5;
            Require(samples.Max(s => s.CacheBytes) < bound, "Rolling cache exceeded its measured bitrate allowance.");
            report["soakSeconds"] = soak.Elapsed.TotalSeconds; report["cacheBoundBytes"] = bound;
            report["samples"] = samples; report["clips"] = clips; report["saves"] = saves;
            report["metricsMeaning"] = "CPU normalized across logical processors; drops/duplicates are FFmpeg progress counters; GPU load and gaming FPS are not measured";
            var hashes = clips.ToDictionary(p => p, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));

            await Call("pause", true); await Until(s => s.Paused && s.Metrics.CaptureProcessId == 0, "Pause did not stop encoder.");
            Require((await Call("quit")).Error == null, "Worker did not acknowledge explicit Quit.");
            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await ui.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); ui.Dispose(); ui = null;
            report["explicitQuitStopsWorkerAndUi"] = true; worker.Dispose();
            worker = Launch(Environment.ProcessPath, "--worker", data, desktop);
            var restored = await Until(s => s.Paused, "Worker restart lost persisted pause.");
            Require(restored.Metrics.CaptureProcessId == 0, "Paused worker restart launched an encoder.");
            Require(hashes.All(h => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(h.Key))) == h.Value), "Restart changed a saved clip.");
            report["pausePersistenceAndSavedClipPreservation"] = true;
            await Call("quit"); await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            if (ui != null) { if (!ui.HasExited) ui.Kill(); ui.Dispose(); }
            if (worker != null)
            {
                if (!worker.HasExited) { try { await Call("quit"); await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch { worker.Kill(); await worker.WaitForExitAsync(); } }
                worker.Dispose();
            }
        }
    }
}
