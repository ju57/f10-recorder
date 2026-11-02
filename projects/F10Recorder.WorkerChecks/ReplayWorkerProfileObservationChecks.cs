using System.Diagnostics;
using System.Text.Json;

namespace F10Recorder;

internal static partial class ReplayCalibrationRun
{
    static object WorkerLatchObservationChecks(string root, ReplayReferenceModel model)
    {
        Require(!Directory.Exists(root), "Fresh worker-latch control required.");
        string profile = Path.Combine(root, "profile"), authority = Path.Combine(root, "authority"), cache = Path.Combine(root, "cache");
        Directory.CreateDirectory(profile); Directory.CreateDirectory(cache);
        var latch = new ReplayStorageStopLatch(profile, authority);
        using var coordinator = new ReplayStorageCoordinator(latch, new ReplayVolumeReservations(authority),
            catalog: () => new(model, null), applicable: (_, _) => true);
        coordinator.BeginCapture(model.Scope, cache, cache, () => throw new IOException("No media producer exists in this control."));
        // The same private authority also owns a volume-reservation lock. Select
        // the actual guard document rather than assuming there is one lock.
        var guardPath = Directory.GetFiles(Path.Combine(authority, "profiles"), "*.json").Single(path =>
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("Mode", out var mode) && mode.GetString() == "guard";
        });
        var lockPath = Path.ChangeExtension(guardPath, ".lock");
        ReplayStorageHold competingRead;
        using (var competingLock = new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            competingRead = new ReplayStorageStopLatch(profile, authority).Read();
            Require(competingRead.Held && !competingRead.Durable && competingRead.HoldId == null,
                "Independent active latch read did not reproduce the exclusive-lock failure.");
            for (int i = 0; i < 100; i++)
            {
                var metrics = new RecorderMetrics(); coordinator.WriteMetrics(metrics);
                RequireWorkerHealth(new WorkerStatus { Metrics = metrics }, 1);
            }
        }
        bool rejected = false;
        try { RequireWorkerHealth(new WorkerStatus { Metrics = new RecorderMetrics { ReplayStorageSafetyHeld = true } }, 1); }
        catch (IOException) { rejected = true; }
        Require(rejected, "Protocol health must still reject a real safety hold.");
        coordinator.CompleteCapture(true);
        Require(!coordinator.CapturePending && !coordinator.Stopped, "Control capture claim did not retire normally.");
        coordinator.Dispose();
        Require(!new ReplayStorageStopLatch(profile, authority).Read().Held, "Post-retirement durable guard must be clear.");
        return new { result = "passed", competingRead, protocolObservations = 100, heldStatusRejected = rejected,
            durableGuardClearAfterRetirement = true, effects = "Task-private tiny guard/ledger files; no worker, capture, media, UI or account state." };
    }
    static object WorkerProfileObservationChecks(string root)
    {
        Require(!Directory.Exists(root), "Fresh worker-profile observation control required.");
        string current = Path.Combine(root, "observer"), profile = Path.Combine(current, "H03", "worker-profile");
        string history = Path.Combine(root, "history"); Directory.CreateDirectory(profile); Directory.CreateDirectory(history);
        var settings = new Settings { OutputDirectory = Path.Combine(root, "unused-media"),
            ScreenshotDirectory = Path.Combine(root, "unused-pictures"), ReplayCacheDirectory = Path.Combine(root, "unused-cache") };
        RecordingProfile.Load(profile).Commit(settings, false, false, false);
        string status = Path.Combine(profile, "worker-status.json"), pending = status + ".tmp";
        File.WriteAllText(status, "{\"generation\":-1}");
        File.WriteAllText(pending, "{\"generation\":0}"); bool legacyRejected = false;
        using (var lease = ReplayStorage.OpenInventory(status))
            try { File.Move(pending, status, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { legacyRejected = true; }
        Require(legacyRejected, "Held leaf metadata did not reproduce the atomic status replacement conflict.");
        File.Move(pending, status, true);
        var first = ReplayStorage.Observe(status);
        var plan = new CalibrationPlan(current, "unused", "worker-profile-atomic-observation", new(), Order, [history], 0, 0,
            BudgetBytes: 10_000_000_000, StopBytes: 9_000_000_000, FloorBytes: 0, DeadlineSeconds: 20, RetainedOfflineSeconds: 0,
            MetadataHandleRoots: [history], ReuseCurrentMetadataHandles: true, RetainedBudgetBytes: 1_000_000,
            FileLimit: 256, DirectoryLimit: 64, OfflineDeadlineSeconds: 3, OutsideBuildAllowanceBytes: 0, FreezeRetainedAllocations: true);
        int writes = 0, observed;
        using (var sampler = new Sampler(plan, active: false))
        {
            sampler.BeginActive(); int before = sampler.Observations; var clock = Stopwatch.StartNew();
            while (writes < 20 || sampler.Observations < before + 3)
            {
                Require(writes < 200 && clock.Elapsed.TotalSeconds < 5, "Atomic profile writes did not overlap actual sampler observations.");
                writes++;
                File.WriteAllText(pending, JsonSerializer.Serialize(new { generation = writes, text = new string('x', 20 + writes) }));
                File.Move(pending, status, true);
                if (writes % 5 == 0)
                {
                    settings.BitrateMbps = writes % 2 == 0 ? 20 : 21;
                    RecordingProfile.Load(profile).Commit(settings, false, false, false);
                }
                RecordingProfile.Load(profile).PersistState(writes % 2 == 0, false);
                sampler.Check(); Thread.Sleep(5);
            }
            observed = sampler.WorkerControlObservations;
            Require(observed > 0 && new[] { status, Path.Combine(profile, "settings.json"), Path.Combine(profile, "worker-state.json") }
                .All(path => !sampler.RetainsMetadata(path)), "Sampler opened/retained atomic worker control leaves.");
            sampler.EndActive(); sampler.StopAndCheck();
        }
        using var latest = JsonDocument.Parse(File.ReadAllText(status));
        var state = RecordingProfile.Load(profile);
        Require(latest.RootElement.GetProperty("generation").GetInt32() == writes && state.Status.CanWrite
            && state.State.Paused == (writes % 2 == 0) && !state.State.UserStopped
            && !Directory.GetFiles(profile, "*.tmp").Any() && !File.Exists(Path.Combine(profile, "profile-migration.json")),
            "Concurrent observation left stale/interrupted profile or status state.");
        var final = ReplayStorage.Observe(status);
        Require(first.Identity != final.Identity && final.Length > first.Length, "Atomic controls did not replace actual file identities and lengths.");
        return new { result = "passed", legacyRejected, writes, observed, profileCommitAndPersistState = true,
            noPendingOrJournal = true, actualStatusIdentityChanged = true,
            effects = "Tiny new task-owned profile/status files only; real atomic replacement and sampler. No worker/engine/capture/media children/UI/account state." };
    }
}
