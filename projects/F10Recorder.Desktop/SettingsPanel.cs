using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace F10Recorder;

internal sealed class SettingsPanel : UserControl
{
    readonly IWindowsRecorder recorder;
    readonly NumericUpDown minutes = Number("ReplayMinutes", 1, 30), bitrate = Number("BitrateMbps", 5, 100), display = Number("DisplayIndex", -1, 31);
    readonly ComboBox fps = new() { Name = "FramesPerSecond", ItemsSource = Settings.SupportedFrameRates }, codec = new() { Name = "VideoCodec", MinWidth = 260 };
    readonly ComboBox captureMode = new() { Name = "CaptureMode", ItemsSource = new[] { "Monitor", "Application window · experimental" } }, window = new() { Name = "CaptureWindow", HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 40, PlaceholderText = "Refresh windows to choose an application" };
    readonly StackPanel windowSelection = new() { Spacing = 8 };
    readonly TextBlock resolution = Ui.Label("", 13, Ui.Muted), targetPolicy = Ui.Label("", 12, Ui.Muted), screenshotPolicy = Ui.Label("", 12, Ui.Muted);
    readonly Button quickSave, quickScreenshot;
    string selectedExecutable, selectedClass;
    readonly CheckBox audio = new() { Name = "SystemAudio", Content = "Capture system audio" }, startup = new() { Name = "StartWithWindows", Content = "Start buffering at Windows sign-in" };
    readonly TextBox clips = new() { Name = "OutputDirectory" }, screenshots = new() { Name = "ScreenshotDirectory" };
    readonly ShortcutEditor saveShortcut = new("Save", false), overlayShortcut = new("Overlay", true), screenshotShortcut = new("Screenshot", true);
    readonly ToggleSwitch recording = new() { Name = "RecordingEnabled", OnContent = "On", OffContent = "Off" };
    readonly TextBlock state = Ui.Label("Paused", 15, Ui.Warning, FontWeight.SemiBold), stateDetail = Ui.Label("", 12, Ui.Muted), estimate = Ui.Label("", 12, Ui.Muted), error = Ui.Label("", 13, Ui.Warning), codecStatus = Ui.Label("Checking your GPU…", 12, Ui.Muted);
    readonly TextBlock profileStatus = Ui.Label("", 13, Ui.Warning);
    readonly TextBlock audioFormat = Ui.Label("", 13, Ui.Muted), playbackDevice = Ui.Label("Windows default · automatic", 13, Ui.Muted);
    readonly Button apply;
    readonly Button repairEngine = Ui.Button("Repair recording engine"), recheckEngine = Ui.Button("Check engine again");
    readonly TextBlock engineStatus = Ui.Label("Checking recording engine…", 12, Ui.Muted);
    readonly ContentControl page = new();
    readonly List<StackPanel> pages = [];
    readonly List<Button> navigation = [];
    bool updating, loaded, probing = true;
    string capabilityContext;
    bool probeInvalidated;
    public Action CloseRequested;
    internal Settings Submitted { get; private set; }
    internal string Error => error.Text;
    internal bool CodecCheckFinished => !probing;
    internal string EngineStatus => engineStatus.Text;
    internal int SelectedPage { get; private set; }

