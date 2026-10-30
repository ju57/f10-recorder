using System.Text.Json;
using System.Diagnostics;

namespace F10Recorder;

internal static class ReplayCalibrationTimingChecks
{
    internal static int ProspectiveSampler(string root, string planFile, string expectedHash, bool startup = false)
    {
        using (var input = File.OpenRead(planFile))
            if (Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input)) != expectedHash) throw new IOException("Prospective plan changed.");
        using var document = JsonDocument.Parse(File.ReadAllText(planFile));
        CalibrationPlan plan;
        if (document.RootElement.TryGetProperty("PriorRoot", out _))
        {
            var continuation = document.RootElement.Deserialize<CalibrationContinuationPlan>();
            ReplayCalibrationRun.ValidateContinuation(continuation); plan = continuation.Plan;
        }
        else
        {
            plan = document.RootElement.GetProperty("plan").Deserialize<CalibrationPlan>();
            if (startup) ReplayCalibrationStartup.ValidateResources(plan); else ReplayCalibrationRun.ValidateResources(plan);
        }
        root = Path.GetFullPath(root);
        if (Directory.Exists(root) || plan.RetainedRoots.Any(r => root.StartsWith(Path.GetFullPath(r) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Use a fresh independent sampler control root.");
        Directory.CreateDirectory(root);
        int retainedFiles = plan.RetainedRoots.Sum(r => Directory.GetFiles(r, "*", SearchOption.AllDirectories).Length);
        int retainedDirectories = plan.RetainedRoots.Sum(r => 1 + Directory.GetDirectories(r, "*", SearchOption.AllDirectories).Length);
        int fixtureFiles = plan.FileLimit - retainedFiles - 8, fixtureDirectories = plan.DirectoryLimit - retainedDirectories - 1;
        if (fixtureFiles < 350 || fixtureDirectories < 1) throw new IOException("Retained history leaves no declared metadata fixture envelope.");
        var directories = Enumerable.Range(0, fixtureDirectories).Select(n => Path.Combine(root, "d-" + n)).ToArray();
        foreach (string directory in directories) Directory.CreateDirectory(directory);
        var tiny = new byte[512];
        for (int n = 0; n < fixtureFiles; n++) File.WriteAllBytes(Path.Combine(directories[n % directories.Length],
            n < 350 ? "chunk-" + n.ToString("D9") + ".ts" : "fixture-" + n.ToString("D5") + ".bin"), tiny);
        var changed = Directory.GetFiles(root, "chunk-*.ts", SearchOption.AllDirectories);
        using var sampler = new ReplayCalibrationRun.Sampler(plan with { Root = root }, active: false);
        sampler.BeginActive();
        using var cancellation = new CancellationTokenSource();
        var writer = Task.Run(() =>
        {
            var block = new byte[32768];
            for (int n = 0; n < 60 && !cancellation.IsCancellationRequested; n++)
            {
                using (var output = new FileStream(changed[n % changed.Length], FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                { output.Write(block); output.Flush(true); }
                Thread.Sleep(50);
            }
        });
        Exception failure = null; var timer = Stopwatch.StartNew();
        try
        {
            while (Volatile.Read(ref sampler.Observations) < 25)
            { sampler.Check(); if (timer.Elapsed.TotalSeconds > 15) throw new IOException("Prospective sampler control deadline."); Thread.Sleep(25); }
            writer.GetAwaiter().GetResult(); sampler.EndActive(); sampler.StopAndCheck();
        }
        catch (Exception error) { failure = error; }
        finally { cancellation.Cancel(); writer.GetAwaiter().GetResult(); }
        File.WriteAllText(Path.Combine(root, "prospective-sampler.json"), JsonSerializer.Serialize(new {
            result = failure == null ? "passed" : "failed", error = failure?.ToString(), retainedFiles, retainedDirectories,
            fixtureFiles, fixtureDirectories, sampler.MaximumFiles, sampler.MaximumDirectories, sampler.MaximumActiveGap,
            sampler.PeakUsed, elapsedSeconds = timer.Elapsed.TotalSeconds, planFile, expectedHash,
            effects = "Read-only retained roots, under4MiB initial dummy bytes and under2MiB additional ordinary writes across350 mutable chunk names; metadata path/count control only, no engine/child/capture/audio/GPU/UI/catalog write." }, new JsonSerializerOptions { WriteIndented = true }));
        return failure == null ? 0 : 1;
    }
    internal static int PauseRun(string root, string[] retainedRoots)
    {
        if (Directory.Exists(root)) throw new IOException("Pause control needs a fresh root.");
        Directory.CreateDirectory(root);
        int retained = retainedRoots.Sum(r => Directory.GetFiles(r, "*", SearchOption.AllDirectories).Length);
        string fixture = Path.Combine(root, "small-files"); Directory.CreateDirectory(fixture);
        for (int i = 0; i < 4096 - retained - 2; i++) File.WriteAllBytes(Path.Combine(fixture, "fixture-" + i.ToString("D5") + ".bin"), new byte[512]);
        string growing = Path.Combine(root, "growing.partial.mp4"); File.WriteAllBytes(growing, []);
        var plan = new CalibrationPlan(root, "unused-no-engine", "nonmedia-pause-control", new(), [], retainedRoots, 486.9295893, 10);
        Exception failure = null;
        using var cancel = new CancellationTokenSource();
        Task writer = Task.Run(() =>
        {
            using var file = new FileStream(growing, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            var block = new byte[1_048_576];
            for (int i = 0; i < 32 && !cancel.IsCancellationRequested; i++) { file.Write(block); file.Flush(true); Thread.Sleep(10); }
        });
        ReplayCalibrationRun.Sampler sampler = null;
        try
        {
            sampler = new(plan); var deadline = Stopwatch.StartNew();
            while (Volatile.Read(ref sampler.Observations) < 20)
            { sampler.Check(); if (deadline.Elapsed.TotalSeconds > 15) throw new IOException("Pause control deadline."); Thread.Sleep(10); }
            sampler.StopAndCheck();
        }
        catch (Exception ex) { failure = ex; }
        finally { cancel.Cancel(); writer.GetAwaiter().GetResult(); sampler?.Dispose(); }
        File.WriteAllText(Path.Combine(root, "pause-check.json"), JsonSerializer.Serialize(new { result = failure == null ? "passed" : "failed", error = failure?.ToString(),
            observations = sampler?.Observations, maximumGap = sampler?.MaximumGap, retained, fixtureFiles = 4096 - retained - 2,
            growingBytes = new FileInfo(growing).Length, effects = "tiny file fixtures plus max32MiB dummy output; no media/engine/UI/audio/GPU" }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Pause control: " + (failure == null ? "passed" : "failed") + " " + root);
        return failure == null ? 0 : 1;
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
        Check("observed-418ms-scan-does-not-add100ms", () =>
        {
            int wait = ReplayCalibrationRun.SamplingWaitMilliseconds(0, 418);
            Require(wait == 0, "The observed418ms scan needs zero additional wait, not100ms.");
            Require(ReplayCalibrationRun.SamplingGap(0, 418 + wait) == 418, "Next start must stay inside500ms.");
        });
        Check("short-scan-retains100ms-start-cadence", () =>
            Require(ReplayCalibrationRun.SamplingWaitMilliseconds(1000, 1023) == 77, "A23ms scan leaves77ms, not100ms."));
        Check("500ms-accepted-501ms-rejected", () =>
        {
            Require(ReplayCalibrationRun.SamplingGap(0, 500) == 500, "Exact500ms boundary must remain admissible.");
            bool rejected = false; try { ReplayCalibrationRun.SamplingGap(0, 501); } catch (IOException) { rejected = true; }
            Require(rejected, "A501ms gap must fail.");
        });
        Check("fractional-and-invalid-gap-rejected", () =>
        {
            foreach (double gap in new[] { 500.1, -0.1, double.NaN, double.PositiveInfinity })
            {
                bool rejected = false; try { ReplayCalibrationRun.SamplingGap(0, gap); } catch (IOException) { rejected = true; }
                Require(rejected, "Invalid or over500ms gap must fail: " + gap);
            }
        });
        File.WriteAllText(Path.Combine(root, "timing-checks.json"), JsonSerializer.Serialize(new { failed, results }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Sampler timing checks: " + failed + " failures; " + root);
        return failed == 0 ? 0 : 1;
    }

    internal static int SamplerRun(string root, string[] retainedRoots)
    {
        if (Directory.Exists(root)) throw new IOException("Timing tracer requires a fresh root.");
        Directory.CreateDirectory(root);
        var plan = new CalibrationPlan(root, "unused-no-engine", "nonmedia-sampler-tracer", new(), [], retainedRoots, 122.7460017, 7);
        using var sampler = new ReplayCalibrationRun.Sampler(plan);
        var deadline = Stopwatch.StartNew();
        while (Volatile.Read(ref sampler.Observations) < 20)
        {
            sampler.Check();
            if (deadline.Elapsed.TotalSeconds > 10) throw new IOException("Nonmedia sampler tracer deadline exceeded.");
            Thread.Sleep(10);
        }
        sampler.StopAndCheck();
        File.WriteAllText(Path.Combine(root, "sampler-check.json"), JsonSerializer.Serialize(new { result = "passed", sampler.Used,
            sampler.MaximumGap, sampler.Observations, retainedRoots, effects = "native storage only; no engine/media/UI/audio/profile/startup" }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Nonmedia cold sampler passed: " + root);
        return 0;
    }
}
