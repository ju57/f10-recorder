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
internal sealed record ReplayCacheStatus(bool Saving, ReplaySaveResult LastSave);
internal sealed record ReplaySessionStatus(double BufferedSeconds, long CacheBytes);

// Only operations whose failures must be controlled in ownership checks vary.
internal class ReplayFiles
{
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
    bool closing, saving;
    ReplaySaveResult lastSave;
    Task close;

    internal ReplayCache(ReplayFiles files = null, TimeProvider time = null,
        Func<Task> waitForSegment = null, Action<string> diagnostic = null)
    {
        this.files = files ?? new ReplayFiles();
        this.time = time ?? TimeProvider.System;
        this.waitForSegment = waitForSegment ?? (() => Task.Delay(TimeSpan.FromMilliseconds(100), this.time));
        this.diagnostic = diagnostic ?? Log.Write;
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

    internal ReplayCacheStatus Status { get { lock (gate) return new(saving, lastSave); } }

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
            if (segments.Count == 0) { session.Source.CheckCaptureHealth(0); return; }
            double newest = segments[^1].End;
            if (newest < session.LastEnd) { session.Source.CheckCaptureHealth(session.LastEnd); return; } // Partial rewrite.
            session.LastEnd = newest;
            double threshold = newest - session.Info.ReplaySeconds - 10;
            string oldest = segments.First(s => s.End >= threshold).Path;
            foreach (string path in Directory.GetFiles(session.Info.Directory, "chunk-*" + session.Info.Extension))
                if (StringComparer.OrdinalIgnoreCase.Compare(path, oldest) < 0 && !session.Pins.Contains(path)) Cleanup(() => files.DeleteFile(path), path);
            long limit = (long)((session.Info.ReplaySeconds + 30) * (session.Info.BitrateMbps * 1000000L + 192000) / 8 * 1.5);
            if (Directory.GetFiles(session.Info.Directory, "chunk-*" + session.Info.Extension).Sum(p => new FileInfo(p).Length) > limit)
                throw new IOException("Replay cache exceeded its byte limit.");
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
                    var saved = await SaveAsync(session, end, root).ConfigureAwait(false);
                    result = new(true, saved.File, null, saved.Bytes, elapsed.Elapsed.TotalMilliseconds);
                }
                catch (Exception ex) { Report(ex.ToString()); result = new(false, null, ex.Message, 0, elapsed.Elapsed.TotalMilliseconds); }
                lock (gate)
                {
                    lastSave = result;
                    session.PendingEnd = null;
                    session.Reserved.Clear();
                    session.Pins.Clear();
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
                    Cleanup(() => files.DeleteDirectory(session.Info.Directory), session.Info.Directory);
                    lock (gate) sessions.Remove(id);
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

    async Task<(string File, long Bytes)> SaveAsync(Session session, double end, string root)
    {
        var info = session.Info;
        string snapshot = Path.Combine(info.Directory, "save-" + Guid.NewGuid().ToString("N"));
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
            var originals = selection.Select(s => s.Path).ToArray();
            try
            {
                // Slow copies never hold the cache lock. Pruning honors every pin.
                for (int i = 0; i < selection.Count; i++)
                {
                    string copy = Path.Combine(snapshot, i.ToString("D6") + info.Extension);
                    if (!files.TryHardLink(copy, originals[i])) files.Copy(originals[i], copy);
                    selection[i].Path = copy;
                }
            }
            finally { lock (gate) { session.Pins.Clear(); session.Reserved.Clear(); } }
            Directory.CreateDirectory(root);
            string final = Path.Combine(root, "Replay-" + DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss-fff") + "-" + Guid.NewGuid().ToString("N")[..6] + ".mp4");
            string partial = final + ".partial.mp4";
            string concat = Path.Combine(snapshot, "concat.txt");
            File.WriteAllLines(concat, selection.SelectMany(s => new[] { "file '" + Path.GetFileName(s.Path) + "'", "duration " + (s.End - s.Start).ToString("0.000000", CultureInfo.InvariantCulture) }), new UTF8Encoding(false));
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
        finally { Cleanup(() => files.DeleteDirectory(snapshot), snapshot); }
    }

    void Cleanup(Action action, string path)
    {
        try { action(); }
        catch (IOException ex) { Report("Replay cleanup failed for " + path + ": " + ex.Message); }
        catch (UnauthorizedAccessException ex) { Report("Replay cleanup failed for " + path + ": " + ex.Message); }
    }
    void Report(string message) { try { diagnostic(message); } catch (Exception) { /* Diagnostics cannot own save completion. */ } }
}
