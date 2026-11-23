namespace F10Recorder;

// Conservative cache maintenance retains a fresh native parent through each
// mutation too. A scheduled healthy namespace scan never authorizes a later
// pathname traversal for pruning or retired-session cleanup.
internal static class ReplayCacheFileOperations
{
    internal static ReplayGuardDirectory OpenDirectory(string path)
        => new(path, Native.DirectoryIdentity(path), ReplayStorage.ObserveCapacity(path).Volume);

    internal static void Prune(string directory, string extension, string oldest, IReadOnlySet<string> pins, Action changing)
    {
        using var parent = OpenDirectory(directory);
        foreach (var entry in parent.EnumerateLeaves(4096))
        {
            string leaf = Path.GetFileName(entry.Path);
            if (!leaf.StartsWith("chunk-", StringComparison.Ordinal) || !leaf.EndsWith(extension, StringComparison.Ordinal)
                || StringComparer.OrdinalIgnoreCase.Compare(entry.Path, oldest) >= 0 || pins.Contains(entry.Path)) continue;
            if ((entry.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new IOException("Cache pruning found a redirected or non-file completed chunk.");
            changing?.Invoke(); // Caller owns Cache.gate; outside every native lock.
            using var removal = parent.OpenForRemoval(leaf, entry.Extent);
            removal.Delete();
        }
    }

    internal static void DeleteTree(string path, string workspacePartial = null)
    {
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (workspacePartial != null && !path.Equals(Path.GetFullPath(workspacePartial) + ".work", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Replay scratch directory differs from its owning output.");
        if (!Directory.Exists(path)) return;
        using var parent = OpenDirectory(Path.GetDirectoryName(path));
        string leaf = Path.GetFileName(path), identity = Native.DirectoryIdentity(path);
        bool snapshot = System.Text.RegularExpressions.Regex.IsMatch(leaf, "^save-[0-9a-f]{32}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        using (var directory = parent.OpenDirectory(leaf, identity))
        {
            var entries = directory.EnumerateLeaves(4096);
            // Closed-session cleanup has no authority over arbitrary ordinary
            // descendants. Unknown names/children retain the entire tree.
            foreach (var entry in entries)
            {
                string name = Path.GetFileName(entry.Path);
                bool known = workspacePartial != null ? ReplayOutputWorkspace.Classify(workspacePartial, entry.Path) != null : snapshot
                    ? name == "concat.txt" || System.Text.RegularExpressions.Regex.IsMatch(name, "^[0-9]{6}\\.(ts|mkv)$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)
                    : name is "segments.csv" or "segments.csv.tmp" || System.Text.RegularExpressions.Regex.IsMatch(name, "^chunk-[0-9]{9}\\.(ts|mkv)$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
                if (!known || (entry.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                    throw new IOException("Retired cache tree contains an unknown or redirected member; evidence remains retained.");
            }
            foreach (var entry in entries)
            { using var removal = directory.OpenForRemoval(Path.GetFileName(entry.Path), entry.Extent); removal.Delete(); }
        }
        // Refuse new/foreign members rather than recursively following them.
        parent.DeleteEmptyDirectory(leaf, identity);
    }
}
