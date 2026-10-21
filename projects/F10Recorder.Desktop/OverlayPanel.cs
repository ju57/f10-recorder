using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace F10Recorder;

// Quick actions are separate from Settings so opening the overlay never commits edits.
internal sealed class OverlayPanel : UserControl
{
    readonly IWindowsRecorder recorder;
    readonly TextBlock state = Ui.Label("", 19, null, FontWeight.SemiBold);
    readonly TextBlock detail = Ui.Label("", 13, Ui.Muted), saveHint = Ui.Label("", 12, Ui.Muted);
    readonly Button save, pause, screenshot;
    internal Button SaveAction => save;
    internal Button ScreenshotAction => screenshot;
    internal void FocusInitial() => (save.IsEnabled ? save : screenshot).Focus();

    internal OverlayPanel(IWindowsRecorder session, Action dismiss, Action openSettings)
    {
        recorder = session; Background = Ui.Background;
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(28, 26) };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 28) };
        var brand = new StackPanel { Spacing = 4 };
        brand.Children.Add(Ui.Label("F10 RECORDER", 12, Ui.Accent, FontWeight.SemiBold));
        brand.Children.Add(Ui.Label("Replay", 28, null, FontWeight.SemiBold)); header.Children.Add(brand);
        var close = Ui.Button("Esc", dismiss); close.Name = "CloseOverlay"; close.VerticalAlignment = VerticalAlignment.Top;
        AutomationProperties.SetName(close, "Close overlay"); ToolTip.SetTip(close, "Return to your game");
        Grid.SetColumn(close, 1); header.Children.Add(close); root.Children.Add(header);

        var body = new StackPanel { Spacing = 12 };
        var status = new StackPanel { Spacing = 8 }; status.Children.Add(state); status.Children.Add(detail);
        body.Children.Add(new Border { Background = Ui.Surface, BorderBrush = Ui.Border, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(20), Child = status, Margin = new Thickness(0, 0, 0, 12) });
        save = Action("Save replay", saveHint, recorder.SaveReplay, true); save.Name = "OverlaySaveReplay"; body.Children.Add(save);
        screenshot = Action("Screenshot", Ui.Label("Save a PNG of the capture source", 12, Ui.Muted), recorder.TakeScreenshot); screenshot.Name = "OverlayScreenshot"; body.Children.Add(screenshot);
        pause = Action("Pause replay", Ui.Label("Saved clips stay in place", 12, Ui.Muted), () => recorder.Pause(!recorder.Paused)); pause.Name = "OverlayPause"; body.Children.Add(pause);
        var settings = Action("Recording settings", Ui.Label("Capture, audio, shortcuts and storage", 12, Ui.Muted), openSettings); settings.Name = "OverlaySettings"; body.Children.Add(settings);
        root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, [Grid.RowProperty] = 1 });

        var footer = new StackPanel { Spacing = 8, Margin = new Thickness(0, 24, 0, 0) };
        footer.Children.Add(new Border { Height = 1, Background = Ui.Border, Margin = new Thickness(0, 0, 0, 8) });
        footer.Children.Add(Ui.Label("Opening replay controls does not pause an active buffer.", 12, Ui.Muted));
        footer.Children.Add(Ui.Label("Escape or click outside to return to your game.", 12, Ui.Muted));
        Grid.SetRow(footer, 2); root.Children.Add(footer); Content = root;
        UpdateState();
    }

    static Button Action(string title, TextBlock description, System.Action callback, bool primary = false)
    {
        var content = new StackPanel { Spacing = 5 };
        content.Children.Add(Ui.Label(title, 15, primary ? Ui.Background : Ui.Text, FontWeight.SemiBold));
        if (primary) description.Foreground = Ui.Background;
        content.Children.Add(description);
        var button = Ui.Button("", callback, primary); button.Content = content;
        button.HorizontalAlignment = HorizontalAlignment.Stretch; button.HorizontalContentAlignment = HorizontalAlignment.Left;
        button.Padding = new Thickness(20, 16); button.MinHeight = 76;
        AutomationProperties.SetName(button, title); return button;
    }

    internal void UpdateState()
    {
        var status = ReplayPresentation.Describe(recorder.Phase, recorder.State, recorder.Paused, recorder.Ready, recorder.CanSave, recorder.Applying, recorder.Saving, recorder.SaveRegistered, recorder.Metrics?.BufferedSeconds ?? 0);
        state.Text = status.Heading; state.Foreground = status.Healthy && recorder.SaveRegistered ? Ui.Accent : Ui.Warning;
        detail.Text = status.Detail;
        saveHint.Text = recorder.Config.HotkeyText + " · MP4 video";
        save.IsEnabled = status.CanSave;
        foreach (var label in ((StackPanel)save.Content).Children.OfType<TextBlock>()) label.Foreground = save.IsEnabled ? Ui.Background : Ui.Muted;
        screenshot.IsEnabled = !recorder.Applying && !recorder.ScreenshotInProgress;
        pause.IsEnabled = !recorder.Applying;
        var title = ((StackPanel)pause.Content).Children.OfType<TextBlock>().First();
        title.Text = recorder.Paused ? "Resume replay" : "Pause replay";
        AutomationProperties.SetName(pause, title.Text);
    }
}
