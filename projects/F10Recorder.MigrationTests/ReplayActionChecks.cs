using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace F10Recorder;

// Actual Settings action and worker-client event policy, controlled transport.
// No native Window, worker, capture, engine, shortcut or preference writes.
internal static class ReplayActionChecks
{
    internal static int Run(string root)
    {
        if (Directory.Exists(root)) throw new IOException("Fresh replay action root required.");
        Directory.CreateDirectory(root);
        AppBuilder.Configure<Application>().UsePlatformDetect().SetupWithoutStarting();
        Application.Current.Styles.Add(new FluentTheme()); Ui.AddStyles(Application.Current.Styles);
        using var done = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        bool passed = false; string error = null; var checks = new List<string>();
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                long sequence = 0; int commands = 0, successful = 0, failed = 0;
                var notices = new List<(string Title, string Message, NotificationKind Kind)>();
                WorkerReply response = null;
                using var client = new WorkerRecorder(Status(false), request =>
                {
                    Require(request.Command == "save", "Unexpected command from actual desktop action."); commands++;
                    return Task.FromResult(response ?? throw new IOException("Unplanned desktop command."));
                }, root);
                client.Notification += (title, message, kind, _) => notices.Add((title, message, kind));
                client.Saved += (_, success) => { if (success) successful++; else failed++; };
                var panel = new SettingsPanel(client); client.Changed += panel.UpdateState; panel.UpdateState();
                var save = panel.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == "QuickSaveReplay");
                Require(save.IsEnabled, "Ready desktop action was unavailable.");

                var accepted = Status(true); accepted.Metrics.ReplaySaveState = "Accepted";
                accepted.Events = [Event("notification", "Saving the available completed footage.", "Saving replay…")];
                response = new WorkerReply { ReplayAccepted = true, Status = accepted };
                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(commands == 1 && client.Saving && !save.IsEnabled && successful == 0 && failed == 0
                    && notices.Count == 1 && notices[0].Title == "Saving replay…" && notices.All(n => n.Kind != NotificationKind.Success),
                    "Admission was displayed as publication or left Save active.");
                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(commands == 1, "Busy action submitted a second save.");
                checks.Add("actual-settings-action-admission-is-not-publication-and-blocks-busy-click");

                string path = Path.Combine(root, "controlled-final.mp4");
                var published = Status(false); published.Metrics.ReplaySaveState = "Saved";
                published.Metrics.LastSaveSucceeded = true; published.Metrics.LastSaveValidation = "FullDecode";
                published.Events = [Event("notification", "Saved 2.5 s of 300 s. Earlier history unavailable.", "Replay saved", true),
                    Event("saved", path, success: true)];
                client.Update(published);
                Require(successful == 1 && failed == 0 && !client.Saving && save.IsEnabled
                    && notices.Last().Kind == NotificationKind.Success && notices.Last().Message.Contains("Earlier history unavailable"),
                    "Validated publication did not reach the actual desktop client/action.");
                checks.Add("validated-publication-restores-action-and-discloses-warmup");

                response = new WorkerReply { ReplayAccepted = false, ReplayRejection = "Controlled storage rejection.", Status = Status(false) };
                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(commands == 2 && successful == 1 && failed == 0 && save.IsEnabled
                    && notices.Last().Kind == NotificationKind.Warning && notices.Last().Message.Contains("Controlled storage rejection"),
                    "Rejected request became a desktop success.");
                checks.Add("explicit-worker-rejection-is-visible-without-success");

                response = new WorkerReply { ReplayAccepted = true, Status = Status(true) };
                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var rejectedFile = Status(false); rejectedFile.Metrics.ReplaySaveState = "Failed";
                rejectedFile.Events = [Event("notification", "Controlled full decode failure.", "Replay not saved"),
                    Event("saved", "Controlled full decode failure.")];
                client.Update(rejectedFile);
                Require(commands == 3 && successful == 1 && failed == 1 && save.IsEnabled
                    && notices.Last().Kind == NotificationKind.Warning && notices.Last().Title == "Replay not saved",
                    "Validation failure became a success event or left the action stuck.");
                checks.Add("accepted-then-validation-failure-is-not-published-success");
                passed = true;

                WorkerStatus Status(bool saving) => new() { Instance = "replay-action-controlled-worker", Revision = ++sequence,
                    Config = new Settings { CaptureMode = "window" }, ProfileStatus = new ProfileStatus { ExistingProfile = true },
                    Phase = CapturePhase.Buffering, Ready = true, CanSave = true, SaveRegistered = true, Saving = saving,
                    Metrics = new RecorderMetrics { BufferedSeconds = 2.5 } };
                WorkerEvent Event(string kind, string message, string title = null, bool success = false) => new() {
                    Sequence = ++sequence, Kind = kind, Message = message, Title = title, Success = success,
                    NotificationKind = (int)(success ? NotificationKind.Success : kind == "notification" && title == "Saving replay…" ? NotificationKind.Info : NotificationKind.Warning) };
            }
            catch (Exception failure) { error = failure.ToString(); }
            finally { done.Cancel(); }
        });
        Dispatcher.UIThread.MainLoop(done.Token);
        File.WriteAllText(Path.Combine(root, "verification.json"), JsonSerializer.Serialize(new { result = passed ? "passed" : "failed", error, checks,
            effects = "Actual offscreen Settings button and WorkerRecorder with controlled transport/events; no Window, worker, engine, capture, native input, startup/preferences or vendor changes.",
            limits = "UI event policy only. Media validity and real worker publication are verified separately." }));
        return passed ? 0 : 1;
    }
    static void Require(bool value, string reason) { if (!value) throw new IOException(reason); }
}
