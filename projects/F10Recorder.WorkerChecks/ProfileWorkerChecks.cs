using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace F10Recorder;

internal static class ProfileWorkerChecks
{
    internal static int Run(string data)
    {
        Directory.CreateDirectory(data);
        var report = new Dictionary<string, object>(); var snapshots = new List<WorkerStatus>(); var cases = new List<object>();
        bool startupBefore = WindowsStartup.Enabled;
        void Scenario(string scenario)
        {
        Process worker = null;
        string profile = Path.Combine(data, scenario); Directory.CreateDirectory(profile);
        string settings = Path.Combine(profile, "settings.json"), state = Path.Combine(profile, "worker-state.json");
        var config = new Settings { SaveKey = 115, SaveModifiers = 7, OverlayKey = 116, OverlayModifiers = 7, ScreenshotKey = 117, ScreenshotModifiers = 7, SystemAudio = false, DisplayIndex = 0, VideoCodec = "hevc_nvenc", CaptureMode = "window", WindowExecutable = Path.Combine(profile, "unavailable.exe"), WindowClass = "UnavailableSource", OutputDirectory = Path.Combine(profile, "clips"), ScreenshotDirectory = Path.Combine(profile, "pictures") };
        var legacy = JsonSerializer.SerializeToNode(config, WorkerProtocol.Json).AsObject();
        if (scenario is not ("current-paused" or "current-stopped")) legacy.Remove("SchemaVersion");
        if (scenario == "part-invalid") legacy["FramesPerSecond"] = 999;
        if (scenario == "future-settings") legacy["SchemaVersion"] = 999;
        File.WriteAllText(settings, legacy.ToJsonString());
        File.WriteAllText(state, scenario == "current-paused" ? "{\"SchemaVersion\":1,\"Paused\":true,\"UserStopped\":false,\"ActivationRequired\":false}" : scenario == "current-stopped" ? "{\"SchemaVersion\":1,\"Paused\":false,\"UserStopped\":true,\"ActivationRequired\":false}" : scenario == "future-state" ? "{\"SchemaVersion\":999,\"Paused\":false}" : scenario == "malformed-state" ? "null" : "{\"Paused\":false,\"UserStopped\":true}");
        if (scenario == "interrupted") File.WriteAllText(Path.Combine(profile, "profile-migration.json"), "{\"phase\":\"settings-written\"}");
        byte[] originalSettings = File.ReadAllBytes(settings), originalState = File.ReadAllBytes(state);
        string retained = Path.Combine(profile, "buffer", "session-preserved", "existing.ts"); Directory.CreateDirectory(Path.GetDirectoryName(retained)); File.WriteAllText(retained, "Existing retained input; do not clear on migration.");
        string saved = Path.Combine(profile, "preserved.mp4"); File.WriteAllText(saved, "Saved-file preservation sentinel; not playable media."); string savedHash = MediaDependency.Hash(saved);
        Task<WorkerReply> Call(string command = "status", bool value = false) => WorkerProtocol.CallAsync(profile, new WorkerRequest { Command = command, Value = value, AfterEvent = 0 });
        Process Launch()
        {
            var info = new ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = false, CreateNoWindow = true };
            foreach (string argument in new[] { "--profile-migration-worker", profile, "--ownership-root", Path.Combine(data, "ownership"), "--engine-root", Path.Combine(data, "missing-engine") }) info.ArgumentList.Add(argument);
            return Process.Start(info) ?? throw new IOException("Migration worker did not start.");
        }
        try
        {
            worker = Launch();
            var reply = Call().GetAwaiter().GetResult();
            Require(reply.Error == null && reply.Status.Paused && !reply.Status.Ready && reply.Status.Metrics.CaptureProcessId == 0, "Migration worker must remain inactive.");
            Require(File.ReadAllBytes(settings).SequenceEqual(originalSettings) && File.ReadAllBytes(state).SequenceEqual(originalState), "Opening a migration worker must not rewrite settings or pause/stopped intent.");
            Require(File.Exists(retained), "Opening a migration worker must preserve existing retained cache inputs.");
            Require(reply.Status.ProfileStatus.ExistingProfile && reply.Status.Config.VideoCodec == "hevc_nvenc" && !reply.Status.Config.SystemAudio && reply.Status.Config.DisplayIndex == 0 && reply.Status.Config.WindowExecutable == config.WindowExecutable, scenario + ": worker lost valid source/codec/zero/audio intent.");
            if (scenario == "legacy")
            {
                var window = Native.FindWindow(null, WorkerIdentity.ControlTitle(profile)); Native.GetWindowThreadProcessId(window, out uint ownerPid);
                Require(window != IntPtr.Zero && ownerPid == worker.Id, "Task-owned shortcut window identity could not be established.");
                Require(Native.PostMessage(window, 0x0312, new IntPtr(1), IntPtr.Zero) && Native.PostMessage(window, 0x0312, new IntPtr(3), IntPtr.Zero), "Could not dispatch task-owned hidden shortcut messages.");
                var timeout = Stopwatch.StartNew(); WorkerReply unavailable;
                do { unavailable = Call().GetAwaiter().GetResult(); if (unavailable.Status.Events.Count(e => e.Title == "Recording profile needs attention") >= 2) break; Thread.Sleep(20); } while (timeout.ElapsedMilliseconds < 3000);
                Require(!worker.HasExited && unavailable.Status.Events.Count(e => e.Title == "Recording profile needs attention") >= 2 && unavailable.Status.Paused && unavailable.Status.Metrics.CaptureProcessId == 0, "Held native save/screenshot dispatch must remain alive with observable unavailable results.");
                Require(Call("save").GetAwaiter().GetResult().Error != null, "Held pipe Save must report unavailable.");
                Call("screenshot").GetAwaiter().GetResult(); Require(!worker.HasExited, "Held async screenshot dispatch killed the worker.");
            }
            var reconnect = WorkerProtocol.EnsureWorker(Path.Combine(profile, "..", scenario), false, "must-not-launch-a-second-worker.exe", false);
            Require(reconnect.Status.Instance == reply.Status.Instance && reconnect.Status.WorkerProcessId == worker.Id && reconnect.Status.Paused, scenario + ": reconnect duplicated activation.");
            using (var competitor = Launch())
            {
                if (!competitor.WaitForExit(5000)) { competitor.Kill(true); competitor.WaitForExit(); throw new Exception("Competing worker did not reject ownership."); }
                Require(competitor.ExitCode != 0, "Competing worker acquired owned profile.");
            }
            Require(File.ReadAllBytes(settings).SequenceEqual(originalSettings) && File.ReadAllBytes(state).SequenceEqual(originalState) && File.Exists(retained) && MediaDependency.Hash(saved) == savedHash, "Ownership/reconnect changed original inputs/media.");
            if (scenario == "current-stopped")
            {
                var paused = Call("pause", true).GetAwaiter().GetResult();
                var intent = RecordingProfile.Load(profile);
                Require(paused.Error == null && intent.State.Paused && intent.State.UserStopped, "Explicit Pause on an already stopped worker must retain both deliberate pause and stop intent.");
            }
            if (scenario == "current-paused")
            {
                Call("screenshot").GetAwaiter().GetResult();
                var timeout = Stopwatch.StartNew(); WorkerReply screenshot;
                do { screenshot = Call().GetAwaiter().GetResult(); if (screenshot.Status.Events.Any(e => e.Title == "Screenshot not saved")) break; Thread.Sleep(20); } while (timeout.ElapsedMilliseconds < 3000);
                Require(screenshot.Status.Events.Any(e => e.Title == "Screenshot not saved" && e.Message.Contains("Open the selected application")) && screenshot.Status.Paused && screenshot.Status.Metrics.CaptureProcessId == 0, "Paused window screenshots must resolve the preserved source identity without starting replay.");
            }
            if (scenario is "part-invalid" or "future-settings" or "future-state" or "malformed-state" or "interrupted")
            {
                var resume = Call("pause", false).GetAwaiter().GetResult();
                Require(resume.Error != null && resume.Status.Paused && resume.Status.Metrics.CaptureProcessId == 0, "Uncertain/invalid profile resumed capture.");
                Require(File.ReadAllBytes(settings).SequenceEqual(originalSettings) && File.ReadAllBytes(state).SequenceEqual(originalState), "Rejected Resume changed originals.");
            }
            snapshots.Add(reply.Status);
            cases.Add(new { scenario, result = "passed", workerPid = worker.Id, instance = reply.Status.Instance, outcome = reply.Status.ProfileStatus.Outcome,
                originalSettingsHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(originalSettings)), originalStateHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(originalState)),
                settingsHash = MediaDependency.Hash(settings), stateHash = MediaDependency.Hash(state), savedHash });
        }
        finally
        {
            if (worker != null)
            {
                try { using var deadline = new CancellationTokenSource(3000); WorkerProtocol.CallAsync(profile, new WorkerRequest { Command = "quit" }, deadline.Token).GetAwaiter().GetResult(); } catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
                if (!worker.WaitForExit(10000)) { worker.Kill(true); worker.WaitForExit(); }
                report["ownedWorkerExited"] = worker.HasExited; worker.Dispose();
                if (scenario == "current-stopped") { var stopped = RecordingProfile.Load(profile); Require(stopped.State.Paused && stopped.State.UserStopped, "Quit lost the explicit pause/stopped intent."); }
            }
        }
        }
        try
        {
            foreach (string scenario in new[] { "legacy", "current-paused", "current-stopped", "part-invalid", "future-settings", "future-state", "malformed-state", "interrupted" }) Scenario(scenario);
            Require(WindowsStartup.Enabled == startupBefore, "Migration changed startup."); report["result"] = "passed";
        }
        catch (Exception ex) { report["result"] = "failed"; report["error"] = ex.ToString(); }
        report["cases"] = cases; report["startupPreserved"] = WindowsStartup.Enabled == startupBefore;
        File.WriteAllText(Path.Combine(data, "worker-snapshots.json"), JsonSerializer.Serialize(snapshots, WorkerProtocol.Json));
        File.WriteAllText(Path.Combine(data, "verification.json"), JsonSerializer.Serialize(report, WorkerProtocol.Json));
        return (string)report["result"] == "passed" ? 0 : 1;
    }
    static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
