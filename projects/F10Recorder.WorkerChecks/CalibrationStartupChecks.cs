using System.Text.Json;

namespace F10Recorder;

internal static class CalibrationStartupChecks
{
    internal static int Run(string root)
    {
        using var owner = PilotStorageObservation.Create(root);
        var checks = new List<string>();
        var failures = new List<object>();
        void Require(bool value, string message) { if (!value) throw new IOException(message); }
        void Check(string name, Action action)
        {
            try { action(); checks.Add(name); }
            catch (Exception ex) { failures.Add(new { name, error = ex.ToString() }); }
        }
        var healthy = new CaptureHealthSnapshot(false, null, null, 20, 19, 0.5, 18, 1, 1140, "");
        Check("healthy-progress-and-segments", () => Require(healthy.Healthy, "Fresh source rejected."));
        Check("stalled-startup-diagnosis", () =>
        {
            var stalled = healthy with { ElapsedSeconds = 15.1, ProgressSeconds = 0, ProgressAgeSeconds = 15.1,
                CompletedSegmentEnd = 0, SegmentAgeSeconds = 15.1, EncodedFrames = 0 };
            Require(!stalled.Healthy && !stalled.HasExited && stalled.AudioFailure == null && stalled.Diagnostics == "",
                "Live silent-stderr startup stall must fail without pretending the engine exited or audio failed.");
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(stalled));
            Require(!json.RootElement.GetProperty("Healthy").GetBoolean()
                && json.RootElement.GetProperty("ProgressAgeSeconds").GetDouble() == 15.1, "Failure observations were not serialized.");
        });
        Check("progress-threshold-unchanged", () => Require(!(healthy with { ProgressAgeSeconds = 15 }).Healthy, "Progress guard moved."));
        Check("segment-threshold-unchanged", () => Require(!(healthy with { SegmentAgeSeconds = 15 }).Healthy, "Segment guard moved."));
        Check("audio-failure-independent", () => Require(!(healthy with { AudioFailure = "controlled bridge failure" }).Healthy, "Audio failure omitted."));
        Check("engine-exit-independent", () => Require(!(healthy with { HasExited = true, ExitCode = 1 }).Healthy, "Engine exit omitted."));
        Check("wake-restored-after-normal-return", () =>
        {
            var flags = new List<uint>();
            var lease = new CalibrationWakeLease(value => { flags.Add(value); return 0x80000001; });
            lease.Dispose(); lease.Dispose();
            Require(lease.Restored && flags.SequenceEqual(new uint[] { CalibrationWakeLease.Required, 0x80000001 }), "Wake restoration changed prior flags or was repeated.");
        });
        Check("wake-restored-after-fixture-failure", () =>
        {
            var flags = new List<uint>();
            try { using var lease = new CalibrationWakeLease(value => { flags.Add(value); return 0x80000000; }); throw new InvalidOperationException("controlled fixture failure"); }
            catch (InvalidOperationException) { }
            Require(flags.SequenceEqual(new uint[] { CalibrationWakeLease.Required, 0x80000000 }), "Wake state leaked after failure.");
        });
        Check("wake-failure-never-admits", () =>
        {
            bool rejected = false;
            try { using var lease = new CalibrationWakeLease(_ => 0); } catch (IOException) { rejected = true; }
            Require(rejected, "Failed wake request admitted source.");
        });
        Check("locked-remote-missing-desktop-never-admits", () =>
        {
            var local = new CalibrationDesktop.DesktopState(1, 1, false, "Default", "Default", true, 0);
            Require(CalibrationDesktop.LocalDesktop(local), "Local desktop rejected.");
            foreach (var state in new[] { local with { InputDesktop = "Winlogon" }, local with { RemoteSession = true },
                local with { ConsoleSessionId = 2 }, local with { InputDesktop = null }, local with { ReceivingInput = false } })
                Require(!CalibrationDesktop.LocalDesktop(state), "Unavailable desktop admitted source.");
        });
        Check("startup-sampler-enforces-private-source-cap", () =>
        {
            string cell = Path.Combine(root, "budget-negative"); Directory.CreateDirectory(cell);
            File.WriteAllBytes(Path.Combine(cell, "dummy.bin"), new byte[65536]);
            var plan = new CalibrationPlan(cell, "unused", "nonmedia-control", new(), [], [], 0, 111,
                BudgetBytes: 2_000_000_000, StopBytes: 1_900_000_000, FloorBytes: 0, DeadlineSeconds: 30,
                RetainedOfflineSeconds: 0, OutsideBuildAllowanceBytes: 0);
            bool rejected = false;
            try { using var sampler = new ReplayCalibrationRun.Sampler(plan, currentBudgetBytes: 32768); }
            catch (IOException ex) when (ex.Message.Contains("Startup diagnostic source byte stop")) { rejected = true; }
            Require(rejected, "Actual native private-source allocation exceeded its diagnostic cap without rejection.");
        });
        Check("startup-sampler-observes-bounded-private-source", () =>
        {
            string cell = Path.Combine(root, "budget-positive"); Directory.CreateDirectory(cell);
            File.WriteAllBytes(Path.Combine(cell, "dummy.bin"), new byte[65536]);
            var plan = new CalibrationPlan(cell, "unused", "nonmedia-control", new(), [], [], 0, 111,
                BudgetBytes: 2_000_000_000, StopBytes: 1_900_000_000, FloorBytes: 0, DeadlineSeconds: 30,
                RetainedOfflineSeconds: 0, OutsideBuildAllowanceBytes: 0);
            using var sampler = new ReplayCalibrationRun.Sampler(plan, currentBudgetBytes: 1048576);
            sampler.StopAndCheck();
            Require(sampler.CurrentAllocatedBytes >= 65536 && sampler.MaximumActiveGap <= 500,
                "Actual native diagnostic allocation/freshness observation failed.");
        });
        Check("capture-arguments-native-separators", () =>
        {
            string cell = Path.Combine(root, "T02"); var arguments = ReplayCalibrationRun.CaptureTemplate("0");
            arguments[^1] = Path.Combine(cell, "buffer", "session-probe", "chunk-%09d.ts");
            Require(ReplayCalibrationStartup.NormalizeCaptureArguments(arguments, cell).SequenceEqual(ReplayCalibrationRun.CaptureTemplate("0")), "Native source path did not match the reference recipe.");
        });
        Check("capture-arguments-mixed-separators", () =>
        {
            string cell = Path.Combine(root, "T02").Replace('\\', '/'); var arguments = ReplayCalibrationRun.CaptureTemplate("0");
            arguments[^1] = Path.Combine(cell, "buffer", "session-probe", "chunk-%09d.ts");
            Require(ReplayCalibrationStartup.NormalizeCaptureArguments(arguments, cell).SequenceEqual(ReplayCalibrationRun.CaptureTemplate("0")), "Equivalent mixed-separator source path did not match the reference recipe.");
        });
        Check("capture-arguments-outside-buffer-still-rejected", () =>
        {
            string cell = Path.Combine(root, "T02");
            foreach (string escaped in new[] { Path.Combine(cell + "-other", "buffer", "session-probe", "chunk-%09d.ts"),
                Path.Combine(cell, "buffer", "..", "outside", "chunk-%09d.ts") })
            {
                var arguments = ReplayCalibrationRun.CaptureTemplate("0"); arguments[^1] = escaped; bool rejected = false;
                try { ReplayCalibrationStartup.NormalizeCaptureArguments(arguments, cell); } catch (IOException) { rejected = true; }
                Require(rejected, "An escaped source path passed private-buffer admission.");
            }
        });
        Check("mixed-current-root-allocation-is-counted", () =>
        {
            string cell = Path.Combine(root, "mixed-current"); Directory.CreateDirectory(cell);
            File.WriteAllBytes(Path.Combine(cell, "dummy.bin"), new byte[65536]);
            var plan = new CalibrationPlan(cell.Replace('\\', '/'), "unused", "nonmedia-control", new(), [], [], 0, 111,
                BudgetBytes: 2_000_000_000, StopBytes: 1_900_000_000, FloorBytes: 0, DeadlineSeconds: 30,
                RetainedOfflineSeconds: 0, OutsideBuildAllowanceBytes: 0);
            using var sampler = new ReplayCalibrationRun.Sampler(plan, currentBudgetBytes: 1048576);
            sampler.StopAndCheck();
            Require(sampler.CurrentAllocatedBytes >= 65536, "A mixed-separator source root was omitted from the private-source byte cap.");
        });
        Check("mixed-retained-root-budget-is-enforced", () =>
        {
            string cell = Path.Combine(root, "mixed-retained-current"), retained = Path.Combine(root, "mixed-retained-history");
            Directory.CreateDirectory(cell); Directory.CreateDirectory(retained);
            File.WriteAllBytes(Path.Combine(retained, "dummy.bin"), new byte[65536]);
            var plan = new CalibrationPlan(cell, "unused", "nonmedia-control", new(), [], [retained.Replace('\\', '/')], 0, 111,
                BudgetBytes: 2_000_000_000, StopBytes: 1_900_000_000, FloorBytes: 0, DeadlineSeconds: 30,
                RetainedOfflineSeconds: 0, RetainedBudgetBytes: 32768, OutsideBuildAllowanceBytes: 0);
            bool rejected = false;
            try { using var sampler = new ReplayCalibrationRun.Sampler(plan, currentBudgetBytes: 1048576); }
            catch (IOException ex) when (ex.Message.Contains("Retained evidence envelope exceeded")) { rejected = true; }
            Require(rejected, "A mixed-separator retained root evaded the retained-evidence budget.");
        });
        CalibrationPlan Boundary(int children, int roots, int offline) => new(root, "unused", "pure-admission", new(), ["T02"],
            Enumerable.Range(0, roots).Select(n => Path.Combine(root, "history-" + n)).ToArray(), 11000, children,
            MaximumChildren: 137, BudgetBytes: 84_000_000_000, StopBytes: 82_500_000_000, DeadlineSeconds: 14400,
            RetainedOfflineSeconds: 5100, DirectoryLimit: 160, RetainedBudgetBytes: 44_000_000_000, FileLimit: 10240,
            OfflineDeadlineSeconds: offline, OutsideBuildAllowanceBytes: 1_100_000_000);
        Check("original-startup-resource-boundary", () => ReplayCalibrationStartup.ValidateResources(Boundary(111, 12, 5400)));
        Check("corrected-startup-resource-boundary-needs-own-approval", () =>
        {
            var valid = Boundary(113, 13, 6000);
            ReplayCalibrationStartup.ValidateResources(valid);
            foreach (var invalid in new[] { valid with { RetainedChildren = 112 }, valid with { OfflineDeadlineSeconds = 5400 },
                valid with { RetainedOfflineSeconds = 5880 }, valid with { RetainedElapsedSeconds = 14325 },
                valid with { BudgetBytes = 84_000_000_001 }, valid with { MaximumChildren = 138 } })
            {
                bool rejected = false; try { ReplayCalibrationStartup.ValidateResources(invalid); } catch (IOException) { rejected = true; }
                Require(rejected, "Startup diagnostic silently changed its cumulative resource boundary.");
            }
        });
        File.WriteAllText(Path.Combine(root, "verification.json"), JsonSerializer.Serialize(new { result = failures.Count == 0 ? "passed" : "failed", checks, failures,
            effects = "Health/path snapshots, injected wake delegates and256KiB of dummy payload through the actual native sampler; no native window, wake request, engine, media, audio, GPU or child." },
            new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Calibration startup controls: {checks.Count}/{checks.Count + failures.Count} passed");
        return failures.Count == 0 ? 0 : 1;
    }
}
