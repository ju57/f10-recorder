using System.Diagnostics;

namespace F10Recorder;

// Owns only capture's future growth. Accepted exports retain their independent
// reservations and source lifetime in ReplayCache. No unmeasured allowance is
// substituted for missing empirical capture evidence.
internal sealed class ReplayStorageCoordinator : IDisposable
{
    internal const int MaximumObservationGapMilliseconds = 500;
    readonly ReplayStorageStopLatch latch;
    readonly ReplayVolumeReservations reservations;
    readonly ReplayFiles files;
    readonly Func<ReplayCalibrationAvailability> catalog;
    readonly Func<ReplayReferenceModel, string, bool> applicable;
    readonly object gate = new();
    readonly ManualResetEventSlim finish = new(false);
    ReplayStorageCaptureGuard guard;
    ReplayReferenceModel armedModel;
    ReplayProducerProof producer;
    string scope, directory, destination;
    Action requestStop;
    Thread watcher;
    long lastFresh;
    bool claimHeld, completing, producerAbsenceConfirmed;
    volatile bool storageStopped;
    volatile ReplayStorageAssessment assessment;
    volatile ReplayStorageHold failedHold;
    internal event Action<string> Warning;
    internal bool Stopped => storageStopped;
    internal bool CapturePending { get { lock (gate) return guard != null || completing; } }
    internal ReplayStorageHold Hold
    {
        get
        {
            if (storageStopped) return failedHold ?? new(true, assessment?.Reason ?? "Storage safety stopped capture.", false, guard?.HoldId);
            if (guard != null) return new(false, null, true);
            return latch.Read();
        }
    }
    internal ReplayStorageCoordinator(ReplayStorageStopLatch latch, ReplayVolumeReservations reservations,
        ReplayFiles files = null, Func<ReplayCalibrationAvailability> catalog = null,
        Func<ReplayReferenceModel, string, bool> applicable = null)
    {
        this.latch = latch; this.reservations = reservations; this.files = files ?? new ReplayFiles();
        producerAbsenceConfirmed = !latch.Read().Held;
        this.catalog = catalog ?? ReplayCalibrationCatalog.Load;
        this.applicable = applicable ?? ((model, path) => ReplayCalibrationCatalog.IsApplicableToDestination(model, path));
    }
    ReplayCaptureHeadroom Headroom(ReplayReferenceModel model, string actualScope, string cache, string output)
    {
        if (model?.CaptureHeadroom == null || actualScope != model.Scope
            || !applicable(model, cache) || !applicable(model, output)) return null;
        return model.CaptureHeadroom;
    }
    ReplayStorageAssessment Observe(string cache, ReplayCaptureHeadroom headroom, string excludeOwner = null)
    {
        var capacity = reservations.ObserveUnreserved(cache, files, excludeOwner);
        return ReplayStorageSafety.Assess(new(capacity.Volume, capacity.AllocationUnit, capacity.Bytes), headroom);
    }
    // This nonmedia preflight deliberately precedes Commit, dependency discovery,
    // audio metadata and probing. Actual scope is checked again before acquisition.
    internal void RequireAdmission(string cache, string output, Settings settings = null)
    {
        var model = catalog().Model;
        if (model?.CaptureHeadroom == null || settings != null && !ReplayReferenceModel.ReferenceSettings(settings))
            throw new IOException("Capture requires qualified capture headroom; this recording profile is unmeasured.");
        if (!applicable(model, cache) || !applicable(model, output))
            throw new IOException("Capture requires a qualified model matching the current cache and output volumes.");
        var result = Observe(cache, model.CaptureHeadroom);
        if (!result.SafeToResume) throw new IOException(result.Reason);
    }
    internal void RequireResume(string cache, string output)
    {
        var model = catalog().Model;
        if (model?.CaptureHeadroom == null)
            throw new IOException("Resume requires qualified capture headroom; this recording profile is unmeasured.");
        if (!producerAbsenceConfirmed && !CapturePending && latch.ProducerAbsenceConfirmed())
        {
            var held = latch.Read();
            if (held.HoldId == null) throw new IOException("Recovered capture hold identity is unavailable.");
            reservations.Release(held.HoldId);
            producerAbsenceConfirmed = true;
        }
        if (!producerAbsenceConfirmed)
            throw new IOException("Resume requires confirmed absence of the previous capture producer; restart alone does not establish that proof.");
        if (CapturePending) CompleteCapture(true); // Retry only an already confirmed closure.
        if (CapturePending) throw new IOException("Capture growth closure remains unconfirmed; its claim and hold are retained.");
        if (!applicable(model, cache) || !applicable(model, output))
            throw new IOException("Resume requires a qualified model matching the current cache and output volumes.");
        var result = Observe(cache, model.CaptureHeadroom);
        if (!result.SafeToResume) throw new IOException(result.Reason);
    }
    internal void BeginCapture(string actualScope, string cache, string output, Action stop, bool explicitResume = false, ReplayProducerProof producerProof = null)
    {
        lock (gate)
        {
            if (guard != null || completing) throw new IOException("Wait for the previous capture and accepted replay save to stop.");
            scope = actualScope; directory = cache; destination = output; requestStop = stop;
            var qualification = catalog();
            var headroom = Headroom(qualification.Model, scope, directory, destination);
            var held = latch.Read();
            ReplayStorageCaptureGuardResult armed;
            if (held.Held)
            {
                if (!explicitResume) throw new IOException(held.Reason);
                if (!producerAbsenceConfirmed) throw new IOException("Previous capture producer absence remains unconfirmed.");
                var resume = Observe(directory, headroom);
                if (!resume.SafeToResume) throw new IOException("Resume rejected: " + resume.Reason);
                armed = latch.BeginCaptureGuardAfterSafeResume(resume);
            }
            else armed = latch.BeginCaptureGuard();
            storageStopped = false; failedHold = null;
            guard = armed.Guard;
            if (guard == null) { storageStopped = true; failedHold = armed.Hold; throw new IOException(armed.Hold.Reason); }
            producerAbsenceConfirmed = false;
            try
            {
                producer = producerProof;
                if (producer != null) latch.RecordProducer(guard.HoldId, producer);
                assessment = Observe(directory, headroom);
                if (assessment.ShouldStop) throw new IOException(assessment.Reason);
                armedModel = headroom == null ? null : qualification.Model;
                if (headroom != null)
                {
                    reservations.Update(guard.HoldId, [new(Path.Combine(directory, "capture-growth-" + guard.HoldId), directory,
                        headroom.Volume, headroom.AllocationUnit, checked(headroom.PollGrowthBytes + headroom.StopGrowthBytes), ReplayVolumeClaimKind.UnmaterializedGrowth)], files, producer);
                    claimHeld = true;
                    assessment = Observe(directory, headroom, guard.HoldId);
                    if (assessment.ShouldStop) throw new IOException(assessment.Reason);
                }
                lastFresh = Stopwatch.GetTimestamp();
            }
            catch (Exception ex) { Stop(ex.Message); throw new IOException("Capture storage admission failed: " + ex.Message, ex); }
        }
    }
    internal void SealProducer(ReplayProducerProof proof)
    {
        lock (gate)
        {
            if (guard == null || storageStopped || !ReplayProducerProof.Valid(proof) || !proof.Sealed
                || producer == null || proof != (producer with { Sealed = true }))
                throw new IOException("Capture producer launch evidence changed or storage stopped during launch.");
            latch.RecordProducer(guard.HoldId, proof);
            if (claimHeld) reservations.SetProducer(guard.HoldId, proof);
            producer = proof;
        }
    }
    internal void StartSupervision()
    {
        lock (gate)
        {
            if (guard == null || storageStopped || watcher != null) return;
            finish.Reset();
            watcher = new Thread(() =>
            {
                while (!finish.IsSet && !storageStopped)
                {
                    long started = Stopwatch.GetTimestamp();
                    bool observed = Poll();
                    // A busy ledger supplied no fresh observation. Retry soon
                    // within the SAME500ms budget instead of spending another
                    // full200ms interval between unsuccessful acquisitions.
                    int remaining = observed ? Math.Max(0, 200 - (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds) : 5;
                    if (finish.Wait(remaining)) break;
                }
            }) { IsBackground = true, Name = "Capture storage safety" };
            watcher.Start();
        }
    }
    internal bool Poll()
    {
        lock (gate)
        {
            if (guard == null || storageStopped || completing) return true;
            try
            {
                if (Stopwatch.GetElapsedTime(lastFresh).TotalMilliseconds > MaximumObservationGapMilliseconds)
                    throw new IOException("Capture storage observation exceeded its qualified 500 ms interval.");
                var current = catalog();
                if (armedModel != null && (current.Model?.Revision != armedModel.Revision
                    || Headroom(current.Model, scope, directory, destination) != armedModel.CaptureHeadroom))
                    throw new IOException("Qualified capture headroom changed or was withdrawn during capture.");
                var held = latch.Read();
                if (!held.Held || held.HoldId != guard.HoldId)
                    throw new IOException("The durable capture storage guard changed or became unavailable.");
                var next = Observe(directory, armedModel?.CaptureHeadroom, claimHeld ? guard.HoldId : null);
                if (Stopwatch.GetElapsedTime(lastFresh).TotalMilliseconds > MaximumObservationGapMilliseconds)
                    throw new IOException("Capture storage observation including filesystem overhead exceeded 500 ms.");
                lastFresh = Stopwatch.GetTimestamp();
                bool notify = next.ShouldWarn && assessment?.ShouldWarn != true;
                assessment = next;
                if (next.ShouldStop) Stop(next.Reason);
                else if (notify) Warning?.Invoke(next.Reason);
            }
            catch (ReplayVolumeReservationBusyException) when (Stopwatch.GetElapsedTime(lastFresh).TotalMilliseconds < MaximumObservationGapMilliseconds)
            { return false; /* Do not refresh lastFresh on contention. */ }
            catch (Exception ex) { Stop(ex.Message); }
            return true;
        }
    }
    internal void StopForStorageFailure(string reason) { lock (gate) Stop(reason); }
    void Stop(string reason)
    {
        if (storageStopped) return;
        storageStopped = true; // In-memory fence and stop request precede cause I/O.
        reason = ReplayOutputDemand.Bounded(reason?.Replace('\r', ' ').Replace('\n', ' '), 512) ?? "Capture storage observation failed.";
        failedHold = new(true, reason, false, guard?.HoldId);
        try { requestStop?.Invoke(); }
        finally { failedHold = guard == null ? latch.Stop(reason) : guard.Stop(reason); }
    }
    internal void CompleteCapture(bool producerAbsent)
    {
        Thread running;
        lock (gate) { completing = true; finish.Set(); running = watcher; }
        if (running != null && running != Thread.CurrentThread) running.Join();
        lock (gate)
        {
            watcher = null;
            try
            {
                if (producerAbsent) producerAbsenceConfirmed = true;
                if (guard == null) return;
                if (!producerAbsent) { Stop("Capture producer absence is unconfirmed; storage hold and growth are retained."); return; }
                latch.RecordProducer(guard.HoldId, producer, absent: true);
                if (claimHeld) { reservations.Release(guard.HoldId); claimHeld = false; }
                if (!storageStopped)
                {
                    var result = guard.CompleteNormally(true);
                    if (result.Held) { storageStopped = true; failedHold = result; }
                }
                guard = null; armedModel = null;
            }
            catch (Exception ex) { Stop("Capture storage closure failed: " + ex.Message); }
            finally { completing = false; }
        }
    }
    // Quiesce the only concurrent storage-stop writer before the final retention
    // decision. ReplayCache still owns accepted export waits and final cleanup.
    internal void StopAndPreserve(ReplayCache replays, ReplaySessionId id, Func<bool> producerAbsent, Func<bool> preserve)
    {
        try { replays.StopAcquisitionAsync(id).GetAwaiter().GetResult(); }
        finally
        {
            bool absent = false;
            try { absent = producerAbsent(); }
            finally
            {
                bool completed = false;
                try { CompleteCapture(absent); completed = true; }
                finally
                {
                    if (!completed || !absent || Stopped || Hold.Held || preserve()) replays.PreserveSession(id);
                }
            }
        }
    }
    internal void WriteMetrics(RecorderMetrics metrics)
    {
        var held = Hold; var value = assessment;
        metrics.ReplayStorageSafetyKind = held.Held ? "Stop" : value?.Kind.ToString() ?? "Unmeasured";
        metrics.ReplayStorageSafetyHeld = held.Held;
        metrics.ReplayStorageSafetyWarning = value?.ShouldWarn == true;
        metrics.ReplayStorageSafetyReason = ReplayOutputDemand.Bounded(held.Held ? held.Reason : value?.Reason ?? "Capture poll and stop growth have not been qualified for this recording profile.", 512);
    }
    public void Dispose() { finish.Set(); watcher?.Join(); /* No absence is inferred from disposal. */ }
}
