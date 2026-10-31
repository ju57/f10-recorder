using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace F10Recorder;

// Independently designed integration tracer. Uses the existing paused-worker
// fixture entry and production status/client projection; never starts capture.
internal static class ReplayStorageWorkerChecks
{
    internal static int Run(string root, string workerExecutable, string engineRoot)
    {
        Require(Path.IsPathFullyQualified(root) && Path.IsPathFullyQualified(workerExecutable) && Path.IsPathFullyQualified(engineRoot), "All tracer paths must be absolute.");
        root = Path.GetFullPath(root); workerExecutable = Path.GetFullPath(workerExecutable); engineRoot = Path.GetFullPath(engineRoot);
        Require(File.Exists(workerExecutable), "The frozen worker executable is missing.");
        string profile = Path.Combine(root, "profile"), receipt = Path.Combine(root, "verification.json");
        Require(!Directory.Exists(profile) && !File.Exists(receipt), "The tracer requires a fresh profile and evidence receipt.");
        Directory.CreateDirectory(root);
        var checks = new List<string>(); var report = new Dictionary<string, object>();
        Process worker = null; bool normalQuit = false, fallbackTermination = false;
        string engine = Path.Combine(engineRoot, "ffmpeg-" + MediaDependency.PackageVersion, "bin", "ffmpeg.exe");
        string startupBefore = StartupValue();
        var hashes = new Dictionary<string, string>();
        try
        {
            Require(File.Exists(engine) && Hash(engine).Equals(MediaDependency.ExecutableSha256, StringComparison.OrdinalIgnoreCase), "Existing approved engine is unavailable; this tracer cannot provision it.");
            string engineHash = Hash(engine);
            report["workerExecutable"] = workerExecutable; report["workerExecutableSha256"] = Hash(workerExecutable);
            report["engine"] = engine; report["engineSha256"] = engineHash;
            Directory.CreateDirectory(profile);
            var config = new Settings {
                SystemAudio = false, DisplayIndex = 0,
                SaveKey = 115, SaveModifiers = 7, OverlayKey = 116, OverlayModifiers = 7, ScreenshotKey = 117, ScreenshotModifiers = 7,
                OutputDirectory = Path.Combine(profile, "Recordings"), ScreenshotDirectory = Path.Combine(profile, "Screenshots")
            };
            config.AdditionalPreferences["TracerOpaqueFalse"] = JsonSerializer.SerializeToElement(false);
            config.AdditionalPreferences["TracerOpaqueZero"] = JsonSerializer.SerializeToElement(0);
            config.Save(Path.Combine(profile, "settings.json"));
            File.WriteAllText(Path.Combine(profile, "worker-state.json"), "{\"SchemaVersion\":1,\"Paused\":true,\"UserStopped\":true,\"ActivationRequired\":true,\"TracerState\":0}");
            var persisted = RecordingProfile.Load(profile);
            Require(persisted.Status.CanWrite && !persisted.Status.RequiresCorrection && persisted.State.Paused && persisted.Status.ActivationRequired, "Portable profile setup is not paused and activation-held.");
            string retained = Path.Combine(profile, "buffer", "session-retained", "retained.ts");
            Directory.CreateDirectory(Path.GetDirectoryName(retained));
            File.WriteAllText(retained, "Owned retained-footage sentinel, not playable media.");
            Directory.CreateDirectory(config.OutputDirectory); Directory.CreateDirectory(config.ScreenshotDirectory);
            File.WriteAllText(Path.Combine(config.OutputDirectory, "saved.mp4"), "Owned saved-media sentinel, not playable media.");
            File.WriteAllText(Path.Combine(config.ScreenshotDirectory, "saved.png"), "Owned picture sentinel, not a real image.");
            foreach (string file in new[] { Path.Combine(profile, "settings.json"), Path.Combine(profile, "worker-state.json"), retained,
                Path.Combine(config.OutputDirectory, "saved.mp4"), Path.Combine(config.ScreenshotDirectory, "saved.png") }) hashes.Add(file, Hash(file));
            File.WriteAllText(Path.Combine(root, "before.json"), JsonSerializer.Serialize(hashes, WorkerProtocol.Json));
            var launch = new ProcessStartInfo(workerExecutable) { UseShellExecute = false, CreateNoWindow = true };
            string[] arguments = ["--profile-correction-worker", profile, "--ownership-root", Path.Combine(root, "ownership"), "--engine-root", engineRoot];
            foreach (string argument in arguments) launch.ArgumentList.Add(argument);
            report["arguments"] = arguments;
            worker = Process.Start(launch) ?? throw new IOException("Task-owned paused worker did not launch.");
            report["workerPid"] = worker.Id; report["workerStartedUtc"] = worker.StartTime.ToUniversalTime().ToString("O");
            var initial = Call("status", 10000);
            Require(initial.Error == null && initial.Status != null && initial.Status.WorkerProcessId == worker.Id, "Status did not come from the task-owned worker.");
            File.WriteAllText(Path.Combine(root, "actual-reply.json"), JsonSerializer.Serialize(initial, WorkerProtocol.Json));
            AssertHeld(initial.Status);
            using (var client = new WorkerRecorder(initial.Status, _ => throw new InvalidOperationException("Status-consumer tracer must not issue a command."), profile))
            {
                AssertUnmeasured(client.Metrics);
                Require(client.Paused && client.ProfileStatus.ActivationRequired && client.CaptureProcessId == 0 && !client.Ready && !client.CanSave, "The actual reply changed while consumed by WorkerRecorder.");
            }
            checks.Add("actual-paused-worker-reply-consumed-as-unmeasured");

            // Sensitivity control: remove fields from the actual reply's JSON.
            // Production framing/deserialization must restore RecorderMetrics'
            // declared defaults; the real consumer must retain that observation.
            var document = JsonNode.Parse(JsonSerializer.Serialize(initial, WorkerProtocol.Json)).AsObject();
            var metrics = document["Status"]["Metrics"].AsObject();
            Require(metrics.Remove("ReplayOutputDemandKind") && metrics.Remove("ReplayOutputDemandRevision") && metrics.Remove("ReplayOutputDemandReason"), "Actual reply is missing the new field contract.");
            byte[] body = Encoding.UTF8.GetBytes(document.ToJsonString());
            using var framed = new MemoryStream(); byte[] header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, body.Length); framed.Write(header); framed.Write(body); framed.Position = 0;
            var absent = WorkerProtocol.ReadAsync<WorkerReply>(framed, CancellationToken.None).GetAwaiter().GetResult();
            using (var client = new WorkerRecorder(absent.Status, _ => throw new InvalidOperationException("Missing-field consumer control must not issue a command."), profile)) AssertUnmeasured(client.Metrics);
            checks.Add("missing-fields-use-production-defaults-through-framing-and-consumer");
            AssertPreserved();
            var final = Call("status", 10000); AssertHeld(final.Status); AssertUnmeasured(final.Status.Metrics);
            Require(final.Error == null && final.Status.WorkerProcessId == worker.Id, "Final status lost the owned worker.");
            var quit = Call("quit", 10000);
            normalQuit = quit.Error == null && worker.WaitForExit(10000) && worker.ExitCode == 0;
            Require(normalQuit, "Paused worker did not complete normal Quit with exit code zero.");
            checks.Add("normal-quit-and-owned-process-exit");
            AssertPreserved();
            Require(Hash(engine) == engineHash, "Read-only engine changed during the tracer.");
            checks.Add("profile-startup-engine-and-media-preserved");
            report["result"] = "passed";
        }
        catch (Exception ex) { report["result"] = "failed"; report["error"] = ex.ToString(); }
        finally
        {
            if (worker != null)
            {
                try
                {
                    if (!worker.HasExited)
                    {
                        try { var quit = Call("quit", 10000); normalQuit = quit.Error == null && worker.WaitForExit(10000) && worker.ExitCode == 0; }
                        catch (Exception ex) { report["cleanupError"] = ex.ToString(); }
                        if (!worker.HasExited) { fallbackTermination = true; worker.Kill(true); worker.WaitForExit(10000); }
                    }
                    report["workerExited"] = worker.HasExited;
                    if (worker.HasExited) report["workerExitCode"] = worker.ExitCode;
                    if (!worker.HasExited || !normalQuit || fallbackTermination) { report["result"] = "failed"; report["cleanupFailed"] = true; }
                }
                catch (Exception ex) { report["result"] = "failed"; report["cleanupError"] = ex.ToString(); }
                finally { worker.Dispose(); }
            }
        }
        report["normalQuit"] = normalQuit; report["fallbackTermination"] = fallbackTermination; report["checks"] = checks;
        report["startupPreserved"] = StartupValue() == startupBefore;
        if (!(bool)report["startupPreserved"]) report["result"] = "failed";
        report["effects"] = "One isolated paused/activation-held worker, current-user named pipe, private profile ownership and hidden Ctrl+Alt+Shift F4-F6 shortcuts; read-only approved engine hash checks. No engine/probe/capture/GPU/audio/UI/startup registry write/install/vendor change. Missing-field control is framed-consumer compatibility, not model qualification or runtime enforcement.";
        File.WriteAllText(receipt, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Replay storage actual-worker tracer: {report["result"]}; {checks.Count} checks. {root}");
        return (string)report["result"] == "passed" ? 0 : 1;

        WorkerReply Call(string command, int timeoutMilliseconds)
        {
            Require(command is "status" or "quit", "Only status and Quit are permitted by this tracer.");
            using var deadline = new CancellationTokenSource(timeoutMilliseconds);
            return WorkerProtocol.CallAsync(profile, new WorkerRequest { Command = command }, deadline.Token).GetAwaiter().GetResult();
        }
        void AssertPreserved()
        {
            foreach (var item in hashes) Require(File.Exists(item.Key) && Hash(item.Key) == item.Value, "Tracer modified a preserved profile/media sentinel: " + item.Key);
            Require(!File.Exists(Path.Combine(profile, "fixture-startup-intent.txt")), "Status-only tracer invoked the fixture startup writer.");
            Require(StartupValue() == startupBefore, "Actual F10 startup registry value or kind changed.");
        }
    }
    static void AssertHeld(WorkerStatus status) => Require(status != null && status.Paused && status.ProfileStatus.ActivationRequired && !status.Ready && !status.CanSave && status.Metrics?.CaptureProcessId == 0, "Worker must remain paused/activation-held with no acquisition.");
    static void AssertUnmeasured(RecorderMetrics metrics) => Require(metrics != null && metrics.ReplayOutputDemandKind == "Unmeasured" && metrics.ReplayOutputDemandRevision == null && !string.IsNullOrWhiteSpace(metrics.ReplayOutputDemandReason) && metrics.ReplayOutputDemandReason.Length <= 512, "Consumer must observe explicit bounded Unmeasured reason and no revision.");
    static string Hash(string path) { using var input = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(input)); }
    static string StartupValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        object value = key?.GetValue("F10Recorder", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return JsonSerializer.Serialize(new { exists = value != null, kind = value == null ? "absent" : key.GetValueKind("F10Recorder").ToString(), value });
    }
    static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
