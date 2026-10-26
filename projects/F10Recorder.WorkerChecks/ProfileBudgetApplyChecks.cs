using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace F10Recorder;

internal static partial class ProfileCorrectionChecks
{
    // Extends the existing actual paused-worker route. Reuses one fixed profile
    // and payload; each invocation retains its own small before/after evidence.
    internal static int RunBudget(string data, string engineSource, string engineRoot)
    {
        Directory.CreateDirectory(data);
        string run = Path.Combine(data, "runs", DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(run);
        string profile = Path.Combine(data, "profile"), settings = Path.Combine(profile, "settings.json"), state = Path.Combine(profile, "worker-state.json");
        string payload = Path.Combine(profile, "buffer", "session-retained", "retained.bin"), alias = payload + ".alias", tiny = payload + ".tiny";
        var report = new Dictionary<string, object>(); var cases = new List<object>(); var exits = new List<object>();
        Process worker = null;
        string startup = StartupValue();
        string pidMarker = Path.Combine(data, "last-worker.json");
        try
        {
            Require(!File.Exists(Path.Combine(data, "cleanup-failed.txt")), "A previous failed cleanup must be reviewed before fixture reuse.");
            if (File.Exists(pidMarker))
            {
                using var marker = JsonDocument.Parse(File.ReadAllText(pidMarker));
                try
                {
                    using var previous = Process.GetProcessById(marker.RootElement.GetProperty("pid").GetInt32());
                    Require(previous.StartTime.ToUniversalTime().Ticks != marker.RootElement.GetProperty("started").GetInt64() || previous.HasExited,
                        "Previous task worker remains active; fixture reset is forbidden.");
                }
                catch (ArgumentException) { } // Recorded PID no longer exists.
            }
            StageEngine(engineSource, engineRoot, run, reuseOnly: true);
            ProfileOwnership.RootDirectory = Path.Combine(data, "ownership");
            Directory.CreateDirectory(profile);
            Snapshot("before-fixture-reset");
            string callback = Path.Combine(profile, "fixture-startup-intent.txt");
            if (File.Exists(callback)) File.Delete(callback); // Small task control, preserved above.
            if (File.Exists(tiny)) File.Delete(tiny); // Only the known tiny task fixture.
            var config = new Settings { ReplayMinutes = 1, BitrateMbps = 5, ReplayCacheBudgetBytes = 100_000_000,
                FramesPerSecond = 60, VideoCodec = "hevc_nvenc", SystemAudio = false, DisplayIndex = 0,
                CaptureMode = "window", WindowExecutable = Path.Combine(profile, "unavailable.exe"), WindowClass = "BudgetApplyUnavailable",
                SaveKey = 115, SaveModifiers = 7, OverlayKey = 116, OverlayModifiers = 7, ScreenshotKey = 117, ScreenshotModifiers = 7,
                OutputDirectory = Path.Combine(profile, "Clips ação"), ScreenshotDirectory = Path.Combine(profile, "Pictures") };
            var document = JsonSerializer.SerializeToNode(config, WorkerProtocol.Json).AsObject();
            document["DeviceIdentity"] = "retained-device"; document["MicrophoneMuted"] = false; document["MicrophoneLevel"] = 0;
            File.WriteAllBytes(settings, Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(document.ToJsonString().Replace(",", ",\r\n"))).ToArray());
            File.WriteAllText(state, "{\"SchemaVersion\":1,\"Paused\":false,\"UserStopped\":true,\"ActivationRequired\":true,\"OpaqueState\":0}");
            byte[] originalSettings = File.ReadAllBytes(settings), originalState = File.ReadAllBytes(state);
            Directory.CreateDirectory(Path.GetDirectoryName(payload));
            string payloadRecord = Path.Combine(data, "payload.json");
            if (!File.Exists(payload))
            {
                Require(!File.Exists(payloadRecord), "Recorded large fixture disappeared; do not silently replace it.");
                var random = new Random(1305); byte[] block = new byte[65536];
                using (var output = new FileStream(payload, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    for (long remaining = 45_000_000; remaining > 0; remaining -= Math.Min(block.Length, remaining))
                    { random.NextBytes(block); output.Write(block, 0, (int)Math.Min(block.Length, remaining)); }
                    output.Flush(true);
                }
                File.WriteAllText(payloadRecord, JsonSerializer.Serialize(new { bytes = 45_000_000, seed = 1305, sha256 = MediaDependency.Hash(payload) }));
            }
            using (var recorded = JsonDocument.Parse(File.ReadAllText(payloadRecord)))
                Require(new FileInfo(payload).Length == 45_000_000 && MediaDependency.Hash(payload) == recorded.RootElement.GetProperty("sha256").GetString(), "Large reused fixture changed.");
            Require((File.GetAttributes(payload) & (FileAttributes.SparseFile | FileAttributes.Compressed | FileAttributes.ReparsePoint)) == 0, "Payload is not an admitted ordinary file.");
            var files = new ReplayFiles(); var allocation = files.Observe(payload);
            Require(allocation.AllocatedBytes > 40_000_000 && allocation.AllocatedBytes < 100_000_000, "Fixture does not establish the admitted 40M/100M allocation boundary.");
            string payloadHash = MediaDependency.Hash(payload);
            report["payload"] = new { path = payload, bytes = 45_000_000, allocation, sha256 = payloadHash };
            string saved = Path.Combine(profile, "saved.mp4"); File.WriteAllText(saved, "Saved-media sentinel; not playable footage.");
            string savedHash = MediaDependency.Hash(saved);
            var launch = new ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = false, CreateNoWindow = true };
            foreach (string argument in new[] { "--profile-correction-worker", profile, "--ownership-root", ProfileOwnership.RootDirectory, "--engine-root", engineRoot }) launch.ArgumentList.Add(argument);
            worker = Process.Start(launch) ?? throw new IOException("Paused task worker did not start.");
            File.WriteAllText(pidMarker, JsonSerializer.Serialize(new { pid = worker.Id, started = worker.StartTime.ToUniversalTime().Ticks }));
            var initial = Call(new WorkerRequest()); AssertHeld(initial.Status);
            string control = WorkerIdentity.ControlTitle(profile); IntPtr controlWindow = Native.FindWindow(null, control);
            Native.GetWindowThreadProcessId(controlWindow, out uint controlPid);
            Require(controlWindow != IntPtr.Zero && controlPid == worker.Id, "Hidden worker control identity does not match the task process.");
            report["nativeIdentity"] = new { control, pipe = WorkerIdentity.PipeName(profile), ownership = ProfileOwnership.RootDirectory, workerPid = worker.Id, engineRoot };
            Require(initial.Error == null && initial.Status.WorkerProcessId == worker.Id && initial.Status.SaveRegistered && initial.Status.OverlayRegistered && initial.Status.ScreenshotRegistered, "Initial isolated shortcuts unavailable; do not displace another owner.");
            using var client = new WorkerRecorder(initial.Status, request => Task.FromResult(Call(request)));
            WorkerReply completion = null; WorkerRequest sent = null;
            Settings Draft(long budget) { var next = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(client.Config, WorkerProtocol.Json), WorkerProtocol.Json); next.ReplayCacheBudgetBytes = budget; next.FramesPerSecond = 30; next.OutputDirectory = Path.Combine(profile, "Requested ação"); return next; }
            void Apply(string name, Settings next, bool succeeds)
            {
                var before = Call(new WorkerRequest()).Status; string beforeDocs = Fingerprint(); Snapshot(name + "-before");
                using var actualClient = new WorkerRecorder(before, request => { sent = request; completion = Call(request); return Task.FromResult(completion); });
                Exception rejected = null;
                try { actualClient.ApplyAsync(next, false).GetAwaiter().GetResult(); } catch (Exception ex) { rejected = ex; }
                File.WriteAllText(Path.Combine(run, name + "-reply.json"), JsonSerializer.Serialize(completion, WorkerProtocol.Json)); Snapshot(name + "-after");
                Require(completion?.Status != null && completion.Status.Instance == before.Instance && completion.Status.Revision > before.Revision
                    && completion.Status.CompletedApply == sent.OperationId && !completion.Status.Applying, "Apply did not return a fresh matching completion.");
                AssertHeld(completion.Status);
                if (!succeeds)
                {
                    Require(rejected != null && completion.Error?.Contains("budget", StringComparison.OrdinalIgnoreCase) == true, "Expected definite storage budget rejection; received: " + completion.Error);
                    Require(Fingerprint() == beforeDocs && completion.Status.Config.ReplayCacheBudgetBytes == before.Config.ReplayCacheBudgetBytes
                        && completion.Status.EngineCapabilityContext == before.EngineCapabilityContext && completion.Status.SaveRegistered == before.SaveRegistered
                        && completion.Status.OverlayRegistered == before.OverlayRegistered && completion.Status.ScreenshotRegistered == before.ScreenshotRegistered,
                        "Rejected reduction changed documents, bindings, engine context or current budget.");
                }
                else Require(rejected == null && completion.Error == null && completion.Status.Config.ReplayCacheBudgetBytes == next.ReplayCacheBudgetBytes && actualClient.CaptureModeKnown, "Adequate retry was not acknowledged: " + rejected);
                Require(MediaDependency.Hash(saved) == savedHash, "Apply altered saved media.");
                client.Update(completion.Status);
                cases.Add(new { name, result = "passed", succeeds, submittedBudget = next.ReplayCacheBudgetBytes, actualBudget = completion.Status.Config.ReplayCacheBudgetBytes, operation = sent.OperationId });
            }
            Apply("unsafe-whole-apply", Draft(40_000_000), false);
            Require(!File.Exists(callback), "Storage rejection called startup writer.");
            var reconnected = WorkerProtocol.EnsureWorker(profile, true, "must-not-launch.exe", false);
            Require(reconnected.Status.Instance == initial.Status.Instance && reconnected.Status.WorkerProcessId == worker.Id, "Rejection lost worker ownership/reconnect.");
            string helperTitle = "F10BudgetApply.Helper." + Guid.NewGuid().ToString("N");
            report["helperTitle"] = helperTitle;
            using (var helper = new HotkeyWindow(helperTitle))
            {
                IntPtr window = Native.FindWindow(null, helperTitle); Native.GetWindowThreadProcessId(window, out uint pid);
                Require(window != IntPtr.Zero && pid == Environment.ProcessId, "Hidden helper identity unavailable.");
                Require(Native.RegisterHotKey(window, 499, 7 | 0x4000, 118), "Task F7 chord unavailable; setup must fail without displacing owner.");
                try { var next = Draft(40_000_000); next.SaveKey = 118; Apply("storage-before-occupied-shortcut", next, false); }
                finally { Require(Native.UnregisterHotKey(window, 499), "Task F7 registration did not release normally."); }
            }
            Require(Native.FindWindow(null, helperTitle) == IntPtr.Zero, "Task helper window remained after disposal.");
            Require(!File.Exists(callback), "Rejected shortcut/storage proposal called startup writer.");
            if (!File.Exists(alias)) Require(files.TryHardLink(alias, payload), "Task hard-link fixture unavailable.");
            var aliasAllocation = files.Observe(alias);
            Require(aliasAllocation.Volume == allocation.Volume && aliasAllocation.Identity == allocation.Identity, "Hard-link fixture is not the same allocation.");
            byte[] small = new byte[8192]; new Random(13).NextBytes(small); File.WriteAllBytes(tiny, small);
            Require(files.Observe(tiny).AllocatedBytes > 0, "Tiny copied fixture has no accounted allocation.");
            Apply("tiny-copy-prevents-equality", Draft(allocation.AllocatedBytes), false);
            File.WriteAllBytes(Path.Combine(run, "tiny-copy-fixture.bin"), small);
            File.Delete(tiny); // Explicit fixture action, never production pruning.
            Apply("same-allocation-equality-retry", Draft(allocation.AllocatedBytes), true);
            using (var denied = new FileStream(payload, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Apply("unknown-allocation-rejects-reduction", Draft(40_000_000), false);
                Apply("unchanged-budget-skips-inventory", Draft(allocation.AllocatedBytes), true);
                Apply("increased-budget-skips-inventory", Draft(allocation.AllocatedBytes + 1_000_000), true);
            }
            var persisted = RecordingProfile.Load(profile);
            Require(!persisted.State.Paused && persisted.State.UserStopped && persisted.State.ActivationRequired && !persisted.Settings.SystemAudio && persisted.Settings.DisplayIndex == 0
                && !persisted.Settings.AdditionalPreferences["MicrophoneMuted"].GetBoolean() && persisted.Settings.AdditionalPreferences["MicrophoneLevel"].GetInt32() == 0
                && persisted.Settings.AdditionalPreferences["DeviceIdentity"].GetString() == "retained-device", "Accepted retry lost original false/zero/opaque/stop intent.");
            Require(MediaDependency.Hash(payload) == payloadHash && MediaDependency.Hash(alias) == payloadHash, "Retained payload/alias changed.");
            Require(File.ReadAllBytes(settings + ".original-" + Hash(originalSettings)).SequenceEqual(originalSettings)
                && File.ReadAllBytes(state + ".original-" + Hash(originalState)).SequenceEqual(originalState), "Successful retry did not retain exact original documents.");
            Require(File.ReadAllLines(callback).Length == 3, "Only the three accepted Applies may call fixture startup.");
            QuitWorker();
            Require(!File.Exists(Path.Combine(data, "cleanup-failed.txt")), "Normal Quit failed; next fixture is forbidden.");
            profile = Path.Combine(data, "empty-profile"); settings = Path.Combine(profile, "settings.json"); state = Path.Combine(profile, "worker-state.json");
            Directory.CreateDirectory(profile); Snapshot("empty-before-fixture-reset");
            Require(!Directory.Exists(Path.Combine(profile, "buffer")), "Empty fixture unexpectedly contains retained cache; do not delete it.");
            File.WriteAllText(settings, JsonSerializer.Serialize(config, WorkerProtocol.Json));
            File.WriteAllText(state, "{\"SchemaVersion\":1,\"Paused\":false,\"UserStopped\":true,\"ActivationRequired\":true}");
            launch = new ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = false, CreateNoWindow = true };
            foreach (string argument in new[] { "--profile-correction-worker", profile, "--ownership-root", ProfileOwnership.RootDirectory, "--engine-root", engineRoot }) launch.ArgumentList.Add(argument);
            worker = Process.Start(launch) ?? throw new IOException("Empty-cache paused worker did not start.");
            File.WriteAllText(pidMarker, JsonSerializer.Serialize(new { pid = worker.Id, started = worker.StartTime.ToUniversalTime().Ticks }));
            var emptyInitial = Call(new WorkerRequest()); AssertHeld(emptyInitial.Status); client.Update(emptyInitial.Status);
            report["emptyWorker"] = new { profile, pid = worker.Id, control = WorkerIdentity.ControlTitle(profile), pipe = WorkerIdentity.PipeName(profile) };
            Apply("positively-absent-cache-reduction", Draft(50_000_000), true);
            Require(!Directory.Exists(Path.Combine(profile, "buffer")), "Empty-cache preflight created storage or capture.");
            report["result"] = "passed";
        }
        catch (Exception ex) { report["result"] = "failed"; report["error"] = ex.ToString(); }
        finally
        {
            if (worker != null) QuitWorker();
            string startupAfter = StartupValue();
            report["startupBeforeSha256"] = HashText(startup); report["startupAfterSha256"] = HashText(startupAfter);
            report["startupPreserved"] = startupAfter == startup;
            if (startupAfter != startup) report["result"] = "failed";
            report["cases"] = cases; report["workerExits"] = exits; report["profile"] = profile;
            report["effects"] = "Paused held actual worker, hidden control/F7 helper, F4-F7 shortcuts/current-user pipe; callback startup only; read-only approved engine verification; no execution/probe/GPU/capture/visible UI/input/install/vendor changes.";
            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(run, "verification.json"), json); File.WriteAllText(Path.Combine(data, "verification.json"), json);
        }
        Console.WriteLine($"Budget Apply: {report["result"]}; {run}");
        return (string)report["result"] == "passed" ? 0 : 1;

        WorkerReply Call(WorkerRequest request) { Require(request.Command is "status" or "apply" or "quit", "Forbidden worker command."); return WorkerProtocol.CallAsync(profile, request).GetAwaiter().GetResult(); }
        string Fingerprint() => string.Join("\n", Directory.GetFiles(profile).Where(p => Path.GetFileName(p).StartsWith("settings.json") || Path.GetFileName(p).StartsWith("worker-state.json") || Path.GetFileName(p) == "profile-migration.json").OrderBy(p => p).Select(p => Path.GetFileName(p) + ":" + MediaDependency.Hash(p) + ":" + File.GetLastWriteTimeUtc(p).Ticks));
        void Snapshot(string name)
        {
            string target = Path.Combine(run, name); Directory.CreateDirectory(target);
            foreach (string path in Directory.Exists(profile) ? Directory.GetFiles(profile) : [])
                if (new FileInfo(path).Length < 1_000_000) File.Copy(path, Path.Combine(target, Path.GetFileName(path)));
        }
        void QuitWorker()
        {
            var owned = worker; worker = null; bool normal = false;
            try { var quit = Call(new WorkerRequest { Command = "quit" }); normal = quit.Error == null && owned.WaitForExit(10000) && owned.ExitCode == 0; }
            catch (Exception ex) { report["cleanupError"] = ex.ToString(); }
            finally
            {
                if (!owned.HasExited) { owned.Kill(true); owned.WaitForExit(); }
                exits.Add(new { pid = owned.Id, exited = owned.HasExited, normalQuit = normal, exitCode = owned.ExitCode }); owned.Dispose();
            }
            if (!normal) { report["result"] = "failed"; File.WriteAllText(Path.Combine(data, "cleanup-failed.txt"), "Fixture reuse forbidden until reviewed."); }
        }
    }
}
