using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace F10Recorder;

// Offscreen controls and a real synthetic worker. No Window, capture affinity,
// desktop/audio acquisition, input injection, network or startup registration.
internal static class EngineTrustChecks
{
    static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    internal static int Run(string data, string workerExecutable, string package)
    {
        var report = new Dictionary<string, object>();
        Process worker = null;
        WorkerRecorder client = null;
        SettingsPanel panel = null;
        string originalRoot = MediaDependency.RootDirectory;
        var ownedCaptureIds = new HashSet<int>();
        string profile = Path.Combine(data, "profile"), managed = Path.Combine(data, "managed");
        Directory.CreateDirectory(profile); RecorderHost.Initialize(profile);
        MediaDependency.RootDirectory = managed;
        Task<WorkerReply> Call(string command = "status", bool value = false) => WorkerProtocol.CallAsync(profile, new WorkerRequest { Command = command, Value = value, AfterEvent = 0 });
        void Launch()
        {
            var info = new ProcessStartInfo(workerExecutable) { UseShellExecute = false, CreateNoWindow = true };
            foreach (string arg in new[] { "--engine-trust-worker", profile, package, "--engine-root", managed, "--ownership-root", Path.Combine(data, "ownership") }) info.ArgumentList.Add(arg);
            worker = Process.Start(info) ?? throw new IOException("Trust worker did not start.");
        }
        async Task<WorkerStatus> Until(Func<WorkerStatus, bool> predicate, string reason)
        {
            var timeout = Stopwatch.StartNew();
            while (timeout.Elapsed < TimeSpan.FromSeconds(25))
            {
                var reply = await Call(); Require(reply.Error == null, reply.Error);
                var status = reply.Status;
                if (status.Metrics?.CaptureProcessId > 0) ownedCaptureIds.Add(status.Metrics.CaptureProcessId);
                client?.Update(status);
                if (predicate(status)) return status;
                await Task.Delay(100);
            }
            throw new TimeoutException(reason);
        }
        Button ApplyButton() => panel.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == "ApplySettings");
        void Render(string name)
        {
            panel.SelectPage(3);
            // Render only the changed section; unattached content presenters need
            // explicit children. No platform Window is constructed for this fixture.
            var root = (Control)panel.GetLogicalDescendants().OfType<TextBlock>().Single(t => t.Text == "Recording engine").Parent;
            void Prepare(Control control)
            {
                control.ApplyStyling(); control.ApplyTemplate();
                if (control is ContentPresenter presenter) presenter.UpdateChild();
                foreach (var child in control.GetVisualChildren().OfType<Control>()) Prepare(child);
            }
            Prepare(root);
            root.Measure(new Size(980, 850)); root.Arrange(new Rect(0, 0, 980, 850));
            Require(root.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Recording engine" && t.Bounds.Height > 0), "Offscreen engine controls were not laid out.");
            using var bitmap = new RenderTargetBitmap(new PixelSize(980, 850)); bitmap.Render(root); bitmap.Save(Path.Combine(data, name + ".png"), PngBitmapEncoderOptions.Default);
            panel.SelectPage(0);
        }
        async Task Rejected(string scenario)
        {
            var status = await Until(s => !s.Ready && !s.CanSave && s.Metrics.CaptureProcessId == 0 && s.State.Contains("Repair recording engine"), scenario + " did not stop acquisition with repair feedback.");
            await panel.ProbeAsync();
            Require(!ApplyButton().IsEnabled && panel.EngineStatus.Contains("Repair recording engine"), scenario + " left stale UI choices.");
            report[scenario] = new { status.State, status.Ready, status.CanSave, Ui = panel.EngineStatus };
            Render(scenario);
        }
        async Task Check()
        {
            Require(MediaDependency.Hash(package) == MediaDependency.PackageSha256, "Input archive is not the approved package.");
            report["packageReceipt"] = new { Path = package, Sha256 = MediaDependency.Hash(package), MediaDependency.PackageVersion, MediaDependency.PackageUrl };
            bool startupBefore = WindowsStartup.Enabled;
            var settings = new Settings { ReplayMinutes = 1, FramesPerSecond = 30, BitrateMbps = 5, SystemAudio = false,
                SaveKey = 112, OverlayKey = 113, ScreenshotKey = 114, SaveModifiers = 7, OverlayModifiers = 7, ScreenshotModifiers = 7,
                OutputDirectory = Path.Combine(profile, "clips"), ScreenshotDirectory = Path.Combine(profile, "screenshots") };
            string settingsPath = Path.Combine(profile, "settings.json"); settings.Save(settingsPath);
            string preferences = File.ReadAllText(settingsPath);
            Directory.CreateDirectory(settings.OutputDirectory); Directory.CreateDirectory(settings.ScreenshotDirectory);
            string existingClip = Path.Combine(settings.OutputDirectory, "preserve.mp4"), existingImage = Path.Combine(settings.ScreenshotDirectory, "preserve.png");
            File.WriteAllText(existingClip, "Existing clip sentinel"); File.WriteAllText(existingImage, "Existing screenshot sentinel");
            string clipHash = MediaDependency.Hash(existingClip), imageHash = MediaDependency.Hash(existingImage);
            Launch();
            var initial = await Until(s => s.Paused, "Worker did not start paused.");
            Require(initial.SaveRegistered && initial.OverlayRegistered && initial.ScreenshotRegistered, "Isolated fixture shortcuts unavailable; use an ordinary desktop token with Ctrl+Alt+Shift+F1-F3 free.");
            client = new WorkerRecorder(initial, request => WorkerProtocol.CallAsync(profile, request));
            panel = new SettingsPanel(client); client.Changed += panel.UpdateState;
            await client.ProbeMediaAsync().ContinueWith(task => Require(task.IsFaulted, "Missing engine probe unexpectedly succeeded."));
            await Call("pause", false);
            await Rejected("missing");
            await Call("restart"); await Task.Delay(500);
            Require(!File.Exists(Path.Combine(profile, "repair-acquisitions.txt")), "A worker restart provisioned an engine.");
            File.WriteAllText(Path.Combine(profile, "reject-repair.txt"), "bad archive");
            await panel.RepairEngineAsync();
            Require(panel.Error.Contains("repair failed") && !ApplyButton().IsEnabled, "Repair failure was hidden or enabled stale choices.");
            report["repairFailure"] = panel.Error; Render("repair-failure");
            Require(File.ReadAllText(Path.Combine(profile, "media-install-error.txt")).Contains("checksum"), "Detailed repair rejection was not retained.");
            File.Delete(Path.Combine(profile, "reject-repair.txt"));
            await panel.RepairEngineAsync();
            var good = await Until(s => s.Ready && s.CanSave && s.Metrics.CaptureProgressSeconds >= 3, "Verified repair did not restore synthetic acquisition.");
            Require(ApplyButton().IsEnabled && panel.EngineStatus.Contains("verified"), "Good engine capabilities did not enable UI choices.");
            var receipt = await client.ProbeMediaAsync(); report["capabilityReceipt"] = receipt;
            Require(receipt.EngineSha256 == MediaDependency.Hash(MediaDependency.ExecutablePath) && receipt.Encoders.Any(c => c.Codec == "h264_nvenc"), "Good receipt lacks authenticated initialized H.264.");
            Render("verified");
            long savedAfter = good.Sequence;
            Require((await Call("save")).Error == null, "Synthetic replay was rejected.");
            var saved = await Until(s => s.Events.Any(e => e.Sequence > savedAfter && e.Kind == "saved"), "Synthetic save did not finish.");
            var outcome = saved.Events.First(e => e.Sequence > savedAfter && e.Kind == "saved");
            Require(outcome.Success && File.Exists(outcome.Message), "Synthetic save failed: " + outcome.Message);
            using (var decode = CaptureSession.StartProcess(MediaDependency.ResolveExecutable(), new[] { "-v", "error", "-i", outcome.Message, "-f", "null", "-" }, data))
            {
                var errors = decode.StandardError.ReadToEndAsync(); var output = decode.StandardOutput.ReadToEndAsync();
                try { Require(await Task.Run(() => decode.WaitForExit(15000)) && decode.ExitCode == 0, "Synthetic replay failed decoding."); }
                finally { if (!decode.HasExited) { decode.Kill(); decode.WaitForExit(); } }
                Require(string.IsNullOrWhiteSpace(await errors), "Decode reported errors."); await output;
            }
            report["decodedSyntheticClip"] = outcome.Message;
            await Call("pause", true);
            var paused = await Until(s => s.Paused && s.Metrics.CaptureProcessId == 0, "Pause did not stop synthetic acquisition.");
            await panel.ProbeAsync(); Require(ApplyButton().IsEnabled, "Paused engine could not be checked.");
            paused = (await Call()).Status; client.Update(paused);
            // Production client policy: transport loss and replacement workers invalidate
            // capabilities even when a delayed response from the old worker arrives.
            var disconnected = JsonSerializer.Deserialize<WorkerStatus>(JsonSerializer.Serialize(paused, WorkerProtocol.Json), WorkerProtocol.Json);
            disconnected.Revision++; disconnected.Phase = CapturePhase.Reconnecting; disconnected.State = "Recorder unavailable";
            client.Update(disconnected); Require(!ApplyButton().IsEnabled, "Disconnected UI retained capabilities.");
            await Call("quit"); Require(await Task.Run(() => worker.WaitForExit(15000)), "Worker did not exit."); worker.Dispose(); Launch();
            var replacement = await Until(s => s.Paused && s.Instance != paused.Instance, "Replacement worker/persisted pause unavailable.");
            client.Update(paused); Require(client.EngineCapabilityContext.StartsWith(replacement.Instance) && !ApplyButton().IsEnabled, "Delayed old-worker status revived capabilities.");
            await panel.ProbeAsync(); Require(ApplyButton().IsEnabled, "Reconnect did not allow an explicit fresh probe.");
            // Deliver an authenticated receipt late across each invalidating transition.
            foreach (string transition in new[] { "worker", "generation", "reconnecting", "recovering" })
            {
                WorkerStatus Copy(WorkerStatus value) => JsonSerializer.Deserialize<WorkerStatus>(JsonSerializer.Serialize(value, WorkerProtocol.Json), WorkerProtocol.Json);
                var before = Copy(replacement); before.Revision = 100;
                var delayed = new TaskCompletionSource<WorkerReply>();
                using var delayedClient = new WorkerRecorder(before, _ => delayed.Task);
                var delayedPanel = new SettingsPanel(delayedClient); delayedClient.Changed += delayedPanel.UpdateState;
                var checking = delayedPanel.ProbeAsync();
                var changed = Copy(before); changed.Revision++;
                if (transition == "worker") changed.Instance = "replacement-" + transition;
                else if (transition == "generation") changed.EngineCapabilityContext += "-new";
                else changed.Phase = transition == "reconnecting" ? CapturePhase.Reconnecting : CapturePhase.Recovering;
                delayedClient.Update(changed);
                if (transition is "reconnecting" or "recovering") { var restored = Copy(changed); restored.Revision++; restored.Phase = CapturePhase.Paused; delayedClient.Update(restored); }
                string currentContext = delayedClient.EngineCapabilityContext;
                delayed.SetResult(new WorkerReply { Status = Copy(before), Capabilities = receipt }); await checking;
                var delayedApply = delayedPanel.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == "ApplySettings");
                Require(!delayedApply.IsEnabled && delayedClient.EngineCapabilityContext == currentContext && delayedPanel.EngineStatus.Contains("changed"), "Delayed " + transition + " receipt revived stale capabilities or status.");
            }
            report["delayedProbeWorkerGenerationAndRecoveryRejected"] = true;
            var oldProtocol = await WorkerProtocol.CallAsync(profile, new WorkerRequest { Version = 3, Command = "probeMedia" });
            Require(oldProtocol.Error != null && oldProtocol.Capabilities == null, "Old protocol request was accepted.");
            report["reconnectAndOldProtocol"] = true;
            DateTime timestamp = File.GetLastWriteTimeUtc(MediaDependency.ExecutablePath);
            using (var bytes = new FileStream(MediaDependency.ExecutablePath, FileMode.Open, FileAccess.ReadWrite)) { int first = bytes.ReadByte(); bytes.Position = 0; bytes.WriteByte((byte)(first ^ 1)); }
            File.SetLastWriteTimeUtc(MediaDependency.ExecutablePath, timestamp);
            await Call("pause", false); await Rejected("tampered");
            await Call("pause", true); await Until(s => s.Paused && s.Metrics.CaptureProcessId == 0, "Tampered worker could not pause.");
            string tamperedHash = MediaDependency.Hash(MediaDependency.ExecutablePath);
            File.WriteAllText(Path.Combine(profile, "reject-repair.txt"), "bad archive"); await panel.RepairEngineAsync();
            Require(panel.Error.Contains("repair failed") && MediaDependency.Hash(MediaDependency.ExecutablePath) == tamperedHash, "Failed repair changed the rejected engine or hid failure.");
            File.Delete(Path.Combine(profile, "reject-repair.txt")); await panel.RepairEngineAsync();
            var repaired = (await Call()).Status;
            Require(repaired.Paused && !repaired.Ready && repaired.Metrics.CaptureProcessId == 0 && ApplyButton().IsEnabled, "Repair lost intentional pause or capabilities.");
            Require(File.ReadAllText(settingsPath) == preferences && MediaDependency.Hash(existingClip) == clipHash && MediaDependency.Hash(existingImage) == imageHash, "Repair changed preferences or existing media.");
            Require(!File.Exists(Path.Combine(profile, "unexpected-startup-write.txt")) && WindowsStartup.Enabled == startupBefore, "Startup activation changed.");
            Require(File.ReadAllLines(Path.Combine(profile, "repair-acquisitions.txt")).Length == 4, "Unexpected provisioning outside four explicit repairs.");
            report["pausePreferencesMediaStartupPreserved"] = true;
            report["result"] = "passed";
        }
        AppBuilder.Configure<Application>().UsePlatformDetect().SetupWithoutStarting();
        Application.Current.Styles.Add(new FluentTheme()); Ui.AddStyles(Application.Current.Styles);
        using var done = new CancellationTokenSource();
        Dispatcher.UIThread.Post(async () =>
        {
            try { await Check(); }
            catch (Exception ex) { report["result"] = "failed"; report["error"] = ex.ToString(); }
            finally
            {
                client?.Dispose();
                if (worker != null)
                {
                    try { await Call("quit"); await Task.Run(() => worker.WaitForExit(15000)); } catch (Exception ex) { report["cleanupError"] = ex.Message; }
                    if (!worker.HasExited) { worker.Kill(true); worker.WaitForExit(5000); }
                    worker.Dispose();
                }
                report["ownedCaptureIds"] = ownedCaptureIds.ToArray();
                report["remainingOwnedCaptureIds"] = ownedCaptureIds.Where(id => { try { using var process = Process.GetProcessById(id); return !process.HasExited; } catch (ArgumentException) { return false; } }).ToArray();
                if (((int[])report["remainingOwnedCaptureIds"]).Length != 0) { report["result"] = "failed"; report["cleanupError"] = "Owned engine process remains."; }
                MediaDependency.RootDirectory = originalRoot;
                File.WriteAllText(Path.Combine(data, "verification.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions(WorkerProtocol.Json) { WriteIndented = true }));
                done.Cancel();
            }
        });
        Dispatcher.UIThread.MainLoop(done.Token);
        return report.TryGetValue("result", out object result) && (string)result == "passed" ? 0 : 1;
    }
}
