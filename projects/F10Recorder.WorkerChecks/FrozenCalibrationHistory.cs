using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace F10Recorder;

// Archival roots have no remaining task writers. Data-read leases deny writes
// and deletion through every alias; kernel change notifications invalidate any
// addition/rename/error. Charge their freshly observed native allocation once,
// instead of re-reading this immutable archive inside every live500ms scan.
// Native contract: https://learn.microsoft.com/windows/win32/api/fileapi/nf-fileapi-findfirstchangenotificationw
// Root-directory leases also prevent deletion/rename of the watched root itself.
internal sealed class FrozenCalibrationHistory : IDisposable
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr FindFirstChangeNotificationW(string path, [MarshalAs(UnmanagedType.Bool)] bool subtree, uint filter);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool FindCloseChangeNotification(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint WaitForSingleObject(ChangeNotification handle, uint milliseconds);
    sealed class ChangeNotification : SafeHandleZeroOrMinusOneIsInvalid
    {
        ChangeNotification(IntPtr value) : base(true) { SetHandle(value); }
        internal static ChangeNotification Create(string path)
        {
            var handle = new ChangeNotification(FindFirstChangeNotificationW(NativePath(path), true, 0x11f));
            if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error, path); }
            return handle;
        }
        protected override bool ReleaseHandle() => FindCloseChangeNotification(handle);
        // Never re-arm: any event permanently invalidates the archived snapshot.
        // Synchronous zero-time waits avoid managed callback/buffer delays.
        internal uint State => WaitForSingleObject(this, 0);
    }
    readonly List<SafeFileHandle> leases = new();
    readonly List<ChangeNotification> watchers = new();
    readonly Dictionary<string, long> allocations = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, ReplayFileExtent> inventory;
    string invalidation;
    int disposed;
    internal sealed record Volume(string Directory, string Identity, long AllocationUnit);
    internal readonly List<Volume> Volumes = new();
    internal int FileCount => inventory.Count;
    internal int DirectoryCount { get; }
    internal long AllocatedBytes { get; }
    internal IReadOnlyDictionary<string, ReplayFileExtent> Inventory => inventory;
    static string Key(ReplayFileExtent extent) => extent.Volume + ":" + extent.Identity;

    internal FrozenCalibrationHistory(string[] roots, int fileLimit, int directoryLimit)
    {
        try
        {
            var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in roots.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                void LeaseDirectory(string path)
                {
                    if (!directories.Add(path)) return;
                    if (directories.Count > directoryLimit || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Frozen history directory bound or reparse validation failed.");
                    var handle = Open(path, directory: true);
                    leases.Add(handle);
                    if (Native.DirectoryIdentity(handle) != Native.DirectoryIdentity(path))
                        throw new IOException("Frozen history directory changed before its lease.");
                    foreach (string child in Directory.GetDirectories(path)) LeaseDirectory(child);
                }
                // Watch first: a concurrent namespace mutation cannot disappear
                // between inventory and the beginning of supervision.
                watchers.Add(ChangeNotification.Create(root));
                LeaseDirectory(root);
                var capacity = ReplayStorage.ObserveCapacity(root);
                if (!Volumes.Any(v => v.Identity.Equals(capacity.Volume, StringComparison.OrdinalIgnoreCase)))
                    Volumes.Add(new(root, capacity.Volume, capacity.AllocationUnit));
            }
            DirectoryCount = directories.Count;
            inventory = ReplayStorage.Inventory(roots, new LeaseFiles(this), fileLimit, directoryLimit,
                ReplayCalibrationRun.ValidateMediaAttributes);
            foreach (var extent in inventory.Values)
            {
                string key = Key(extent);
                if (allocations.TryGetValue(key, out long previous) && previous != extent.AllocatedBytes)
                    throw new IOException("Historical shared allocation changed during observation.");
                allocations[key] = extent.AllocatedBytes;
            }
            AllocatedBytes = allocations.Values.Aggregate(0L, (sum, bytes) => checked(sum + bytes));
            Check();
        }
        catch { Dispose(); throw; }
    }
    static SafeFileHandle Open(string path, bool directory)
    {
        // GENERIC_READ (including READ_DATA), not READ_ATTRIBUTES alone: this
        // lease participates in Windows share-access enforcement against writes.
        var handle = CreateFileW(NativePath(path), 0x80000000, 1, IntPtr.Zero, 3,
            0x00200000u | (directory ? 0x02000000u : 0), IntPtr.Zero);
        if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error, path); }
        return handle;
    }
    sealed class LeaseFiles(FrozenCalibrationHistory owner) : ReplayFiles
    {
        internal override ReplayFileExtent ObserveManifest(string path, ReplayFileExtent enumerated) => ObserveInventory(path, enumerated);
        internal override ReplayFileExtent ObserveInventory(string path, ReplayFileExtent enumerated)
        {
            var handle = Open(path, directory: false);
            try
            {
                var observed = ReplayStorage.ObserveInventory(handle, enumerated);
                owner.leases.Add(handle); return observed;
            }
            catch { handle.Dispose(); throw; }
        }
    }
    static string NativePath(string path) => Path.GetFullPath(path).StartsWith(@"\\?\", StringComparison.Ordinal)
        ? Path.GetFullPath(path) : @"\\?\" + Path.GetFullPath(path);
    internal void Invalidate(string reason)
    { if (Volatile.Read(ref disposed) == 0) Interlocked.CompareExchange(ref invalidation, reason, null); }
    internal void Check()
    {
        if (Volatile.Read(ref disposed) != 0) throw new ObjectDisposedException(nameof(FrozenCalibrationHistory));
        foreach (var watcher in watchers)
        {
            uint status = watcher.State;
            if (status == 0) Invalidate("Historical namespace or allocation metadata changed.");
            else if (status != 258) Invalidate("Historical native change monitor failed.");
        }
        string error = Volatile.Read(ref invalidation);
        if (error != null) throw new IOException(error);
    }
    internal long BytesIncluding(IReadOnlyDictionary<string, ReplayFileExtent> current)
    {
        Check();
        long bytes = AllocatedBytes;
        foreach (var group in current.Values.GroupBy(Key, StringComparer.OrdinalIgnoreCase))
        {
            long observed = group.First().AllocatedBytes;
            if (group.Any(v => v.AllocatedBytes != observed)) throw new IOException("Current shared allocation changed during observation.");
            if (allocations.TryGetValue(group.Key, out long previous))
            { if (previous != observed) throw new IOException("Leased historical allocation changed through a current alias."); }
            else bytes = checked(bytes + observed);
        }
        return bytes;
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        foreach (var watcher in watchers) watcher.Dispose();
        foreach (var handle in leases) handle.Dispose();
        watchers.Clear(); leases.Clear();
    }
}
