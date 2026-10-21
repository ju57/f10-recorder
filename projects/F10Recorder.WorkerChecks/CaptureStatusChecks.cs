using System.Text.Json;

namespace F10Recorder;

// The production status and owner-dispatch seams, with controlled process liveness.
// This route never constructs a recorder, CaptureSession, window or encoder.
internal static class CaptureStatusChecks
{
    static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    internal static int Run(string root)
    {
        Directory.CreateDirectory(root);
        var checks = new List<object>();
        int failed = 0;
        void Check(string name, Action body)
        {
            try { body(); checks.Add(new { name, result = "passed" }); }
            catch (Exception ex) { failed++; checks.Add(new { name, result = "failed", error = ex.ToString() }); }
        }
        Check("delayed-ready-after-pause", () =>
        {
            var capture = new CaptureLifecycle(false, _ => { });
            var queue = new Queue<Action>();
            var owner = new RecorderDispatch(queue.Enqueue);
            var dispatch = owner.NextGeneration();
            CaptureStatus delivered = null;
            capture.CaptureStatusChanged += () => dispatch(() => delivered = capture.ReadCaptureStatus());
            Require(capture.TryBeginAttempt("Starting", out var attempt), "Capture did not start.");
            capture.AttachProcess(attempt, () => true);
            capture.Publish(attempt, CapturePhase.Buffering, "Ready", true);
            capture.Pause(true);
            queue.Dequeue()();
            Require(delivered.Phase == CapturePhase.Paused && !delivered.Ready, "A queued update revived readiness after pause.");
        });
        Check("read-rejects-exited-process-but-cannot-restore-readiness", () =>
        {
            bool alive = true;
            var capture = new CaptureLifecycle(false, _ => { });
            capture.TryBeginAttempt("Starting", out var attempt);
            capture.AttachProcess(attempt, () => alive);
            capture.Publish(attempt, CapturePhase.Buffering, "Advancing", true);
            Require(capture.ReadCaptureStatus().Ready, "Supervisor did not establish readiness.");
            alive = false;
            Require(!capture.ReadCaptureStatus().Ready, "Read advertised readiness after process exit.");
            alive = true;
            Require(!capture.ReadCaptureStatus().Ready, "Process liveness restored readiness without supervisor evidence.");
            capture.Publish(attempt, CapturePhase.Buffering, "Fresh advancing evidence", true);
            Require(capture.ReadCaptureStatus().Ready, "Fresh supervisor evidence was rejected.");
        });
        Check("retired-attempt-cannot-publish-after-restart", () =>
        {
            var capture = new CaptureLifecycle(false, _ => { });
            capture.TryBeginAttempt("Starting", out var old);
            capture.AttachProcess(old, () => true);
            capture.Publish(old, CapturePhase.Buffering, "Old ready", true);
            capture.Restart();
            Require(!capture.ReadCaptureStatus().Ready, "Restart did not immediately clear readiness.");
            capture.TryBeginAttempt("New attempt", out var current);
            capture.AttachProcess(current, () => true);
            capture.Publish(current, CapturePhase.Buffering, "Current ready", true);
            capture.Publish(old, CapturePhase.Recovering, "Old error", false);
            capture.Publish(old, CapturePhase.Buffering, "Old ready", true);
            Require(capture.ReadCaptureStatus().Message == "Current ready", "Retired attempt replaced current status.");
        });
        Check("source-loss-rebind-and-shutdown-reject-late-publication", () =>
        {
            var capture = new CaptureLifecycle(false, _ => { });
            capture.TryBeginAttempt("Starting", out var old);
            capture.AttachProcess(old, () => true);
            capture.Publish(old, CapturePhase.Buffering, "Ready", true);
            capture.SourceUnavailable(CapturePhase.TargetMinimized, "Minimized");
            capture.Publish(old, CapturePhase.Buffering, "Retained process must not revive capture", true);
            Require(capture.ReadCaptureStatus().Phase == CapturePhase.TargetMinimized, "Retained source revived capture.");
            capture.TryBeginAttempt("Rebinding", out var rebound);
            capture.AttachProcess(rebound, () => true);
            capture.Publish(rebound, CapturePhase.Buffering, "Rebound", true);
            Require(capture.ReadCaptureStatus().Ready, "Fresh rebound was rejected.");
            capture.Close();
            capture.Publish(rebound, CapturePhase.Recovering, "Late error", false);
            Require(capture.ReadCaptureStatus().Phase == CapturePhase.Stopped && !capture.ReadCaptureStatus().Ready, "Shutdown accepted a late update.");
            Require(!capture.TryBeginAttempt("Too late", out _), "Shutdown admitted a new attempt.");
        });
        Check("transition-history-survives-delayed-delivery-without-progress-spam", () =>
        {
            var log = new List<string>();
            var capture = new CaptureLifecycle(false, log.Add);
            capture.TryBeginAttempt("Starting", out var attempt);
            capture.AttachProcess(attempt, () => true);
            capture.Publish(attempt, CapturePhase.Buffering, "1 second", true);
            Require(log.Count > 0, "Capture transitions were not logged.");
            int before = log.Count;
            for (int i = 2; i < 20; i++) capture.Publish(attempt, CapturePhase.Buffering, i + " seconds", true);
            Require(log.Count == before, "Buffer duration updates flooded transition history.");
            capture.Publish(attempt, CapturePhase.Recovering, "Temporary failure", false);
            capture.Publish(attempt, CapturePhase.Buffering, "Recovered", true);
            Require(log.Count == before + 2, "Brief recovery history was lost.");
        });
        Check("reset-with-unavailable-source-and-newer-command", () =>
        {
            var capture = new CaptureLifecycle(false, _ => { });
            capture.Restart(); var request = capture.ResetRequest;
            capture.CompleteReset(request);
            capture.SourceUnavailable(CapturePhase.WaitingForTarget, "No window");
            Require(capture.ReadCaptureStatus().Phase == CapturePhase.WaitingForTarget, "Completed reset hid source loss.");
            capture.Restart(); request = capture.ResetRequest;
            capture.Pause(true);
            capture.CompleteReset(request);
            Require(capture.RestartPending && capture.ReadCaptureStatus().Phase == CapturePhase.Paused, "Old cleanup acknowledged a newer command.");
        });
        Check("typed-recovery-notification-does-not-parse-message", () =>
        {
            var titles = new List<string>();
            var notices = new RecorderNotifications((title, _, _, _) => titles.Add(title));
            notices.CaptureChanged(CapturePhase.Recovering, "Encoder exited", false, false, new Settings(), true);
            notices.CaptureChanged(CapturePhase.Buffering, "Advancing", true, false, new Settings(), true);
            Require(titles.SequenceEqual(new[] { "Recording is recovering", "Replay recording recovered" }), "Typed recovery lost its notices.");
            titles.Clear();
            notices = new RecorderNotifications((title, _, _, _) => titles.Add(title));
            notices.CaptureChanged(CapturePhase.Starting, "Recovering: arbitrary display text", false, false, new Settings(), true);
            Require(titles.Count == 0, "English display text changed the notification phase.");
        });
        void Delayed(string name, Action<CaptureLifecycle> invalidate, CapturePhase expected)
        {
            Check(name, () =>
            {
                var capture = new CaptureLifecycle(false, _ => { });
                var queue = new Queue<Action>();
                var dispatch = new RecorderDispatch(queue.Enqueue).NextGeneration();
                CaptureStatus delivered = null;
                capture.CaptureStatusChanged += () => dispatch(() => delivered = capture.ReadCaptureStatus());
                capture.TryBeginAttempt("Starting", out var attempt); capture.AttachProcess(attempt, () => true);
                queue.Clear();
                capture.Publish(attempt, CapturePhase.Buffering, "Ready", true);
                invalidate(capture);
                queue.Dequeue()(); // Deliver the notification queued by Ready itself.
                Require(delivered.Phase == expected && !delivered.Ready, "Delayed Ready replaced the current state.");
                capture.Publish(attempt, CapturePhase.Starting, "Late starting", false);
                capture.Publish(attempt, CapturePhase.Buffering, "Late ready", true);
                while (queue.TryDequeue(out var next)) next();
                Require(delivered.Phase == expected && !delivered.Ready, "Late publication replaced current status.");
            });
        }
        Delayed("delayed-ready-after-pause-resume", c => { c.Pause(true); c.Pause(false); }, CapturePhase.Starting);
        Delayed("delayed-ready-after-restart", c => c.Restart(), CapturePhase.Starting);
        Delayed("delayed-ready-after-source-loss", c => c.SourceUnavailable(CapturePhase.TargetClosed, "Closed"), CapturePhase.TargetClosed);
        Delayed("delayed-ready-after-shutdown", c => c.Close(), CapturePhase.Stopped);
        Check("old-engine-and-disposed-owner-do-not-even-read-status", () =>
        {
            var queue = new Queue<Action>(); var owner = new RecorderDispatch(queue.Enqueue);
            int reads = 0;
            var old = owner.NextGeneration(); old(() => reads += 100);
            var current = owner.NextGeneration(); current(() => reads++);
            while (queue.TryDequeue(out var next)) next();
            Require(reads == 1, "An old engine callback was consumed.");
            current(() => reads++); owner.Dispose();
            while (queue.TryDequeue(out var next)) next();
            Require(reads == 1, "A disposed owner consumed a callback.");
        });
        Check("late-initialization-and-cleanup-cannot-touch-replacement", () =>
        {
            var capture = new CaptureLifecycle(false, _ => { });
            capture.TryBeginAttempt("Old initialization", out var old);
            capture.Restart();
            Require(!capture.AttachProcess(old, () => true), "Retired initialization attached its process.");
            capture.TryBeginAttempt("New initialization", out var current);
            capture.AttachProcess(current, () => true);
            Require(!capture.ReadCaptureStatus().Ready, "Liveness established readiness before supervisor evidence.");
            capture.Publish(current, CapturePhase.Buffering, "Current", true);
            capture.EndAttempt(old);
            capture.Publish(old, CapturePhase.Recovering, "Old initialization failed", false);
            Require(capture.ReadCaptureStatus().Ready && capture.ReadCaptureStatus().Message == "Current", "Old attempt altered replacement status.");
        });
        Check("process-exit-at-delivery-logs-once-and-probe-failure-is-nonready", () =>
        {
            var log = new List<string>(); bool alive = true;
            var capture = new CaptureLifecycle(false, log.Add);
            var queue = new Queue<Action>(); var dispatch = new RecorderDispatch(queue.Enqueue).NextGeneration();
            CaptureStatus delivered = null;
            capture.CaptureStatusChanged += () => dispatch(() => delivered = capture.ReadCaptureStatus());
            capture.TryBeginAttempt("Starting", out var attempt); capture.AttachProcess(attempt, () => alive);
            queue.Clear(); capture.Publish(attempt, CapturePhase.Buffering, "Ready", true);
            int before = log.Count; alive = false;
            while (queue.TryDequeue(out var next)) next();
            for (int i = 0; i < 10; i++) Require(!capture.ReadCaptureStatus().Ready, "Read revived readiness.");
            Require(!delivered.Ready && log.Count == before + 1, "Exit was missed or logged repeatedly.");
            capture.TryBeginAttempt("Retry", out attempt);
            capture.AttachProcess(attempt, () => throw new System.ComponentModel.Win32Exception("Unavailable process"));
            capture.Publish(attempt, CapturePhase.Buffering, "Cannot verify", true);
            Require(!capture.ReadCaptureStatus().Ready, "Probe failure advertised readiness.");
        });
        Check("latest-status-skips-ended-recovery-but-retains-history", () =>
        {
            var titles = new List<string>(); var log = new List<string>(); var queue = new Queue<Action>();
            var capture = new CaptureLifecycle(false, log.Add);
            var notices = new RecorderNotifications((title, _, _, _) => titles.Add(title));
            var dispatch = new RecorderDispatch(queue.Enqueue).NextGeneration();
            capture.CaptureStatusChanged += () => dispatch(() =>
            {
                var status = capture.ReadCaptureStatus();
                notices.CaptureChanged(status.Phase, status.Message, status.Ready, false, new Settings(), true);
            });
            capture.TryBeginAttempt("Starting", out var attempt); capture.AttachProcess(attempt, () => true);
            capture.Publish(attempt, CapturePhase.Recovering, "Brief error", false);
            capture.Publish(attempt, CapturePhase.Buffering, "Advancing", true);
            while (queue.TryDequeue(out var next)) next();
            Require(titles.SequenceEqual(new[] { "Replay recording active" }), "Ended recovery appeared after queue catch-up or readiness notice repeated.");
            Require(log.Any(line => line.Contains("phase=Recovering")), "Skipped recovery disappeared from history.");
        });
        Check("pause-confirmations-and-ordered-save-results-remain-events", () =>
        {
            var queue = new System.Collections.Concurrent.ConcurrentQueue<Action>();
            var dispatch = new RecorderDispatch(queue.Enqueue).NextGeneration();
            var titles = new List<string>(); var notices = new RecorderNotifications((title, _, _, _) => titles.Add(title));
            var capture = new CaptureLifecycle(false, _ => { });
            capture.CaptureStatusChanged += () => dispatch(() => capture.ReadCaptureStatus());
            var saves = new ReplaySaveNotifications();
            var completed = new ReplaySaveResult(true, "saved.mp4", null, 2000, 1);
            var a = saves.Observe(Task.FromResult(completed), () => dispatch(notices.SaveStarted), (m, ok) => dispatch(() => notices.SaveFinished(m, ok)));
            var b = saves.Observe(Task.FromResult(completed), () => dispatch(notices.SaveStarted), (m, ok) => dispatch(() => notices.SaveFinished(m, ok)));
            capture.Pause(true); dispatch(() => notices.PauseChanged(true));
            capture.Pause(false); dispatch(() => notices.PauseChanged(false));
            Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            while (queue.TryDequeue(out var next)) next();
            Require(titles.Where(t => t == "Saving replay…" || t == "Replay saved").SequenceEqual(new[] { "Saving replay…", "Replay saved", "Saving replay…", "Replay saved" }), "Save events were coalesced or reordered.");
            Require(titles.Contains("Replay recording paused") && titles.Contains("Starting replay recording"), "Pause confirmations were coalesced.");
        });
        File.WriteAllText(Path.Combine(root, "verification.json"), JsonSerializer.Serialize(new
        {
            result = failed == 0 ? "passed" : "failed", checks,
            effects = new { ui = false, capture = false, gpu = false, engineLaunch = false, startupWrites = false }
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Capture status: {checks.Count - failed}/{checks.Count} passed. {root}");
        return failed == 0 ? 0 : 1;
    }
}
