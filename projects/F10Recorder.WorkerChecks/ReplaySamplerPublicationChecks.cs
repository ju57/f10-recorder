using System.Diagnostics;
using System.Text.Json;

namespace F10Recorder;

// The actual sampler must coexist with production atomic ledger/guard writers.
// Tiny private fixtures, native capacity and controlled headroom; no producer.
internal static class ReplaySamplerPublicationChecks
{
    internal static int Run(string root)
    {
        using var owned = PilotStorageObservation.Create(root);
        string profile = Path.Combine(root, "profile"), cache = Path.Combine(root, "buffer"), authority = Path.Combine(root, "calibration-ownership");
        Directory.CreateDirectory(profile); Directory.CreateDirectory(cache);
        var capacity = ReplayStorage.ObserveCapacity(root);
        string scope = new('C', 64), owner = Guid.NewGuid().ToString("N");
        var headroom = new ReplayCaptureHeadroom(scope, capacity.Volume, capacity.AllocationUnit, 4096, 8192);
        var model = new ReplayReferenceModel("controlled-publication", scope, capacity.Volume, capacity.AllocationUnit, 0, 0, [], headroom);
        var files = new ReplayFiles();
        var claims = new ReplayVolumeReservations(authority, ReplayStorageSafety.MinimumBytes);
        var latch = new ReplayStorageStopLatch(profile, authority);
        using var coordinator = new ReplayStorageCoordinator(latch, claims, files, () => new(model, null), (_, _) => true);
        var plan = new CalibrationPlan(root, "unused-no-engine", "controlled-atomic-publication", new(), [], [], 0, 0,
            BudgetBytes: 3_000_000_000, StopBytes: 2_500_000_000, FloorBytes: 0, DeadlineSeconds: 30,
            ReuseCurrentMetadataHandles: true);
        ReplayCalibrationRun.Sampler sampler = null;
        Exception failure = null; bool stopped = false; int updates = 0;
        bool ledgerRetained = false, guardRetained = false;
        var timer = Stopwatch.StartNew();
        try
        {
            sampler = new(plan, active: false);
            coordinator.BeginCapture(scope, cache, root, () => stopped = true);
            coordinator.StartSupervision(); sampler.BeginActive();
            while (Volatile.Read(ref sampler.Observations) < 4)
            { sampler.Check(); if (timer.Elapsed.TotalSeconds > 5) throw new IOException("Sampler readiness deadline."); Thread.Sleep(20); }
            ledgerRetained = sampler.RetainsMetadata(Path.Combine(authority, "replay-volume-reservations.json"));
            foreach (string guard in Directory.GetFiles(Path.Combine(authority, "profiles"), "*.json"))
                guardRetained |= sampler.RetainsMetadata(guard);
            for (int n = 0; n < 40; n++)
            {
                sampler.Check();
                claims.Update(owner, [new(Path.Combine(root, "export-growth"), root, capacity.Volume, capacity.AllocationUnit,
                    4096 + n, ReplayVolumeClaimKind.UnmaterializedGrowth)], files);
                updates++;
                if (stopped || coordinator.Stopped) throw new IOException("Production coordinator stopped during observer publication control.");
                Thread.Sleep(30);
            }
            claims.Release(owner);
            coordinator.CompleteCapture(true);
            if (coordinator.CapturePending || coordinator.Stopped || latch.Read().Held)
                throw new IOException("Atomic capture guard/claim closure failed while sampler was active.");
            if (ledgerRetained || guardRetained) throw new IOException("Mutable atomic claim metadata retained a conflicting leaf handle.");
            sampler.EndActive(); sampler.StopAndCheck();
        }
        catch (Exception error) { failure = error; }
        finally
        {
            sampler?.Dispose();
            // No child exists. Release only this fixture's scalar owner and
            // retry its confirmed closure after observer handles have closed.
            try { claims.Release(owner); coordinator.CompleteCapture(true); }
            catch (Exception error) { failure ??= error; }
        }
        File.WriteAllText(Path.Combine(root, "publication-check.json"), JsonSerializer.Serialize(new {
            result = failure == null ? "passed" : "failed", error = failure?.ToString(), updates, stopped,
            ledgerRetained, guardRetained, maximumActiveGapMs = sampler?.MaximumActiveGap,
            elapsedSeconds = timer.Elapsed.TotalSeconds,
            effects = "Actual native sampler plus production coordinator/ledger/guard writers with controlled headroom and tiny files. No child, engine, capture, audio, GPU, visible UI, startup or account catalog write." }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Sampler/production atomic publication: " + (failure == null ? "passed" : "failed"));
        return failure == null ? 0 : 1;
    }
}
