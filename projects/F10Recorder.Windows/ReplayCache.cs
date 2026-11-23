using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace F10Recorder;

internal readonly record struct ReplaySessionId(Guid Value);
internal sealed class ReplayMaintenanceTiming
{
    public long StartedQpc, GateEnteredQpc, CompletedQpc;
    public double GateWait, Total;
    public double? Capacity, Segments, Reserve, Prune, Lengths, Budget, Health;
    public string Error, FailedPhase;
}
internal sealed record ReplaySessionInfo(string Directory, string Manifest, int ReplaySeconds,
    int BitrateMbps, int FramesPerSecond, string Codec, string Extension, bool ExcludeInitialSegment, bool RecordAudio);

// This seam supplies capture state and process lifetime, not save ordering.
internal interface IReplaySessionSource : IDisposable
{
    ReplaySessionInfo ReplayInfo { get; }
    bool PreserveEvidence => false;
    bool SupportsOwnedExport => false;
    bool SupportsStagedPreparation => false;
    ReplaySavePreparation BeginPreparation(ReplayRootedFiles files, ReplayInterval interval, Segment candidate, string partial,
        Action requireAccepted, Action<ReplayProducerProof> producerChanged, Action checkStorage, Action<ReplaySavePreparation> takeOwnership) => null;
    bool Stopped { get; }
    string Diagnostics { get; }
    double SaveCutoffAt(long triggerTimestamp);
    void FreezeAt(long stopTimestamp);
    void StopCapture();
    void CheckCaptureHealth(double completedEnd);
    List<Segment> ReadSegments() => ReplaySelection.Read(ReplayInfo.Manifest, ReplayInfo.Directory);
    double InputPrerollSeconds => 0;
    string OutputDemandIdentity => null;
    ReplayOutputDemand OutputDemandStatus => ReplayOutputDemand.Unmeasured("Output/finalization calibration is not available for this source.");
    // Numeric providers must report absence from their owned process handle,
    // including failure paths. An opaque exception is not a cleanup receipt.
    bool OutputProducerStopped => false;
    ReplayProducerProof ProducerProof => null;
    ReplayOutputDemand EstimateOutput(ReplayOutputSelection selection) => ReplayOutputDemand.Unmeasured("Output/finalization calibration is not available for this source.");
    // Periodic policy checks run on the capacity query thread. Production uses
    // a read-only implementation, without overwriting admission diagnostics.
    ReplayOutputDemand RecheckOutputDemand(ReplayOutputSelection selection) => EstimateOutput(selection);
    ReplayValidation ValidateOutput(string path, ReplayInterval interval, Action<ReplayProducerProof> producerChanged, Action checkStorage)
        => throw new IOException("Replay source does not support final MP4 validation.");
    ReplayValidation ValidateOutput(ReplayExportRequest request, Action<ReplayProducerProof> producerChanged, Action checkStorage)
        => ValidateOutput(request.Partial, request.Interval, producerChanged, checkStorage);
    void Export(IReadOnlyList<string> arguments, string snapshot);
    void Export(ReplayExportRequest request, string snapshot, Action<ReplayProducerProof> producerChanged, Action checkStorage)
        => Export(request.LegacyArguments, snapshot, producerChanged, checkStorage);
    void Export(IReadOnlyList<string> arguments, string snapshot, Action<ReplayProducerProof> producerChanged) => Export(arguments, snapshot);
    void Export(IReadOnlyList<string> arguments, string snapshot, Action<ReplayProducerProof> producerChanged, Action checkStorage)
    {
        checkStorage?.Invoke();
        Export(arguments, snapshot, producerChanged);
        checkStorage?.Invoke();
    }
}

internal sealed record ReplaySaveResult(bool Succeeded, string File, string Error, long Bytes, double Milliseconds,
    ReplayInterval Interval = null, ReplayValidation Validation = null, ReplaySaveTiming Timing = null);
internal sealed record SaveSubmission(string Rejection, Task<ReplaySaveResult> Completion, ReplayOutputDemand AdmissionDemand = null, ReplayInterval Interval = null)
{
    internal bool Accepted => Completion != null;
}
internal sealed record ReplayCacheStatus(bool Saving, ReplaySaveResult LastSave, long? CacheBytes, string StorageError,
    ReplayOutputDemand OutputDemand = null, string CacheBytesObservedUtc = null);
internal sealed record ReplaySessionStatus(double BufferedSeconds, long CacheBytes);
internal sealed class ReplayCacheBudgetExceededException(string message) : IOException(message);

// Named policies keep custom/fault providers conservative unless a fixture
// explicitly promises the corresponding native observation/ownership semantics.
internal enum ReplayFileMode
{
    Conservative, Production, NativeInventoryForChecks, NativeOutputForChecks, OwnedSessionForChecks
}

// Only operations whose failures must be controlled in ownership checks vary.
internal class ReplayFiles(ReplayFileMode mode = ReplayFileMode.Conservative)
{
    internal ReplayFileMode Mode { get; } = mode;
    internal virtual void CreateDirectory(string path) => Directory.CreateDirectory(path);
    internal virtual void WriteText(string path, string text) => File.WriteAllText(path, text, new UTF8Encoding(false));
    internal virtual ReplayNamespaceIdentity ObserveNamespace(string path, ReplayFileExtent enumerated)
    {
        var extent = Mode == ReplayFileMode.Production ? enumerated : ObserveInventory(path, enumerated);
        return new(extent.Volume, extent.Identity);
    }
    internal virtual ReplayNamespaceIdentity ObserveManifestNamespace(string path, ReplayFileExtent enumerated)
    {
        var extent = ObserveManifest(path, enumerated);
        return new(extent.Volume, extent.Identity);
    }
    internal virtual ReplayFileExtent Observe(string path) => ReplayStorage.Observe(path);
    internal virtual ReplayFileExtent ObserveInventory(string path, ReplayFileExtent enumerated) => ReplayStorage.ObserveInventory(path, enumerated);
    internal virtual ReplayFileExtent ObserveManifest(string path, ReplayFileExtent enumerated) => enumerated;
    internal virtual ReplayVolumeCapacity ObserveCapacity(string directory) => ReplayStorage.ObserveCapacity(directory);
    internal virtual bool TryHardLink(string copy, string original) => Native.CreateHardLink(copy, original, IntPtr.Zero);
    internal virtual void Copy(string original, string copy) => System.IO.File.Copy(original, copy);
    internal virtual void Publish(string partial, string final) => System.IO.File.Move(partial, final);
    internal virtual void Publish(ReplayExportRequest request, string final)
    {
        if (request.ValidatedOutput != null)
            request.ValidatedOutput.Publish(final, request.RequirePublicationFreshness);
        else
        {
            request.RequirePublicationFreshness?.Invoke();
            Publish(request.Partial, final);
        }
    }
    internal virtual void DeleteFile(string path) => System.IO.File.Delete(path);
    internal virtual void DeleteDirectory(string path, string workspacePartial = null) => ReplayCacheFileOperations.DeleteTree(path, workspacePartial);
}

// One instance belongs to one protected recording profile. It owns registered
// sources, accepted saves, and the point at which session resources can be freed.
internal sealed partial class ReplayCache : IDisposable
{
    sealed class Session(IReplaySessionSource source)
    {
        internal readonly IReplaySessionSource Source = source;
        internal readonly ReplaySessionInfo Info = source.ReplayInfo;
        internal readonly HashSet<string> Pins = new(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, Segment> Reserved = new(StringComparer.OrdinalIgnoreCase);
        internal double? PendingEnd;
        internal bool Retiring;
        internal bool PreserveEvidence;
        internal Task Stop, Retirement;
        internal Task<ReplaySaveResult> Save;
        internal ReplayCompletedHistory History;
        internal double LastEnd;
        internal long Bytes, BytesAt;
    }

    readonly object gate = new();
    readonly Dictionary<ReplaySessionId, Session> sessions = new();
    readonly ReplayFiles files;
    readonly ReplaySnapshotInventory snapshotInventory;
    readonly bool reuseCompletedInputMetadata;
    readonly bool reuseActiveOutputMetadata;
    ReplayActiveOutputObservation activeOutputObservation;
    internal Action<ReplayActiveOutputObservation> ActiveOutputCreatedForChecks;
    ReplayFiles OutputFiles => activeOutputObservation ?? files;
    readonly ReplayVolumeReservations volumeReservations;
    readonly string volumeReservationOwner = Guid.NewGuid().ToString("N");
    bool volumeReservationHeld;
    IReplaySessionSource pendingSource;
    string reservationError;
    readonly TimeProvider time;
    readonly Func<Task> waitForSegment;
    readonly Action<string> diagnostic;
    readonly string[] cacheDirectories;
    readonly long cacheBudgetBytes;
    Dictionary<string, long> pendingAllocations = new(StringComparer.OrdinalIgnoreCase);
    ReplayVolumeCapacity pendingTarget;
    string pendingSnapshot;
    sealed class OutputClaim(IReplaySessionSource source, string directory, string partial, ReplayVolumeCapacity target, ReplayOutputDemand demand, ReplayOutputSelection selection)
    {
        internal readonly IReplaySessionSource Source = source;
        internal readonly string Directory = directory, Partial = partial;
        internal readonly ReplayVolumeCapacity Target = target;
        internal readonly ReplayOutputDemand Demand = demand;
        internal readonly ReplayOutputSelection Selection = selection;
        internal string MaterializedIdentity;
        internal readonly Dictionary<string, string> WorkIdentities = new(StringComparer.OrdinalIgnoreCase);
    }
    sealed class OutputEvidenceInvalid(string message) : IOException(message);
    OutputClaim pendingOutput;
    ReplayCapacityObservation<bool> capacityObservation;
    long capacityObservedAt;
    bool capacityQuiescing;
    bool capacityRequireNext;
    long capacityLastAttemptAt, capacityLastPolicyAt;
    long capacityRequiredPolicyAt;
    int pendingProducerUpdates;
    ReplayStorageCheckTiming capacityTiming;
    long capacityLastTimedQuery;
    ReplayOutputDemand outputDemand = ReplayOutputDemand.Unmeasured("Output/finalization calibration has not been established.");
    bool closing, saving;
    readonly bool useRequestAuthority;
    readonly bool useOwnedProductionFiles;
    ReplayRootedFiles retainedNativeRequest;
    internal Action<ReplayRootedFiles> OwnedRequestCreatedForChecks;
    internal Func<Task> CapacityDelayForChecks;
    // Qualification alone serializes its inventory with namespace mutations.
    // The delegate may take only its observer gate; preparation/callback work
    // happens before this seam, outside Cache and native metadata locks.
    internal Action<Action> SerializeNamespaceMutationForChecks;
    internal bool RetainSourceNamespaceForQualificationChecks;
    IDisposable pendingAuthoritySession;
    Action nativeNamespaceChanged;
    ReplaySaveResult lastSave;
    Task close;

    internal ReplayCache(ReplayFiles files = null, TimeProvider time = null,
        Func<Task> waitForSegment = null, Action<string> diagnostic = null, string cacheDirectory = null, long cacheBudgetBytes = 10_000_000_000, IEnumerable<string> retainedDirectories = null, ReplayVolumeReservations volumeReservations = null,
        ReplayFileMode? fileMode = null, bool? reuseCompletedInputMetadata = null)
    {
        this.files = files ?? new ReplayFiles(ReplayFileMode.Production);
        (bool OwnedExport, bool Authority, bool Inventory, bool Output) policy = (fileMode ?? this.files.Mode) switch
        {
            ReplayFileMode.Production => (true, true, true, true),
            ReplayFileMode.Conservative => (false, false, false, false),
            ReplayFileMode.NativeInventoryForChecks => (false, false, true, false),
            ReplayFileMode.NativeOutputForChecks => (false, false, false, true),
            ReplayFileMode.OwnedSessionForChecks => (false, true, true, false),
            _ => throw new ArgumentOutOfRangeException(nameof(fileMode))
        };
        useOwnedProductionFiles = policy.OwnedExport;
        useRequestAuthority = policy.Authority;
        if (policy.Inventory) snapshotInventory = new();
        if (reuseCompletedInputMetadata == true && !policy.Inventory)
            throw new ArgumentException("Completed-input metadata reuse requires explicit native inventory semantics.");
        this.reuseCompletedInputMetadata = reuseCompletedInputMetadata ?? policy.Inventory;
        reuseActiveOutputMetadata = policy.Output;
        this.volumeReservations = volumeReservations;
        this.time = time ?? TimeProvider.System;
        this.waitForSegment = waitForSegment ?? (() => Task.Delay(TimeSpan.FromMilliseconds(100), this.time));
        this.diagnostic = diagnostic ?? Log.Write;
        cacheDirectories = cacheDirectory == null && retainedDirectories == null ? null
            : (cacheDirectory == null ? Enumerable.Empty<string>() : new[] { cacheDirectory }).Concat(retainedDirectories ?? []).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (cacheBudgetBytes <= 0) throw new ArgumentException("Replay-cache budget must be positive.");
        this.cacheBudgetBytes = cacheBudgetBytes;
    }

