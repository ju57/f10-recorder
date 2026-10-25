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
                Require(snapshots.Length == 8, "Expected eight real worker outcomes.");
                foreach (var status in snapshots)
                {
                    using var client = new WorkerRecorder(status, _ => throw new Exception("Offscreen migration checks must not issue engine/capture commands."));
                    var panel = new SettingsPanel(client); client.Changed += panel.UpdateState;
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
