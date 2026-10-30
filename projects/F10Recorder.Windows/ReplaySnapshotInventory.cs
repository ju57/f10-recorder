using Microsoft.Win32.SafeHandles;

namespace F10Recorder;

// Only sealed, task-owned export snapshots enter this reader. Reuse the verified
// file object, never its allocation/EOF: both are refreshed on every inventory.
// Growing acquisition files and atomically replaced metadata keep short leases.
internal sealed class ReplaySnapshotInventory : ReplayFiles, IDisposable
{
    const int MaximumHandles = 4096;
    sealed record Entry(SafeFileHandle Handle, ReplayFileExtent Identity);
    readonly Dictionary<string, Entry> handles = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> snapshots = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
    internal int HandleCount => handles.Count;
    internal void Seal(string directory) => snapshots.Add(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
    internal void BeginScan() => seen.Clear();
    internal void EndScan()
    {
        foreach (string path in handles.Keys.Where(p => !seen.Contains(p)).ToArray()) Release(path);
    }
    void Release(string path)
    { if (handles.Remove(path, out var entry)) entry.Handle.Dispose(); }
    internal void ReleaseUnder(string path)
    {
        string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar), prefix = full + Path.DirectorySeparatorChar;
        foreach (string name in handles.Keys.Where(p => p.Equals(full, StringComparison.OrdinalIgnoreCase) || p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray()) Release(name);
        snapshots.RemoveWhere(p => p.Equals(prefix, StringComparison.OrdinalIgnoreCase) || p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }
    internal override ReplayFileExtent ObserveInventory(string path, ReplayFileExtent enumerated)
    {
        string full = Path.GetFullPath(path);
        if (!full.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) || !snapshots.Any(p => full.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            return base.ObserveInventory(path, enumerated);
        if (handles.TryGetValue(full, out var previous) && (previous.Identity.Volume != enumerated.Volume
            || previous.Identity.Identity != enumerated.Identity || previous.Identity.AllocationUnit != enumerated.AllocationUnit)) Release(full);
        if (!handles.TryGetValue(full, out var entry))
        {
            if (handles.Count >= MaximumHandles) return base.ObserveInventory(path, enumerated);
            var handle = ReplayStorage.OpenInventory(path);
            try
            {
                var verified = ReplayStorage.ObserveInventory(handle, enumerated);
                handles.Add(full, new(handle, verified)); seen.Add(full); return verified;
            }
            catch { handle.Dispose(); throw; }
        }
        seen.Add(full);
        return ReplayStorage.RefreshInventoryAllocation(entry.Handle, entry.Identity);
    }
    public void Dispose()
    {
        foreach (var entry in handles.Values) entry.Handle.Dispose();
        handles.Clear(); snapshots.Clear(); seen.Clear();
    }
}
