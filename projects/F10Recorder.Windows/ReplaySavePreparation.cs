using System.Diagnostics;

namespace F10Recorder;

// An accepted request owns a rooted workspace before its final immutable export
// request exists. Only a CLOSED boundary capability can cross that boundary.
internal sealed class ReplaySavePreparation : IDisposable
{
    readonly int owner = Environment.CurrentManagedThreadId;
    readonly ReplayRootedFiles files;
    readonly ReplaySessionInfo info;
    readonly ReplayInterval interval;
    readonly double sourceStart, sourceEnd;
    readonly int first, count;
    readonly Action check;
    readonly string directoryIdentity;
    readonly ReplayGuardDirectory.FileTicket creator, observer;
    ReplayStagedProcess process;
    ReplayGuardDirectory.FileTicket feedOwner;
    ReplayBoundaryProjection projection;
    ReplayTransportInput projectedSource;
    bool started, closed, transferred, disposed;
    bool receiptRecorded;
    internal Action<ReplayExportProcessReceipt> RecordReceipt;
    volatile bool launchPending;
    internal readonly string Partial, WorkDirectory, Boundary;
    internal ReplayStagedProcessTiming Timing => process?.Timing;
    internal bool Stopped => !launchPending && (process?.Stopped ?? true);
    internal bool Joined => (closed || disposed) && Stopped;

