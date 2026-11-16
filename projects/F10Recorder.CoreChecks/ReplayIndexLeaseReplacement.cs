using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

// File-study plumbing only. Preserve continuous source protection while switching
// the read-handle mode; no changed identity may replace the old owner's lease.
internal static class ReplayIndexLeaseReplacement
{
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, [Out] byte[] information, uint size);
    static byte[] Identity(FileStream source)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This lease study requires Windows file identities.");
        // FILE_ID_INFO:64bit volume serial +128bit file ID.
        // https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_id_info
        byte[] identity = new byte[24];
        if (!GetFileInformationByHandleEx(source.SafeFileHandle, 18, identity, 24)) throw new Win32Exception();
        return identity;
    }
    internal static void RequireSame(FileStream previous, FileStream replacement)
    {
        if (!Identity(previous).AsSpan().SequenceEqual(Identity(replacement)) || previous.Length != replacement.Length)
            throw new IOException("Fresh source reader is not the same immutable file object/extent.");
    }
    internal static FileStream Replace(IDictionary<string, FileStream> owners, string path, bool asynchronous, List<FileStream> uncertain)
    {
        var previous = owners[path]; FileStream replacement = null; bool transferred = false;
        try
        {
            replacement = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
                FileOptions.SequentialScan | (asynchronous ? FileOptions.Asynchronous : FileOptions.None));
            if (replacement.IsAsync != asynchronous) throw new IOException("Source reader handle mode differs from its admission.");
            RequireSame(previous, replacement);
            // Own the new verified object BEFORE closing the previous lease.
            // A failed predecessor close remains in the outer cleanup list.
            owners[path] = replacement; transferred = true; uncertain.Add(previous);
            previous.Dispose(); uncertain.Remove(previous);
            return replacement;
        }
        finally
        {
            if (!transferred && replacement != null)
            {
                uncertain.Add(replacement); replacement.Dispose(); uncertain.Remove(replacement);
            }
        }
    }
    internal static void RunChecks(string root)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (Directory.Exists(root)) throw new IOException("Fresh lease-check root required.");
        Directory.CreateDirectory(root);
        string first = Path.Combine(root, "source.fixture"), other = Path.Combine(root, "same-bytes-different-object.fixture");
        File.WriteAllBytes(first, [1, 2, 3, 4]); File.WriteAllBytes(other, [1, 2, 3, 4]);
        var owners = new Dictionary<string, FileStream> { [first] = new(first, FileMode.Open, FileAccess.Read, FileShare.Read) };
        var uncertain = new List<FileStream>();
        try
        {
            using var separate = new FileStream(other, FileMode.Open, FileAccess.Read, FileShare.Read); bool rejected = false;
            try { RequireSame(owners[first], separate); } catch (IOException) { rejected = true; }
            if (!rejected) throw new Exception("Equal bytes at a different file identity passed lease continuity.");
            foreach (bool asynchronous in new[] { false, true, false })
            {
                var prior = owners[first].SafeFileHandle; var current = Replace(owners, first, asynchronous, uncertain);
                if (!prior.IsClosed || current.IsAsync != asynchronous || !current.CanRead || current.Length != 4 || uncertain.Count != 0)
                    throw new Exception("Lease transition lost predecessor closure, actual handle mode or source ownership.");
            }
        }
        finally { foreach (var stream in owners.Values.Concat(uncertain)) stream.Dispose(); }
        Console.WriteLine("Passed: same-native-identity continuous source lease replacement across sync/async modes; different objects with identical bytes reject.");
    }
}
