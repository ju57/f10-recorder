using System.ComponentModel;
namespace F10Recorder;

// One accepted save, including protection and cleanup. Live capture/manifest
// registration stays outside this registry. Only independently admitted
// completed inputs and files created here may be read/linked/retired.
internal sealed class ReplayRootedFiles() : ReplayFiles(ReplayFileMode.NativeInventoryForChecks), IDisposable
{
    internal readonly ReplayOwnedFileSystem Registry = new();
    static readonly object retainedGate = new();
    static readonly List<ReplayRootedFiles> retained = [];
    static readonly List<object> retainedDependents = [];
    readonly object metadataGate = new();
    readonly Dictionary<string, ReplayOwnedFileSystem.DirectoryOwner> directories = new(StringComparer.OrdinalIgnoreCase);
    internal ReplayNativeExportScope Active;
    internal Action<string,ReplayOwnedFileSystem.DirectoryOwner> DirectoryCreatedForChecks;
    internal bool ForceCopyForChecks;
    internal ReplayOwnedFileSystem.DirectoryOwner DirectoryOwner(string path)
    { lock(metadataGate) return directories.TryGetValue(Path.GetFullPath(path), out var owner) ? owner : throw new IOException("Unregistered native operation directory: " + path); }
    internal void RetainUncertain()
    { lock(retainedGate) if(!retained.Contains(this)) retained.Add(this); }
    internal void RetainDependent(object dependent)
    { lock(retainedGate) { if(!retained.Contains(this)) retained.Add(this); if(dependent != null) retainedDependents.Add(dependent); } }
    internal void Root(string path)
    {
        lock(metadataGate) {
        path = Path.GetFullPath(path); if(directories.ContainsKey(path))return;
        directories.Add(path, Registry.AdmitRoot(path, Native.DirectoryIdentity(path), ReplayStorage.ObserveCapacity(path).Volume));
        }
    }
    internal void EnsureOutputRoot(string path)
    {
        path = Path.GetFullPath(path);
        if(Directory.Exists(path)){Root(path);return;}
        string parent=Path.GetDirectoryName(path)??throw new IOException("Output root has no existing local ancestor.");
        EnsureOutputRoot(parent); CreateDirectory(path);
    }
    internal void Child(string path, bool retirable)
    {
        lock(metadataGate) {
        path = Path.GetFullPath(path);
        directories.Add(path, Registry.AdmitChild(DirectoryOwner(Path.GetDirectoryName(path)), Path.GetFileName(path), Native.DirectoryIdentity(path), retirable));
        }
    }
    internal void Admit(string path, ReplayFileExtent expected, bool retirable)
        => Registry.AdmitFile(DirectoryOwner(Path.GetDirectoryName(path)), Path.GetFileName(path), expected, retirable);
    internal void AdmitPreparedInput(string path, ReplayFileExtent expected, ReplayCompletedHistory.Snapshot snapshot)
        => Registry.AdmitPreparedInput(DirectoryOwner(Path.GetDirectoryName(path)),path,expected,snapshot);
    internal List<Segment> ReadSegments(string directory)
    {
        var result = new List<Segment>();
        Registry.Read(DirectoryOwner(directory), "segments.csv", stream =>
        {
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, true, 4096, true);
            string text = reader.ReadToEnd(); int last = text.LastIndexOf('\n'); if(last < 0) return;
            foreach(string line in text[..last].Split('\n'))
            {
                var match = System.Text.RegularExpressions.Regex.Match(line.Trim(), "^(?:\"((?:[^\"]|\"\")*)\"|([^,]+)),([0-9.]+),([0-9.]+)$");
                if(!match.Success || !double.TryParse(match.Groups[3].Value,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out double start)
                    || !double.TryParse(match.Groups[4].Value,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out double end) || end<=start)continue;
                string name=match.Groups[1].Success?match.Groups[1].Value.Replace("\"\"","\""):match.Groups[2].Value;
                string path=Path.GetFullPath(Path.Combine(directory,name));
                if(!path.StartsWith(Path.GetFullPath(directory)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("Segment path is outside the buffer.");
                result.Add(new(){Path=path,Start=start,End=end});
            }
        });
        return result.OrderBy(s=>s.Start).ToList();
    }
    internal override ReplayNamespaceIdentity ObserveNamespace(string path, ReplayFileExtent listed) => new(listed.Volume, listed.Identity);
    internal override ReplayNamespaceIdentity ObserveManifestNamespace(string path, ReplayFileExtent listed) => new(listed.Volume, listed.Identity);
    internal override void CreateDirectory(string path)
    {
        ReplayOwnedFileSystem.DirectoryOwner created;
        lock(metadataGate) {
        path = Path.GetFullPath(path); if (directories.ContainsKey(path)) return;
        directories.Add(path, created=Registry.CreateDirectory(DirectoryOwner(Path.GetDirectoryName(path)), Path.GetFileName(path)));
        }
        DirectoryCreatedForChecks?.Invoke(path,created);
    }
    internal override void WriteText(string path, string text)
        => Registry.WriteNew(DirectoryOwner(Path.GetDirectoryName(path)), Path.GetFileName(path), stream => stream.Write(System.Text.Encoding.UTF8.GetBytes(text)));
    internal override bool TryHardLink(string copy, string original)
    {
        if(ForceCopyForChecks)return false;
        try { Registry.Link(DirectoryOwner(Path.GetDirectoryName(original)), Path.GetFileName(original), DirectoryOwner(Path.GetDirectoryName(copy)), Path.GetFileName(copy)); return true; }
        catch (Win32Exception error) when (error.NativeErrorCode is 1 or 17 or 50) { return false; }
    }
    internal override void Copy(string original, string copy)
        => Registry.Copy(DirectoryOwner(Path.GetDirectoryName(original)), Path.GetFileName(original), DirectoryOwner(Path.GetDirectoryName(copy)), Path.GetFileName(copy));
    internal override void DeleteFile(string path)
    {
        var owner = DirectoryOwner(Path.GetDirectoryName(path)); string leaf = Path.GetFileName(path);
        if (owner.Files.TryGetValue(leaf, out var registered) && registered.Retired) return;
        if (!owner.Files.ContainsKey(leaf) && !owner.Native.EnumerateLeaves(4096).Any(e=>e.Path.Equals(Path.GetFullPath(path),StringComparison.OrdinalIgnoreCase))) return;
        Registry.RemoveFile(owner, leaf);
    }
    internal override void DeleteDirectory(string path)
    { try { Registry.RemoveRegisteredTree(DirectoryOwner(path)); } catch(Win32Exception error) { throw new IOException("Rooted directory retirement failed.",error); } }
    internal override void Publish(ReplayExportRequest request, string final)
    {
        if (request.ValidatedOutput == null) throw new IOException("Isolated native packet publication requires the checked lease.");
        request.NativeFiles?.RequireHealthyPublication();
        var owner = DirectoryOwner(Path.GetDirectoryName(request.Partial));
        if (!owner.Files.TryGetValue(Path.GetFileName(request.Partial), out var file) || file.Extent.Identity != request.ValidatedOutput.CheckedIdentity.Identity)
            throw new IOException("Published object was not registered by its managed creator.");
        base.Publish(request, final);
        file.Retired = true;
    }
    public void Dispose() => Registry.Dispose();
}