    internal ReplaySessionId RegisterSession(IReplaySessionSource source)
    {
        lock (gate)
        {
            if (closing) throw new InvalidOperationException("Replay cache is closing.");
            if (sessions.Values.Any(s => ReferenceEquals(s.Source, source))) throw new InvalidOperationException("Session already registered.");
            var id = new ReplaySessionId(Guid.NewGuid());
            nativeNamespaceChanged?.Invoke();
            var session=new Session(source) { PreserveEvidence = source.PreserveEvidence };
            if(useOwnedProductionFiles && source.SupportsOwnedExport && ReplaySaveExport.Supports(session.Info))
                session.History=new(session.Info);
            sessions.Add(id,session);
            if (!saving && pendingOutput == null) outputDemand = source.OutputDemandStatus;
            return id;
        }
    }

    internal ReplayCacheStatus Status
    {
        get
        {
            lock (gate)
            {
                try { return new(saving, lastSave, ReplayStorage.Bytes(Inventory()), reservationError, outputDemand, cacheBytesObservedUtc); }
                catch (Exception ex) when (StorageFailure(ex)) { return new(saving, lastSave, null, ex.Message, outputDemand); }
            }
        }
    }
    long? lastObservedCacheBytes;
    string cacheBytesObservedUtc, inventoryError;
    // Diagnostics only. Current save state is live; byte usage is explicitly the
    // last complete observation. No admission, claim or capacity decision uses it.
    internal ReplayCacheStatus LastKnownStatus
    {
        get
        {
            lock (gate)
            {
                string error = reservationError == null ? inventoryError : inventoryError == null
                    ? reservationError : reservationError + " " + inventoryError;
                return new(saving, lastSave, lastObservedCacheBytes, error, outputDemand, cacheBytesObservedUtc);
            }
        }
    }
    internal bool IsSaving { get { lock (gate) return saving; } }
    static bool StorageFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or OverflowException;
    // Keep this compound predicate outside the async exception filters. The
    // pinned compiler otherwise reuses its pattern temporary as lockTaken;
    // first-pass filtering can then suppress the monitor's finally release.
    bool RequiresSaveEvidenceRetention(Exception error) => error is OutputEvidenceInvalid or ReplayOutputIdentityException
        || volumeReservationHeld && StorageFailure(error);
    Dictionary<string, ReplayFileExtent> Inventory()
    {
        try
        {
            Dictionary<string, ReplayFileExtent> inventory;
            snapshotInventory?.BeginScan();
            try { inventory = ReplayStorage.Inventory(cacheDirectories == null
                ? sessions.Values.Select(s => s.Info.Directory).Where(Directory.Exists) : ReplayCacheLocations.Existing(cacheDirectories), snapshotInventory ?? files); }
            finally { snapshotInventory?.EndScan(); }
            // Publish diagnostic data only after the entire scan and handle
            // cleanup succeed. The dictionary itself is never cached or reused.
            lastObservedCacheBytes = ReplayStorage.Bytes(inventory);
            cacheBytesObservedUtc = time.GetUtcNow().ToString("o"); inventoryError = null;
            return inventory;
        }
        catch (Exception error) when (StorageFailure(error))
        {
            lastObservedCacheBytes = null; cacheBytesObservedUtc = null; inventoryError = error.Message;
            throw;
        }
    }

    internal void ValidateBudget(long proposedBudgetBytes)
    {
        if (proposedBudgetBytes <= 0) throw new ArgumentOutOfRangeException(nameof(proposedBudgetBytes));
        lock (gate) RequireBudget(pendingAllocations, proposedBudgetBytes);
    }

    void RequireBudget(IReadOnlyDictionary<string, long> proposed, long? ceiling = null, Dictionary<string, ReplayFileExtent> observed = null)
    {
        var inventory = observed ?? Inventory();
        long committed = ReplayStorage.Bytes(inventory);
        foreach (var claim in proposed)
        {
            // Observed allocation already affects physical usage. Only its future
            // remainder is a claim; never charge a materializing copy twice.
            long allocated = inventory.TryGetValue(claim.Key, out var extent) ? extent.AllocatedBytes : 0;
            committed = checked(committed + Math.Max(0, claim.Value - allocated));
        }
        if (committed > (ceiling ?? cacheBudgetBytes)) throw new ReplayCacheBudgetExceededException("Replay-cache budget cannot hold retained footage and temporary input copies. Increase the budget or release retained material explicitly.");
    }

    string ConcatText(IReadOnlyList<Segment> selection, string extension) => string.Concat(selection.Select((s, i) =>
        "file '" + i.ToString("D6") + extension + "'" + Environment.NewLine + "duration " + (s.End - s.Start).ToString("0.000000", CultureInfo.InvariantCulture)
            + Environment.NewLine + "inpoint " + s.Start.ToString("0.000000", CultureInfo.InvariantCulture) + Environment.NewLine));

    static long AllocationBytes(long length, long unit) => checked((length / unit + (length % unit == 0 ? 0 : 1)) * unit);

    static void RequireTarget(ReplayVolumeCapacity expected, ReplayVolumeCapacity actual)
    {
        if (!string.Equals(expected.Volume, actual.Volume, StringComparison.OrdinalIgnoreCase) || expected.AllocationUnit != actual.AllocationUnit)
            throw new IOException("Known-input capacity target volume or allocation unit changed.");
    }

    void RequireKnownInputCapacity(IReadOnlyDictionary<string, long> proposed, string snapshot, ReplayVolumeCapacity target)
        => RequireStorageCapacity(proposed, snapshot, target, pendingOutput);

    // Periodic capacity does not re-inventory protected inputs or discharge
    // materialized allocation. Full validation remains at admission, rebinding
    // and release; native input/output owners still check identity per callback.
    void RequireAcceptedCapacity(string snapshot, ReplayVolumeCapacity target)
    {
        if (volumeReservations == null || !volumeReservationHeld)
        {
            RequireKnownInputCapacity(pendingAllocations, snapshot, target);
            if (pendingOutput != null && pendingOutput.Source.EstimateOutput(pendingOutput.Selection) != pendingOutput.Demand)
                throw new OutputEvidenceInvalid("Replay output calibration changed during export; evidence is retained.");
            return;
        }
        if (Stopwatch.GetElapsedTime(capacityObservedAt).TotalMilliseconds > ReplayCapacityPolicy.CoverageMilliseconds)
            throw new IOException("Accepted replay capacity monitoring exhausted its conservative cover.");
        if (capacityObservation != null)
        {
            var query = capacityObservation;
            try
            {
                if (capacityObservation.TryComplete(out var completed))
                {
                    capacityObservation = null;
                    if (!completed.Value || Stopwatch.GetElapsedTime(completed.Started, completed.Finished).TotalMilliseconds > ReplayCapacityPolicy.CoverageMilliseconds)
                        throw new IOException("Accepted replay capacity query exhausted its conservative cover.");
                    capacityObservedAt = Math.Max(capacityObservedAt, completed.Started);
                    capacityLastPolicyAt = completed.Started;
                }
            }
            catch (ReplayVolumeReservationBusyException) { capacityObservation = null; }
            finally
            {
                if (capacityTiming != null)
                {
                    capacityTiming.CapacityQueryPending = query.Running;
                    if (!query.Running && query.LastFinished != 0 && query.LastStarted != capacityLastTimedQuery)
                    {
                        double work = Stopwatch.GetElapsedTime(query.LastStarted, query.LastFinished).TotalMilliseconds;
                        capacityTiming.CapacityCompletions++;
                        capacityTiming.CapacityQueryMilliseconds += work;
                        capacityTiming.MaximumCapacityQueryMilliseconds = Math.Max(capacityTiming.MaximumCapacityQueryMilliseconds, work);
                        capacityLastTimedQuery = query.LastStarted;
                    }
                }
            }
        }
        if (capacityObservation == null && !capacityQuiescing
            && (capacityRequiredPolicyAt == 0 || capacityLastPolicyAt < capacityRequiredPolicyAt)
            && Stopwatch.GetElapsedTime(capacityLastAttemptAt).TotalMilliseconds >= ReplayCapacityPolicy.IntervalMilliseconds
            && (capacityRequireNext || Stopwatch.GetElapsedTime(capacityObservedAt).TotalMilliseconds >= ReplayCapacityPolicy.IntervalMilliseconds))
        {
            // Immutable request bindings; query work never enters the Cache gate.
            var targets = new List<(string Path, ReplayVolumeCapacity Target)> { (snapshot, target) };
            var output = pendingOutput;
            if (output != null && !string.Equals(target.Volume, output.Target.Volume, StringComparison.OrdinalIgnoreCase))
                targets.Add((output.Directory, output.Target));
            capacityObservation = new(() =>
            {
                foreach (var item in targets)
                {
                    var value = volumeReservations.ObserveUnreserved(item.Path, files);
                    RequireTarget(item.Target, new(value.Volume, value.AllocationUnit, 0));
                    if (value.Bytes < 0)
                        throw new IOException("Accepted replay capacity no longer covers all outstanding commitments.");
                }
                if (output != null && output.Source.RecheckOutputDemand(output.Selection) != output.Demand)
                    throw new OutputEvidenceInvalid("Replay output calibration changed or became unmeasured during export; evidence is retained.");
                return true;
            });
            capacityObservation.Start();
            if (capacityTiming != null) { capacityTiming.CapacityQueries++; capacityTiming.CapacityQueryPending = true; }
            capacityLastAttemptAt = Stopwatch.GetTimestamp();
            capacityRequireNext = false;
        }
    }

    ReplayNativeExportScope.Snapshot nativeSnapshot;
    sealed class DeferredNativeClose(ReplayExportRequest request, IReplaySessionSource source,
        ReplayRootedFiles files, bool ownsFiles, string owner)
    {
        internal readonly ReplayExportRequest Request = request;
        internal readonly IReplaySessionSource Source = source;
        internal readonly ReplayRootedFiles Files = files;
        internal readonly bool OwnsFiles = ownsFiles;
        internal readonly string Owner = owner;
        internal bool LeaseClosed, ScopeClosed, RegistryClosed;
    }
    DeferredNativeClose deferredNativeClose;

