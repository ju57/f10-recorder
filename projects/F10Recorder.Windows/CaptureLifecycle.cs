namespace F10Recorder;

internal sealed record CaptureStatus(CapturePhase Phase, bool Ready, string Message);
internal readonly record struct CaptureAttempt(long Value);

// Capture supervision and status consumers cross this same seam. Replay storage
// may outlive an attempt; neither its session id nor a retained PID grants readiness.
internal sealed class CaptureLifecycle
{
    readonly object gate = new();
    readonly Action<string> diagnostic;
    CaptureStatus status;
    long generation, transition;
    bool paused, closing, restart, active;
    Func<bool> processAlive;
    internal event Action CaptureStatusChanged;
    internal CaptureLifecycle(bool paused, Action<string> diagnostic = null)
    {
        this.paused = paused;
        this.diagnostic = diagnostic ?? Log.Write;
        status = new(paused ? CapturePhase.Paused : CapturePhase.Starting, false,
            paused ? "Paused" : "Starting replay buffer…");
    }
    internal bool Paused { get { lock (gate) return paused; } }
    internal bool Closing { get { lock (gate) return closing; } }
    internal bool RestartPending { get { lock (gate) return restart; } }
    internal long ResetRequest { get { lock (gate) return restart ? generation : 0; } }
    internal void CompleteReset(long request)
    {
        lock (gate) if (request != 0 && generation == request) restart = false;
    }
    bool Current(CaptureAttempt attempt) => active && !paused && !closing && attempt.Value == generation;
    internal bool IsCurrent(CaptureAttempt attempt) { lock (gate) return Current(attempt); }

    internal bool TryBeginAttempt(string message, out CaptureAttempt attempt)
    {
        string log;
        lock (gate)
        {
            attempt = default;
            if (paused || closing) return false;
            attempt = new(++generation); active = true; restart = false; processAlive = null;
            log = Set(new(CapturePhase.Starting, false, message), "begin attempt");
        }
        Notify(log); return true;
    }
    internal bool AttachProcess(CaptureAttempt attempt, Func<bool> alive)
    {
        lock (gate)
        {
            if (!Current(attempt)) return false;
            processAlive = alive; return true;
        }
    }
    // Only called by the supervisor after its existing health/progress checks.
    internal void Publish(CaptureAttempt attempt, CapturePhase phase, string message, bool ready)
    {
        string log;
        lock (gate)
        {
            if (!Current(attempt)) return;
            if (ready && (phase != CapturePhase.Buffering || !ProcessAlive())) return;
            log = Set(new(phase, ready, message));
        }
        Notify(log);
    }
    internal void EndAttempt(CaptureAttempt attempt)
    {
        string log;
        lock (gate)
        {
            if (!Current(attempt)) return;
            Invalidate();
            log = Set(status.Ready ? new(CapturePhase.Starting, false, "Restarting replay buffer…") : status, "end attempt");
        }
        Notify(log);
    }
    internal void SourceUnavailable(CapturePhase phase, string message)
    {
        string log;
        lock (gate)
        {
            // A source observation cannot overwrite a newer owner command.
            if (paused || closing || restart) return;
            bool invalidated = active;
            if (invalidated) Invalidate();
            log = Set(new(phase, false, message), invalidated ? "source unavailable" : null);
        }
        Notify(log);
    }
    internal void Close()
    {
        string log;
        lock (gate)
        {
            if (closing) return;
            closing = true; Invalidate();
            log = Set(new(CapturePhase.Stopped, false, "Recorder is stopping."), "shutdown");
        }
        Notify(log);
    }
    internal void Restart()
    {
        string log;
        lock (gate)
        {
            if (closing) return;
            restart = true; Invalidate();
            log = Set(new(paused ? CapturePhase.Paused : CapturePhase.Starting, false,
                paused ? "Paused" : "Restarting replay buffer…"), "restart requested");
        }
        Notify(log);
    }
    internal void Pause(bool value)
    {
        string log;
        lock (gate)
        {
            if (closing || paused == value) return;
            paused = value; restart = true; Invalidate();
            log = Set(new(value ? CapturePhase.Paused : CapturePhase.Starting, false,
                value ? "Paused" : "Starting replay buffer…"), value ? "pause requested" : "resume requested");
        }
        Notify(log);
    }
    void Invalidate() { generation++; active = false; processAlive = null; }
    bool ProcessAlive()
    {
        try { return processAlive != null && processAlive(); }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }
    internal CaptureStatus ReadCaptureStatus()
    {
        CaptureStatus result; string log = null;
        lock (gate)
        {
            // This read may reject readiness, never establish it. A later live
            // process still needs fresh supervisor evidence before becoming ready.
            if (status.Ready && (!active || !ProcessAlive()))
                log = Set(new(CapturePhase.Recovering, false, "Capture process is no longer running."), "process unavailable at status read");
            result = status;
        }
        if (log != null) Notify(log);
        return result;
    }
    string Set(CaptureStatus next, string reason = null)
    {
        bool meaningful = reason != null || status.Phase != next.Phase || status.Ready != next.Ready;
        status = next;
        return meaningful ? $"Capture transition {++transition}; attempt={generation}; phase={next.Phase}; ready={next.Ready}; reason={reason ?? next.Message}" : null;
    }
    void Notify(string log)
    {
        // Diagnostics and subscriber callbacks never execute under the state lock.
        if (log != null) Report(log);
        var observers = CaptureStatusChanged;
        if (observers == null) return;
        foreach (Action observer in observers.GetInvocationList())
            try { observer(); } catch (Exception ex) { Report("Capture status observer failed: " + ex); }
    }
    void Report(string message) { try { diagnostic(message); } catch (Exception) { } }
}
