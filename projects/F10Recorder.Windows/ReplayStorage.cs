using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace F10Recorder;

// Namespace observations carry no allocation credit. Capacity accounting uses
// the separate handle-backed full inventory.
internal readonly record struct ReplayNamespaceIdentity(string Volume, string Identity);

internal sealed record ReplayFileExtent(string Volume, string Identity, long AllocatedBytes, long Length, long AllocationUnit)
{
    internal long CopyBytes => checked((Length / AllocationUnit + (Length % AllocationUnit == 0 ? 0 : 1)) * AllocationUnit);
}
internal sealed record ReplayVolumeCapacity(string Volume, long AllocationUnit, long AvailableBytes);

// Filesystem observations for the replay module. Unknown identity/allocation is
// an admission failure, never an assumption of spare capacity. Capacity covers
// known snapshot inputs only, not output/finalization or an unfinished tail.
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
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, [Out] byte[] information, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder name, uint length, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetDiskFreeSpace(string root, out uint sectors, out uint sectorBytes, out uint freeClusters, out uint clusters);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetDiskFreeSpaceEx(string directory, out ulong available, out ulong total, out ulong free);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    internal static ReplayVolumeCapacity ObserveCapacity(string directory)
    {
        string existing = Path.GetFullPath(directory);
        while (true)
        {
            try
            {
                var attributes = File.GetAttributes(existing);
                if ((attributes & FileAttributes.Directory) == 0) throw new IOException("Known-input capacity requires a directory ancestor.");
                break;
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            existing = Path.GetDirectoryName(existing) ?? throw new IOException("Known-input capacity has no observable directory ancestor.");
        }
        for (string parent = existing; parent != null; parent = Path.GetDirectoryName(parent))
            if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) throw new IOException("Known-input capacity does not traverse reparse points.");
        using var anchor = Native.AnchorDirectory(existing);
        string volume = Volume(anchor);
        if (!GetDiskFreeSpace(volume, out uint sectors, out uint sectorBytes, out _, out _)
            || !GetDiskFreeSpaceEx(volume, out ulong available, out ulong total, out _)) throw new Win32Exception();
        if (available > total || Native.DirectoryIdentity(anchor) != Native.DirectoryIdentity(existing))
            throw new IOException("Known-input capacity observation changed or is inconsistent.");
        return ValidateCapacity(new(volume, checked((long)sectors * sectorBytes), checked((long)available)));
    }

    internal static ReplayVolumeCapacity ValidateCapacity(ReplayVolumeCapacity capacity)
    {
        if (capacity == null || string.IsNullOrWhiteSpace(capacity.Volume) || capacity.AllocationUnit <= 0 || capacity.AvailableBytes < 0)
            throw new IOException("Known-input capacity observation is invalid or incomplete.");
        return capacity;
    }

    static string Volume(SafeFileHandle file)
    {
        var name = new StringBuilder(256);
        uint length = GetFinalPathNameByHandle(file, name, (uint)name.Capacity, 1);
        // Insufficient-buffer results include NUL; a required32768 still
        // represents a32767-character path supported by the previous buffer.
        if (length >= name.Capacity && length <= 32768)
        {
            name.EnsureCapacity(checked((int)length));
            length = GetFinalPathNameByHandle(file, name, (uint)name.Capacity, 1);
        }
        if (length == 0 || length >= name.Capacity) throw new IOException("Cannot identify the replay storage volume.", new Win32Exception());
        string resolved = name.ToString();
        int end = resolved.IndexOf("}\\", StringComparison.Ordinal);
        if (!resolved.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase) || end < 0)
            throw new IOException("Replay storage accounting requires an identifiable local volume.");
        return resolved[..(end + 2)];
    }

    internal static ReplayFileExtent Observe(string path)
    {
        using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (!GetFileInformationByHandleEx(file, 1, out var info, (uint)Marshal.SizeOf<StandardInformation>())) throw new Win32Exception();
        string volume = Volume(file);
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

    internal static ReplayFileExtent ObserveInventory(string path, ReplayFileExtent enumerated)
    {
        // Inventory needs metadata, not a data-read handle on every growing or
        // already-closed payload. Do not follow a leaf retargeted to a reparse.
        using var file = OpenInventory(path);
        return ObserveInventory(file, enumerated);
    }

    internal static SafeFileHandle OpenInventory(string path) =>
        CreateFile(Path.GetFullPath(path), 0x80, 7, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);

    // Request data-read access (without reading payload): an attribute-only
    // handle did not enforce no-delete sharing in the prospective control.
    // Owned scratch remains writable, but cannot be renamed/replaced while its
    // allocation observer is in use. Never used for rolling/manifests.
    internal static SafeFileHandle OpenPinnedInventory(string path) =>
        CreateFile(Path.GetFullPath(path), 0x81, 3, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);

    internal static ReplayFileExtent ObserveInventory(SafeFileHandle file, ReplayFileExtent enumerated)
    {
        var observed = RefreshInventoryAllocation(file, enumerated);
        string volume = Volume(file), identity = Native.DirectoryIdentity(file);
        if (!volume.Equals(enumerated.Volume, StringComparison.OrdinalIgnoreCase) || identity != enumerated.Identity)
            throw new IOException("Replay storage entry identity or actual volume changed during inventory.");
        // Allocation unit was queried from this anchored directory's actual
        // volume. It is volume metadata, not a new per-file free-space sample.
        // Still refresh each leaf's actual allocation/EOF, even during growth.
        return observed;
    }

    // Only reuse identity from the SAME still-open handle after it has been
    // matched to directory enumeration. A Windows file handle cannot retarget
    // to another file object. Allocation/EOF still require a fresh native query.
    internal static ReplayFileExtent RefreshInventoryAllocation(SafeFileHandle file, ReplayFileExtent verifiedIdentity)
    {
        Validate(verifiedIdentity);
        if (file.IsInvalid || !GetFileInformationByHandleEx(file, 1, out var info, (uint)Marshal.SizeOf<StandardInformation>())) throw new Win32Exception();
        if (info.Directory != 0 || info.DeletePending != 0) throw new IOException("Replay inventory leaf is not an available regular file.");
        return Validate(verifiedIdentity with { AllocatedBytes = info.AllocationSize, Length = info.EndOfFile });
    }

    internal static Dictionary<string, ReplayNamespaceIdentity> InventoryNamespaces(IEnumerable<string> roots, ReplayFiles files,
        int maximumFiles = int.MaxValue, int maximumDirectories = int.MaxValue)
    {
        var result = new Dictionary<string, ReplayNamespaceIdentity>(StringComparer.OrdinalIgnoreCase);
        int directories = 0;
        void Read(string directory)
        {
            if (++directories > maximumDirectories) throw new IOException("Replay namespace directory bound exceeded.");
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("Replay namespace does not traverse reparse points.");
            foreach (var entry in DirectoryEntries(directory))
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Replay namespace does not traverse reparse points.");
                if ((entry.Attributes & FileAttributes.Directory) != 0) { Read(entry.Path); continue; }
                if (result.Count >= maximumFiles) throw new IOException("Replay namespace file bound exceeded.");
                string name = Path.GetFileName(entry.Path);
                var identity = name.Equals("segments.csv", StringComparison.OrdinalIgnoreCase) || name.Equals("segments.csv.tmp", StringComparison.OrdinalIgnoreCase)
                    ? files.ObserveManifestNamespace(entry.Path, entry.Extent) : files.ObserveNamespace(entry.Path, entry.Extent);
                if (string.IsNullOrWhiteSpace(identity.Volume) || string.IsNullOrWhiteSpace(identity.Identity))
                    throw new IOException("Replay namespace file identity is unknown.");
                if (result.TryGetValue(entry.Path, out var prior) && prior != identity)
                    throw new IOException("Replay namespace identity changed across overlapping roots.");
                result[entry.Path] = identity;
            }
        }
        foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase)) Read(root);
        return result;
    }

    internal static Dictionary<string, ReplayFileExtent> Inventory(IEnumerable<string> roots, ReplayFiles files,
        int maximumFiles = int.MaxValue, int maximumDirectories = int.MaxValue,
        Action<string, FileAttributes> inspectAttributes = null)
    {
        var result = new Dictionary<string, ReplayFileExtent>(StringComparer.OrdinalIgnoreCase);
        int directories = 0;
        void Read(string directory)
        {
            if (++directories > maximumDirectories) throw new IOException("Replay storage inventory directory bound exceeded.");
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("Replay-cache inventory does not traverse reparse points.");
            foreach (var entry in DirectoryEntries(directory))
            {
                string path = entry.Path;
                var attributes = entry.Attributes;
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Replay-cache inventory does not traverse reparse points.");
                if ((attributes & FileAttributes.Directory) != 0) Read(path);
                else
                {
                    if (result.Count >= maximumFiles) throw new IOException("Replay storage inventory file bound exceeded.");
                    inspectAttributes?.Invoke(path, attributes);
                    // The segmenter replaces its list atomically. Even a delete-sharing
                    // leaf reader can deny Windows replacement of the destination.
                    // Keep the enumerated allocation/identity if that name is renamed;
                    // never reopen a volatile list or turn a vanished name into zero.
                    string name = Path.GetFileName(path);
                    result[path] = Validate(name.Equals("segments.csv", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("segments.csv.tmp", StringComparison.OrdinalIgnoreCase)
                        ? files.ObserveManifest(path, entry.Extent) : files.ObserveInventory(path, entry.Extent));
                }
            }
        }
        foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase)) Read(root);
        return result;
    }

    internal sealed record DirectoryEntry(string Path, FileAttributes Attributes, ReplayFileExtent Extent);
    // Identity/attributes only. Callers must separately query native allocation
    // through the matched file handle; directory EOF/allocation may lag writes.
    internal static DirectoryEntry DirectoryLeaf(string path)
    {
        string full = Path.GetFullPath(path), directory = Path.GetDirectoryName(full);
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Output namespace does not traverse reparse points.");
        return DirectoryEntries(directory).FirstOrDefault(e => e.Path.Equals(full, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException("Output namespace leaf is absent.", full);
    }
    static IEnumerable<DirectoryEntry> DirectoryEntries(string directory)
    {
        using var anchor = Native.AnchorDirectory(directory);
        string identity = Native.DirectoryIdentity(anchor), volume = Volume(anchor);
        if (!GetDiskFreeSpace(volume, out uint sectors, out uint sectorBytes, out _, out _)) throw new Win32Exception();
        long unit = checked((long)sectors * sectorBytes);
        using var directoryBuffer = new ReplayDirectoryBuffer();
        var buffer = directoryBuffer.Bytes;
        bool restart = true;
        while (true)
        {
            // FILE_ID_BOTH_DIR_INFO contains identity, EOF, allocation and attributes
            // in one directory query, without opening any child. Ordinary growing
            // media still uses handle observations below; directory metadata may lag.
            directoryBuffer.Prepare();
            if (!GetFileInformationByHandleEx(anchor, restart ? 11 : 10, buffer, ReplayDirectoryBuffer.Size))
            {
                int error = Marshal.GetLastWin32Error();
                if (error == 18) yield break; // ERROR_NO_MORE_FILES only.
                throw new Win32Exception(error);
            }
            restart = false;
            int offset = 0;
            while (true)
            {
                if (offset < 0 || offset > ReplayDirectoryBuffer.Size - 104) throw new IOException("Invalid directory metadata offset.");
                uint next = BitConverter.ToUInt32(buffer, offset), bytes = BitConverter.ToUInt32(buffer, offset + 60);
                if (bytes == 0 || bytes % 2 != 0 || bytes > ReplayDirectoryBuffer.Size - offset - 104
                    || (next != 0 && (next % 8 != 0 || next < 104 + bytes || next > ReplayDirectoryBuffer.Size - offset - 104)))
                    throw new IOException("Invalid directory metadata length.");
                string name = Encoding.Unicode.GetString(buffer, offset + 104, (int)bytes);
                if (name != "." && name != "..")
                {
                    if (name.IndexOfAny(['\\', '/', ':', '\0']) >= 0) throw new IOException("Invalid directory entry name.");
                    ulong fileId = BitConverter.ToUInt64(buffer, offset + 96);
                    if (fileId == 0) throw new IOException("Directory entry has no observable identity.");
                    var extent = new ReplayFileExtent(volume, identity[..8] + ":" + fileId.ToString("X16"),
                        BitConverter.ToInt64(buffer, offset + 48), BitConverter.ToInt64(buffer, offset + 40), unit);
                    yield return new(Path.Combine(directory, name), (FileAttributes)BitConverter.ToUInt32(buffer, offset + 56), extent);
                }
                if (next == 0) break;
                offset = checked(offset + (int)next);
            }
        }
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
