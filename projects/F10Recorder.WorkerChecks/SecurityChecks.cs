using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace F10Recorder;

internal static class SecurityChecks
{
    static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    static Process Start(params string[] args)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        info.ArgumentList.Add("--security-child"); foreach (string arg in args) info.ArgumentList.Add(arg);
        foreach (string arg in new[] { "--engine-root", MediaDependency.RootDirectory, "--ownership-root", ProfileOwnership.RootDirectory }) info.ArgumentList.Add(arg);
        return Process.Start(info) ?? throw new IOException("Security test child did not start.");
    }
    static void Stop(Process child) { if (!child.HasExited) { child.Kill(); child.WaitForExit(5000); } }
    static void Until(Func<bool> condition)
    {
        var deadline = Stopwatch.StartNew(); while (!condition() && deadline.ElapsedMilliseconds < 10000) Thread.Sleep(25);
        Require(condition(), "Security fixture timed out.");
    }
    internal static int Child(string[] args)
    {
        switch (args[1])
        {
            case "pipe":
                using (var pipe = new NamedPipeClientStream(".", args[2], PipeDirection.In))
                {
                    pipe.Connect(5000);
                    int value = pipe.ReadByte(); File.WriteAllText(args[3], value.ToString()); return 0;
                }
            case "idle": Thread.Sleep(20000); return 0;
            case "owner":
                ProfileOwnership.RootDirectory = args[2];
                using (ProfileOwnership.Acquire(args[3])) { File.WriteAllText(args[4], "ready"); Thread.Sleep(20000); }
                return 0;
            case "worker":
                ProfileOwnership.RootDirectory = args[2];
                return RecorderWorker.Run(args[3], true, synthetic: true, setStartup: _ => { });
            case "foreign":
                // Run explicitly under a different account after the parent has
                // created the fixture. A timeout is inconclusive, never a pass.
                try { using var pipe = new NamedPipeClientStream(".", args[2], PipeDirection.In); pipe.Connect(3000); return 1; }
                catch (UnauthorizedAccessException) { return 0; }
        }
        return 2;
    }
    internal static int Run(string data, string package)
    {
        Directory.CreateDirectory(data); RecorderHost.Initialize(data);
        var report = new Dictionary<string, object>();
        string before = MediaDependency.RootDirectory, ownershipBefore = ProfileOwnership.RootDirectory;
        try
        {
            MediaDependency.RootDirectory = Path.Combine(data, "managed");
            ProfileOwnership.RootDirectory = Path.Combine(data, "ownership");
            MediaDependency.Ensure(true, (_, path) => File.Copy(package, path));
            CheckPipes(data, report);
            CheckOwnership(data, report);
            CheckScreenshots(data, report);
            CheckStorage(data, report);
            string accountFixture = Path.Combine(data, "account-fixture"); Directory.CreateDirectory(accountFixture);
            using (var identity = WindowsIdentity.GetCurrent())
                File.WriteAllText(Path.Combine(accountFixture, "challenge.json"), JsonSerializer.Serialize(new { Pipe = "unused", OwnerSid = identity.User.Value, LockPath = "unused" }, WorkerProtocol.Json));
            Require(CrossAccountChecks.Client(Path.Combine(accountFixture, "challenge.json")) != 0, "A same-account client was reported as cross-account evidence.");
            using (var rejected = JsonDocument.Parse(File.ReadAllText(Path.Combine(accountFixture, "client-result.json"))))
                Require(rejected.RootElement.GetProperty("Error").GetString().Contains("genuinely different account"), "Cross-account fixture failed before checking account identity.");
            report["crossAccountFixtureRejectsSameAccountEvidence"] = true;
            CheckAcknowledgements(report).GetAwaiter().GetResult();
            CheckOldReply(data).GetAwaiter().GetResult(); report["oldReplyProtocolRejected"] = true;
            report["crossAccountIsolation"] = "Outstanding: same-account kernel checks and ACL checks do not establish different-account execution.";
            report["liveAudioAndWindowBinding"] = "Outstanding: no system audio, desktop or application window was captured.";
            report["result"] = "passed"; return 0;
        }
        catch (Exception ex) { report["result"] = "failed"; report["error"] = ex.ToString(); return 1; }
        finally
        {
            MediaDependency.RootDirectory = before; ProfileOwnership.RootDirectory = ownershipBefore;
            File.WriteAllText(Path.Combine(data, "verification.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
    internal static int LiveAudio(string data, string engineRoot)
    {
        // Explicit opt-in only: real default-output WASAPI, generated video.
        // No desktop pixels, UI, input injection, startup or device changes.
        TrustedStorage.CreatePrivateDirectory(data); RecorderHost.Initialize(data); MediaDependency.RootDirectory = engineRoot;
        var report = new Dictionary<string, object>();
        bool replaced = false;
        RecorderRuntime.CaptureArgumentProbe = arguments => arguments.Select(argument =>
        {
            if (!argument.StartsWith("ddagrab=", StringComparison.Ordinal)) return argument;
            replaced = true; int comma = argument.IndexOf(',');
            return "testsrc2=size=640x360:rate=30" + (comma < 0 ? "" : argument.Substring(comma));
        }).ToList();
        try
        {
            using var privateEvidence = TrustedStorage.Acquire(data);
            var settings = new Settings { ReplayMinutes = 1, FramesPerSecond = 30, BitrateMbps = 5, SystemAudio = true };
            using var capture = new CaptureSession(settings, data, false);
            Require(replaced, "The video source was not replaced with generated imagery.");
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(8)) { capture.Maintain(); Require(capture.Healthy, "Live audio pipeline stopped."); Thread.Sleep(100); }
            Require(capture.Metrics.CaptureProgressSeconds > 3 && capture.BufferedSeconds > 2, "Live audio did not advance.");
            string clip = capture.Save(capture.Position, Path.Combine(data, "clips"));
            using var decode = CaptureSession.StartProcess(MediaDependency.ResolveExecutable(), new[] { "-hide_banner", "-v", "error", "-i", clip, "-map", "0:v:0", "-map", "0:a:0", "-f", "null", "-" }, data);
            var error = decode.StandardError.ReadToEndAsync(); var output = decode.StandardOutput.ReadToEndAsync();
            if (!decode.WaitForExit(15000)) { decode.Kill(); decode.WaitForExit(); throw new TimeoutException("Live audio fixture decode timed out."); }
            Require(decode.ExitCode == 0 && error.Result.Length == 0, "Live audio/video decode failed: " + error.Result); output.Wait();
            report["result"] = "passed"; report["clip"] = clip; report["metrics"] = capture.Metrics;
            report["scope"] = "Eight seconds of real default-output system audio with generated video; both tracks decoded. No desktop capture or perceptual audio-quality/sync claim.";
            return 0;
        }
        catch (Exception ex) { report["result"] = "failed"; report["error"] = ex.ToString(); return 1; }
        finally { RecorderRuntime.CaptureArgumentProbe = null; File.WriteAllText(Path.Combine(data, "verification.json"), JsonSerializer.Serialize(report, WorkerProtocol.Json)); }
    }
    static void CheckPipes(string data, Dictionary<string, object> report)
    {
        string name = "F10Recorder-security-" + Guid.NewGuid().ToString("N"), output = Path.Combine(data, "admitted.txt");
        using (var pipe = AudioPipe.Create(name))
        using (var child = Start("pipe", name, output))
        {
            try
            {
                try { AudioPipe.Accept(pipe, child, null, 5000); }
                catch (Exception ex) { throw new IOException("Expected-child pipe admission failed; process=" + Environment.ProcessPath + "; exited=" + child.HasExited + (child.HasExited ? "; error=" + child.StandardError.ReadToEnd() : ""), ex); }
                pipe.WriteByte(83); pipe.Flush(); pipe.Dispose();
                Require(child.WaitForExit(5000) && child.ExitCode == 0 && File.ReadAllText(output) == "83", "Expected child did not receive the marker.");
            }
            finally { Stop(child); }
        }
        report["expectedChildAdmitted"] = true;
        name += "-wrong"; output = Path.Combine(data, "rejected.txt");
        using (var pipe = AudioPipe.Create(name))
        using (var expected = Start("idle"))
        using (var other = Start("pipe", name, output))
        {
            try
            {
                bool rejected = false; try { AudioPipe.Accept(pipe, expected, null, 5000); } catch (IOException) { rejected = true; }
                Require(rejected, "Wrong PID was admitted.");
                Require(other.WaitForExit(5000) && File.ReadAllText(output) == "-1", "Wrong PID received bytes or did not close.");
            }
            finally { Stop(expected); Stop(other); }
        }
        report["wrongPidRejectedBeforeAnyBytes"] = true;
        using (var expected = Start("idle"))
        {
            try
            {
                using (var pipe = AudioPipe.Create(name + "-timeout"))
                { bool failed = false; try { AudioPipe.Accept(pipe, expected, null, 100); } catch (IOException) { failed = true; } Require(failed, "No-client timeout was ignored."); }
                using (var pipe = AudioPipe.Create(name + "-cancel"))
                { bool failed = false; try { AudioPipe.Accept(pipe, expected, () => true, 1000); } catch (OperationCanceledException) { failed = true; } Require(failed, "Cancellation was ignored."); }
            }
            finally { Stop(expected); }
        }
        report["pipeTimeoutCancellationAndCleanup"] = true;
    }
    static void CheckOwnership(string data, Dictionary<string, object> report)
    {
        string profile = Path.Combine(data, "profile"), second = Path.Combine(data, "other-profile");
        Directory.CreateDirectory(profile); Directory.CreateDirectory(second);
        string sentinel = Path.Combine(profile, "buffer", "session-sentinel", "chunk-sentinel.ts");
        Directory.CreateDirectory(Path.GetDirectoryName(sentinel)); File.WriteAllText(sentinel, "keep");
        using (ProfileOwnership.Acquire(profile))
        {
            bool refused = false; try { using var other = ProfileOwnership.Acquire(Path.Combine(profile.ToUpperInvariant(), ".")); } catch (IOException) { refused = true; }
            Require(refused, "An alias acquired the same profile twice.");
            using (ProfileOwnership.Acquire(second)) { }
            using var blocked = Start("worker", ProfileOwnership.RootDirectory, profile);
            Require(blocked.WaitForExit(10000) && blocked.ExitCode == 1 && File.ReadAllText(sentinel) == "keep", "A blocked worker changed the active cache.");
        }
        using (ProfileOwnership.Acquire(profile)) { }
        string ready = Path.Combine(data, "owner-ready.txt");
        using (var owner = Start("owner", ProfileOwnership.RootDirectory, profile, ready))
        {
            try
            {
                Until(() => File.Exists(ready)); bool refused = false;
                try { using var other = ProfileOwnership.Acquire(profile); } catch (IOException) { refused = true; }
                Require(refused, "A child-held profile lock was ignored.");
            }
            finally { Stop(owner); }
        }
        using (ProfileOwnership.Acquire(profile)) { }
        string alias = Path.Combine(data, "profile-alias");
        CreateJunction(alias, profile);
        using (ProfileOwnership.Acquire(alias))
        {
            bool refused = false; try { Directory.Move(profile, profile + "-moved"); } catch (IOException) { refused = true; }
            Require(refused, "A leased profile target could be moved.");
            refused = false; try { using var other = ProfileOwnership.Acquire(profile); } catch (IOException) { refused = true; }
            Require(refused, "Junction and target acquired separate ownership.");
        }
        Directory.Delete(alias); CreateJunction(alias, second);
        using (ProfileOwnership.Acquire(alias)) { }
        report["profileAliasExclusivityDistinctProfilesCachePreservationAndCrashRelease"] = true;
        CheckWorkerRebinding(data, profile, second, report);
        report["profileTargetPinnedUntilRelease"] = true;
    }
    static void CheckWorkerRebinding(string data, string first, string second, Dictionary<string, object> report)
    {
        void Configure(string path, int key)
        {
            new Settings { ReplayMinutes = 1, FramesPerSecond = 30, BitrateMbps = 5, OutputDirectory = Path.Combine(path, "clips"), ScreenshotDirectory = Path.Combine(path, "screenshots"),
                SaveKey = key, SaveModifiers = 7, OverlayKey = key + 1, OverlayModifiers = 7, ScreenshotKey = key + 2, ScreenshotModifiers = 7 }.Save(Path.Combine(path, "settings.json"));
        }
        Configure(first, 113); Configure(second, 116);
        string alias = Path.Combine(data, "worker-profile-alias"); CreateJunction(alias, first);
        using var workerA = Start("worker", ProfileOwnership.RootDirectory, alias);
        Process workerB = null;
        WorkerStatus Read(string path)
        {
            using var deadline = new CancellationTokenSource(400);
            return WorkerProtocol.CallAsync(path, new WorkerRequest(), deadline.Token).GetAwaiter().GetResult().Status;
        }
        bool IsReady(string path) { try { return Read(path).Paused; } catch (IOException) { return false; } catch (OperationCanceledException) { return false; } }
        try
        {
            Until(() => { if (workerA.HasExited) throw new IOException("First paused worker exited: " + workerA.StandardError.ReadToEnd()); return IsReady(first); });
            var oldRequest = WorkerProtocol.CallAsync(first, new WorkerRequest { Version = 2 }).GetAwaiter().GetResult();
            Require(oldRequest.Error != null && Read(first).Paused, "An older client request changed the paused worker.");
            Directory.Delete(alias); CreateJunction(alias, second);
            workerB = Start("worker", ProfileOwnership.RootDirectory, alias);
            Until(() => { if (workerB.HasExited) throw new IOException("Second paused worker exited: " + workerB.StandardError.ReadToEnd()); return IsReady(second); });
            Require(Read(first).WorkerProcessId == workerA.Id && Read(second).WorkerProcessId == workerB.Id, "Retargeting changed an active worker's bound profile.");
            string sentinel = Path.Combine(second, "buffer", "session-preserve", "chunk-sentinel.ts"); Directory.CreateDirectory(Path.GetDirectoryName(sentinel)); File.WriteAllText(sentinel, "keep");
            var config = Read(first).Config; config.BitrateMbps = 11;
            var applied = WorkerProtocol.CallAsync(first, new WorkerRequest { Command = "apply", Settings = config, Value = false }).GetAwaiter().GetResult();
            Require(applied.Error == null, "Alias fixture could not apply settings: " + applied.Error);
            Require(File.ReadAllText(sentinel) == "keep", "An active worker followed a retargeted alias into another cache.");
            report["retargetedAliasCannotMoveActiveWorkerIntoAnotherCache"] = true;
        }
        finally
        {
            try { using var deadline = new CancellationTokenSource(1000); WorkerProtocol.CallAsync(first, new WorkerRequest { Command = "quit" }, deadline.Token).GetAwaiter().GetResult(); } catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
            if (workerB != null) { try { using var deadline = new CancellationTokenSource(1000); WorkerProtocol.CallAsync(second, new WorkerRequest { Command = "quit" }, deadline.Token).GetAwaiter().GetResult(); } catch (Exception ex) when (ex is IOException or OperationCanceledException) { } Stop(workerB); workerB.Dispose(); }
            Stop(workerA);
        }
    }
    static void CreateJunction(string alias, string target)
    {
        string command = "$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path $env:F10_SECURITY_LINK -Target $env:F10_SECURITY_TARGET | Out-Null";
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(command)) }) info.ArgumentList.Add(argument);
        info.Environment["F10_SECURITY_LINK"] = alias; info.Environment["F10_SECURITY_TARGET"] = target;
        using var child = Process.Start(info) ?? throw new IOException("Junction fixture did not start.");
        var error = child.StandardError.ReadToEndAsync();
        if (!child.WaitForExit(10000)) { child.Kill(); child.WaitForExit(); throw new TimeoutException("Junction fixture timed out."); }
        Require(child.ExitCode == 0, "Junction fixture failed: " + error.Result);
    }
    static void CheckScreenshots(string data, Dictionary<string, object> report)
    {
        string folder = Path.Combine(data, "screenshots"); Directory.CreateDirectory(folder);
        string existing = Path.Combine(folder, "existing.png"); File.WriteAllText(existing, "preserve");
        bool failed = false;
        try { ScreenshotCapture.CaptureSource("color=size=64x64:rate=1", folder, false, () => false); } catch (IOException) { failed = true; }
        Require(failed, "Invalid target started a screenshot.");
        int checks = 0; failed = false;
        try { ScreenshotCapture.CaptureSource("color=size=64x64:rate=1", folder, false, () => ++checks == 1); } catch (IOException) { failed = true; }
        Require(failed && checks >= 2 && Directory.GetFiles(folder).Length == 1 && File.ReadAllText(existing) == "preserve", "Stale screenshot was published or partial output survived.");
        string saved = ScreenshotCapture.CaptureSource("color=size=64x64:rate=1", folder, false, () => true);
        Require(File.Exists(saved) && Directory.GetFiles(folder, "*.partial.png").Length == 0, "Valid generated screenshot did not save.");
        var first = new WindowTargetSnapshot { Handle = 1, ProcessId = 1, ProcessStartedUtcTicks = 1, Generation = 1, Phase = CapturePhase.Starting };
        var changed = new WindowTargetSnapshot { Handle = 1, ProcessId = 2, ProcessStartedUtcTicks = 1, Generation = 1, Phase = CapturePhase.Starting };
        Require(!ScreenshotCapture.SameTarget(first, changed), "Same HWND with different PID was accepted.");
        report["staleScreenshotRejectedValidSyntheticSavedExistingPreserved"] = true;
    }
    static void CheckStorage(string data, Dictionary<string, object> report)
    {
        string path = Path.Combine(data, "unsafe-storage"); TrustedStorage.CreatePrivateDirectory(path);
        var directory = new DirectoryInfo(path); var acl = directory.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Write, AccessControlType.Allow)); directory.SetAccessControl(acl);
        bool refused = false; try { using var lease = TrustedStorage.Acquire(path); } catch (IOException) { refused = true; }
        Require(refused, "Cross-account writable private storage was trusted.");
        report["unsafeStorageAclRejected"] = true;
        string unrestricted = Path.Combine(data, "unrestricted-storage"); TrustedStorage.CreatePrivateDirectory(unrestricted);
        var unrestrictedDirectory = new DirectoryInfo(unrestricted); var original = unrestrictedDirectory.GetAccessControl();
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var raw = new RawSecurityDescriptor(ControlFlags.DiscretionaryAclPresent | ControlFlags.DiscretionaryAclProtected, identity.User, identity.User, null, null);
            byte[] bytes = new byte[raw.BinaryLength]; raw.GetBinaryForm(bytes, 0);
            var security = new DirectorySecurity(); security.SetSecurityDescriptorBinaryForm(bytes, AccessControlSections.Access); unrestrictedDirectory.SetAccessControl(security);
            Require(new RawSecurityDescriptor(unrestrictedDirectory.GetAccessControl().GetSecurityDescriptorBinaryForm(), 0).DiscretionaryAcl == null, "Null-DACL fixture did not establish its prerequisite.");
            refused = false; try { using var lease = TrustedStorage.Acquire(unrestricted); } catch (IOException) { refused = true; }
            Require(refused, "Null DACL was trusted as an empty ACL.");
        }
        finally
        {
            var restored = new DirectorySecurity(); restored.SetSecurityDescriptorBinaryForm(original.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
            unrestrictedDirectory.SetAccessControl(restored);
        }
        report["nullStorageDaclRejected"] = true;
    }
    static async Task CheckAcknowledgements(Dictionary<string, object> report)
    {
        WorkerStatus Snapshot(long revision, string mode, string completed = null, string instance = "worker-1") => new()
        { Instance = instance, Revision = revision, Config = new Settings { CaptureMode = mode }, CompletedApply = completed };
        var response = new TaskCompletionSource<WorkerReply>(); WorkerRequest submitted = null;
        using var recorder = new WorkerRecorder(Snapshot(1, "monitor"), request => { submitted = request; return response.Task; });
        var apply = recorder.ApplyAsync(new Settings { CaptureMode = "window" }, false);
        Require(!recorder.CaptureModeKnown, "A pending transition trusted cached capture mode.");
        recorder.Update(Snapshot(2, "monitor"));
        Require(!recorder.CaptureModeKnown, "A pre-apply status released exclusion.");
        response.SetException(new IOException("Controlled transport failure after request submission."));
        try { await apply; } catch (IOException) { }
        Require(!recorder.CaptureModeKnown, "A failed transport trusted cached mode.");
        recorder.Update(Snapshot(3, "window", "different-operation"));
        Require(!recorder.CaptureModeKnown, "Another operation acknowledged this transition.");
        recorder.Update(Snapshot(4, "window", submitted.OperationId));
        Require(recorder.CaptureModeKnown && recorder.Config.CaptureMode == "window", "Completed transition did not restore authoritative mode.");
        recorder.Update(Snapshot(2, "monitor"));
        Require(recorder.Config.CaptureMode == "window", "A stale reply replaced the acknowledged mode.");
        response = new TaskCompletionSource<WorkerReply>();
        apply = recorder.ApplyAsync(new Settings { CaptureMode = "monitor" }, false);
        Require(!recorder.CaptureModeKnown, "Window-to-monitor did not protect the pending transition.");
        response.SetResult(new WorkerReply { Status = Snapshot(5, "window", submitted.OperationId), Error = "Controlled settings rejection." });
        try { await apply; } catch (InvalidOperationException) { }
        Require(recorder.CaptureModeKnown && recorder.Config.CaptureMode == "window", "A rejected acknowledged transition lost the active mode.");
        recorder.Update(Snapshot(1, "monitor", instance: "worker-2"));
        recorder.Update(Snapshot(100, "window"));
        Require(recorder.Config.CaptureMode == "monitor", "A retired worker revived stale capture mode.");
        report["pendingRejectedTimedOutStaleAndReplacedWorkerCaptureMode"] = true;
    }
    static async Task CheckOldReply(string data)
    {
        string profile = Path.Combine(data, "old-reply"); Directory.CreateDirectory(profile);
        using var deadline = new CancellationTokenSource(5000);
        using var pipe = new NamedPipeServerStream(WorkerIdentity.PipeName(profile), PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var server = Task.Run(async () =>
        {
            await pipe.WaitForConnectionAsync(deadline.Token);
            await WorkerProtocol.ReadAsync<WorkerRequest>(pipe, deadline.Token);
            await WorkerProtocol.WriteAsync(pipe, new WorkerReply { Version = 2 }, deadline.Token);
        });
        bool rejected = false;
        try { await WorkerProtocol.CallAsync(profile, new WorkerRequest(), deadline.Token); } catch (WorkerProtocolMismatchException) { rejected = true; }
        await server; Require(rejected, "A pre-remediation worker reply was accepted.");
    }
}
