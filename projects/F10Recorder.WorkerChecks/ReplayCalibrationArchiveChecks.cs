using System.Formats.Tar;
using System.Text.Json;

namespace F10Recorder;

internal static class ReplayCalibrationArchiveChecks
{
    internal static int Run(string root)
    {
        Directory.CreateDirectory(root); var results = new List<object>(); int failed = 0;
        void Require(bool value, string message) { if (!value) throw new IOException(message); }
        void Check(string name, Action<string, string> action)
        {
            string owned = Path.Combine(root, name), snapshot = Path.Combine(owned, "buffer", "save-owned");
            Directory.CreateDirectory(snapshot);
            File.WriteAllBytes(Path.Combine(snapshot, "00000000.ts"), Enumerable.Range(0, 4097).Select(i => (byte)(i % 251)).ToArray());
            File.WriteAllText(Path.Combine(snapshot, "concat.txt"), "file '00000000.ts'\n");
            try { action(owned, snapshot); results.Add(new { name, passed = true }); }
            catch (Exception ex) { failed++; results.Add(new { name, passed = false, error = ex.ToString() }); }
        }
        void Reject(Action action)
        { bool rejected = false; try { action(); } catch (IOException) { rejected = true; } Require(rejected, "Expected preservation rejection."); }
        Check("roundtrip-before-retirement", (owned, snapshot) =>
        {
            long reserved = 0; var receipt = ReplayCalibrationArchive.Create(owned, snapshot, b => reserved = b);
            Require(reserved > 4097 && Directory.Exists(snapshot), "Original remains until verification/retirement.");
            using (var input = File.OpenRead(receipt.Archive))
            using (var reader = new TarReader(input))
            {
                int count = 0; TarEntry entry;
                while ((entry = reader.GetNextEntry()) != null)
                {
                    if (entry.Name.EndsWith(".ts"))
                    {
                        using var data = new MemoryStream(); entry.DataStream.CopyTo(data);
                        Require(data.ToArray().SequenceEqual(Enumerable.Range(0, 4097).Select(i => (byte)(i % 251))), "Archive bytes differ.");
                    }
                    count++;
                }
                Require(count == 2, "Archive membership differs.");
            }
            ReplayCalibrationArchive.Retire(owned, receipt);
            Require(!Directory.Exists(snapshot) && File.Exists(receipt.Archive) && File.Exists(receipt.Archive + ".receipt.json"), "Verified archive/provenance missing.");
        });
        Check("budget-failure-preserves-original", (owned, snapshot) =>
        {
            Reject(() => ReplayCalibrationArchive.Create(owned, snapshot, _ => throw new IOException("Controlled admission rejection.")));
            Require(Directory.GetFiles(snapshot).Length == 2 && !Directory.Exists(Path.Combine(owned, "archived-snapshots")), "Budget failure changed evidence.");
        });
        Check("tampered-archive-preserves-original", (owned, snapshot) =>
        {
            var receipt = ReplayCalibrationArchive.Create(owned, snapshot, _ => { });
            using (var output = File.OpenWrite(receipt.Archive)) output.WriteByte(255);
            Reject(() => ReplayCalibrationArchive.Retire(owned, receipt));
            Require(Directory.GetFiles(snapshot).Length == 2, "Tamper rejection lost originals.");
        });
        Check("changed-native-original-preserved", (owned, snapshot) =>
        {
            var receipt = ReplayCalibrationArchive.Create(owned, snapshot, _ => { });
            File.WriteAllBytes(Path.Combine(snapshot, "00000000.ts"), new byte[4097]);
            Reject(() => ReplayCalibrationArchive.Retire(owned, receipt));
            Require(Directory.GetFiles(snapshot).Length == 2, "Changed original lost.");
        });
        Check("outside-owned-root-rejected", (owned, snapshot) =>
        {
            Reject(() => ReplayCalibrationArchive.Create(Path.Combine(owned, "unrelated"), snapshot, _ => { }));
            Require(Directory.GetFiles(snapshot).Length == 2, "Outside-root rejection lost originals.");
        });
        File.WriteAllText(Path.Combine(root, "archive-checks.json"), JsonSerializer.Serialize(new { failed, results }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Snapshot archive controls: " + failed + " failures; " + root); return failed == 0 ? 0 : 1;
    }
}
