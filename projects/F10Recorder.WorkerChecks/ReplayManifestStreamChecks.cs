using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace F10Recorder;

// Ordinary tiny CSV fixtures and the native replacement API only. No engine,
// capture, audio, GPU, shortcut, visible window or recorder worker starts.
internal static class ReplayManifestStreamChecks
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool MoveFileEx(string from, string to, uint flags);
    static void Require(bool value, string reason) { if (!value) throw new IOException(reason); }
    static string Line(int n, string extension = ".ts") => $"chunk-{n:D9}{extension},{n},{n + 1}";
    static void Reject(Action action)
    {
        bool rejected = false; try { action(); } catch (Exception ex) when (ex is IOException or ArgumentException) { rejected = true; }
        Require(rejected, "Invalid/incomplete manifest state was accepted.");
    }
    internal static int Run(string root)
    {
        Require(!Directory.Exists(root) && !File.Exists(root), "Manifest checks need fresh evidence.");
        Directory.CreateDirectory(root); var results = new List<object>(); int failed = 0;
        void Check(string name, Action<string> action)
        {
            string path = Path.Combine(root, name); Directory.CreateDirectory(path);
            try { action(path); results.Add(new { name, passed = true }); }
            catch (Exception ex) { failed++; results.Add(new { name, passed = false, error = ex.ToString() }); }
        }
        Check("native-reader-conflict-sensitivity", path =>
        {
            string file = Path.Combine(path, "segments.csv"), temporary = file + ".tmp";
            File.WriteAllText(file, Line(0) + "\n"); File.WriteAllText(temporary, Line(1) + "\n");
            using (var reader = File.OpenHandle(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                Require(!MoveFileEx(temporary, file, 1) && Marshal.GetLastWin32Error() == 5, "Legacy named read did not reproduce the Windows replacement conflict.");
            Require(MoveFileEx(temporary, file, 1), "Control replacement did not recover after reader closure.");
        });
        Check("progress-dispatch-and-bounded-list", path =>
        {
            var stream = new ReplayManifestStream(path, ".ts", 3);
            foreach (string progress in new[] { "frame=1", "out_time_us=1234567", "speed=1.01x", "progress=end", "" })
                Require(!stream.Accept(progress), "Manifest parser consumed a progress record.");
            for (int n = 0; n < 5; n++) Require(stream.Accept(Line(n)), "CSV entry was not consumed.");
            stream.Complete(); stream.WaitForCompletion(10);
            var segments = stream.Read();
            Require(segments.Select(s => s.Start).SequenceEqual(new[] { 2d, 3d, 4d }) && segments[^1].End == 5,
                "Completed list did not retain the newest bounded entries.");
            Require(!File.Exists(Path.Combine(path, "segments.csv.tmp")), "Published list left a temporary file.");
            Require(ReplaySelection.Select(segments, 4.5, 2).All(s => s.Start >= 2.5), "Production keyframe selection changed.");
        });
        Check("publisher-waits-for-named-reader", path =>
        {
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            bool held = false;
            var stream = new ReplayManifestStream(path, ".ts", 3, (file, directory) =>
            {
                using var reader = File.OpenHandle(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (!held) { held = true; entered.Set(); Require(release.Wait(3000), "Controlled reader was not released."); }
                return ReplaySelection.Read(file, directory);
            });
            stream.Accept(Line(0));
            Task<List<Segment>> read = Task.Run(stream.Read);
            Require(entered.Wait(3000), "Controlled reader did not enter.");
            Task writer = null;
            try
            {
                writer = Task.Run(() => stream.Accept(Line(1)));
                Require(!writer.Wait(100), "Publisher raced the named reader.");
            }
            finally { release.Set(); }
            Require(read.Wait(3000) && writer.Wait(3000), "Coordinated publication did not finish.");
            Require(read.Result.Count == 1 && stream.Read().Count == 2, "Reader/publication ordering lost completed segments.");
        });
        Check("concurrent-reader-and-storage-observer", path =>
        {
            var stream = new ReplayManifestStream(path, ".ts", 10);
            stream.Accept(Line(0));
            using var stop = new CancellationTokenSource();
            Task observer = Task.Run(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    var segments = stream.Read();
                    Require(segments.Count is > 0 and <= 10 && segments.All(s => s.End > s.Start), "Concurrent reader saw an incomplete list.");
                    _ = ReplayStorage.Bytes(ReplayStorage.Inventory([path], new ReplayFiles()));
                    Thread.Yield();
                }
            });
            try
            {
                for (int n = 1; n <= 200; n++) { stream.Accept(Line(n)); stream.CheckHealthy(); }
            }
            finally { stop.Cancel(); Require(observer.Wait(5000), "Observer did not stop."); }
            stream.Complete(); stream.WaitForCompletion(10);
            Require(stream.Read()[^1].End == 201, "Final completed entry was lost.");
        });
        Check("malformed-and-regressing-entries-fail-closed", path =>
        {
            string[] invalid = ["../outside.ts,1,2", "chunk-000000001.ts,1", "chunk-000000001.ts,NaN,2",
                "chunk-000000001.ts,-1,2", "chunk-000000001.ts,1,1", "chunk-000000001.mkv,1,2",
                Line(0), "chunk-000000001.ts,0,0.5", new string('9', 257) + ",0,1"];
            for (int n = 0; n < invalid.Length; n++)
            {
                string cell = Path.Combine(path, n.ToString()); Directory.CreateDirectory(cell);
                var stream = new ReplayManifestStream(cell, ".ts", 3); stream.Accept(Line(0));
                string file = Path.Combine(cell, "segments.csv"); byte[] before = SHA256.HashData(File.ReadAllBytes(file));
                stream.Accept(invalid[n]); Reject(stream.CheckHealthy); Reject(() => stream.Read());
                Require(before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(file))), "Rejected metadata changed the last complete list.");
            }
        });
        Check("publication-failure-preserves-last-good-list", path =>
        {
            var stream = new ReplayManifestStream(path, ".ts", 3); stream.Accept(Line(0));
            string file = Path.Combine(path, "segments.csv"), before = File.ReadAllText(file);
            Directory.CreateDirectory(file + ".tmp");
            stream.Accept(Line(1)); Reject(stream.CheckHealthy); stream.Complete(); Reject(() => stream.WaitForCompletion(10));
            Require(File.ReadAllText(file) == before, "Failed publication damaged completed metadata.");
        });
        Check("final-segment-and-bounded-stdout-drain", path =>
        {
            var incomplete = new ReplayManifestStream(path, ".ts", 3);
            Reject(() => incomplete.WaitForCompletion(1)); Reject(incomplete.CheckHealthy);
            var stream = new ReplayManifestStream(path, ".ts", 3);
            stream.Accept(Line(0)); stream.Accept("chunk-000000001.ts,1,1.125"); stream.Complete(); stream.WaitForCompletion(10);
            Require(stream.Read()[^1].End == 1.125, "Shutdown lost the last completed partial-duration segment.");
            stream.Accept(Line(2)); Reject(stream.CheckHealthy);
        });
        Check("thirty-minute-list-size-and-matroska", path =>
        {
            var stream = new ReplayManifestStream(path, ".mkv", 30 * 60 + 20);
            for (int n = 0; n < 1825; n++) stream.Accept(Line(n, ".mkv"));
            stream.CheckHealthy(); stream.Complete(); stream.WaitForCompletion(10);
            var list = stream.Read();
            Require(list.Count == 1820 && list[0].Start == 5 && list[^1].End == 1825, "30-minute metadata bound or extension support changed.");
            Reject(() => new ReplayManifestStream(path, ".exe", 3)); Reject(() => new ReplayManifestStream(path, ".ts", 4001));
            Require(ReplayManifestStream.OutputArguments().SequenceEqual(new[] { "-segment_list", "pipe:1", "-segment_list_type", "csv", "-segment_list_size", "0" }), "Capture must stream one CSV entry per completed segment.");
        });
        File.WriteAllText(Path.Combine(root, "verification.json"), JsonSerializer.Serialize(new { result = failed == 0 ? "passed" : "failed", results,
            effects = "Native tiny CSV fixtures, coordinated tasks and metadata observation only. Under2MiB retained; about128MB cumulative ordinary writes. No engine/children/capture/audio/GPU/UI/profile/startup changes." }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Manifest publication controls: {results.Count - failed}/{results.Count} passed. {root}"); return failed == 0 ? 0 : 1;
    }
}
