using System.Diagnostics;

namespace F10Recorder;

// Owns only capture's future growth. Accepted exports retain their independent
// reservations and source lifetime in ReplayCache. No unmeasured allowance is
// substituted for missing empirical capture evidence.
internal sealed class ReplayStorageCoordinator : IDisposable
{
    internal const int MaximumObservationGapMilliseconds = ReplayCapacityPolicy.CoverageMilliseconds;
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
    ReplayCapacityObservation<ReplayStorageAssessment> observation;
    long lastFresh;
    bool claimHeld, completing, producerAbsenceConfirmed;
    bool sealing;
    internal Action BeforeClosureForChecks;
    volatile bool storageStopped;
    volatile ReplayStorageAssessment assessment;
    volatile ReplayStorageHold failedHold;
    internal event Action<string> Warning;
    internal bool Stopped => storageStopped;
    internal ReplayVolumeReservations CreateExportReservations() => reservations.CreatePeer();
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
        this.catalog = catalog;
        this.applicable = applicable ?? ((model, path) => ReplayCalibrationCatalog.IsApplicableToDestination(model, path));
    }
    ReplayCalibrationAvailability Qualification(int replayMinutes) => catalog?.Invoke() ?? ReplayCalibrationCatalog.Load(replayMinutes);
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
        int minutes = settings?.ReplayMinutes ?? 5;
        var model = Qualification(minutes).Model;
        if (model?.CaptureHeadroom == null || model.ReplayMinutes != minutes
            || settings != null && !ReplayReferenceModel.ReferenceSettings(settings))
            throw new IOException("Capture requires qualified capture headroom; this recording profile is unmeasured.");
        if (!applicable(model, cache) || !applicable(model, output))
            throw new IOException("Capture requires a qualified model matching the current cache and output volumes.");
        var result = Observe(cache, model.CaptureHeadroom);
        if (!result.SafeToResume) throw new IOException(result.Reason);
    }
    internal void RequireResume(string cache, string output, int replayMinutes = 5)
    {
        var model = Qualification(replayMinutes).Model;
        if (model?.CaptureHeadroom == null || model.ReplayMinutes != replayMinutes)
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
    internal void BeginCapture(string actualScope, string cache, string output, Action stop, bool explicitResume = false, ReplayProducerProof producerProof = null,
        int replayMinutes = 5)
    {
        lock (gate)
        {
            if (guard != null || completing || observation != null) throw new IOException("Wait for the previous capture, capacity observer and accepted replay save to stop.");
            scope = actualScope; directory = cache; destination = output; requestStop = stop;
            var qualification = Qualification(replayMinutes);
            if (qualification.Model != null && qualification.Model.ReplayMinutes != replayMinutes)
                throw new IOException("Capture calibration belongs to another replay duration.");
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
                // Bind every asynchronous result to this exact capture. All
                // metadata I/O, including catalog and durable-guard reads, runs
                // outside the lock used to request a stop.
                var captureModel = armedModel;
                string captureScope = scope, cachePath = directory, outputPath = destination, holdId = guard.HoldId;
                string excluded = claimHeld ? holdId : null;
                observation = new(() =>
                {
                    var current = Qualification(replayMinutes);
                    if (captureModel != null && (current.Model?.Revision != captureModel.Revision
                        || Headroom(current.Model, captureScope, cachePath, outputPath) != captureModel.CaptureHeadroom))
                        throw new IOException("Qualified capture headroom changed or was withdrawn during capture.");
                    var currentHold = latch.Read();
                    if (!currentHold.Held || currentHold.HoldId != holdId)
                        throw new IOException("The durable capture storage guard changed or became unavailable.");
                    return Observe(cachePath, captureModel?.CaptureHeadroom, excluded);
                });
            }
            catch (Exception ex) { Stop(ex.Message); throw new IOException("Capture storage admission failed: " + ex.Message, ex); }
        }
    }
    internal void SealProducer(ReplayProducerProof proof)
    {
        ReplayStorageCaptureGuard owner;
        bool heldClaim;
        lock (gate)
        {
            if (guard == null || storageStopped || completing || sealing || !ReplayProducerProof.Valid(proof) || !proof.Sealed
                || producer == null || proof != (producer with { Sealed = true }))
                throw new IOException("Capture producer launch evidence changed or storage stopped during launch.");
            owner = guard; heldClaim = claimHeld; sealing = true;
        }
        try
        {
            // A launched producer already has an owned job. Persisting its seal
            // cannot own the lock needed to signal its stop or retain its claim.
            latch.RecordProducer(owner.HoldId, proof);
            if (heldClaim) reservations.SetProducer(owner.HoldId, proof);
            lock (gate)
            {
                if (guard != owner || storageStopped || completing)
                    throw new IOException("Capture stopped while producer sealing completed; ownership remains retained.");
                producer = proof;
            }
        }
        finally { lock (gate) sealing = false; }
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
                    // One pending query is shared; retries never queue queries
                    // behind blocked I/O. Poll itself can always request stop.
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
                    throw new IOException("Capture capacity monitoring exceeded its qualified coverage; stopping with claims retained.");
                if (!observation.TryComplete(out var completed))
                {
                    observation.Start();
                    return false;
                }
                // Charge queueing and query work to the original cover. The
                // completion tick is genuine, but it cannot erase a long query.
                if (completed.Started < lastFresh || completed.Finished < completed.Started
                    || Stopwatch.GetElapsedTime(lastFresh, completed.Finished).TotalMilliseconds > MaximumObservationGapMilliseconds)
                    throw new IOException("Capture capacity query exhausted its qualified cover.");
                lastFresh = completed.Started;
                var next = completed.Value;
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
        guard?.RequestStop(); // In-memory fence; the pre-existing guard is durable.
        try { requestStop?.Invoke(); }
        finally
        {
            // A blocked durable-guard read can own the latch lock. Do not wait
            // for it to record a more specific cause: the existing durable guard
            // already blocks restart. Persist the cause after observer closure.
            if (observation?.Running != true && !sealing)
                failedHold = guard == null ? latch.Stop(reason, producerNeverLaunched: producerAbsenceConfirmed) : guard.Stop(reason);
        }
    }
    internal void CompleteCapture(bool producerAbsent)
    {
        Thread running;
        ReplayCapacityObservation<ReplayStorageAssessment> pending;
        lock (gate)
        {
            // One closer owns this generation through its outside-lock waits.
            // A second caller must not carry old absence into a later capture.
            if (completing) return;
            completing = true; finish.Set(); running = watcher; pending = observation;
        }
        bool supervisorClosed = running == null || running == Thread.CurrentThread || running.Join(1000);
        pending?.WaitForClosure(1000); // Bounded drain outside the stop lock, never inferred closure.
        BeforeClosureForChecks?.Invoke();
        lock (gate)
        {
            if (supervisorClosed) watcher = null;
            try
            {
                if (producerAbsent) producerAbsenceConfirmed = true;
                if (guard == null) return;
                if (!producerAbsent) { Stop("Capture producer absence is unconfirmed; storage hold and growth are retained."); return; }
                if (!supervisorClosed || observation?.Running == true || sealing)
                { Stop("Capacity observer closure is unconfirmed; storage hold and growth are retained."); return; }
                // A stopped/failed query may complete late. Observe its closure,
                // never its result, before releasing this exact capture's claim.
                try { observation?.TryComplete(out _); } catch { }
                if (storageStopped && failedHold?.Durable != true)
                    failedHold = guard.Stop(failedHold?.Reason ?? "Capture capacity monitoring stopped.");
                latch.RecordProducer(guard.HoldId, producer, absent: true);
                if (claimHeld) { reservations.Release(guard.HoldId); claimHeld = false; }
                if (!storageStopped)
                {
                    var result = guard.CompleteNormally(true);
                    if (result.Held) { storageStopped = true; failedHold = result; }
                }
                guard = null; armedModel = null; observation = null;
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
                    if (!completed || !absent || CapturePending || Stopped || Hold.Held || preserve()) replays.PreserveSession(id);
                }
            }
        }
    }
    internal void WriteMetrics(RecorderMetrics metrics)
    {
        var held = Hold; var value = assessment;
        metrics.ReplayStorageSafetyKind = held.Held ? "Stop" : value?.Kind.ToString() ?? "Unmeasured";
        metrics.ReplayStorageSafetyHeld = held.Held;
        metrics.ReplayStorageSafetyHoldId = held.Held ? held.HoldId : null;
        metrics.ReplayStorageSafetyWarning = value?.ShouldWarn == true;
        metrics.ReplayStorageSafetyReason = ReplayOutputDemand.Bounded(held.Held ? held.Reason : value?.Reason ?? "Capture poll and stop growth have not been qualified for this recording profile.", 512);
    }
    public void Dispose()
    {
        finish.Set();
        bool joined = watcher == null || watcher == Thread.CurrentThread || watcher.Join(1000);
        if (!joined || observation?.Running == true || sealing)
            StopForStorageFailure("Capacity observer disposal is incomplete; producer and growth claims remain owned.");
        // Disposal never infers producer absence or releases a claim.
    }
}
