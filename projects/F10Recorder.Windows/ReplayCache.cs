using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace F10Recorder;

internal readonly record struct ReplaySessionId(Guid Value);
internal sealed record ReplaySessionInfo(string Directory, string Manifest, int ReplaySeconds,
    int BitrateMbps, int FramesPerSecond, string Codec, string Extension, bool TimestampedAudio, bool RecordAudio);

// This seam supplies capture state and process lifetime, not save ordering.
internal interface IReplaySessionSource : IDisposable
{
    ReplaySessionInfo ReplayInfo { get; }
    bool Stopped { get; }
    string Diagnostics { get; }
    double SaveCutoffAt(long triggerTimestamp);
    void FreezeAt(long stopTimestamp);
    void StopCapture();
    void CheckCaptureHealth(double completedEnd);
    List<Segment> ReadSegments() => ReplaySelection.Read(ReplayInfo.Manifest, ReplayInfo.Directory);
    string OutputDemandIdentity => null;
    ReplayOutputDemand OutputDemandStatus => ReplayOutputDemand.Unmeasured("Output/finalization calibration is not available for this source.");
    // Numeric providers must report absence from their owned process handle,
    // including failure paths. An opaque exception is not a cleanup receipt.
    bool OutputProducerStopped => false;
    ReplayProducerProof ProducerProof => null;
    ReplayOutputDemand EstimateOutput(ReplayOutputSelection selection) => ReplayOutputDemand.Unmeasured("Output/finalization calibration is not available for this source.");
    void Export(IReadOnlyList<string> arguments, string snapshot);
    void Export(IReadOnlyList<string> arguments, string snapshot, Action<ReplayProducerProof> producerChanged) => Export(arguments, snapshot);
    void Export(IReadOnlyList<string> arguments, string snapshot, Action<ReplayProducerProof> producerChanged, Action checkStorage)
    {
        checkStorage?.Invoke();
        Export(arguments, snapshot, producerChanged);
        checkStorage?.Invoke();
    }
}

internal sealed record ReplaySaveResult(bool Succeeded, string File, string Error, long Bytes, double Milliseconds);
internal sealed record SaveSubmission(string Rejection, Task<ReplaySaveResult> Completion, ReplayOutputDemand AdmissionDemand = null)
{
    internal bool Accepted => Completion != null;
}
internal sealed record ReplayCacheStatus(bool Saving, ReplaySaveResult LastSave, long? CacheBytes, string StorageError,
    ReplayOutputDemand OutputDemand = null);
internal sealed record ReplaySessionStatus(double BufferedSeconds, long CacheBytes);
internal sealed class ReplayCacheBudgetExceededException(string message) : IOException(message);

// Only operations whose failures must be controlled in ownership checks vary.
internal class ReplayFiles
{
    internal virtual ReplayFileExtent Observe(string path) => ReplayStorage.Observe(path);
    internal virtual ReplayFileExtent ObserveInventory(string path, ReplayFileExtent enumerated) => ReplayStorage.ObserveInventory(path, enumerated);
    internal virtual ReplayFileExtent ObserveManifest(string path, ReplayFileExtent enumerated) => enumerated;
    internal virtual ReplayVolumeCapacity ObserveCapacity(string directory) => ReplayStorage.ObserveCapacity(directory);
    internal virtual bool TryHardLink(string copy, string original) => Native.CreateHardLink(copy, original, IntPtr.Zero);
    internal virtual void Copy(string original, string copy) => System.IO.File.Copy(original, copy);
    internal virtual void Publish(string partial, string final) => System.IO.File.Move(partial, final);
    internal virtual void DeleteFile(string path) => System.IO.File.Delete(path);
    internal virtual void DeleteDirectory(string path) => System.IO.Directory.Delete(path, true);
}

