using Microsoft.Win32.SafeHandles;

namespace F10Recorder;

// Only this recipe's three task-owned scratch names are admitted. Growing file
// allocation/EOF is always queried through ReplayFiles' handle-backed observer.
internal static class ReplayOutputWorkspace
{
    internal enum Member { Assembly, Boundary }
    internal static Member? Classify(string partial, string path)
        => path.Equals(AssemblyPath(partial), StringComparison.OrdinalIgnoreCase) ? Member.Assembly
            : path.Equals(BoundaryPath(partial, 0), StringComparison.OrdinalIgnoreCase)
                || path.Equals(BoundaryPath(partial, 1), StringComparison.OrdinalIgnoreCase) ? Member.Boundary : null;
    // Only construction owns these leases. Volume/unit/identity are established
    // once, never allocation/EOF. No-delete metadata handles pin each file
    // object and the directory; every observation still refreshes native
    // FileStandardInfo and verifies the current namespace and attributes.
    internal sealed class Reader(string partial) : IDisposable
    {
        sealed record Entry(SafeFileHandle Handle, ReplayFileExtent Identity);
        readonly Dictionary<string, Entry> handles = new(StringComparer.OrdinalIgnoreCase);
        SafeFileHandle anchor;
        string identity;
        internal Dictionary<string, ReplayFileExtent> Observe()
        {
            string directory = partial + ".work";
            if (!Directory.Exists(directory)) throw new IOException("Owned replay workspace disappeared.");
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 || Directory.GetDirectories(directory).Length != 0)
                throw new IOException("Replay workspace contains an unsupported directory or reparse point.");
            if (anchor == null) { anchor = Native.AnchorDirectory(directory); identity = Native.DirectoryIdentity(anchor); }
            if (Native.DirectoryIdentity(directory) != identity) throw new IOException("Replay workspace directory identity changed.");
            string[] paths = Directory.GetFiles(directory);
            if (paths.Length > 3 || handles.Keys.Any(path => !paths.Contains(path, StringComparer.OrdinalIgnoreCase)))
                throw new IOException("Replay workspace members changed outside construction.");
            var result = new Dictionary<string, ReplayFileExtent>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths)
            {
                if (Classify(partial, path) == null)
                    throw new IOException("Replay workspace contains an unregistered output.");
                if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Compressed | FileAttributes.SparseFile)) != 0)
                    throw new IOException("Replay workspace output has unsupported allocation attributes.");
                if (!handles.TryGetValue(path, out var entry))
                {
                    var initial = ReplayStorage.Observe(path);
                    var handle = ReplayStorage.OpenPinnedInventory(path);
                    try
                    {
                        var verified = ReplayStorage.ObserveInventory(handle, initial);
                        entry = new(handle, verified); handles.Add(path, entry);
                    }
                    catch { handle.Dispose(); throw; }
                }
                result.Add(path, ReplayStorage.RefreshInventoryAllocation(entry.Handle, entry.Identity));
            }
            if (Native.DirectoryIdentity(directory) != identity) throw new IOException("Replay workspace directory identity changed during observation.");
            return result;
        }
        public void Dispose()
        {
            foreach (var entry in handles.Values) entry.Handle.Dispose();
            handles.Clear(); anchor?.Dispose(); anchor = null;
        }
    }
    internal static string AssemblyPath(string partial) => Path.Combine(partial + ".work", "input.ts");
    internal static string BoundaryPath(string partial, int number) => Path.Combine(partial + ".work", "boundary-" + number.ToString("D6") + ".ts");
    internal static Dictionary<string, ReplayFileExtent> Observe(string partial, ReplayFiles files)
    {
        string directory = partial + ".work";
        var result = new Dictionary<string, ReplayFileExtent>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(directory))
        {
            if (File.Exists(directory)) throw new IOException("Replay workspace is not a directory.");
            return result;
        }
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 || Directory.GetDirectories(directory).Length != 0)
            throw new IOException("Replay workspace contains an unsupported directory or reparse point.");
        string[] paths = Directory.GetFiles(directory);
        if (paths.Length > 3) throw new IOException("Replay workspace exceeds its file envelope.");
        foreach (string path in paths)
        {
            if (Classify(partial, path) == null)
                throw new IOException("Replay workspace contains an unregistered output.");
            if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Compressed | FileAttributes.SparseFile)) != 0)
                throw new IOException("Replay workspace output has unsupported allocation attributes.");
            result.Add(path, ReplayStorage.Validate(files.Observe(path)));
        }
        return result;
    }
}
