namespace F10Recorder;

// Partition an already fresh native inventory once. No cached allocation values
// or directory sizes enter accounting, and Bytes still rejects inconsistent
// aliases of the same object. This changes bookkeeping, not observation policy.
internal sealed class CalibrationInventoryAccounting
{
    internal sealed class Cell
    {
        internal readonly Dictionary<string, ReplayFileExtent> Sources = new(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, ReplayFileExtent> OutputsAndWork = new(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, ReplayFileExtent> Capture = new(StringComparer.OrdinalIgnoreCase);
    }
    internal readonly Dictionary<string, Cell> Cells;
    internal readonly Dictionary<string, ReplayFileExtent> Retained = new(StringComparer.OrdinalIgnoreCase);
    internal readonly Dictionary<string, ReplayFileExtent> Current = new(StringComparer.OrdinalIgnoreCase);
    internal readonly Dictionary<string, Dictionary<string, ReplayFileExtent>> Workspaces = new(StringComparer.OrdinalIgnoreCase);
    internal readonly Dictionary<string, ReplayFileExtent> Outputs = new(StringComparer.OrdinalIgnoreCase);
    internal CalibrationInventoryAccounting(IReadOnlyDictionary<string, ReplayFileExtent> inventory, string root,
        IEnumerable<string> labels, string[] retainedPrefixes, bool includeCurrent)
    {
        Cells = labels.ToDictionary(label => label, _ => new Cell(), StringComparer.OrdinalIgnoreCase);
        string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string workMarker = ".partial.mp4.work" + Path.DirectorySeparatorChar;
        foreach (var item in inventory)
        {
            string path = item.Key, name = Path.GetFileName(path);
            bool work = path.Contains(workMarker, StringComparison.OrdinalIgnoreCase);
            if (retainedPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase))) Retained.Add(path, item.Value);
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                if (includeCurrent) Current.Add(path, item.Value);
                int end = path.IndexOf(Path.DirectorySeparatorChar, prefix.Length);
                if (end >= 0 && Cells.TryGetValue(path[prefix.Length..end], out var cell))
                {
                    if (name.StartsWith("chunk-")) cell.Sources.Add(path, item.Value);
                    if (path.EndsWith(".mp4") || work) cell.OutputsAndWork.Add(path, item.Value);
                    string buffer = path[..(end + 1)] + "buffer" + Path.DirectorySeparatorChar;
                    if (path.StartsWith(buffer, StringComparison.OrdinalIgnoreCase)
                        && (name.StartsWith("chunk-", StringComparison.Ordinal) || name == "segments.csv"))
                        cell.Capture.Add(path, item.Value);
                }
            }
            if (work)
            {
                string directory = Path.GetDirectoryName(path);
                if (!directory.EndsWith(".partial.mp4.work", StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Nested replay workspace is outside the qualification recipe.");
                string output = directory[..^5];
                if (!Workspaces.TryGetValue(output, out var members)) Workspaces.Add(output, members = new(StringComparer.OrdinalIgnoreCase));
                members.Add(path, item.Value);
            }
            if (path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) Outputs.Add(path, item.Value);
        }
    }
}
