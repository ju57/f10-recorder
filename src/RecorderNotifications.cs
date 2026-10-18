using System;
namespace F10Recorder
{
    internal enum NotificationKind { Info, Success, Warning }
    internal sealed class RecorderNotifications
    {
        readonly Action<string, string, NotificationKind, int> show;
        bool buffering, recovering;
        DateTime lastRecovery = DateTime.MinValue;
        CapturePhase lastTargetPhase = CapturePhase.Unknown;
        internal RecorderNotifications(Action<string, string, NotificationKind, int> present) { show = present; }
        internal void PauseChanged(bool paused)
        {
            buffering = false; recovering = false; lastTargetPhase = CapturePhase.Unknown;
            show(paused ? "Replay recording paused" : "Starting replay recording", paused ? "Replay buffering is off until you resume." : "Preparing your replay buffer…", paused ? NotificationKind.Warning : NotificationKind.Info, 4000);
        }
        internal void StateChanged(string message, bool ready, bool paused, Settings settings, bool saveShortcutAvailable)
        {
            if (paused) { buffering = false; recovering = false; return; }
            if (ready)
            {
                if (!buffering) show(recovering ? "Replay recording recovered" : "Replay recording active", "Up to " + settings.ReplayMinutes + " minutes · " + (saveShortcutAvailable ? settings.HotkeyText + " to save" : "Save from the overlay"), NotificationKind.Success, 4000);
                buffering = true; recovering = false; return;
            }
            buffering = false;
            if (message.StartsWith("Recovering:"))
            {
                recovering = true;
                if (DateTime.UtcNow - lastRecovery > TimeSpan.FromMinutes(2)) { lastRecovery = DateTime.UtcNow; show("Recording is recovering", message.Substring("Recovering:".Length).Trim(), NotificationKind.Warning, 6000); }
            }
        }
        internal void CaptureChanged(CapturePhase phase, string message, bool ready, bool paused, Settings settings, bool saveShortcutAvailable)
        {
            if (phase == CapturePhase.WaitingForTarget || phase == CapturePhase.TargetMinimized || phase == CapturePhase.TargetClosed)
            {
                buffering = false;
                if (!paused && phase != lastTargetPhase) show(phase == CapturePhase.TargetMinimized ? "Selected window minimized" : phase == CapturePhase.TargetClosed ? "Selected window unavailable" : "Waiting for a window", message, NotificationKind.Warning, 6000);
                lastTargetPhase = phase; return;
            }
            lastTargetPhase = phase;
            StateChanged(phase == CapturePhase.Recovering && !message.StartsWith("Recovering:") ? "Recovering: " + message : message, ready, paused, settings, saveShortcutAvailable);
        }
        internal void SaveStarted() { show("Saving replay…", "Saving the available completed footage.", NotificationKind.Info, 0); }
        internal void SaveFinished(string message, bool success) { show(success ? "Replay saved" : "Replay not saved", success ? System.IO.Path.GetFileName(message) : message, success ? NotificationKind.Success : NotificationKind.Warning, success ? 4000 : 6000); }
    }
}
