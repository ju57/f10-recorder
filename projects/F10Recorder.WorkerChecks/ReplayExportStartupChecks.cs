using System.Diagnostics;
using System.Text.Json;

namespace F10Recorder;

// A tiny CPU-only real-engine process exercises the actual export adapter's
// lifecycle callbacks. Startup delays never permit a >500ms live evidence gap.
internal static class ReplayExportStartupChecks
{
    internal static int Run(string root, string engineRoot, bool stale = false)
    {
        using var owner = PilotStorageObservation.Create(root);
        MediaDependency.RootDirectory = Path.GetFullPath(engineRoot);
        string executable = MediaDependency.ResolveExecutable();
        using var job = new Native.ProcessJob(recoverable: true);
        var clock = Stopwatch.StartNew(); var events = new List<object>();
        long last = Stopwatch.GetTimestamp(); double maximumGap = 0;
        void Observe()
        {
            double gap = Stopwatch.GetElapsedTime(last).TotalMilliseconds;
            maximumGap = Math.Max(maximumGap, gap);
            events.Add(new { stage = "storage-start", at = clock.Elapsed.TotalMilliseconds, gap });
            if (gap > 500) throw new IOException("Controlled startup storage evidence exceeded500ms.");
            Thread.Sleep(30); // Bounded storage-work schedule, not a refreshed timestamp alone.
            gap = Stopwatch.GetElapsedTime(last).TotalMilliseconds;
            maximumGap = Math.Max(maximumGap, gap);
            if (gap > 500) throw new IOException("Controlled startup scan including overhead exceeded500ms.");
            last = Stopwatch.GetTimestamp();
            events.Add(new { stage = "storage-complete", at = clock.Elapsed.TotalMilliseconds, gap });
        }
        string[] args = ["-hide_banner", "-loglevel", "warning", "-nostdin", "-re", "-f", "lavfi", "-i",
            "color=c=black:s=16x16:r=10", "-t", "1.5", "-f", "null", "-"];
        var receipt = ReplayExportProcess.Run(executable, args, root, job,
            started: _ => { events.Add(new { stage = "seal-start", at = clock.Elapsed.TotalMilliseconds }); Thread.Sleep(stale ? 600 : 250); events.Add(new { stage = "seal-end", at = clock.Elapsed.TotalMilliseconds }); },
            producerStopped: absent =>
            {
                events.Add(new { stage = absent ? "producer-absent" : "unseal-start", at = clock.Elapsed.TotalMilliseconds });
                if (!absent) { Thread.Sleep(250); events.Add(new { stage = "unseal-end", at = clock.Elapsed.TotalMilliseconds }); }
            }, timeoutMilliseconds: 8000, outputLimitBytes: 65536, checkStorage: Observe);
        bool passed = stale ? !receipt.Success && receipt.ProducerAbsent && receipt.Error?.Contains("exceeded500ms") == true
            : receipt.Success && maximumGap <= 500;
        File.WriteAllText(Path.Combine(root, "verification.json"), JsonSerializer.Serialize(new { result = passed ? "passed" : "failed",
            maximumGap, receipt, events, staleMustReject = stale, newMediaChildren = 1,
            effects = "One bounded CPU-generated16x16video/null-output engine; no audio playback/acquisition, desktop/GPU/window, model or user-state change.250msunseal/seal and30msstorage-work schedule; unchanged500msgate." },
            new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Export startup evidence continuity: " + (passed ? "passed" : "failed"));
        return passed ? 0 : 1;
    }
}