    public SettingsPanel(IWindowsRecorder session)
    {
        Background = Ui.Background;
        recorder = session; var config = session.Config;
        minutes.Value = config.ReplayMinutes; bitrate.Value = config.BitrateMbps; display.Value = config.DisplayIndex; fps.SelectedItem = config.FramesPerSecond;
        selectedExecutable = config.WindowExecutable; selectedClass = config.WindowClass; captureMode.SelectedIndex = config.CaptureMode == "window" ? 1 : 0;
        audio.IsChecked = config.SystemAudio; startup.IsChecked = WindowsStartup.Enabled; clips.Text = config.OutputDirectory; screenshots.Text = config.ScreenshotDirectory;
        saveShortcut.Load(config.SaveKey, config.SaveModifiers); overlayShortcut.Load(config.OverlayKey, config.OverlayModifiers); screenshotShortcut.Load(config.ScreenshotKey, config.ScreenshotModifiers);
        estimate.Name = "RollingCacheEstimate"; audioFormat.Name = "AudioFormat"; playbackDevice.Name = "PlaybackDevicePolicy";
        ToolTip.SetTip(estimate, "Estimate from the selected duration and bitrates, including 10 seconds of cache headroom. Actual disk usage varies.");
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), Margin = new Thickness(28, 24) };
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 22) };
        var title = new StackPanel { Spacing = 5 }; title.Children.Add(Ui.Label("F10 Recorder", 27, null, FontWeight.SemiBold)); title.Children.Add(Ui.Label("Keep the moment. Save it when it matters.", 13, Ui.Muted)); heading.Children.Add(title);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        quickScreenshot = Ui.Button("Screenshot", recorder.TakeScreenshot); quickScreenshot.Name = "QuickScreenshot"; actions.Children.Add(quickScreenshot);
        quickSave = Ui.Button("Save replay", recorder.SaveReplay, true); quickSave.Name = "QuickSaveReplay"; actions.Children.Add(quickSave); Grid.SetColumn(actions, 1); heading.Children.Add(actions); root.Children.Add(heading);
        var header = new StackPanel { Spacing = 18 };
        var statusGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var statusText = new StackPanel { Spacing = 5 }; statusText.Children.Add(state); statusText.Children.Add(stateDetail); statusGrid.Children.Add(statusText);
        recording.VerticalAlignment = VerticalAlignment.Center; AutomationProperties.SetName(recording, "Replay recording"); Grid.SetColumn(recording, 1); statusGrid.Children.Add(recording);
        header.Children.Add(new Border { Background = Ui.Surface, BorderBrush = Ui.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(18, 16), Child = statusGrid });
        profileStatus.Name = "ProfileCompatibility"; AutomationProperties.SetName(profileStatus, "Recording profile compatibility"); header.Children.Add(profileStatus);
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 0, 0, 8) };
        foreach (string name in new[] { "Capture", "Audio", "Controls", "Storage" })
        {
            int index = navigation.Count; var button = Ui.Button(name, () => SelectPage(index)); button.Name = name + "Tab";
            button.MinHeight = 42; button.Padding = new Thickness(18, 10); button.BorderThickness = new Thickness(1);
            AutomationProperties.SetName(button, name + " settings"); navigation.Add(button); tabs.Children.Add(button);
        }
        header.Children.Add(tabs); Grid.SetRow(header, 1); root.Children.Add(header);
        pages.Add(CapturePage()); pages.Add(AudioPage()); pages.Add(ControlsPage()); pages.Add(StoragePage());
        var scroll = new ScrollViewer { Content = page, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 2); root.Children.Add(scroll);
        var footer = new StackPanel { Spacing = 10, Margin = new Thickness(0, 14, 0, 0) }; footer.Children.Add(error);
        var footerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8 };
        var info = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center }; info.Children.Add(estimate);
        var logs = Ui.Button("Diagnostic logs", () => { try { RecorderHost.OpenFolder(recorder.DataDirectory); } catch (Exception ex) { error.Text = ex.Message; } }); logs.Padding = new Thickness(0); logs.Background = Brushes.Transparent; logs.Foreground = Ui.Muted; info.Children.Add(logs); footerGrid.Children.Add(info);
        var close = Ui.Button("Close", () => CloseRequested?.Invoke()); Grid.SetColumn(close, 1); footerGrid.Children.Add(close);
        apply = Ui.Button("Apply changes", primary: true); apply.Name = "ApplySettings"; apply.Click += async (_, _) => await ApplyAsync(); Grid.SetColumn(apply, 2); footerGrid.Children.Add(apply);
        repairEngine.Name = "RepairRecordingEngine"; recheckEngine.Name = "RecheckRecordingEngine"; engineStatus.Name = "RecordingEngineStatus";
        repairEngine.Click += async (_, _) => await RepairEngineAsync(); recheckEngine.Click += async (_, _) => await ProbeAsync();
        footer.Children.Add(footerGrid); Grid.SetRow(footer, 3); root.Children.Add(footer); Content = root;
        recording.PropertyChanged += (_, e) => { if (!updating && e.Property == ToggleButton.IsCheckedProperty) recorder.Pause(recording.IsChecked != true); };
        minutes.PropertyChanged += (_, e) => { if (e.Property == NumericUpDown.ValueProperty) Estimate(); }; bitrate.PropertyChanged += (_, e) => { if (e.Property == NumericUpDown.ValueProperty) Estimate(); };
        audio.PropertyChanged += (_, e) => { if (e.Property == ToggleButton.IsCheckedProperty) UpdateAudioSummary(); };
        captureMode.SelectionChanged += (_, _) => UpdateSourceSummary();
        window.SelectionChanged += (_, _) => { if (window.SelectedItem is WindowCaptureChoice choice) { selectedExecutable = choice.Executable; selectedClass = choice.ClassName; UpdateSourceSummary(); } };
        UpdateSourceSummary();
        SelectPage(0); UpdateAudioSummary(); UpdateState(); AttachedToVisualTree += async (_, _) => { if (!loaded) { loaded = true; await ProbeAsync(); } };
    }
    static NumericUpDown Number(string name, decimal minimum, decimal maximum) => new() { Name = name, Minimum = minimum, Maximum = maximum, Increment = 1, FormatString = "0", HorizontalAlignment = HorizontalAlignment.Stretch };
    static StackPanel Page(string title, string description)
    {
        var body = new StackPanel { Spacing = 0, Margin = new Thickness(0, 8, 12, 8) }; body.Children.Add(Ui.Label(title, 21, null, FontWeight.SemiBold));
        var hint = Ui.Label(description, 13, Ui.Muted); hint.Margin = new Thickness(0, 7, 0, 16); body.Children.Add(hint); return body;
    }
    static void Row(StackPanel body, string title, string description, Control control)
    {
        AutomationProperties.SetName(control, title);
        AutomationProperties.SetHelpText(control, description);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,280"), ColumnSpacing = 20, Margin = new Thickness(0, 14) };
        var labels = new StackPanel { Spacing = 5, VerticalAlignment = VerticalAlignment.Center }; labels.Children.Add(Ui.Label(title, 14, null, FontWeight.SemiBold)); labels.Children.Add(Ui.Label(description, 12, Ui.Muted)); grid.Children.Add(labels);
        control.VerticalAlignment = VerticalAlignment.Center; control.HorizontalAlignment = HorizontalAlignment.Stretch; Grid.SetColumn(control, 1); grid.Children.Add(control);
        body.Children.Add(new Border { BorderBrush = Ui.Border, BorderThickness = new Thickness(0, 0, 0, 1), Child = grid });
    }
    StackPanel CapturePage()
    {
        var body = Page("Capture", "Set the length and quality of your next replays. Applying changes starts a new buffer.");
        Row(body, "Video source", "Record a display or one application window.", captureMode);
        windowSelection.Children.Add(window);
        var refresh = Ui.Button("Refresh windows", RefreshWindows); refresh.HorizontalAlignment = HorizontalAlignment.Left; windowSelection.Children.Add(refresh);
        windowSelection.Children.Add(targetPolicy); body.Children.Add(windowSelection);
        body.Children.Add(screenshotPolicy);
        Row(body, "Replay length", "Last 1–30 minutes; the cache uses disk storage.", minutes);
        Row(body, "Video codec", "Only encoders that initialize on your GPU are offered.", codec); body.Children.Add(codecStatus);
        Row(body, "Output frame rate", "Configured FPS for saved video, not a live capture measurement.", fps);
        Row(body, "Video bitrate", "5–100 Mbps. Higher values need more cache space.", bitrate);
        Row(body, "Capture display", "−1 selects the primary display; 0–31 are adapter output indices.", display);
        resolution.Name = "NativeResolutionPolicy";
        Row(body, "Resolution", "Automatic for the selected capture source.", resolution); return body;
    }
    StackPanel AudioPage()
    {
        var body = Page("Audio", "Include the sound playing on your PC. Apply changes to update recording.");
        Row(body, "System audio", "Includes continuous silence when nothing is playing.", audio);
        Row(body, "Playback device", "Automatically follows Windows' default output; choose that device in Windows.", playbackDevice);
        Row(body, "Saved audio", "Fixed output format when system audio is enabled. Microphone capture is not available.", audioFormat); return body;
    }
    StackPanel ControlsPage()
    {
        var body = Page("Controls", "Global shortcuts remain available while another application has focus.");
        Row(body, "Save replay", "Save the buffered footage as an MP4.", saveShortcut);
        Row(body, "Open overlay", "Open replay controls over desktop or borderless fullscreen apps.", overlayShortcut);
        Row(body, "Take screenshot", "Save a PNG of the applied capture source. Window mode needs a visible, unambiguous target.", screenshotShortcut);
        body.Children.Add(startup); startup.Margin = new Thickness(0, 24, 0, 0); return body;
    }
    StackPanel StoragePage()
    {
        var body = Page("Storage", "Saved clips and screenshots remain separate from the rolling cache.");
        body.Children.Add(Ui.Label("Clips", 15, null, FontWeight.SemiBold)); body.Children.Add(Ui.Label("MP4 folders follow the selected application in window mode, or the foreground application in monitor mode.", 12, Ui.Muted)); body.Children.Add(Folder(clips));
        var label = Ui.Label("Screenshots", 15, null, FontWeight.SemiBold); label.Margin = new Thickness(0, 24, 0, 0); body.Children.Add(label); body.Children.Add(Ui.Label("PNG files use a separately configured location.", 12, Ui.Muted)); body.Children.Add(Folder(screenshots));
        var engine = Ui.Label("Recording engine", 15, null, FontWeight.SemiBold); engine.Margin = new Thickness(0, 26, 0, 6); body.Children.Add(engine);
        body.Children.Add(Ui.Label("F10 uses only its approved, verified recording engine. Repair replaces missing or rejected engine files and preserves your preferences and saved media. An internet connection may be needed.", 12, Ui.Muted));
        body.Children.Add(engineStatus);
        var engineActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 12, 0, 0) };
        engineActions.Children.Add(recheckEngine); engineActions.Children.Add(repairEngine); body.Children.Add(engineActions); return body;
    }
    Control Folder(TextBox text)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8, Margin = new Thickness(0, 12, 0, 0) }; grid.Children.Add(text);
        var browse = Ui.Button("Browse…"); Grid.SetColumn(browse, 1); grid.Children.Add(browse);
        browse.Click += async (_, _) =>
        {
            try
            {
                var provider = TopLevel.GetTopLevel(this).StorageProvider;
                var selected = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose save folder", AllowMultiple = false, SuggestedStartLocation = await provider.TryGetFolderFromPathAsync(text.Text) });
                if (selected.Count > 0) text.Text = selected[0].TryGetLocalPath() ?? throw new IOException("Choose a local filesystem folder.");
            }
            catch (Exception ex) { error.Text = ex.Message; }
        }; return grid;
    }
    public void SelectPage(int index)
    {
        SelectedPage = index; page.Content = pages[index];
        for (int i = 0; i < navigation.Count; i++)
        {
            bool selected = i == index; var button = navigation[i];
            button.Background = selected ? Ui.Input : Brushes.Transparent; button.Foreground = selected ? Ui.Text : Ui.Muted;
            button.BorderBrush = selected ? Ui.Accent : Brushes.Transparent; button.FontWeight = selected ? FontWeight.SemiBold : FontWeight.Normal;
            AutomationProperties.SetHelpText(button, selected ? "Current settings section." : "Open this settings section.");
        }
    }
    internal async Task ProbeAsync()
    {
        string selected = (codec.SelectedItem as EncoderOption)?.Codec ?? recorder.Config.VideoCodec;
        probing = true; codec.ItemsSource = Array.Empty<EncoderOption>(); codec.SelectedItem = null;
        capabilityContext = null; probeInvalidated = false;
        string checkingContext = recorder.EngineCapabilityContext;
        codecStatus.Text = engineStatus.Text = "Checking approved engine and GPU initialization…"; UpdateState();
        try
        {
            var receipt = await recorder.ProbeMediaAsync(); var choices = receipt.Encoders;
            UpdateState();
            if (probeInvalidated || checkingContext != recorder.EngineCapabilityContext) throw new IOException("Recording engine state changed during the check. Check again.");
            capabilityContext = recorder.EngineCapabilityContext;
            var savedChoice = choices.FirstOrDefault(c => c.Codec == selected);
            bool retainUnavailable = savedChoice == null && choices.Length > 0 && recorder.ProfileStatus.ActivationRequired && recorder.ProfileStatus.RequiresCorrection && recorder.ProfileStatus.CanWrite
                && !recorder.ProfileStatus.Issues.Any(i => i.StartsWith("VideoCodec:", StringComparison.Ordinal));
            if (retainUnavailable) savedChoice = new EncoderOption { Codec = selected, Name = selected.Replace("_nvenc", "").ToUpperInvariant() + " · saved preference, unavailable" };
            codec.ItemsSource = retainUnavailable ? choices.Append(savedChoice).ToArray() : choices; codec.SelectedItem = savedChoice;
            codecStatus.Text = choices.Length > 0 ? "Initialized: " + string.Join(" / ", choices.Select(c => c.Codec.Replace("_nvenc", "").ToUpperInvariant())) + ". Generated 256×256 frame at 30 FPS; other capture settings require their own checks." : "No GPU encoder initialized. Check the NVIDIA driver, then check again.";
            if (retainUnavailable) codecStatus.Text += " Corrections keep your unavailable saved codec; replay remains paused until you explicitly resume.";
            else if (choices.Length > 0 && codec.SelectedItem == null) codecStatus.Text += " Your saved " + selected.Replace("_nvenc", "").ToUpperInvariant() + " choice is unavailable. Check again or explicitly choose another codec.";
            engineStatus.Text = "Approved engine " + receipt.PackageVersion + " verified at " + receipt.CheckedUtc + ". " + codecStatus.Text;
        }
        catch (Exception ex) { codec.ItemsSource = Array.Empty<EncoderOption>(); codec.SelectedItem = null; codecStatus.Text = engineStatus.Text = ex.Message; }
        finally { probing = false; UpdateState(); }
    }
    internal async Task RepairEngineAsync()
    {
        if (probing || recorder.Applying) return;
        probing = true; codec.ItemsSource = Array.Empty<EncoderOption>(); codec.SelectedItem = null;
        codecStatus.Text = engineStatus.Text = "Repairing recording engine…"; error.Text = ""; UpdateState();
        try { await recorder.RepairMediaAsync(); await ProbeAsync(); }
        catch (Exception ex) { error.Text = codecStatus.Text = engineStatus.Text = ex.Message; }
        finally { probing = false; UpdateState(); }
    }
    public void UpdateState()
    {
        if (probing && (recorder.Applying || recorder.Phase is CapturePhase.Recovering or CapturePhase.Reconnecting or CapturePhase.Unknown)) probeInvalidated = true;
        if (!probing && capabilityContext != null && (capabilityContext != recorder.EngineCapabilityContext || recorder.Applying || recorder.Phase is CapturePhase.Recovering or CapturePhase.Reconnecting or CapturePhase.Unknown))
        {
            capabilityContext = null; codec.ItemsSource = Array.Empty<EncoderOption>(); codec.SelectedItem = null;
            codecStatus.Text = engineStatus.Text = "Recording engine state changed. Check again before choosing a codec.";
        }
        updating = true; recording.IsChecked = !recorder.Paused; updating = false;
        var profile = recorder.ProfileStatus;
        profileStatus.Text = profile.Message; profileStatus.IsVisible = profile.ActivationRequired || profile.RequiresCorrection;
        var status = ReplayPresentation.Describe(recorder.Phase, recorder.State, recorder.Paused, recorder.Ready, recorder.CanSave, recorder.Applying, recorder.Saving, recorder.SaveRegistered, recorder.Metrics?.BufferedSeconds ?? 0);
        state.Text = status.Heading; state.Foreground = status.Healthy && recorder.SaveRegistered ? Ui.Accent : Ui.Warning; stateDetail.Text = status.Detail;
        quickSave.IsEnabled = status.CanSave; quickScreenshot.IsEnabled = !recorder.Applying && !recorder.ScreenshotInProgress && !profile.ActivationRequired;
        apply.IsEnabled = !probing && codec.SelectedItem != null && !recorder.Applying && profile.CanWrite;
        codec.IsEnabled = !probing && !recorder.Applying;
        recheckEngine.IsEnabled = repairEngine.IsEnabled = !probing && !recorder.Applying;
        repairEngine.Content = recorder.Applying ? "Working…" : "Repair recording engine";
        apply.Content = recorder.Applying ? "Applying…" : "Apply changes"; recording.IsEnabled = !recorder.Applying && recorder.Phase != CapturePhase.Reconnecting && recorder.Phase != CapturePhase.Unknown && profile.CanWrite && !profile.RequiresCorrection;
    }
    void RefreshWindows()
    {
        try
        {
            var choices = WindowCaptureTarget.List(); window.ItemsSource = choices;
            window.SelectedItem = choices.FirstOrDefault(c => string.Equals(c.Executable, selectedExecutable, StringComparison.OrdinalIgnoreCase) && c.ClassName == selectedClass);
            UpdateSourceSummary();
        }
        catch (Exception ex) { error.Text = "Windows could not be listed: " + ex.Message; }
    }
    void UpdateSourceSummary()
    {
        bool selectedWindow = captureMode.SelectedIndex == 1;
        display.IsEnabled = !selectedWindow; windowSelection.IsVisible = selectedWindow;
        resolution.Text = selectedWindow ? "Initial client area · fit on resize" : "Native · no resizing";
        targetPolicy.Text = string.IsNullOrEmpty(selectedExecutable) ? "Select a window, then Apply. If it is unavailable, replay waits."
            : "Selected: " + Path.GetFileNameWithoutExtension(selectedExecutable) + ". Reconnects only when one matching application window is open. A restored window starts a fresh buffer.";
        screenshotPolicy.Text = selectedWindow ? "Experimental window capture. System audio includes all PC sound; check audio sync before relying on clips."
            : "Monitor capture can interrupt NVIDIA Instant Replay when F10 controls are shown on this PC.";
    }
    void UpdateAudioSummary()
    {
        bool enabled = audio.IsChecked == true;
        audioFormat.Text = enabled ? "AAC · stereo · 48 kHz · 192 kbps" : "Off · no audio track";
        playbackDevice.Text = enabled ? "Windows default · automatic" : "Not used · system audio is off";
        Estimate();
    }
    void Estimate()
    {
        decimal audioMbps = audio.IsChecked == true ? 0.192m : 0;
        decimal megabytes = ((minutes.Value ?? 5) * 60 + 10) * ((bitrate.Value ?? 20) + audioMbps) / 8;
        estimate.Text = "Estimated cache ≈ " + megabytes.ToString("0") + " MB";
    }
    internal async Task<bool> ApplyAsync()
    {
        try
        {
            if (probing || codec.SelectedItem is not EncoderOption chosen) throw new InvalidOperationException("Wait for an available GPU encoder.");
            var next = new Settings { CaptureMode = captureMode.SelectedIndex == 1 ? "window" : "monitor", WindowExecutable = selectedExecutable, WindowClass = selectedClass, ReplayMinutes = (int)(minutes.Value ?? 5), BitrateMbps = (int)(bitrate.Value ?? 20), DisplayIndex = (int)(display.Value ?? -1), FramesPerSecond = (int)fps.SelectedItem, VideoCodec = chosen.Codec, SystemAudio = audio.IsChecked == true, OutputDirectory = clips.Text, ScreenshotDirectory = screenshots.Text, SaveKey = saveShortcut.Key, SaveModifiers = saveShortcut.Modifiers, OverlayKey = overlayShortcut.Key, OverlayModifiers = overlayShortcut.Modifiers, ScreenshotKey = screenshotShortcut.Key, ScreenshotModifiers = screenshotShortcut.Modifiers };
            next.Validate(); error.Text = ""; CaptureVisibility.Configure(null); await recorder.ApplyAsync(next, startup.IsChecked == true); CaptureVisibility.Configure(recorder.Config, recorder.CaptureModeKnown); Submitted = next; CloseRequested?.Invoke(); return true;
        }
        catch (Exception ex) { CaptureVisibility.Configure(recorder.Config, recorder.CaptureModeKnown); error.Text = ex.Message; return false; }
    }
}

