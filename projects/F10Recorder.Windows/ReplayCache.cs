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
    void Export(IReadOnlyList<string> arguments, string snapshot);
}

internal sealed record ReplaySaveResult(bool Succeeded, string File, string Error, long Bytes, double Milliseconds);
internal sealed record SaveSubmission(string Rejection, Task<ReplaySaveResult> Completion)
{
    internal bool Accepted => Completion != null;
}
internal sealed record ReplayCacheStatus(bool Saving, ReplaySaveResult LastSave, long? CacheBytes, string StorageError);
internal sealed record ReplaySessionStatus(double BufferedSeconds, long CacheBytes);

// Only operations whose failures must be controlled in ownership checks vary.
internal class ReplayFiles
{
    internal virtual ReplayFileExtent Observe(string path) => ReplayStorage.Observe(path);
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
        internal Task Stop, Retirement;
        internal Task<ReplaySaveResult> Save;
        internal double LastEnd;
        internal long Bytes, BytesAt;
    }

    readonly object gate = new();
    readonly Dictionary<ReplaySessionId, Session> sessions = new();
    readonly ReplayFiles files;
    readonly TimeProvider time;
    readonly Func<Task> waitForSegment;
    readonly Action<string> diagnostic;
    readonly string cacheDirectory;
    readonly long cacheBudgetBytes;
    Dictionary<string, long> pendingAllocations = new(StringComparer.OrdinalIgnoreCase);
    ReplayVolumeCapacity pendingTarget;
    bool closing, saving;
    ReplaySaveResult lastSave;
    Task close;

    internal ReplayCache(ReplayFiles files = null, TimeProvider time = null,
        Func<Task> waitForSegment = null, Action<string> diagnostic = null, string cacheDirectory = null, long cacheBudgetBytes = 10_000_000_000)
    {
        this.files = files ?? new ReplayFiles();
        this.time = time ?? TimeProvider.System;
        this.waitForSegment = waitForSegment ?? (() => Task.Delay(TimeSpan.FromMilliseconds(100), this.time));
        this.diagnostic = diagnostic ?? Log.Write;
        this.cacheDirectory = cacheDirectory == null ? null : Path.GetFullPath(cacheDirectory);
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
            return id;
        }
    }

    internal ReplayCacheStatus Status
    {
        get
        {
            lock (gate)
            {
                try { return new(saving, lastSave, ReplayStorage.Bytes(Inventory()), null); }
                catch (Exception ex) when (StorageFailure(ex)) { return new(saving, lastSave, null, ex.Message); }
            }
        }
    }
    internal bool IsSaving { get { lock (gate) return saving; } }
    static bool StorageFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or OverflowException;
    Dictionary<string, ReplayFileExtent> Inventory() => ReplayStorage.Inventory(cacheDirectory == null
        ? sessions.Values.Select(s => s.Info.Directory).Where(Directory.Exists) : [cacheDirectory], files);

    internal void ValidateBudget(long proposedBudgetBytes)
    {
        if (proposedBudgetBytes <= 0) throw new ArgumentOutOfRangeException(nameof(proposedBudgetBytes));
        lock (gate) RequireBudget(pendingAllocations, proposedBudgetBytes);
    }

    void RequireBudget(IReadOnlyDictionary<string, long> proposed, long? ceiling = null)
    {
        var inventory = Inventory();
        long committed = ReplayStorage.Bytes(inventory);
        foreach (var claim in proposed)
        {
            // Observed allocation already affects physical usage. Only its future
            // remainder is a claim; never charge a materializing copy twice.
            long allocated = inventory.TryGetValue(claim.Key, out var extent) ? extent.AllocatedBytes : 0;
            committed = checked(committed + Math.Max(0, claim.Value - allocated));
        }
        if (committed > (ceiling ?? cacheBudgetBytes)) throw new IOException("Replay-cache budget cannot hold retained footage and temporary input copies. Increase the budget or release retained material explicitly.");
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
    {
        // Inventory precedes free-space sampling: a newer materialized prefix
        // must not be credited against older, pre-write available capacity.
        var inventory = Inventory();
        _ = ReplayStorage.Bytes(inventory); // Reject inconsistent shared-allocation observations, without subtracting them from free space.
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
        if (remaining > capacity.AvailableBytes)
            throw new IOException("Known-input capacity cannot hold remaining replay snapshot copies and concat data on their target volume.");
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
            var segments = ReplaySelection.Read(info.Manifest, info.Directory);
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
                throw new IOException("Replay cache exceeded its byte limit.");
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
            if (!sessions.TryGetValue(id, out var session) || session.Retiring)
                return new("The replay buffer is warming up or recovering.", null);
            double end = session.Source.SaveCutoffAt(triggerTimestamp);
            if (end <= 0) return new("The replay buffer is warming up or recovering.", null);
            // Admission, cutoff and the lifetime claim are atomic with retirement.
            string root = Path.GetFullPath(destination);
            var available = ReadSegments(session);
            string snapshot = Path.Combine(session.Info.Directory, "save-" + Guid.NewGuid().ToString("N"));
            try
            {
                var plan = PlanSnapshot(ReplaySelection.Select(available, end, session.Info.ReplaySeconds), session.Info.Extension, snapshot, out var target);
                RequireBudget(plan);
                RequireKnownInputCapacity(plan, snapshot, target);
                pendingAllocations = plan;
                pendingTarget = target;
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
                    var saved = await SaveAsync(session, end, root, snapshot).ConfigureAwait(false);
                    result = new(true, saved.File, null, saved.Bytes, elapsed.Elapsed.TotalMilliseconds);
                }
                catch (Exception ex) { Report(ex.ToString()); result = new(false, null, ex.Message, 0, elapsed.Elapsed.TotalMilliseconds); }
                lock (gate)
                {
                    lastSave = result;
                    session.PendingEnd = null;
                    session.Reserved.Clear();
                    session.Pins.Clear();
                    pendingAllocations.Clear();
                    pendingTarget = null;
                    saving = false;
                    completion.SetResult(result);
                }
            });
            return new(null, completion.Task);
        }
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
                        Cleanup(() => files.DeleteDirectory(session.Info.Directory), session.Info.Directory);
                        sessions.Remove(id);
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
            return close = Task.WhenAll(sessions.Keys.ToArray().Select(RetireSessionAsync));
        }
    }

    public void Dispose() => CloseAsync().GetAwaiter().GetResult();

    async Task<(string File, long Bytes)> SaveAsync(Session session, double end, string root, string snapshot)
    {
        var info = session.Info;
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
                RequireBudget(plan);
                RequireKnownInputCapacity(plan, snapshot, target);
                pendingAllocations = plan;
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
                            RequireBudget(plan);
                            RequireKnownInputCapacity(plan, snapshot, pendingTarget);
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
            string final = Path.Combine(root, "Replay-" + DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss-fff") + "-" + Guid.NewGuid().ToString("N")[..6] + ".mp4");
            string partial = final + ".partial.mp4";
            string concat = Path.Combine(snapshot, "concat.txt");
            lock (gate)
            {
                RequireBudget(pendingAllocations);
                RequireKnownInputCapacity(pendingAllocations, snapshot, pendingTarget);
            }
            File.WriteAllText(concat, ConcatText(selection, info.Extension), new UTF8Encoding(false));
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
                session.Source.Export(arguments, snapshot);
                if (!File.Exists(partial) || new FileInfo(partial).Length < 1000) throw new IOException("Replay save failed: output is missing or incomplete.");
                long bytes = new FileInfo(partial).Length;
                files.Publish(partial, final);
                // Publication is the success point. Logging/cleanup must not reverse it.
                Report("Saved " + final + "; requested end=" + end.ToString(CultureInfo.InvariantCulture));
                return (final, bytes);
            }
            finally { Cleanup(() => files.DeleteFile(partial), partial); }
        }
        // Inventory and removal own the same namespace. Export and copying have
        // already finished; no task/resource wait occurs while holding the gate.
        finally { lock (gate) Cleanup(() => files.DeleteDirectory(snapshot), snapshot); }
    }

    void Cleanup(Action action, string path)
    {
        try { action(); }
        catch (IOException ex) { Report("Replay cleanup failed for " + path + ": " + ex.Message); }
        catch (UnauthorizedAccessException ex) { Report("Replay cleanup failed for " + path + ": " + ex.Message); }
    }
    void Report(string message) { try { diagnostic(message); } catch (Exception) { /* Diagnostics cannot own save completion. */ } }
}
