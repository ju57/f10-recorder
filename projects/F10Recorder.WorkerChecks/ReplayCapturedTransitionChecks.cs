using System.Diagnostics;
using System.Text.Json;

namespace F10Recorder;

internal static partial class ReplayCalibrationRun
{
    internal static int CapturedTransitionChecks(string path, string hash)
    {
        Require(new FileInfo(path).Length < 262144 && HashFile(path) == hash, "Captured-transition plan changed.");
        using var document = JsonDocument.Parse(File.ReadAllText(path)); var p = document.RootElement;
        Require(p.GetProperty("kind").GetString() == "issue14-captured-transition-v1" && p.GetProperty("maximumChildren").GetInt32() == 3
            && p.GetProperty("deadlineSeconds").GetInt32() == 210 && p.GetProperty("budgetBytes").GetInt64() == 26_000_000_000,
            "Captured-transition bounds changed.");
        foreach (var binding in p.GetProperty("files").EnumerateObject()) Require(HashFile(binding.Name) == binding.Value.GetString(), "Transition input changed: " + binding.Name);
        string root = Path.GetFullPath(p.GetProperty("root").GetString()), family = Path.GetDirectoryName(root);
        string captured = p.GetProperty("capturedSource").GetString();
        Require(p.GetProperty("files").TryGetProperty(Path.GetFullPath(Environment.ProcessPath), out _)
            && p.GetProperty("files").TryGetProperty(typeof(ReplayCalibrationRun).Assembly.Location, out _), "Actual transition host/consumer unbound.");
        MediaDependency.RootDirectory = p.GetProperty("engineRoot").GetString(); string engine = MediaDependency.ResolveExecutable();
        Require(p.GetProperty("files").GetProperty(engine).GetString() == MediaDependency.ExecutableSha256.ToUpperInvariant(), "Transition engine unbound.");
        var original = ReplaySelection.Read(Path.Combine(captured, "segments.csv"), captured);
        Require(original.Count > 300 && original.All(segment => p.GetProperty("files").TryGetProperty(segment.Path, out _)), "Complete captured inputs are not bound.");
        double end = original[^1].End; Require(end - original[1].Start >= 300, "Captured history cannot supply a full300seconds.");
        using var owned = PilotStorageObservation.Create(root); Log.DirectoryPath = root; Log.FileName = "captured-transition.log";
        var clock = Stopwatch.StartNew();
        Write(Path.Combine(root, "trace-controls.json"), BoundedCalibrationTraceChecks.Run());
        var history = Directory.GetDirectories(family).Where(item => Path.GetFullPath(item) != root).ToArray();
        var plan = new CalibrationPlan(root, MediaDependency.RootDirectory, "captured-transition", new(), Order, history, 0, 0,
            BudgetBytes: 26_000_000_000, StopBytes: 24_000_000_000, FloorBytes: 60_000_000_000, DeadlineSeconds: 205,
            RetainedOfflineSeconds: 0, MetadataHandleRoots: history, ReuseCurrentMetadataHandles: true, FileLimit: 24576, DirectoryLimit: 3072,
            RetainedBudgetBytes: 18_000_000_000, OutputComponentBytes: 1_100_000_000, OutsideBuildAllowanceBytes: 268_435_456,
            FreezeRetainedAllocations: true, RetainActiveOutputHandles: true);
        int traceWrites = 0;
        using var sampler = new Sampler(plan, active: false, traceOpen: () => new BoundedCalibrationTraceChecks.WriteControlStream(
            new FileStream(Path.Combine(root, "storage.csv"), FileMode.CreateNew, FileAccess.Write, FileShare.Read),
            () => { if (Interlocked.Increment(ref traceWrites) == 40) Thread.Sleep(650); }));
        string cell = Path.Combine(root, "T01"), session = Path.Combine(cell, "buffer", "session-closed-capture"); Directory.CreateDirectory(session);
        foreach (var segment in original) Require(Native.CreateHardLink(Path.Combine(session, Path.GetFileName(segment.Path)), segment.Path, IntPtr.Zero), "Closed captured input link failed.");
        File.Copy(Path.Combine(captured, "segments.csv"), Path.Combine(session, "segments.csv"));
        var diagnostic = new ClosedValidationSource(session, null, engine, true, sampler, null, reencode: true, cutoff: end);
        string failure = null, traceStallPhase = null; ReplaySaveResult result = null;
        using var cache = new ReplayCache(new RetainFiles(sampler, true), cacheDirectory: Path.Combine(cell, "buffer"));
        try
        {
            sampler.BeginActive(); var id = cache.RegisterSession(diagnostic); var save = cache.TrySave(id, 0, cell); Require(save.Accepted, save.Rejection);
            while (!save.Completion.IsCompleted) { Require(clock.Elapsed.TotalSeconds < 205, "Captured-transition deadline."); sampler.Check(); Thread.Sleep(25); }
            result = save.Completion.GetAwaiter().GetResult();
            Require(!result.Succeeded && result.Error.Contains(ClosedValidationSource.Terminal) && diagnostic.OfflineEntered
                && diagnostic.EncoderReceipt?.Success == true && diagnostic.Receipt?.Success == true && diagnostic.Validation?.Kind == "FullDecode",
                "Actual captured encode/validation transition failed: " + result.Error);
            cache.CloseAsync().GetAwaiter().GetResult(); sampler.EndOffline();
        }
        catch (Exception error) { failure = error.ToString(); }
        finally
        {
            try { cache.CloseAsync().GetAwaiter().GetResult(); } catch (Exception error) { failure = (failure ?? "") + "\nCleanup: " + error; }
            try
            {
                sampler.StopAndCheck();
                traceStallPhase = File.ReadLines(Path.Combine(root, "storage.csv")).Skip(39).FirstOrDefault()?.Split(',').ElementAtOrDefault(12);
                Require(traceWrites >= 40 && sampler.TraceMaximumWriteMilliseconds >= 600 && traceStallPhase == "active",
                    "The controlled650ms trace-write stall did not execute during active native sampling.");
            }
            catch (Exception error) { failure = (failure ?? "") + "\nCleanup: " + error; }
        }
        Write(Path.Combine(root, "verification.json"), new { result = failure == null ? "passed" : "failed", failure, requestedEnd = end,
            save = result, diagnostic.Validation,
            encoder = diagnostic.EncoderReceipt == null ? null : SaveProcess(root, "encoder", diagnostic.EncoderReceipt),
            validator = diagnostic.Receipt == null ? null : SaveProcess(root, "validator", diagnostic.Receipt),
            sampler.MaximumActiveGap, sampler.MaximumOfflineGap, sampler.LastObservation, operations = sampler.OutputOperationTimings,
            trace = sampler.TraceReceipt, traceStallPhase,
            elapsedSeconds = clock.Elapsed.TotalSeconds,
            effects = "Existing completed captured TS inputs; onehelper,oneNVENCencoder,oneCPUdecoder. No acquisition/UI/device/startup/preferences/model activation. Diagnostic sentinel suppresses publication; failed live cases stay failed." });
        Console.WriteLine("Captured encode/validation transition: " + (failure == null ? "passed" : "failed")); return failure == null ? 0 : 1;
    }
}
