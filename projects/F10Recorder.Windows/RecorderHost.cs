using Microsoft.Win32;
using System.Diagnostics;

namespace F10Recorder;

// All members except encoder callbacks run on the host's owner thread.
// NativeWindow is used only for its Win32 message dispatch, never visual controls.
internal sealed class RecorderHost : IWindowsRecorder
{
    readonly Action<Action> post;
    readonly string settingsPath;
    readonly bool synthetic;
    readonly Func<int, string, string> captureScreenshot;
    readonly Action<bool> startupWriter;
    readonly bool customStartupWriter;
    readonly HotkeyWindow shortcuts;
    readonly ForegroundApplicationTracker foreground;
    readonly RecorderNotifications notices;
    ReplayEngine engine;
    WindowCaptureTarget windowTarget;
    bool disposed;
    int generation;
    public Settings Config { get; private set; }
    public string DataDirectory { get; }
    public bool Paused { get; private set; }
    public bool Ready { get; private set; }
    public CapturePhase Phase { get; private set; } = CapturePhase.Starting;
    public bool CanSave => !Applying && !Paused && engine.CanSave;
    public bool Applying { get; private set; }
    public bool CaptureModeKnown => !Applying;
    public bool ScreenshotInProgress { get; private set; }
    public string LastScreenshot { get; private set; }
    public string State { get; private set; } = "Starting replay buffer…";
    public bool SaveRegistered => shortcuts.SaveRegistered;
    public bool OverlayRegistered => shortcuts.OverlayRegistered;
    public bool ScreenshotRegistered => shortcuts.ScreenshotRegistered;
    public string ApplicationName => Config.CaptureMode == "window" ? Path.GetFileNameWithoutExtension(Config.WindowExecutable) : foreground.ApplicationName;
    public int CaptureProcessId => engine.CaptureProcessId;
    public RecorderMetrics Metrics => engine.Metrics;
    public bool Saving => engine.IsSaving;
    internal Func<Task> PrepareRemoteScreenshot;
    internal void ExcludeUiProcess(int id) => foreground.ExcludeProcess(id);
    public event Action Changed;
    public event Action OpenSettingsRequested;
    public event Action OverlayRequested;
    public event Action QuitRequested;
    public event Action PreparingScreenshot;
    public event Action<string, string, NotificationKind, int> Notification;
    public event Action<string, bool> Saved;

    internal static void Initialize(string data)
    {
        Directory.CreateDirectory(data); Log.DirectoryPath = data;
        RecorderRuntime.MediaEngineResolver = MediaDependency.ResolveExecutable;
    }

