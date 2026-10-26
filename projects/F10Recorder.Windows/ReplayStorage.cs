using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace F10Recorder;

internal sealed record ReplayFileExtent(string Volume, string Identity, long AllocatedBytes, long Length, long AllocationUnit)
{
    internal long CopyBytes => checked((Length / AllocationUnit + (Length % AllocationUnit == 0 ? 0 : 1)) * AllocationUnit);
}

// Filesystem observations for the replay module. Unknown identity/allocation is
// an admission failure, never an assumption of spare capacity. No free-space or
// calibrated output/finalization promise is made by this cache-only inventory.
internal static class ReplayStorage
{
    [StructLayout(LayoutKind.Sequential)]
    struct StandardInformation
    {
        internal long AllocationSize, EndOfFile;
        internal uint Links;
        internal byte DeletePending, Directory;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, out StandardInformation information, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder name, uint length, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetDiskFreeSpace(string root, out uint sectors, out uint sectorBytes, out uint freeClusters, out uint clusters);

    internal static ReplayFileExtent Observe(string path)
    {
        using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (!GetFileInformationByHandleEx(file, 1, out var info, (uint)Marshal.SizeOf<StandardInformation>())) throw new Win32Exception();
        var name = new StringBuilder(32768);
        uint length = GetFinalPathNameByHandle(file, name, (uint)name.Capacity, 1); // Actual local volume GUID.
        if (length == 0 || length >= name.Capacity) throw new IOException("Cannot identify the replay cache volume.", new Win32Exception());
        string resolved = name.ToString();
        int end = resolved.IndexOf("}\\", StringComparison.Ordinal);
        if (!resolved.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase) || end < 0) throw new IOException("Replay-cache allocation accounting requires an identifiable local volume.");
        string volume = resolved[..(end + 2)];
        if (!GetDiskFreeSpace(volume, out uint sectors, out uint sectorBytes, out _, out _)) throw new Win32Exception();
        long unit = checked((long)sectors * sectorBytes);
        return Validate(new(volume, Native.DirectoryIdentity(file), info.AllocationSize, info.EndOfFile, unit));
    }

    internal static ReplayFileExtent Validate(ReplayFileExtent extent)
    {
        if (extent == null || string.IsNullOrWhiteSpace(extent.Volume) || string.IsNullOrWhiteSpace(extent.Identity)
            || extent.AllocatedBytes < 0 || extent.Length < 0 || extent.AllocationUnit <= 0)
            throw new IOException("Replay-cache storage observation is invalid or incomplete.");
        _ = extent.CopyBytes; // Reject overflow before making an allocation promise.
        return extent;
    }

    internal static Dictionary<string, ReplayFileExtent> Inventory(IEnumerable<string> roots, ReplayFiles files)
    {
        var result = new Dictionary<string, ReplayFileExtent>(StringComparer.OrdinalIgnoreCase);
        void Read(string directory)
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("Replay-cache inventory does not traverse reparse points.");
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Replay-cache inventory does not traverse reparse points.");
                if ((attributes & FileAttributes.Directory) != 0) Read(path);
                else result[path] = Validate(files.Observe(path));
            }
        }
        foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase)) Read(root);
        return result;
    }

    internal static long Bytes(IReadOnlyDictionary<string, ReplayFileExtent> inventory)
    {
        long total = 0;
        foreach (var group in inventory.Values.GroupBy(e => (e.Volume.ToUpperInvariant(), e.Identity.ToUpperInvariant())))
        {
            // Changing observations of a shared allocation are not spare space.
            if (group.Any(e => e.AllocatedBytes != group.First().AllocatedBytes)) throw new IOException("Replay-cache allocation changed during observation.");
            total = checked(total + group.First().AllocatedBytes);
        }
        return total;
    }
}
