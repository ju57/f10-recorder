using System.Text.Json;

namespace F10Recorder;

// Native file/observer controls only. No media engine, UI, capture or GPU starts.
internal static class ReplayCalibrationPhaseChecks
{
    static void Require(bool value, string reason) { if (!value) throw new IOException(reason); }
    internal static int Run(string root)
    {
        if (Directory.Exists(root)) throw new IOException("Phase controls require fresh evidence.");
        Directory.CreateDirectory(root);
        var results = new List<object>(); int failed = 0;
        void Check(string name, Action<string> run)
        {
            string path = Path.Combine(root, name); Directory.CreateDirectory(path);
            try { run(path); results.Add(new { name, passed = true }); }
            catch (Exception ex) { failed++; results.Add(new { name, passed = false, error = ex.ToString() }); }
        }
        CalibrationPlan Plan(string path) => new(path, "unused-no-engine", "nonmedia-phase-control",
            new(), [], [], 0, 0, BudgetBytes: 3_000_000_000, StopBytes: 2_500_000_000, FloorBytes: 0, DeadlineSeconds: 30);

        Check("anchored-inventory-attributes-enforce-media-policy", path =>
        {
            File.WriteAllBytes(Path.Combine(path, "source.ts"), new byte[4096]);
            File.WriteAllBytes(Path.Combine(path, "output.mp4"), new byte[4096]);
            var observed = new Dictionary<string, FileAttributes>();
            var inventory = ReplayStorage.Inventory([path], new ReplayFiles(), inspectAttributes: (name, attributes) =>
            { observed.Add(name, attributes); ReplayCalibrationRun.ValidateMediaAttributes(name, attributes); });
            Require(observed.Count == 2 && inventory.Keys.All(observed.ContainsKey), "Attribute policy missed a native inventory entry.");
            bool callbackRejected = false;
            try { ReplayStorage.Inventory([path], new ReplayFiles(), inspectAttributes: (_, _) => throw new IOException("Controlled attribute rejection")); }
            catch (IOException) { callbackRejected = true; }
            Require(callbackRejected, "Native attribute-policy rejection was ignored.");
            foreach (string name in new[] { "source.ts", "output.mp4", "upper.TS", "upper.MP4" })
                foreach (var attributes in new[] { FileAttributes.Compressed, FileAttributes.SparseFile })
                {
                    bool rejected = false;
                    try { ReplayCalibrationRun.ValidateMediaAttributes(name, attributes); } catch (IOException) { rejected = true; }
                    Require(rejected, "Unsupported media attributes passed the inventory policy.");
                }
        });

        Check("inspection-frame-positions-match-rounded-duration", path =>
        {
            foreach (double seconds in new[] { 0.05, 0.1, 0.2, 0.249, 0.5, 1.0, 1.2, 2.0, 298.0, 299.2341926, 300.1 })
            {
                int[] frames = ReplayCalibrationRun.InspectionFrames(seconds);
                Require(frames.Length == 3 && frames[0] < frames[1] && frames[1] < frames[2] && frames[0] >= 0 && frames[2] < Math.Floor(seconds * 60),
                    "Short rounded duration selected absent or duplicate frames.");
                if (seconds >= 1) Require(frames.SequenceEqual(new[] { 15, (int)(seconds * 30), (int)(seconds * 60) - 15 }), "Main/ordinary inspection positions drifted.");
            }
            Require(ReplayCalibrationRun.InspectionFrames(0.2).SequenceEqual(new[] { 3, 6, 9 }), "T02 twelve-frame regression was not covered.");
            foreach (double seconds in new[] { double.NaN, double.PositiveInfinity, -1.0, 0.0, 0.049, 311.0 })
            {
                bool rejected = false; try { ReplayCalibrationRun.InspectionFrames(seconds); } catch (IOException) { rejected = true; }
                Require(rejected, "Invalid or insufficient inspection duration was admitted.");
            }
        });
        Check("offline-delay-and-terminal-active-observation", path =>
        {
            string output = Path.Combine(path, "bounded-output.mp4");
            File.WriteAllBytes(output, []);
            using var sampler = new ReplayCalibrationRun.Sampler(Plan(path), active: false);
            sampler.BeginOffline("archive-control", 2_000_000, 5);
            lock (sampler.Gate) Thread.Sleep(650);
            sampler.EndOffline();
            sampler.BeginActive(); // Fresh baseline must tolerate the prior offline gap.
            Require(sampler.MaximumOfflineGap > 500, "The offline sensitivity delay did not reach the observer.");
            using (var writer = new FileStream(output, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            {
                writer.Write(new byte[1_048_576]); writer.Flush(true);
            }
            int before = Volatile.Read(ref sampler.Observations);
            sampler.EndActive();
            Require(Volatile.Read(ref sampler.Observations) > before,
                "A terminal observation must start after producer absence, not reuse an older row.");
            Require(sampler.MaximumActiveGap is > 0 and <= 500, "Active coverage did not retain the unchanged limit.");
            Require(sampler.Peak(output) >= ReplayStorage.Observe(output).AllocatedBytes,
                "Terminal output allocation was omitted from the observed peak.");
            sampler.StopAndCheck();
        });
        Check("active-650ms-delay-rejects", path =>
        {
            using var sampler = new ReplayCalibrationRun.Sampler(Plan(path), active: false);
            sampler.BeginActive();
            lock (sampler.Gate) Thread.Sleep(650);
            Exception rejection = null;
            try { sampler.EndActive(); } catch (IOException ex) { rejection = ex; }
            Require(rejection != null, "The same delay while a producer can grow must reject the active interval.");
        });
        Check("offline-allocation-shortage-before-admission", path =>
        {
            var plan = Plan(path);
            using var sampler = new ReplayCalibrationRun.Sampler(plan, active: false);
            bool rejected = false;
            try { sampler.BeginOffline("archive-control", plan.BudgetBytes, 5); } catch (IOException) { rejected = true; }
            Require(rejected, "An archive must reserve its full coexisting allocation before it writes.");
            Require(!File.Exists(Path.Combine(path, "archive.tar")), "Rejected admission created an archive.");
            sampler.BeginActive(); sampler.EndActive(); sampler.StopAndCheck();
        });
        Check("offline-deadline-remains-enforced", path =>
        {
            using var sampler = new ReplayCalibrationRun.Sampler(Plan(path), active: false);
            sampler.BeginOffline("archive-control", 0, 1);
            Thread.Sleep(1150);
            bool rejected = false;
            try { sampler.Check(); } catch (IOException) { rejected = true; }
            Require(rejected, "An offline phase outside its own deadline must block further work.");
        });
        Check("offline-cumulative-full-phase-admission", path =>
        {
            var plan = Plan(path) with { RetainedElapsedSeconds = 1799, RetainedOfflineSeconds = 1799, DeadlineSeconds = 4800 };
            using var sampler = new ReplayCalibrationRun.Sampler(plan, active: false);
            bool rejected = false;
            try { sampler.BeginOffline("too-large-time", 0, 2); } catch (IOException) { rejected = true; }
            Require(rejected, "A two-second child cannot fit one second of remaining cumulative analysis time.");
            sampler.BeginOffline("exact-deadline", 0, 1);
            sampler.EndOffline(); sampler.StopAndCheck();
        });
        Check("unknown-historical-offline-time-charged-conservatively", path =>
        {
            var plan = Plan(path) with { RetainedElapsedSeconds = 1800, DeadlineSeconds = 4800 };
            using var sampler = new ReplayCalibrationRun.Sampler(plan, active: false);
            bool rejected = false;
            try { sampler.BeginOffline("no-remaining-time", 0, 1); } catch (IOException) { rejected = true; }
            Require(rejected, "Unknown historical phase split must charge all retained wall time before admission.");
            sampler.StopAndCheck();
        });
        Check("capture-positive-growth-and-terminal-drain", path =>
        {
            string buffer = Path.Combine(path, "T01", "buffer", "session-control");
            Directory.CreateDirectory(buffer);
            using var sampler = new ReplayCalibrationRun.Sampler(Plan(path), active: false);
            sampler.BeginActive();
            string chunk = Path.Combine(buffer, "chunk-000000000.ts");
            File.WriteAllBytes(chunk, new byte[4096]);
            int before = Volatile.Read(ref sampler.Observations);
            var wait = System.Diagnostics.Stopwatch.StartNew();
            while (Volatile.Read(ref sampler.Observations) <= before)
            { sampler.Check(); Require(wait.ElapsedMilliseconds < 2000, "Control observation timed out."); Thread.Sleep(5); }
            sampler.MarkCaptureStop("T01");
            using (var writer = new FileStream(chunk, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            { writer.Write(new byte[4096]); writer.Flush(true); }
            sampler.EndActive();
            var measured = sampler.CaptureMetrics("T01");
            Require(measured.PollGrowth >= 4096 && measured.StopGrowth >= 4096,
                "Positive allocation and allocation after the stop prefix must both be observed.");
            Require(measured.Volume == ReplayStorage.ObserveCapacity(buffer).Volume, "Growth must retain its actual volume identity.");
            sampler.StopAndCheck();
        });
        Check("full-stage-must-fit-cumulative-wall-time", path =>
        {
            var plan = Plan(path) with { RetainedElapsedSeconds = 27, RetainedOfflineSeconds = 0, DeadlineSeconds = 30 };
            using var sampler = new ReplayCalibrationRun.Sampler(plan, active: false);
            bool activeRejected = false, offlineRejected = false;
            try { sampler.RequireTime(4); } catch (IOException ex) when (ex.Message.Contains("full stage")) { activeRejected = true; }
            try { sampler.BeginOffline("too-long-wall-time", 0, 4); } catch (IOException ex) when (ex.Message.Contains("full stage")) { offlineRejected = true; }
            Require(activeRejected && offlineRejected, "Active/offline stages must not borrow time past the cumulative wall deadline.");
            sampler.RequireTime(1); sampler.StopAndCheck();
        });
        Check("completed-case-handles-only-after-producer-absence", path =>
        {
            string cell = Path.Combine(path, "T01"); Directory.CreateDirectory(cell);
            File.WriteAllBytes(Path.Combine(cell, "closed.bin"), new byte[4096]);
            using var sampler = new ReplayCalibrationRun.Sampler(Plan(path), active: false);
            sampler.BeginActive(); bool rejected = false;
            try { sampler.SealClosedCase("T01"); } catch (IOException) { rejected = true; }
            Require(rejected, "A case retained metadata handles while a producer could still write.");
            sampler.EndActive(); sampler.SealClosedCase("T01");
            var wait = System.Diagnostics.Stopwatch.StartNew();
            while (Volatile.Read(ref sampler.MetadataHandles) == 0)
            { sampler.Check(); Require(wait.ElapsedMilliseconds < 2000, "Closed case metadata handles were not enabled."); Thread.Sleep(5); }
            sampler.StopAndCheck();
        });
        Check("mutable-handles-released-before-verified-retirement", path =>
        {
            string snapshot = Path.Combine(path, "buffer", "save-owned"); Directory.CreateDirectory(snapshot);
            File.WriteAllBytes(Path.Combine(snapshot, "000000.ts"), new byte[4096]); File.WriteAllText(Path.Combine(snapshot, "concat.txt"), "file '000000.ts'\n");
            using var sampler = new ReplayCalibrationRun.Sampler(Plan(path) with { ReuseCurrentMetadataHandles = true }, active: false);
            Require(Volatile.Read(ref sampler.MetadataHandles) >= 2, "Mutable fixture did not exercise reused native handles.");
            bool ungatedRejected = false;
            try { sampler.ReleaseInventoryHandles(); } catch (IOException) { ungatedRejected = true; }
            Require(ungatedRejected, "Handle release bypassed the inventory mutation gate.");
            sampler.BeginOffline("gated-retirement", 32_000_000, 10);
            var archived = ReplayCalibrationArchive.Create(path, snapshot, bytes => sampler.Check(bytes), sampler.Gate);
            ReplayCalibrationArchive.Retire(path, archived, sampler.Gate, sampler.ReleaseInventoryHandles);
            sampler.EndOffline(); sampler.StopAndCheck();
            Require(!Directory.Exists(snapshot) && File.Exists(archived.Archive), "Held metadata handles blocked verified namespace retirement.");
        });
        Check("contracted-recording-stage-ceilings-remain-full-admission", path =>
        {
            Require(ReplayCalibrationRun.MainInspectionSeconds == 60 && ReplayCalibrationRun.ShortInspectionSeconds == 30
                && ReplayCalibrationRun.ArchiveSeconds == 60, "Recording phase limits are not the frozen contractions.");
            var plan = Plan(path) with { RetainedElapsedSeconds = 1741, RetainedOfflineSeconds = 1741, DeadlineSeconds = 9000 };
            using var sampler = new ReplayCalibrationRun.Sampler(plan, active: false); bool rejected = false;
            try { sampler.BeginOffline("full-sixty-does-not-fit", 0, ReplayCalibrationRun.MainInspectionSeconds); } catch (IOException) { rejected = true; }
            Require(rejected, "A60-second stage was admitted with only59offline seconds remaining.");
            sampler.BeginOffline("thirty-fits", 0, ReplayCalibrationRun.ShortInspectionSeconds); sampler.EndOffline(); sampler.StopAndCheck();
        });
        Check("current-capture-payload-handles-not-retained", path =>
        {
            string cell = Path.Combine(path, "T01"); Directory.CreateDirectory(cell);
            string chunk = Path.Combine(cell, "chunk-000000001.ts"), output = Path.Combine(cell, "growing.mp4");
            File.WriteAllBytes(chunk, new byte[4096]); File.WriteAllBytes(output, new byte[4096]);
            using var sampler = new ReplayCalibrationRun.Sampler(Plan(path) with { ReuseCurrentMetadataHandles = true }, active: false);
            Require(!sampler.RetainsMetadata(chunk) && !sampler.RetainsMetadata(output), "Current producer payload retained an observer handle through close.");
            sampler.BeginActive();
            using (var writer = new FileStream(chunk, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            { writer.Write(new byte[1_048_576]); writer.Flush(); }
            sampler.EndActive();
            Require(!sampler.RetainsMetadata(chunk), "Current source handle remained pinned after an observation.");
            sampler.SealClosedCase("T01");
            var wait = System.Diagnostics.Stopwatch.StartNew();
            while (!sampler.RetainsMetadata(chunk))
            { sampler.Check(); Require(wait.ElapsedMilliseconds < 2000, "Closed case did not enable retained metadata."); Thread.Sleep(5); }
            sampler.StopAndCheck();
        });
        Check("expanded-directory-observation-boundary", path =>
        {
            for (int n = 0; n < 55; n++)
            {
                string directory = Path.Combine(path, "dir-" + n); Directory.CreateDirectory(directory);
                for (int file = 0; file < 20; file++) File.WriteAllBytes(Path.Combine(directory, "meta-" + file + ".bin"), []);
            }
            using var sampler = new ReplayCalibrationRun.Sampler(Plan(path) with { DirectoryLimit = 56, ReuseCurrentMetadataHandles = true }, active: false);
            sampler.BeginActive(); sampler.EndActive(); sampler.StopAndCheck();
            Require(sampler.MaximumDirectories == 56 && sampler.MaximumFiles >= 1100, "Expanded directory/path observation was not exercised.");
        });
        Check("retained-inventory-plus-current-payload-at-proposed-boundary", path =>
        {
            for (int n = 0; n < 70; n++)
            {
                string directory = Path.Combine(path, "dir-" + n); Directory.CreateDirectory(directory);
                for (int file = 0; file < 68; file++) File.WriteAllBytes(Path.Combine(directory, "meta-" + file + ".bin"), []);
            }
            string cell = Path.Combine(path, "T02"); Directory.CreateDirectory(cell);
            for (int n = 0; n < 350; n++) File.WriteAllBytes(Path.Combine(cell, $"chunk-{n:D9}.ts"), []);
            for (int n = 0; n < 9; n++) File.WriteAllBytes(Path.Combine(path, "extra-" + n + ".bin"), []);
            using var sampler = new ReplayCalibrationRun.Sampler(Plan(path) with { DirectoryLimit = 72, FileLimit = 5120, ReuseCurrentMetadataHandles = true }, active: false);
            sampler.BeginActive(); int before = sampler.Observations;
            var wait = System.Diagnostics.Stopwatch.StartNew();
            while (sampler.Observations < before + 3)
            { sampler.Check(); Require(wait.ElapsedMilliseconds < 3000, "Proposed inventory observation did not progress."); Thread.Sleep(5); }
            sampler.EndActive(); sampler.StopAndCheck();
            Require(sampler.MaximumDirectories == 72 && sampler.MaximumFiles == 5120 && sampler.MaximumActiveGap <= 500,
                "Proposed full inventory did not meet the unchanged active observation gate.");
            Require(!sampler.RetainsMetadata(Path.Combine(cell, "chunk-000000000.ts")), "Current payload handle was retained.");
        });
        File.WriteAllText(Path.Combine(root, "phase-checks.json"), JsonSerializer.Serialize(new {
            failed, results, effects = "Tiny file/native observer controls; one bounded 1 MiB binary write and declared delay injection. No engine, media, capture, GPU, audio or UI.",
            limits = "Phase enforcement and terminal observation only; not a full-size workflow rehearsal, empirical calibration or storage acceptance."
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Calibration phase controls: {results.Count - failed}/{results.Count} passed. {root}");
        return failed == 0 ? 0 : 1;
    }
}