    // Cache gate held; actual producers/readers have already joined. These are
    // idempotent resource closes, never a task/process join under the gate.
    void CloseDeferredNative()
    {
        var deferred = deferredNativeClose;
        if (deferred == null) return;
        if (deferred.Owner != volumeReservationOwner || !ReferenceEquals(deferred.Source, pendingSource)
            || !deferred.Source.OutputProducerStopped || !deferred.Request.NativeFiles.CanDispose)
            throw new IOException("Deferred native closure no longer has its exact stopped request owner.");
        if (!deferred.ScopeClosed && !ReferenceEquals(deferred.Files.Active, deferred.Request.NativeFiles))
            throw new IOException("Deferred native registry changed before observer closure.");
        if (!deferred.LeaseClosed)
        {
            deferred.Request.ValidatedOutput?.Dispose();
            deferred.LeaseClosed = true;
        }
        if (!deferred.ScopeClosed)
        {
            deferred.Request.NativeFiles.Dispose();
            deferred.ScopeClosed = deferred.Request.NativeFiles.Closed;
            if (!deferred.ScopeClosed) throw new IOException("Deferred native scope closure is unconfirmed.");
        }
        if (deferred.Files.Active != null && !ReferenceEquals(deferred.Files.Active, deferred.Request.NativeFiles))
            throw new IOException("Deferred native registry belongs to another request.");
        deferred.Files.Active = null;
        if (deferred.OwnsFiles && !deferred.RegistryClosed)
        {
            deferred.Files.Dispose();
            deferred.RegistryClosed = true;
        }
        deferredNativeClose = null;
    }
    long ObserveOutputEvidence(OutputClaim output, IReadOnlyDictionary<string, ReplayNamespaceIdentity> namespaces)
    {
        long outputAllocated = 0;
        if (output != null)
        {
            try
            {
                var attributes = nativeSnapshot != null ? (FileAttributes)(nativeSnapshot.Main?.Attributes ?? throw new FileNotFoundException()) : File.GetAttributes(output.Partial);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                    throw new IOException("Replay output is not an ordinary file.");
                var extent = ReplayStorage.Validate(nativeSnapshot != null ? nativeSnapshot.Main.Extent : OutputFiles.Observe(output.Partial));
                RequireTarget(output.Target, new(extent.Volume, extent.AllocationUnit, 0));
                if (output.MaterializedIdentity != null && !string.Equals(output.MaterializedIdentity, extent.Identity, StringComparison.OrdinalIgnoreCase))
                    throw new OutputEvidenceInvalid("Replay output allocation identity changed; output and snapshot are retained.");
                output.MaterializedIdentity = extent.Identity;
                if (namespaces.Any(entry => !string.Equals(entry.Key, output.Partial, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(entry.Value.Volume, extent.Volume, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(entry.Value.Identity, extent.Identity, StringComparison.OrdinalIgnoreCase)))
                    throw new OutputEvidenceInvalid("Replay output aliases retained input storage; output and snapshot are retained.");
                if (extent.AllocatedBytes > output.Demand.PeakAllocatedBytes - (output.Demand.Scratch?.Total ?? 0))
                    throw new OutputEvidenceInvalid("Replay output exceeded its qualified peak allocation; output and snapshot are retained.");
                outputAllocated = extent.AllocatedBytes;
            }
            catch (FileNotFoundException)
            {
                if (output.MaterializedIdentity != null) throw new OutputEvidenceInvalid("Materialized replay output disappeared; snapshot is retained.");
            }
            catch (DirectoryNotFoundException)
            {
                if (output.MaterializedIdentity != null) throw new OutputEvidenceInvalid("Materialized replay output directory disappeared; snapshot is retained.");
            }
            var workspace = nativeSnapshot?.Work ?? ReplayOutputWorkspace.Observe(output.Partial, files);
            if (output.WorkIdentities.Keys.Any(path => !workspace.ContainsKey(path)))
                throw new OutputEvidenceInvalid("Materialized replay workspace output disappeared; protected evidence is retained.");
            var workIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (output.MaterializedIdentity != null) workIdentities.Add(output.MaterializedIdentity);
            foreach (var member in workspace)
            {
                var extent = member.Value;
                if (output.Demand.Scratch is { } scratch)
                {
                    long limit = ReplayOutputWorkspace.Classify(output.Partial, member.Key) switch
                    {
                        ReplayOutputWorkspace.Member.Assembly => scratch.AssemblyPeakAllocatedBytes,
                        ReplayOutputWorkspace.Member.Boundary => scratch.BoundaryPeakAllocatedBytes,
                        _ => throw new OutputEvidenceInvalid("Replay scratch path is outside its declared claims.")
                    };
                    if (extent.AllocatedBytes > limit) throw new OutputEvidenceInvalid("Replay scratch exceeded its declared per-file peak.");
                }
                else if (volumeReservations != null) throw new OutputEvidenceInvalid("Replay scratch has no declared shared-storage claims.");
                RequireTarget(output.Target, new(extent.Volume, extent.AllocationUnit, 0));
                if (!workIdentities.Add(extent.Identity) || namespaces.Values.Any(e => e.Volume == extent.Volume && e.Identity == extent.Identity))
                    throw new OutputEvidenceInvalid("Replay scratch aliases another retained allocation; protected evidence is retained.");
                if (output.WorkIdentities.TryGetValue(member.Key, out string identity) && identity != extent.Identity)
                    throw new OutputEvidenceInvalid("Replay scratch identity changed; protected evidence is retained.");
                output.WorkIdentities[member.Key] = extent.Identity;
                outputAllocated = checked(outputAllocated + extent.AllocatedBytes);
            }
            if (outputAllocated > output.Demand.PeakAllocatedBytes)
                throw new OutputEvidenceInvalid("Replay output and scratch exceeded their qualified combined peak allocation.");
        }
        return outputAllocated;
    }

    void RequireStorageCapacity(IReadOnlyDictionary<string, long> proposed, string snapshot, ReplayVolumeCapacity target, OutputClaim output,
        Dictionary<string, ReplayFileExtent> observed = null, ReplayProducerProof producer = null)
    {
        using var trace = TraceStorage("Capacity");
        try
        {
        trace?.Phase("Inventory");
        // Inventory precedes free-space sampling: a newer materialized prefix
        // must not be credited against older, pre-write available capacity.
        // A caller may share one fresh observation across budget/capacity checks
        // in this same locked operation. No observation survives a write.
        var inventory = observed ?? Inventory();
        _ = ReplayStorage.Bytes(inventory); // Reject inconsistent shared-allocation observations, without subtracting them from free space.
        trace?.Phase("OutputEvidence");
        long outputAllocated = ObserveOutputEvidence(output, inventory.ToDictionary(e => e.Key, e => new ReplayNamespaceIdentity(e.Value.Volume, e.Value.Identity), StringComparer.OrdinalIgnoreCase));
        trace?.Phase("InputCapacity");
        var capacity = ReplayStorage.ValidateCapacity(files.ObserveCapacity(snapshot));
        RequireTarget(target, capacity);
        trace?.Phase("InputClaims");
        long remaining = 0;
        foreach (var claim in proposed)
        {
            long allocated = 0;
            if (inventory.TryGetValue(claim.Key, out var extent))
            {
                RequireTarget(target, new(extent.Volume, extent.AllocationUnit, 0));
                allocated = extent.AllocatedBytes;
            }
            remaining = checked(remaining + Math.Max(0, claim.Value - allocated));
        }
        long knownInputRemaining = remaining;
        if (output != null)
        {
            trace?.Phase("OutputCapacity");
            var outputCapacity = ReplayStorage.ValidateCapacity(files.ObserveCapacity(output.Directory));
            RequireTarget(output.Target, outputCapacity);
            long outputRemaining = Math.Max(0, output.Demand.PeakAllocatedBytes - outputAllocated);
            if (string.Equals(capacity.Volume, outputCapacity.Volume, StringComparison.OrdinalIgnoreCase))
            {
                RequireTarget(capacity, outputCapacity);
                remaining = checked(remaining + outputRemaining);
                capacity = capacity with { AvailableBytes = Math.Min(capacity.AvailableBytes, outputCapacity.AvailableBytes) };
            }
            else if (outputRemaining > outputCapacity.AvailableBytes)
                throw new IOException("Replay output capacity cannot hold remaining output/finalization demand on its target volume.");
        }
        if (remaining > capacity.AvailableBytes)
            throw new IOException("Known-input capacity cannot hold remaining replay snapshot copies and concat data on their target volume.");
        if (volumeReservations != null)
        {
            trace?.Phase("ReservationClaims");
            var claims = new List<ReplayVolumeClaim>();
            if (knownInputRemaining != 0)
                claims.Add(new(Path.Combine(snapshot, ".known-input-growth"), snapshot, target.Volume,
                    target.AllocationUnit, knownInputRemaining, ReplayVolumeClaimKind.UnmaterializedGrowth));
            if (output != null)
            {
                var scratch = output.Demand.Scratch;
                claims.Add(new(output.Partial, output.Directory, output.Target.Volume,
                    output.Target.AllocationUnit, output.Demand.PeakAllocatedBytes - (scratch?.Total ?? 0)));
                if (scratch != null)
                {
                    claims.Add(new(ReplayOutputWorkspace.AssemblyPath(output.Partial), output.Directory, output.Target.Volume,
                        output.Target.AllocationUnit, scratch.AssemblyPeakAllocatedBytes));
                    for (int n = 0; n < 2; n++) claims.Add(new(ReplayOutputWorkspace.BoundaryPath(output.Partial, n), output.Directory,
                        output.Target.Volume, output.Target.AllocationUnit, scratch.BoundaryPeakAllocatedBytes));
                }
            }
            trace?.Phase("ReservationLedger");
            using var evidence=useRequestAuthority&&nativeSnapshot!=null
                ?nativeSnapshot.Owner?.BorrowReservationEvidence(nativeSnapshot,volumeReservationOwner):null;
            if(useRequestAuthority&&claims.Count!=0)volumeReservationHeld=true; // A throwing Update may already have persisted.
            volumeReservations.Update(volumeReservationOwner, claims, OutputFiles, producer ?? pendingSource?.ProducerProof, acceptedWork: saving,evidence:evidence);
            volumeReservationHeld = claims.Count != 0;
            reservationError = null;
        }
        capacityObservedAt = Stopwatch.GetTimestamp(); // Successful full capacity boundary, never a callback/busy timestamp.
        }
        catch (Exception error) { trace?.Failed(error); throw; }
    }

    OutputClaim PlanOutput(Session session, IReadOnlyList<Segment> selection, double end, string root, string partial,
        bool closed, out ReplayOutputDemand demand)
        => PlanOutput(session, selection, end, root, partial, closed, out demand, out _, false);

    OutputClaim PlanOutput(Session session, IReadOnlyList<Segment> selection, double end, string root, string partial,
        bool closed, out ReplayOutputDemand demand, out ReplayOutputSelection binding, bool allowTailReadiness)
    {
        binding = null;
        string exporter = session.Source.OutputDemandIdentity;
        if (string.IsNullOrWhiteSpace(exporter))
        {
            if (pendingOutput != null) throw new IOException("Replay exporter identity became unmeasured after reservation.");
            demand = session.Source.OutputDemandStatus;
            if (demand == null || demand.Kind != ReplayOutputEvidenceKind.Unmeasured || string.IsNullOrWhiteSpace(demand.Reason))
                throw new IOException("A source without qualified exporter identity must report explicit unmeasured demand.");
            RequireQualifiedOutput(demand);
            return null;
        }
        binding = ReplayOutputSelection.Create(session.Info, exporter, root, end, closed, selection, files);
        demand = session.Source.EstimateOutput(binding);
        if (demand == null || !Enum.IsDefined(demand.Kind)) throw new IOException("Replay output demand evidence is invalid.");
        if (demand.Kind == ReplayOutputEvidenceKind.Unmeasured)
        {
            if (pendingOutput != null) throw new IOException("Replay output demand became unmeasured after reservation.");
            if (string.IsNullOrWhiteSpace(demand.Reason)) throw new IOException("Unmeasured replay output demand needs a reason.");
            if (allowTailReadiness && volumeReservations != null && !closed
                && demand.Readiness is { AwaitingCompletedTail: true, ModelIdentity.Length: 64 })
                return null;
            RequireQualifiedOutput(demand);
            return null;
        }
        if (demand.Readiness?.AwaitingCompletedTail == true
            || string.IsNullOrWhiteSpace(demand.Revision) || string.IsNullOrWhiteSpace(binding.ExporterIdentity)
            || demand.ExporterIdentity != binding.ExporterIdentity || demand.SelectionIdentity != binding.Identity
            || demand.AllocationUnit <= 0 || demand.PeakAllocatedBytes <= 0 || demand.PeakAllocatedBytes % demand.AllocationUnit != 0
            || demand.ConcatMaximumBytes is < 1 or > 1048576 || !closed && !demand.CoversOpenTail)
            throw new IOException("Replay output demand is stale, incomplete or does not cover the unfinished tail.");
        if (demand.Scratch is { } scratch && (scratch.AssemblyPeakAllocatedBytes <= 0 || scratch.BoundaryPeakAllocatedBytes <= 0
            || scratch.AssemblyPeakAllocatedBytes % demand.AllocationUnit != 0 || scratch.BoundaryPeakAllocatedBytes % demand.AllocationUnit != 0
            || scratch.Total >= demand.PeakAllocatedBytes)) throw new IOException("Replay output scratch demand is incomplete or outside its combined peak.");
        if (pendingOutput != null && (pendingOutput.Demand.Revision != demand.Revision || pendingOutput.Demand.ExporterIdentity != demand.ExporterIdentity
            || pendingOutput.Demand.ConcatMaximumBytes != demand.ConcatMaximumBytes))
            throw new IOException("Replay output demand revision or exporter changed after reservation.");
        var target = ReplayStorage.ValidateCapacity(files.ObserveCapacity(root));
        RequireTarget(new(demand.Volume, demand.AllocationUnit, 0), target);
        if (pendingOutput != null) RequireTarget(pendingOutput.Target, target);
        return new(session.Source, root, partial, target, demand, binding);
    }

    void RequireQualifiedOutput(ReplayOutputDemand demand)
    {
        outputDemand = demand;
        // The production engine always joins the account ledger. A collector
        // without that ledger can still measure an unqualified source, but must
        // not describe its necessary-only admission as storage sufficiency.
        if (volumeReservations != null)
            throw new IOException("Replay output/finalization demand is unmeasured: " + demand.Reason);
    }

    static void ReserveOpenSnapshot(Dictionary<string, long> plan, string snapshot, ReplayVolumeCapacity target, OutputClaim output)
    {
        if (output == null || output.Selection.Closed || !output.Demand.CoversOpenTail) return;
        long bound = output.Demand.SnapshotPeakAllocatedBytes ?? throw new IOException("Open-tail admission needs a complete snapshot allocation envelope.");
        long known = 0;
        foreach (long value in plan.Values) known = checked(known + value);
        if (bound < known || bound <= 0 || bound % target.AllocationUnit != 0)
            throw new IOException("Open-tail snapshot allocation envelope is invalid or smaller than its known inputs.");
        plan.Add(Path.Combine(snapshot, ".open-tail-growth"), bound - known);
    }

    static bool OutputStopped(OutputClaim claim)
    {
        if (claim == null) return true;
        try { return claim.Source.OutputProducerStopped; }
        catch (Exception) { return false; }
    }

    bool PendingProducerStopped()
    {
        if (pendingSource == null) return OutputStopped(pendingOutput);
        try { return pendingSource.OutputProducerStopped; }
        catch (Exception) { return false; }
    }

    bool ReleasePendingStorage()
    {
        if (capacityObservation?.Running == true || pendingProducerUpdates != 0)
        {
            reservationError = "Replay capacity observer closure is unconfirmed; storage remains reserved.";
            Report(reservationError);
            return false;
        }
        if (activeOutputObservation?.HasOwnedResources == true)
        {
            reservationError = "Replay output observer closure is unconfirmed; storage remains reserved.";
            Report(reservationError);
            return false;
        }
        if (volumeReservationHeld)
        {
            try { volumeReservations.Release(volumeReservationOwner, acceptedWork: saving); }
            catch (Exception ex) when (StorageFailure(ex))
            {
                reservationError = "Replay storage release is unconfirmed: " + ex.Message;
                Report(reservationError);
                return false;
            }
        }
        try { CloseDeferredNative(); }
        catch (Exception error) when (StorageFailure(error) || error is ObjectDisposedException or AggregateException)
        {
            reservationError = "Replay deferred native closure is unconfirmed: " + error.Message;
            Report(reservationError); return false;
        }
        volumeReservationHeld = false;
        pendingAllocations.Clear();
        pendingTarget = null;
        pendingSnapshot = null;
        pendingOutput = null;
        pendingSource = null;
        capacityObservation = null; capacityQuiescing = false; capacityRequireNext = false; capacityObservedAt = 0;
        capacityLastAttemptAt = capacityLastPolicyAt = 0;
        capacityRequiredPolicyAt = 0;
        capacityTiming = null; capacityLastTimedQuery = 0;
        reservationError = null;
        return true;
    }

    Dictionary<string, long> PlanSnapshot(IReadOnlyList<Segment> selection, string extension, string snapshot, out ReplayVolumeCapacity target, Action service = null)
    {
        target = ReplayStorage.ValidateCapacity(files.ObserveCapacity(snapshot));
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < selection.Count; i++)
        {
            service?.Invoke();
            var extent = ReplayStorage.Validate(files.Observe(selection[i].Path));
            result.Add(Path.Combine(snapshot, i.ToString("D6") + extension), AllocationBytes(extent.Length, target.AllocationUnit));
        }
        long length = Encoding.UTF8.GetByteCount(ConcatText(selection, extension));
        result.Add(Path.Combine(snapshot, "concat.txt"), AllocationBytes(length, target.AllocationUnit));
        return result;
    }

    List<Segment> ReadSegments(Session session)
    {
        try
        {
            var info = session.Info;
            var segments = session.Source.ReadSegments();
            if (info.ExcludeInitialSegment) segments.RemoveAll(s => Path.GetFileName(s.Path) == "chunk-000000000" + info.Extension);
            return segments;
        }
        catch (IOException) { return []; }
    }

    void ReserveCompleted(Session session, List<Segment> available)
    {
        if (!session.PendingEnd.HasValue) return;
        foreach (var segment in SelectInputs(session, available, session.PendingEnd.Value, session.Info.ReplaySeconds))
        {
            session.Reserved.TryAdd(segment.Path, segment);
            session.Pins.Add(segment.Path);
        }
    }

    static List<Segment> SelectInputs(Session session, List<Segment> available, double end, double seconds)
    {
        double context = session.Source.InputPrerollSeconds;
        if (!double.IsFinite(context) || context < 0 || context > 1) throw new IOException("Replay input context is invalid.");
        return ReplaySelection.Select(available, end, seconds + context);
    }

    internal ReplaySessionStatus GetStatus(ReplaySessionId id)
    {
        lock (gate)
        {
            if (!sessions.TryGetValue(id, out var session) || session.Retiring) return new(0, 0);
            try
            {
                var segments = ReadSegments(session);
                if (session.BytesAt == 0 || time.GetElapsedTime(session.BytesAt).TotalSeconds >= 1)
                {
                    session.Bytes = Directory.GetFiles(session.Info.Directory, "chunk-*" + session.Info.Extension)
                        .Sum(p => { try { return new FileInfo(p).Length; } catch (IOException) { return 0L; } });
                    session.BytesAt = time.GetTimestamp();
                }
                return new(segments.Count == 0 ? 0 : Math.Min(session.Info.ReplaySeconds, segments[^1].End - segments[0].Start), session.Bytes);
            }
            catch (IOException) { return new(0, 0); }
            catch (UnauthorizedAccessException) { return new(0, 0); }
        }
    }

    internal void Maintain(ReplaySessionId id)
    {
        // Only finite checks opt in. Normal capture adds no timer/record allocation.
        var observed = MaintenanceObservedForChecks;
        ReplayMaintenanceTiming timing = observed == null ? null : new();
        long started = timing == null ? 0 : Stopwatch.GetTimestamp(), at = started;
        string phase = null;
        void Phase(string next)
        {
            if (timing == null) return;
            long now = Stopwatch.GetTimestamp(); double elapsed = Stopwatch.GetElapsedTime(at, now).TotalMilliseconds;
            switch (phase)
            {
                case "capacity": timing.Capacity = elapsed; break;
                case "segments": timing.Segments = elapsed; break;
                case "reserve": timing.Reserve = elapsed; break;
                case "prune": timing.Prune = elapsed; break;
                case "lengths": timing.Lengths = elapsed; break;
                case "budget": timing.Budget = elapsed; break;
                case "health": timing.Health = elapsed; break;
            }
            phase = next; at = now;
        }
        lock (gate)
        {
            if (timing != null) { timing.StartedQpc = started; timing.GateEnteredQpc = Stopwatch.GetTimestamp(); timing.GateWait = Stopwatch.GetElapsedTime(started, timing.GateEnteredQpc).TotalMilliseconds; at = timing.GateEnteredQpc; }
            try
            {
            if (!sessions.TryGetValue(id, out var session) || session.Retiring) return;
            Phase("capacity");
            // Qualified output claims remain owned while the synchronous export
            // can grow. Unmeasured sources retain the existing maintenance path.
            if (pendingOutput != null) RequireAcceptedCapacity(pendingSnapshot, pendingTarget);
            Phase("segments");
            var segments = ReadSegments(session);
            Phase("reserve");
            // Reserve newly closed inputs even if the save task has not resumed.
            ReserveCompleted(session, segments);
            session.History?.Offer(segments);
            Phase(null);
            if (segments.Count == 0) { Phase("budget"); RequireBudget(pendingAllocations); Phase("health"); session.Source.CheckCaptureHealth(0); Phase(null); return; }
            double newest = segments[^1].End;
            if (newest < session.LastEnd) { Phase("budget"); RequireBudget(pendingAllocations); Phase("health"); session.Source.CheckCaptureHealth(session.LastEnd); Phase(null); return; } // Partial rewrite.
            session.LastEnd = newest;
            Phase("prune");
            double threshold = newest - session.Info.ReplaySeconds - 10;
            string oldest = segments.First(s => s.End >= threshold).Path;
            session.History?.Trim(oldest);
            var pruningPins=new HashSet<string>(session.Pins,StringComparer.OrdinalIgnoreCase);
            if(session.History!=null)pruningPins.UnionWith(session.History.ActivePaths);
            if (RetainSourceNamespaceForQualificationChecks && session.PreserveEvidence) { /* Explicit bounded qualification retention; budgets/health still run below. */ }
            else if (useOwnedProductionFiles)
            {
                if (Directory.GetFiles(session.Info.Directory, "chunk-*" + session.Info.Extension)
                    .Any(path => StringComparer.OrdinalIgnoreCase.Compare(path, oldest) < 0 && !pruningPins.Contains(path)))
                    Cleanup(() => ReplayCacheFileOperations.Prune(session.Info.Directory, session.Info.Extension, oldest, pruningPins,
                        () => nativeNamespaceChanged?.Invoke()), session.Info.Directory);
            }
            else foreach (string path in Directory.GetFiles(session.Info.Directory, "chunk-*" + session.Info.Extension))
                if (StringComparer.OrdinalIgnoreCase.Compare(path, oldest) < 0 && !pruningPins.Contains(path)) Cleanup(() => files.DeleteFile(path), path);
            Phase("lengths");
            long limit = (long)((session.Info.ReplaySeconds + 30) * (session.Info.BitrateMbps * 1000000L + 192000) / 8 * 1.5);
            // Pruning is finished. One fresh native inventory supplies both EOF
            // and physical allocation for this locked operation; no observation
            // crosses a write or survives into the next maintenance call.
            // Keep custom providers' Observe semantics and count each direct
            // chunk path, including unfinished chunks and hardlink aliases.
            var inventory = useOwnedProductionFiles ? Inventory() : null;
            bool refreshBudget = false;
            if (Directory.GetFiles(session.Info.Directory, "chunk-*" + session.Info.Extension).Sum(p => {
                if (inventory != null && inventory.TryGetValue(Path.GetFullPath(p), out var extent)) return extent.Length;
                // A newly discovered chunk must not be missing from budget
                // accounting just because it appeared after the inventory.
                refreshBudget = true;
                return files.Observe(p).Length;
            }) > limit)
                throw new ReplayCacheBudgetExceededException("Replay cache exceeded its byte limit. Increase the cache budget or change recording settings explicitly.");
            Phase("budget");
            RequireBudget(pendingAllocations, observed: refreshBudget ? null : inventory);
            Phase("health");
            session.Source.CheckCaptureHealth(newest);
            Phase(null);
            }
            catch (Exception error) { if (timing != null) { timing.FailedPhase = phase; Phase(null); timing.Error = error.ToString(); } throw; }
            finally
            {
                if (timing != null)
                {
                    timing.CompletedQpc = Stopwatch.GetTimestamp(); timing.Total = Stopwatch.GetElapsedTime(started, timing.CompletedQpc).TotalMilliseconds;
                    // Observer exceptions must not replace a storage/capture failure.
                    try { observed(timing); } catch when (timing.Error != null) { }
                }
            }
        }
    }
    internal Action<ReplayMaintenanceTiming> MaintenanceObservedForChecks;
    internal int? NativeSnapshotHandleCountForChecks => snapshotInventory?.HandleCount;
    internal ReplayHistoryStatus CompletedHistoryStatusForChecks(ReplaySessionId id)
    {lock(gate)return sessions.TryGetValue(id,out var session)?session.History?.Status:null;}
    internal bool ActiveOutputOwnsResourcesForChecks { get { lock (gate) return activeOutputObservation?.HasOwnedResources == true; } }
    internal bool AuthoritySessionOwnedForChecks {get{lock(gate)return pendingAuthoritySession!=null;}}

    internal SaveSubmission TrySave(ReplaySessionId id, long triggerTimestamp, string destination)
    {
        lock (gate)
        {
            if (closing) return new("Recorder is stopping.", null);
            if (retainedNativeRequest != null) return new("Previous replay file ownership is uncertain; restart only after its producers are stopped.", null);
            if (saving) return new("A replay is already being saved.", null);
            if (pendingSource != null || pendingOutput != null || volumeReservationHeld || activeOutputObservation != null||pendingAuthoritySession!=null)
            {
                if (!PendingProducerStopped()) return new("Previous replay exporter cleanup is unconfirmed; retained storage remains reserved.", null);
                if (activeOutputObservation != null)
                {
                    try { activeOutputObservation.Dispose(); activeOutputObservation = null; }
                    catch (Exception error) when (StorageFailure(error))
                    { return new("Previous replay output observer closure is unconfirmed: " + error.Message, null); }
                }
                if (!ReleasePendingStorage()) return new("Previous replay storage release is unconfirmed; retained claims block another save.", null);
                if(pendingAuthoritySession!=null)
                    try{pendingAuthoritySession.Dispose();pendingAuthoritySession=null;}
                    catch(Exception error)when(StorageFailure(error)){return new("Previous authority closure is unconfirmed: "+error.Message,null);}
            }
            if (!sessions.TryGetValue(id, out var session) || session.Retiring)
                return new("The replay buffer is warming up or recovering.", null);
            double end = session.Source.SaveCutoffAt(triggerTimestamp);
            if (end <= 0) return new("The replay buffer is warming up or recovering.", null);
            // Admission, cutoff and the lifetime claim are atomic with retirement.
            string root = Path.GetFullPath(destination);
            string final = Path.Combine(root, "Replay-" + DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss-fff") + "-" + Guid.NewGuid().ToString("N")[..6] + ".mp4");
            string partial = final + ".partial.mp4";
            var available = ReadSegments(session);
            string snapshot = Path.Combine(session.Info.Directory, "save-" + Guid.NewGuid().ToString("N"));
            ReplayInterval intervalAtAdmission;
            long? readinessStarted = null, readinessCompleted = null;
            try
            {
                intervalAtAdmission = ReplayInterval.AtRequest(available, end, session.Info.ReplaySeconds);
                var selection = SelectInputs(session, available, end, intervalAtAdmission.Seconds);
                var output = PlanOutput(session, selection, end, root, partial, available.Count > 0 && available[^1].End >= end,
                    out var demand, out var binding, true);
                if (demand.Readiness?.AwaitingCompletedTail == true && volumeReservations != null)
                {
                    // This is one initial admission, atomic with retirement. No
                    // job, pins, allocation or ledger credit exist while waiting.
                    // Capture/manifest publication do not acquire this gate.
                    var original = binding;
                    string modelIdentity = demand.Readiness.ModelIdentity;
                    long started = Stopwatch.GetTimestamp();
                    readinessStarted = time.GetTimestamp();
                    while (demand.Readiness?.AwaitingCompletedTail == true)
                    {
                        outputDemand = demand; // Remains explicitly unmeasured.
                        if (session.Source.Stopped) throw new IOException("Capture stopped before replay admission became ready.");
                        session.Source.CheckCaptureHealth(available[^1].End);
                        if (Stopwatch.GetElapsedTime(started).TotalMilliseconds >= 200)
                            throw new IOException("Completed-segment metadata did not become eligible within the 200 ms admission deadline.");
                        Thread.Sleep(5);
                        if (Stopwatch.GetElapsedTime(started).TotalMilliseconds >= 200)
                            throw new IOException("Completed-segment metadata did not become eligible within the 200 ms admission deadline.");
                        available = ReadSegments(session);
                        if (session.Source.Stopped) throw new IOException("Capture stopped before replay admission became ready.");
                        if (ReplayInterval.AtRequest(available, end, session.Info.ReplaySeconds) != intervalAtAdmission)
                            throw new IOException("Requested replay history changed before admission became ready.");
                        session.Source.CheckCaptureHealth(available[^1].End);
                        selection = SelectInputs(session, available, end, intervalAtAdmission.Seconds);
                        output = PlanOutput(session, selection, end, root, partial, available[^1].End >= end,
                            out demand, out binding, true);
                        if (demand.Readiness?.ModelIdentity != modelIdentity
                            || binding.ExporterIdentity != original.ExporterIdentity)
                            throw new IOException("Replay calibration or exporter changed before admission became ready.");
                        foreach (var input in original.Inputs)
                            if (!binding.Inputs.Contains(input))
                                throw new IOException("Completed replay input changed before admission became ready.");
                        if (session.Source.Stopped) throw new IOException("Capture stopped before replay admission became ready.");
                        session.Source.CheckCaptureHealth(available[^1].End);
                        if (Stopwatch.GetElapsedTime(started).TotalMilliseconds >= 200)
                            throw new IOException("Completed-segment metadata did not become eligible within the 200 ms admission deadline.");
                    }
                    readinessCompleted = time.GetTimestamp();
                }
                var plan = PlanSnapshot(selection, session.Info.Extension, snapshot, out var target);
                ReserveOpenSnapshot(plan, snapshot, target, output);
                RequireBudget(plan);
                if(useRequestAuthority&&volumeReservations!=null)
                    pendingAuthoritySession=volumeReservations.BeginRequestSession();
                RequireStorageCapacity(plan, snapshot, target, output, producer: session.Source.ProducerProof);
                pendingAllocations = plan;
                pendingTarget = target;
                pendingSnapshot = snapshot;
                pendingOutput = output;
                outputDemand = demand;
                pendingSource = session.Source;
            }
            catch (Exception ex) when (StorageFailure(ex))
            {
                if(pendingAuthoritySession!=null&&PendingProducerStopped()&&ReleasePendingStorage())
                    try{pendingAuthoritySession.Dispose();pendingAuthoritySession=null;}catch(Exception cleanup){Report("Authority admission closure uncertain: "+cleanup.Message);}
                return new("Replay storage admission failed: " + ex.Message, null);
            }
            session.PendingEnd = end;
            ReserveCompleted(session, available);
            saving = true;
            var timing = new ReplaySaveTiming { Frequency = time.TimestampFrequency, Requested = triggerTimestamp, Accepted = time.GetTimestamp(),
                AdmissionReadinessStarted = readinessStarted, AdmissionReadinessCompleted = readinessCompleted };
            var completion = new TaskCompletionSource<ReplaySaveResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.Save = completion.Task;
            _ = Task.Run(async () =>
            {
                var elapsed = Stopwatch.StartNew();
                timing.WorkStarted = time.GetTimestamp();
                ReplaySaveResult result;
                try
                {
                    var saved = await SaveAsync(session, intervalAtAdmission, root, snapshot, final, partial, timing).ConfigureAwait(false);
                    result = new(true, saved.File, null, saved.Bytes, elapsed.Elapsed.TotalMilliseconds, intervalAtAdmission, saved.Validation, timing);
                }
                catch (Exception ex) { Report(ex.ToString()); result = new(false, null, ex.Message, 0, elapsed.Elapsed.TotalMilliseconds, intervalAtAdmission, Timing: timing); }
                lock (gate)
                {
                    lastSave = result;
                    session.PendingEnd = null;
                    if(timing.PreparedOwnedInputs==0 || retainedNativeRequest==null && PendingProducerStopped())
                    {session.Reserved.Clear();session.Pins.Clear();}
                    bool storageReleased=PendingProducerStopped()&&ReleasePendingStorage();
                    if(pendingAuthoritySession!=null&&storageReleased)
                    {
                        try{pendingAuthoritySession.Dispose();pendingAuthoritySession=null;}
                        catch(Exception cleanup){session.PreserveEvidence=true;Report("Authority request closure uncertain: "+cleanup.Message);}
                    }
                    saving = false;
                    timing.CompletionReady = time.GetTimestamp();
                    completion.SetResult(result);
                }
            });
            return new(null, completion.Task, outputDemand, intervalAtAdmission);
        }
    }

    internal void PreserveSession(ReplaySessionId id)
    {
        lock (gate)
            if (sessions.TryGetValue(id, out var session)) session.PreserveEvidence = true;
    }

    internal Task StopAcquisitionAsync(ReplaySessionId id)
    {
        lock (gate) return sessions.TryGetValue(id, out var session) ? Stop(session) : Task.CompletedTask;
    }

    Task Stop(Session session)
    {
        if (session.Stop != null) return session.Stop;
        // Freeze at request time, before asynchronous process draining.
        session.Source.FreezeAt(Stopwatch.GetTimestamp());
        return session.Stop = Task.Run(session.Source.StopCapture);
    }

    internal Task RetireSessionAsync(ReplaySessionId id)
    {
        lock (gate)
        {
            if (!sessions.TryGetValue(id, out var session)) return Task.CompletedTask;
            if (session.Retirement != null) return session.Retirement;
            session.Retiring = true;
            Task stop = Stop(session);
            Task save = session.Save ?? Task.CompletedTask;
            return session.Retirement = Task.Run(async () =>
            {
                // Even a stop failure cannot permit disposal while export owns the job.
                try { await Task.WhenAll(stop, save).ConfigureAwait(false); }
                finally
                {
                    try{session.History?.Dispose();}
                    catch{session.PreserveEvidence=true;throw;}
                    finally{session.Source.Dispose();}
                    lock (gate)
                    {
                        // Only namespace removal and registration release share
                        // the inventory gate; resource waits/disposal stay above.
                        if (!session.PreserveEvidence && (pendingSource != session.Source || PendingProducerStopped()))
                        {
                            Cleanup(() => files.DeleteDirectory(session.Info.Directory), session.Info.Directory);
                            sessions.Remove(id);
                        }
                    }
                }
            });
        }
    }

    internal Task CloseAsync()
    {
        lock (gate)
        {
            if (close != null) return close;
            closing = true;
            return close = CloseResources(Task.WhenAll(sessions.Keys.ToArray().Select(RetireSessionAsync)));
        }
    }

    async Task CloseResources(Task retirement)
    {
        Exception primary = null;
        var cleanup = new List<Exception>();
        try { await retirement.ConfigureAwait(false); }
        catch (Exception error) { primary = error; }
        // Waiting for a read-only observer never owns the Cache stop lock.
        // A timeout keeps its authority session and claims, rather than inferring
        // closure from the already-finished source/export producer.
        if (capacityObservation?.WaitForClosure(1000) == false)
            cleanup.Add(new IOException("Replay capacity observer closure remains unconfirmed."));
        lock (gate)
        {
            try { snapshotInventory?.Dispose(); }
            catch (Exception error) { cleanup.Add(error); }
            bool stopped = PendingProducerStopped();
            if (activeOutputObservation != null && stopped)
            {
                try { activeOutputObservation.Dispose(); activeOutputObservation = null; }
                catch (Exception error) { cleanup.Add(error); }
            }
            // A failed save may have retained claims until its producer and
            // observer actually stopped. Closing caches have no later TrySave
            // to retry that release. Never reclaim on an attempted close alone.
            if (stopped && cleanup.Count == 0 && activeOutputObservation == null)
            {
                try { if (!ReleasePendingStorage()) cleanup.Add(new IOException(reservationError)); }
                catch (Exception error) { cleanup.Add(error); }
                if(cleanup.Count==0&&pendingAuthoritySession!=null)
                    try{pendingAuthoritySession.Dispose();pendingAuthoritySession=null;}catch(Exception error){cleanup.Add(error);}
            }
        }
        foreach (var error in cleanup) Report("Replay shutdown cleanup failed: " + error);
        if (primary != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
        if (cleanup.Count != 0) throw new AggregateException("Replay shutdown ownership cleanup is unconfirmed.", cleanup);
    }

    public void Dispose()
    {
        CloseAsync().GetAwaiter().GetResult();
        lock (gate) snapshotInventory?.Dispose();
    }

    async Task<(string File, long Bytes, ReplayValidation Validation)> SaveAsync(Session session, ReplayInterval interval, string root, string snapshot, string final, string partial, ReplaySaveTiming timing)
    {
        lock (gate) capacityTiming = timing.StorageChecks ??= new();
        ReplayFiles saveFiles = files;
        ReplayRootedFiles ownedFiles = null;
        ReplaySavePreparation preparation = null;
        ReplayCompletedHistory.Snapshot preparedInputs = null;
        ReplayExportRequest acceptedExport = null;
        ReplaySaveObservation<ReplayNativeExportScope.Snapshot> earlyObservation = null;
        Action phaseCheck = null;
        Action phaseChanged = null;
        void PumpPreparation() => preparation?.Pump();
        void CheckPhase() { phaseCheck?.Invoke(); }
        void CheckPhaseAndPump() { PumpPreparation(); CheckPhase(); PumpPreparation(); }
        void ProducerChanged(ReplayProducerProof proof)
        {
            bool persist;
            lock (gate)
            {
                persist = volumeReservationHeld;
                if (persist) pendingProducerUpdates++;
            }
            try
            {
                if (persist) volumeReservations.SetProducer(volumeReservationOwner, proof, acceptedWork: true);
            }
            finally { if (persist) lock (gate) pendingProducerUpdates--; }
            lock (gate)
            {
                if (pendingSource != session.Source || !saving)
                    throw new OutputEvidenceInvalid("Replay producer seal lost accepted ownership; evidence is retained.");
                phaseChanged?.Invoke();
            }
        }
        var info = session.Info;
        double end = interval.End;
        bool retainEvidence = false;
        bool preparationUncertain = false;
        bool namespaceObservationRegistered = false;
        IDisposable completedInputMetadata = null;
        void CloseCompletedInputMetadata()
        {
            if (completedInputMetadata == null) return;
            using var trace = TraceStorage("CompletedInputMetadataClose");
            try { completedInputMetadata.Dispose(); completedInputMetadata = null; }
            catch (Exception error) { trace?.Failed(error); throw; }
        }
        try
        {
            Segment candidate = null;
            if (useOwnedProductionFiles && session.Source.SupportsOwnedExport && session.Source.SupportsStagedPreparation)
            {
                lock (gate)
                {
                    if (pendingSource != session.Source || !saving)
                        throw new OutputEvidenceInvalid("Staging lost the accepted request owner.");
                    // Unmeasured qualification training keeps the ordinary path.
                    // A capability alone is not an accepted scratch reservation.
                    if (pendingOutput?.Demand.Scratch != null && volumeReservationHeld && pendingAuthoritySession != null)
                        candidate = session.Reserved.Values.SingleOrDefault(s => s.Start <= interval.Start && s.End > interval.Start);
                    if (ReplaySavePreparation.Candidate(info, interval, candidate) != null)
                    {
                        var inventory = Inventory();
                        if (!inventory.TryGetValue(candidate.Path, out var extent) || extent.Length > 8388608) candidate = null;
                    }
                    else candidate = null;
                }
            }
            if (candidate != null)
            {
                ownedFiles = new ReplayRootedFiles(); saveFiles = ownedFiles;
                ownedFiles.Root(info.Directory); OwnedRequestCreatedForChecks?.Invoke(ownedFiles);
                ownedFiles.EnsureOutputRoot(root);
                bool ObserveEarlyGlobal()
                {
                    RequireBudget(pendingAllocations);
                    try { RequireAcceptedCapacity(snapshot, pendingTarget); }
                    catch (ReplayVolumeReservationBusyException) { return false; }
                    return true;
                }
                void ObserveEarlyConservative()
                {
                    var roots = cacheDirectories == null ? sessions.Values.Select(s => s.Info.Directory).Where(Directory.Exists) : ReplayCacheLocations.Existing(cacheDirectories);
                    _ = ObserveOutputEvidence(pendingOutput, ReplayStorage.InventoryNamespaces(roots, saveFiles));
                }
                timing.StorageChecks ??= new();
                earlyObservation = new(body => { lock (gate) body(); }, () => preparation != null,
                    () => { var observed = preparation.Observe(); nativeSnapshot = observed; try { _ = ObserveOutputEvidence(pendingOutput, new Dictionary<string, ReplayNamespaceIdentity>()); } finally { nativeSnapshot = null; } return observed; },
                    ObserveEarlyConservative,
                    observed => { nativeSnapshot = observed; try { return ObserveEarlyGlobal(); } finally { nativeSnapshot = null; } },
                    ObserveEarlyGlobal, timing.StorageChecks, completedEvidenceAt: () => capacityObservedAt);
                phaseCheck = () => earlyObservation.Check(); phaseChanged = earlyObservation.MarkChanged;
                lock (gate)
                {
                    nativeNamespaceChanged = phaseChanged;
                    namespaceObservationRegistered = true;
                }
                earlyObservation.Check(true);
                session.Source.BeginPreparation(ownedFiles, interval, candidate, partial,
                    () => { lock (gate) if (!saving || pendingSource != session.Source || pendingOutput?.Partial != partial) throw new OutputEvidenceInvalid("Staged launch lost accepted ownership."); },
                    ProducerChanged, CheckPhase, owned => preparation = owned);
                earlyObservation.Check(true); PumpPreparation();
            }
            List<Segment> selection = null;
            long started = time.GetTimestamp();
            while (time.GetElapsedTime(started).TotalSeconds < 5)
            {
                lock (gate)
                {
                    var available = ReadSegments(session);
                    ReserveCompleted(session, available);
                    // The bounded manifest may no longer list inputs reserved at
                    // admission. Keep their authoritative boundaries with the pins.
                    available = available.Concat(session.Reserved.Values).DistinctBy(s => s.Path, StringComparer.OrdinalIgnoreCase).OrderBy(s => s.Start).ToList();
                    if (session.Source.Stopped && (available.Count == 0 || available[^1].End < end))
                        throw new OutputEvidenceInvalid("Capture stopped before the original requested replay endpoint; protected footage is retained.");
                    if (available.Count > 0 && available[^1].End >= end)
                    {
                        selection = SelectInputs(session, available, end, interval.Seconds);
                        if (selection.Count == 0) throw new IOException("No replay frames are available yet.");
                        interval.RequireCoverage(selection);
                        session.PendingEnd = null;
                        selection = selection.Select(s => new Segment { Path = s.Path, Start = s.Start, End = s.End }).ToList();
                        foreach (var segment in selection) session.Pins.Add(segment.Path);
                        if (reuseCompletedInputMetadata)
                            completedInputMetadata = snapshotInventory.LeaseCompletedInputs(selection.Select(s => s.Path));
                        break;
                    }
                }
                if (preparation == null) await waitForSegment().ConfigureAwait(false);
                else
                {
                    var waiting = waitForSegment();
                    ReplaySavePreparation.WaitForTail(waiting, time, started, CheckPhaseAndPump, () => session.Source.Diagnostics);
                }
            }
            if (selection == null) throw new IOException("Capture did not finish the current segment in time. " + session.Source.Diagnostics);
            timing.TailClosed = time.GetTimestamp();
            // Preserve the original tail deadline and overlap startup with it.
            // Materialization waits for the launch worker's job/ledger seal,
            // pumping guards outside the cache stop lock.
            preparation?.WaitForReady();
            timing.ProtectionStarted ??= time.GetTimestamp();
            // Borrowing pinned history cannot allocate snapshot files. The accepted
            // observer already checks the live budget, claimed capacity and output.
            // Rebind the full claim once the selected inputs and output are known.
            void CheckHistoryCapacity()
            {
                if (earlyObservation != null) CheckPhaseAndPump();
                else lock (gate) RequireKnownInputCapacity(pendingAllocations, snapshot, pendingTarget);
            }
            preparedInputs = session.History?.Acquire(selection, CheckHistoryCapacity);
            timing.PreparedOwnedInputs=preparedInputs?.Count??0;
            lock (gate)
            {
                var plan = PlanSnapshot(selection, info.Extension, snapshot, out var target, PumpPreparation);
                RequireTarget(pendingTarget, target);
                var output = PlanOutput(session, selection, end, root, partial, true, out var demand);
                PumpPreparation();
                var rebindInventory = Inventory();
                RequireBudget(plan, observed: rebindInventory);
                RequireStorageCapacity(plan, snapshot, target, output, rebindInventory);
                pendingAllocations = plan;
                pendingOutput = output;
                outputDemand = demand;
                if (useOwnedProductionFiles && session.Source.SupportsOwnedExport)
                {
                    timing.StorageChecks ??= new();
                    var setupObservation = new ReplayStorageObservationGate(() =>
                    {
                        RequireKnownInputCapacity(pendingAllocations, snapshot, pendingTarget);
                        return true;
                    }, 100, ReplayStorageCoordinator.MaximumObservationGapMilliseconds, timing.StorageChecks,
                        error: message => new OutputEvidenceInvalid(message));
                    PumpPreparation();
                    setupObservation.CheckNow();
                    void CheckSetup()
                    {
                        // No payload is allocated while admitting these input
                        // capabilities. Use the already-bound budget and volume
                        // observer between full rebind and final admission.
                        // Running two independent inventory clocks here can
                        // make each scan immediately trigger the other.
                        if (earlyObservation != null) CheckPhaseAndPump();
                        else setupObservation.Check();
                    }
                    ownedFiles ??= new ReplayRootedFiles();
                    ownedFiles.Root(info.Directory);
                    if (preparation == null) OwnedRequestCreatedForChecks?.Invoke(ownedFiles);
                    CheckSetup();
                    ownedFiles.EnsureOutputRoot(root);
                    CheckSetup();
                    var originalsInventory = Inventory();
                    CheckSetup();
                    for (int i = 0; i < selection.Count; i++)
                    {
                        CheckSetup();
                        var input = selection[i];
                        if (!originalsInventory.TryGetValue(input.Path, out var expected))
                            throw new OutputEvidenceInvalid("Completed source is absent from the fresh protection inventory.");
                        if(preparedInputs?.Contains(input.Path)==true)
                            ownedFiles.AdmitPreparedInput(input.Path,expected,preparedInputs);
                        else ownedFiles.Admit(input.Path, expected, false);
                        CheckSetup();
                    }
                    setupObservation.CheckNow();
                    saveFiles = ownedFiles;
                }
            }
            CheckPhaseAndPump();
            saveFiles.CreateDirectory(snapshot);
            var originals = selection.Select(s => s.Path).ToArray();
            timing.ProtectionStarted ??= time.GetTimestamp();
            try
            {
                // Slow copies never hold the cache lock. Pruning honors every pin.
                long snapshotObserved = Stopwatch.GetTimestamp();
                for (int i = 0; i < selection.Count; i++)
                {
                    if (preparation != null) CheckPhaseAndPump();
                    string copy = Path.Combine(snapshot, i.ToString("D6") + info.Extension);
                    if(preparedInputs?.Contains(originals[i])==true)
                    {
                        lock(gate)pendingAllocations.Remove(copy);
                        continue; // Stable original pathname remains pinned by the accepted owner.
                    }
                    if (i == 0 || Stopwatch.GetElapsedTime(snapshotObserved).TotalMilliseconds >= 200)
                    {
                        lock (gate) RequireKnownInputCapacity(pendingAllocations, snapshot, pendingTarget);
                        snapshotObserved = Stopwatch.GetTimestamp();
                    }
                    long linkStarted = time.GetTimestamp();
                    bool linked = saveFiles.TryHardLink(copy, originals[i]);
                    timing.HardLinkMilliseconds += time.GetElapsedTime(linkStarted).TotalMilliseconds;
                    if (linked) timing.HardLinkedInputs++;
                    if (!linked)
                    {
                        lock (gate)
                        {
                            // Copy growth still needs a fresh observation before
                            // every copy, including a failed hard-link fallback.
                            RequireKnownInputCapacity(pendingAllocations, snapshot, pendingTarget);
                            var plan = new Dictionary<string, long>(pendingAllocations, StringComparer.OrdinalIgnoreCase);
                            plan[copy] = AllocationBytes(ReplayStorage.Validate(saveFiles.Observe(originals[i])).Length, pendingTarget.AllocationUnit);
                            var inventory = Inventory();
                            RequireBudget(plan, observed: inventory);
                            RequireStorageCapacity(plan, snapshot, pendingTarget, pendingOutput, inventory);
                            pendingAllocations = plan;
                        }
                        long copyStarted = time.GetTimestamp();
                        saveFiles.Copy(originals[i], copy);
                        timing.CopyMilliseconds += time.GetElapsedTime(copyStarted).TotalMilliseconds;
                        timing.CopiedInputs++;
                        lock (gate) RequireKnownInputCapacity(pendingAllocations, snapshot, pendingTarget);
                        snapshotObserved = Stopwatch.GetTimestamp();
                    }
                    lock (gate)
                    {
                        // A successful hard link allocates no second payload.
                        // Discharge its copy claim; periodic/final observations
                        // still account real inventory and concurrent disk use.
                        pendingAllocations.Remove(copy);
                    }
                    selection[i].Path = copy;
                }
            }
            finally { if(preparedInputs==null)lock (gate) { session.Pins.Clear(); session.Reserved.Clear(); } }
            saveFiles.CreateDirectory(root);
            string concat = Path.Combine(snapshot, "concat.txt");
            lock (gate)
            {
                PumpPreparation();
                var inventory = Inventory();
                RequireBudget(pendingAllocations, observed: inventory);
                RequireStorageCapacity(pendingAllocations, snapshot, pendingTarget, pendingOutput, inventory);
            }
            PumpPreparation();
            string concatText = ConcatText(selection, info.Extension);
            if (pendingOutput != null && Encoding.UTF8.GetByteCount(concatText) > pendingOutput.Demand.ConcatMaximumBytes)
                throw new OutputEvidenceInvalid("Replay concat metadata exceeds its reserved reference envelope.");
            saveFiles.WriteText(concat, concatText);
            lock (gate) pendingAllocations.Remove(concat);
            var exportInputs = new ReplayOutputInput[selection.Count];
            for (int i = 0; i < selection.Count; i++)
            {
                PumpPreparation();
                var segment = selection[i];
                exportInputs[i] = new(segment.Path, segment.Start, segment.End, ReplayStorage.Validate(saveFiles.Observe(segment.Path)));
            }
            var exportRequest = new ReplayExportRequest(info, interval, Array.AsReadOnly(exportInputs), concat, partial, timing);
            acceptedExport=exportRequest;
            exportRequest.PreparedInputs=preparedInputs;
            exportRequest.Preparation = preparation;
            exportRequest.ConcatSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(concatText)));
            if (saveFiles is ReplayRootedFiles rooted)
            {
                if (rooted.Active != null) throw new OutputEvidenceInvalid("Prior native request still owns uncertain resources.");
                exportRequest.NativeFiles = rooted.Active = new(rooted, exportRequest);
                preparation?.BindRequest(exportRequest);
            }
            try
            {
                lock (gate)
                {
                    // Copies have new file identities. Bind the evidence to the
                    // actual immutable snapshot consumed by this export.
                    var output = PlanOutput(session, selection, end, root, partial, true, out var demand);
                    snapshotInventory?.Seal(snapshot);
                    PumpPreparation();
                    RequireStorageCapacity(pendingAllocations, snapshot, pendingTarget, output);
                    pendingOutput = output;
                    outputDemand = demand;
                    if (useRequestAuthority && volumeReservations != null && output != null && exportRequest.NativeFiles != null)
                        exportRequest.NativeFiles.RegisterReservationObservation(volumeReservations, volumeReservationOwner,
                            demand.PeakAllocatedBytes - (demand.Scratch?.Total ?? 0));
                    if (saveFiles is not ReplayRootedFiles && reuseActiveOutputMetadata && output != null)
                    {
                        if (activeOutputObservation != null) throw new OutputEvidenceInvalid("Previous output observer still owns accepted work.");
                        activeOutputObservation = new(saveFiles, partial);
                        ActiveOutputCreatedForChecks?.Invoke(activeOutputObservation);
                    }
                }
                timing.StorageChecks ??= new();
                bool ObserveExportStorage()
                {
                    using var trace = TraceStorage("FullObservation");
                    try
                    {
                    trace?.Phase("Capacity");
                    try { RequireAcceptedCapacity(snapshot, pendingTarget); }
                    catch (ReplayVolumeReservationBusyException) { return false; }
                    return true;
                    }
                    catch (Exception error) { trace?.Failed(error); throw; }
                }
                var storageObservation = new ReplayStorageObservationGate(ObserveExportStorage, 100,
                    ReplayStorageCoordinator.MaximumObservationGapMilliseconds, timing.StorageChecks,
                    error: message => new OutputEvidenceInvalid(message), completedEvidenceAt: () => capacityObservedAt);
                void ConservativeLocal()
                {
                    var roots = cacheDirectories == null ? sessions.Values.Select(s => s.Info.Directory).Where(Directory.Exists) : ReplayCacheLocations.Existing(cacheDirectories);
                    _ = ObserveOutputEvidence(pendingOutput, ReplayStorage.InventoryNamespaces(roots, saveFiles));
                }
                ReplaySaveObservation<ReplayNativeExportScope.Snapshot> nativeObservation = null;
                if (exportRequest.NativeFiles != null)
                    nativeObservation = new(body => { lock (gate) body(); }, () => exportRequest.NativeFiles.Eligible,
                        () => { var snapshot = exportRequest.NativeFiles.Observe(); nativeSnapshot = snapshot; try { _ = ObserveOutputEvidence(pendingOutput, new Dictionary<string, ReplayNamespaceIdentity>()); } finally { nativeSnapshot = null; } return snapshot; },
                        ConservativeLocal,
                        snapshot => { nativeSnapshot = snapshot; try { return ObserveExportStorage(); } finally { nativeSnapshot = null; } },
                        ObserveExportStorage, timing.StorageChecks, completedEvidenceAt: () => capacityObservedAt);
                if (nativeObservation != null) lock (gate)
                {
                    nativeNamespaceChanged = nativeObservation.MarkChanged;
                    namespaceObservationRegistered = true;
                }
                void CheckExportStorageCore(bool force)
                {
                    if (pendingOutput == null && !volumeReservationHeld) return;
                    long waitStarted = Stopwatch.GetTimestamp();
                    lock (gate)
                    {
                        using var trace = TraceStorage(force ? "ForcedCallback" : "Callback", waitStarted);
                        try
                        {
                        trace?.AddPhase("GateWait", waitStarted, Stopwatch.GetTimestamp());
                        double wait = Stopwatch.GetElapsedTime(waitStarted).TotalMilliseconds;
                        timing.StorageChecks.GateWaitMilliseconds += wait;
                        timing.StorageChecks.MaximumGateWaitMilliseconds = Math.Max(timing.StorageChecks.MaximumGateWaitMilliseconds, wait);
                        if (nativeObservation != null)
                        {
                            try { nativeObservation.Check(force); }
                            catch (Exception error) when (StorageFailure(error)) { throw new OutputEvidenceInvalid("Native replay observation failed; evidence is retained: " + error.Message); }
                            return;
                        }
                        storageObservation.RequireFreshness();
                        trace?.Phase("Namespace");
                        // Namespace/identity/peak checks remain per callback.
                        // Enumeration identities never supply allocation credit.
                        long started = Stopwatch.GetTimestamp();
                        try
                        {
                            if (pendingOutput != null)
                            {
                                var roots = cacheDirectories == null
                                    ? sessions.Values.Select(s => s.Info.Directory).Where(Directory.Exists)
                                    : ReplayCacheLocations.Existing(cacheDirectories);
                                var namespaces = ReplayStorage.InventoryNamespaces(roots, saveFiles);
                                _ = ObserveOutputEvidence(pendingOutput, namespaces);
                            }
                        }
                        catch (Exception error) when (StorageFailure(error) && error is not OutputEvidenceInvalid)
                        { throw new OutputEvidenceInvalid("Replay namespace observation is uncertain; output and snapshot are retained: " + error.Message); }
                        finally
                        {
                            double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                            timing.StorageChecks.NamespaceMilliseconds += elapsed;
                            timing.StorageChecks.MaximumNamespaceMilliseconds = Math.Max(timing.StorageChecks.MaximumNamespaceMilliseconds, elapsed);
                        }
                        trace?.Phase("ObservationGate");
                        if (force) storageObservation.CheckNow(); else storageObservation.Check();
                        }
                        catch (Exception error) { trace?.Failed(error); throw; }
                    }
                }
                void CheckExportStorage() { PumpPreparation(); CheckExportStorageCore(false); PumpPreparation(); }
                void CheckExportStorageNow() => CheckExportStorageCore(true);
                exportRequest.NativeFiles?.BindBarrier(CheckExportStorageNow);
                PumpPreparation();
                if (earlyObservation != null) earlyObservation.Check(true);
                PumpPreparation();
                CheckExportStorageNow();
                earlyObservation?.RequireFreshness();
                phaseCheck = () => CheckExportStorageCore(false);
                phaseChanged = () => { if (nativeObservation != null) nativeObservation.MarkChanged(); else storageObservation.MarkChanged(); };
                timing.Protected = time.GetTimestamp();
                bool fallbackMaterializationStarted=false;
                if(preparedInputs!=null)exportRequest.MaterializePreparedFallback=()=>
                {
                    if(fallbackMaterializationStarted)throw new IOException("Prepared fallback materialization is single-use.");
                    fallbackMaterializationStarted=true;
                    long started=Stopwatch.GetTimestamp();
                    try
                    {
                        for(int n=0;n<originals.Length;n++)
                        {
                            if(!preparedInputs.Contains(originals[n]))continue;
                            CheckExportStorage();
                            string copy=Path.Combine(snapshot,n.ToString("D6")+info.Extension);
                            var sourceExtent=preparedInputs.Extent(originals[n]);
                            lock(gate)
                            {
                                var plan=new Dictionary<string,long>(pendingAllocations,StringComparer.OrdinalIgnoreCase)
                                    {[copy]=AllocationBytes(sourceExtent.Length,pendingTarget.AllocationUnit)};
                                RequireBudget(plan);
                                RequireStorageCapacity(plan,snapshot,pendingTarget,pendingOutput);
                                pendingAllocations=plan;
                            }
                            bool linked=saveFiles.TryHardLink(copy,originals[n]);
                            if(linked)timing.HardLinkedInputs++;
                            else
                            {
                                saveFiles.Copy(originals[n],copy);timing.CopiedInputs++;
                            }
                            var alias=exportRequest.NativeFiles.OpenInput(copy,exportRequest.NativeFiles.Extent(copy),true);
                            exportRequest.SourceProtection.Add(alias);exportRequest.Closure.SourceOpened();
                            if(!linked)
                            {
                                using var original=exportRequest.NativeFiles.OpenInput(originals[n],sourceExtent,true);
                                // This is fallback-only copy evidence. Neither
                                // cached index bytes nor path equality prove it.
                                byte[] Hash(Stream source)
                                {
                                    using var hash=System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
                                    byte[] block=System.Buffers.ArrayPool<byte>.Shared.Rent(65536);
                                    var guard=new ReplayPlanningGuard(CheckExportStorage);
                                    long total=0;
                                    try
                                    {
                                        guard.CheckNow();
                                        int read;
                                        while((read=source.Read(block,0,block.Length))!=0)
                                        {hash.AppendData(block,0,read);total=checked(total+read);if(total>sourceExtent.Length)throw new IOException("Prepared fallback copy grew.");guard.Check();}
                                        guard.CheckNow();
                                        if(total!=sourceExtent.Length)throw new IOException("Prepared fallback copy was truncated.");
                                        return hash.GetHashAndReset();
                                    }
                                    finally{System.Buffers.ArrayPool<byte>.Shared.Return(block);}
                                }
                                byte[] expected=Hash(original);
                                byte[] actual=Hash(alias);
                                if(!expected.AsSpan().SequenceEqual(actual))throw new IOException("Prepared fallback copy differs from its owned source.");
                                alias.Position=0;
                            }
                            lock(gate)pendingAllocations.Remove(copy);
                            timing.PreparedFallbackInputs++;
                            phaseChanged?.Invoke();CheckExportStorage();
                        }
                    }
                    finally{timing.PreparedFallbackMilliseconds+=Stopwatch.GetElapsedTime(started).TotalMilliseconds;}
                };
                timing.ExportStarted = time.GetTimestamp();
                session.Source.Export(exportRequest, snapshot, ProducerChanged, CheckExportStorage);
                if (exportRequest.NativeFiles != null)
                {
                    exportRequest.NativeFiles.CompleteExport();
                    exportRequest.NativeFiles.AttachLease();
                }
                timing.ExportCompleted = time.GetTimestamp();
                if(useRequestAuthority&&completedInputMetadata!=null)
                {
                    if(!PendingProducerStopped()||!exportRequest.Closure.SourcesClosed||!exportRequest.IndexReaders.Joined||!exportRequest.FallbackIndexReaders.Joined
                        ||!exportRequest.PacketReaders.Joined||exportRequest.NativeFiles!=null&&!exportRequest.NativeFiles.CanDispose)
                        throw new IOException("Source readers have not joined before metadata handoff.");
                    lock(gate)CloseCompletedInputMetadata();
                }
                CheckExportStorageNow();
                if (!PendingProducerStopped()) throw new IOException("Replay exporter cleanup is unconfirmed; output and inputs are retained.");
                if (pendingOutput != null) lock (gate) RequireKnownInputCapacity(pendingAllocations, snapshot, pendingTarget);
                if (!File.Exists(partial) || new FileInfo(partial).Length < 1000) throw new IOException("Replay save failed: output is missing or incomplete.");
                timing.ValidationStarted = time.GetTimestamp();
                var validation = session.Source.ValidateOutput(exportRequest, ProducerChanged, CheckExportStorage);
                exportRequest.NativeFiles?.BindDecodedPublication(exportRequest.ValidatedOutput);
                timing.ValidationCompleted = time.GetTimestamp();
                if ((ReplaySaveExport.IsHybridRecipe(exportRequest.Recipe) || exportRequest.Recipe == ReplayExactExport.Recipe)
                    && exportRequest.ValidatedOutput == null)
                    throw new IOException("Replay validation did not retain its checked-output publication lease.");
                CheckExportStorageNow();
                if (!PendingProducerStopped()) throw new IOException("Replay validator cleanup is unconfirmed; output and inputs are retained.");
                if (activeOutputObservation != null)
                {
                    timing.OutputObserverCloseStarted = time.GetTimestamp();
                    lock (gate) activeOutputObservation.Complete(exportRequest.ValidatedOutput);
                    timing.OutputObserverCloseCompleted = time.GetTimestamp();
                    CheckExportStorageNow(); // Fresh actual allocation AFTER observer closure.
                }
                if (completedInputMetadata != null)
                {
                    // Close the experimental observer before the publication
                    // point, then refresh actual storage after those closes.
                    lock (gate) CloseCompletedInputMetadata();
                    CheckExportStorageNow();
                }
                long bytes = new FileInfo(partial).Length;
                // Join the one read-only capacity query before releasing its
                // authority session/claims or mutating publication paths. While
                // waiting, normal callbacks retain their original cover/deadline.
                capacityQuiescing = true;
                while (capacityObservation?.Running == true)
                {
                    CheckExportStorageNow();
                    // Staged capabilities must close on their original Save
                    // owner. This Save already runs off the UI thread; keep
                    // bounded capacity waits outside the cache gate without
                    // moving its final publication/cleanup to another thread.
                    if (preparation != null) Thread.Sleep(5);
                    else await (CapacityDelayForChecks?.Invoke() ?? Task.Delay(5)).ConfigureAwait(false);
                }
                CheckExportStorageNow();
                // Recheck the final immutable binding and current qualification,
                // even for a save completed inside one sampling interval.
                long requiredPolicyAt = Stopwatch.GetTimestamp();
                capacityRequiredPolicyAt = requiredPolicyAt;
                capacityQuiescing = false;
                if (volumeReservations != null && volumeReservationHeld)
                    while (capacityLastPolicyAt < requiredPolicyAt)
                    {
                        capacityRequireNext = true;
                        CheckExportStorageNow();
                        if (preparation != null) Thread.Sleep(5);
                        else await (CapacityDelayForChecks?.Invoke() ?? Task.Delay(5)).ConfigureAwait(false);
                    }
                capacityQuiescing = true;
                capacityRequiredPolicyAt = 0;
                // Export/finalization is over. End durable growth ownership before
                // rename/cleanup can remove paths another profile still validates.
                // If release fails, retain inspectable inputs/partial and report
                // failure before publication, never a successful moved stale claim.
                bool requiresPublicationFreshness = useRequestAuthority && (pendingOutput != null || volumeReservationHeld);
                timing.ReleaseStarted = time.GetTimestamp();
                if (volumeReservationHeld || pendingOutput != null)
                    lock (gate)
                        if (!ReleasePendingStorage()) throw new OutputEvidenceInvalid("Replay storage release failed before publication; partial output and snapshot are retained.");
                if(pendingAuthoritySession!=null)lock(gate)
                {
                    if(!PendingProducerStopped())throw new OutputEvidenceInvalid("Authority session still has a producer before publication.");
                    pendingAuthoritySession.Dispose();pendingAuthoritySession=null;
                }
                if(requiresPublicationFreshness)
                {
                    exportRequest.RequirePublicationFreshness = nativeObservation != null
                        ? nativeObservation.RequireFreshness : storageObservation.RequireFreshness;
                    exportRequest.RequirePublicationFreshness();
                }
                timing.Released = time.GetTimestamp();
                timing.PublicationStarted = time.GetTimestamp();
                // Prepare diagnostics before the success point; no allocation
                // or new failure decision may reverse an already-published file.
                var identityBoundValidation = exportRequest.ValidatedOutput == null ? validation : validation with { PublicationIdentityBound = true };
                try
                {
                    if (saveFiles is ReplayRootedFiles)
                    {
                        lock (gate)
                            if (SerializeNamespaceMutationForChecks == null) saveFiles.Publish(exportRequest, final);
                            else SerializeNamespaceMutationForChecks(() => saveFiles.Publish(exportRequest, final));
                    }
                    else saveFiles.Publish(exportRequest, final);
                }
                catch (Exception error) when (exportRequest.ValidatedOutput != null)
                {
                    // A changed namespace may contain another owner's file.
                    // Retain the checked object and current paths for review;
                    // ordinary partial cleanup must not unlink a substitute.
                    throw new OutputEvidenceInvalid("Validated replay publication failed; output identities are retained: " + error.Message);
                }
                timing.Published = time.GetTimestamp();
                if (exportRequest.ValidatedOutput?.Published == true) validation = identityBoundValidation;
                // Publication is the success point. Logging/cleanup must not reverse it.
                Report("Saved " + final + "; requested end=" + end.ToString(CultureInfo.InvariantCulture));
                return (final, bytes, validation);
            }
            catch (Exception ex) when (RequiresSaveEvidenceRetention(ex))
            { retainEvidence = true; throw; }
            finally
            {
                bool mayCloseNativeObservers = true;
                bool deferredKnownClose = false;
                if (exportRequest.NativeFiles?.HasReservationObservation == true && volumeReservationHeld)
                {
                    lock (gate)
                    {
                        bool stopped = PendingProducerStopped() && exportRequest.NativeFiles.CanDispose;
                        mayCloseNativeObservers = stopped && ReleasePendingStorage();
                        if (stopped && !mayCloseNativeObservers)
                        {
                            deferredNativeClose = new(exportRequest, pendingSource, (ReplayRootedFiles)saveFiles,
                                ReferenceEquals(ownedFiles, saveFiles), volumeReservationOwner);
                            deferredKnownClose = true;
                        }
                    }
                    if (!mayCloseNativeObservers) { retainEvidence = true; session.PreserveEvidence = true; }
                }
                // Custom/unmeasured paths have no output observer. Their
                // partial-file cleanup must remain independent of a concurrent
                // snapshot inventory holding the cache gate.
                if (activeOutputObservation != null) lock (gate)
                {
                    if (activeOutputObservation != null && PendingProducerStopped())
                    {
                        try { activeOutputObservation.Dispose(); activeOutputObservation = null; }
                        catch (Exception error)
                        {
                            retainEvidence = true; session.PreserveEvidence = true;
                            Report("Replay output metadata observer cleanup failed: " + error.Message);
                        }
                    }
                }
                if (exportRequest.ValidatedOutput != null && mayCloseNativeObservers)
                {
                    timing.ValidationLeaseCloseStarted = time.GetTimestamp();
                    try { exportRequest.ValidatedOutput.Dispose(); }
                    catch (Exception error) { retainEvidence = true; session.PreserveEvidence = true; Report("Replay checked lease closure uncertain: " + error.Message); }
                    finally { timing.ValidationLeaseCloseCompleted = time.GetTimestamp(); }
                }
                if (exportRequest.NativeFiles != null)
                {
                    if (mayCloseNativeObservers && PendingProducerStopped() && exportRequest.NativeFiles.CanDispose)
                    {
                        try { exportRequest.NativeFiles.Dispose(); }
                        catch (Exception error) { retainEvidence = true; session.PreserveEvidence = true; Report("Native scope closure uncertain: " + error.Message); }
                    }
                    else { if (!deferredKnownClose) exportRequest.NativeFiles.RetainUncertain(); retainEvidence = true; session.PreserveEvidence = true; }
                }
                // Detach actual registrations under the gate. A custom path
                // without one must keep partial cleanup independent of inventory.
                if (namespaceObservationRegistered) lock (gate) nativeNamespaceChanged = null;
                if (saveFiles is ReplayRootedFiles nativeFiles && exportRequest.NativeFiles.Closed) nativeFiles.Active = null;
                if (!retainEvidence && PendingProducerStopped())
                {
                    if (!Cleanup(() => saveFiles.DeleteFile(partial), partial)) { retainEvidence = true; session.PreserveEvidence = true; }
                    if (exportRequest.OwnWorkDirectory && !Cleanup(() => saveFiles.DeleteDirectory(exportRequest.WorkDirectory, exportRequest.Partial), exportRequest.WorkDirectory))
                    { retainEvidence = true; session.PreserveEvidence = true; }
                }
            }
        }
        catch (Exception ex) when (RequiresSaveEvidenceRetention(ex))
        {
            // A durable holder can still name these paths after a failed update
            // or release. Do not turn a local failure into missing global anchors.
            retainEvidence = true;
            lock (gate) session.PreserveEvidence = true;
            throw;
        }
        // Inventory and removal own the same namespace. Export and copying have
        // already finished; no task/resource wait occurs while holding the gate.
        finally
        {
            if (preparation != null)
            {
                try { preparation.Dispose(); }
                catch (Exception error)
                {
                    preparationUncertain = true; retainEvidence = true; session.PreserveEvidence = true;
                    ownedFiles?.RetainDependent(new object[] { preparation, completedInputMetadata, session.Source });
                    retainedNativeRequest = ownedFiles;
                    Report("Staged preparation closure uncertain: " + error.Message);
                }
            }
            if(preparedInputs!=null && acceptedExport?.SourceProtection==null)
            {
                // Export never acquired a child/reader owner. Otherwise its
                // SourceProtection (including unknown-exit retention) owns refs.
                if(!preparationUncertain)preparedInputs.Dispose();
                else ownedFiles?.RetainDependent(preparedInputs);
            }
            lock (gate)
            {
                // Failure cleanup cannot mask the original error. Success
                // already closed these observers before publication above.
                try { if (!preparationUncertain) CloseCompletedInputMetadata(); }
                catch (Exception error)
                {
                    retainEvidence = true; session.PreserveEvidence = true;
                    Report("Replay input metadata observer cleanup failed: " + error.Message);
                }
                if (!retainEvidence && !session.PreserveEvidence && PendingProducerStopped()
                    && !Cleanup(() => saveFiles.DeleteDirectory(snapshot), snapshot)) session.PreserveEvidence = true;
                if (ownedFiles != null && !preparationUncertain && !ReferenceEquals(deferredNativeClose?.Files, ownedFiles))
                {
                    try
                    {
                        if (ownedFiles.Active is { Closed: false }) throw new IOException("Native request users have not closed.");
                        ownedFiles.Dispose();
                    }
                    catch (Exception error)
                    {
                        ownedFiles.RetainUncertain(); retainedNativeRequest = ownedFiles;
                        session.PreserveEvidence = true;
                        Report("Replay operation registry remains retained: " + error.Message);
                    }
                }
                nativeNamespaceChanged = null;
            }
        }
    }

    bool Cleanup(Action action, string path)
    {
        try
        {
            // Directory retirement already owns the inventory gate at its call
            // sites. Partial-output unlinking must keep its independent path.
            if (SerializeNamespaceMutationForChecks == null)
            {
                if (snapshotInventory != null && Directory.Exists(path))
                    lock (gate) snapshotInventory.ReleaseUnder(path);
                action();
            }
            else lock (gate) SerializeNamespaceMutationForChecks(() =>
            {
                if (snapshotInventory != null && Directory.Exists(path)) snapshotInventory.ReleaseUnder(path);
                action();
            });
            return true;
        }
        catch (IOException ex) { Report("Replay cleanup failed for " + path + ": " + ex.Message); }
        catch (UnauthorizedAccessException ex) { Report("Replay cleanup failed for " + path + ": " + ex.Message); }
        catch (System.ComponentModel.Win32Exception ex) { Report("Replay cleanup failed for " + path + ": " + ex.Message); }
        return false;
    }
    void Report(string message) { try { diagnostic(message); } catch (Exception) { /* Diagnostics cannot own save completion. */ } }
}
