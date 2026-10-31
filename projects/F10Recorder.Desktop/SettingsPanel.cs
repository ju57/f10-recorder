using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using System.Globalization;

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
    string selectedExecutable, selectedClass, codecDraft;
    readonly CheckBox audio = new() { Name = "SystemAudio", Content = "Capture system audio" }, startup = new() { Name = "StartWithWindows", Content = "Start buffering at Windows sign-in" };
    readonly TextBox clips = new() { Name = "OutputDirectory" }, screenshots = new() { Name = "ScreenshotDirectory" };
    readonly TextBox cacheFolder = new() { Name = "ReplayCacheDirectory" }, cacheBudget = new() { Name = "ReplayCacheBudgetGB" };
    readonly TextBlock budgetError = Ui.Label("", 12, Ui.Warning), cacheUsage = Ui.Label("", 12, Ui.Muted), outputDemandStatus = Ui.Label("", 12, Ui.Muted);
    readonly Dictionary<TextBox, TextBlock> folderErrors = [];
    readonly Func<string, string, Task<string>> selectFolder;
    readonly Button reset;
    bool loadingStorage;
    readonly ShortcutEditor saveShortcut = new("Save", false), overlayShortcut = new("Overlay", true), screenshotShortcut = new("Screenshot", true);
    readonly ToggleSwitch recording = new() { Name = "RecordingEnabled", OnContent = "On", OffContent = "Off" };
    readonly TextBlock state = Ui.Label("Paused", 15, Ui.Warning, FontWeight.SemiBold), stateDetail = Ui.Label("", 12, Ui.Muted), estimate = Ui.Label("", 12, Ui.Muted), error = Ui.Label("", 13, Ui.Warning), codecStatus = Ui.Label("Check the recording engine to load video choices.", 12, Ui.Muted);
    readonly TextBlock profileStatus = Ui.Label("", 13, Ui.Warning);
    readonly TextBlock profileHeading = Ui.Label("", 14, new SolidColorBrush(Color.Parse("#F2F2F2")), FontWeight.SemiBold);
    readonly Border profileCard = new() { Name = "ProfileEntryPresentation", Background = new SolidColorBrush(Color.Parse("#1E1E1E")), BorderBrush = new SolidColorBrush(Color.Parse("#3D3D3D")), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(16) };
    readonly Button reviewPreferences = Ui.Button("Review preferences");
    readonly TextBlock audioFormat = Ui.Label("", 13, Ui.Muted), playbackDevice = Ui.Label("Windows default · automatic", 13, Ui.Muted);
    readonly Button apply;
    readonly Button repairEngine = Ui.Button("Repair recording engine"), recheckEngine = Ui.Button("Check engine again");
    readonly TextBlock engineStatus = Ui.Label("Recording engine not checked.", 12, Ui.Muted);
    readonly ContentControl page = new() { Name = "ProfileEditor" };
    readonly List<StackPanel> pages = [];
    readonly List<Button> navigation = [];
    bool updating, loaded, probing, hasProfileDraft;
    string lastProfileHeading;
    string capabilityContext;
    bool probeInvalidated;
    public Action CloseRequested;
    internal Settings Submitted { get; private set; }
    internal string Error => error.Text;
    internal bool CodecCheckFinished => !probing;
    internal string EngineStatus => engineStatus.Text;
    internal int SelectedPage { get; private set; }

    public SettingsPanel(IWindowsRecorder session, Func<string, string, Task<string>> folderPicker = null)
    {
        Background = Ui.Background;
        recorder = session; selectFolder = folderPicker ?? PickFolderAsync; hasProfileDraft = ProfileEntry.Describe(session).CanEdit;
        LoadDraft(ProfileEntry.HasUsableConfig(session.Config) ? session.Config : new Settings()); startup.IsChecked = WindowsStartup.Enabled;
        estimate.Name = "RollingCacheEstimate"; audioFormat.Name = "AudioFormat"; playbackDevice.Name = "PlaybackDevicePolicy";
        ToolTip.SetTip(estimate, "Estimate from the selected duration and bitrates, including 10 seconds of cache headroom. Actual disk usage varies.");
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(28, 24) };
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 22) };
        var title = new StackPanel { Spacing = 5 }; title.Children.Add(Ui.Label("F10 Recorder", 27, null, FontWeight.SemiBold)); title.Children.Add(Ui.Label("Keep the moment. Save it when it matters.", 13, Ui.Muted)); heading.Children.Add(title);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        quickScreenshot = Ui.Button("Screenshot", () => { if (CanUseCaptureActions() && !recorder.ScreenshotInProgress) recorder.TakeScreenshot(); }); quickScreenshot.Name = "QuickScreenshot"; actions.Children.Add(quickScreenshot);
        quickSave = Ui.Button("Save replay", () => { if (CanUseCaptureActions() && recorder.CanSave && !recorder.Saving) recorder.SaveReplay(); }, true); quickSave.Name = "QuickSaveReplay"; actions.Children.Add(quickSave); Grid.SetColumn(actions, 1); heading.Children.Add(actions); root.Children.Add(heading);
        var header = new StackPanel { Spacing = 18 };
        var statusGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var statusText = new StackPanel { Spacing = 5 }; statusText.Children.Add(state); statusText.Children.Add(stateDetail); statusGrid.Children.Add(statusText);
        recording.VerticalAlignment = VerticalAlignment.Center; AutomationProperties.SetName(recording, "Replay recording"); Grid.SetColumn(recording, 1); statusGrid.Children.Add(recording);
        header.Children.Add(new Border { Background = Ui.Surface, BorderBrush = Ui.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(18, 16), Child = statusGrid });
        profileStatus.Name = "ProfileCompatibility"; profileStatus.Foreground = new SolidColorBrush(Color.Parse("#B8B8B8")); AutomationProperties.SetName(profileStatus, "Recording profile compatibility");
        profileHeading.Name = "ProfileEntryHeading";
        reviewPreferences.Name = "ReviewProfilePreferences"; reviewPreferences.HorizontalAlignment = HorizontalAlignment.Left;
        reviewPreferences.Background = new SolidColorBrush(Color.Parse("#262626")); reviewPreferences.BorderBrush = new SolidColorBrush(Color.Parse("#686868"));
        reviewPreferences.Foreground = new SolidColorBrush(Color.Parse("#F2F2F2")); reviewPreferences.CornerRadius = new CornerRadius(8); reviewPreferences.MinHeight = 36;
        AutomationProperties.SetName(reviewPreferences, "Review recording preferences"); ToolTip.SetTip(reviewPreferences, "Open Capture preferences without applying changes.");
        reviewPreferences.Click += (_, _) => { if (ProfileEntry.Describe(recorder).CanEdit) SelectPage(0); };
        var profileBody = new StackPanel { Spacing = 8 }; profileBody.Children.Add(profileHeading); profileBody.Children.Add(profileStatus); profileBody.Children.Add(reviewPreferences);
        profileCard.Child = profileBody; header.Children.Add(profileCard);
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 0, 0, 8) };
        foreach (string name in new[] { "Capture", "Audio", "Controls", "Storage" })
        {
            int index = navigation.Count; var button = Ui.Button(name, () => { if (ProfileEntry.Describe(recorder).CanEdit) SelectPage(index); }); button.Name = name + "Tab";
            button.MinHeight = 42; button.Padding = new Thickness(18, 10); button.BorderThickness = new Thickness(1);
            AutomationProperties.SetName(button, name + " settings"); navigation.Add(button); tabs.Children.Add(button);
        }
        header.Children.Add(tabs);
        pages.Add(CapturePage()); pages.Add(AudioPage()); pages.Add(ControlsPage()); pages.Add(StoragePage());
        // Status and navigation scroll with the form in short work areas;
        // the title and footer retain their space without shrinking controls.
        var body = new StackPanel(); body.Children.Add(header); body.Children.Add(page);
        var scroll = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1); root.Children.Add(scroll);
        var footer = new StackPanel { Spacing = 10, Margin = new Thickness(0, 14, 0, 0) }; footer.Children.Add(error);
        var footerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8 };
        var info = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center }; info.Children.Add(estimate);
        var logs = Ui.Button("Diagnostic logs", () => { try { RecorderHost.OpenFolder(recorder.DataDirectory); } catch (Exception ex) { error.Text = ex.Message; } }); logs.Padding = new Thickness(0); logs.Background = Brushes.Transparent; logs.Foreground = Ui.Muted; info.Children.Add(logs); footerGrid.Children.Add(info);
        var close = Ui.Button("Close", () => CloseRequested?.Invoke()); Grid.SetColumn(close, 1); footerGrid.Children.Add(close);
        reset = Ui.Button("Reset edits", ResetEdits); reset.Name = "ResetStorageEdits";
        reset.HorizontalAlignment = HorizontalAlignment.Left; footer.Children.Insert(0, reset);
        apply = Ui.Button("Apply changes", primary: true); apply.Name = "ApplySettings"; apply.Click += async (_, _) => await ApplyAsync(); Grid.SetColumn(apply, 2); footerGrid.Children.Add(apply);
        repairEngine.Name = "RepairRecordingEngine"; recheckEngine.Name = "RecheckRecordingEngine"; engineStatus.Name = "RecordingEngineStatus";
        repairEngine.Click += async (_, _) => await RepairEngineAsync(); recheckEngine.Click += async (_, _) => await ProbeAsync();
        footer.Children.Add(footerGrid); Grid.SetRow(footer, 2); root.Children.Add(footer); Content = root;
        recording.PropertyChanged += (_, e) => { if (!updating && e.Property == ToggleButton.IsCheckedProperty && ProfileEntry.Describe(recorder).CanEdit && !recorder.Applying && recorder.ProfileStatus?.RequiresCorrection == false) recorder.Pause(recording.IsChecked != true); };
        minutes.PropertyChanged += (_, e) => { if (e.Property == NumericUpDown.ValueProperty) Estimate(); }; bitrate.PropertyChanged += (_, e) => { if (e.Property == NumericUpDown.ValueProperty) Estimate(); };
        audio.PropertyChanged += (_, e) => { if (e.Property == ToggleButton.IsCheckedProperty) UpdateAudioSummary(); };
        captureMode.SelectionChanged += (_, _) => UpdateSourceSummary();
        codec.SelectionChanged += (_, _) => { if (!probing && ProfileEntry.Describe(recorder).CanEdit && codec.SelectedItem is EncoderOption chosen) codecDraft = chosen.Codec; };
        window.SelectionChanged += (_, _) => { if (window.SelectedItem is WindowCaptureChoice choice) { selectedExecutable = choice.Executable; selectedClass = choice.ClassName; UpdateSourceSummary(); } };
        foreach (var field in new[] { clips, screenshots, cacheFolder, cacheBudget }) field.TextChanged += (_, _) => { if (!loadingStorage) { ClearStorageErrors(); Estimate(); UpdateState(); } };
        UpdateSourceSummary();
        SelectPage(0); UpdateAudioSummary(); UpdateState(); AttachedToVisualTree += async (_, _) => await EnterAsync();
    }
    void LoadDraft(Settings config)
    {
        minutes.Value = config.ReplayMinutes; bitrate.Value = config.BitrateMbps; display.Value = config.DisplayIndex; fps.SelectedItem = config.FramesPerSecond;
        selectedExecutable = config.WindowExecutable; selectedClass = config.WindowClass; captureMode.SelectedIndex = config.CaptureMode == "window" ? 1 : 0;
        codecDraft = config.VideoCodec;
        audio.IsChecked = config.SystemAudio; clips.Text = config.OutputDirectory; screenshots.Text = config.ScreenshotDirectory;
        loadingStorage = true;
        cacheFolder.Text = config.ReplayCacheDirectory.Length == 0 ? ReplayCacheLocations.Default(recorder.DataDirectory) : config.ReplayCacheDirectory;
        cacheBudget.Text = (config.ReplayCacheBudgetBytes / 1_000_000_000m).ToString("0.#########", CultureInfo.CurrentCulture);
        loadingStorage = false;
        saveShortcut.Load(config.SaveKey, config.SaveModifiers); overlayShortcut.Load(config.OverlayKey, config.OverlayModifiers); screenshotShortcut.Load(config.ScreenshotKey, config.ScreenshotModifiers);
    }
    internal async Task EnterAsync()
    {
        if (loaded) return;
        loaded = true;
        if (ProfileEntry.Describe(recorder).AutoCheckEngine) await ProbeAsync();
    }
    bool CanUseCaptureActions() => ProfileEntry.Describe(recorder).CanEdit && recorder.ProfileStatus?.ActivationRequired == false && !recorder.Applying;
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
        StorageFolder(body, "Recordings", clips);
        StorageFolder(body, "Screenshots", screenshots);
        StorageFolder(body, "Replay cache", cacheFolder);
        var budgetTitle = Ui.Label("Replay cache budget", 14, null, FontWeight.SemiBold); budgetTitle.Margin = new Thickness(0, 18, 0, 8); body.Children.Add(budgetTitle);
        AutomationProperties.SetName(cacheBudget, "Replay cache budget in GB"); AutomationProperties.SetHelpText(cacheBudget, "Decimal gigabytes. Existing exact byte values are preserved.");
        var budgetRow = new Grid { ColumnDefinitions = new ColumnDefinitions("180,Auto"), ColumnSpacing = 10 };
        cacheBudget.Padding = new Thickness(12, 8); cacheBudget.MinHeight = 40; cacheBudget.CornerRadius = new CornerRadius(8); cacheBudget.Background = new SolidColorBrush(Color.Parse("#262626")); cacheBudget.BorderBrush = new SolidColorBrush(Color.Parse("#686868")); budgetRow.Children.Add(cacheBudget);
        var unit = Ui.Label("GB", 14, Ui.Muted); unit.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(unit, 1); budgetRow.Children.Add(unit); body.Children.Add(budgetRow);
        budgetError.Name = "ReplayCacheBudgetError"; body.Children.Add(budgetError); cacheUsage.Name = "ReplayCacheUsage"; cacheUsage.Margin = new Thickness(0, 8, 0, 0); body.Children.Add(cacheUsage);
        outputDemandStatus.Name = "ReplayOutputDemandStatus"; outputDemandStatus.Margin = new Thickness(0, 6, 0, 0);
        outputDemandStatus.TextWrapping = TextWrapping.Wrap; AutomationProperties.SetName(outputDemandStatus, "Replay storage status");
        body.Children.Add(outputDemandStatus);
        body.Children.Add(Ui.Label("Changing folders affects future work. Existing recordings, screenshots and retained cache files stay where they are.", 12, Ui.Muted));
        var engine = Ui.Label("Recording engine", 15, null, FontWeight.SemiBold); engine.Margin = new Thickness(0, 26, 0, 6); body.Children.Add(engine);
        body.Children.Add(Ui.Label("F10 uses only its approved, verified recording engine. Repair replaces missing or rejected engine files and preserves your preferences and saved media. An internet connection may be needed.", 12, Ui.Muted));
        body.Children.Add(engineStatus);
        var engineActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 12, 0, 0) };
        engineActions.Children.Add(recheckEngine); engineActions.Children.Add(repairEngine); body.Children.Add(engineActions); return body;
    }
    void StorageFolder(StackPanel body, string title, TextBox text)
    {
        var label = Ui.Label(title + " folder", 14, null, FontWeight.SemiBold); label.Margin = new Thickness(0, 16, 0, 8); body.Children.Add(label);
        AutomationProperties.SetName(text, title + " folder");
        text.Padding = new Thickness(12, 8, 48, 8); text.MinHeight = 40; text.BorderThickness = new Thickness(0); text.Background = Brushes.Transparent;
        var grid = new Grid(); grid.Children.Add(text);
        var icon = new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse("M245,110.64A16,16,0,0,0,232,104H216V88a16,16,0,0,0-16-16H130.67L102.94,51.2a16.14,16.14,0,0,0-9.6-3.2H40A16,16,0,0,0,24,64V208h0a8,8,0,0,0,8,8H211.1a8,8,0,0,0,7.59-5.47l28.49-85.47A16.05,16.05,0,0,0,245,110.64ZM93.34,64,123.2,86.4A8,8,0,0,0,128,88h72v16H69.77a16,16,0,0,0-15.18,10.94L40,158.7V64Z"), Fill = new SolidColorBrush(Color.Parse("#F2F2F2")), Stretch = Stretch.Uniform, Width = 20, Height = 20 };
        var browse = new Button { Name = text.Name + "Picker", Content = icon, Width = 36, Height = 36, MinHeight = 36, Padding = new Thickness(8), Margin = new Thickness(0, 2, 2, 2), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(0), Background = Brushes.Transparent };
        AutomationProperties.SetName(browse, "Choose " + title.ToLowerInvariant() + " folder"); ToolTip.SetTip(browse, "Choose " + title.ToLowerInvariant() + " folder"); grid.Children.Add(browse);
        var message = Ui.Label("", 12, Ui.Warning); message.Name = text.Name + "Error"; folderErrors[text] = message;
        body.Children.Add(new Border { Child = grid, Background = new SolidColorBrush(Color.Parse("#262626")), BorderBrush = new SolidColorBrush(Color.Parse("#686868")), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8) }); body.Children.Add(message);
        browse.Click += async (_, _) =>
        {
            if (!ProfileEntry.Describe(recorder).CanEdit || recorder.Applying) return;
            try
            {
                string selected = await selectFolder("Choose " + title.ToLowerInvariant() + " folder", text.Text);
                if (!ProfileEntry.Describe(recorder).CanEdit || recorder.Applying) return;
                if (selected != null) { ValidateFolder(selected); text.Text = selected; }
            }
            catch (Exception ex) { message.Text = ex.Message; }
        };
    }
    async Task<string> PickFolderAsync(string title, string current)
    {
        var provider = TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new IOException("Open Settings in a window to choose a folder.");
        var selected = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false, SuggestedStartLocation = await provider.TryGetFolderFromPathAsync(current) });
        return selected.Count == 0 ? null : selected[0].TryGetLocalPath() ?? throw new IOException("Choose a local filesystem folder.");
    }
    static void ValidateFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new ArgumentException("Choose an absolute folder path.");
        Path.GetFullPath(path);
    }
    void ClearStorageErrors() { foreach (var message in folderErrors.Values) message.Text = ""; budgetError.Text = ""; }
    void ResetEdits()
    {
        if (!ProfileEntry.Describe(recorder).CanEdit || recorder.Applying) return;
        loadingStorage = true; LoadDraft(recorder.Config); startup.IsChecked = WindowsStartup.Enabled;
        codec.SelectedItem = (codec.ItemsSource as IEnumerable<EncoderOption>)?.FirstOrDefault(item => item.Codec == recorder.Config.VideoCodec);
        ClearStorageErrors(); error.Text = ""; loadingStorage = false; UpdateSourceSummary(); UpdateAudioSummary(); UpdateState();
    }
    long StorageBudget()
    {
        if (!decimal.TryParse(cacheBudget.Text, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, CultureInfo.CurrentCulture, out decimal gb)
            || gb <= 0 || gb > long.MaxValue / 1_000_000_000m || decimal.Truncate(gb * 1_000_000_000m) != gb * 1_000_000_000m)
            throw new ArgumentException("Enter a positive budget in GB, with at most nine decimal places.");
        return checked((long)(gb * 1_000_000_000m));
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
        var entry = ProfileEntry.Describe(recorder);
        if (!entry.CanEdit || recorder.Applying || recorder.Phase == CapturePhase.Recovering) { error.Text = entry.Reason; return; }
        string selected = codecDraft;
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
            bool retainUnavailable = selected == recorder.Config.VideoCodec && savedChoice == null && choices.Length > 0 && recorder.ProfileStatus.ActivationRequired && recorder.ProfileStatus.RequiresCorrection && recorder.ProfileStatus.CanWrite
                && !recorder.ProfileStatus.Issues.Any(i => i.StartsWith("VideoCodec:", StringComparison.Ordinal));
            if (retainUnavailable) savedChoice = new EncoderOption { Codec = selected, Name = selected.Replace("_nvenc", "").ToUpperInvariant() + " · saved preference, unavailable" };
            codec.ItemsSource = retainUnavailable ? choices.Append(savedChoice).ToArray() : choices; codec.SelectedItem = savedChoice;
            codecStatus.Text = choices.Length > 0 ? "Initialized: " + string.Join(" / ", choices.Select(c => c.Codec.Replace("_nvenc", "").ToUpperInvariant())) + ". Generated 256×256 frame at 30 FPS; other capture settings require their own checks." : "No GPU encoder initialized. Check the NVIDIA driver, then check again.";
            if (retainUnavailable) codecStatus.Text += " Corrections keep your unavailable saved codec; replay remains paused until you explicitly resume.";
            else if (choices.Length > 0 && codec.SelectedItem == null) codecStatus.Text += " Your " + selected.Replace("_nvenc", "").ToUpperInvariant() + " choice is unavailable. Check again or explicitly choose another codec.";
            engineStatus.Text = "Approved engine " + receipt.PackageVersion + " verified at " + receipt.CheckedUtc + ". " + codecStatus.Text;
        }
        catch (Exception ex) { codec.ItemsSource = Array.Empty<EncoderOption>(); codec.SelectedItem = null; codecStatus.Text = engineStatus.Text = ex.Message; }
        finally { probing = false; UpdateState(); }
    }
    internal async Task RepairEngineAsync()
    {
        if (probing || recorder.Applying || !ProfileEntry.Describe(recorder).CanEdit || recorder.Phase == CapturePhase.Recovering) return;
        probing = true; codec.ItemsSource = Array.Empty<EncoderOption>(); codec.SelectedItem = null;
        codecStatus.Text = engineStatus.Text = "Repairing recording engine…"; error.Text = ""; UpdateState();
        try { await recorder.RepairMediaAsync(); await ProbeAsync(); }
        catch (Exception ex) { error.Text = codecStatus.Text = engineStatus.Text = ex.Message; }
        finally { probing = false; UpdateState(); }
    }
    public void UpdateState()
    {
        if (probing && (!ProfileEntry.Describe(recorder).CanEdit || recorder.Applying || recorder.Phase is CapturePhase.Recovering or CapturePhase.Reconnecting or CapturePhase.Unknown)) probeInvalidated = true;
        if (!probing && capabilityContext != null && (capabilityContext != recorder.EngineCapabilityContext || recorder.Applying || recorder.Phase is CapturePhase.Recovering or CapturePhase.Reconnecting or CapturePhase.Unknown))
        {
            capabilityContext = null; codec.ItemsSource = Array.Empty<EncoderOption>(); codec.SelectedItem = null;
            codecStatus.Text = engineStatus.Text = "Recording engine state changed. Check again before choosing a codec.";
        }
        updating = true; recording.IsChecked = !recorder.Paused; updating = false;
        var entry = ProfileEntry.Describe(recorder);
        if (!hasProfileDraft && entry.CanEdit) { LoadDraft(recorder.Config); hasProfileDraft = true; UpdateSourceSummary(); UpdateAudioSummary(); }
        if (entry.Kind != ProfileEntryKind.Unavailable) lastProfileHeading = entry.Heading;
        profileHeading.Text = entry.Kind == ProfileEntryKind.Unavailable ? lastProfileHeading ?? entry.Heading : entry.Heading;
        profileStatus.Text = entry.Reason; profileStatus.IsVisible = true; profileCard.IsVisible = entry.AttentionVisible;
        reviewPreferences.IsVisible = entry.CanEdit; reviewPreferences.IsEnabled = entry.CanEdit;
        page.IsVisible = entry.CanEdit;
        foreach (var button in navigation) button.IsEnabled = entry.CanEdit;
        var status = ReplayPresentation.Describe(recorder.Phase, recorder.State, recorder.Paused, recorder.Ready, recorder.CanSave, recorder.Applying, recorder.Saving, recorder.SaveRegistered, recorder.Metrics?.BufferedSeconds ?? 0);
        state.Text = entry.CanEdit ? status.Heading : "Recording status unavailable";
        state.Foreground = entry.CanEdit && status.Healthy && recorder.SaveRegistered ? Ui.Accent : Ui.Warning;
        stateDetail.Text = entry.CanEdit ? status.Detail : "Recording readiness cannot be confirmed while this profile needs attention.";
        quickSave.IsEnabled = status.CanSave && CanUseCaptureActions(); quickScreenshot.IsEnabled = CanUseCaptureActions() && !recorder.ScreenshotInProgress;
        apply.IsEnabled = entry.CanEdit && !probing && codec.SelectedItem != null && !recorder.Applying;
        codec.IsEnabled = entry.CanEdit && !probing && !recorder.Applying;
        recheckEngine.IsEnabled = repairEngine.IsEnabled = entry.CanEdit && !probing && !recorder.Applying && recorder.Phase != CapturePhase.Recovering;
        repairEngine.Content = recorder.Applying ? "Working…" : "Repair recording engine";
        apply.Content = recorder.Applying ? "Applying…" : "Apply changes"; recording.IsEnabled = entry.CanEdit && !recorder.Applying && recorder.ProfileStatus?.RequiresCorrection == false;
        if (reset != null) reset.IsEnabled = entry.CanEdit && !recorder.Applying;
        if (cacheUsage != null) cacheUsage.Text = recorder.Metrics?.CacheBytes >= 0 && !string.IsNullOrEmpty(recorder.Metrics.SampledUtc) ? "Cache used: " + (recorder.Metrics.CacheBytes / 1_000_000_000m).ToString("0.###", CultureInfo.CurrentCulture) + " GB" : "Cache used: unavailable";
        var metrics = recorder.Metrics;
        bool known = metrics != null && recorder.Phase is not (CapturePhase.Unknown or CapturePhase.Reconnecting);
        outputDemandStatus.Text = !known ? "Replay output estimate: unavailable." : metrics.ReplayOutputDemandKind switch
        {
            "CalibratedEstimate" => "Replay output estimate: calibrated for the last replay selection.",
            "SourceBound" => "Replay output estimate: verified for the last replay selection.",
            _ => "Replay output estimate: unmeasured. " + (metrics.ReplayOutputDemandReason ?? "No qualified estimate is available.")
        };
        if (known && !string.IsNullOrEmpty(metrics.ReplayStorageError))
            outputDemandStatus.Text += Environment.NewLine + metrics.ReplayStorageError;
        if (known && metrics.ReplayStorageSafetyHeld)
            outputDemandStatus.Text += Environment.NewLine + "Recording stopped for storage. " + metrics.ReplayStorageSafetyReason
                + " Use Resume after the storage checks pass.";
        else if (known && metrics.ReplayStorageSafetyWarning)
            outputDemandStatus.Text += Environment.NewLine + "Low storage. " + metrics.ReplayStorageSafetyReason;
        else if (known && metrics.ReplayStorageSafetyKind == "Unmeasured")
            outputDemandStatus.Text += Environment.NewLine + "Capture growth: unmeasured.";
        outputDemandStatus.Foreground = known && (!string.IsNullOrEmpty(metrics.ReplayStorageError)
            || metrics.ReplayStorageSafetyHeld || metrics.ReplayStorageSafetyWarning) ? Ui.Warning : Ui.Muted;
    }
    void RefreshWindows()
    {
        if (!ProfileEntry.Describe(recorder).CanEdit) return;
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
        var entry = ProfileEntry.Describe(recorder);
        if (!entry.CanEdit || recorder.Applying) { error.Text = entry.Reason; return false; }
        try
        {
            if (probing || codec.SelectedItem is not EncoderOption chosen) throw new InvalidOperationException("Wait for an available GPU encoder.");
            ClearStorageErrors();
            foreach (var field in new[] { clips, screenshots, cacheFolder })
                try { ValidateFolder(field.Text); } catch (Exception ex) { folderErrors[field].Text = ex.Message; throw; }
            long budget;
            try { budget = StorageBudget(); } catch (Exception ex) { budgetError.Text = ex.Message; throw; }
            var next = new Settings { CaptureMode = captureMode.SelectedIndex == 1 ? "window" : "monitor", WindowExecutable = selectedExecutable, WindowClass = selectedClass, ReplayMinutes = (int)(minutes.Value ?? 5), BitrateMbps = (int)(bitrate.Value ?? 20), DisplayIndex = (int)(display.Value ?? -1), FramesPerSecond = (int)fps.SelectedItem, VideoCodec = chosen.Codec, SystemAudio = audio.IsChecked == true, OutputDirectory = clips.Text, ScreenshotDirectory = screenshots.Text, SaveKey = saveShortcut.Key, SaveModifiers = saveShortcut.Modifiers, OverlayKey = overlayShortcut.Key, OverlayModifiers = overlayShortcut.Modifiers, ScreenshotKey = screenshotShortcut.Key, ScreenshotModifiers = screenshotShortcut.Modifiers };
            next.ReplayCacheBudgetBytes = budget;
            next.ReplayCacheDirectory = string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(cacheFolder.Text)), Path.TrimEndingDirectorySeparator(ReplayCacheLocations.Default(recorder.DataDirectory)), StringComparison.OrdinalIgnoreCase) ? "" : cacheFolder.Text;
            if ((minutes.Value ?? 5) * 60m * (bitrate.Value ?? 20) * 1_000_000 / 8 > budget) { budgetError.Text = "This budget cannot hold even the selected video history. Increase it or change replay length/bitrate."; throw new ArgumentException(budgetError.Text); }
            next.Validate(); error.Text = ""; CaptureVisibility.Configure(null); await recorder.ApplyAsync(next, startup.IsChecked == true); CaptureVisibility.Configure(recorder.Config, recorder.CaptureModeKnown); Submitted = next; CloseRequested?.Invoke(); return true;
        }
        catch (Exception ex) { CaptureVisibility.Configure(recorder.Config, recorder.CaptureModeKnown); error.Text = ex.Message; if (ex.Message.Contains("budget", StringComparison.OrdinalIgnoreCase)) budgetError.Text = ex.Message; else if (ex.Message.Contains("cache", StringComparison.OrdinalIgnoreCase)) folderErrors[cacheFolder].Text = ex.Message; return false; }
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