    internal static (int First, int Count)? Candidate(ReplaySessionInfo info, ReplayInterval interval, Segment source)
    {
        if (!ReplaySaveExport.Supports(info) || source == null || source.End - source.Start > 2.001) return null;
        long start = (long)Math.Round(source.Start * info.FramesPerSecond), end = (long)Math.Round(source.End * info.FramesPerSecond);
        var part = ReplayHybridPlan.ProjectPictures((start, end), ReplayHybridPlan.FrameAtOrAfter(interval.Start, info.FramesPerSecond), ReplayHybridPlan.FrameAtOrAfter(interval.End, info.FramesPerSecond));
        return part.First > 0 && part.Count > 0 ? (part.First, part.Count) : null;
    }
    internal static void WaitForTail(Task waiting, TimeProvider time, long started, Action checkAndPump, Func<string> diagnostics)
    {
        while (!waiting.IsCompleted)
        {
            if (time.GetElapsedTime(started).TotalSeconds >= 5) throw new IOException("Capture did not finish the current segment in time. " + diagnostics());
            checkAndPump(); Thread.Sleep(2);
        }
        waiting.GetAwaiter().GetResult(); checkAndPump();
    }
    internal ReplaySavePreparation(ReplayRootedFiles files, ReplaySessionInfo info, ReplayInterval interval,
        Segment candidate, string partial, Action guard)
    {
        this.files = files; this.info = info; this.interval = interval; check = guard;
        (first, count) = Candidate(info, interval, candidate) ?? throw new IOException("No bounded staged boundary candidate.");
        sourceStart = candidate.Start; sourceEnd = candidate.End;
        Partial = partial; WorkDirectory = partial + ".work"; Boundary = ReplayOutputWorkspace.BoundaryPath(partial, 0);
        guard();
        if (Directory.Exists(WorkDirectory) || File.Exists(WorkDirectory)) throw new IOException("Staged workspace is not exclusive.");
        files.CreateDirectory(WorkDirectory); directoryIdentity = files.DirectoryOwner(WorkDirectory).Native.Identity;
        // Constructor does not publish a producer yet; caller forces evidence
        // immediately after installing this owner, before Start can launch.
        var root = files.DirectoryOwner(WorkDirectory).Native;
        creator = root.CreateExternalScratch(Path.GetFileName(Boundary));
        try { observer = root.OpenObservation(Path.GetFileName(Boundary), creator.Observe().Extent); }
        catch { creator.Dispose(); throw; }
    }
    void Require()
    {
        if (disposed || owner != Environment.CurrentManagedThreadId)
            throw new IOException($"Staged workspace owner or native identity changed; disposed={disposed}; ownerThread={owner}; currentThread={Environment.CurrentManagedThreadId}.");
        if (files.DirectoryOwner(WorkDirectory).Native.Identity != directoryIdentity)
            throw new IOException("Staged workspace native directory identity changed.");
    }
    internal void Start(Func<IReadOnlyList<string>, ReplaySavePreparation, ReplayStagedProcess> launch)
    {
        Require(); if (started) throw new IOException("Staged preparation starts once.");
        started = true; check();
        var arguments = ReplaySaveExport.BoundaryArguments(info, "pipe:0", first, count, Boundary).ToList();
        int input = arguments.IndexOf("-i"); arguments.InsertRange(input, ["-f", "mpegts"]);
        launchPending = true;
        try { process = launch(arguments, this) ?? throw new IOException("Staged launcher returned no request owner."); }
        finally { launchPending = false; }
    }
    internal void Pump() { Require(); process?.Pump(); }
    internal void WaitForReady() { Require(); process.WaitForReady(); }
    internal ReplayNativeExportScope.Snapshot Observe()
    {
        Require();
        var parent = files.DirectoryOwner(Path.GetDirectoryName(Partial)).Native;
        if (parent.LeafExists(Path.GetFileName(Partial)))
            throw new IOException("Staged preparation must not create the main output.");
        var work = ObserveWork(); return new(null, null, work, work.Values.Any(e => e.Identity == null));
    }
    internal Dictionary<string, ReplayFileExtent> ObserveWork()
    {
        Require(); var result = new Dictionary<string, ReplayFileExtent>(StringComparer.OrdinalIgnoreCase);
        var entries = files.DirectoryOwner(WorkDirectory).Native.EnumerateLeaves(3);
        if (!transferred && (entries.Count != 1 || !entries[0].Path.Equals(Boundary, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Staged workspace has an unowned or missing member.");
        if (!transferred)
        {
            var observed = observer.Observe();
            RequireBoundary(entries[0], observed);
            result.Add(Boundary, observed.Extent);
        }
        return result;
    }
    internal static void RequireBoundary(ReplayStorage.DirectoryEntry listed, ReplayGuardDirectory.Observation observed)
    {
        const FileAttributes unsupported = FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Compressed | FileAttributes.SparseFile;
        if (listed == null || observed == null || (listed.Attributes & unsupported) != 0 || ((FileAttributes)observed.Attributes & unsupported) != 0
            || listed.Extent.Identity != observed.Extent.Identity || listed.Extent.Volume != observed.Extent.Volume
            || observed.Links != 1 || observed.Extent.Length > ReplaySaveExport.BoundaryLimit || observed.Extent.AllocatedBytes > ReplaySaveExport.BoundaryLimit)
            throw new IOException("Staged boundary attributes/identity/link/growth contract failed.");
    }
    internal void BindRequest(ReplayExportRequest request)
    {
        Require();
        if (request.Preparation != this || request.NativeFiles == null || request.Partial != Partial || request.Interval != interval || request.OwnWorkDirectory)
            throw new IOException("Staged workspace cannot bind this export request.");
        request.OwnWorkDirectory = true;
    }
    internal int CandidateInput(ReplayExportRequest request)
    {
        Require();
        var candidates = request.Inputs.Select((input, ordinal) => (input, ordinal)).Where(pair => pair.input.Start == sourceStart && pair.input.End == sourceEnd).ToArray();
        if (candidates.Length != 1) throw new IOException("Protected selection changed the staged source.");
        return candidates[0].ordinal;
    }
    internal void Feed(ReplayExportRequest request, int ordinal, ReplayTransportIndex index)
    {
        Require(); if (projection != null || ordinal != CandidateInput(request)) throw new IOException("Staged source can bind once.");
        var input = request.Inputs[ordinal];
        projectedSource = new(input.Start, input.End, index);
        projection = ReplayBoundaryProjection.Create(ordinal, projectedSource, index, request.Interval, info.FramesPerSecond);
        if (projection == null || projection.First != first || projection.Count != count)
            throw new IOException("Indexed physical boundary differs from the staged fixed arguments.");
        feedOwner = files.DirectoryOwner(Path.GetDirectoryName(input.Path)).Native.OpenAsyncRead(Path.GetFileName(input.Path), input.Extent);
        process.Feed(feedOwner.Data(), input.Extent.Length, index.Sha256, () => {
            Require(); check();
            if (request.SourceProtection == null || request.SourceProtection.Inputs.Count != request.Inputs.Count + 1)
                throw new IOException("Every selected source and concat must be protected before staged input release.");
            if (feedOwner.Observe().Extent != input.Extent) throw new IOException("Staged source native extent changed.");
        });
    }
    internal void Complete(ReplayExportRequest request, ReplayHybridPlan fullPlan, ReplayTransportIndex finalIndex)
    {
        Require();
        var actual = request.Inputs[CandidateInput(request)];
        var finalSource = new ReplayTransportInput(actual.Start, actual.End, finalIndex);
        RequireFinalBoundary(projection, fullPlan, finalSource, request.Interval, info.FramesPerSecond);
        try { process.Join(); }
        finally { ObserveReceipt(); }
        feedOwner.Data().Dispose(); feedOwner.Dispose(); feedOwner = null;
        var expected = observer.Observe().Extent;
        creator.Dispose(); files.Admit(Boundary, expected, true); closed = true;
        check();
    }
    internal static void RequireFinalBoundary(ReplayBoundaryProjection projection, ReplayHybridPlan plan,
        ReplayTransportInput finalSource, ReplayInterval interval, int fps)
    {
        if (projection == null || !projection.Matches(plan, finalSource, interval, fps))
            throw new IOException("Whole-source plan differs from the staged local boundary.");
    }
    internal ReplayGuardDirectory.FileTicket TransferClosed(ReplayExportRequest request)
    {
        Require(); if (!Joined || transferred || request.Preparation != this || !request.OwnWorkDirectory)
            throw new IOException("Boundary transfer requires a single closed request-owned capability.");
        transferred = true; return observer;
    }
    internal void Cancel()
    {
        Require(); try { process?.AbortAndJoin(); } finally { ObserveReceipt(); }
        feedOwner?.Data().Dispose(); feedOwner?.Dispose(); feedOwner = null;
        if (!closed)
        {
            var expected = observer.Observe().Extent; creator.Dispose(); files.Admit(Boundary, expected, true); closed = true;
        }
        check();
    }
    void ObserveReceipt()
    {
        var receipt = process?.ObservedReceipt;
        if (receiptRecorded || receipt == null) return;
        RecordReceipt?.Invoke(receipt); receiptRecorded = true;
    }
    public void Dispose()
    {
        if (disposed) return; Require();
        try { process?.Dispose(); } finally { ObserveReceipt(); }
        if (process != null && !process.Stopped) { files.RetainUncertain(); throw new IOException("Staged users remain live."); }
        feedOwner?.Data().Dispose(); feedOwner?.Dispose(); feedOwner = null;
        creator.Dispose(); if (!transferred) observer.Dispose(); disposed = true;
    }
}
