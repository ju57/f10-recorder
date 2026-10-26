using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace F10Recorder;

internal static class ProfileDesktopChecks
{
    internal static int Run(string data, string snapshotsPath)
    {
        Directory.CreateDirectory(data); var report = new Dictionary<string, object>(); var cases = new List<object>();
        var snapshots = JsonSerializer.Deserialize<WorkerStatus[]>(File.ReadAllText(snapshotsPath), WorkerProtocol.Json);
        AppBuilder.Configure<Application>().UsePlatformDetect().SetupWithoutStarting();
        Application.Current.Styles.Add(new FluentTheme()); Ui.AddStyles(Application.Current.Styles);
        using var done = new CancellationTokenSource();
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                Require(snapshots.Length == 11, "Expected eleven real worker outcomes, including opaque preference transport.");
                foreach (var status in snapshots)
                {
                    using var client = new WorkerRecorder(status, _ => throw new Exception("Offscreen migration checks must not issue engine/capture commands."));
                    var panel = new SettingsPanel(client); client.Changed += panel.UpdateState;
                    Require(status.Config.AdditionalPreferences.Count == 0, "Desktop snapshots must not carry worker-owned opaque preference data.");
                    var controls = panel.GetLogicalDescendants().OfType<Control>().ToArray();
                    var feedback = controls.OfType<TextBlock>().SingleOrDefault(c => c.Name == "ProfileCompatibility");
                    Require(feedback != null && feedback.Text == status.ProfileStatus.Message, "Desktop must expose the worker's migration/correction result.");
                    Require(controls.OfType<ComboBox>().Single(c => c.Name == "FramesPerSecond").SelectedItem.Equals(status.Config.FramesPerSecond), "Desktop discarded the effective FPS.");
                    panel.SelectPage(3);
                    Require(panel.GetLogicalDescendants().OfType<TextBox>().Single(c => c.Name == "OutputDirectory").Text == status.Config.OutputDirectory, "Desktop discarded the valid output folder.");
                    panel.SelectPage(0);
                    bool canResume = status.ProfileStatus.CanWrite && !status.ProfileStatus.RequiresCorrection;
                    Require(controls.Single(c => c.Name == "RecordingEnabled").IsEnabled == canResume, "Desktop offered Resume for an incompatible/correction-required profile.");
                    // Reconnecting/late old-instance input exercises production status ordering.
                    var replacement = JsonSerializer.Deserialize<WorkerStatus>(JsonSerializer.Serialize(status, WorkerProtocol.Json), WorkerProtocol.Json);
                    replacement.Instance = "replacement-" + status.Instance; replacement.Revision++;
                    client.Update(replacement); client.Update(status);
                    Require(client.ProfileStatus.Outcome == replacement.ProfileStatus.Outcome && client.EngineCapabilityContext.StartsWith(replacement.Instance), "Delayed old-worker status replaced reconnect state.");
                    if (status.ProfileStatus.Outcome == "correction-required")
                    {
                        feedback.Measure(new Size(900, 160)); feedback.Arrange(new Rect(0, 0, 900, 160));
                        using var bitmap = new RenderTargetBitmap(new PixelSize(900, 160)); bitmap.Render(feedback); bitmap.Save(Path.Combine(data, "profile-correction.png"), PngBitmapEncoderOptions.Default);
                    }
                    cases.Add(new { status.ProfileStatus.Outcome, status.ProfileStatus.ExistingProfile, canResume, result = "passed" });
                }
                Require(snapshots.Count(s => s.RetainedPreferenceCount == 1) == 3, "Expected small, Unicode and near-limit opaque preference receipts.");
                var rejection = JsonSerializer.Deserialize<WorkerReply>(File.ReadAllText(Path.Combine(Path.GetDirectoryName(snapshotsPath), "large-unicode", "correction-reply.json")), WorkerProtocol.Json);
                var heldStatus = snapshots.Single(s => s.Instance == rejection.Status.Instance);
                using (var client = new WorkerRecorder(heldStatus, _ => System.Threading.Tasks.Task.FromResult(rejection)))
                {
                    var panel = new SettingsPanel(client); client.Changed += panel.UpdateState;
                    var notice = new System.Threading.Tasks.TaskCompletionSource<string>();
                    client.Notification += (_, message, _, _) => notice.TrySetResult(message);
                    client.Pause(false);
                    string message = await notice.Task.WaitAsync(TimeSpan.FromSeconds(3));
                    Require(message.Contains("Encoded profile exceeds 1 MiB") && message.Contains("Original preserved") && client.Paused && client.ProfileStatus.ActivationRequired && client.Config.VideoCodec == "hevc_nvenc" && !client.Config.SystemAudio,
                        "Desktop must surface the actual bounded correction failure while preserving represented intent and the activation hold.");
                }
                var selectedCodec = snapshots[0];
                using (var client = new WorkerRecorder(selectedCodec, request =>
                {
                    Require(request.Command == "probeMedia", "Unexpected command in controlled capability-response check.");
                    return System.Threading.Tasks.Task.FromResult(new WorkerReply { Status = selectedCodec, Capabilities = new RecordingEngineCapabilities { Encoders = new[] { new EncoderOption { Codec = "h264_nvenc", Name = "Controlled H.264 capability response" } } } });
                }))
                {
                    var panel = new SettingsPanel(client); await panel.ProbeAsync();
                    Require(panel.GetLogicalDescendants().OfType<ComboBox>().Single(c => c.Name == "VideoCodec").SelectedItem == null && client.Config.VideoCodec == "hevc_nvenc", "An unavailable valid codec must remain selected intent, without silently substituting another codec during correction.");
                }
                var partialCodec = snapshots.Single(s => s.ProfileStatus.Outcome == "correction-required"); Settings submitted = null;
                using (var client = new WorkerRecorder(partialCodec, request =>
                {
                    if (request.Command == "probeMedia") return System.Threading.Tasks.Task.FromResult(new WorkerReply { Status = partialCodec, Capabilities = new RecordingEngineCapabilities { Encoders = new[] { new EncoderOption { Codec = "h264_nvenc", Name = "Controlled H.264 capability response" } } } });
                    Require(request.Command == "apply", "Unexpected correction command."); submitted = request.Settings;
                    var corrected = JsonSerializer.Deserialize<WorkerStatus>(JsonSerializer.Serialize(partialCodec, WorkerProtocol.Json), WorkerProtocol.Json);
                    corrected.Config = submitted; corrected.CompletedApply = request.OperationId; corrected.Revision++;
                    corrected.ProfileStatus = new ProfileStatus { ExistingProfile = true, ActivationRequired = true };
                    return System.Threading.Tasks.Task.FromResult(new WorkerReply { Status = corrected });
                }))
                {
                    var panel = new SettingsPanel(client); client.Changed += panel.UpdateState; await panel.ProbeAsync();
                    panel.GetLogicalDescendants().OfType<ComboBox>().Single(c => c.Name == "FramesPerSecond").SelectedItem = 30;
                    Require(panel.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == "ApplySettings").IsEnabled && await panel.ApplyAsync() && submitted.VideoCodec == "hevc_nvenc" && submitted.FramesPerSecond == 30 && client.Paused && client.ProfileStatus.ActivationRequired,
                        "Correcting FPS must retain a valid unavailable saved codec and the activation hold, after an explicit authenticated capability check.");
                }
                var acknowledgements = new List<object>();
                foreach (string failure in new[] { "missing-status", "wrong-operation", "write-error" })
                {
                    var initial = new WorkerStatus {
                        Instance = "desktop-correction-worker", Revision = 8, Config = new Settings { SystemAudio = false, DisplayIndex = 0 },
                        Paused = true, Phase = CapturePhase.Paused,
                        ProfileStatus = new ProfileStatus { ExistingProfile = true, RequiresCorrection = true, ActivationRequired = true, Issues = ["FramesPerSecond: Choose 24, 30, 60, or 120 FPS."] }
                    };
                    bool retry = false; int closes = 0; long revision = 8;
                    using var client = new WorkerRecorder(initial, request =>
                    {
                        if (request.Command == "probeMedia") return System.Threading.Tasks.Task.FromResult(new WorkerReply { Status = initial, Capabilities = new RecordingEngineCapabilities { Encoders = [new EncoderOption { Codec = "h264_nvenc", Name = "Controlled H.264 response" }] } });
                        Require(request.Command == "apply", "Unexpected correction command.");
                        var corrected = new WorkerStatus {
                            Instance = initial.Instance, Revision = ++revision,
                            CompletedApply = !retry && failure == "wrong-operation" ? "different-operation" : request.OperationId,
                            Config = retry ? request.Settings : initial.Config, Paused = true, Phase = CapturePhase.Paused,
                            ProfileStatus = new ProfileStatus { ExistingProfile = true, ActivationRequired = true }
                        };
                        return System.Threading.Tasks.Task.FromResult(new WorkerReply {
                            Status = !retry && failure == "missing-status" ? null : corrected,
                            Error = !retry && failure == "write-error" ? "Controlled profile correction write failed" : null
                        });
                    });
                    var panel = new SettingsPanel(client); client.Changed += panel.UpdateState; panel.CloseRequested = () => closes++;
                    await panel.ProbeAsync();
                    var fps = panel.GetLogicalDescendants().OfType<ComboBox>().Single(c => c.Name == "FramesPerSecond"); fps.SelectedItem = 30;
                    panel.SelectPage(3);
                    var folder = panel.GetLogicalDescendants().OfType<TextBox>().Single(c => c.Name == "OutputDirectory");
                    string draftFolder = Path.Combine(data, "preserved-draft-clips"); folder.Text = draftFolder; panel.SelectPage(0);
                    Require(!await panel.ApplyAsync() && panel.Submitted == null && closes == 0 && !string.IsNullOrWhiteSpace(panel.Error), "Rejected/unconfirmed correction closed the editor or reported submission.");
                    Require(fps.SelectedItem.Equals(30) && folder.Text == draftFolder && client.Config.FramesPerSecond == 60 && !client.Config.SystemAudio && client.Config.DisplayIndex == 0 && client.Paused && client.ProfileStatus.ActivationRequired,
                        "Rejected/unconfirmed correction discarded valid drafts or changed deliberate recording intent.");
                    // Capability admission is deliberately rechecked after the failed change.
                    retry = true; await panel.ProbeAsync();
                    Require(await panel.ApplyAsync() && closes == 1 && panel.Submitted.FramesPerSecond == 30 && panel.Submitted.OutputDirectory == draftFolder && client.Paused && client.ProfileStatus.ActivationRequired,
                        "A fresh acknowledged retry failed, lost the draft, or activated capture.");
                    acknowledgements.Add(new { failure, result = "passed", editorClosedOnlyAfterAcknowledgedRetry = true });
                }
                report["correctionAcknowledgements"] = acknowledgements;
                report["result"] = "passed";
            }
            catch (Exception ex) { report["result"] = "failed"; report["error"] = ex.ToString(); }
            finally { report["cases"] = cases; report["snapshotInput"] = snapshotsPath; File.WriteAllText(Path.Combine(data, "desktop-verification.json"), JsonSerializer.Serialize(report)); done.Cancel(); }
        });
        Dispatcher.UIThread.MainLoop(done.Token);
        return report.TryGetValue("result", out var value) && (string)value == "passed" ? 0 : 1;
    }
    static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