internal sealed class ShortcutEditor : StackPanel
{
    readonly ComboBox key;
    readonly CheckBox ctrl, alt, shift;
    readonly bool letters;
    public int Key => key.SelectedIndex < 12 ? 112 + key.SelectedIndex : 65 + key.SelectedIndex - 12;
    public int Modifiers => (ctrl.IsChecked == true ? 2 : 0) | (alt.IsChecked == true ? 1 : 0) | (shift.IsChecked == true ? 4 : 0);
    public ShortcutEditor(string name, bool allowLetters)
    {
        letters = allowLetters; Spacing = 7;
        key = new ComboBox { Name = name + "Key", HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = Enumerable.Range(1, 12).Select(i => "F" + i).Concat(allowLetters ? Enumerable.Range(65, 26).Select(i => ((char)i).ToString()) : [] ).ToArray() }; Children.Add(key);
        var modifiers = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        ctrl = new CheckBox { Content = "Ctrl", Name = name + "Ctrl" }; alt = new CheckBox { Content = "Alt", Name = name + "Alt" }; shift = new CheckBox { Content = "Shift", Name = name + "Shift" };
        modifiers.Children.Add(ctrl); modifiers.Children.Add(alt); modifiers.Children.Add(shift); Children.Add(modifiers);
    }
    public void Load(int virtualKey, int modifiers)
    {
        key.SelectedIndex = virtualKey >= 112 ? virtualKey - 112 : letters ? virtualKey - 65 + 12 : 0;
        ctrl.IsChecked = (modifiers & 2) != 0; alt.IsChecked = (modifiers & 1) != 0; shift.IsChecked = (modifiers & 4) != 0;
    }
}
