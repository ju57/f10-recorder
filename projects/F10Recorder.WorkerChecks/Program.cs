using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace F10Recorder;

internal static class WorkerChecks
{
    static string data, desktop;
    static bool live;
    static bool gpuFixture;
    static int liveFps = 60;
    const string StartupKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    readonly record struct StartupState(object Value, RegistryValueKind Kind);
    static StartupState startupBefore;
    static readonly Dictionary<string, object> report = new();
    static readonly List<string> clips = [];
    static readonly List<RecorderMetrics> samples = [];
    static readonly List<object> saves = [];
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Any(value => value.StartsWith("--pilot-storage", StringComparison.OrdinalIgnoreCase))) return PilotStorageObservation.Dispatch(args);
        if (args.Length == 2 && args[0] == "--replay-ownership") return ReplayOwnershipChecks.Run(Path.GetFullPath(args[1]));
        if (args.Length == 2 && args[0] == "--capture-status") return CaptureStatusChecks.Run(Path.GetFullPath(args[1]));
        if (args.Length == 2 && args[0] == "--profile-migration") return ProfileWorkerChecks.Run(Path.GetFullPath(args[1]));
        if (args.Length == 5 && args[0] == "--profile-correction-real" && args[4] == "--budget-apply-only") return ProfileCorrectionChecks.RunBudget(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), Path.GetFullPath(args[3]));
        if (args.Length == 4 && args[0] == "--profile-correction-real") return ProfileCorrectionChecks.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), Path.GetFullPath(args[3]));
        if (args.Length == 2 && args[0] == "--profile-status-transport") return ProfileStatusTransportChecks.Run(Path.GetFullPath(args[1]));
        if (args.Length >= 2 && args[0] == "--apply-acknowledgements") return ApplyAcknowledgementChecks.Run(Path.GetFullPath(args[1]), args.Contains("--sensitivity-control"));
        if (args.Length >= 2 && args[0] == "--engine-capabilities") return EngineCapabilityChecks.Run(Path.GetFullPath(args[1]), args.Contains("--sensitivity-control"));
        int engineRootIndex = Array.IndexOf(args, "--engine-root");
        if (engineRootIndex >= 0) MediaDependency.RootDirectory = Path.GetFullPath(args[engineRootIndex + 1]);
        int ownershipRootIndex = Array.IndexOf(args, "--ownership-root");
        if (ownershipRootIndex >= 0) ProfileOwnership.RootDirectory = Path.GetFullPath(args[ownershipRootIndex + 1]);
        if (args[0] == "--profile-migration-worker") return RecorderWorker.Run(Path.GetFullPath(args[1]), false, setStartup: _ => throw new Exception("Migration must not write startup."));
        if (args[0] == "--profile-correction-worker") return RecorderWorker.Run(Path.GetFullPath(args[1]), true,
            setStartup: enabled =>
            {
                string profile = Path.GetFullPath(args[1]);
                File.AppendAllLines(Path.Combine(profile, "fixture-startup-intent.txt"), new[] { enabled ? "true" : "false" });
                if (File.Exists(Path.Combine(profile, "fixture-reject-startup.txt"))) throw new IOException("Controlled fixture startup failure after profile commit.");
            });
        if (args[0] == "--engine-trust-worker")
        {
            string profile = Path.GetFullPath(args[1]), package = Path.GetFullPath(args[2]);
            return RecorderWorker.Run(profile, true, synthetic: true,
                setStartup: _ => File.WriteAllText(Path.Combine(profile, "unexpected-startup-write.txt"), "startup writer called"),
                repairEngine: () => MediaDependency.Install(profile, true, (_, destination) =>
                {
                    File.AppendAllText(Path.Combine(profile, "repair-acquisitions.txt"), "explicit repair\n");
                    if (File.Exists(Path.Combine(profile, "reject-repair.txt"))) File.WriteAllText(destination, "Rejected package fixture");
                    else File.Copy(package, destination);
                }));
        }
        if (args[0] == "--dependency-marker") { File.WriteAllText(args[1], "Unapproved engine executed"); return 0; }
        if (args[0] == "--dependency-test")
        {
            int packageIndex = Array.IndexOf(args, "--package");
            return DependencyVerification.Run(Path.GetFullPath(args[1]), args.Contains("--download"), packageIndex >= 0 ? Path.GetFullPath(args[packageIndex + 1]) : null);
        }
        if (args[0] == "--security-checks") return SecurityChecks.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
        if (args[0] == "--settings-validation") return SettingsValidationChecks.Run(Path.GetFullPath(args[1]));
        if (args[0] == "--screenshot-binding") return ScreenshotBindingChecks.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
        if (args[0] == "--screenshot-binding-fixture") { ScreenshotBindingFixture.Run(args[1]); return 0; }
        if (args[0] == "--security-live-audio") return SecurityChecks.LiveAudio(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
        if (args[0] == "--security-account-server") return CrossAccountChecks.Server(Path.GetFullPath(args[1]));
        if (args[0] == "--security-account-client") return CrossAccountChecks.Client(Path.GetFullPath(args[1]));
        if (args[0] == "--security-child") return SecurityChecks.Child(args);
        if (args[0] == "--window-fixture") { WindowCaptureChecks.Fixture(args[1]); return 0; }
        if (args[0] == "--window-checks") return WindowCaptureChecks.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
        if (args[0] == "--headless-status") return HeadlessStatusChecks.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
        if (args[0] == "--analyze-saved")
        {
            string folder = Path.GetFullPath(args[1]);
            using var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "verification.json")));
            var root = saved.RootElement;
            int rate = root.GetProperty("fixture").GetProperty("fps").GetInt32();
            var analyses = root.GetProperty("soakClips").EnumerateArray().Select(c => c.GetString()).Select(clip => new
            {
                clip, analysis = FramePacingChecks.Analyze(folder, clip, root.GetProperty("mediaEngine").GetString(), rate, true)
            }).ToArray();
            File.WriteAllText(Path.Combine(folder, "pacing-reanalysis.json"), JsonSerializer.Serialize(analyses, WorkerProtocol.Json));
            return analyses.All(a => JsonSerializer.SerializeToElement(a.analysis).GetProperty("acceptance").GetString() == "passed") ? 0 : 1;
        }
        if (args[0] == "--pacing-fixture") { PacingFixture.Run(args[1], int.Parse(args[2]), args[3]); return 0; }
        if (args[0] == "--pacing") return FramePacingChecks.Run(args[1], int.Parse(args[2]), int.Parse(args[3]), args[4], args[5] == "on", args.Length > 6 ? args[6] : "baseline", args.Length > 7 ? int.Parse(args[7]) : 0, args.Length > 8 ? int.Parse(args[8]) : int.Parse(args[3]), args.Length > 9 ? args[9] : "h264_nvenc");
        if (args[0] == "--fixture") { DesktopFixture.Run(args[1]); return 0; }
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
        live = args.Contains("--live");
        gpuFixture = args.Contains("--gpu-fixture");
        int fpsIndex = Array.IndexOf(args, "--fps"); if (fpsIndex >= 0) liveFps = int.Parse(args[fpsIndex + 1]);
        Require(!gpuFixture || live, "GPU fixture requires live desktop capture.");
        startupBefore = ReadStartup(); report["startupEnabledBefore"] = startupBefore.Value != null;
        Directory.CreateDirectory(data); RecorderHost.Initialize(data);
        var settings = new Settings { ReplayMinutes = 1, FramesPerSecond = 30, BitrateMbps = 5, OutputDirectory = Path.Combine(data, "clips"), ScreenshotDirectory = Path.Combine(data, "screenshots"), SaveKey = 121, SaveModifiers = 7, OverlayKey = 122, OverlayModifiers = 7, ScreenshotKey = 123, ScreenshotModifiers = 7 };
        if (args.Contains("--alternate-shortcuts")) { settings.SaveKey = 117; settings.OverlayKey = 118; settings.ScreenshotKey = 119; }
        if (live) { settings.FramesPerSecond = liveFps; settings.BitrateMbps = 20; }
        settings.Save(Path.Combine(data, "settings.json"));
        report["mediaEngine"] = MediaDependency.ResolveExecutable();
        report["captureSource"] = live ? "Primary monitor native resolution, " + liveFps + "fps/20Mbps H264 NVENC; controlled " + (gpuFixture ? "DXGI flip-model GPU source" : "GDI animated borderless window") + " and real WASAPI default output (silence permitted); one-minute replay buffer" : "Synthetic 640x360/30 testsrc2 and 440Hz/48kHz tone; no desktop recording";
        try
        {
            Check(int.Parse(args[2])).GetAwaiter().GetResult();
            if (gpuFixture)
            {
                var analyses = new List<object>();
                foreach (string clip in (string[])report["soakClips"])
                {
                    object analysis = FramePacingChecks.Analyze(data, clip, (string)report["mediaEngine"], liveFps, true);
                    analyses.Add(new { clip, analysis }); report["pacingReports"] = analyses;
                    var measured = JsonSerializer.SerializeToElement(analysis);
                    Require(measured.GetProperty("acceptance").GetString() == "passed", "Soak clip failed pacing acceptance: " + clip + " " + measured);
                }
            }
            report["result"] = "passed";
        }
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
    static Task<WorkerReply> Call(string command = "status", bool value = false, Settings settings = null, long after = -1) => WorkerProtocol.CallAsync(data, new WorkerRequest { Command = command, Value = command == "apply" ? startupBefore.Value != null : value, Settings = settings, AfterEvent = after });
    static StartupState ReadStartup()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupKey);
        object value = key?.GetValue("F10Recorder", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return new StartupState(value, value == null ? RegistryValueKind.String : key.GetValueKind("F10Recorder"));
    }
    static bool SameStartup(StartupState a, StartupState b) => a.Kind == b.Kind && JsonSerializer.Serialize(a.Value) == JsonSerializer.Serialize(b.Value);
    static void PreserveStartup()
    {
        bool changed = !SameStartup(startupBefore, ReadStartup());
        if (changed)
        {
            using var key = Registry.CurrentUser.CreateSubKey(StartupKey);
            if (startupBefore.Value == null) key.DeleteValue("F10Recorder", false);
            else key.SetValue("F10Recorder", startupBefore.Value, startupBefore.Kind);
        }
        Require(SameStartup(startupBefore, ReadStartup()), "The original sign-in startup entry was not preserved.");
        report["startupEntryPreservedWithoutChange"] = !changed;
        report["startupEntryRestored"] = changed;
    }
    static void Require(bool condition, string error) { if (!condition) throw new Exception(error); }
    static async Task<WorkerStatus> Until(Func<WorkerStatus, bool> predicate, string error, int seconds = 30)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            try
            {
                var reply = await Call(); Require(reply.Error == null, reply.Error);
                if (predicate(reply.Status)) return reply.Status;
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
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
    static async Task<WorkerEvent> Save(bool expected, bool hotkeyDispatch = false, Func<WorkerStatus, Task> afterAccepted = null)
    {
        long sequence = (await Call()).Status.Sequence;
        if (hotkeyDispatch)
            Require(Native.PostMessage(Native.FindWindow(null, WorkerIdentity.ControlTitle(data)), 0x0312, new IntPtr(1), IntPtr.Zero), "Headless worker hotkey dispatch failed.");
        else
        {
            var begin = await Call("save"); Require(begin.Error == null, begin.Error);
            if (afterAccepted != null) await afterAccepted(begin.Status);
        }
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
        Process worker = null, ui = null, fixture = null;
        try
        {
            await CheckProfileOwnership();
            if (live)
            {
                Require(new DriveInfo(Path.GetPathRoot(data)).AvailableFreeSpace > 8L * 1024 * 1024 * 1024, "Live acceptance requires at least 8GiB of free space.");
                fixture = gpuFixture ? Launch(Environment.ProcessPath, "--pacing-fixture", data, liveFps.ToString(), "dxgi") : Launch(Environment.ProcessPath, "--fixture", data);
                string readyFile = Path.Combine(data, gpuFixture ? "pacing-ready.json" : "fixture-ready.json");
                var start = Stopwatch.StartNew();
                while (!File.Exists(readyFile) && !fixture.HasExited && start.Elapsed.TotalSeconds < 10) await Task.Delay(100);
                Require(!fixture.HasExited && File.Exists(readyFile), "Controlled desktop fixture did not become ready.");
                report["fixture"] = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(readyFile));
                worker = Launch(desktop, "--recorder-worker", "--data-dir", data);
            }
            else worker = Launch(Environment.ProcessPath, "--worker", data, desktop);
            var initial = await Until(s => s.Ready && s.Metrics.CaptureProgressSeconds >= 5, "Worker did not begin buffering.");
            Require(initial.WorkerProcessId == worker.Id, "Unexpected worker ownership.");
            Require(initial.SaveRegistered && initial.OverlayRegistered && initial.ScreenshotRegistered, "Worker did not own all shortcuts.");
            ui = Launch(desktop, "--data-dir", data, "--settings"); await UntilUi(ui, worker.Id);
            int capture = initial.Metrics.CaptureProcessId;
            double progress = (await Call()).Status.Metrics.CaptureProgressSeconds;
            ui.Kill(); await ui.WaitForExitAsync(); ui.Dispose(); ui = null;
            await Task.Delay(2500);
            var survived = (await Call()).Status;
            Require(survived.Ready && survived.Metrics.CaptureProcessId == capture && survived.Metrics.CaptureProgressSeconds > progress, "Capture changed during UI-crash check: " + survived.State);
            await Save(true, true); report["uiCrashAndHeadlessSave"] = true;
            ui = Launch(desktop, "--data-dir", data); await UntilUi(ui, worker.Id);
            Require((await Call()).Status.Metrics.CaptureProcessId == capture, "UI reattachment rebuilt the buffer."); report["uiReattachmentPreservesBuffer"] = true;
            await Save(true, afterAccepted: async status =>
            {
                Require(status.Saving, "Save finished before the in-flight lifecycle check.");
                Require((await Call("quit")).Error != null, "Quit interrupted an in-flight save.");
                Require((await Call("apply", settings: Copy(status.Config))).Error != null, "Settings replacement interrupted an in-flight save.");
                Require((await Call()).Status.Saving, "Save finished before the UI crash could be injected.");
                ui.Kill(); await ui.WaitForExitAsync(); ui.Dispose(); ui = null;
            });
            report["uiCrashDuringSaveAndBusyCommandRejection"] = true;
            ui = Launch(desktop, "--data-dir", data); await UntilUi(ui, worker.Id);
            using (var command = Launch(desktop, "--data-dir", data, "--overlay"))
                await command.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(500);
            Require((await Call("screenshot")).Error == null, "Remote screenshot command failed.");
            var screenshot = await Until(s => s.LastScreenshot != null && !s.ScreenshotInProgress, "Cross-process screenshot preparation did not complete.");
            Require(File.Exists(screenshot.LastScreenshot), "Controlled screenshot is missing.");
            using (var uiStatus = JsonDocument.Parse(File.ReadAllText(Path.Combine(data, "status.json"))))
                Require(!uiStatus.RootElement.GetProperty("OverlayVisible").GetBoolean(), "Screenshot preparation left the UI overlay visible.");
            report["remoteScreenshotPreparation"] = screenshot.LastScreenshot;

            var requests = await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => Call()));
            Require(requests.All(r => r.Error == null && r.Status.Ready), "Concurrent status readers affected capture.");
            report["concurrentStatusReaders"] = requests.Length;

            using (var pipe = new NamedPipeClientStream(".", WorkerIdentity.PipeName(data), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
            {
                await pipe.ConnectAsync(2000);
                byte[] oversized = BitConverter.GetBytes(WorkerProtocol.MaxBytes + 1); await pipe.WriteAsync(oversized);
                using var limit = new CancellationTokenSource(3000); byte[] one = new byte[1];
                Require(await pipe.ReadAsync(one, limit.Token) == 0, "Oversized request was not rejected.");
            }
            var wrongVersion = await WorkerProtocol.CallAsync(data, new WorkerRequest { Version = 1 });
            Require(wrongVersion.Error != null && (await Call()).Status.Ready, "Bad protocol request affected capture."); report["boundedProtocolRejection"] = true;

            var original = Copy((await Call()).Status.Config);
            var invalid = Copy(original); invalid.VideoCodec = "invalid";
            Require((await Call("apply", settings: invalid)).Error != null && (await Call()).Status.Config.VideoCodec == original.VideoCodec, "Invalid settings changed live capture.");
            Require(Settings.Load(Path.Combine(data, "settings.json")).VideoCodec == original.VideoCodec, "Invalid settings changed persisted capture."); report["invalidApplyRollback"] = true;

            int priorCapture = (await Call()).Status.Metrics.CaptureProcessId;
            var restarting = await Call("restart");
            Require(restarting.Error == null && !restarting.Status.Ready, "Restart continued to advertise capture readiness.");
            await Until(s => s.Ready && s.Metrics.CaptureProcessId != 0 && s.Metrics.CaptureProcessId != priorCapture, "Explicit restart did not rebuild capture.");
            report["restartInvalidatesReadiness"] = true;

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
            await Until(s => s.Ready && s.Metrics.CaptureProcessId != 0 && s.Metrics.CaptureProcessId != before.Metrics.CaptureProcessId, "Encoder crash did not recover."); report["encoderCrashRecovery"] = true;
            before = (await Call()).Status;
            Native.PostMessage(Native.FindWindow(null, WorkerIdentity.ControlTitle(data)), 0x007E, IntPtr.Zero, IntPtr.Zero);
            await Until(s => s.Ready && s.Metrics.CaptureProcessId != 0 && s.Metrics.CaptureProcessId != before.Metrics.CaptureProcessId, "Display-change message did not recover capture."); report["displayChangeMessageRecovery"] = true;
            before = (await Call()).Status;
            if (!live)
            {
                File.WriteAllText(Path.Combine(data, "controlled-health-failure.txt"), "Controlled audio endpoint change at capture-health boundary.");
                await Until(s => s.Ready && s.Metrics.CaptureProcessId != 0 && s.Metrics.CaptureProcessId != before.Metrics.CaptureProcessId && s.Metrics.Recoveries > before.Metrics.Recoveries, "Controlled audio-health failure did not recover capture.");
                report["audioHealthRecovery"] = "Injected health-boundary failure; physical audio endpoint was unchanged";
            }
            else
            {
                // Production UI must restart the real worker without any test-only launcher.
                using var oldEncoder = Process.GetProcessById(before.Metrics.CaptureProcessId);
                Require(oldEncoder.Handle != IntPtr.Zero, "Could not retain the owned encoder's exit handle.");
                worker.Kill(); await worker.WaitForExitAsync(); worker.Dispose(); worker = null;
                var recovered = await Until(s => s.Ready && s.Instance != before.Instance, "UI did not restart the crashed worker.", 45);
                worker = Process.GetProcessById(recovered.WorkerProcessId); await UntilUi(ui, worker.Id);
                await oldEncoder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Require(recovered.SaveRegistered && recovered.OverlayRegistered && recovered.ScreenshotRegistered, "Restarted worker did not reacquire shortcuts.");
                await Until(s => s.Ready && s.Metrics.CaptureProgressSeconds > 4, "Restarted worker did not refill its buffer."); await Save(true);
                report["workerCrashAutomaticRestartAndEncoderCleanup"] = new { oldWorker = before.WorkerProcessId, newWorker = recovered.WorkerProcessId, oldEncoder = oldEncoder.Id, bufferRebuilt = true };
                foreach (int message in new[] { 0x02B1, 0x0218 })
                {
                    before = (await Call()).Status;
                    Require(Native.PostMessage(Native.FindWindow(null, WorkerIdentity.ControlTitle(data)), (uint)message, new IntPtr(message == 0x02B1 ? 8 : 18), IntPtr.Zero), "Environment message dispatch failed.");
                    await Until(s => s.Ready && s.Metrics.CaptureProcessId != 0 && s.Metrics.CaptureProcessId != before.Metrics.CaptureProcessId, "Unlock/resume message did not rebuild capture.");
                }
                report["unlockResumeMessageRouting"] = "Injected unlock/resume messages; no real lock or suspend";
            }

            int soakClipStart = clips.Count;
            var soak = Stopwatch.StartNew(); int nextSave = 15;
            double previousProgress = -1;
            while (soak.Elapsed.TotalSeconds < soakSeconds)
            {
                if (live) Require(!fixture.HasExited, "Controlled fixture closed during desktop capture.");
                var snapshot = (await Call()).Status;
                Require(snapshot.Ready && snapshot.Metrics.CaptureProcessId != 0, "Soak capture stopped advancing.");
                Require(snapshot.Metrics.CaptureProgressSeconds > previousProgress, "Soak encoder progress stalled.");
                previousProgress = snapshot.Metrics.CaptureProgressSeconds;
                samples.Add(snapshot.Metrics);
                if (soak.Elapsed.TotalSeconds >= nextSave)
                {
                    await Save(true); nextSave += live ? 120 : 30;
                    File.WriteAllText(Path.Combine(data, "acceptance-progress.json"), JsonSerializer.Serialize(new { elapsedSeconds = soak.Elapsed.TotalSeconds, samples = samples.Count, clips = clips.Count, latest = snapshot.Metrics }, WorkerProtocol.Json));
                }
                await Task.Delay(1000);
            }
            await Save(true);
            report["soakClips"] = clips.Skip(soakClipStart).ToArray();
            Require(samples.Any(s => s.EncodedFrames > 0 && s.CacheBytes > 0 && s.CaptureWorkingSetBytes > 0), "Capture measurements were not populated.");
            double bound = original.BitrateMbps * 1000000d / 8 * (original.ReplayMinutes * 60 + 15) * 1.5;
            Require(samples.Max(s => s.CacheBytes) < bound, "Rolling cache exceeded its measured bitrate allowance.");
            report["soakSeconds"] = soak.Elapsed.TotalSeconds; report["cacheBoundBytes"] = bound;
            report["samples"] = samples; report["clips"] = clips; report["saves"] = saves;
            report["metricsMeaning"] = "CPU normalized across logical processors; drops/duplicates are FFmpeg progress counters; optional wrapper GPU counters cover the whole device; gaming FPS and perceptual sync are not measured";
            var hashes = clips.ToDictionary(p => p, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));

            await Call("pause", true); await Until(s => s.Paused && s.Metrics.CaptureProcessId == 0, "Pause did not stop encoder.");
            Require((await Call("quit")).Error == null, "Worker did not acknowledge explicit Quit.");
            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await ui.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); ui.Dispose(); ui = null;
            report["explicitQuitStopsWorkerAndUi"] = true; worker.Dispose();
            worker = live ? Launch(desktop, "--recorder-worker", "--data-dir", data) : Launch(Environment.ProcessPath, "--worker", data, desktop);
            var restored = await Until(s => s.Paused, "Worker restart lost persisted pause.");
            Require(restored.Metrics.CaptureProcessId == 0, "Paused worker restart launched an encoder.");
            Require(hashes.All(h => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(h.Key))) == h.Value), "Restart changed a saved clip.");
            report["pausePersistenceAndSavedClipPreservation"] = true;
            await Call("quit"); await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            try
            {
                if (ui != null) { if (!ui.HasExited) ui.Kill(); ui.Dispose(); }
                if (worker != null)
                {
                    if (!worker.HasExited) { try { await Call("quit"); await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch { worker.Kill(); await worker.WaitForExitAsync(); } }
                    worker.Dispose();
                }
                if (fixture != null) { if (!fixture.HasExited) { fixture.CloseMainWindow(); if (!fixture.WaitForExit(2000)) fixture.Kill(); } fixture.Dispose(); }
            }
            finally { PreserveStartup(); }
        }
    }

    static async Task CheckProfileOwnership()
    {
        string otherProfile = Path.Combine(data, "ownership-check");
        string sentinel = Path.Combine(otherProfile, "buffer", "session-sentinel", "chunk-sentinel.ts");
        Directory.CreateDirectory(Path.GetDirectoryName(sentinel)); File.WriteAllText(sentinel, "Preserve another session's active buffer");
        // Hold the session-independent profile lock without the session worker lock.
        // This exercises the same rejection path as a worker in another session.
        using (var owner = ProfileOwnership.Acquire(otherProfile))
        {
            using var blocked = Launch(desktop, "--recorder-worker", "--data-dir", otherProfile, "--paused");
            // Keep mutex ownership and disposal on this STA thread; do not await here.
            Require(blocked.WaitForExit(5000) && blocked.ExitCode == 1, "A second session was not refused before opening the profile.");
            Require(File.ReadAllText(sentinel) == "Preserve another session's active buffer", "Blocked worker changed the active session's buffer.");
        }
        using var replacement = Launch(desktop, "--recorder-worker", "--data-dir", otherProfile, "--paused");
        try
        {
            var ready = await WorkerProtocol.CallAsync(otherProfile, new WorkerRequest());
            Require(ready.Error == null && ready.Status.Paused, "Profile could not reopen after ownership was released.");
            Require((await WorkerProtocol.CallAsync(otherProfile, new WorkerRequest { Command = "quit" })).Error == null, "Ownership-check worker did not stop.");
            await replacement.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { if (!replacement.HasExited) { replacement.Kill(); await replacement.WaitForExitAsync(); } }
        report["profileOwnershipAndBufferPreservation"] = "Session-independent profile lock refused another owner before cache cleanup; profile reopened after release. Actual parallel Windows logins were not created.";
    }
}
