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
    bool CaptureModeKnown { get; }
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
    Task RepairMediaAsync();
    void TakeScreenshot();
    Task StopAsync();
}
