using System.Diagnostics;
using System.Text.Json;

namespace F10Recorder;

internal static class FrozenCalibrationHistoryChecks
{
    internal static int Selected(string root)
    {
        using var owner = PilotStorageObservation.Create(Path.GetFullPath(root));
        var watch = Stopwatch.StartNew();
        var passed = new List<string>();
        void Check(bool value, string name) { if (!value) throw new IOException(name); passed.Add(name); }
        bool Rejected(Action action) { try { action(); return false; } catch (IOException) { return true; } catch (System.ComponentModel.Win32Exception) { return true; } }
        string session = Path.Combine(root, "dummy-session"), admission = Path.Combine(root, "admission");
        Directory.CreateDirectory(session); Directory.CreateDirectory(admission);
        string selected = Path.Combine(session, "selected.dummy"), excluded = Path.Combine(session, "excluded.dummy"), alias = Path.Combine(session, "alias.dummy");
        File.WriteAllBytes(selected, new byte[4096]); File.WriteAllBytes(excluded, new byte[4096]);
        Check(new ReplayFiles().TryHardLink(alias, selected), "native-dummy-alias-created");
        using (var history = new FrozenCalibrationHistory([admission], 65, 4, selectedFiles: [selected]))
        {
            Check(history.FileCount == 1 && history.Inventory.Keys.SequenceEqual(new[] { selected }), "exact-selected-only-no-session-inventory");
            Check(history.Inventory[selected] == ReplayStorage.Observe(selected), "selected-held-native-extent");
            Check(Rejected(() => { using var w = new FileStream(alias, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); }), "alias-write-denied");
            Check(Rejected(() => File.Delete(selected)), "selected-unlink-denied");
            Check(Rejected(() => Directory.Move(session, session + "-moved")), "selected-parent-rename-denied");
            using (var w = new FileStream(excluded, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) w.WriteByte(42);
            history.Check(); Check(true, "excluded-object-remains-unleased-and-writable");
        }
        using (var w = new FileStream(selected, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) w.WriteByte(42);
        Check(true, "selected-disposal-releases-file");
        using (var w = new FileStream(excluded, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            Check(Rejected(() => { using var h = new FrozenCalibrationHistory([admission], 65, 4, selectedFiles: [selected, excluded]); }), "existing-writer-rejected-after-partial-admission");
        using (var w = new FileStream(selected, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) w.WriteByte(43);
        Directory.Move(session, session + "-released"); Check(true, "constructor-failure-releases-file-and-parents");
        Check(Rejected(() => { using var h = new FrozenCalibrationHistory([admission], 65, 4, selectedFiles: [selected, selected]); }), "duplicate-selected-path-rejected");
        string metadata=Path.Combine(admission,"closed-reference.json");File.WriteAllText(metadata,"{\"nonempiricalDummy\":true}");
        using(var references=new FrozenCalibrationHistory([],16,64,selectedFiles:[metadata]))
        {
            Check(references.FileCount==1&&references.Inventory.Keys.Single()==metadata,"empty-root-exact-metadata-only-owner");
            Check(Rejected(()=>{using var writer=new FileStream(metadata,FileMode.Open,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete);}),"reference-metadata-write-denied");
            Check(Rejected(()=>Directory.Move(admission,admission+"-moved")),"reference-metadata-parent-rename-denied");
            using(var writer=new FileStream(Path.Combine(session+"-released","excluded.dummy"),FileMode.Open,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete))writer.WriteByte(44);
            references.Check();Check(true,"metadata-owner-does-not-lease-unselected-originals");
        }
        using(var writer=new FileStream(metadata,FileMode.Append,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete))writer.WriteByte(32);
        Check(true,"reference-metadata-disposal-releases-object");
        Check(watch.Elapsed.TotalSeconds < 45, "fixture-work-under-45-seconds");
        File.WriteAllText(Path.Combine(root, "selected-verification.json"), JsonSerializer.Serialize(new { result = "passed", passed,
            seconds = watch.Elapsed.TotalSeconds, media = false, capture = false, selectedFiles = 1 }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Selected history controls: {passed.Count}/{passed.Count} passed"); return 0;
    }
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
        var sampler = new ReplayCalibrationRun.Sampler(plan, active: false);
        try
        {
            string admissionFile = Path.Combine(active, "history-admission-events.json");
            string admission = File.ReadAllText(admissionFile);
            using (var report = JsonDocument.Parse(admission))
                Need(report.RootElement.GetProperty("nativeFailure").ValueKind == JsonValueKind.Null
                    && report.RootElement.GetProperty("eventCount").GetInt32() == 0,
                    "Quiet actual sampler admission lacked closed diagnostic evidence.");
            sampler.BeginActive();
            File.WriteAllBytes(Path.Combine(active, "current.bin"), new byte[8192]); sampler.Check();
            File.WriteAllText(Path.Combine(monitored, "unexpected.json"), "changed");
            Need(Blocked(() => sampler.Check()), "Integrated sampler admitted a stage after historical mutation.");
            Need(File.ReadAllText(admissionFile) == admission, "Admission diagnostics changed after active coverage began.");
            passed.Add("admission-diagnostics-close-before-active-coverage-and-never-clear-native-mutation");
        }
        finally { Need(Blocked(sampler.Dispose), "Injected sticky historical failure must remain a disposal failure."); }
        Need(sampler.ObserverAbsent, "Injected failure left an observer running.");
        Need(Blocked(() => { using var writer = new FileStream(historical, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); }),
            "Failed sampler must retain historical owners until actual helper exit.");
        passed.Add("integrated-sampler-rejects-mutation-joins-observers-and-retains-failed-owners-until-controller-exit");
        File.WriteAllText(Path.Combine(root, "verification.json"), JsonSerializer.Serialize(new { result = "passed", passed, effects = "Tiny native private files/read leases/change handles; no child, capture, audio, UI, model, account setting or prior evidence change." }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Frozen history controls: {passed.Count}/{passed.Count} passed"); return 0;
    }
}