// One instance belongs to one protected recording profile. It owns registered
// sources, accepted saves, and the point at which session resources can be freed.
internal sealed class ReplayCache : IDisposable
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
        internal double LastEnd;
        internal long Bytes, BytesAt;
    }

    readonly object gate = new();
    readonly Dictionary<ReplaySessionId, Session> sessions = new();
    readonly ReplayFiles files;
    readonly ReplaySnapshotInventory snapshotInventory;
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
    }
    sealed class OutputEvidenceInvalid(string message) : IOException(message);
    OutputClaim pendingOutput;
    ReplayOutputDemand outputDemand = ReplayOutputDemand.Unmeasured("Output/finalization calibration has not been established.");
    bool closing, saving;
    ReplaySaveResult lastSave;
    Task close;

    internal ReplayCache(ReplayFiles files = null, TimeProvider time = null,
        Func<Task> waitForSegment = null, Action<string> diagnostic = null, string cacheDirectory = null, long cacheBudgetBytes = 10_000_000_000, IEnumerable<string> retainedDirectories = null, ReplayVolumeReservations volumeReservations = null)
    {
        this.files = files ?? new ReplayFiles();
        // Fault-injecting/custom filesystem providers retain their own exact
        // observation semantics. The maintained native path owns this reader.
        if (this.files.GetType() == typeof(ReplayFiles)) snapshotInventory = new();
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
            sessions.Add(id, new Session(source));
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
                try { return new(saving, lastSave, ReplayStorage.Bytes(Inventory()), reservationError, outputDemand); }
                catch (Exception ex) when (StorageFailure(ex)) { return new(saving, lastSave, null, ex.Message, outputDemand); }
            }
        }
    }
    internal bool IsSaving { get { lock (gate) return saving; } }
    static bool StorageFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or OverflowException;
    Dictionary<string, ReplayFileExtent> Inventory()
    {
        snapshotInventory?.BeginScan();
        try { return ReplayStorage.Inventory(cacheDirectories == null
            ? sessions.Values.Select(s => s.Info.Directory).Where(Directory.Exists) : ReplayCacheLocations.Existing(cacheDirectories), snapshotInventory ?? files); }
        finally { snapshotInventory?.EndScan(); }
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
        "file '" + i.ToString("D6") + extension + "'" + Environment.NewLine + "duration " + (s.End - s.Start).ToString("0.000000", CultureInfo.InvariantCulture) + Environment.NewLine));

    static long AllocationBytes(long length, long unit) => checked((length / unit + (length % unit == 0 ? 0 : 1)) * unit);

    static void RequireTarget(ReplayVolumeCapacity expected, ReplayVolumeCapacity actual)
    {
        if (!string.Equals(expected.Volume, actual.Volume, StringComparison.OrdinalIgnoreCase) || expected.AllocationUnit != actual.AllocationUnit)
            throw new IOException("Known-input capacity target volume or allocation unit changed.");
    }

    void RequireKnownInputCapacity(IReadOnlyDictionary<string, long> proposed, string snapshot, ReplayVolumeCapacity target)
        => RequireStorageCapacity(proposed, snapshot, target, pendingOutput);

    void RequireStorageCapacity(IReadOnlyDictionary<string, long> proposed, string snapshot, ReplayVolumeCapacity target, OutputClaim output,
        Dictionary<string, ReplayFileExtent> observed = null, ReplayProducerProof producer = null)
    {
        // Inventory precedes free-space sampling: a newer materialized prefix
        // must not be credited against older, pre-write available capacity.
        // A caller may share one fresh observation across budget/capacity checks
        // in this same locked operation. No observation survives a write.
        var inventory = observed ?? Inventory();
        _ = ReplayStorage.Bytes(inventory); // Reject inconsistent shared-allocation observations, without subtracting them from free space.
        long outputAllocated = 0;
        if (output != null)
        {
            try
            {
                var attributes = File.GetAttributes(output.Partial);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                    throw new IOException("Replay output is not an ordinary file.");
                var extent = ReplayStorage.Validate(files.Observe(output.Partial));
                RequireTarget(output.Target, new(extent.Volume, extent.AllocationUnit, 0));
                if (output.MaterializedIdentity != null && !string.Equals(output.MaterializedIdentity, extent.Identity, StringComparison.OrdinalIgnoreCase))
                    throw new OutputEvidenceInvalid("Replay output allocation identity changed; output and snapshot are retained.");
                output.MaterializedIdentity = extent.Identity;
                if (inventory.Any(entry => !string.Equals(entry.Key, output.Partial, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(entry.Value.Volume, extent.Volume, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(entry.Value.Identity, extent.Identity, StringComparison.OrdinalIgnoreCase)))
                    throw new OutputEvidenceInvalid("Replay output aliases retained input storage; output and snapshot are retained.");
                if (extent.AllocatedBytes > output.Demand.PeakAllocatedBytes)
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
        }
        var capacity = ReplayStorage.ValidateCapacity(files.ObserveCapacity(snapshot));
        RequireTarget(target, capacity);
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
            var claims = new List<ReplayVolumeClaim>();
            if (knownInputRemaining != 0)
                claims.Add(new(Path.Combine(snapshot, ".known-input-growth"), snapshot, target.Volume,
                    target.AllocationUnit, knownInputRemaining, ReplayVolumeClaimKind.UnmaterializedGrowth));
            if (output != null) claims.Add(new(output.Partial, output.Directory, output.Target.Volume,
                output.Target.AllocationUnit, output.Demand.PeakAllocatedBytes));
            volumeReservations.Update(volumeReservationOwner, claims, files, producer ?? pendingSource?.ProducerProof, acceptedWork: saving);
            volumeReservationHeld = claims.Count != 0;
            reservationError = null;
        }
    }

    OutputClaim PlanOutput(Session session, IReadOnlyList<Segment> selection, double end, string root, string partial,
        bool closed, out ReplayOutputDemand demand)
    {
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
        var binding = ReplayOutputSelection.Create(session.Info, exporter, root, end, closed, selection, files);
        demand = session.Source.EstimateOutput(binding);
        if (demand == null || !Enum.IsDefined(demand.Kind)) throw new IOException("Replay output demand evidence is invalid.");
        if (demand.Kind == ReplayOutputEvidenceKind.Unmeasured)
        {
            if (pendingOutput != null) throw new IOException("Replay output demand became unmeasured after reservation.");
            if (string.IsNullOrWhiteSpace(demand.Reason)) throw new IOException("Unmeasured replay output demand needs a reason.");
            RequireQualifiedOutput(demand);
            return null;
        }
        if (string.IsNullOrWhiteSpace(demand.Revision) || string.IsNullOrWhiteSpace(binding.ExporterIdentity)
            || demand.ExporterIdentity != binding.ExporterIdentity || demand.SelectionIdentity != binding.Identity
            || demand.AllocationUnit <= 0 || demand.PeakAllocatedBytes <= 0 || demand.PeakAllocatedBytes % demand.AllocationUnit != 0
            || !closed && !demand.CoversOpenTail)
            throw new IOException("Replay output demand is stale, incomplete or does not cover the unfinished tail.");
        if (pendingOutput != null && (pendingOutput.Demand.Revision != demand.Revision || pendingOutput.Demand.ExporterIdentity != demand.ExporterIdentity))
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
        volumeReservationHeld = false;
        pendingAllocations.Clear();
        pendingTarget = null;
        pendingSnapshot = null;
        pendingOutput = null;
        pendingSource = null;
        reservationError = null;
        return true;
    }

    Dictionary<string, long> PlanSnapshot(IReadOnlyList<Segment> selection, string extension, string snapshot, out ReplayVolumeCapacity target)
    {
        target = ReplayStorage.ValidateCapacity(files.ObserveCapacity(snapshot));
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < selection.Count; i++)
        {
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
            if (info.TimestampedAudio) segments.RemoveAll(s => Path.GetFileName(s.Path) == "chunk-000000000" + info.Extension);
            return segments;
        }
        catch (IOException) { return []; }
    }

    void ReserveCompleted(Session session, List<Segment> available)
    {
        if (!session.PendingEnd.HasValue) return;
        foreach (var segment in ReplaySelection.Select(available, session.PendingEnd.Value, session.Info.ReplaySeconds))
        {
            session.Reserved.TryAdd(segment.Path, segment);
            session.Pins.Add(segment.Path);
        }
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
        lock (gate)
        {
            if (!sessions.TryGetValue(id, out var session) || session.Retiring) return;
            // Qualified output claims remain owned while the synchronous export
            // can grow. Unmeasured sources retain the existing maintenance path.
            if (pendingOutput != null) RequireKnownInputCapacity(pendingAllocations, pendingSnapshot, pendingTarget);
            var segments = ReadSegments(session);
            // Reserve newly closed inputs even if the save task has not resumed.
            ReserveCompleted(session, segments);
            if (segments.Count == 0) { RequireBudget(pendingAllocations); session.Source.CheckCaptureHealth(0); return; }
            double newest = segments[^1].End;
            if (newest < session.LastEnd) { RequireBudget(pendingAllocations); session.Source.CheckCaptureHealth(session.LastEnd); return; } // Partial rewrite.
            session.LastEnd = newest;
            double threshold = newest - session.Info.ReplaySeconds - 10;
            string oldest = segments.First(s => s.End >= threshold).Path;
            foreach (string path in Directory.GetFiles(session.Info.Directory, "chunk-*" + session.Info.Extension))
                if (StringComparer.OrdinalIgnoreCase.Compare(path, oldest) < 0 && !session.Pins.Contains(path)) Cleanup(() => files.DeleteFile(path), path);
            long limit = (long)((session.Info.ReplaySeconds + 30) * (session.Info.BitrateMbps * 1000000L + 192000) / 8 * 1.5);
            if (Directory.GetFiles(session.Info.Directory, "chunk-*" + session.Info.Extension).Sum(p => files.Observe(p).Length) > limit)
                throw new ReplayCacheBudgetExceededException("Replay cache exceeded its byte limit. Increase the cache budget or change recording settings explicitly.");
            RequireBudget(pendingAllocations);
            session.Source.CheckCaptureHealth(newest);
        }
    }

    internal SaveSubmission TrySave(ReplaySessionId id, long triggerTimestamp, string destination)
    {
        lock (gate)
        {
            if (closing) return new("Recorder is stopping.", null);
            if (saving) return new("A replay is already being saved.", null);
            if (pendingSource != null || pendingOutput != null || volumeReservationHeld)
            {
                if (!PendingProducerStopped()) return new("Previous replay exporter cleanup is unconfirmed; retained storage remains reserved.", null);
                if (!ReleasePendingStorage()) return new("Previous replay storage release is unconfirmed; retained claims block another save.", null);
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
            try
            {
                var selection = ReplaySelection.Select(available, end, session.Info.ReplaySeconds);
                var plan = PlanSnapshot(selection, session.Info.Extension, snapshot, out var target);
                var output = PlanOutput(session, selection, end, root, partial, available.Count > 0 && available[^1].End >= end, out var demand);
                ReserveOpenSnapshot(plan, snapshot, target, output);
                RequireBudget(plan);
                RequireStorageCapacity(plan, snapshot, target, output, producer: session.Source.ProducerProof);
                pendingAllocations = plan;
                pendingTarget = target;
                pendingSnapshot = snapshot;
                pendingOutput = output;
                outputDemand = demand;
                pendingSource = session.Source;
            }
            catch (Exception ex) when (StorageFailure(ex)) { return new("Replay storage admission failed: " + ex.Message, null); }
            session.PendingEnd = end;
            ReserveCompleted(session, available);
            saving = true;
            var completion = new TaskCompletionSource<ReplaySaveResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.Save = completion.Task;
            _ = Task.Run(async () =>
            {
                var elapsed = Stopwatch.StartNew();
                ReplaySaveResult result;
                try
                {
                    var saved = await SaveAsync(session, end, root, snapshot, final, partial).ConfigureAwait(false);
                    result = new(true, saved.File, null, saved.Bytes, elapsed.Elapsed.TotalMilliseconds);
                }
                catch (Exception ex) { Report(ex.ToString()); result = new(false, null, ex.Message, 0, elapsed.Elapsed.TotalMilliseconds); }
                lock (gate)
                {
                    lastSave = result;
                    session.PendingEnd = null;
                    session.Reserved.Clear();
                    session.Pins.Clear();
                    if (PendingProducerStopped()) ReleasePendingStorage();
                    saving = false;
                    completion.SetResult(result);
                }
            });
            return new(null, completion.Task, outputDemand);
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
                    session.Source.Dispose();
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
        try { await retirement.ConfigureAwait(false); }
        finally { lock (gate) snapshotInventory?.Dispose(); }
    }

    public void Dispose()
    {
        CloseAsync().GetAwaiter().GetResult();
        lock (gate) snapshotInventory?.Dispose();
    }

    async Task<(string File, long Bytes)> SaveAsync(Session session, double end, string root, string snapshot, string final, string partial)
    {
        var info = session.Info;
        bool retainEvidence = false;
        try
        {
            Directory.CreateDirectory(snapshot);
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
                    if (available.Count > 0 && (available[^1].End >= end || session.Source.Stopped))
                    {
                        if (session.Source.Stopped) end = Math.Min(end, available[^1].End);
                        selection = ReplaySelection.Select(available, end, info.ReplaySeconds);
                        if (selection.Count == 0) throw new IOException("No replay frames are available yet.");
                        session.PendingEnd = null;
                        selection = selection.Select(s => new Segment { Path = s.Path, Start = s.Start, End = s.End }).ToList();
                        foreach (var segment in selection) session.Pins.Add(segment.Path);
                        break;
                    }
                }
                await waitForSegment().ConfigureAwait(false);
            }
            if (selection == null) throw new IOException("Capture did not finish the current segment in time. " + session.Source.Diagnostics);
            lock (gate)
            {
                var plan = PlanSnapshot(selection, info.Extension, snapshot, out var target);
                RequireTarget(pendingTarget, target);
                var output = PlanOutput(session, selection, end, root, partial, true, out var demand);
                RequireBudget(plan);
                RequireStorageCapacity(plan, snapshot, target, output);
                pendingAllocations = plan;
                pendingOutput = output;
                outputDemand = demand;
            }
            var originals = selection.Select(s => s.Path).ToArray();
            try
            {
                // Slow copies never hold the cache lock. Pruning honors every pin.
                for (int i = 0; i < selection.Count; i++)
                {
                    string copy = Path.Combine(snapshot, i.ToString("D6") + info.Extension);
                    lock (gate) RequireKnownInputCapacity(pendingAllocations, snapshot, pendingTarget);
                    if (!files.TryHardLink(copy, originals[i]))
                    {
                        lock (gate)
                        {
                            var plan = new Dictionary<string, long>(pendingAllocations, StringComparer.OrdinalIgnoreCase);
                            plan[copy] = AllocationBytes(ReplayStorage.Validate(files.Observe(originals[i])).Length, pendingTarget.AllocationUnit);
                            var inventory = Inventory();
                            RequireBudget(plan, observed: inventory);
                            RequireStorageCapacity(plan, snapshot, pendingTarget, pendingOutput, inventory);
                            pendingAllocations = plan;
                        }
                        files.Copy(originals[i], copy);
                    }
                    lock (gate)
                    {
                        RequireKnownInputCapacity(pendingAllocations, snapshot, pendingTarget);
                        pendingAllocations.Remove(copy);
                    }
                    selection[i].Path = copy;
                }
            }
            finally { lock (gate) { session.Pins.Clear(); session.Reserved.Clear(); } }
            Directory.CreateDirectory(root);
            string concat = Path.Combine(snapshot, "concat.txt");
            lock (gate)
            {
                RequireBudget(pendingAllocations);
                RequireKnownInputCapacity(pendingAllocations, snapshot, pendingTarget);
            }
            string concatText = ConcatText(selection, info.Extension);
            if (pendingOutput != null && Encoding.UTF8.GetByteCount(concatText) > 65536)
                throw new OutputEvidenceInvalid("Replay concat metadata exceeds its reserved reference envelope.");
            File.WriteAllText(concat, concatText, new UTF8Encoding(false));
            lock (gate) pendingAllocations.Remove(concat);
            var arguments = new List<string> { "-hide_banner", "-loglevel", "warning", "-y", "-f", "concat", "-safe", "1", "-i", concat, "-t", (end - selection[0].Start).ToString("0.000000", CultureInfo.InvariantCulture), "-c", "copy", "-avoid_negative_ts", "make_zero", "-movflags", "+faststart", "-map", "0:v:0" };
            if (info.RecordAudio) arguments.AddRange(["-map", "0:a:0"]);
            if (info.Codec == "hevc_nvenc") arguments.AddRange(["-tag:v", "hvc1"]);
            if (info.Codec == "av1_nvenc")
            {
                string interval = "1/(" + info.FramesPerSecond + "*TB)";
                arguments.AddRange(["-bsf:v", "setts=time_base=1/48000:prescale=1:pts=STARTPTS+N*" + interval + ":dts=STARTDTS+N*" + interval + ":duration=" + interval, "-video_track_timescale", "48000"]);
            }
            arguments.Add(partial);
            try
            {
                lock (gate)
                {
                    // Copies have new file identities. Bind the evidence to the
                    // actual immutable snapshot consumed by this export.
                    var output = PlanOutput(session, selection, end, root, partial, true, out var demand);
                    snapshotInventory?.Seal(snapshot);
                    RequireStorageCapacity(pendingAllocations, snapshot, pendingTarget, output);
                    pendingOutput = output;
                    outputDemand = demand;
                }
                long lastStorageCheck = Stopwatch.GetTimestamp();
                void CheckExportStorage()
                {
                    if (pendingOutput == null && !volumeReservationHeld) return;
                    lock (gate)
                    {
                        if (Stopwatch.GetElapsedTime(lastStorageCheck).TotalMilliseconds > ReplayStorageCoordinator.MaximumObservationGapMilliseconds)
                            throw new OutputEvidenceInvalid("Replay export storage observation exceeded 500 ms; output and snapshot are retained.");
                        try { RequireKnownInputCapacity(pendingAllocations, snapshot, pendingTarget); }
                        catch (ReplayVolumeReservationBusyException) when (Stopwatch.GetElapsedTime(lastStorageCheck).TotalMilliseconds < ReplayStorageCoordinator.MaximumObservationGapMilliseconds)
                        { return; }
                        if (pendingOutput != null && pendingOutput.Source.EstimateOutput(pendingOutput.Selection) != pendingOutput.Demand)
                            throw new OutputEvidenceInvalid("Replay output calibration changed or became unmeasured during export; output and snapshot are retained.");
                        if (Stopwatch.GetElapsedTime(lastStorageCheck).TotalMilliseconds > ReplayStorageCoordinator.MaximumObservationGapMilliseconds)
                            throw new OutputEvidenceInvalid("Replay export storage observation including filesystem overhead exceeded 500 ms.");
                        lastStorageCheck = Stopwatch.GetTimestamp();
                    }
                }
                session.Source.Export(arguments, snapshot, proof =>
                {
                    lock (gate)
                        if (volumeReservationHeld) volumeReservations.SetProducer(volumeReservationOwner, proof, acceptedWork: true);
                }, CheckExportStorage);

                CheckExportStorage();
                if (!PendingProducerStopped()) throw new IOException("Replay exporter cleanup is unconfirmed; output and inputs are retained.");
                if (pendingOutput != null) lock (gate) RequireKnownInputCapacity(pendingAllocations, snapshot, pendingTarget);
                if (!File.Exists(partial) || new FileInfo(partial).Length < 1000) throw new IOException("Replay save failed: output is missing or incomplete.");
                long bytes = new FileInfo(partial).Length;
                // Export/finalization is over. End durable growth ownership before
                // rename/cleanup can remove paths another profile still validates.
                // If release fails, retain inspectable inputs/partial and report
                // failure before publication, never a successful moved stale claim.
                if (volumeReservationHeld || pendingOutput != null)
                    lock (gate)
                        if (!ReleasePendingStorage()) throw new OutputEvidenceInvalid("Replay storage release failed before publication; partial output and snapshot are retained.");
                files.Publish(partial, final);
                // Publication is the success point. Logging/cleanup must not reverse it.
                Report("Saved " + final + "; requested end=" + end.ToString(CultureInfo.InvariantCulture));
                return (final, bytes);
            }
            catch (Exception ex) when (ex is OutputEvidenceInvalid || volumeReservationHeld && StorageFailure(ex))
            { retainEvidence = true; throw; }
            finally { if (!retainEvidence && PendingProducerStopped()) Cleanup(() => files.DeleteFile(partial), partial); }
        }
        catch (Exception ex) when (ex is OutputEvidenceInvalid || volumeReservationHeld && StorageFailure(ex))
        {
            // A durable holder can still name these paths after a failed update
            // or release. Do not turn a local failure into missing global anchors.
            retainEvidence = true;
            lock (gate) session.PreserveEvidence = true;
            throw;
        }
        // Inventory and removal own the same namespace. Export and copying have
        // already finished; no task/resource wait occurs while holding the gate.
        finally { lock (gate) if (!retainEvidence && PendingProducerStopped()) Cleanup(() => files.DeleteDirectory(snapshot), snapshot); }
    }

    void Cleanup(Action action, string path)
    {
        try
        {
            // Directory retirement already owns the inventory gate at its call
            // sites. Partial-output unlinking must keep its independent path.
            if (snapshotInventory != null && Directory.Exists(path))
                lock (gate) snapshotInventory.ReleaseUnder(path);
            action();
        }
        catch (IOException ex) { Report("Replay cleanup failed for " + path + ": " + ex.Message); }
        catch (UnauthorizedAccessException ex) { Report("Replay cleanup failed for " + path + ": " + ex.Message); }
    }
    void Report(string message) { try { diagnostic(message); } catch (Exception) { /* Diagnostics cannot own save completion. */ } }
}
