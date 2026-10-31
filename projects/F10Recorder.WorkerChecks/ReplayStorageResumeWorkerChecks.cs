using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace F10Recorder;

// A real paused worker and ordinary client consume the negative Resume result.
// The deliberately absent private engine cannot execute even if preflight regresses.
internal static class ReplayStorageResumeWorkerChecks
{
    internal static int Run(string root, string workerExecutable, bool initialAdmission = false)
    {
        root = Path.GetFullPath(root); workerExecutable = Path.GetFullPath(workerExecutable);
        if (Directory.Exists(root)) throw new IOException("Use a fresh worker evidence directory.");
        if (ReplayCalibrationCatalog.Load().Model != null) throw new IOException("Missing-model tracer requires an absent ordinary catalog; existing evidence is preserved.");
        Directory.CreateDirectory(root);
        string profile = Path.Combine(root, "profile"), security = Path.Combine(root, "ownership"), missingEngine = Path.Combine(root, "absent-engine");
        Directory.CreateDirectory(profile);
        var config = new Settings { OutputDirectory = Path.Combine(profile, "output"), ScreenshotDirectory = Path.Combine(profile, "pictures"),
            SaveKey = 115, SaveModifiers = 7, OverlayKey = 116, OverlayModifiers = 7, ScreenshotKey = 117, ScreenshotModifiers = 7 };
        config.Save(Path.Combine(profile, "settings.json"));
        File.WriteAllText(Path.Combine(profile, "worker-state.json"), "{\"SchemaVersion\":1,\"Paused\":true,\"UserStopped\":true,\"ActivationRequired\":true}");
        string sentinel = Path.Combine(profile, "buffer", "session-retained", "sentinel.ts");
        Directory.CreateDirectory(Path.GetDirectoryName(sentinel)); File.WriteAllText(sentinel, "Preserve retained footage sentinel; not media.");
        var latch = new ReplayStorageStopLatch(profile, security);
        var held = initialAdmission ? latch.Read() : latch.Stop("Controlled durable low-storage stop.");
        Require(initialAdmission ? !held.Held : held.Held && held.Durable, "Fixture initial hold state is incorrect.");
        var originals = new[] { Path.Combine(profile, "settings.json"), Path.Combine(profile, "worker-state.json"), sentinel }.ToDictionary(path => path, Hash);
        string startup = Startup();
        var checks = new List<string>(); var report = new Dictionary<string, object> { ["workerExecutable"] = workerExecutable, ["workerSha256"] = Hash(workerExecutable) };
        Process worker = null; bool normal = false, fallback = false;
        try
        {
            Start();
            var initial = Call("status"); AssertHeld(initial);
            File.WriteAllText(Path.Combine(root, "initial-reply.json"), JsonSerializer.Serialize(initial, WorkerProtocol.Json));
            using (var client = new WorkerRecorder(initial.Status, _ => throw new IOException("Status consumer must issue no command."), profile))
                Require(client.Paused && client.Metrics.ReplayStorageSafetyHeld == !initialAdmission && !client.Ready,
                    "Ordinary client lost the actual worker storage hold.");
            checks.Add("actual-worker-storage-hold-consumed-by-ordinary-client");
            var rejected = Call("pause", false);
            File.WriteAllText(Path.Combine(root, "resume-reply.json"), JsonSerializer.Serialize(rejected, WorkerProtocol.Json));
            Require(rejected.Error?.Contains("qualified capture headroom", StringComparison.OrdinalIgnoreCase) == true,
                "Missing-model Resume must reject at storage preflight before profile Commit or engine/audio discovery.");
            AssertHeld(rejected);
            foreach (var original in originals) Require(Hash(original.Key) == original.Value, "Rejected Resume changed profile/state/retained media.");
            Require(latch.Read().HoldId == held.HoldId && !Directory.Exists(missingEngine), "Rejected Resume changed the named hold or touched the absent engine root.");
            checks.Add(initialAdmission ? "initial-missing-model-admission-rejected-before-commit-and-engine-discovery" : "missing-model-resume-rejected-before-commit-and-engine-discovery");
            var oversized = new Settings { ReplayMinutes = 30, BitrateMbps = 100, ReplayCacheBudgetBytes = 10_000_000_000 };
            var budgetRejected = Call("apply", settings: oversized);
            File.WriteAllText(Path.Combine(root, "long-profile-reply.json"), JsonSerializer.Serialize(budgetRejected, WorkerProtocol.Json));
            Require(budgetRejected.Error?.Contains("budget", StringComparison.OrdinalIgnoreCase) == true
                && budgetRejected.Error.Contains("explicitly change", StringComparison.OrdinalIgnoreCase),
                "The actual worker must require explicit budget/settings changes for an impossible 30-minute 100Mbps profile.");
            AssertHeld(budgetRejected);
            Require(JsonSerializer.Serialize(initial.Status.Config, WorkerProtocol.Json) == JsonSerializer.Serialize(budgetRejected.Status.Config, WorkerProtocol.Json),
                "Budget rejection silently changed actual quality, duration or settings.");
            foreach (var original in originals) Require(Hash(original.Key) == original.Value, "Long-profile rejection changed originals or footage.");
            Require(!Directory.Exists(missingEngine), "Impossible-profile rejection reached engine discovery.");
            checks.Add("actual-worker-impossible-long-profile-requires-explicit-change-without-media-loss");
            AssertHeld(Call("restart"));
            checks.Add("environment-restart-does-not-clear-storage-hold");
            Quit();
            normal = false; Start(); AssertHeld(Call("status")); Quit();
            Require(latch.Read().HoldId == held.HoldId && Hash(sentinel) == originals[sentinel], "Worker replacement changed hold or media.");
            checks.Add("hold-survives-actual-worker-quit-and-replacement");
            Require(!File.Exists(Path.Combine(profile, "fixture-startup-intent.txt")) && Startup() == startup, "Tracer changed startup intent or actual startup state.");
            checks.Add("normal-owned-process-exit-and-startup-preservation");
            report["result"] = "passed";
        }
        catch (Exception ex) { report["result"] = "failed"; report["error"] = ex.ToString(); }
        finally
        {
            if (worker != null)
            {
                try
                {
                    if (!worker.HasExited) Quit();
                    Require(worker.HasExited, "Owned paused worker absence is unconfirmed.");
                }
                catch (Exception ex)
                {
                    report["cleanupError"] = ex.ToString(); report["result"] = "failed";
                    if (!worker.HasExited) { fallback = true; worker.Kill(true); worker.WaitForExit(5000); }
                }
                finally { worker.Dispose(); }
            }
        }
        report["normalQuit"] = normal; report["fallbackTermination"] = fallback; report["checks"] = checks;
        report["startupPreserved"] = Startup() == startup;
        report["initialAdmission"] = initialAdmission;
        report["effects"] = "Two serial isolated paused workers, current-user pipe, hidden Ctrl+Alt+Shift F4-F6 shortcuts, tiny fixtures and read-only startup state. No engine/probe/capture/GPU/audio/visible UI/startup write/install. Missing-model admission rejection is controlled negative evidence, not positive qualified capture acceptance.";
        if (!normal || fallback || !(bool)report["startupPreserved"]) report["result"] = "failed";
        File.WriteAllText(Path.Combine(root, "storage-resume-worker.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Storage Resume paused-worker checks: {report["result"]}; {checks.Count} checks");
        return (string)report["result"] == "passed" ? 0 : 1;

        void Start()
        {
            worker?.Dispose();
            var launch = new ProcessStartInfo(workerExecutable) { UseShellExecute = false, CreateNoWindow = true };
            foreach (string arg in new[] { "--profile-correction-worker", profile, "--ownership-root", security, "--engine-root", missingEngine }) launch.ArgumentList.Add(arg);
            worker = Process.Start(launch) ?? throw new IOException("Paused worker failed to launch.");
        }
        WorkerReply Call(string command, bool value = false, Settings settings = null)
        {
            using var timeout = new CancellationTokenSource(10000);
            return WorkerProtocol.CallAsync(profile, new WorkerRequest { Command = command, Value = value, Settings = settings,
                OperationId = command == "apply" ? Guid.NewGuid().ToString("N") : null }, timeout.Token).GetAwaiter().GetResult();
        }
        void AssertHeld(WorkerReply reply)
        {
            var status = reply.Status;
            Require(status != null && status.WorkerProcessId == worker.Id && status.Paused && !status.Ready && !status.CanSave
                && status.Metrics.CaptureProcessId == 0 && status.Metrics.ReplayStorageSafetyHeld == !initialAdmission
                && status.Metrics.ReplayStorageSafetyKind == (initialAdmission ? "Unmeasured" : "Stop")
                && !string.IsNullOrWhiteSpace(status.Metrics.ReplayStorageSafetyReason) && status.Metrics.ReplayStorageSafetyReason.Length <= 512,
                "Actual worker must expose its bounded durable hold and no acquisition.");
        }
        void Quit()
        {
            var quit = Call("quit");
            normal = quit.Error == null && worker.WaitForExit(10000) && worker.ExitCode == 0;
            Require(normal, "Owned worker did not quit normally.");
        }
    }
    static string Hash(string path) { using var input = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(input)); }
    static string Startup()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        var value = key?.GetValue("F10Recorder", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return JsonSerializer.Serialize(new { value, kind = value == null ? "absent" : key.GetValueKind("F10Recorder").ToString() });
    }
    static void Require(bool value, string message) { if (!value) throw new IOException(message); }
}
