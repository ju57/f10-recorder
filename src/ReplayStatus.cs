using System;

namespace F10Recorder
{
    public enum CapturePhase { Unknown, Starting, Buffering, Recovering, WaitingForTarget, TargetMinimized, TargetClosed, Paused, Reconnecting, Applying, Stopped }

    // Stable user intent belongs in Settings. These identities are session-only.
    internal sealed class WindowTargetSnapshot
    {
        internal long Handle, ProcessStartedUtcTicks, Generation;
        internal int ProcessId;
        internal string ApplicationName;
        internal CapturePhase Phase;
        internal string Reason;
        internal bool Available { get { return Phase == CapturePhase.Starting && Handle != 0; } }
    }

    internal sealed class ReplayPresentation
    {
        internal string Heading, Detail;
        internal bool Healthy, CanSave;
        internal static ReplayPresentation Describe(CapturePhase phase, string reason, bool paused, bool ready, bool canSave, bool applying, bool saving, bool shortcutAvailable, double seconds)
        {
            var result = new ReplayPresentation { CanSave = canSave && !paused && !applying && !saving };
            if (phase == CapturePhase.Reconnecting || phase == CapturePhase.Unknown)
            {
                result.Heading = "Recorder unavailable"; result.Detail = "Checking the worker connection. Recording readiness cannot be confirmed."; result.CanSave = false;
            }
            else if (applying) { result.Heading = "Applying recording settings"; result.Detail = "A new replay buffer will start after the settings change."; result.CanSave = false; }
            else if (paused) { result.Heading = "Replay paused"; result.Detail = "Buffering is off until you choose Resume. Saved clips are kept."; result.CanSave = false; }
            else if (ready && phase == CapturePhase.Buffering)
            {
                result.Heading = "Replay recording active"; result.Detail = "About " + Math.Max(0, (int)seconds) + " seconds of completed footage available."; result.Healthy = true;
            }
            else
            {
                switch (phase)
                {
                    case CapturePhase.WaitingForTarget: result.Heading = "Waiting for a window"; break;
                    case CapturePhase.TargetMinimized: result.Heading = "Selected window minimized"; break;
                    case CapturePhase.TargetClosed: result.Heading = "Selected window unavailable"; break;
                    case CapturePhase.Recovering: result.Heading = "Recording is recovering"; break;
                    case CapturePhase.Stopped: result.Heading = "Recorder stopped"; result.CanSave = false; break;
                    default: result.Heading = "Starting replay recording"; break;
                }
                result.Detail = string.IsNullOrWhiteSpace(reason) ? "Waiting for completed footage. Replay is enabled." : reason;
                if (result.CanSave) result.Detail += " Previously buffered footage can still be saved.";
            }
            if (saving) result.Detail = "Saving the available replay footage. " + (ready ? "Recording continues." : "No new footage is currently being buffered.");
            if (ready && !shortcutAvailable) result.Detail += " Save shortcut unavailable; use Save replay here.";
            return result;
        }
    }
}
