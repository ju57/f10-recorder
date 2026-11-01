using Microsoft.Win32.SafeHandles;

namespace F10Recorder;

// Reuse metadata handles, never allocation values. Every scan enumerates names
// and identities and refreshes actual allocation/EOF through each live handle.
// This is local to the experiment observer; ordinary replay inventory is unchanged.
internal class ReplayInventoryReader : ReplayFiles, IDisposable
{
    readonly List<string> roots;
    readonly Func<string, bool> mayRetain;
    readonly bool reuseRenamedOutputs;
    internal Action<string, string, double> OperationTimed;
    internal Action<string, ReplayFileExtent> OutputObserved;
    internal ReplayInventoryReader(IEnumerable<string> metadataHandleRoots, Func<string, bool> mayRetain = null, bool reuseRenamedOutputs = false)
    { roots = metadataHandleRoots.Select(r => Path.GetFullPath(r).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar).ToList(); this.mayRetain = mayRetain; this.reuseRenamedOutputs = reuseRenamedOutputs; }
    T Timed<T>(string path, string operation, Func<T> action)
    {
        long at = System.Diagnostics.Stopwatch.GetTimestamp();
        try { return action(); }
        finally { if (path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) OperationTimed?.Invoke(path, operation, System.Diagnostics.Stopwatch.GetElapsedTime(at).TotalMilliseconds); }
    }
    void Close(string path, Entry entry) => Timed(path, "close", () => { entry.Handle.Dispose(); return true; });
    ReplayFileExtent Report(string path, ReplayFileExtent extent)
    { if (path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) OutputObserved?.Invoke(path, extent); return extent; }
    internal bool Retains(string path) => handles.ContainsKey(path);
    internal void RetainHandlesUnder(string root)
    {
        string full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!roots.Contains(full, StringComparer.OrdinalIgnoreCase)) roots.Add(full);
    }
    sealed record Entry(SafeFileHandle Handle, ReplayFileExtent Established);
    readonly Dictionary<string, Entry> handles = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
    internal int OpenCount { get; private set; }
    internal int HandleCount => handles.Count;

    internal void BeginScan() { seen.Clear(); OpenCount = 0; }
    internal void EndScan()
    {
        foreach (string path in handles.Keys.Where(p => !seen.Contains(p)).ToArray())
        { Close(path, handles[path]); handles.Remove(path); }
    }
    internal override ReplayFileExtent ObserveInventory(string path, ReplayFileExtent enumerated)
    {
        string full = Path.GetFullPath(path);
        if (!roots.Any(r => full.StartsWith(r, StringComparison.OrdinalIgnoreCase)) || mayRetain?.Invoke(path) == false)
        {
            if (handles.Remove(path, out var old)) Close(path, old);
            var handle = Timed(path, "open", () => ReplayStorage.OpenInventory(path)); OpenCount++;
            try { return Report(path, Timed(path, "observe", () => ReplayStorage.ObserveInventory(handle, enumerated))); }
            finally { Timed(path, "close", () => { handle.Dispose(); return true; }); }
        }
        if (handles.TryGetValue(path, out var previous)
            && (!previous.Established.Volume.Equals(enumerated.Volume, StringComparison.OrdinalIgnoreCase)
                || previous.Established.Identity != enumerated.Identity || previous.Established.AllocationUnit != enumerated.AllocationUnit))
        { Close(path, previous); handles.Remove(path); }
        if (!handles.ContainsKey(path) && reuseRenamedOutputs && path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            var alias = handles.FirstOrDefault(item => !seen.Contains(item.Key) && item.Key.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
                && item.Value.Established.Volume == enumerated.Volume && item.Value.Established.Identity == enumerated.Identity
                && item.Value.Established.AllocationUnit == enumerated.AllocationUnit);
            if (alias.Key != null) { handles.Remove(alias.Key); handles.Add(path, alias.Value); }
        }
        if (!handles.TryGetValue(path, out var entry))
        {
            var handle = Timed(path, "open", () => ReplayStorage.OpenInventory(path)); OpenCount++;
            try
            {
                var observed = Timed(path, "observe", () => ReplayStorage.ObserveInventory(handle, enumerated));
                handles.Add(path, new(handle, observed)); seen.Add(path); return Report(path, observed);
            }
            catch { Timed(path, "close", () => { handle.Dispose(); return true; }); throw; }
        }
        seen.Add(path);
        return Report(path, Timed(path, "refresh", () => ReplayStorage.RefreshInventoryAllocation(entry.Handle, entry.Established)));
    }
    internal void ReleaseHandles()
    {
        foreach (var entry in handles) Close(entry.Key, entry.Value);
        handles.Clear(); seen.Clear();
    }
    internal void ReleaseOutputHandles()
    {
        foreach (string path in handles.Keys.Where(p => p.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)).ToArray())
        { Close(path, handles[path]); handles.Remove(path); }
    }
    public void Dispose() => ReleaseHandles();
}
