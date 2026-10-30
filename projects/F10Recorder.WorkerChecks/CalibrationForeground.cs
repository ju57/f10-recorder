using System.Text.Json;

namespace F10Recorder;

// Wait for an actual, identity-checked foreground window. Never manufacture an
// input event or disable Windows focus protection to admit capture.
internal static class CalibrationForeground
{
    internal const int DeadlineSeconds = 10;
    internal static void Wait(Func<bool> ready, Func<bool> exited, Action health, Action pause, Func<double> seconds)
    {
        double previous = 0;
        while (true)
        {
            health();
            if (exited()) throw new IOException("Controlled fixture exited before foreground admission; no capture admitted.");
            double at = seconds();
            if (!double.IsFinite(at) || at < previous || at > DeadlineSeconds)
                throw new IOException("Controlled fixture foreground deadline or clock failed; no capture admitted.");
            previous = at;
            if (ready()) return;
            if (at >= DeadlineSeconds)
                throw new IOException("Controlled fixture did not become foreground within10seconds. Click the pattern when it appears; no capture admitted.");
            pause();
        }
    }
    internal static int Checks(string root)
    {
        using var owner = PilotStorageObservation.Create(root);
        var results = new List<object>();
        void Check(string name, Action test) { test(); results.Add(new { name, passed = true }); }
        static void Reject(Action test)
        { bool rejected = false; try { test(); } catch (IOException) { rejected = true; } if (!rejected) throw new IOException("Foreground admission should reject."); }
        Check("already-foreground", () => Wait(() => true, () => false, () => { }, () => throw new IOException("Unexpected wait"), () => 0));
        Check("user-focus-before-deadline", () =>
        { int polls = 0; Wait(() => polls == 3, () => false, () => { }, () => polls++, () => polls); if (polls != 3) throw new IOException("Premature focus admission."); });
        Check("timeout-never-admits", () =>
        { int ticks = 0; Reject(() => Wait(() => false, () => false, () => { }, () => ticks++, () => ticks)); if (ticks != DeadlineSeconds) throw new IOException("Deadline moved."); });
        Check("exited-fixture-even-if-stale-ready", () => Reject(() => Wait(() => true, () => true, () => { }, () => { }, () => 0)));
        Check("health-failure-never-admits", () => Reject(() => Wait(() => true, () => false, () => throw new IOException("Controlled storage failure"), () => { }, () => 0)));
        Check("backwards-clock-never-admits", () =>
        { int calls = 0; Reject(() => Wait(() => false, () => false, () => { }, () => { }, () => calls++ == 0 ? 2 : 1)); });
        Check("replacement-child-boundary-exact-fit", () =>
        {
            string[] history = Enumerable.Range(0, 4).Select(n => Path.Combine(root, "history-" + n)).ToArray();
            foreach (string directory in history) Directory.CreateDirectory(directory);
            var plan = new CalibrationPlan(Path.Combine(root, "unused-candidate"), "unused", "pure-admission-control", new(),
                ["T01", "T02", "T03", "H02", "H01", "H03"], history, 0, 19, MaximumChildren: 51, RetainedOfflineSeconds: 0);
            ReplayCalibrationRun.ValidateResources(plan);
            Reject(() => ReplayCalibrationRun.ValidateResources(plan with { MaximumChildren = 46 }));
            Reject(() => ReplayCalibrationRun.ValidateResources(plan with { MaximumChildren = 50 }));
            Reject(() => ReplayCalibrationRun.ValidateResources(plan with { RetainedChildren = 20 }));
            var next = plan with { RetainedChildren = 25, MaximumChildren = 57, DirectoryLimit = 56,
                OutputComponentBytes = 2_000_000_000, RetainedBudgetBytes = 24_000_000_000 };
            ReplayCalibrationRun.ValidateResources(next);
            Reject(() => ReplayCalibrationRun.ValidateResources(next with { RetainedChildren = 26 }));
            Reject(() => ReplayCalibrationRun.ValidateResources(next with { DirectoryLimit = 57 }));
            var full = next with { MaximumChildren = 74, RetainedChildren = 42, DirectoryLimit = 72, FileLimit = 5120, OfflineDeadlineSeconds = 2700 };
            ReplayCalibrationRun.ValidateResources(full);
            Reject(() => ReplayCalibrationRun.ValidateResources(full with { RetainedChildren = 43 }));
            Reject(() => ReplayCalibrationRun.ValidateResources(full with { FileLimit = 5121 }));
            Reject(() => ReplayCalibrationRun.ValidateResources(full with { DirectoryLimit = 73 }));
            Reject(() => ReplayCalibrationRun.ValidateResources(full with { OfflineDeadlineSeconds = 2701 }));
            Reject(() => ReplayCalibrationRun.ValidateResources(full with { RetainedElapsedSeconds = 1801, RetainedOfflineSeconds = 1801 }));
            var fresh = full with { MaximumChildren = 108, RetainedChildren = 76, BudgetBytes = 72_000_000_000,
                StopBytes = 70_500_000_000, DeadlineSeconds = 10800, DirectoryLimit = 128, FileLimit = 8192,
                RetainedBudgetBytes = 32_000_000_000, OfflineDeadlineSeconds = 3600,
                RetainedElapsedSeconds = 6000, RetainedOfflineSeconds = 2200 };
            ReplayCalibrationRun.ValidateResources(fresh);
            Reject(() => ReplayCalibrationRun.ValidateResources(fresh with { RetainedChildren = 77 }));
            Reject(() => ReplayCalibrationRun.ValidateResources(fresh with { RetainedElapsedSeconds = 6721 }));
            Reject(() => ReplayCalibrationRun.ValidateResources(fresh with { RetainedOfflineSeconds = 2701 }));
            Reject(() => ReplayCalibrationRun.ValidateResources(fresh with { FileLimit = 8193 }));
            Reject(() => ReplayCalibrationRun.ValidateResources(fresh with { StopBytes = 70_500_000_001 }));
            Reject(() => ReplayCalibrationRun.ValidateResources(fresh with { BudgetBytes = 64_000_000_000 }));
            var replacement = fresh with { MaximumChildren = 115, RetainedChildren = 83,
                DeadlineSeconds = 11700, OfflineDeadlineSeconds = 4500, RetainedBudgetBytes = 34_000_000_000,
                RetainedElapsedSeconds = 7620, RetainedOfflineSeconds = 3420, OutsideBuildAllowanceBytes = 1_100_000_000 };
            ReplayCalibrationRun.ValidateResources(replacement); // Exact full-wall/offline/child fit.
            Reject(() => ReplayCalibrationRun.ValidateResources(replacement with { RetainedChildren = 84 }));
            Reject(() => ReplayCalibrationRun.ValidateResources(replacement with { RetainedChildren = int.MaxValue }));
            Reject(() => ReplayCalibrationRun.ValidateResources(replacement with { MaximumChildren = 108 }));
            Reject(() => ReplayCalibrationRun.ValidateResources(replacement with { RetainedElapsedSeconds = 7620.01 }));
            Reject(() => ReplayCalibrationRun.ValidateResources(replacement with { RetainedOfflineSeconds = 3420.01 }));
            Reject(() => ReplayCalibrationRun.ValidateResources(replacement with { DeadlineSeconds = 11701 }));
            Reject(() => ReplayCalibrationRun.ValidateResources(replacement with { OfflineDeadlineSeconds = 4501 }));
            Reject(() => ReplayCalibrationRun.ValidateResources(replacement with { RetainedBudgetBytes = 34_000_000_001 }));
            Reject(() => ReplayCalibrationRun.ValidateResources(replacement with { OutputComponentBytes = 2_000_000_001 }));
            Reject(() => ReplayCalibrationRun.ValidateResources(replacement with { FileLimit = 8193 }));
            Reject(() => ReplayCalibrationRun.ValidateResources(replacement with { DirectoryLimit = 129 }));
            Reject(() => ReplayCalibrationRun.ValidateResources(replacement with { StopBytes = 70_500_000_001 }));
            Reject(() => ReplayCalibrationRun.ValidateResources(replacement with { BudgetBytes = 72_000_000_001 }));
            Reject(() => ReplayCalibrationRun.ValidateResources(replacement with { FloorBytes = 59_999_999_999 }));
            Reject(() => ReplayCalibrationRun.ValidateResources(replacement with { OutsideBuildAllowanceBytes = 1_100_000_001 }));
            var contention = replacement with { MaximumChildren = 137, RetainedChildren = 105, BudgetBytes = 84_000_000_000,
                StopBytes = 82_500_000_000, DeadlineSeconds = 14400, DirectoryLimit = 160, FileLimit = 10240,
                RetainedBudgetBytes = 44_000_000_000, OfflineDeadlineSeconds = 5400,
                RetainedElapsedSeconds = 10320, RetainedOfflineSeconds = 4290 };
            ReplayCalibrationRun.ValidateResources(contention);
            Reject(() => ReplayCalibrationRun.ValidateResources(contention with { RetainedChildren = 106 }));
            Reject(() => ReplayCalibrationRun.ValidateResources(contention with { RetainedElapsedSeconds = 10320.01 }));
            Reject(() => ReplayCalibrationRun.ValidateResources(contention with { RetainedOfflineSeconds = 4290.01 }));
            Reject(() => ReplayCalibrationRun.ValidateResources(contention with { MaximumChildren = 115 }));
            Reject(() => ReplayCalibrationRun.ValidateResources(contention with { FileLimit = 10241 }));
            Reject(() => ReplayCalibrationRun.ValidateResources(contention with { DirectoryLimit = 161 }));
            Reject(() => ReplayCalibrationRun.ValidateResources(contention with { BudgetBytes = 84_000_000_001 }));
            Reject(() => ReplayCalibrationRun.ValidateResources(contention with { StopBytes = 82_500_000_001 }));
            Reject(() => ReplayCalibrationRun.ValidateResources(contention with { DeadlineSeconds = 14401 }));
            Reject(() => ReplayCalibrationRun.ValidateResources(contention with { OfflineDeadlineSeconds = 5401 }));
            Reject(() => ReplayCalibrationRun.ValidateResources(contention with { RetainedBudgetBytes = 44_000_000_001 }));
            Reject(() => ReplayCalibrationRun.ValidateResources(contention with { OutsideBuildAllowanceBytes = 1_100_000_001 }));
            var startup = contention with { MaximumChildren = 147, RetainedChildren = 115, DeadlineSeconds = 18000,
                OfflineDeadlineSeconds = 8400, RetainedElapsedSeconds = 13800, RetainedOfflineSeconds = 7110,
                RetainedRoots = history.Concat(Enumerable.Range(0, 10).Select(n => Path.Combine(root, "replacement-history-" + n))).ToArray() };
            foreach (string directory in startup.RetainedRoots) Directory.CreateDirectory(directory);
            ReplayCalibrationRun.ValidateResources(startup);
            Reject(() => ReplayCalibrationRun.ValidateResources(startup with { RetainedChildren = 116 }));
            Reject(() => ReplayCalibrationRun.ValidateResources(startup with { RetainedElapsedSeconds = 13800.01 }));
            Reject(() => ReplayCalibrationRun.ValidateResources(startup with { RetainedOfflineSeconds = 7110.01 }));
            Reject(() => ReplayCalibrationRun.ValidateResources(startup with { MaximumChildren = 137 }));
            Reject(() => ReplayCalibrationRun.ValidateResources(startup with { DirectoryLimit = 161 }));
            Reject(() => ReplayCalibrationRun.ValidateResources(startup with { BudgetBytes = 84_000_000_001 }));
            var publication = startup with { MaximumChildren = 179, RetainedChildren = 147, OfflineDeadlineSeconds = 9000,
                OutsideBuildAllowanceBytes = 1_200_000_000, RetainedOfflineSeconds = 7710,
                RetainedRoots = startup.RetainedRoots.Concat([Path.Combine(root, "readiness-failure-history")]).ToArray() };
            Directory.CreateDirectory(publication.RetainedRoots[^1]);
            ReplayCalibrationRun.ValidateResources(publication);
            Reject(() => ReplayCalibrationRun.ValidateResources(publication with { RetainedChildren = 148 }));
            Reject(() => ReplayCalibrationRun.ValidateResources(publication with { RetainedOfflineSeconds = 7710.01 }));
            Reject(() => ReplayCalibrationRun.ValidateResources(publication with { OutsideBuildAllowanceBytes = 1_200_000_001 }));
            Reject(() => ReplayCalibrationRun.ValidateResources(publication with { MaximumChildren = 147 }));
            var evidence = publication with { MaximumChildren = 288, RetainedChildren = 256, BudgetBytes = 112_000_000_000,
                StopBytes = 110_500_000_000, DeadlineSeconds = 28800, OfflineDeadlineSeconds = 16200, DirectoryLimit = 224, FileLimit = 16384,
                RetainedBudgetBytes = 72_000_000_000, OutsideBuildAllowanceBytes = 1_400_000_000, RetainedElapsedSeconds = 24000, RetainedOfflineSeconds = 14000,
                RetainedRoots = publication.RetainedRoots.Concat([Path.Combine(root, "export-history"), Path.Combine(root, "capture-evidence-history")]).ToArray() };
            foreach (string directory in evidence.RetainedRoots) Directory.CreateDirectory(directory);
            ReplayCalibrationRun.ValidateResources(evidence);
            Reject(() => ReplayCalibrationRun.ValidateResources(evidence with { RetainedChildren = 257 }));
            Reject(() => ReplayCalibrationRun.ValidateResources(evidence with { FileLimit = 16385 }));
            Reject(() => ReplayCalibrationRun.ValidateResources(evidence with { OutsideBuildAllowanceBytes = 1_400_000_001 }));
            var lifecycle = evidence with { MaximumChildren = 342, RetainedChildren = 310, DeadlineSeconds = 36000,
                OfflineDeadlineSeconds = 21600, DirectoryLimit = 320, FileLimit = 20480,
                RetainedBudgetBytes = 92_000_000_000, OutsideBuildAllowanceBytes = 2_000_000_000,
                RetainedElapsedSeconds = 28000, RetainedOfflineSeconds = 16000,
                RetainedRoots = evidence.RetainedRoots.Concat(Enumerable.Range(0, 7).Select(n => Path.Combine(root, "lifecycle-history-" + n))).ToArray() };
            foreach (string directory in lifecycle.RetainedRoots) Directory.CreateDirectory(directory);
            ReplayCalibrationRun.ValidateResources(lifecycle);
            Reject(() => ReplayCalibrationRun.ValidateResources(lifecycle with { RetainedChildren = 309 }));
            Reject(() => ReplayCalibrationRun.ValidateResources(lifecycle with { RetainedChildren = 311 }));
            Reject(() => ReplayCalibrationRun.ValidateResources(lifecycle with { FloorBytes = 59_999_999_999 }));
            Reject(() => ReplayCalibrationRun.ValidateResources(lifecycle with { FileLimit = 20481 }));
            Reject(() => ReplayCalibrationRun.ValidateResources(lifecycle with { DirectoryLimit = 321 }));
            Reject(() => ReplayCalibrationRun.ValidateResources(lifecycle with { StopBytes = 110_500_000_001 }));
            Reject(() => ReplayCalibrationRun.ValidateResources(lifecycle with { OutsideBuildAllowanceBytes = 2_000_000_001 }));
            var foreground = lifecycle with { MaximumChildren = 374, RetainedChildren = 342,
                BudgetBytes = 128_000_000_000, StopBytes = 126_500_000_000, DeadlineSeconds = 39600,
                OfflineDeadlineSeconds = 25200, DirectoryLimit = 384, FileLimit = 24576,
                RetainedBudgetBytes = 106_000_000_000, RetainedElapsedSeconds = 31200, RetainedOfflineSeconds = 18200,
                RetainedRoots = lifecycle.RetainedRoots.Append(Path.Combine(root, "foreground-failed-history")).ToArray() };
            Directory.CreateDirectory(foreground.RetainedRoots[^1]);
            ReplayCalibrationRun.ValidateResources(foreground);
            Reject(() => ReplayCalibrationRun.ValidateResources(foreground with { RetainedChildren = 341 }));
            Reject(() => ReplayCalibrationRun.ValidateResources(foreground with { RetainedChildren = 343 }));
            Reject(() => ReplayCalibrationRun.ValidateResources(foreground with { FloorBytes = 59_999_999_999 }));
            Reject(() => ReplayCalibrationRun.ValidateResources(foreground with { FileLimit = 24577 }));
            Reject(() => ReplayCalibrationRun.ValidateResources(foreground with { DirectoryLimit = 385 }));
            Reject(() => ReplayCalibrationRun.ValidateResources(foreground with { StopBytes = 126_500_000_001 }));
            Reject(() => ReplayCalibrationRun.ValidateResources(foreground with { OutsideBuildAllowanceBytes = 2_000_000_001 }));
        });
        File.WriteAllText(Path.Combine(root, "foreground-checks.json"), JsonSerializer.Serialize(new {
            result = "passed", results, effects = "Pure admission delegates and tiny receipt; no window, input injection, child, engine, sound or capture." }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Foreground/resource admission controls: 7/7 passed"); return 0;
    }
}
