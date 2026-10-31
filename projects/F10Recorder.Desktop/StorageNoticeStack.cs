namespace F10Recorder;

// An accepted save can finish while capture remains held. Its transient result
// must not replace the independent, individually dismissible storage problem.
internal sealed class StorageNoticeStack : IDisposable
{
    internal NoticeWindow Transient { get; } = new();
    internal NoticeWindow Problem { get; } = new();
    SettingsWindow settings;
    bool observedHold, dismissedHold, restoreAfterScreenshot, screenshotPreparing;
    string holdId;
    internal StorageNoticeStack()
    {
        Problem.Dismissed += () => { dismissedHold = observedHold; restoreAfterScreenshot = false; Reposition(settings); };
    }
    internal void Present(string title, string message, NotificationKind kind, int duration, SettingsWindow currentSettings)
    {
        var window = kind == NotificationKind.Warning && duration <= 0 ? Problem : Transient;
        if (window == Problem && observedHold && dismissedHold) return;
        window.Present(title, message, kind, duration, currentSettings);
        if (window == Problem && screenshotPreparing) { restoreAfterScreenshot = true; Problem.Hide(); }
        Reposition(currentSettings);
    }
    internal void Observe(bool storageHeld, bool screenshotInProgress, SettingsWindow currentSettings, string reason = null, string currentHoldId = null)
    {
        screenshotPreparing = screenshotInProgress;
        if (storageHeld)
        {
            if (!observedHold || holdId != null && currentHoldId != null && holdId != currentHoldId)
            {
                observedHold = true; dismissedHold = false; restoreAfterScreenshot = false;
                holdId = currentHoldId;
                Problem.Present("Replay recording paused for storage",
                    string.IsNullOrWhiteSpace(reason) ? "Check storage, then resume recording explicitly." : reason,
                    NotificationKind.Warning, 0, currentSettings);
                if (screenshotInProgress) { restoreAfterScreenshot = true; Problem.Hide(); }
            }
            else holdId ??= currentHoldId; // Missing identity is not evidence of a new hold.
        }
        else if (observedHold)
        {
            observedHold = false; dismissedHold = false; holdId = null; restoreAfterScreenshot = false;
            Problem.Dismiss();
        }
        if (restoreAfterScreenshot && !screenshotInProgress)
        {
            restoreAfterScreenshot = false;
            if (storageHeld && !dismissedHold) Problem.Show();
        }
        Reposition(currentSettings);
    }
    internal void HideForScreenshot()
    {
        screenshotPreparing = true;
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
