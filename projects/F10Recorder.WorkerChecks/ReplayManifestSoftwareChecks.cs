using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace F10Recorder;

// Plan-bound, two-child CPU-only check of the actual CaptureSession stdout,
// publication and shutdown path. Synthetic video/tone; no device acquisition.
internal static class ReplayManifestSoftwareChecks
{
    static void Require(bool value, string reason) { if (!value) throw new IOException(reason); }
    static string Hash(string path) { using var input = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(input)); }
    internal static string[] Arguments(string target, double duration) => ["-hide_banner", "-loglevel", "warning", "-y",
        "-stats_period", "0.25", "-progress", "pipe:1", "-re", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=30",
        "-re", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000", "-map", "0:v:0", "-map", "1:a:0",
        "-af", "aresample=async=1:first_pts=0", "-ac", "2", "-ar", "48000", "-c:a", "aac", "-b:a", "192k",
        "-c:v", "mpeg2video", "-b:v", "1M", "-g", "30", "-bf", "0", "-sc_threshold", "1000000000",
        "-flags", "+cgop", "-force_key_frames", "expr:gte(t,n_forced*1)", "-t", duration.ToString("F1", System.Globalization.CultureInfo.InvariantCulture),
        "-f", "segment", "-segment_time", "1", "-segment_format", "mpegts", "-reset_timestamps", "1",
        .. ReplayManifestStream.OutputArguments(), target];
    internal static int Run(string planPath, string expectedHash)
    {
        Require(new FileInfo(planPath).Length <= 65536 && Hash(planPath) == expectedHash, "Software manifest plan changed.");
        using var document = JsonDocument.Parse(File.ReadAllText(planPath)); var plan = document.RootElement;
        Require(plan.GetProperty("kind").GetString() == "two-cpu-manifest-controls"
            && plan.GetProperty("children").GetInt32() == 2 && plan.GetProperty("deadlineSeconds").GetInt32() == 60
            && plan.GetProperty("budgetBytes").GetInt64() == 33_554_432, "Software manifest boundary changed.");
        foreach (var file in plan.GetProperty("files").EnumerateObject()) Require(Hash(file.Name) == file.Value.GetString(), "Software input changed: " + file.Name);
        Require(plan.GetProperty("files").TryGetProperty(Path.GetFullPath(typeof(ReplayManifestSoftwareChecks).Assembly.Location), out _)
            && plan.GetProperty("files").TryGetProperty(Path.GetFullPath(typeof(CaptureSession).Assembly.Location), out _), "Runtime assemblies must be bound.");
        string root = plan.GetProperty("root").GetString();
        MediaDependency.RootDirectory = plan.GetProperty("engineRoot").GetString();
        string engine = MediaDependency.ResolveExecutable();
        Require(plan.GetProperty("files").TryGetProperty(Path.GetFullPath(engine), out _), "Managed engine must be bound.");
        using var owned = PilotStorageObservation.Create(root);
        Log.DirectoryPath = root;
        var timer = Stopwatch.StartNew(); var results = new List<object>();
        try
        {
            foreach (string mode in new[] { "natural-eof", "graceful-q" })
            {
                Require(timer.Elapsed.TotalSeconds + 25 <= 60, "Full software control deadline cannot fit.");
                string cell = Path.Combine(root, mode); Directory.CreateDirectory(cell);
                CaptureSession capture = null; var child = Stopwatch.StartNew();
                try
                {
                    RecorderRuntime.CaptureArgumentProbe = original => Arguments(original[^1], mode == "natural-eof" ? 4.2 : 6.2).ToList();
                    capture = new CaptureSession(new Settings { FramesPerSecond = 30, BitrateMbps = 1, SystemAudio = false }, cell, true,
                        cancelled: () => child.Elapsed.TotalSeconds >= 12);
                    int pid = capture.ProcessId;
                    File.WriteAllText(Path.Combine(cell, "arguments.json"), JsonSerializer.Serialize(capture.CaptureArguments));
                    var source = (IReplaySessionSource)capture;
                    while (!capture.HasExited)
                    {
                        Require(child.Elapsed.TotalSeconds < 12, "Software source exceeded its child deadline.");
                        capture.CheckCaptureHealth(0);
                        _ = source.ReadSegments();
                        Require(ReplayStorage.Bytes(ReplayStorage.Inventory([root], new ReplayFiles())) <= 33_554_432,
                            "Software control storage exceeded32MiB.");
                        if (mode == "graceful-q" && capture.Position >= 2.2) break;
                        Thread.Sleep(10);
                    }
                    capture.StopCapture();
                    Require(capture.HasExited && capture.CaptureProducerAbsent && capture.CaptureExitCode == 0, "Software producer did not stop normally.");
                    var segments = source.ReadSegments();
                    Require(segments.Count >= 2 && segments[^1].End >= (mode == "natural-eof" ? 4 : 2.2)
                        && segments.All(s => File.Exists(s.Path)) && capture.Position > 0, "Final list or progress dispatch is incomplete.");
                    Require(!capture.ErrorTail.Contains("failed to rename", StringComparison.OrdinalIgnoreCase)
                        && !File.Exists(Path.Combine(capture.DirectoryPath, "segments.csv.tmp")), "Manifest rename failure or partial publication remains.");
                    Require(child.Elapsed.TotalSeconds <= 25, "Software control including producer drain exceeded25seconds.");
                    results.Add(new { mode, pid, exit = capture.CaptureExitCode, absent = capture.CaptureProducerAbsent,
                        segments = segments.Count, finalEnd = segments[^1].End, capture.Position, diagnostics = capture.ErrorTail });
                }
                finally { RecorderRuntime.CaptureArgumentProbe = null; capture?.Dispose(); }
            }
            File.WriteAllText(Path.Combine(root, "verification.json"), JsonSerializer.Serialize(new { result = "passed-two-software-manifest-controls",
                elapsedSeconds = timer.Elapsed.TotalSeconds, results, newMediaChildren = 2, desktopCapture = false, modelActivated = false }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(root, "failure.json"), JsonSerializer.Serialize(new { error = ex.ToString(), results, elapsedSeconds = timer.Elapsed.TotalSeconds }));
            Console.Error.WriteLine(ex.Message); return 1;
        }
    }
}
