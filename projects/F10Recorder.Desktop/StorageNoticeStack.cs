namespace F10Recorder;

// An accepted save can finish while capture remains held. Its transient result
// must not replace the independent, individually dismissible storage problem.
internal sealed class StorageNoticeStack : IDisposable
{
    internal NoticeWindow Transient { get; } = new();
    internal NoticeWindow Problem { get; } = new();
    SettingsWindow settings;
    bool observedHold, restoreAfterScreenshot;
    internal StorageNoticeStack()
    {
        Problem.Dismissed += () => { restoreAfterScreenshot = false; Reposition(settings); };
    }
    internal void Present(string title, string message, NotificationKind kind, int duration, SettingsWindow currentSettings)
    {
        var window = kind == NotificationKind.Warning && duration <= 0 ? Problem : Transient;
        window.Present(title, message, kind, duration, currentSettings);
        Reposition(currentSettings);
    }
    internal void Observe(bool storageHeld, bool screenshotInProgress, SettingsWindow currentSettings)
    {
        if (storageHeld) observedHold = true;
        else if (observedHold) { observedHold = false; Problem.Dismiss(); }
        if (restoreAfterScreenshot && !screenshotInProgress)
        {
            restoreAfterScreenshot = false;
            Problem.Show();
        }
        Reposition(currentSettings);
    }
    internal void HideForScreenshot()
    {
        restoreAfterScreenshot |= Problem.IsVisible;
        Problem.Hide(); // Temporary capture preparation is not user dismissal.
        Transient.Dismiss();
    }
    internal void Reposition(SettingsWindow currentSettings)
    {
        settings = currentSettings;
        if (Problem.IsVisible) Problem.Reposition(settings);
        if (Transient.IsVisible) Transient.Reposition(settings, Problem.IsVisible ? Problem.Height + 12 : 0);
    }
    public void Dispose() { Transient.Close(); Problem.Close(); }
}
