using System.Text.Json;

namespace F10Recorder;

internal static class ReplayStorageRuntimeChecks
{
    static readonly string Scope = new('D', 64);
    static ReplayCalibrationAvailability Qualification()
    {
        var headroom = new ReplayCaptureHeadroom(Scope, @"\\?\Volume{00000000-0000-0000-0000-000000000001}\", 4096, 4096, 8192);
        return new(new("controlled-runtime", Scope, headroom.Volume, 4096, 0, 0, [], headroom), null);
    }
    sealed class CapacityFiles(long bytes) : ReplayFiles
    {
        internal long Bytes = bytes;
        internal Action BeforeCapacity;
        internal override ReplayVolumeCapacity ObserveCapacity(string directory)
        {
            BeforeCapacity?.Invoke();
            return new(@"\\?\Volume{00000000-0000-0000-0000-000000000001}\", 4096, Bytes);
        }
    }
    sealed class DrainSource(string directory) : IReplaySessionSource
    {
        internal readonly ManualResetEventSlim Entered = new(false), Release = new(false);
        public ReplaySessionInfo ReplayInfo => new(directory, Path.Combine(directory, "segments.csv"), 300, 20, 60, "h264_nvenc", ".ts", false, false);
        public bool Stopped { get; private set; }
        public string Diagnostics => "controlled nonmedia drain";
        public double SaveCutoffAt(long timestamp) => 0;
        public void FreezeAt(long timestamp) { }
        public void StopCapture()
        {
            Entered.Set();
            if (!Release.Wait(TimeSpan.FromSeconds(5))) throw new IOException("Controlled drain was not released.");
            Stopped = true;
        }
        public void CheckCaptureHealth(double end) { }
        public void Export(IReadOnlyList<string> arguments, string snapshot) => throw new NotSupportedException();
        public void Dispose() { }
    }
    internal static int Run(string root)
    {
        Directory.CreateDirectory(root);
        var checks = new List<object>(); int failures = 0;
        void Check(string name, Action action)
        {
            try { action(); checks.Add(new { name, passed = true }); }
            catch (Exception ex) { failures++; checks.Add(new { name, passed = false, error = ex.ToString() }); }
        }
        foreach (bool missingModel in new[] { true, false })
        Check(missingModel ? "never-launched unmeasured hold resumes after worker replacement" : "never-launched low-space hold resumes after worker replacement", () =>
        {
            string name = missingModel ? "preflight-model" : "preflight-space";
            string profile = Path.Combine(root, name), security = Path.Combine(root, name + "-security");
            Directory.CreateDirectory(profile);
            var files = new CapacityFiles(missingModel ? 9_000_000_000 : 2_000_000_000);
            ReplayCalibrationAvailability available = missingModel ? new(null, "Controlled missing model.") : Qualification();
            var latch = new ReplayStorageStopLatch(profile, security);
            using (var first = new ReplayStorageCoordinator(latch, new ReplayVolumeReservations(security), files, () => available, (_, _) => true))
            {
                bool rejected = false;
                try { first.RequireAdmission(profile, profile); }
                catch (IOException error) { rejected = true; first.StopForStorageFailure(error.Message); }
                Require(rejected && latch.Read().Held, "Preflight must reject and retain an explicit-Resume hold.");
                var metrics = new RecorderMetrics(); first.WriteMetrics(metrics);
                Require(metrics.ReplayStorageSafetyHoldId == latch.Read().HoldId && metrics.ReplayStorageSafetyHoldId != null,
                    "Current-state clients need the durable hold identity, including initial rejection.");
            }
            available = Qualification(); files.Bytes = 9_000_000_000;
            using var replacement = new ReplayStorageCoordinator(new ReplayStorageStopLatch(profile, security),
                new ReplayVolumeReservations(security), files, () => available, (_, _) => true);
            Require(replacement.Hold.Held, "Worker replacement must not automatically clear the rejection.");
            var replacementMetrics = new RecorderMetrics(); replacement.WriteMetrics(replacementMetrics);
            Require(replacementMetrics.ReplayStorageSafetyHoldId == latch.Read().HoldId,
                "Worker replacement must preserve the notice's hold identity.");
            replacement.RequireAdmission(profile, profile);
            replacement.RequireResume(profile, profile);
            Require(replacement.Hold.Held, "Safe checks alone must retain the hold until explicit acquisition.");
            replacement.BeginCapture(Scope, profile, profile, () => { }, explicitResume: true);
            replacement.CompleteCapture(true);
            Require(!latch.Read().Held, "Known never-launched producer must permit explicit safe Resume after replacement.");
            replacement.WriteMetrics(replacementMetrics);
            Require(replacementMetrics.ReplayStorageSafetyHoldId == null, "Resolved hold identity leaked into later status.");
        });
        Check("preflight failure cannot upgrade an unknown previous producer", () =>
        {
            string profile = Path.Combine(root, "unknown-preflight"), security = Path.Combine(root, "unknown-preflight-security");
            Directory.CreateDirectory(profile);
            var latch = new ReplayStorageStopLatch(profile, security);
            latch.Stop("Unconfirmed previous producer.");
            using var coordinator = new ReplayStorageCoordinator(latch, new ReplayVolumeReservations(security),
                new CapacityFiles(9_000_000_000), Qualification, (_, _) => true);
            coordinator.StopForStorageFailure("Repeated preflight failure.");
            bool rejected = false;
            try { coordinator.RequireResume(profile, profile); } catch (IOException) { rejected = true; }
            Require(rejected && !latch.ProducerAbsenceConfirmed(), "A missing producer identity must never imply positive absence.");
        });
        Check("initial unmeasured capture rejects even with abundant capacity", () =>
        {
            string profile = Path.Combine(root, "unmeasured-profile"), security = Path.Combine(root, "unmeasured-security");
            Directory.CreateDirectory(profile);
            var files = new CapacityFiles(100_000_000_000);
            var latch = new ReplayStorageStopLatch(profile, security);
            using var coordinator = new ReplayStorageCoordinator(latch, new ReplayVolumeReservations(security), files,
                () => new(null, "Missing qualification."), (_, _) => false);
            bool stopped = false, admitted = false;
            try { coordinator.BeginCapture(Scope, profile, profile, () => stopped = true); admitted = true; } catch (IOException) { }
            Require(!admitted && stopped && coordinator.Stopped && latch.Read().Held, "Initial acquisition must fail closed above the floor too.");
            coordinator.CompleteCapture(true);
            Require(latch.Read().Held, "Capacity alone must not clear an unmeasured admission hold.");
        });
        Check("low floor rejects before producer and remains held after capacity returns", () =>
        {
            string profile = Path.Combine(root, "floor-profile"), security = Path.Combine(root, "floor-security");
            Directory.CreateDirectory(profile);
            var files = new CapacityFiles(2_000_000_000);
            var latch = new ReplayStorageStopLatch(profile, security);
            using var coordinator = new ReplayStorageCoordinator(latch, new ReplayVolumeReservations(security, 2_000_000_000), files,
                () => new(null, "Controlled missing model."), (_, _) => false);
            bool producer = false, stop = false;
            try { coordinator.BeginCapture(Scope, profile, profile, () => stop = true); producer = true; }
            catch (IOException) { }
            Require(!producer && stop && latch.Read().Held, "Low floor must stop before the producer callback.");
            coordinator.CompleteCapture(true);
            files.Bytes = 9_000_000_000;
            Require(latch.Read().Held, "Capacity return must leave the durable stop held.");
            try { coordinator.RequireResume(profile, profile); throw new Exception("Unmeasured Resume was accepted."); }
            catch (IOException) { }
            Require(latch.Read().Held, "Rejected Resume must preserve the hold.");
        });
        Check("qualified capture promise survives withdrawal until confirmed absence", () =>
        {
            string profile = Path.Combine(root, "qualified-profile"), security = Path.Combine(root, "qualified-security");
            Directory.CreateDirectory(profile);
            string scope = new('A', 64);
            var headroom = new ReplayCaptureHeadroom(scope, @"\\?\Volume{00000000-0000-0000-0000-000000000001}\", 4096, 4096, 8192);
            var model = new ReplayReferenceModel("controlled", scope, headroom.Volume, 4096, 0, 0, [], headroom);
            ReplayCalibrationAvailability available = new(model, null);
            var files = new CapacityFiles(9_000_000_000);
            var store = new ReplayVolumeReservations(security, 2_000_000_000);
            var latch = new ReplayStorageStopLatch(profile, security);
            using var coordinator = new ReplayStorageCoordinator(latch, store, files, () => available, (_, _) => true);
            bool stopped = false;
            coordinator.BeginCapture(scope, profile, profile, () => stopped = true);
            Require(!stopped && !coordinator.Hold.Held, "Safe qualified admission must permit acquisition.");
            Require(store.ObserveUnreserved(profile, files).Bytes == 8_999_987_712, "Capture must charge its 12288-byte poll plus stop promise.");
            coordinator.Poll();
            Require(!stopped, "Watcher must exclude its own promise before applying its qualified threshold.");
            available = new(null, "Controlled withdrawal."); coordinator.Poll();
            Require(stopped && latch.Read().Held, "Withdrawal must stop capture and retain its durable guard.");
            coordinator.CompleteCapture(false);
            Require(store.ObserveUnreserved(profile, files).Bytes == 8_999_987_712, "Unknown absence must retain capture growth.");
            coordinator.CompleteCapture(true);
            Require(store.ObserveUnreserved(profile, files).Bytes == 9_000_000_000, "Confirmed absence releases only capture growth.");
            Require(latch.Read().Held, "Completion after a storage stop must preserve the hold.");
            available = new(model, null);
            coordinator.RequireResume(profile, profile);
            coordinator.BeginCapture(scope, profile, profile, () => stopped = true, explicitResume: true);
            Require(!coordinator.Hold.Held && !coordinator.Stopped, "Explicit qualified Resume must arm a fresh guard.");
            coordinator.CompleteCapture(true);
            Require(!latch.Read().Held, "Normal confirmed closure of the resumed capture must clear only its owned guard.");
        });
        Check("combined export promise and own margin trigger exact stop boundary", () =>
        {
            string profile = Path.Combine(root, "combined-profile"), security = Path.Combine(root, "combined-security"); Directory.CreateDirectory(profile);
            var files = new CapacityFiles(3_000_000_000);
            var store = new ReplayVolumeReservations(security, 2_000_000_000);
            string scope = new('B', 64), owner = Guid.NewGuid().ToString("N");
            string volume = @"\\?\Volume{00000000-0000-0000-0000-000000000001}\";
            var headroom = new ReplayCaptureHeadroom(scope, volume, 4096, 4096, 8192);
            var model = new ReplayReferenceModel("controlled", scope, volume, 4096, 0, 0, [], headroom);
            store.Update(owner, [new(Path.Combine(profile, "export-growth"), profile, volume, 4096, 900_000_000, ReplayVolumeClaimKind.UnmaterializedGrowth)], files);
            var latch = new ReplayStorageStopLatch(profile, security);
            using var coordinator = new ReplayStorageCoordinator(latch, store, files, () => new(model, null), (_, _) => true);
            bool stopped = false; coordinator.BeginCapture(scope, profile, profile, () => stopped = true);
            files.Bytes = 2_900_012_289; coordinator.Poll();
            Require(!stopped, "One byte above other commitments plus 2GB plus own margin must keep acquisition.");
            files.Bytes = 2_900_012_288; coordinator.Poll();
            Require(stopped, "Exact stop boundary must stop with other profile growth still charged.");
            coordinator.CompleteCapture(true);
            Require(store.ObserveUnreserved(profile, files).Bytes == 2_000_012_288, "Capture closure must preserve the accepted export promise.");
        });
        Check("late observation stops and restart cannot infer producer absence", () =>
        {
            string profile = Path.Combine(root, "gap-profile"), security = Path.Combine(root, "gap-security"); Directory.CreateDirectory(profile);
            var latch = new ReplayStorageStopLatch(profile, security); var files = new CapacityFiles(9_000_000_000);
            using (var coordinator = new ReplayStorageCoordinator(latch, new ReplayVolumeReservations(security), files, Qualification, (_, _) => true))
            {
                bool stopped = false; coordinator.BeginCapture(Scope, profile, profile, () => stopped = true);
                Thread.Sleep(550); coordinator.Poll();
                Require(stopped && latch.Read().Held, "A late observation must stop acquisition.");
                coordinator.CompleteCapture(false);
            }
            string scope = new('C', 64), volume = @"\\?\Volume{00000000-0000-0000-0000-000000000001}\";
            var headroom = new ReplayCaptureHeadroom(scope, volume, 4096, 4096, 8192);
            var model = new ReplayReferenceModel("controlled", scope, volume, 4096, 0, 0, [], headroom);
            using var restarted = new ReplayStorageCoordinator(latch, new ReplayVolumeReservations(security), files, () => new(model, null), (_, _) => true);
            try { restarted.RequireResume(profile, profile); throw new Exception("Restart manufactured absence proof."); }
            catch (IOException ex) { Require(ex.Message.Contains("absence"), "Restart must report the absence proof gap."); }
        });
        Check("filesystem observation overhead belongs to freshness budget", () =>
        {
            string profile = Path.Combine(root, "overhead-profile"), security = Path.Combine(root, "overhead-security"); Directory.CreateDirectory(profile);
            var files = new CapacityFiles(9_000_000_000);
            var latch = new ReplayStorageStopLatch(profile, security);
            using var coordinator = new ReplayStorageCoordinator(latch, new ReplayVolumeReservations(security), files, Qualification, (_, _) => true);
            bool stopped = false; coordinator.BeginCapture(Scope, profile, profile, () => stopped = true);
            files.BeforeCapacity = () => Thread.Sleep(550);
            coordinator.Poll();
            Require(stopped && latch.Read().Held, "A slow filesystem sample must not refresh qualified freshness after its deadline.");
            coordinator.CompleteCapture(true);
        });
        Check("shared lock contention retries only while prior observation is fresh", () =>
        {
            string profile = Path.Combine(root, "busy-profile"), security = Path.Combine(root, "busy-security"); Directory.CreateDirectory(profile);
            var files = new CapacityFiles(9_000_000_000); var latch = new ReplayStorageStopLatch(profile, security);
            using var coordinator = new ReplayStorageCoordinator(latch, new ReplayVolumeReservations(security), files, Qualification, (_, _) => true);
            bool stopped = false; coordinator.BeginCapture(Scope, profile, profile, () => stopped = true);
            using (TrustedStorage.AcquireLock(security, "replay-volume-reservations"))
            {
                coordinator.Poll(); Require(!stopped, "Immediate Busy within the fresh interval may retry.");
                Thread.Sleep(550); coordinator.Poll();
                Require(stopped, "Busy cannot extend freshness indefinitely.");
            }
            coordinator.CompleteCapture(true);
        });
        Check("typed cache budget failure holds capture until explicit action", () =>
        {
            string profile = Path.Combine(root, "budget-profile"), security = Path.Combine(root, "budget-security"); Directory.CreateDirectory(profile);
            string retained = Path.Combine(profile, "retained.ts"); File.WriteAllBytes(retained, new byte[8192]);
            var latch = new ReplayStorageStopLatch(profile, security); var files = new CapacityFiles(9_000_000_000);
            using var coordinator = new ReplayStorageCoordinator(latch, new ReplayVolumeReservations(security), files, Qualification, (_, _) => true);
            bool stopped = false; coordinator.BeginCapture(Scope, profile, profile, () => stopped = true);
            using (var cache = new ReplayCache(cacheDirectory: profile, cacheBudgetBytes: 1))
            {
                try { cache.ValidateBudget(1); throw new Exception("Impossible cache budget passed."); }
                catch (ReplayCacheBudgetExceededException ex) { coordinator.StopForStorageFailure(ex.Message); }
                Require(stopped && latch.Read().Held, "Budget exception must use storage stop fence instead of recovery.");
                coordinator.CompleteCapture(true);
                cache.ValidateBudget(1_000_000);
                Require(latch.Read().Held, "Raising the budget alone cannot automatically clear the hold.");
            }
            Require(new ReplayStorageStopLatch(profile, security).Read().Held && File.ReadAllBytes(retained).SequenceEqual(new byte[8192]), "Reopen must preserve the budget hold and retained footage.");
        });
        Check("storage stop during source drain preserves footage before retirement cleanup", () =>
        {
            foreach (bool stopDuringDrain in new[] { true, false })
            {
                string fixture = Path.Combine(root, stopDuringDrain ? "late-stop" : "normal-stop"), profile = Path.Combine(fixture, "profile"), security = Path.Combine(fixture, "security");
                Directory.CreateDirectory(profile);
                string session = Path.Combine(profile, "session-fixture"); Directory.CreateDirectory(session);
                string footage = Path.Combine(session, "retained.ts"); File.WriteAllText(footage, "tiny retained footage sentinel; not playable media");
                var source = new DrainSource(session);
                var files = new CapacityFiles(9_000_000_000);
                var latch = new ReplayStorageStopLatch(profile, security);
                using var coordinator = new ReplayStorageCoordinator(latch, new ReplayVolumeReservations(security), files, Qualification, (_, _) => true);
                using var cache = new ReplayCache(cacheDirectory: profile);
                var id = cache.RegisterSession(source);
                bool requested = false;
                coordinator.BeginCapture(Scope, session, profile, () => requested = true);
                coordinator.StartSupervision();
                var stopping = Task.Run(() => coordinator.StopAndPreserve(cache, id, () => source.Stopped, () => false));
                try
                {
                    Require(source.Entered.Wait(TimeSpan.FromSeconds(3)), "Source did not enter the controlled drain.");
                    if (stopDuringDrain) { files.Bytes = 2_000_000_000; coordinator.Poll(); Require(requested, "Low storage must arrive while source drain is blocked."); }
                }
                finally { source.Release.Set(); }
                stopping.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                cache.RetireSessionAsync(id).GetAwaiter().GetResult();
                Require(File.Exists(footage) == stopDuringDrain, "Late stop must preserve footage; the normal-stop control must still clean it up.");
                Require(latch.Read().Held == stopDuringDrain, "Only the storage stop retains its hold after confirmed drain.");
            }
        });
        File.WriteAllText(Path.Combine(root, "storage-runtime-checks.json"), JsonSerializer.Serialize(new { failures, checks }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Storage runtime checks: {checks.Count - failures}/{checks.Count} passed");
        return failures == 0 ? 0 : 1;
    }
    internal static int RunPrelaunch(string root, string engineRoot)
    {
        if (Directory.Exists(root)) throw new IOException("Use a fresh prelaunch evidence directory.");
        string executable = Path.Combine(engineRoot, "ffmpeg-" + MediaDependency.PackageVersion, "bin", "ffmpeg.exe");
        string Hash(string path) { using var input = File.OpenRead(path); return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input)); }
        Require(File.Exists(executable) && Hash(executable).Equals(MediaDependency.ExecutableSha256, StringComparison.OrdinalIgnoreCase), "Existing approved private engine must match before the read-only tracer.");
        Directory.CreateDirectory(root);
        string previous = MediaDependency.RootDirectory;
        var results = new List<object>(); int failures = 0;
        MediaDependency.RootDirectory = engineRoot;
        try
        {
            foreach (bool lowFloor in new[] { true, false })
            {
                string folder = Path.Combine(root, lowFloor ? "low-floor" : "safe-callback"), profile = Path.Combine(folder, "profile"), security = Path.Combine(folder, "security");
                Directory.CreateDirectory(profile);
                var config = new Settings { SystemAudio = false, OutputDirectory = Path.Combine(profile, "output"), ScreenshotDirectory = Path.Combine(profile, "pictures") };
                var files = new CapacityFiles(lowFloor ? 2_000_000_000 : 9_000_000_000);
                var latch = new ReplayStorageStopLatch(profile, security);
                using var coordinator = new ReplayStorageCoordinator(latch, new ReplayVolumeReservations(security), files, Qualification, (_, _) => true);
                CaptureSession observed = null; bool reached = false, requestedStop = false, rejected = false;
                try
                {
                    using var capture = new CaptureSession(config, profile, true, null, null, beforeLaunch: source =>
                    {
                        observed = source; reached = true;
                        Require(source.ProcessId == 0 && source.CaptureProducerAbsent && source.CaptureArguments?.Length > 0, "Prelaunch callback requires prepared metadata and no owned producer.");
                        coordinator.BeginCapture(Scope, source.DirectoryPath, config.OutputDirectory, () => requestedStop = true);
                        throw new IOException("Controlled callback cancellation before StartProcess.");
                    });
                }
                catch (IOException) { rejected = true; }
                bool absent = observed?.CaptureProducerAbsent == true;
                coordinator.CompleteCapture(absent);
                bool passed = reached && rejected && absent && observed.ProcessId == 0 && requestedStop == lowFloor && latch.Read().Held == lowFloor;
                if (!passed) failures++;
                results.Add(new { name = lowFloor ? "actual-constructor-low-floor-rejects-before-launch" : "actual-constructor-safe-callback-cancellation-closes-guard", passed, reached, rejected, absent, requestedStop });
            }
            var export = ReplayExportProcess.Run(executable, ["-version"], root, null,
                checkStorage: () => throw new IOException("Controlled export storage rejection before process creation."));
            bool exportPassed = export.ProcessId == null && export.ProducerAbsent && export.Failure == "launch-failed"
                && export.Error.Contains("Controlled export storage rejection");
            if (!exportPassed) failures++;
            results.Add(new { name = "actual-export-storage-check-rejects-before-launch", passed = exportPassed });
            CaptureSession prepared = null; bool stereoRejected = false;
            try
            {
                var settings = new Settings { ReplayMinutes = 5, FramesPerSecond = 60, BitrateMbps = 20, SystemAudio = true,
                    CaptureMode = "monitor", DisplayIndex = -1, OutputDirectory = root };
                using var capture = new CaptureSession(settings, Path.Combine(root, "stereo-prelaunch"), false, null, null,
                    beforeLaunch: source =>
                    {
                        prepared = source;
                        int layout = Array.IndexOf(source.CaptureArguments, "-ch_layout");
                        int input = Array.IndexOf(source.CaptureArguments, "-i");
                        Require(source.ProcessId == 0 && source.CaptureProducerAbsent && source.ReferenceScope != null
                            && layout >= 0 && source.CaptureArguments[layout + 1] == "stereo" && layout < input,
                            "Actual stereo reference arguments must declare the known input layout before any acquisition.");
                        throw new OperationCanceledException("Controlled stereo argument check before StartProcess.");
                    });
            }
            catch (OperationCanceledException) { stereoRejected = true; }
            bool stereoPassed = stereoRejected && prepared?.CaptureProducerAbsent == true && prepared.ProcessId == 0;
            if (!stereoPassed) failures++;
            results.Add(new { name = "actual-stereo-reference-layout-declared-before-launch", passed = stereoPassed });
            Require(Hash(executable).Equals(MediaDependency.ExecutableSha256, StringComparison.OrdinalIgnoreCase), "Read-only engine changed during callback checks.");
        }
        catch (Exception ex) { failures++; results.Add(new { name = "prelaunch-tracer-error", passed = false, error = ex.ToString() }); }
        finally { MediaDependency.RootDirectory = previous; }
        File.WriteAllText(Path.Combine(root, "storage-prelaunch-checks.json"), JsonSerializer.Serialize(new { failures, engine = executable, engineSha256 = Hash(executable), results,
            effects = "Read-only trusted engine resolution/hash and task-owned native process-job handles; actual CaptureSession constructor callbacks reject before StartProcess. No process launch, probe, capture, audio, GPU, UI, startup write or installation." }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Actual capture prelaunch checks: {results.Count - failures}/{results.Count} passed");
        return failures == 0 ? 0 : 1;
    }
    static void Require(bool value, string message) { if (!value) throw new IOException(message); }
}

