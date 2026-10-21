namespace F10Recorder;

// Event subscribers observe a save; they do not own its lifetime or result.
internal sealed class ReplaySaveNotifications
{
    readonly object gate = new();
    Task tail = Task.CompletedTask;

    internal Task Observe(Task<ReplaySaveResult> completion, Action started, Action<string, bool> finished)
    {
        lock (gate)
        {
            var previous = tail;
            // Callers enqueue at admission. Slow or throwing observers cannot hold
            // the cache busy, but A's completion must precede B's start notice.
            return tail = Task.Run(async () =>
            {
                await previous.ConfigureAwait(false);
                Start(started);
                var result = await completion.ConfigureAwait(false);
                Finish(finished, result.Succeeded ? result.File : result.Error, result.Succeeded);
            });
        }
    }

    internal static void Start(Action observers)
    {
        if (observers == null) return;
        foreach (Action observer in observers.GetInvocationList())
            try { observer(); } catch (Exception ex) { Report(ex); }
    }

    internal static void Finish(Action<string, bool> observers, string message, bool success)
    {
        if (observers == null) return;
        foreach (Action<string, bool> observer in observers.GetInvocationList())
            try { observer(message, success); } catch (Exception ex) { Report(ex); }
    }

    static void Report(Exception ex)
    {
        try { Log.Write("Replay notification failed: " + ex); } catch (Exception) { }
    }
}
