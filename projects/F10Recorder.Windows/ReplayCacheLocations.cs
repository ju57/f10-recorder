using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace F10Recorder;

// Location changes never migrate or erase old footage. Worker-owned history
// keeps retained roots in this profile's aggregate budget across restarts.
internal static class ReplayCacheLocations
{
    const string HistoryKey = "F10.ReplayCacheRoots.v1";
    internal static string Default(string data) => Path.Combine(data, "buffer");
    internal static string Current(Settings settings, string data)
    {
        if (settings.ReplayCacheDirectory.Length == 0) return Default(data);
        string identity = WorkerIdentity.DataPath(data).ToUpperInvariant();
        string suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24];
        return Path.Combine(settings.ReplayCacheDirectory, "F10-cache-" + suffix);
    }
    internal static string[] Roots(Settings settings, string data)
    {
        // A custom preference may predate this implementation as an opaque
        // field. Its old default cache remains charged even without history.
        var roots = new List<string> { Current(settings, data), Default(data) };
        if (settings.AdditionalPreferences.TryGetValue(HistoryKey, out var history))
        {
            if (history.ValueKind != JsonValueKind.Array) throw new IOException("Replay-cache location history is invalid; preferences preserved.");
            foreach (var item in history.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || !Path.IsPathFullyQualified(item.GetString())) throw new IOException("Replay-cache location history is invalid; preferences preserved.");
                roots.Add(Path.GetFullPath(item.GetString()));
            }
        }
        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    internal static void PreservePrevious(Settings next, Settings previous, string data)
    {
        if (string.Equals(Current(next, data), Current(previous, data), StringComparison.OrdinalIgnoreCase)) return;
        next.AdditionalPreferences[HistoryKey] = JsonSerializer.SerializeToElement(Roots(previous, data).Concat(Roots(next, data)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }
    internal static IDisposable PinSelection(Settings settings)
    {
        if (settings.ReplayCacheDirectory.Length == 0) return null;
        try
        {
            var lease = new TrustedStorage.ProfileDirectoryLease(settings.ReplayCacheDirectory);
            settings.ReplayCacheDirectory = lease.DirectoryPath;
            return lease;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { throw new IOException("Replay-cache folder is unavailable: " + ex.Message, ex); }
    }
    internal static IEnumerable<string> Existing(IEnumerable<string> roots)
    {
        foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Present(root)) continue;
            using var lease = new TrustedStorage.ProfileDirectoryLease(root);
            yield return lease.DirectoryPath;
        }
    }
    static bool Present(string root)
    {
        string missing = root;
        while (true)
        {
            try
            {
                var attributes = File.GetAttributes(missing);
                if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Replay-cache location must be an ordinary directory.");
                if (missing == root) return true;
                else
                {
                    // A readable parent proves absence; denied/hidden/changed
                    // entries must not silently become an empty cache.
                    string relative = Path.GetRelativePath(missing, root).Split(Path.DirectorySeparatorChar)[0];
                    if (Directory.EnumerateFileSystemEntries(missing).Any(p => string.Equals(Path.GetFileName(p), relative, StringComparison.OrdinalIgnoreCase))) throw new IOException("Replay-cache location changed during observation.");
                }
                return false;
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            missing = Path.GetDirectoryName(missing) ?? throw new IOException("Replay-cache location has no readable ancestor.");
        }
    }
}
