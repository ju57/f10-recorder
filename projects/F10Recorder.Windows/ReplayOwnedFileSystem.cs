namespace F10Recorder;

// Request-owned operation registry for the isolated evidence-owner candidate.
// Paths are display/lookup keys; all data and destructive operations resolve
// registered direct leaves through the retained native directory capability.
// A fresh healthy global scan can never authorize an unregistered deletion.
internal sealed class ReplayOwnedFileSystem : IDisposable
{
    internal sealed class DirectoryOwner
    {
        internal readonly ReplayGuardDirectory Native;
        internal readonly DirectoryOwner Parent;
        internal readonly string Leaf;
        internal readonly bool Retirable;
        internal readonly Dictionary<string, DirectoryOwner> Directories = new(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, FileOwner> Files = new(StringComparer.OrdinalIgnoreCase);
        internal bool Retired, Uncertain;
        internal DirectoryOwner(ReplayGuardDirectory native, DirectoryOwner parent, string leaf, bool retirable)
        { Native = native; Parent = parent; Leaf = leaf; Retirable = retirable; }
    }
    internal sealed class FileOwner(ReplayFileExtent extent, bool retirable)
    {
        internal ReplayFileExtent Extent = extent;
        internal readonly bool Retirable = retirable;
        internal bool Retired, Uncertain, Writing;
    }
    readonly object sync = new();
    readonly List<DirectoryOwner> roots = [];
    readonly List<ReplayGuardDirectory.FileTicket> retained = [];
    const int MaximumDirectories = 128, MaximumFiles = 4096, MaximumDepth = 32;
    bool disposed, closing;
    int operations, directories, files;
    internal Action<DirectoryOwner> BeforeEmptyDirectoryDeleteForChecks, AfterEmptyDirectoryDeleteForChecks;
    void Require() { if (disposed || closing) throw new ObjectDisposedException(nameof(ReplayOwnedFileSystem)); }
    void Require(DirectoryOwner owner)
    {
        Require();
        if (owner == null || owner.Retired || owner.Uncertain || !Contains(owner)) throw new IOException("Operation directory is not live and certain in this request.");
    }
    bool Contains(DirectoryOwner owner)
    {
        var root = owner;
        while (root.Parent != null) root = root.Parent;
        return roots.Contains(root);
    }
    void DirectoryRoom(DirectoryOwner parent = null)
    {
        int depth = 0;
        for (var node = parent; node != null; node = node.Parent)
            if (++depth >= MaximumDepth) throw new IOException("Owned directory depth exceeded its supported bound.");
        if (directories >= MaximumDirectories) throw new IOException("Owned directory count exceeded its supported bound.");
    }
    void FileRoom()
    {
        if (files >= MaximumFiles) throw new IOException("Owned file count exceeded its supported bound.");
    }
    FileOwner File(DirectoryOwner owner, string leaf)
    {
        Require(owner); ReplayGuardDirectory.ValidateLeaf(leaf);
        if (!owner.Files.TryGetValue(leaf, out var file) || file.Retired || file.Uncertain || file.Writing)
            throw new IOException("Operation file is not registered, live and certain.");
        return file;
    }
    internal DirectoryOwner AdmitRoot(string path, string expectedIdentity, string expectedVolume)
    {
        lock (sync)
        {
            Require();
            DirectoryRoom();
            var root = new DirectoryOwner(new(path, expectedIdentity, expectedVolume), null, null, false);
            roots.Add(root); directories++; return root;
        }
    }
    internal DirectoryOwner AdmitChild(DirectoryOwner parent, string leaf, string expectedIdentity, bool retirable)
    {
        lock (sync)
        {
            Require(parent);
            DirectoryRoom(parent);
            if (parent.Directories.ContainsKey(leaf)) throw new IOException("Directory was already admitted.");
            var child = new DirectoryOwner(parent.Native.OpenDirectory(leaf, expectedIdentity), parent, leaf, retirable);
            parent.Directories.Add(leaf, child); directories++; return child;
        }
    }
    internal DirectoryOwner CreateDirectory(DirectoryOwner parent, string leaf)
    {
        lock (sync)
        {
            Require(parent);
            DirectoryRoom(parent);
            if (parent.Directories.ContainsKey(leaf)) throw new IOException("Directory name already has an owner.");
            var child = new DirectoryOwner(parent.Native.CreateDirectory(leaf), parent, leaf, true);
            parent.Directories.Add(leaf, child); directories++; return child;
        }
    }
    // Existing media requires an independent admission identity/extent. Reading
    // a pathname now is deliberately not an admission API for its contents.
    internal void AdmitFile(DirectoryOwner directory, string leaf, ReplayFileExtent expected, bool retirable)
    {
        lock (sync)
        {
            Require(directory);
            FileRoom();
            using var input = directory.Native.OpenRead(leaf, expected);
            directory.Files.Add(leaf, new(input.Observe().Extent, retirable)); files++;
        }
    }
    internal void AdmitPreparedInput(DirectoryOwner directory, string path, ReplayFileExtent expected, ReplayCompletedHistory.Snapshot snapshot)
    {
        lock(sync)
        {
            Require(directory);FileRoom();
            string leaf=Path.GetFileName(path);ReplayGuardDirectory.ValidateLeaf(leaf);
            var extent=snapshot.Admit(path,directory.Native);
            if(extent.Volume!=expected.Volume || extent.Identity!=expected.Identity || extent.Length!=expected.Length
                || extent.AllocationUnit!=expected.AllocationUnit)throw new IOException("Prepared replay input differs from fresh admission inventory.");
            directory.Files.Add(leaf,new(extent,false));files++;
        }
    }
    // Synchronous borrowed data only: no escaped stream or async work. The
    // actual index integration must use the evidence owner's joined async
    // source scope instead of relabelling this synchronous route.
    internal void Read(DirectoryOwner directory, string leaf, Action<FileStream> read)
    {
        FileOwner admitted;
        ReplayGuardDirectory.FileTicket input;
        lock (sync)
        {
            admitted = File(directory, leaf);
            input = directory.Native.OpenRead(leaf, admitted.Extent); operations++;
        }
        // Never hold this registry lock across callbacks that may acquire
        // Cache.gate. Cleanup already enters in Cache -> registry order.
        try { read(input.Data()); }
        finally { CloseOperation(input, admitted); }
    }
    internal void Link(DirectoryOwner source, string sourceLeaf, DirectoryOwner target, string targetLeaf)
    {
        lock (sync)
        {
            var admitted = File(source, sourceLeaf); Require(target); ReplayGuardDirectory.ValidateLeaf(targetLeaf);
            FileRoom();
            if (target.Files.ContainsKey(targetLeaf)) throw new IOException("Link target already has an owner.");
            using var input = source.Native.OpenRead(sourceLeaf, admitted.Extent);
            input.LinkTo(target.Native, targetLeaf);
            target.Files.Add(targetLeaf, new(admitted.Extent, true)); files++;
        }
    }
    internal void WriteNew(DirectoryOwner directory, string leaf, Action<FileStream> write)
    {
        FileOwner owner;
        ReplayGuardDirectory.FileTicket output;
        lock (sync)
        {
            Require(directory); ReplayGuardDirectory.ValidateLeaf(leaf);
            FileRoom();
            if (directory.Files.ContainsKey(leaf)) throw new IOException("Output name already has an owner.");
            output = directory.Native.CreateNew(leaf);
            try { owner = new FileOwner(output.Observe().Extent, true) { Writing = true }; }
            catch { output.Dispose(); throw; }
            directory.Files.Add(leaf, owner); files++; operations++;
        }
        try
        {
            write(output.Data()); output.Data().Flush();
            var actual = output.Observe().Extent;
            lock (sync) owner.Extent = actual;
        }
        catch
        {
            try { var actual = output.Observe().Extent; lock (sync) owner.Extent = actual; }
            catch { lock (sync) owner.Uncertain = true; }
            throw;
        }
        finally { CloseOperation(output, owner); }
    }
    internal void Copy(DirectoryOwner source, string sourceLeaf, DirectoryOwner target, string targetLeaf)
    {
        FileOwner admitted;
        ReplayGuardDirectory.FileTicket input;
        lock (sync)
        {
            admitted = File(source, sourceLeaf); Require(target);
            input = source.Native.OpenRead(sourceLeaf, admitted.Extent); operations++;
        }
        try { WriteNew(target, targetLeaf, output => input.Data().CopyTo(output)); }
        finally { CloseOperation(input, admitted); }
    }
    void CloseOperation(ReplayGuardDirectory.FileTicket ticket, FileOwner owner)
    {
        Exception failure = null;
        try { ticket.Dispose(); } catch (Exception error) { failure = error; }
        lock (sync)
        {
            operations--; owner.Writing = false;
            if (failure != null) { owner.Uncertain = true; closing = true; retained.Add(ticket); }
        }
        if (failure != null) throw new IOException("Native data owner closure is uncertain; no further operations are admitted.", failure);
    }
    internal void RemoveFile(DirectoryOwner directory, string leaf)
    {
        lock (sync)
        {
            var admitted = File(directory, leaf);
            if (!admitted.Retirable) throw new IOException("Original input is not owned for retirement.");
            using var removal = directory.Native.OpenForRemoval(leaf, admitted.Extent);
            // From issuance onward, unknown completion cannot be re-admitted
            // by another scan. Keep the registry uncertain unless it succeeds.
            admitted.Uncertain = true;
            removal.Delete(); admitted.Retired = true;
        }
    }
    internal void RemoveRegisteredTree(DirectoryOwner directory)
    {
        lock (sync)
        {
            Require(directory);
            if (!directory.Retirable || directory.Parent == null) throw new IOException("Root/original directory is not owned for retirement.");
            if (operations != 0) throw new IOException("A registered data operation still owns resources.");
            foreach (var child in directory.Directories.Values.Where(child => !child.Retired)) RemoveRegisteredTree(child);
            foreach (var file in directory.Files.Where(file => !file.Value.Retired).ToArray()) RemoveFile(directory, file.Key);
            directory.Uncertain = true;
            directory.Native.Dispose();
            // No recursive pathname deletion: unexpected members, injected
            // junctions and changed names make this atomic empty-delete fail.
            try { BeforeEmptyDirectoryDeleteForChecks?.Invoke(directory); directory.Parent.Native.DeleteEmptyDirectory(directory.Leaf, directory.Native.Identity); }
            finally { AfterEmptyDirectoryDeleteForChecks?.Invoke(directory); }
            directory.Retired = true; directory.Uncertain = false;
        }
    }
    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            if (operations != 0) throw new IOException("Cannot close the operation registry with active data users.");
            closing = true;
            List<Exception> failures = [];
            foreach (var ticket in retained)
                try { ticket.Dispose(); } catch (Exception error) { failures.Add(error); }
            void Close(DirectoryOwner directory)
            {
                foreach (var child in directory.Directories.Values) Close(child);
                try { directory.Native.Dispose(); } catch (Exception error) { failures.Add(error); }
            }
            foreach (var root in roots) Close(root);
            if (failures.Count != 0) throw new AggregateException("Owned filesystem closure is uncertain; every directory was attempted.", failures);
            disposed = true;
        }
    }
}
