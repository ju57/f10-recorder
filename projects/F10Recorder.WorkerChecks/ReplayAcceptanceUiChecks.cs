using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text.Json;

namespace F10Recorder;

// Checks real frozen UI/worker attachment with acquisition strictly disarmed.
internal static class ReplayAcceptanceUiChecks
{
    static void Need(bool value, string reason) { if (!value) throw new IOException(reason); }
    static void Write(string root, string name, object value)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions(WorkerProtocol.Json) { WriteIndented = true });
        Need(bytes.Length <= (name == "runtime-closure.json" ? 262144 : 65536), "UI control report bound.");
        using var output = new FileStream(Path.Combine(root, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        output.Write(bytes); output.Flush(true);
    }
    internal static int Prepare(string planFile, string root, string uiBuild, string product, string productionData)
    {
        foreach (string path in new[] { planFile, root, uiBuild, product, productionData }) Need(Path.IsPathFullyQualified(path), "Absolute paths required.");
        Need(!File.Exists(planFile) && !Directory.Exists(root) && !File.Exists(root)
            && root.StartsWith(Path.GetFullPath("artifacts") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Fresh private plan/root required.");
        foreach (string file in Directory.GetFiles(uiBuild, "*.dll", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(uiBuild, file);
            if (relative == "F10Recorder.MigrationTests.dll") continue;
            string original = Path.Combine(product, relative);
            if (!File.Exists(original) && !relative.Contains(Path.DirectorySeparatorChar)
                && relative is "av_libglesv2.dll" or "libHarfBuzzSharp.dll" or "libSkiaSharp.dll")
                original = Path.Combine(product, "runtimes", "win-x64", "native", relative);
            Need(File.Exists(original) && ReplayWorkerLatencyPilot.Hash(file) == ReplayWorkerLatencyPilot.Hash(original),
                "Every UI managed/native product dependency must execute unchanged frozen bytes: " + relative);
        }
        string admission = Path.GetDirectoryName(planFile); Directory.CreateDirectory(admission);
        string manifest = Path.Combine(admission, "runtime-closure.json");
        Write(admission, "runtime-closure.json", ReplayWorkerLatencyPilot.RuntimeFiles().ToDictionary(f => f, f => ReplayWorkerLatencyPilot.Hash(f)));
        string[] shared = [Path.Combine(productionData, "settings.json"), Path.Combine(productionData, "worker-state.json")];
        string build = Path.GetDirectoryName(typeof(ReplayAcceptanceUiChecks).Assembly.Location);
        var files = Directory.GetFiles(build).Where(f => Path.GetExtension(f) is ".dll" or ".json")
            .Concat(Directory.GetFiles(uiBuild, "*", SearchOption.AllDirectories))
            .Concat(new[] { Environment.ProcessPath, manifest, Path.Combine(Environment.SystemDirectory, "conhost.exe"),
                Path.GetFullPath("projects/F10Recorder.AcceptanceUi/Program.cs"), Path.GetFullPath("projects/F10Recorder.AcceptanceUi/F10Recorder.AcceptanceUi.csproj"),
                Path.GetFullPath("projects/F10Recorder.WorkerChecks/ReplayAcceptanceUiChecks.cs"), Path.GetFullPath("tests/replay-acceptance-ui.ps1") }).Concat(shared)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(f => f, f => ReplayWorkerLatencyPilot.Hash(f, shared.Contains(f)), StringComparer.OrdinalIgnoreCase);
        Need(files.Count <= 128, "UI runtime closure count.");
        Write(admission, Path.GetFileName(planFile), new { kind = "issue83-paused-acceptance-ui-v1", root, uiBuild, product, productionData, files, sharedFiles = shared, runtimeManifest = manifest,
            maximumChildren = 2, deadlineSeconds = 70, captureSeconds = 0, fixtureLifetimeSeconds = 0,
            consoleSupport = new { path = Path.Combine(Environment.SystemDirectory, "conhost.exe"), maximumDirectChildren = 1, maximumWorkingSetBytes = 67_108_864 },
            budgetBytes = 67_108_864, stopBytes = 50_331_648, minimumFreeBytes = 60_000_000_000L,
            maximumFiles = 256, maximumDirectories = 64, createdUtc = DateTime.UtcNow, startup = ReplayWorkerLatencyPilot.Startup(),
            effects = "One task-owned real frozen Avalonia UI process/tray attached through actual current-user protocol to one private PAUSED worker thread. All3shortcut registrations required before UI; --paused; both processes use private unavailable media root (no installed-engine fallback); capture observer rejects acquisition. UI self-assigns single-process job BEFORE App; checks admitted child counts. Exact sticky SettingsPanel.loaded Boolean must remainfalse atready/every100ms/closure, hiddenpanels; parent waits2seconds to exercise deferred attachment. Zero desktop/system/microphone acquisition, encoder/decoder, playback or save. UI normal owned Quit afterSTOP.UI; private authority/profile/output. No installed/startup/vendor/catalog/provisioning change.",
            decision = "Require real WorkerRecorder, expected worker PID/birth/instance, paused+no capture, hidden settings/overlay, exact normal UI0 and worker0 joins and unchanged bound product/startup. Parent/helper+UI+atmost1console+preparation+outer=5processes. No live/performance/resource/physical-feedback qualification." });
        Console.WriteLine(planFile + " " + ReplayWorkerLatencyPilot.Hash(planFile)); return 0;
    }
    internal static int Run(string planFile, string expected)
    {
        Need(new FileInfo(planFile).Length <= 65536 && ReplayWorkerLatencyPilot.Hash(planFile) == expected, "Exact paused-UI plan required.");
        using var document = JsonDocument.Parse(File.ReadAllBytes(planFile)); var p = document.RootElement;
        Need(p.GetProperty("kind").GetString() == "issue83-paused-acceptance-ui-v1" && p.GetProperty("deadlineSeconds").GetInt32() == 70
            && p.GetProperty("captureSeconds").GetInt32() == 0 && p.GetProperty("maximumChildren").GetInt32() == 2
            && DateTime.UtcNow.AddSeconds(70) < p.GetProperty("createdUtc").GetDateTime().AddHours(4), "Current finite paused UI admission required.");
        string[] shared = p.GetProperty("sharedFiles").EnumerateArray().Select(v => v.GetString()).ToArray();
        void Bindings()
        {
            foreach (var f in p.GetProperty("files").EnumerateObject()) Need(ReplayWorkerLatencyPilot.Hash(f.Name, shared.Contains(f.Name)) == f.Value.GetString(), "Paused UI binding changed: " + f.Name);
            string manifest = p.GetProperty("runtimeManifest").GetString(); Need(new FileInfo(manifest).Length <= 262144, "Runtime manifest bound.");
            var files = ReplayWorkerLatencyPilot.ParseRuntime(File.ReadAllBytes(manifest), p.GetProperty("files").GetProperty(manifest).GetString(), ReplayWorkerLatencyPilot.RuntimeFiles());
            foreach (var f in files) Need(ReplayWorkerLatencyPilot.Hash(f.Key) == f.Value, "Framework changed.");
            Need(ReplayWorkerLatencyPilot.Startup() == p.GetProperty("startup").GetString(), "Startup changed.");
        }
        Bindings(); string root = p.GetProperty("root").GetString();
        Need(Path.IsPathFullyQualified(root) && root.StartsWith(Path.GetFullPath("artifacts") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !Directory.Exists(root) && !File.Exists(root), "Fresh artifact root required.");
        string data = Path.Combine(root, "profile"), evidence = Path.Combine(root, "ui"), authority = Path.Combine(root, "authority");
        Directory.CreateDirectory(data); Directory.CreateDirectory(evidence); Write(root, "admitted-plan.json", p);
        string priorAuthority = ProfileOwnership.RootDirectory, priorLog = Log.DirectoryPath, priorMedia = MediaDependency.RootDirectory; int captures = 0, workerExit = -1; bool uiForced = false;
        Thread worker = null; Process ui = null; Exception failure = null; var cleanup = new ConcurrentQueue<string>(); var clock = Stopwatch.StartNew();
        long maxBytes = 0; int maxFiles = 0, inventoryRetries = 0;
        void Guard()
        {
            Dictionary<string, ReplayFileExtent> inventory = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                Need(clock.Elapsed.TotalSeconds < 50, "Paused UI work deadline;20s cleanup reserved.");
                Need(Volatile.Read(ref captures) == 0, "Capture was requested in a no-acquisition control.");
                try { inventory = ReplayStorage.Inventory([root], new ReplayFiles(), 256, 64); break; }
                catch (System.ComponentModel.Win32Exception error) when (attempt < 2 && error.NativeErrorCode is 2 or 3)
                {
                    // A private atomic rename may race the observer. Discard
                    // the partial scan; only a complete fresh inventory counts.
                    inventoryRetries++;
                }
            }
            Need(clock.Elapsed.TotalSeconds < 50, "Paused UI work deadline;20s cleanup reserved.");
            Need(Volatile.Read(ref captures) == 0, "Capture was requested in a no-acquisition control.");
            long bytes = ReplayStorage.Bytes(inventory);
            maxBytes = Math.Max(maxBytes, bytes); maxFiles = Math.Max(maxFiles, inventory.Count);
            Need(bytes < 50_331_648 && ReplayStorage.ObserveCapacity(root).AvailableBytes >= 60_000_000_000L, "Paused UI storage/floor.");
        }
        WorkerReply Call(string command = "status")
        {
            using var cutoff = new CancellationTokenSource(2000);
            return WorkerProtocol.CallAsync(data, new WorkerRequest { Command = command }, cutoff.Token).GetAwaiter().GetResult();
        }
        try
        {
            ProfileOwnership.RootDirectory = authority; Log.DirectoryPath = data;
            MediaDependency.RootDirectory = Path.Combine(data, "unavailable-media");
            Need(!File.Exists(MediaDependency.ExecutablePath), "Private paused-worker media root must be unavailable.");
            var settings = new Settings { ReplayMinutes = 5, FramesPerSecond = 60, BitrateMbps = 20, SystemAudio = true, VideoCodec = "h264_nvenc",
                OutputDirectory = Path.Combine(root, "output"), ReplayCacheDirectory = Path.Combine(root, "cache"), ScreenshotDirectory = Path.Combine(root, "screenshots"),
                SaveModifiers = 7, OverlayModifiers = 7, ScreenshotModifiers = 7, SaveKey = 121, OverlayKey = 120, ScreenshotKey = 119 };
            RecordingProfile.Load(data).Commit(settings, true, true, false);
            RecorderRuntime.CaptureArgumentObserver = _ => { Interlocked.Increment(ref captures); throw new IOException("Paused UI capture launch forbidden."); };
            worker = new Thread(() => { try { workerExit = RecorderWorker.Run(data, true, setStartup: _ => throw new IOException("Startup forbidden."),
                screenshotSource: (_, _) => throw new IOException("Screenshot forbidden."), repairEngine: () => throw new IOException("Provisioning forbidden.")); }
                catch (Exception error) { cleanup.Enqueue("worker: " + error.Message); } });
            worker.SetApartmentState(ApartmentState.STA); worker.Start();
            WorkerReply initial = null;
            while (initial == null)
            {
                Guard(); try { initial = Call(); } catch (Exception error) when (error is TimeoutException or OperationCanceledException) { Need(clock.Elapsed.TotalSeconds < 10, "Worker bootstrap deadline."); }
            }
            Need(initial.Error == null && initial.Status.Paused && initial.Status.Metrics.CaptureProcessId == 0 && worker.IsAlive
                && initial.Status.SaveRegistered && initial.Status.OverlayRegistered && initial.Status.ScreenshotRegistered, "Real paused worker with all3 shortcuts registered required BEFORE UI launch.");
            using var parent = Process.GetCurrentProcess();
            var start = new ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = false, CreateNoWindow = true };
            foreach (string arg in new[] { Path.Combine(p.GetProperty("uiBuild").GetString(), "F10Recorder.MigrationTests.dll"), data, authority, evidence,
                parent.Id.ToString(), parent.StartTime.ToUniversalTime().Ticks.ToString(), initial.Status.Instance }) start.ArgumentList.Add(arg);
            ui = Process.Start(start); _ = ui.SafeHandle;
            Write(evidence, "ui-launched.json", new { uiPid = ui.Id, uiBirth = ui.StartTime.ToUniversalTime(), workerPid = parent.Id,
                workerBirth = parent.StartTime.ToUniversalTime(), instance = initial.Status.Instance, captureLaunches = captures });
            while (!File.Exists(Path.Combine(evidence, "ui-ready.json"))) { Guard(); Need(!ui.HasExited && clock.Elapsed.TotalSeconds < 20, "UI startup failed/deadline."); Thread.Sleep(50); }
            using var ready = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(evidence, "ui-ready.json")));
            Need(ready.RootElement.GetProperty("pid").GetInt32() == ui.Id && ready.RootElement.GetProperty("birthUtc").GetDateTime() == ui.StartTime.ToUniversalTime()
                && ready.RootElement.GetProperty("workerPid").GetInt32() == parent.Id && ready.RootElement.GetProperty("workerBirthTicks").GetInt64() == parent.StartTime.ToUniversalTime().Ticks
                && ready.RootElement.GetProperty("instance").GetString() == initial.Status.Instance && ready.RootElement.GetProperty("actualWorkerRecorder").GetBoolean()
                && !ready.RootElement.GetProperty("settingsVisible").GetBoolean() && !ready.RootElement.GetProperty("overlayVisible").GetBoolean(), "Actual hidden UI attachment required.");
            var status = Call().Status; Need(status.Instance == initial.Status.Instance && status.Paused && status.Metrics.CaptureProcessId == 0 && captures == 0, "UI attachment changed capture state.");
            var settle = Stopwatch.StartNew();
            while (settle.Elapsed.TotalSeconds < 2) { Guard(); Need(!ui.HasExited, "UI did not survive deferred attachment/event loop work."); Thread.Sleep(50); }
            Write(evidence, "STOP.UI", new { ownedStop = true });
            while (!ui.WaitForExit(50)) { Guard(); Need(clock.Elapsed.TotalSeconds < 35, "Normal UI shutdown deadline."); }
            Need(ui.ExitCode == 0 && worker.Join(5000) && workerExit == 0 && captures == 0, "Normal UI/worker joins required.");
            Bindings(); Guard();
            Write(root, "result.json", new { result = "frozen-real-ui-paused-worker-attached-and-closed", actualWorkerRecorder = true, captures, uiPid = ui.Id,
                uiBirth = ui.StartTime.ToUniversalTime(), uiExit = ui.ExitCode, workerExit, maxBytes, maxFiles, inventoryRetries,
                claim = "No acquisition. Real unchanged Avalonia App/WorkerRecorder, private authority, actual current-user protocol and normal owned quit; no UI resource, physical feedback or live acceptance claim." });
        }
        catch (Exception error) { failure = error; Console.Error.WriteLine(error); }
        finally
        {
            try { if (!File.Exists(Path.Combine(evidence, "STOP.UI"))) Write(evidence, "STOP.UI", new { cleanupStop = true }); } catch (Exception error) { cleanup.Enqueue(error.Message); }
            if (worker?.IsAlive == true) try { Call("quit"); if (!worker.Join(5000)) cleanup.Enqueue("Worker remained live."); } catch (Exception error) { cleanup.Enqueue(error.Message); }
            if (ui != null && !ui.HasExited) try { if (!ui.WaitForExit(2000)) { uiForced = true; ui.Kill(); if (!ui.WaitForExit(2000)) cleanup.Enqueue("Owned UI absence unconfirmed."); } } catch (Exception error) { cleanup.Enqueue(error.Message); }
            if (cleanup.Count != 0 || uiForced) failure ??= new IOException("Paused UI cleanup incomplete/forced.");
            try { Bindings(); Write(root, "closure.json", new { error = failure?.ToString(), cleanup, captures, workerJoined = worker?.IsAlive != true, workerExit,
                uiPid = ui?.Id, uiBirth = ui?.StartTime.ToUniversalTime(), uiAbsent = ui?.HasExited ?? true, uiForced,
                uiExit = ui?.HasExited == true ? (int?)ui.ExitCode : null, maxBytes, maxFiles, inventoryRetries }); }
            catch (Exception error) { failure ??= error; Console.Error.WriteLine(error); }
            ui?.Dispose(); if (worker?.IsAlive != true) { RecorderRuntime.CaptureArgumentObserver = null; ProfileOwnership.RootDirectory = priorAuthority; Log.DirectoryPath = priorLog; MediaDependency.RootDirectory = priorMedia; }
        }
        return failure == null ? 0 : 1;
    }
}
