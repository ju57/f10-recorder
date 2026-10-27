using System.Diagnostics;
using System.Text.Json;

namespace F10Recorder;

internal static class PilotStageRunnerChecks
{
    sealed class ReceiptCollision : PilotStageIo
    {
        bool observed;
        internal override PilotStageCleanup StopAndObserveOwnedProcess(Process process, bool stop)
        {
            var result = base.StopAndObserveOwnedProcess(process, stop);
            observed = result.Verified && process.HasExited;
            return result;
        }
        internal override PilotStagePublication PublishEvidence(string root, byte[] bytes)
        {
            Require(observed, "Collision control requires a real exited child.");
            File.WriteAllText(Path.Combine(root, "receipt.json"), "sentinel\n");
            return base.PublishEvidence(root, bytes);
        }
    }
    sealed class CleanupUnknown : PilotStageIo
    {
        internal override PilotStageCleanup StopAndObserveOwnedProcess(Process process, bool stop)
        {
            var native = base.StopAndObserveOwnedProcess(process, stop);
            Require(native.Verified && process.HasExited, "Unknown control must first stop the real owned child.");
            return new(null, false, "controlled-unknown-after-native-exit");
        }
    }
    sealed class NeverStart : PilotStageIo
    {
        internal int Starts;
        internal override Process StartApprovedProcess(ProcessStartInfo info)
        {
            Starts++;
            throw new System.ComponentModel.Win32Exception(2, "controlled-createprocess-failure");
        }
    }
    sealed class CancelAfterPartial(CancellationTokenSource cancellation) : PilotStageIo
    {
        internal bool Ready;
        internal override Process StartApprovedProcess(ProcessStartInfo info)
        {
            var process = base.StartApprovedProcess(info);
            var clock = Stopwatch.StartNew();
            string partial = Path.Combine(Path.GetDirectoryName(info.ArgumentList[2]), "progress.txt");
            while (!process.HasExited && clock.ElapsedMilliseconds < 1000)
            {
                try { if (File.ReadAllText(partial) == "partial\n") { Ready = true; break; } }
                catch (IOException) { }
                Thread.Sleep(5);
            }
            cancellation.Cancel();
            return process;
        }
    }
    static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    internal static int Run(string root, string only = "all")
    {
        if (only is not ("all" or "S01" or "exit7" or "timeout" or "cancel" or "flood" or "receipt-collision" or "cleanup-unknown" or "admission")) throw new IOException("Unknown selected stage check.");
        using var owner = PilotStorageObservation.Create(root);
        using var suite = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        string input = Path.Combine(root, "source-input.txt");
        File.WriteAllText(input, "input\n");
        var checks = new List<object>();
        int failed = 0;
        void Check(string name, string mode, bool success)
        {
            try
            {
                var request = PilotStageRunner.BindFixture(Path.Combine(root, mode), input, mode);
                File.WriteAllText(Path.Combine(root, mode + "-request.json"), PilotStageRunner.Serialize(request));
                var result = PilotStageRunner.Run(request, suite.Token);
                Require(result.Success == success && result.ExitCode == 0 && result.CleanupVerified && result.ReceiptCommitted, "Actual stage result/exit/cleanup/receipt did not match the contract.");
                Require(File.ReadAllText(Path.Combine(result.Root, "progress.txt")) == "partial\n", "Independent eight-byte partial disappeared.");
                Require(!result.NextStageEnabled && !result.ExecutionEnabled, "Fixture enabled another/media stage.");
                if (success) Require(File.ReadAllBytes(Path.Combine(result.Root, "product.txt")).SequenceEqual("stage-ok\n"u8.ToArray()), "Independent nine-byte product differs.");
                else Require(!File.Exists(Path.Combine(result.Root, "product.txt")) && result.Reason.Contains("product"), "Missing product became a successful exit-only stage.");
                Require(File.Exists(result.ReceiptPath), "Committed receipt is absent.");
                checks.Add(new { name, result = "passed", stage = result });
            }
            catch (Exception error) { failed++; checks.Add(new { name, result = "failed", error = error.ToString() }); }
        }
        if (only is "all" or "S01")
        {
            Check("S01-positive-product-progress-exit-and-receipt", "success", true);
            Check("S01-missing-product-exit-zero-is-failure", "missing-product", false);
        }
        if (only is "all" or "exit7")
        {
            try
            {
                var request = PilotStageRunner.BindFixture(Path.Combine(root, "exit7"), input, "exit7");
                var result = PilotStageRunner.Run(request, suite.Token);
                Require(!result.Success && result.ExitCode == 7 && result.CleanupVerified && result.ReceiptCommitted && result.Reason == "exit-nonzero", "Nonzero stage exit lost its distinct failure/cleanup/evidence.");
                Require(File.ReadAllText(Path.Combine(result.Root, "progress.txt")) == "partial\n" && File.ReadAllText(Path.Combine(result.Root, "stderr.txt")).Contains("fixture-exit7"), "Partial/stderr evidence disappeared.");
                checks.Add(new { name = "exit7-retains-partial-and-real-exit", result = "passed", stage = result });
            }
            catch (Exception error) { failed++; checks.Add(new { name = "exit7-retains-partial-and-real-exit", result = "failed", error = error.ToString() }); }
        }
        if (only is "all" or "timeout")
        {
            try
            {
                var request = PilotStageRunner.BindFixture(Path.Combine(root, "timeout"), input, "wait");
                var clock = Stopwatch.StartNew();
                var result = PilotStageRunner.Run(request, suite.Token);
                Require(!result.Success && result.Status == "timeout" && result.ExitCode != null && result.ActualExitObserved && result.CleanupVerified && result.ProducerAbsent == true && result.ReceiptCommitted, "Deadline did not retain a timed-out stage and verified owned exit.");
                Require(clock.Elapsed.TotalSeconds < 15 && File.ReadAllText(Path.Combine(result.Root, "progress.txt")) == "partial\n", "Timeout exceeded lifetime or lost partial bytes.");
                checks.Add(new { name = "deadline-stops-only-owned-stage-and-keeps-partial", result = "passed", stage = result });
            }
            catch (Exception error) { failed++; checks.Add(new { name = "deadline-stops-only-owned-stage-and-keeps-partial", result = "failed", error = error.ToString() }); }
        }
        if (only is "all" or "cancel")
        {
            try
            {
                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                var pre = PilotStageRunner.Run(PilotStageRunner.BindFixture(Path.Combine(root, "pre-cancel"), input, "wait"), cancelled.Token);
                Require(pre.Status == "cancelled" && pre.Pid == null && !Directory.Exists(pre.Root), "Pre-cancel launched or created stage output.");
                using var afterPartial = CancellationTokenSource.CreateLinkedTokenSource(suite.Token);
                var io = new CancelAfterPartial(afterPartial);
                var result = PilotStageRunner.Run(PilotStageRunner.BindFixture(Path.Combine(root, "cancel"), input, "wait"), afterPartial.Token, io);
                Require(io.Ready && !result.Success && result.Status == "cancelled" && result.ActualExitObserved && result.CleanupVerified && result.ProducerAbsent == true && result.ReceiptCommitted, "Partial-ready cancellation lost its actual exit/receipt.");
                Require(File.ReadAllText(Path.Combine(result.Root, "progress.txt")) == "partial\n", "Partial-ready cancellation lost literal partial.");
                checks.Add(new { name = "pre-cancel-and-after-partial-cancel", result = "passed", pre, stage = result });
            }
            catch (Exception error) { failed++; checks.Add(new { name = "pre-cancel-and-after-partial-cancel", result = "failed", error = error.ToString() }); }
        }
        if (only is "all" or "flood")
        {
            try
            {
                var result = PilotStageRunner.Run(PilotStageRunner.BindFixture(Path.Combine(root, "flood"), input, "flood"), suite.Token);
                Require(!result.Success && result.Status == "output-limit" && result.Truncated && result.CapturedBytes == 65536 && result.ActualExitObserved && result.CleanupVerified && result.ReceiptCommitted, "Combined flood was not capped, terminated and retained as failure.");
                Require(new FileInfo(Path.Combine(result.Root, "stdout.txt")).Length + new FileInfo(Path.Combine(result.Root, "stderr.txt")).Length == 65536, "Captured log bytes exceed the combined bound.");
                checks.Add(new { name = "combined-output-cap-stops-finite-flood", result = "passed", stage = result });
            }
            catch (Exception error) { failed++; checks.Add(new { name = "combined-output-cap-stops-finite-flood", result = "failed", error = error.ToString() }); }
        }
        if (only is "all" or "receipt-collision")
        {
            try
            {
                var result = PilotStageRunner.Run(PilotStageRunner.BindFixture(Path.Combine(root, "receipt-collision"), input, "success"), suite.Token, new ReceiptCollision());
                Require(!result.Success && result.Status == "evidence-failed" && result.ExitCode == 0 && result.ActualExitObserved && result.CleanupVerified && !result.ReceiptCommitted && result.PollReserveHeld, "Evidence collision became successful/durable or lost actual exit.");
                Require(File.ReadAllText(Path.Combine(result.Root, "receipt.json")) == "sentinel\n" && File.Exists(Path.Combine(result.Root, "receipt.pending.json")) && File.ReadAllText(Path.Combine(result.Root, "product.txt")) == "stage-ok\n", "Collision overwrote evidence or removed the failed publication/product.");
                checks.Add(new { name = "publication-collision-after-real-success-is-failure", result = "passed", stage = result });
            }
            catch (Exception error) { failed++; checks.Add(new { name = "publication-collision-after-real-success-is-failure", result = "failed", error = error.ToString() }); }
        }
        if (only is "all" or "cleanup-unknown")
        {
            try
            {
                var result = PilotStageRunner.Run(PilotStageRunner.BindFixture(Path.Combine(root, "cleanup-unknown"), input, "success"), suite.Token, new CleanupUnknown());
                Require(!result.Success && result.Status == "cleanup-unverified" && result.ExitCode == 0 && result.ActualExitObserved && !result.CleanupVerified && result.ProducerAbsent == null && result.ReceiptCommitted && result.PollReserveHeld && result.CleanupOrigin == "controlled-unknown-after-native-exit", "Controlled cleanup uncertainty became native success or released reserve.");
                checks.Add(new { name = "controlled-unknown-after-real-exit-keeps-reserve", result = "passed", stage = result });
            }
            catch (Exception error) { failed++; checks.Add(new { name = "controlled-unknown-after-real-exit-keeps-reserve", result = "failed", error = error.ToString() }); }
        }
        if (only is "all" or "admission")
        {
            try
            {
                string absent = Path.Combine(root, "never-created");
                var request = PilotStageRunner.BindFixture(absent, input, "success");
                var noStart = new NeverStart();
                _ = PilotStageRunner.Preview(null);
                _ = PilotStageRunner.Preview(request with { Plan = request.Plan with { Root = null } });
                Require(!Directory.Exists(absent), "Preview touched the filesystem.");
                void Rejected(string name, PilotStageRequest candidate)
                {
                    var result = PilotStageRunner.Run(candidate, suite.Token, noStart);
                    Require(!result.Success && result.Pid == null && result.ExitCode == null && noStart.Starts == 0 && !Directory.Exists(absent), "Prelaunch negative started a process or created its root: " + name);
                }
                Rejected("missing-plan", null);
                Rejected("missing-host", request with { Plan = request.Plan with { HostSha256 = null } });
                Rejected("missing-build", request with { Plan = request.Plan with { BuildSha256 = null } });
                Rejected("changed-stage", request with { Plan = request.Plan with { StageId = "changed" } });
                Rejected("general-route", request with { Arguments = [request.Plan.AssemblyPath, "--worker"] });
                Rejected("digest", request with { PlanSha256 = new string('0', 64) });
                PilotStageRequest Rebind(PilotStagePlan plan) => new(plan, PilotStageRunner.Digest(plan), request.Arguments);
                Rejected("stale-input-hash", Rebind(request.Plan with { InputSha256 = new string('0', 64) }));
                Rejected("stale-host-hash", Rebind(request.Plan with { HostSha256 = new string('0', 64) }));
                Rejected("input-identity", Rebind(request.Plan with { InputIdentity = "stale" }));
                Rejected("parent-identity", Rebind(request.Plan with { ParentIdentity = "stale" }));
                var staleBuild = new SortedDictionary<string, string>(request.Plan.BuildSha256, StringComparer.Ordinal) { ["F10Recorder.Core.dll"] = new string('0', 64) };
                Rejected("stale-build-hash", Rebind(request.Plan with { BuildSha256 = staleBuild }));
                File.WriteAllText(input, "other\n"); // Same six-byte length; hash must detect replacement.
                Rejected("same-size-input-change", request);
                File.WriteAllText(input, "input\n");
                string collision = Path.Combine(root, "existing.txt");
                File.WriteAllText(collision, "sentinel\n");
                var existing = PilotStageRunner.Run(PilotStageRunner.BindFixture(collision, input, "success"), suite.Token, noStart);
                Require(!existing.Success && existing.Pid == null && noStart.Starts == 0 && File.ReadAllText(collision) == "sentinel\n", "Existing-file root collision changed sentinel or launched.");
                var failedLaunch = PilotStageRunner.Run(PilotStageRunner.BindFixture(Path.Combine(root, "launch-failure"), input, "success"), suite.Token, noStart);
                Require(noStart.Starts == 1 && failedLaunch.Status == "launch-failed" && failedLaunch.Pid == null && failedLaunch.ExitCode == null && !failedLaunch.Success && failedLaunch.PollReserveHeld && failedLaunch.ReceiptCommitted, "Controlled CreateProcess failure invented a child exit or lost its distinct outcome/evidence.");
                foreach (string[] arguments in new string[][] {
                    ["--pilot-stage-unknown", absent], ["--PILOT-STAGE-CHECKS", absent],
                    ["--pilot-stage-checks", absent, "--only", "UNKNOWN"],
                    ["--pilot-stage-checks", absent, "--ONLY", "S01"],
                    ["--pilot-stage-run", absent, "--only", "S01"],
                    ["--engine-root", absent, "--pilot-stage-checks", absent],
                    ["--pilot-stage-checks"], ["--pilot-stage-checks", absent, "extra"] })
                {
                    var output = Console.Out;
                    using var captured = new StringWriter();
                    int exit;
                    try { Console.SetOut(captured); exit = WorkerChecks.Main(arguments); }
                    finally { Console.SetOut(output); }
                    Require(exit == 2 && captured.ToString().Contains("stage-input-rejected") && !Directory.Exists(absent), "Actual Main route escaped its early fail-closed guard.");
                }
                checks.Add(new { name = "inert-preview-identity-binding-and-prelaunch-failures", result = "passed", controlled_launch_failure = failedLaunch });
            }
            catch (Exception error) { failed++; checks.Add(new { name = "inert-preview-identity-binding-and-prelaunch-failures", result = "failed", error = error.ToString() }); }
        }
        string json = PilotStageRunner.Serialize(new { result = failed == 0 ? "passed" : "failed", checks });
        File.WriteAllText(Path.Combine(root, "verification.json"), json);
        Console.WriteLine(json);
        return failed == 0 ? 0 : 1;
    }
}
