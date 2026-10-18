using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using System.Windows.Input;
using System.Web.Script.Serialization;

namespace F10Recorder;

internal sealed class ActionCommand(Action action) : ICommand
{
    public event EventHandler CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object parameter) => true;
    public void Execute(object parameter) => action();
}

public sealed class App : Application
{
    internal static string DataDirectory, ControlTitle = HotkeyWindow.ControlTitle;
    internal static string[] Arguments = [];
    internal static Func<string, Action<Action>, bool, string, WindowsRecorder> RecorderFactory;
    internal static Action<App> Started;
    internal WindowsRecorder Recorder;
    internal SettingsWindow SettingsWindow;
    internal NoticeWindow Notice;
    TrayIcon tray;
    DispatcherTimer timer;
    IClassicDesktopStyleApplicationLifetime desktop;
    NativeMenuItem statusItem, pauseItem;
    bool exiting, quitting;

    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark; Styles.Add(new FluentTheme());
        Resources["SystemAccentColor"] = Color.Parse("#53C58B");
        Resources["SystemControlHighlightAccentBrush"] = new SolidColorBrush(Color.Parse("#53C58B"));
        Ui.AddStyles(Styles);
    }
    public override void OnFrameworkInitializationCompleted()
    {
        desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime;
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DataDirectory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "F10Recorder");
        Action<Action> dispatch = action => Dispatcher.UIThread.Post(() => { if (!exiting) action(); });
        Recorder = RecorderFactory?.Invoke(DataDirectory, dispatch, Arguments.Contains("--paused"), ControlTitle) ?? new WindowsRecorder(DataDirectory, dispatch, Arguments.Contains("--paused"), ControlTitle);
        SettingsWindow = new SettingsWindow(Recorder); Notice = new NoticeWindow();
        Recorder.Changed += Update; Recorder.OpenSettingsRequested += OpenSettings; Recorder.OverlayRequested += ToggleOverlay; Recorder.QuitRequested += Quit;
        Recorder.Notification += PresentNotice;
        Recorder.PreparingScreenshot += () => { SettingsWindow.DismissOverlayForScreenshot(); Notice.Dismiss(); };
        SettingsWindow.VisibilityChanged += () => { if (Notice.IsVisible) Notice.Reposition(SettingsWindow); Update(); };
        var menu = new NativeMenu(); statusItem = new NativeMenuItem(Recorder.Paused ? "Paused" : "Starting replay buffer…") { IsEnabled = false }; menu.Items.Add(statusItem);
        void Item(string name, Action action) { var item = new NativeMenuItem(name); item.Click += (_, _) => action(); menu.Items.Add(item); }
        Item("Save replay", Recorder.SaveReplay); Item("Take screenshot", Recorder.TakeScreenshot);
        Item("Open screenshots", () => OpenFolder(Recorder.Config.ScreenshotDirectory)); Item("Open clips", () => OpenFolder(Recorder.Config.OutputDirectory));
        Item("Settings…", OpenSettings); Item("Open overlay", ToggleOverlay);
        pauseItem = new NativeMenuItem("Pause recording"); pauseItem.Click += (_, _) => Recorder.Pause(!Recorder.Paused); menu.Items.Add(pauseItem);
        Item("Restart buffer", Recorder.Restart); menu.Items.Add(new NativeMenuItemSeparator()); Item("Quit", Quit);
        tray = new TrayIcon { Icon = Ui.TrayIcon(false), Menu = menu, ToolTipText = "F10 Recorder", IsVisible = true, Command = new ActionCommand(OpenSettings) };
        TrayIcon.SetIcons(this, new TrayIcons { tray });
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => { Recorder.SampleForeground(); WriteStatus(); }; timer.Start();
        desktop.Exit += (_, _) => { if (!exiting) DisposeRecorder(); };
        if (Arguments.Contains("--settings") || !Recorder.SaveRegistered || !Recorder.OverlayRegistered || !Recorder.ScreenshotRegistered) OpenSettings();
        if (Arguments.Contains("--overlay")) ToggleOverlay();
        if (Arguments.Contains("--screenshot")) Recorder.TakeScreenshot();
        Update(); base.OnFrameworkInitializationCompleted(); Started?.Invoke(this);
    }
    internal void OpenSettings() { Recorder.SampleForeground(); SettingsWindow.ShowSettings(); Update(); }
    internal void ToggleOverlay() { Recorder.SampleForeground(); SettingsWindow.ToggleOverlay(); Update(); }
    void PresentNotice(string title, string message, NotificationKind kind, int duration)
    {
        Notice.Present(title, message, kind, duration, SettingsWindow); Update();
    }
    internal void Update()
    {
        SettingsWindow.UpdateState(); statusItem.Header = Recorder.State;
        pauseItem.Header = Recorder.Paused ? "Resume recording" : "Pause recording";
        tray.ToolTipText = "F10 Recorder · " + Recorder.State; tray.Icon = Ui.TrayIcon(Recorder.Ready && Recorder.SaveRegistered);
        WriteStatus();
    }
    void WriteStatus()
    {
        try { File.WriteAllText(Path.Combine(DataDirectory, "status.json"), new JavaScriptSerializer().Serialize(new { Ui = "Avalonia", UiProcessId = Environment.ProcessId, WorkerProcessId = Recorder.Metrics?.WorkerProcessId ?? 0, CaptureProcessId = Recorder.CaptureProcessId, CaptureMetrics = Recorder.Metrics, ControlTitle = ControlTitle, Paused = Recorder.Paused, CaptureState = Recorder.State, CaptureReady = Recorder.Ready, SettingsVisible = SettingsWindow.IsVisible, SettingsWindow = SettingsWindow.TryGetPlatformHandle()?.Handle.ToInt64() ?? 0, OverlayVisible = SettingsWindow.OverlayVisible, OverlayWindow = SettingsWindow.OverlaySurface?.TryGetPlatformHandle()?.Handle.ToInt64() ?? 0, OverlayShortcutRegistered = Recorder.OverlayRegistered, OverlayShortcut = Recorder.Config.OverlayHotkeyText, NotificationVisible = Notice.IsVisible, NotificationTitle = Notice.Heading, ScreenshotShortcutRegistered = Recorder.ScreenshotRegistered, ScreenshotShortcut = Recorder.Config.ScreenshotHotkeyText, ScreenshotInProgress = Recorder.ScreenshotInProgress, LastScreenshot = Recorder.LastScreenshot, CurrentApplication = Recorder.ApplicationName, ApplicationFolder = ApplicationFolders.FolderName(Recorder.ApplicationName) })); } catch (IOException) { }
    }
    void OpenFolder(string path) { try { WindowsRecorder.OpenFolder(path); } catch (Exception ex) { PresentNotice("Folder could not open", ex.Message, NotificationKind.Warning, 6000); } }
    internal async void Quit()
    {
        if (exiting || quitting || Recorder.Applying) return;
        quitting = true;
        try { await Recorder.StopAsync(); }
        catch (Exception ex) { quitting = false; Log.Write(ex.ToString()); PresentNotice("Recorder could not stop", ex.Message, NotificationKind.Warning, 6000); return; }
        DisposeRecorder(); desktop.Shutdown();
    }
    void DisposeRecorder()
    {
        exiting = true; timer?.Stop(); tray?.Dispose(); Notice?.Close(); SettingsWindow?.PrepareExit(); Recorder?.Dispose();
    }
}
