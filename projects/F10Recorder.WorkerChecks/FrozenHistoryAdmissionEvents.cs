using System.Diagnostics;

namespace F10Recorder;

// Admission-only diagnostics. The native sticky history guard remains the
// authority; these observers are disposed before any fixture or media starts.
internal sealed class FrozenHistoryAdmissionEvents : IDisposable
{
    readonly object gate = new();
    readonly List<FileSystemWatcher> watchers = new();
    readonly List<object> events = new();
    readonly DateTime startedUtc = DateTime.UtcNow;
    readonly long startedQpc = Stopwatch.GetTimestamp();
    int count;
    bool completed;
    bool accepting = true;
    internal FrozenHistoryAdmissionEvents(string[] roots)
    {
        try
        {
            if (roots.Length is < 1 or > 37) throw new IOException("History diagnostic root count bound.");
            foreach (string root in roots)
            {
                var watcher = new FileSystemWatcher(root) { IncludeSubdirectories = true,
                    NotifyFilter = (NotifyFilters)0x11f, InternalBufferSize = 16384 };
                watchers.Add(watcher);
                void Record(string action, string path, string old = null)
                {
                    lock (gate)
                    {
                        if (!accepting) return;
                        count++;
                        if (events.Count < 32) events.Add(new { utc = DateTime.UtcNow, qpc = Stopwatch.GetTimestamp(),
                            root = ReplayOutputDemand.Bounded(root, 512), action = ReplayOutputDemand.Bounded(action, 256),
                            path = ReplayOutputDemand.Bounded(path, 512), old = ReplayOutputDemand.Bounded(old, 512) });
                    }
                }
                watcher.Changed += (_, e) => Record("changed", e.FullPath);
                watcher.Created += (_, e) => Record("created", e.FullPath);
                watcher.Deleted += (_, e) => Record("deleted", e.FullPath);
                watcher.Renamed += (_, e) => Record("renamed", e.FullPath, e.OldFullPath);
                watcher.Error += (_, e) => Record("watch-error:" + e.GetException().Message, root);
                watcher.EnableRaisingEvents = true;
            }
        }
        catch (Exception error)
        {
            Dispose();
            throw new IOException("Cannot arm bounded historical admission diagnostics.", error);
        }
    }
    internal void Complete(Action<object> report, string nativeFailure)
    {
        if (completed) return;
        completed = true;
        Dispose();
        lock (gate)
        {
            report(new { startedUtc, startedQpc, completedUtc = DateTime.UtcNow, completedQpc = Stopwatch.GetTimestamp(),
                frequency = Stopwatch.Frequency, nativeFailure, eventCount = count, omitted = Math.Max(0, count - events.Count), events,
                scope = "Admission-only supplemental subtree0x11f notifications. Native leases/sticky guard unchanged. No observer survives into capture; an empty log cannot explain an earlier failed run." });
            if (nativeFailure == null && count != 0)
                throw new IOException("Detailed historical change notification during admission; preserve history-admission-events.json.");
        }
    }
    public void Dispose()
    {
        lock (gate) accepting = false;
        foreach (var watcher in watchers) watcher.Dispose();
        watchers.Clear();
    }
}
