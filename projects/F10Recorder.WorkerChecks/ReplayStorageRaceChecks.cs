using System.Runtime.InteropServices;
using System.Text.Json;

namespace F10Recorder;

internal static class ReplayStorageRaceChecks
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool MoveFileEx(string source, string destination, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    sealed class RenameBeforeOpen : ReplayFiles
    {
        internal bool Renamed;
        internal override ReplayFileExtent ObserveManifest(string path, ReplayFileExtent enumerated)
        {
            if (Path.GetFileName(path) == "segments.csv.tmp") { File.Move(path, path[..^4], true); Renamed = true; }
            return base.ObserveManifest(path, enumerated);
        }
        internal override ReplayFileExtent Observe(string path)
        {
            if (Path.GetFileName(path) == "segments.csv.tmp") File.Move(path, path[..^4], true);
            return base.Observe(path);
        }
    }

    internal static int Run(string root)
    {
        Directory.CreateDirectory(root);
        var results = new List<object>(); int failed = 0;
        void Check(string name, Action action)
        {
            try { action(); results.Add(new { name, passed = true }); }
            catch (Exception ex) { failed++; results.Add(new { name, passed = false, error = ex.ToString() }); }
        }
        void Require(bool value, string message) { if (!value) throw new IOException(message); }
        string race = Path.Combine(root, "rename-before-open"); Directory.CreateDirectory(race);
        File.WriteAllBytes(Path.Combine(race, "segments.csv.tmp"), new byte[8192]);
        Check("manifest-replaced-between-enumeration-and-open", () =>
        {
            var files = new RenameBeforeOpen();
            var inventory = ReplayStorage.Inventory([race], files);
            Require(files.Renamed, "Deterministic replacement schedule was not exercised.");
            Require(ReplayStorage.Bytes(inventory) >= 8192, "Volatile allocation must remain charged.");
        });
        string probe = Path.Combine(root, "rename-sharing"); Directory.CreateDirectory(probe);
        string target = Path.Combine(probe, "segments.csv"), source = target + ".tmp";
        File.WriteAllBytes(target, new byte[8192]); File.WriteAllBytes(source, new byte[12288]);
        Check("sensitivity-target-reader-denies-native-replacement", () =>
        {
            using var reader = File.OpenHandle(target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            bool moved = MoveFileEx(source, target, 1);
            results.Add(new { name = "native-replace-probe", moved, win32 = moved ? 0 : Marshal.GetLastWin32Error() });
            Require(!moved, "Sensitivity control failed to reproduce target-reader denial.");
        });
        File.WriteAllBytes(source, new byte[4096]);
        Check("native-replace-no-reader", () => Require(MoveFileEx(source, target, 1), "Native replace failed without a reader."));
        foreach (var item in new[] { (Name: "metadata-target", Path: target, Access: 0x80u), (Name: "data-source", Path: source, Access: 0x80000000u), (Name: "directory", Path: probe, Access: 0x81u) })
        {
            File.WriteAllBytes(source, new byte[4096]);
            Check("probe-" + item.Name, () =>
            {
                using var handle = CreateFile(item.Path, item.Access, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
                Require(!handle.IsInvalid, "Probe open failed.");
                bool moved = MoveFileEx(source, target, 1);
                results.Add(new { name = item.Name, moved, win32 = moved ? 0 : Marshal.GetLastWin32Error() });
                Require(moved == (item.Name != "metadata-target"), "Unexpected native replacement probe result.");
            });
        }
        string report = Path.Combine(root, "storage-race-checks.json");
        File.WriteAllText(report, JsonSerializer.Serialize(new { failed, results }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(report + ": " + failed + " failures");
        return failed == 0 ? 0 : 1;
    }
}
