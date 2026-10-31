using System.Diagnostics;
using System.Text.Json;

namespace F10Recorder;

internal static class FrozenCalibrationHistoryChecks
{
    internal static int Run(string root)
    {
        root = Path.GetFullPath(root);
        using var owner = PilotStorageObservation.Create(root);
        var passed = new List<string>();
        void Need(bool value, string reason) { if (!value) throw new IOException(reason); }
        bool Blocked(Action action)
        { try { action(); return false; } catch (IOException) { return true; } catch (System.ComponentModel.Win32Exception) { return true; } }
        var files = new ReplayFiles();
        string past = Path.Combine(root, "past"), current = Path.Combine(root, "current");
        Directory.CreateDirectory(past); Directory.CreateDirectory(current);
        string input = Path.Combine(past, "chunk.ts"), duplicate = Path.Combine(past, "duplicate.ts"), alias = Path.Combine(current, "alias.ts"), growing = Path.Combine(current, "growing.ts");
        File.WriteAllBytes(input, new byte[4096]);
        Need(files.TryHardLink(duplicate, input) && files.TryHardLink(alias, input), "Native hardlink fixture unavailable.");
        File.WriteAllText(Path.Combine(past, "segments.csv"), "closed");
        File.WriteAllBytes(growing, new byte[8192]);
        using (var history = new FrozenCalibrationHistory([past], 16, 8))
        {
            var inventory = ReplayStorage.Inventory([current], files);
            Need(history.FileCount == 3 && history.DirectoryCount == 1 && history.AllocatedBytes == ReplayStorage.Bytes(history.Inventory), "Frozen native baseline differs.");
            var all = history.Inventory.Concat(inventory).ToDictionary(x => x.Key, x => x.Value);
            Need(history.BytesIncluding(inventory) == ReplayStorage.Bytes(all), "Historical/current aliases double counted.");
            passed.Add("fresh-native-baseline-and-cross-root-hardlinks-counted-once");
            Need(Blocked(() => { using var writer = new FileStream(input, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); }), "Historical data write allowed.");
            Need(Blocked(() => { using var writer = new FileStream(alias, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); }), "Historical data write through alias allowed.");
            Need(Blocked(() => File.Delete(input)), "Historical unlink allowed.");
            Need(Blocked(() => Directory.Move(past, past + "-moved")), "Historical root rename allowed.");
            history.Check(); passed.Add("native-read-leases-deny-data-writes-alias-writes-unlink-and-root-rename");
            using (var writer = new FileStream(growing, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { writer.Write(new byte[4096]); writer.Flush(true); }
            inventory = ReplayStorage.Inventory([current], files);
            Need(inventory[growing].Length == 12288 && history.BytesIncluding(inventory) == ReplayStorage.Bytes(history.Inventory.Concat(inventory).ToDictionary(x => x.Key, x => x.Value)), "Mutable current allocation was not refreshed.");
            history.Check(); passed.Add("current-live-growth-remains-fresh-and-does-not-invalidate-history");
            string unexpected = Path.Combine(past, "unexpected.json");
            bool createBlocked = Blocked(() => File.WriteAllText(unexpected, "mutation"));
            bool invalidated = false; var deadline = Stopwatch.StartNew();
            while (!createBlocked && !invalidated && deadline.Elapsed.TotalSeconds < 2)
            { invalidated = Blocked(history.Check); if (!invalidated) Thread.Sleep(5); }
            Need(createBlocked || invalidated, "Historical namespace mutation was not rejected by synchronous native notification.");
            passed.Add("historical-namespace-mutation-cannot-pass-as-unchanged");
        }
        using (var writer = new FileStream(input, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) writer.WriteByte(1);
        Directory.Move(past, past + "-released");
        passed.Add("dispose-releases-file-directory-and-notification-ownership");
        string conflict = Path.Combine(root, "conflict"); Directory.CreateDirectory(conflict);
        string busy = Path.Combine(conflict, "busy.ts"); File.WriteAllBytes(busy, new byte[4096]);
        using (var writer = new FileStream(busy, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            Need(Blocked(() => { using var history = new FrozenCalibrationHistory([conflict], 8, 8); }), "Existing archive writer accepted.");
        Directory.Move(conflict, conflict + "-released");
        passed.Add("existing-writer-rejected-and-partial-constructor-resources-released");
        string monitored = Path.Combine(root, "monitored-past"), active = Path.Combine(root, "active");
        Directory.CreateDirectory(monitored); Directory.CreateDirectory(active);
        string historical = Path.Combine(monitored, "closed.ts"); File.WriteAllBytes(historical, new byte[4096]);
        var plan = new CalibrationPlan(active, "no-engine", "tiny-locked-history-control", new(), [], [monitored], 0, 0,
            FreezeRetainedAllocations: true);
        using (var sampler = new ReplayCalibrationRun.Sampler(plan, active: false))
        {
            sampler.BeginActive();
            File.WriteAllBytes(Path.Combine(active, "current.bin"), new byte[8192]); sampler.Check();
            File.WriteAllText(Path.Combine(monitored, "unexpected.json"), "changed");
            Need(Blocked(() => sampler.Check()), "Integrated sampler admitted a stage after historical mutation.");
        }
        using (var writer = new FileStream(historical, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) writer.WriteByte(1);
        passed.Add("integrated-sampler-rejects-historical-mutation-before-next-stage-and-releases-leases");
        File.WriteAllText(Path.Combine(root, "verification.json"), JsonSerializer.Serialize(new { result = "passed", passed, effects = "Tiny native private files/read leases/change handles; no child, capture, audio, UI, model, account setting or prior evidence change." }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Frozen history controls: {passed.Count}/{passed.Count} passed"); return 0;
    }
}
