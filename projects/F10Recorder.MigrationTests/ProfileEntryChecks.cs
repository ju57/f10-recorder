using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using System.Linq;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace F10Recorder;

internal static class ProfileEntryChecks
{
    internal static int Run(string data, bool sensitivityControl = false)
    {
        Directory.CreateDirectory(data); var checks = new List<object>(); int failed = 0;
        AppBuilder.Configure<Application>().UsePlatformDetect().SetupWithoutStarting();
        Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
        Application.Current.Styles.Add(new FluentTheme()); Ui.AddStyles(Application.Current.Styles);
        using var done = new CancellationTokenSource();
        Dispatcher.UIThread.Post(async () =>
        {
            async Task Check(string name, Func<Task> body)
            {
                try { await body(); checks.Add(new { name, result = "passed" }); }
                catch (Exception ex) { failed++; checks.Add(new { name, result = "failed", error = ex.ToString() }); }
            }
            try
            {
                await Check("incompatible-profile-rejects-explicit-engine-check-without-command", async () =>
                {
                    int commands = 0;
                    var status = new WorkerStatus {
                        Instance = "profile-entry-worker", Revision = 8, Config = new Settings { SystemAudio = false, DisplayIndex = 0 },
                        Paused = true, Phase = CapturePhase.Paused,
                        ProfileStatus = new ProfileStatus { Outcome = "incompatible", ExistingProfile = true, CanWrite = false, RequiresCorrection = true, ActivationRequired = true, Issues = ["Use a compatible F10 version."] }
                    };
                    using var recorder = new WorkerRecorder(status, _ => { commands++; return Task.FromResult(new WorkerReply { Status = status, Capabilities = new RecordingEngineCapabilities { Encoders = [] } }); });
                    var panel = new SettingsPanel(recorder); await panel.ProbeAsync();
                    if (commands != 0) throw new InvalidOperationException("An incompatible profile issued an engine command merely to enter or inspect Settings.");
                });
                await Check("new-profile-has-distinct-current-editor-guidance", () =>
                {
                    var status = new WorkerStatus {
                        Instance = "new-profile-worker", Revision = 1, Config = new Settings(), Paused = true, Phase = CapturePhase.Paused,
                        ProfileStatus = new ProfileStatus { Outcome = "new-profile", ActivationRequired = true }
                    };
                    using var recorder = new WorkerRecorder(status, _ => throw new InvalidOperationException("Presentation must not send commands."));
                    var panel = new SettingsPanel(recorder);
                    var heading = panel.GetLogicalDescendants().OfType<TextBlock>().SingleOrDefault(c => c.Name == "ProfileEntryHeading");
                    if (heading?.Text != "New recording preferences") throw new InvalidOperationException("The actual Settings editor does not distinguish a new profile.");
                    return Task.CompletedTask;
                });
                foreach (string name in new[] { "existing", "held-existing", "migration", "new", "correction", "new-correction", "incompatible", "new-incompatible", "interrupted", "unknown", "null-outcome", "null-profile", "null-issues", "contradictory-current", "contradictory-interrupted", "null-config", "invalid-config", "unpaused-hold", "reconnecting" })
                    await Check(name + "-integrated-entry-admission", async () =>
                    {
                        var status = Fixture(name); int commands = 0; bool pausedBefore = status.Paused;
                        using var recorder = new WorkerRecorder(status, request => { commands++; return Task.FromResult(CapabilityReply(status)); });
                        var panel = new SettingsPanel(recorder); recorder.Changed += panel.UpdateState;
                        await panel.EnterAsync(); await panel.EnterAsync(); panel.UpdateState();
                        bool canEdit = name is "existing" or "held-existing" or "migration" or "new" or "correction" or "new-correction";
                        Require(Find<ContentControl>(panel, "ProfileEditor").IsVisible == canEdit, "The actual editor/compatibility destination is wrong.");
                        Require(Find<Border>(panel, "ProfileEntryPresentation").IsVisible == (name != "existing"), "The actual attention presentation is wrong.");
                        var review = Find<Button>(panel, "ReviewProfilePreferences");
                        Require((string)review.Content == "Review preferences" && Avalonia.Automation.AutomationProperties.GetName(review) == "Review recording preferences", "The review action lost its content or accessible name.");
                        Require(commands == (name == "existing" ? 1 : 0), "Entry issued a suppressed probe/mutation or lost the one legacy trusted-editor probe.");
                        var feedback = Find<TextBlock>(panel, "ProfileCompatibility").Text;
                        if (name is "correction" or "new-correction") Require(feedback == "FramesPerSecond: Choose 24, 30, 60, or 120 FPS.", "The supplied correction reason changed.");
                        if (name is "incompatible" or "new-incompatible") Require(feedback == "Use a compatible F10 version.", "The supplied compatibility reason changed.");
                        if (!canEdit)
                        {
                            Require(!feedback.Contains("loaded") && !feedback.Contains("Resume"), "Untrusted feedback asserted success or invited activation.");
                            panel.SelectPage(3); int before = commands;
                            Require(!await panel.ApplyAsync(), "Blocked entry reported a successful Apply.");
                            await panel.ProbeAsync(); await panel.RepairEngineAsync();
                            Find<Button>(panel, "QuickSaveReplay").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            Find<Button>(panel, "QuickScreenshot").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            Find<Button>(panel, "ReviewProfilePreferences").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            Find<ToggleSwitch>(panel, "RecordingEnabled").IsChecked = true;
                            Require(commands == before && panel.Submitted == null && panel.SelectedPage == 3, "An invoked blocked handler issued a command or navigated into the editor.");
                            panel.UpdateState(); Require(recorder.Paused == pausedBefore, "Blocked handlers changed recording intent.");
                        }
                        if (name is "new" or "correction" or "incompatible") RenderAttention(panel, Path.Combine(data, name + ".png"));
                    });
                await Check("poll-reconnect-and-replacement-retain-existing-draft-context", async () =>
                {
                    var initial = Fixture("correction"); int commands = 0;
                    using var recorder = new WorkerRecorder(initial, _ => { commands++; throw new InvalidOperationException("Routing must not send commands."); });
                    var panel = new SettingsPanel(recorder); recorder.Changed += panel.UpdateState; await panel.EnterAsync();
                    var fps = Find<ComboBox>(panel, "FramesPerSecond"); fps.SelectedItem = 30;
                    panel.SelectPage(3); var folder = Find<TextBox>(panel, "OutputDirectory"); folder.Text = Path.Combine(data, "exact-draft-folder");
                    string heading = Find<TextBlock>(panel, "ProfileEntryHeading").Text;
                    var poll = Fixture("correction"); poll.Revision = 9; recorder.Update(poll);
                    var reconnect = Fixture("reconnecting"); reconnect.Revision = 10; recorder.Update(reconnect);
                    Require(Find<TextBlock>(panel, "ProfileEntryHeading").Text == heading && Find<TextBlock>(panel, "ProfileCompatibility").Text.Contains("Recorder unavailable"), "Reconnect reset the known editing context or hid unavailability.");
                    var replacement = Fixture("existing"); replacement.Instance = "replacement-worker"; replacement.Revision = 1;
                    recorder.Update(replacement); recorder.Update(initial); await panel.EnterAsync();
                    Require(commands == 0 && panel.SelectedPage == 3 && fps.SelectedItem.Equals(30) && folder.Text == Path.Combine(data, "exact-draft-folder") && panel.Submitted == null,
                        "Poll/reconnect/replacement reset a draft, navigated, submitted or launched the previously suppressed probe.");
                    Require(!recorder.Config.SystemAudio && recorder.Config.DisplayIndex == 0 && recorder.Config.VideoCodec == "hevc_nvenc" && recorder.Paused,
                        "Routing changed source preferences or deliberate recording pause.");
                    Find<Button>(panel, "ReviewProfilePreferences").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Require(panel.SelectedPage == 0 && fps.SelectedItem.Equals(30) && commands == 0, "Explicit generic Review did not open the existing Capture editor safely.");
                });
                await Check("initial-unavailable-config-hydrates-once-before-editing", async () =>
                {
                    var initial = Fixture("null-config"); int commands = 0;
                    using var recorder = new WorkerRecorder(initial, _ => { commands++; throw new InvalidOperationException("Entry must not send commands." ); });
                    var panel = new SettingsPanel(recorder); recorder.Changed += panel.UpdateState; await panel.EnterAsync();
                    var fresh = Fixture("held-existing"); fresh.Revision = 9; fresh.Config.ReplayMinutes = 17; fresh.Config.FramesPerSecond = 120;
                    recorder.Update(fresh); await panel.EnterAsync();
                    Require(Find<NumericUpDown>(panel, "ReplayMinutes").Value == 17 && Find<ComboBox>(panel, "FramesPerSecond").SelectedItem.Equals(120) && commands == 0,
                        "First credible profile did not replace hidden fallback values, or launched the suppressed probe.");
                });
                await Check("codec-draft-survives-unavailable-check-and-restores-with-fresh-capability", async () =>
                {
                    var current = Fixture("held-existing"); bool available = true; int commands = 0;
                    using var recorder = new WorkerRecorder(current, request =>
                    {
                        commands++; Require(request.Command == "probeMedia", "Unexpected recording mutation.");
                        return Task.FromResult(new WorkerReply { Status = current, Capabilities = new RecordingEngineCapabilities {
                            Encoders = available ? [new EncoderOption { Codec = "hevc_nvenc", Name = "Controlled HEVC" }, new EncoderOption { Codec = "av1_nvenc", Name = "Controlled AV1" }] : [new EncoderOption { Codec = "hevc_nvenc", Name = "Controlled HEVC" }]
                        } });
                    });
                    var panel = new SettingsPanel(recorder); recorder.Changed += panel.UpdateState; await panel.EnterAsync(); await panel.ProbeAsync();
                    var codec = Find<ComboBox>(panel, "VideoCodec"); codec.SelectedItem = codec.Items.Cast<EncoderOption>().Single(c => c.Codec == "av1_nvenc");
                    var reconnect = Fixture("reconnecting"); reconnect.Revision = 9; recorder.Update(reconnect);
                    Require(codec.SelectedItem == null && !Find<Button>(panel, "ApplySettings").IsEnabled, "Reconnect left a stale codec initialized/Apply-ready.");
                    current = Fixture("held-existing"); current.Instance = "new-codec-worker"; current.Revision = 10; recorder.Update(current); available = false;
                    await panel.ProbeAsync();
                    Require(codec.SelectedItem == null && !Find<Button>(panel, "ApplySettings").IsEnabled, "Unavailable draft codec was silently replaced or marked initialized.");
                    available = true; await panel.ProbeAsync();
                    Require((codec.SelectedItem as EncoderOption)?.Codec == "av1_nvenc" && commands == 3 && recorder.Config.VideoCodec == "hevc_nvenc",
                        "A fresh check lost the user's codec draft or silently changed the saved codec.");
                });
                await Check("existing-context-fixture-sensitivity", () =>
                {
                    var initial = Fixture(sensitivityControl ? "new" : "existing");
                    using var recorder = new WorkerRecorder(initial, _ => throw new InvalidOperationException("Presentation must not send commands."));
                    var panel = new SettingsPanel(recorder);
                    Require(!Find<Border>(panel, "ProfileEntryPresentation").IsVisible && Find<TextBlock>(panel, "ProfileEntryHeading").Text == "Your recording preferences",
                        "The literal existing-profile presentation assertion disagreed with the fixture.");
                    return Task.CompletedTask;
                });
            }
            finally
            {
                File.WriteAllText(Path.Combine(data, "verification.json"), JsonSerializer.Serialize(new {
                    result = failed == 0 ? "passed" : "failed", sensitivityControl, checks,
                    effects = "Offscreen controls and controlled worker-client snapshots only; no native Window, worker, engine, GPU, capture, startup write or vendor changes.",
                    visualLimits = "Attention text/border are rendered. Button content/accessibility/click behavior are checked; native button appearance and geometry remain unqualified without an attached visual root."
                }, new JsonSerializerOptions { WriteIndented = true }));
                done.Cancel();
            }
        });
        Dispatcher.UIThread.MainLoop(done.Token);
        return failed == 0 ? 0 : 1;
    }
    static WorkerStatus Fixture(string name)
    {
        var status = new WorkerStatus {
            Instance = "profile-entry-worker", EngineCapabilityContext = "controlled-context", Revision = 8,
            Config = new Settings { SystemAudio = false, DisplayIndex = 0, VideoCodec = "hevc_nvenc" }, Paused = true, Phase = CapturePhase.Paused,
            ProfileStatus = new ProfileStatus { ExistingProfile = true }
        };
        var p = status.ProfileStatus;
        switch (name)
        {
            case "held-existing": p.ActivationRequired = true; break;
            case "migration": p.Outcome = "migration-required"; p.ActivationRequired = true; break;
            case "new": p.Outcome = "new-profile"; p.ExistingProfile = false; p.ActivationRequired = true; break;
            case "new-correction": p.ExistingProfile = false; goto case "correction";
            case "correction": p.Outcome = "correction-required"; p.RequiresCorrection = p.ActivationRequired = true; p.Issues = ["FramesPerSecond: Choose 24, 30, 60, or 120 FPS."]; break;
            case "new-incompatible": p.ExistingProfile = false; goto case "incompatible";
            case "incompatible": p.Outcome = "incompatible"; p.CanWrite = false; p.RequiresCorrection = p.ActivationRequired = true; p.Issues = ["Use a compatible F10 version."]; break;
            case "interrupted": p.Outcome = "interrupted"; p.CanWrite = false; p.RequiresCorrection = p.ActivationRequired = true; p.Issues = ["Stop F10 and review the retained original files."]; break;
            case "unknown": p.Outcome = "unknown-fixture"; break;
            case "null-outcome": p.Outcome = null; break;
            case "null-profile": status.ProfileStatus = null; break;
            case "null-issues": p.Issues = null; break;
            case "contradictory-current": p.ExistingProfile = false; break;
            case "contradictory-interrupted": p.Outcome = "interrupted"; p.ActivationRequired = p.RequiresCorrection = true; p.Issues = ["Retained interrupted fixture."]; break;
            case "null-config": status.Config = null; break;
            case "invalid-config": status.Config.FramesPerSecond = 999; break;
            case "unpaused-hold": p.ActivationRequired = true; status.Paused = false; break;
            case "reconnecting": status.Phase = CapturePhase.Reconnecting; break;
        }
        return status;
    }
    static WorkerReply CapabilityReply(WorkerStatus status) => new() { Status = status, Capabilities = new RecordingEngineCapabilities { Encoders = [new EncoderOption { Codec = "hevc_nvenc", Name = "Controlled HEVC response" }] } };
    static T Find<T>(SettingsPanel panel, string name) where T : Control => panel.GetLogicalDescendants().OfType<T>().Single(c => c.Name == name);
    static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    static void RenderAttention(SettingsPanel panel, string path)
    {
        var card = Find<Border>(panel, "ProfileEntryPresentation");
        card.Measure(new Size(620, 240)); card.Arrange(new Rect(0, 0, 620, card.DesiredSize.Height));
        using var bitmap = new RenderTargetBitmap(new PixelSize(620, Math.Max(100, (int)Math.Ceiling(card.DesiredSize.Height))));
        bitmap.Render(card); bitmap.Save(path, PngBitmapEncoderOptions.Default);
    }
}
