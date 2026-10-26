using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace F10Recorder;

// Real worker/persistence acceptance while capture stays explicitly paused.
internal static class ProfileCorrectionChecks
{
    internal static int Run(string data, string engineSource, string engineRoot)
    {
        Directory.CreateDirectory(data); var cases = new List<object>(); var exits = new List<object>();
        string startupBefore = StartupValue();
        File.WriteAllText(Path.Combine(data, "startup-before.json"), startupBefore);
        var report = new Dictionary<string, object>();
        try
        {
            StageEngine(engineSource, engineRoot, data);
            ProfileOwnership.RootDirectory = Path.Combine(data, "ownership");
            foreach (string name in new[] { "held-stopped", "held-paused", "read-lease-failure", "future-settings", "interrupted", "backup-collision", "startup-callback-failure" }) Scenario(name);
            Require(StartupValue() == startupBefore, "The actual Windows startup value or kind changed.");
            report["result"] = "passed";
        }
        catch (Exception ex) { report["result"] = "failed"; report["error"] = ex.ToString(); }
        report["cases"] = cases; report["workerExits"] = exits;
        report["startupPreserved"] = StartupValue() == startupBefore;
        report["startupBeforeSha256"] = HashText(startupBefore);
        report["effects"] = "Task-only paused workers, hidden Ctrl+Alt+Shift+F4-F6 shortcuts and current-user pipes; engine hash verification only. No engine execution, capture/GPU, visible or excluded UI, real startup writes, downloads, repair or installed-recorder mutation.";
        File.WriteAllText(Path.Combine(data, "verification.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Real worker correction: {report["result"]}; {cases.Count} scenarios. {data}");
        return (string)report["result"] == "passed" ? 0 : 1;

        void Scenario(string name)
        {
            report["activeScenario"] = name;
            string profile = Path.Combine(data, name); Directory.CreateDirectory(profile);
            string settingsPath = Path.Combine(profile, "settings.json"), statePath = Path.Combine(profile, "worker-state.json");
            var config = new Settings {
                ReplayMinutes = 7, BitrateMbps = 37, VideoCodec = "hevc_nvenc", SystemAudio = false, DisplayIndex = 0,
                SaveKey = 115, SaveModifiers = 7, OverlayKey = 116, OverlayModifiers = 7, ScreenshotKey = 117, ScreenshotModifiers = 7,
                CaptureMode = "window", WindowExecutable = Path.Combine(profile, "unavailable-fixture.exe"), WindowClass = "F10UnavailableCorrectionFixture",
                OutputDirectory = Path.Combine(profile, "saved-clips"), ScreenshotDirectory = Path.Combine(profile, "saved-screenshots")
            };
            var document = JsonSerializer.SerializeToNode(config, WorkerProtocol.Json).AsObject();
            if (name != "startup-callback-failure") { document.Remove("SchemaVersion"); document["FramesPerSecond"] = 999; }
            document["SelectedPlaybackDeviceId"] = "missing-fixture-playback";
            document["MicrophoneMuted"] = false; document["MicrophoneLevel"] = 0;
            if (name == "future-settings") document["SchemaVersion"] = 999;
            File.WriteAllText(settingsPath, document.ToJsonString());
            bool pausedIntent = name is "held-paused" or "startup-callback-failure";
            File.WriteAllText(statePath, pausedIntent
                ? "{\"SchemaVersion\":1,\"Paused\":true,\"UserStopped\":false,\"ActivationRequired\":true,\"OpaqueState\":{\"Checkpoint\":0,\"AutoResume\":false}}"
                : "{\"Paused\":false,\"UserStopped\":true,\"ActivationRequired\":true,\"OpaqueState\":{\"Checkpoint\":0,\"AutoResume\":false}}");
            if (name == "interrupted") File.WriteAllText(Path.Combine(profile, "profile-migration.json"), "{\"phase\":\"fixture-interrupted\"}");
            byte[] originalSettings = File.ReadAllBytes(settingsPath), originalState = File.ReadAllBytes(statePath);
            File.WriteAllBytes(Path.Combine(profile, "settings.input.json"), originalSettings); File.WriteAllBytes(Path.Combine(profile, "state.input.json"), originalState);
            if (name == "backup-collision") File.WriteAllText(statePath + ".original-" + Hash(originalState), "Deliberately conflicting fixture backup; must not be replaced.");
            if (name == "startup-callback-failure") File.WriteAllText(Path.Combine(profile, "fixture-reject-startup.txt"), "Reject only the task's supplied startup callback, not Windows startup.");
            string retained = Path.Combine(profile, "buffer", "session-preserved", "existing.ts"); Directory.CreateDirectory(Path.GetDirectoryName(retained));
            File.WriteAllText(retained, "Retained cache sentinel, not playable footage.");
            string saved = Path.Combine(profile, "saved.mp4"); File.WriteAllText(saved, "Saved-media preservation sentinel, not playable footage.");
            string retainedHash = MediaDependency.Hash(retained), savedHash = MediaDependency.Hash(saved);
            Process worker = null;
            try
            {
                worker = Launch(); var initial = Call(new WorkerRequest()).GetAwaiter().GetResult();
                Require(initial.Error == null, "Initial status failed: " + initial.Error); AssertHeld(initial.Status);
                Require(initial.Status.WorkerProcessId == worker.Id, "The status was not from the task-owned worker.");
                Require(File.ReadAllBytes(settingsPath).SequenceEqual(originalSettings) && File.ReadAllBytes(statePath).SequenceEqual(originalState), "Launch rewrote original settings or state.");
                Require(initial.Status.Config.FramesPerSecond == 60 && initial.Status.Config.VideoCodec == "hevc_nvenc" && !initial.Status.Config.SystemAudio && initial.Status.Config.DisplayIndex == 0,
                    "The initial effective correction or preserved explicit siblings changed.");
                Require(initial.Status.Config.AdditionalPreferences.Count == 0 && initial.Status.RetainedPreferenceCount == 3, "Opaque preferences must remain worker-owned.");
                WorkerRequest sent = null; WorkerReply completion = null;
                using (var client = new WorkerRecorder(initial.Status, async request =>
                {
                    Require(request.Command == "apply", "Only Apply is permitted from this client fixture.");
                    sent = request; completion = await Call(request); return completion;
                }))
                {
                    var next = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(initial.Status.Config, WorkerProtocol.Json), WorkerProtocol.Json); next.FramesPerSecond = 30;
                    FileStream readLease = name == "read-lease-failure" ? new FileStream(settingsPath, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
                    Exception rejection = null;
                    try { client.ApplyAsync(next, false).GetAwaiter().GetResult(); }
                    catch (Exception ex) { rejection = ex; }
                    finally { readLease?.Dispose(); }
                    File.WriteAllText(Path.Combine(profile, "apply-reply.json"), JsonSerializer.Serialize(completion, WorkerProtocol.Json));
                    Require(completion?.Status != null && sent != null && completion.Status.Instance == initial.Status.Instance && completion.Status.CompletedApply == sent.OperationId && !completion.Status.Applying,
                        "The actual worker did not return the correlated completion marker.");
                    AssertHeld(completion.Status);
                    bool positive = name is "held-stopped" or "held-paused";
                    if (positive)
                    {
                        Require(rejection == null && completion.Error == null && client.Config.FramesPerSecond == 30 && client.CaptureModeKnown,
                            "A real held correction failed: " + rejection?.Message);
                        AssertCorrected(profile, config, pausedIntent);
                        Require(File.ReadAllBytes(settingsPath + ".original-" + Hash(originalSettings)).SequenceEqual(originalSettings)
                            && File.ReadAllBytes(statePath + ".original-" + Hash(originalState)).SequenceEqual(originalState), "Original backups did not retain exact bytes.");
                        Require(!Path.Exists(Path.Combine(profile, "profile-migration.json")) && !Path.Exists(settingsPath + ".tmp") && !Path.Exists(statePath + ".tmp"), "A successful correction left incomplete commit paths.");
                        Require(File.ReadAllLines(Path.Combine(profile, "fixture-startup-intent.txt")).SequenceEqual(new[] { "false" }), "The fixture startup callback did not receive exactly the explicit false intent.");
                    }
                    else
                    {
                        Require(rejection != null && !string.IsNullOrWhiteSpace(completion.Error) && client.Config.FramesPerSecond == 60,
                            "A controlled failed correction was reported as success or changed current preferences.");
                        Require(File.ReadAllBytes(settingsPath).SequenceEqual(originalSettings) && File.ReadAllBytes(statePath).SequenceEqual(originalState), "Rejected correction rewrote original settings or state.");
                        if (name == "startup-callback-failure")
                        {
                            var rolledBack = RecordingProfile.Load(profile);
                            Require(completion.Error.Contains("Controlled fixture startup failure") && rolledBack.Settings.FramesPerSecond == 60 && !rolledBack.Status.RequiresCorrection
                                && rolledBack.State.Paused && !rolledBack.State.UserStopped && rolledBack.State.ActivationRequired,
                                "Failure after completed writes did not roll back the previous valid effective settings.");
                            Require(File.ReadAllLines(Path.Combine(profile, "fixture-startup-intent.txt")).SequenceEqual(new[] { "false" }), "The failed fixture callback did not receive exactly one explicit false intent.");
                        }
                        else Require(!File.Exists(Path.Combine(profile, "fixture-startup-intent.txt")), "Rejected correction invoked the startup callback.");
                        if (name == "read-lease-failure") Require(Path.Exists(Path.Combine(profile, "profile-migration.json")), "The interrupted write lost its retained recovery journal.");
                    }
                    var reconnect = WorkerProtocol.EnsureWorker(profile, true, "must-not-launch-another-worker.exe", false);
                    Require(reconnect.Status.Instance == initial.Status.Instance && reconnect.Status.WorkerProcessId == worker.Id, "Reconnect replaced the task-owned worker."); AssertHeld(reconnect.Status);
                    Require(MediaDependency.Hash(retained) == retainedHash && MediaDependency.Hash(saved) == savedHash, "Correction/reconnect modified retained or saved media sentinels.");
                    if (positive)
                    {
                        byte[] correctedSettings = File.ReadAllBytes(settingsPath), correctedState = File.ReadAllBytes(statePath);
                        Quit(); worker = Launch(); var reopened = Call(new WorkerRequest()).GetAwaiter().GetResult(); AssertHeld(reopened.Status);
                        Require(reopened.Error == null && reopened.Status.Instance != initial.Status.Instance && reopened.Status.WorkerProcessId == worker.Id,
                            "Reopen did not establish a new task-owned worker.");
                        Require(File.ReadAllBytes(settingsPath).SequenceEqual(correctedSettings) && File.ReadAllBytes(statePath).SequenceEqual(correctedState), "Quit/reopen rewrote the corrected profile pair.");
                        AssertCorrected(profile, config, pausedIntent);
                    }
                    cases.Add(new { name, result = "passed", operationId = sent.OperationId, completedApply = completion.Status.CompletedApply,
                        instance = initial.Status.Instance, correctionSucceeded = positive, originalSettingsHash = Hash(originalSettings), originalStateHash = Hash(originalState),
                        settingsHash = MediaDependency.Hash(settingsPath), stateHash = MediaDependency.Hash(statePath), retainedHash, savedHash });
                }
            }
            finally { if (worker != null) Quit(); }

            Task<WorkerReply> Call(WorkerRequest request)
            {
                Require(request.Command is "status" or "apply" or "quit", "This fixture forbids activation, capture and engine commands.");
                return WorkerProtocol.CallAsync(profile, request);
            }
            Process Launch()
            {
                var info = new ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = false, CreateNoWindow = true };
                foreach (string argument in new[] { "--profile-correction-worker", profile, "--ownership-root", ProfileOwnership.RootDirectory, "--engine-root", engineRoot }) info.ArgumentList.Add(argument);
                return Process.Start(info) ?? throw new IOException("The task-owned paused worker did not start.");
            }
            void Quit()
            {
                var owned = worker; worker = null; bool normal = false;
                try { var quit = Call(new WorkerRequest { Command = "quit" }).GetAwaiter().GetResult(); normal = quit.Error == null && owned.WaitForExit(10000) && owned.ExitCode == 0; }
                finally
                {
                    if (!owned.HasExited) { owned.Kill(true); owned.WaitForExit(); }
                    exits.Add(new { pid = owned.Id, exited = owned.HasExited, normalQuit = normal, exitCode = owned.ExitCode }); owned.Dispose();
                }
                Require(normal, "Task-owned worker did not complete normal Quit; fallback termination is a failed cleanup.");
            }
        }
    }
    static void AssertHeld(WorkerStatus status)
    {
        Require(status != null && status.Paused && status.ProfileStatus.ActivationRequired && !status.Ready && !status.CanSave && status.Metrics.CaptureProcessId == 0,
            "The fixture must stay paused/held with no encoder process or save-ready state.");
    }
    static void AssertCorrected(string profile, Settings expected, bool pausedIntent)
    {
        var persisted = RecordingProfile.Load(profile);
        Require(persisted.Settings.FramesPerSecond == 30 && persisted.Settings.ReplayMinutes == 7 && persisted.Settings.BitrateMbps == 37
            && persisted.Settings.VideoCodec == "hevc_nvenc" && !persisted.Settings.SystemAudio && persisted.Settings.DisplayIndex == 0
            && persisted.Settings.CaptureMode == "window" && persisted.Settings.WindowExecutable == expected.WindowExecutable && persisted.Settings.WindowClass == expected.WindowClass
            && persisted.Settings.OutputDirectory == expected.OutputDirectory && persisted.Settings.ScreenshotDirectory == expected.ScreenshotDirectory
            && persisted.Settings.SaveKey == 115 && persisted.Settings.SaveModifiers == 7 && persisted.Settings.OverlayKey == 116 && persisted.Settings.OverlayModifiers == 7
            && persisted.Settings.ScreenshotKey == 117 && persisted.Settings.ScreenshotModifiers == 7, "A correction changed valid represented siblings.");
        Require(persisted.Settings.SchemaVersion == 1 && persisted.State.SchemaVersion == 1 && persisted.State.Paused == pausedIntent && persisted.State.UserStopped != pausedIntent
            && persisted.State.ActivationRequired && !persisted.Status.RequiresCorrection && persisted.Status.CanWrite, "Persisted state lost explicit pause/stopped intent or the activation hold.");
        Require(persisted.Settings.AdditionalPreferences.Count == 3 && persisted.Settings.AdditionalPreferences["SelectedPlaybackDeviceId"].GetString() == "missing-fixture-playback"
            && !persisted.Settings.AdditionalPreferences["MicrophoneMuted"].GetBoolean() && persisted.Settings.AdditionalPreferences["MicrophoneLevel"].GetInt32() == 0
            && persisted.State.AdditionalState["OpaqueState"].GetProperty("Checkpoint").GetInt32() == 0 && !persisted.State.AdditionalState["OpaqueState"].GetProperty("AutoResume").GetBoolean(),
            "Opaque device/false/zero or state values were lost.");
    }
    static void StageEngine(string source, string root, string data)
    {
        Require(MediaDependency.Hash(source).Equals(MediaDependency.ExecutableSha256, StringComparison.OrdinalIgnoreCase), "The supplied retained engine does not match the pinned trust root.");
        TrustedStorage.CreatePrivateDirectory(root); MediaDependency.RootDirectory = root;
        string package = Path.Combine(root, "ffmpeg-" + MediaDependency.PackageVersion), bin = Path.Combine(package, "bin");
        TrustedStorage.CreatePrivateDirectory(package); TrustedStorage.CreatePrivateDirectory(bin);
        if (!File.Exists(MediaDependency.ExecutablePath))
        {
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var output = new FileStream(MediaDependency.ExecutablePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(output); output.Flush(true);
        }
        using (MediaDependency.OpenVerifiedExecutable(MediaDependency.ExecutablePath)) { }
        File.WriteAllText(Path.Combine(data, "engine-copy.json"), JsonSerializer.Serialize(new {
            source, destination = MediaDependency.ExecutablePath, sourceHash = MediaDependency.Hash(source), copyHash = MediaDependency.Hash(MediaDependency.ExecutablePath),
            bytes = new FileInfo(MediaDependency.ExecutablePath).Length, executed = false
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
    static string StartupValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        object value = key?.GetValue("F10Recorder", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return JsonSerializer.Serialize(new { exists = value != null, kind = value == null ? "absent" : key.GetValueKind("F10Recorder").ToString(), value });
    }
    static string Hash(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
    static string HashText(string text) => Hash(System.Text.Encoding.UTF8.GetBytes(text));
    static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
