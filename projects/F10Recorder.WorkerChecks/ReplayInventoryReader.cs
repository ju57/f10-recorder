using Microsoft.Win32.SafeHandles;

namespace F10Recorder;

// Reuse metadata handles, never allocation values. Every scan enumerates names
// and identities and refreshes actual allocation/EOF through each live handle.
// This is local to the experiment observer; ordinary replay inventory is unchanged.
internal class ReplayInventoryReader : ReplayFiles, IDisposable
{
    readonly List<string> roots;
    readonly Func<string, bool> mayRetain;
    internal ReplayInventoryReader(IEnumerable<string> metadataHandleRoots, Func<string, bool> mayRetain = null)
    { roots = metadataHandleRoots.Select(r => Path.GetFullPath(r).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar).ToList(); this.mayRetain = mayRetain; }
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
        { handles[path].Handle.Dispose(); handles.Remove(path); }
    }
    internal override ReplayFileExtent ObserveInventory(string path, ReplayFileExtent enumerated)
    {
        string full = Path.GetFullPath(path);
        if (!roots.Any(r => full.StartsWith(r, StringComparison.OrdinalIgnoreCase)) || mayRetain?.Invoke(path) == false)
        {
            if (handles.Remove(path, out var old)) old.Handle.Dispose();
            OpenCount++; return base.ObserveInventory(path, enumerated);
        }
        if (handles.TryGetValue(path, out var previous)
            && (!previous.Established.Volume.Equals(enumerated.Volume, StringComparison.OrdinalIgnoreCase)
                || previous.Established.Identity != enumerated.Identity || previous.Established.AllocationUnit != enumerated.AllocationUnit))
        { previous.Handle.Dispose(); handles.Remove(path); }
        if (!handles.TryGetValue(path, out var entry))
        {
            var handle = ReplayStorage.OpenInventory(path); OpenCount++;
            try
            {
                var observed = ReplayStorage.ObserveInventory(handle, enumerated);
                handles.Add(path, new(handle, observed)); seen.Add(path); return observed;
            }
            catch { handle.Dispose(); throw; }
        }
        seen.Add(path);
        return ReplayStorage.RefreshInventoryAllocation(entry.Handle, entry.Established);
    }
    internal void ReleaseHandles()
    {
        foreach (var entry in handles.Values) entry.Handle.Dispose();
        handles.Clear(); seen.Clear();
    }
    public void Dispose() => ReleaseHandles();
}