    public RecorderHost(string data, Action<Action> dispatch, bool paused, string controlTitle, bool syntheticSource = false, Action<bool> setStartup = null, Func<int, string, string> screenshotSource = null)
    {
        data = WorkerIdentity.DataPath(data);
        Initialize(data); post = dispatch; synthetic = syntheticSource; customStartupWriter = setStartup != null; startupWriter = setStartup ?? WindowsStartup.SetEnabled; captureScreenshot = screenshotSource ?? ScreenshotCapture.Capture;
        DataDirectory = data; settingsPath = Path.Combine(data, "settings.json"); Config = Settings.Load(settingsPath); Paused = paused;
        shortcuts = new HotkeyWindow(controlTitle); foreground = new ForegroundApplicationTracker();
        notices = new RecorderNotifications((title, message, kind, duration) => Notification?.Invoke(title, message, kind, duration));
        shortcuts.Pressed += SaveReplay; shortcuts.OverlayPressed += () => OverlayRequested?.Invoke(); shortcuts.ScreenshotPressed += TakeScreenshot;
        shortcuts.EnvironmentChanged += Restart;
        shortcuts.CommandRequested += command => { switch (command) { case 1: OpenSettingsRequested?.Invoke(); break; case 2: Pause(true); break; case 3: Pause(false); break; case 4: QuitRequested?.Invoke(); break; case 5: OverlayRequested?.Invoke(); break; case 6: TakeScreenshot(); break; } };
        Register(Config); StartEngine();
        if (!File.Exists(settingsPath)) Config.Save(settingsPath);
    }
    void Register(Settings settings)
    {
        try { shortcuts.Register(settings); }
        catch (Exception ex) { State = ex.Message; Log.Write(ex.Message); }
    }
    void StartEngine()
    {
        int current = ++generation;
        windowTarget?.Dispose(); windowTarget = null;
        if (Config.CaptureMode == "window" && !synthetic) windowTarget = new WindowCaptureTarget(Config);
        engine = new ReplayEngine(Config, DataDirectory, Paused, synthetic, windowTarget == null ? null : windowTarget.Read);
        void Dispatch(Action action) => post(() => { if (!disposed && generation == current) action(); });
        engine.CaptureStateChanged += (phase, message, ready) =>
        {
            int capture = engine.CaptureProcessId;
            Dispatch(() =>
            {
                // A queued ready event cannot revive a retired or restarting source.
                if (ready && (Paused || engine.RestartPending || capture == 0 || capture != engine.CaptureProcessId)) return;
                State = ready && !shortcuts.SaveRegistered ? "Buffering · save shortcut unavailable" : message;
                Phase = phase; Ready = ready; notices.CaptureChanged(phase, message, Ready, Paused, Config, shortcuts.SaveRegistered); Changed?.Invoke();
            });
        };
        engine.SaveStarted += () => Dispatch(notices.SaveStarted);
        engine.SaveFinished += (message, success) => Dispatch(() => { notices.SaveFinished(message, success); Saved?.Invoke(message, success); Changed?.Invoke(); });
    }
    public void SampleForeground() => foreground.Sample();
    public void SaveReplay() { if (!Applying && !disposed) engine.Save(Config.CaptureMode == "window" ? ApplicationName : foreground.Sample()); }
    public void Pause(bool value)
    {
        if (Applying || disposed || Paused == value) return;
        Paused = value; Ready = false; Phase = value ? CapturePhase.Paused : CapturePhase.Starting; State = value ? "Paused" : "Starting replay buffer…";
        engine.Pause(value); notices.PauseChanged(value); Changed?.Invoke();
    }
    public void Restart()
    {
        if (Applying || disposed) return;
        Ready = false; Phase = Paused ? CapturePhase.Paused : CapturePhase.Starting; State = Paused ? "Paused" : "Restarting replay buffer…";
        engine.Restart(); Changed?.Invoke();
    }
    public async Task ApplyAsync(Settings next, bool startup)
    {
        if (Applying || disposed) throw new InvalidOperationException("Wait for the current settings change.");
        if (engine.IsSaving || ScreenshotInProgress) throw new InvalidOperationException("Wait for the current replay or screenshot to finish saving.");
        next.Validate();
        try { shortcuts.Register(next); }
        catch { Register(Config); throw; }
        Applying = true; Changed?.Invoke();
        try
        {
            bool previousStartup = WindowsStartup.Enabled;
            // Stop the old capture before replacing it, preserving the pause state.
            await Task.Run(engine.Dispose);
            try { next.Save(settingsPath); if (customStartupWriter || startup != previousStartup) startupWriter(startup); Config = next; }
            catch
            {
                // A startup-write failure must not leave disk and live configuration different.
                Config.Save(settingsPath);
                if (WindowsStartup.Enabled != previousStartup) startupWriter(previousStartup);
                throw;
            }
            finally { StartEngine(); }
            Ready = false; Phase = Paused ? CapturePhase.Paused : CapturePhase.Starting; State = Paused ? "Paused" : "Starting replay buffer…";
        }
        finally { Applying = false; Register(Config); Changed?.Invoke(); }
    }
    public async Task RepairMediaAsync()
    {
        if (Applying || disposed || engine.IsSaving || ScreenshotInProgress) throw new InvalidOperationException("Wait for settings or saving to finish before repairing the recording engine.");
        Applying = true; Ready = false; Changed?.Invoke();
        try
        {
            await Task.Run(engine.Dispose);
            try
            {
                if (await Task.Run(() => MediaDependency.Install(DataDirectory, true)) != 0)
                    throw new IOException("Recording engine repair failed. Check the connection and retry. Details are in media-install-error.txt.");
            }
            finally { StartEngine(); }
        }
        finally { Applying = false; Changed?.Invoke(); }
    }
    public async void TakeScreenshot()
    {
        if (disposed || ScreenshotInProgress || Applying) return;
        ScreenshotInProgress = true; PreparingScreenshot?.Invoke(); Changed?.Invoke();
        int display = Config.DisplayIndex; string folder = Config.ScreenshotDirectory;
        var selected = Config.CaptureMode == "window" ? windowTarget?.ReadForScreenshot() : null;
        try
        {
            if (PrepareRemoteScreenshot != null) await PrepareRemoteScreenshot();
            await Task.Delay(150);
            string file = await Task.Run(() =>
            {
                if (Config.CaptureMode != "window") return captureScreenshot(display, folder);
                var verified = windowTarget?.ReadForScreenshot();
                if (!ScreenshotCapture.SameTarget(selected, verified)) throw new IOException("No screenshot was saved. " + (selected != null && !selected.Available ? selected.Reason : verified != null && !verified.Available ? verified.Reason : "The selected window changed."));
                return ScreenshotCapture.CaptureSource(CaptureSession.WindowVideoSource(verified, 1).Replace(":capture_cursor=1", ":capture_cursor=0"), folder, true,
                    () => !disposed && ScreenshotCapture.SameTarget(verified, windowTarget?.ReadForScreenshot()));
            });
            if (!disposed) { LastScreenshot = file; Notification?.Invoke("Screenshot saved", Path.GetFileName(file), NotificationKind.Success, 4000); }
        }
        catch (Exception ex) { Log.Write(ex.ToString()); if (!disposed) Notification?.Invoke("Screenshot not saved", ex.Message, NotificationKind.Warning, 6000); }
        finally { ScreenshotInProgress = false; if (!disposed) Changed?.Invoke(); }
    }
    public static void OpenFolder(string path) { Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        shortcuts.Dispose(); foreground.Dispose(); engine.Dispose(); windowTarget?.Dispose();
    }
    public Task StopAsync()
    {
        if (!disposed && (Applying || engine.IsSaving || ScreenshotInProgress)) throw new InvalidOperationException("Wait for settings, replay or screenshot saving to finish before quitting.");
        Dispose(); return Task.CompletedTask;
    }
}

internal static class WindowsStartup
{
    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static bool Enabled
    {
        get
        {
            try { using var key = Registry.CurrentUser.OpenSubKey(Key); return key?.GetValue("F10Recorder") != null; }
            catch (System.Security.SecurityException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
    }
    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Key);
        if (enabled) key.SetValue("F10Recorder", CaptureSession.Quote(Environment.ProcessPath), RegistryValueKind.String);
        else key.DeleteValue("F10Recorder", false);
    }
}
