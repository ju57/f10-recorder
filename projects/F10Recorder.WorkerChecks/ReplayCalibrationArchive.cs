using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text.Json;

namespace F10Recorder;

// Verification evidence only. Completed new snapshots are preserved as full
// byte archives plus native provenance; historical evidence is never passed in.
internal static class ReplayCalibrationArchive
{
    internal sealed record Member(string Name, ReplayFileExtent Extent, string Sha256, DateTime LastWriteUtc);
    internal sealed record Receipt(string Snapshot, string DirectoryIdentity, string Archive, string ArchiveSha256, Member[] Members);
    static void Require(bool value, string message) { if (!value) throw new IOException(message); }
    static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
    static void NoReparseAncestors(string path)
    {
        for (string parent = Path.GetFullPath(path); parent != null; parent = Path.GetDirectoryName(parent))
            Require((File.GetAttributes(parent) & FileAttributes.ReparsePoint) == 0, "Archive does not traverse reparse points.");
    }
    static string OwnSnapshot(string caseRoot, string snapshot)
    {
        string root = Path.GetFullPath(caseRoot), full = Path.GetFullPath(snapshot);
        Require(full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(full).StartsWith("save-", StringComparison.Ordinal), "Archive target is not an owned snapshot.");
        NoReparseAncestors(full);
        Require(Directory.GetDirectories(full).Length == 0, "Snapshot archive requires a flat directory.");
        return full;
    }
    internal static Receipt Create(string caseRoot, string snapshot, Action<long> admit, object inventoryGate = null)
    {
        snapshot = OwnSnapshot(caseRoot, snapshot);
        string identity = Native.DirectoryIdentity(snapshot);
        var members = Directory.GetFiles(snapshot).Order(StringComparer.Ordinal).Select(path =>
        {
            Require((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0, "Snapshot contains a reparse leaf.");
            string name = Path.GetFileName(path);
            Require(name == "concat.txt" || (name.EndsWith(".ts", StringComparison.Ordinal) && name.Length <= 32), "Unexpected snapshot member.");
            return new Member(name, ReplayStorage.Observe(path), Hash(path), File.GetLastWriteTimeUtc(path));
        }).ToArray();
        Require(members.Length is > 0 and <= 320, "Snapshot archive member bound exceeded.");
        long reserve = checked(members.Sum(m => m.Extent.CopyBytes) + 4_194_304);
        Require(reserve <= 1_064_000_000, "Snapshot archive byte bound exceeded.");
        admit(reserve); // All old/current sources, outputs and copies remain charged.
        string folder = Path.Combine(caseRoot, "archived-snapshots"); Directory.CreateDirectory(folder);
        NoReparseAncestors(folder);
        string archive = Path.Combine(folder, Path.GetFileName(snapshot) + ".tar");
        string partial = archive + ".partial";
        using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            using (var writer = new TarWriter(output, TarEntryFormat.Pax, leaveOpen: true))
                foreach (var member in members)
                {
                    using var input = File.OpenRead(Path.Combine(snapshot, member.Name));
                    var entry = new PaxTarEntry(TarEntryType.RegularFile, member.Name)
                        { DataStream = input, ModificationTime = member.LastWriteUtc };
                    writer.WriteEntry(entry);
                    Require(output.Length <= reserve, "Snapshot archive exceeded admitted bytes.");
                }
            output.Flush(true);
        }
        var receipt = new Receipt(snapshot, identity, partial, Hash(partial), members);
        Verify(caseRoot, receipt);
        lock (inventoryGate ?? new object()) File.Move(partial, archive);
        return receipt with { Archive = archive };
    }
    internal static void Verify(string caseRoot, Receipt receipt)
    {
        string snapshot = OwnSnapshot(caseRoot, receipt.Snapshot);
        string archiveRoot = Path.GetFullPath(Path.Combine(caseRoot, "archived-snapshots")) + Path.DirectorySeparatorChar;
        Require(Path.GetFullPath(receipt.Archive).StartsWith(archiveRoot, StringComparison.OrdinalIgnoreCase)
            && (File.GetAttributes(receipt.Archive) & FileAttributes.ReparsePoint) == 0, "Snapshot archive path changed.");
        NoReparseAncestors(receipt.Archive);
        Require(Hash(receipt.Archive) == receipt.ArchiveSha256, "Snapshot archive identity/hash changed.");
        Require(Native.DirectoryIdentity(snapshot) == receipt.DirectoryIdentity, "Snapshot directory changed.");
        var expected = receipt.Members.ToDictionary(m => m.Name, StringComparer.Ordinal);
        Require(Directory.GetFiles(snapshot).Length == expected.Count, "Snapshot members changed.");
        using var input = File.OpenRead(receipt.Archive);
        using var reader = new TarReader(input);
        TarEntry entry;
        while ((entry = reader.GetNextEntry()) != null)
        {
            Require(entry.EntryType == TarEntryType.RegularFile, "Archive has a non-file member.");
            Require(expected.Remove(entry.Name, out var member), "Archive has an unexpected or duplicate member.");
            var original = Path.Combine(snapshot, member.Name);
            Require(entry.Length == member.Extent.Length
                && Convert.ToHexString(SHA256.HashData(entry.DataStream ?? Stream.Null)) == member.Sha256
                && ReplayStorage.Observe(original) == member.Extent && Hash(original) == member.Sha256
                && File.GetLastWriteTimeUtc(original) == member.LastWriteUtc, "Archive/original content or native identity changed.");
        }
        Require(expected.Count == 0, "Archive is incomplete.");
    }
    internal static void Retire(string caseRoot, Receipt receipt, object inventoryGate = null, Action beforeRetirement = null)
    {
        string snapshot = OwnSnapshot(caseRoot, receipt.Snapshot);
        Require(Path.GetFullPath(receipt.Archive).StartsWith(Path.GetFullPath(Path.Combine(caseRoot, "archived-snapshots"))
            + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Archive lease escaped owned root.");
        NoReparseAncestors(receipt.Archive);
        using var archiveLease = new FileStream(receipt.Archive, FileMode.Open, FileAccess.Read, FileShare.Read);
        var pins = new List<Microsoft.Win32.SafeHandles.SafeFileHandle>();
        try
        {
            // All media producers have exited. Pin immutable source bytes only
            // through offline verification/retirement, while allowing alias deletion.
            foreach (var member in receipt.Members)
                pins.Add(File.OpenHandle(Path.Combine(snapshot, member.Name), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete));
            Verify(caseRoot, receipt); // Heavy readback stays OUTSIDE the sampler gate.
            string record = receipt.Archive + ".receipt.json";
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(receipt, new JsonSerializerOptions { WriteIndented = true });
            Require(bytes.Length <= 1_048_576, "Snapshot provenance receipt exceeds1MiB.");
            using (var output = new FileStream(record, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) output.Write(bytes);
            lock (inventoryGate ?? new object())
            {
                beforeRetirement?.Invoke();
                OwnSnapshot(caseRoot, snapshot); // Recheck resolved absolute target immediately before retirement.
                Require(Native.DirectoryIdentity(snapshot) == receipt.DirectoryIdentity, "Snapshot directory changed before retirement.");
                Require(Directory.GetFiles(snapshot).Select(Path.GetFileName).Order(StringComparer.Ordinal)
                    .SequenceEqual(receipt.Members.Select(m => m.Name).Order(StringComparer.Ordinal)), "Snapshot membership changed before retirement.");
                foreach (var member in receipt.Members)
                    Require(ReplayStorage.ObserveInventory(Path.Combine(snapshot, member.Name), member.Extent) == member.Extent,
                        "Snapshot member changed before retirement.");
                foreach (var member in receipt.Members) File.Delete(Path.Combine(snapshot, member.Name));
                // Never recursively remove unarchived additions. An unexpected
                // late member makes this fail and remains beside the full archive.
                Directory.Delete(snapshot, recursive: false);
            }
        }
        finally { foreach (var pin in pins) pin.Dispose(); }
    }
}
