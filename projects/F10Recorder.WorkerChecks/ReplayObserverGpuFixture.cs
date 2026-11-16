using System.Diagnostics;
using System.Text.Json;

namespace F10Recorder;

// Closed generated source, not a desktop/audio capture. Lets a separately
// bounded observer prove discovery of a GPU producer born AFTER its first query.
internal static class ReplayObserverGpuFixture
{
    internal static int Run(string root, string engineRoot)
    {
        string artifacts = Path.GetFullPath("artifacts") + Path.DirectorySeparatorChar;
        if (!Path.IsPathFullyQualified(root) || !root.StartsWith(artifacts, StringComparison.OrdinalIgnoreCase)
            || Directory.Exists(root) || File.Exists(root) || !Path.IsPathFullyQualified(engineRoot)) throw new IOException("Fresh private generated-fixture root/engine required.");
        Directory.CreateDirectory(root); MediaDependency.RootDirectory = engineRoot; Log.DirectoryPath = root;
        var clock = Stopwatch.StartNew();
        void Guard()
        {
            if (clock.Elapsed.TotalSeconds > 18 || ReplayStorage.Bytes(ReplayStorage.Inventory([root], new ReplayFiles(), 64, 16)) > 8_388_608
                || ReplayStorage.ObserveCapacity(root).AvailableBytes < 60_000_000_000L) throw new IOException("Generated GPU fixture time/byte/floor bound.");
        }
        void Write(string name, object value)
        {
            byte[] data = JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions(WorkerProtocol.Json) { WriteIndented = true });
            if (data.Length > 262144) throw new IOException("Generated fixture report bound.");
            string path = Path.Combine(root, name), pending = path + ".pending";
            using (var file = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(data); file.Flush(true); }
            File.Move(pending, path);
        }
        using var job = new Native.ProcessJob();
        var arguments = new[] { "-hide_banner", "-nostdin", "-loglevel", "error", "-re", "-f", "lavfi", "-i", "color=c=blue:s=320x192:r=60", "-t", "9",
            "-an", "-c:v", "h264_nvenc", "-preset", "p1", "-progress", "pipe:1", "-stats_period", "0.5", "-f", "null", "-" };
        var receipt = ReplayExportProcess.Run(MediaDependency.ResolveExecutable(), arguments, root, job, started: process =>
        {
            _ = process.SafeHandle;
            Write("producer.json", new { pid = process.Id, birthUtc = process.StartTime.ToUniversalTime(), qpc = Stopwatch.GetTimestamp(), arguments,
                effects = "One9second paced320x192/60H264 GPU color encode to null; no desktop/system/microphone input, audio, UI, playback, installed/startup/vendor/profile/catalog change." });
        }, timeoutMilliseconds: 13000, outputLimitBytes: 131072, checkStorage: Guard);
        Write("producer-closure.json", receipt);
        if (!receipt.Success || !receipt.ResourcesFinal) return 1;
        return 0;
    }
}
