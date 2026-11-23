using System.Diagnostics;
using System.Text.Json;

namespace F10Recorder;

internal static class ReplayMemoryObservationChecks
{
    // One self-bounded process commits64MiB. No child, media, device, profile,
    // registry, worker or UI action. This checks fresh observation, not #55.
    internal static int Run(string root)
    {
        if (Directory.Exists(root)) throw new IOException("Fresh memory-control root required.");
        Directory.CreateDirectory(root); var clock = Stopwatch.StartNew();
        using var self = Process.GetCurrentProcess(); long cachedBefore = self.WorkingSet64;
        if (!ReplayExportProcess.TryWorkingSet(self, out long before)) throw new IOException("Initial native memory query failed.");
        if (before >= 134217728) throw new IOException("Initial process memory cannot fit the bounded control.");
        byte[] committed = new byte[64 * 1024 * 1024];
        for (int n = 0; n < committed.Length; n += 4096) committed[n] = 123;
        bool measured = ReplayExportProcess.TryWorkingSet(self, out long after);
        long cachedAfter = self.WorkingSet64;
        bool crossed = measured && after >= before + 32L * 1024 * 1024;
        bool passed = crossed && after < 268435456 && clock.Elapsed.TotalSeconds < 5;
        File.WriteAllText(Path.Combine(root, "receipt.json"), JsonSerializer.Serialize(new { result = passed ? "passed" : "failed",
            before, after, cachedBefore, cachedAfter, committedBytes = committed.Length, thresholdBytes = before + 32L * 1024 * 1024,
            freshQueryDetectsCrossing = crossed, cachedQueryReused = cachedBefore == cachedAfter,
            seconds = clock.Elapsed.TotalSeconds, maximumBytes = 268435456, source = "K32GetProcessMemoryInfo on the same owned live handle" }));
        GC.KeepAlive(committed);
        return passed ? 0 : 1;
    }
}
