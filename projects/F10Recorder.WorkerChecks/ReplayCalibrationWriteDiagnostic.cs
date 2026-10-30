using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace F10Recorder;

// One bounded question: does a producer's durable cache flush correlate with
// the blocked metadata read seen in the retained full rehearsal? No engine.
internal static class ReplayCalibrationWriteDiagnostic
{
    internal static int Run(string planFile, string expectedHash, string root, double priorSeconds)
    {
        using (var input = File.OpenRead(planFile))
            if (Convert.ToHexString(SHA256.HashData(input)) != expectedHash) throw new IOException("Diagnostic input plan changed.");
        var original = JsonSerializer.Deserialize<CalibrationPlan>(File.ReadAllText(planFile));
        if (!double.IsFinite(priorSeconds) || priorSeconds < 0 || priorSeconds + original.RetainedElapsedSeconds >= 9000)
            throw new IOException("Diagnostic prior elapsed time is invalid.");
        using var owned = PilotStorageObservation.Create(root);
        var clock = Stopwatch.StartNew(); var results = new List<object>();
        var buffer = new byte[1_048_576]; RandomNumberGenerator.Fill(buffer);
        Directory.CreateDirectory(Path.Combine(root, "ordinary-flush")); Directory.CreateDirectory(Path.Combine(root, "durable-flush"));
        foreach (bool durable in new[] { false, true })
        {
            string path = Path.Combine(root, durable ? "durable-flush" : "ordinary-flush"); Directory.CreateDirectory(path);
            var plan = original with { Root = path, RetainedRoots = [.. original.RetainedRoots, original.Root, Path.Combine(root, durable ? "ordinary-flush" : "durable-flush")],
                RetainedElapsedSeconds = original.RetainedElapsedSeconds + priorSeconds + clock.Elapsed.TotalSeconds };
            using var sampler = new ReplayCalibrationRun.Sampler(plan, active: false);
            sampler.RequireTime(60); sampler.BeginActive();
            var creation = Stopwatch.StartNew();
            var output = new FileStream(Path.Combine(path, "dummy.mp4"), new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write,
                Share = FileShare.ReadWrite | FileShare.Delete, BufferSize = buffer.Length, PreallocationSize = 900_000_000 });
            double createMs = creation.Elapsed.TotalMilliseconds;
            double writeMs = 0, flushMs = 0, closeMs = 0; long bytes = 0; string error = null;
            try
            {
                var write = Stopwatch.StartNew();
                while (bytes < 900_000_000)
                {
                    if (clock.Elapsed.TotalSeconds > 120) throw new IOException("Diagnostic exceeded120seconds.");
                    int count = (int)Math.Min(buffer.Length, 900_000_000 - bytes); output.Write(buffer, 0, count); bytes += count;
                    if (bytes % (64L * buffer.Length) == 0) sampler.Check();
                }
                writeMs = write.Elapsed.TotalMilliseconds;
                var flush = Stopwatch.StartNew(); output.Flush(durable); flushMs = flush.Elapsed.TotalMilliseconds;
            }
            catch (Exception ex) { error = ex.ToString(); }
            finally { var close = Stopwatch.StartNew(); output.Dispose(); closeMs = close.Elapsed.TotalMilliseconds; }
            try { sampler.EndActive(); sampler.StopAndCheck(); }
            catch (Exception ex) { error ??= ex.ToString(); }
            results.Add(new { durable, bytes, createMs, writeMs, flushMs, closeMs, error, sampler.Failure, sampler.SlowestLeafPath,
                sampler.SlowestLeafMilliseconds, sampler.MaximumActiveGap, elapsedSeconds = clock.Elapsed.TotalSeconds });
            Console.WriteLine($"Diagnostic durable={durable}: flush={flushMs:F1}ms; close={closeMs:F1}ms; max leaf={sampler.SlowestLeafMilliseconds:F1}ms; {sampler.SlowestLeafPath}");
        }
        File.WriteAllText(Path.Combine(root, "diagnostic.json"), JsonSerializer.Serialize(new { result = "diagnostic-completed-not-a-rehearsal-pass",
            elapsedSeconds = clock.Elapsed.TotalSeconds, newMediaChildren = 0, results, maximumNewBytes = 1_800_000_000L }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}
