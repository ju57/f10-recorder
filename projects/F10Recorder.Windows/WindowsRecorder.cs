namespace F10Recorder;

internal interface IWindowsRecorder : IDisposable
{
    Settings Config { get; }
    string DataDirectory { get; }
    bool Paused { get; }
    bool Ready { get; }
    CapturePhase Phase { get; }
    bool CanSave { get; }
    bool Saving { get; }
    bool Applying { get; }
    bool ScreenshotInProgress { get; }
    string LastScreenshot { get; }
    string State { get; }
    bool SaveRegistered { get; }
    bool OverlayRegistered { get; }
    bool ScreenshotRegistered { get; }
    string ApplicationName { get; }
    int CaptureProcessId { get; }
    RecorderMetrics Metrics { get; }
    event Action Changed, OpenSettingsRequested, OverlayRequested, QuitRequested, PreparingScreenshot;
    event Action<string, string, NotificationKind, int> Notification;
    event Action<string, bool> Saved;
    void SampleForeground();
    void SaveReplay();
    void Pause(bool value);
    void Restart();
    Task ApplyAsync(Settings next, bool startup);
    void TakeScreenshot();
    Task StopAsync();
}

// Production uses the independent worker. In-process hosting is restricted to the
// existing verification harness, which supplies its synthetic source/startup writer.
internal sealed class WindowsRecorder : IWindowsRecorder
{
    readonly IWindowsRecorder backend;
    public WindowsRecorder(string data, Action<Action> dispatch, bool paused, string controlTitle, bool syntheticSource = false, Action<bool> setStartup = null)
    {
        backend = syntheticSource || setStartup != null
            ? new RecorderHost(data, dispatch, paused, controlTitle, syntheticSource, setStartup)
            : new WorkerRecorder(data, dispatch, paused, controlTitle);
    }
    internal static void Initialize(string data) => RecorderHost.Initialize(data);
    public Settings Config => backend.Config;
    public string DataDirectory => backend.DataDirectory;
    public bool Paused => backend.Paused;
    public bool Ready => backend.Ready;
    public CapturePhase Phase => backend.Phase;
    public bool CanSave => backend.CanSave;
    public bool Saving => backend.Saving;
    public bool Applying => backend.Applying;
    public bool ScreenshotInProgress => backend.ScreenshotInProgress;
    public string LastScreenshot => backend.LastScreenshot;
    public string State => backend.State;
    public bool SaveRegistered => backend.SaveRegistered;
    public bool OverlayRegistered => backend.OverlayRegistered;
    public bool ScreenshotRegistered => backend.ScreenshotRegistered;
    public string ApplicationName => backend.ApplicationName;
    public int CaptureProcessId => backend.CaptureProcessId;
    public RecorderMetrics Metrics => backend.Metrics;
    public event Action Changed { add => backend.Changed += value; remove => backend.Changed -= value; }
    public event Action OpenSettingsRequested { add => backend.OpenSettingsRequested += value; remove => backend.OpenSettingsRequested -= value; }
    public event Action OverlayRequested { add => backend.OverlayRequested += value; remove => backend.OverlayRequested -= value; }
    public event Action QuitRequested { add => backend.QuitRequested += value; remove => backend.QuitRequested -= value; }
    public event Action PreparingScreenshot { add => backend.PreparingScreenshot += value; remove => backend.PreparingScreenshot -= value; }
    public event Action<string, string, NotificationKind, int> Notification { add => backend.Notification += value; remove => backend.Notification -= value; }
    public event Action<string, bool> Saved { add => backend.Saved += value; remove => backend.Saved -= value; }
    public void SampleForeground() => backend.SampleForeground();
    public void SaveReplay() => backend.SaveReplay();
    public void Pause(bool value) => backend.Pause(value);
    public void Restart() => backend.Restart();
    public Task ApplyAsync(Settings next, bool startup) => backend.ApplyAsync(next, startup);
    public void TakeScreenshot() => backend.TakeScreenshot();
    public Task StopAsync() => backend.StopAsync();
    public void Dispose() => backend.Dispose();
    public static void OpenFolder(string path) => RecorderHost.OpenFolder(path);
}
