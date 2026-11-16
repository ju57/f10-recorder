using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace F10Recorder;

// CPU/allocation discriminator only. Small synthetic samples are never decoded,
// published, or represented as source media or an actual storage observation.
internal static class ReplayMp4MetadataCosts
{
    internal static int Run(string root)
    {
        string prefix = Path.GetFullPath("artifacts") + Path.DirectorySeparatorChar;
        if (!root.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || Directory.Exists(root) || File.Exists(root))
            throw new IOException("Fresh isolated metadata report directory required.");
        Directory.CreateDirectory(root);
        var whole = Stopwatch.StartNew();
        var rows = new List<object>();
        Exception failure = null;
        try
        {
            // Five minutes is deliberately first: no writer warm-up before it.
            foreach (int seconds in new[] { 300, 300, 60, 1800 })
            {
                var elapsed = Stopwatch.StartNew();
                int checks = 0;
                long lastCheck = Stopwatch.GetTimestamp();
                double maximumCheckGap = 0;
                void Check()
                {
                    long now = Stopwatch.GetTimestamp();
                    maximumCheckGap = Math.Max(maximumCheckGap, Stopwatch.GetElapsedTime(lastCheck, now).TotalMilliseconds);
                    lastCheck = now; checks++;
                    if (whole.Elapsed.TotalSeconds > 15) throw new IOException("15 second synthetic-work deadline.");
                }
                long logicalAudio = seconds * 48000L;
                int audioCount = checked((int)((logicalAudio + 1023) / 1024));
                var expectedAudio = Enumerable.Range(0, audioCount)
                    .Select(i => new ReplayExpectedAudio(Math.Min(1024, logicalAudio - i * 1024L), "synthetic-no-source-hash")).ToArray();
                long allocatedAt = GC.GetAllocatedBytesForCurrentThread();
                int[] collectionsAt = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
                // Synthetic baseline-profile 1920x1080 configuration. These
                // five-byte samples intentionally assert no playable content.
                byte[] sps = Convert.FromHexString("67420028DA01E0089F95");
                byte[] pps = Convert.FromHexString("68CE3C80");
                using var stream = new MemoryStream(8 * 1024 * 1024);
                var timing = new ReplayMp4WriteTiming();
                var writer = new ReplayMp4PacketWriter(stream, 60, 0, sps, pps, seconds * 60, 0,
                    logicalAudio, expectedAudio, Check, timing);
                byte[] sample = [0, 0, 0, 1, 0x65];
                for (int i = 0; i < seconds * 60; i++) writer.Video(sample, i % 60 == 0);
                for (int i = 0; i < audioCount; i++) writer.Audio(sample.AsSpan(0, 1));
                double samplesMilliseconds = elapsed.Elapsed.TotalMilliseconds;
                long completeAt = Stopwatch.GetTimestamp();
                writer.Complete();
                double completeMilliseconds = Stopwatch.GetElapsedTime(completeAt).TotalMilliseconds;
                Check();
                if (writer.State != ReplayMp4WriterState.Completed || stream.Length > 8 * 1024 * 1024)
                    throw new IOException("Synthetic writer did not complete within the 8MiB stream limit.");
                rows.Add(new { seconds, order = rows.Count, pictures = seconds * 60, audioCount, samplesMilliseconds,
                    completeMilliseconds, totalMilliseconds = elapsed.Elapsed.TotalMilliseconds,
                    maximumCheckGap, checks, timing,
                    allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedAt,
                    collections = Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - collectionsAt[i]).ToArray(),
                    streamBytes = stream.Length });
            }
        }
        catch (Exception error) { failure = error; Console.Error.WriteLine(error); }
        var bindings = new[] { Environment.ProcessPath, typeof(ReplayMp4MetadataCosts).Assembly.Location,
            typeof(ReplayMp4PacketWriter).Assembly.Location, typeof(CaptureSession).Assembly.Location }
            .Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(file => file, file => {
                using var input = File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(input)); });
        byte[] report = JsonSerializer.SerializeToUtf8Bytes(new { result = failure == null ? "synthetic-metadata-collected" : "failed",
            error = failure?.ToString(), rows, bindings, seconds = whole.Elapsed.TotalSeconds,
            scope = "No capture, media reads, codec children, publication, account catalog or storage qualification. First five-minute invocation and repeat share one process; other durations use an already invoked writer. No OS-cache-cold, real-source/storage or Saved latency claim." },
            new JsonSerializerOptions { WriteIndented = true });
        if (report.Length > 65536) throw new IOException("64KiB synthetic report cap.");
        File.WriteAllBytes(Path.Combine(root, "result.json"), report);
        return failure == null ? 0 : 1;
    }
}
