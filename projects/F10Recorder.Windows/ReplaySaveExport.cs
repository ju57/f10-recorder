using System.Diagnostics;
using System.Globalization;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace F10Recorder;

internal sealed record ReplayExportRequest(ReplaySessionInfo Info, ReplayInterval Interval,
    IReadOnlyList<ReplayOutputInput> Inputs, string Concat, string Partial, ReplaySaveTiming Timing)
{
    internal bool OwnWorkDirectory;
    internal ReplayNativeExportScope NativeFiles;
    internal ReplaySavePreparation Preparation;
    internal ReplayMp4Expectation Integrity { get; private set; }
    internal readonly ReplayMp4ReadTiming IntegrityReadTiming = new();
    internal readonly ReplayIntegrityGateTiming IntegrityGateTiming = new();
    internal ReplayTransportBatchTiming IndexBatch;
    internal readonly ReplayWorkerLifetime IndexReaders = new();
    internal readonly ReplayWorkerLifetime FallbackIndexReaders = new();
    internal readonly ReplayPreparedIndexAcquisition PreparedAcquisition = new();
    internal ReplayCompletedHistory.Snapshot PreparedInputs;
    internal Action MaterializePreparedFallback;
    internal readonly ReplayWorkerLifetime PacketReaders = new();
    internal bool? PacketPipelineForChecks;
    internal bool PacketPipelineSelected;
    internal Func<bool> ExternalProducersAbsent;
    internal string ConcatSha256;
    internal ReplayExportInputLease SourceProtection;
    internal ReplayPacketWorkTiming PacketMuxWork;
    internal ReplayPacketReadWork PacketRead;
    internal long PacketPipelineBufferBytes;
    internal readonly ReplayConstructionClosure Closure = new();
    internal string Recipe { get; private set; }
    internal void SelectRecipe(string recipe)
    {
        if (recipe != ReplayExactExport.Recipe && !ReplaySaveExport.IsHybridRecipe(recipe))
            throw new IOException("Unregistered replay export recipe.");
        Recipe = recipe;
        Timing.ExportRecipe = recipe;
    }
    internal ReplayPublicationLease ValidatedOutput { get; private set; }
    Func<ReplayPublicationLease> nativePublication;
    bool nativePublicationUsed;
    internal void AttachNativePublication(Func<ReplayPublicationLease> open)
    {
        if (open == null || nativePublication != null || ValidatedOutput != null || Integrity == null
            || ManagedMux is not { Closed: true, InputHashesComplete: true, State: "Completed", Error: null, CloseError: null, ObservationError: null, FinalOutput: not null }
            || Recipe != ReplaySaveExport.PacketRecipe)
            throw new IOException("Native publication requires one closed packet producer and its independent frozen proof.");
        nativePublication = open;
    }
    internal ReplayPublicationLease OpenPublication(ReplayPublicationLease.OpenTiming timing)
    {
        if (nativePublication == null) return new ReplayPublicationLease(Partial, timing);
        if (nativePublicationUsed)
            throw new IOException("Native publication ownership is single-use and packet-only.");
        nativePublicationUsed = true;
        if (Recipe != ReplaySaveExport.PacketRecipe
            || ManagedMux is not { Closed: true, InputHashesComplete: true, State: "Completed", Error: null, CloseError: null, ObservationError: null, FinalOutput: not null })
            throw new IOException("Native publication ownership is single-use and packet-only.");
        return nativePublication() ?? throw new IOException("Native publication factory returned no checked-object capability.");
    }
    internal ReplayManagedMuxReceipt ManagedMux { get; private set; }
    internal void FreezeManagedMux(ReplayManagedMuxReceipt receipt)
    {
        if (ManagedMux != null || receipt == null) throw new IOException("Replay managed producer receipt must be set exactly once.");
        ManagedMux = receipt;
    }
    internal void FreezeValidatedOutput(ReplayPublicationLease lease)
    {
        if (ValidatedOutput != null || lease == null || PacketRead is { Joined: false }) throw new IOException("Replay validated-output lease must be set exactly once.");
        ValidatedOutput = lease;
    }
    internal void FreezeIntegrity(ReplayMp4Expectation expectation)
    {
        if (Integrity != null || expectation == null) throw new IOException("Replay coded-byte expectation must be set exactly once.");
        Integrity = expectation;
    }
    internal string WorkDirectory => Partial + ".work";
    internal IReadOnlyList<string> LegacyArguments => ReplayExactExport.Arguments(Info, Interval, Inputs[0].Start, Concat, Partial);
}

// Construction only. ReplayCache still owns closed-output validation,
// storage release and publication. All child launches use CaptureSession's
// existing verified engine/job/lifetime route.
internal static class ReplaySaveExport
{
    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateWorkDirectory(string path, IntPtr security);
    internal enum Construction { PacketCopyBatchCancellable = 3, ExactFallback = 4 }
    // Candidate policy. Ordinary admission still requires fresh exact-binary
    // composite-policy calibration; offline pilots do not activate a catalog.
    internal const Construction DefaultConstruction = Construction.PacketCopyBatchCancellable;
    internal const string PolicyIdentity = "replay-avc-aac-owned-completed-history-direct-avcc-with-reserved-staging-exact-fallback-v5";
    internal const string BoundedPacketConstruction = nameof(Construction.PacketCopyBatchCancellable) + "+BoundedPacketPipeline";
    internal const string PacketRecipe = "replay-hybrid-avc-aac-packet-mp4-v4";
    internal static string Recipe => RecipeFor(DefaultConstruction);
    internal static string RecipeFor(Construction construction) => construction switch
    {
        Construction.PacketCopyBatchCancellable => PacketRecipe,
        Construction.ExactFallback => ReplayExactExport.Recipe,
        _ => throw new ArgumentOutOfRangeException(nameof(construction))
    };
    internal static bool IsHybridRecipe(string recipe) => recipe == PacketRecipe;
    internal const long BoundaryLimit = 32L * 1024 * 1024;
    internal static bool Supports(ReplaySessionInfo info) => info.Codec == "h264_nvenc" && info.Extension == ".ts" && info.FramesPerSecond == 60;
    internal static double Preroll(ReplaySessionInfo info) => Supports(info) && info.RecordAudio ? ReplayAacCopyPlan.ProtectionSeconds : 0;
    // Retain the conservative historical boundary/assembly envelope, including
    // producer stop/allocator growth. Retiring concat construction does not
    // qualify smaller scratch bounds or transfer a model to rebuilt bytes.
    internal static ReplayOutputScratchDemand WorkDemand(long inputBytes, long unit) => new(
        ReplayReferenceModel.Round(checked(inputBytes + 2 * BoundaryLimit), unit),
        ReplayReferenceModel.Round(BoundaryLimit + 16L * 1024 * 1024, unit));

    internal static void Run(ReplayExportRequest request, Action<IReadOnlyList<string>, Action> runChild, Action checkStorage,
        Construction construction)
    {
        _ = RecipeFor(construction);
        request.Timing.ExportConstruction = construction.ToString();
        bool cancellableIndex = construction == Construction.PacketCopyBatchCancellable;
        long inputBytes = request.Inputs.Sum(i => i.Extent.Length);
        using var workspaceReader = new ReplayOutputWorkspace.Reader(request.Partial);
        void CheckWorkspace()
        {
            if (request.NativeFiles != null) { checkStorage?.Invoke(); return; }
            long observedAt = Stopwatch.GetTimestamp();
            request.Timing.WorkspaceCheckStarted = observedAt;
            request.Timing.WorkspaceObservationCompleted = request.Timing.WorkspaceCheckCompleted = null;
            try
            {
            var work = workspaceReader.Observe();
            long allocated = 0;
            foreach (var entry in work)
            {
                allocated = checked(allocated + Math.Max(entry.Value.Length, entry.Value.AllocatedBytes));
                if (Path.GetFileName(entry.Key).StartsWith("boundary-", StringComparison.Ordinal)
                    && Math.Max(entry.Value.Length, entry.Value.AllocatedBytes) > BoundaryLimit)
                    throw new IOException("Growing replay boundary exceeded its recipe envelope.");
            }
            long unit = work.Count == 0 ? 4096 : work.First().Value.AllocationUnit;
            if (allocated > WorkDemand(inputBytes, unit).Total) throw new IOException("Growing replay workspace exceeded its recipe envelope.");
            }
            finally
            {
                double elapsed = Stopwatch.GetElapsedTime(observedAt).TotalMilliseconds;
                request.Timing.WorkspaceObservations++;
                request.Timing.WorkspaceObservationMilliseconds += elapsed;
                request.Timing.MaximumWorkspaceObservationMilliseconds = Math.Max(request.Timing.MaximumWorkspaceObservationMilliseconds, elapsed);
                request.Timing.WorkspaceObservationCompleted = Stopwatch.GetTimestamp();
            }
            checkStorage?.Invoke();
            request.Timing.WorkspaceCheckCompleted = Stopwatch.GetTimestamp();
        }
        void run(IReadOnlyList<string> arguments)
        {
            Action guard = request.OwnWorkDirectory ? CheckWorkspace : checkStorage;
            if (request.NativeFiles == null) { runChild(arguments, guard); return; }
            string path = arguments[^1];
            bool enteredChild = false;
            try
            {
                request.NativeFiles.BeginExternal(path);
                enteredChild = true;
                runChild(arguments, guard);
                if (request.ExternalProducersAbsent?.Invoke() != true)
                    throw new IOException("External replay producer absence is unconfirmed.");
                request.NativeFiles.CompleteExternal(path);
            }
            catch
            {
                request.NativeFiles.CloseFailedExternal(path, !enteredChild || request.ExternalProducersAbsent?.Invoke() == true);
                throw;
            }
        }
        var streams = new List<FileStream>();
        var indices = new List<ReplayTransportIndex>();
        bool fallbackSourcesChecked=false;
        void CheckFallbackSources()
        {
            if(request.PreparedInputs==null || fallbackSourcesChecked)return;
            foreach(var stream in streams)stream.Position=0;
            try
            {
                ReplayTransportBatchIndex.Read(streams.Select((s,n)=>new ReplayTransportSource(s,request.Inputs[n].Extent.Length)).ToArray(),
                    request.Info.FramesPerSecond,request.Info.RecordAudio,4,checkStorage,new(),cancellableReads:cancellableIndex,
                    readDeadline:TimeSpan.FromSeconds(30),lifetime:request.FallbackIndexReaders);
            }
            catch(NotSupportedException){ /* Joined all-source corruption precedence is unchanged. */ }
            fallbackSourcesChecked=true;
        }
        void RunFallback()
        {
            CheckFallbackSources();
            request.MaterializePreparedFallback?.Invoke();
            run(request.LegacyArguments);
        }
        request.SourceProtection = new();
        if(request.PreparedInputs!=null)request.SourceProtection.Depend(request.PreparedInputs);
        Exception primaryFailure = null;
        try
        {
            if (request.NativeFiles != null || request.ConcatSha256 != null || File.Exists(request.Concat))
            {
                var concatInput = request.NativeFiles?.OpenInput(request.Concat, request.NativeFiles.Extent(request.Concat), false)
                    ?? new FileStream(request.Concat, FileMode.Open, FileAccess.Read, FileShare.Read);
                request.SourceProtection.Add(concatInput); request.Closure.SourceOpened();
                if (request.NativeFiles != null && request.ConcatSha256 == null
                    || request.ConcatSha256 != null && Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(concatInput)) != request.ConcatSha256)
                    throw new IOException("Immutable replay concat metadata differs from its accepted selection.");
            }
            // Open EVERY completed input before any codec/fallback decision.
            // Valid unsupported layouts can otherwise bypass later source pins.
            foreach (var input in request.Inputs)
            {
                checkStorage?.Invoke();
                if ((File.GetAttributes(input.Path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                    throw new IOException("Replay snapshot is not a regular protected input.");
                var stream = request.NativeFiles?.OpenInput(input.Path, input.Extent, cancellableIndex)
                    ?? new FileStream(input.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
                        FileOptions.SequentialScan | (cancellableIndex ? FileOptions.Asynchronous : FileOptions.None));
                streams.Add(stream); request.SourceProtection.Add(stream); request.Closure.SourceOpened();
                if (stream.IsAsync) request.Timing.AsynchronousInputs++;
                if (stream.IsAsync != cancellableIndex) throw new IOException("Replay protected source handle mode differs from its construction policy.");
                if (ReplayStorage.ObserveInventory(stream.SafeFileHandle, input.Extent).Length != input.Extent.Length)
                    throw new IOException("Protected replay input length changed.");
            }
            request.SelectRecipe(ReplayExactExport.Recipe);
            if (construction == Construction.ExactFallback || !Supports(request.Info)) { RunFallback(); return; }
            ReplayHybridPlan plan; ReplayAacCopyPlan audio = null;
            int preparedInput = -1;
            ReplayTransportIndex preparedIndex = null;
            NotSupportedException preparedUnsupported = null;
            try
            {
                request.Timing.IndexStarted = Stopwatch.GetTimestamp();
                if (request.Inputs.Any(input => input.End - input.Start > 2.001))
                    throw new NotSupportedException("Long replay GOPs require the existing exporter.");
                if (request.Preparation != null)
                {
                    if (!cancellableIndex) throw new IOException("Staged preparation requires the bounded cancellable construction.");
                    preparedInput = request.Preparation.CandidateInput(request);
                    try
                    {
                    preparedIndex = request.PreparedInputs?.Index(request.Inputs[preparedInput].Path)
                        ?? ReplayTransportReader.Read(streams[preparedInput], request.Inputs[preparedInput].Extent.Length,
                            request.Info.FramesPerSecond, request.Info.RecordAudio, checkStorage);
                    request.Preparation.Feed(request, preparedInput, preparedIndex);
                    }
                    catch (NotSupportedException error) { preparedUnsupported = error; }
                    // Keep the existing whole-batch corruption/unsupported
                    // precedence unchanged. The bounded local source is read
                    // again (<=8MiB); its pipe has a separate native handle.
                    streams[preparedInput].Position = 0;
                }
                {
                    // A request owns every stream before readers start. The
                    // batch retains owner-only guards and joins on all exits.
                    request.IndexBatch = new();
                    var sources=streams.Select((s,n)=>new ReplayTransportSource(s,request.Inputs[n].Extent.Length)).ToArray();
                    if(request.PreparedInputs==null)
                        indices.AddRange(ReplayTransportBatchIndex.Read(sources,request.Info.FramesPerSecond,request.Info.RecordAudio,4,
                            checkStorage,request.IndexBatch,cancellableReads:cancellableIndex,readDeadline:TimeSpan.FromSeconds(30),lifetime:request.IndexReaders));
                    else
                        indices.AddRange(request.PreparedAcquisition.Read(sources,request.Inputs.Select(i=>request.PreparedInputs.Index(i.Path)).ToArray(),
                            request.Info.FramesPerSecond,request.Info.RecordAudio,checkStorage,request.IndexBatch,request.IndexReaders));
                    request.Timing.PreparedIndexHits=request.PreparedAcquisition.Hits;
                    request.Timing.PreparedIndexMisses=request.PreparedInputs==null?sources.Length:request.PreparedAcquisition.Misses;
                    request.Timing.IndexAcquisitionComplete=request.PreparedInputs==null
                        ?indices.Count==sources.Length && request.IndexReaders.Joined:request.PreparedAcquisition.Complete;
                    request.Timing.IndexReadersStarted=request.IndexReaders.Started;
                    request.Timing.IndexReadersJoined=request.IndexReaders.Joined;
                }
                if (preparedUnsupported != null) throw preparedUnsupported;
                var indexed = request.Inputs.Select((s, n) => new ReplayTransportInput(s.Start, s.End, indices[n])).ToArray();
                plan = ReplayHybridPlan.Create(indexed,
                    request.Interval, request.Info.FramesPerSecond, request.Info.RecordAudio);
                if (request.Info.RecordAudio)
                {
                    var planning = new ReplayPlanningGuard(checkStorage);
                    planning.CheckNow();
                    audio = ReplayAacCopyPlan.Create(indexed, plan, request.Interval, planning.Check);
                    planning.CheckNow();
                }
                request.Timing.IndexCompleted = Stopwatch.GetTimestamp();
            }
            catch (NotSupportedException ex)
            {
                // Only explicit valid-but-unsupported layout can select this
                // path. Recognized corruption/storage/identity failures escape.
                Log.Write("Replay hybrid ineligible: " + ex.Message);
                request.Preparation?.Cancel();
                if (request.Preparation != null) request.NativeFiles.AdoptPreparedBoundary();
                request.SelectRecipe(ReplayExactExport.Recipe);
                RunFallback(); return;
            }
            if (request.Preparation == null && (Directory.Exists(request.WorkDirectory) || File.Exists(request.WorkDirectory)))
                throw new IOException("Replay construction workspace already exists.");
            if (request.Preparation != null)
            {
                request.Preparation.Complete(request, plan, indices[preparedInput]);
                request.NativeFiles.AdoptPreparedBoundary();
            }
            else if (request.NativeFiles != null) request.NativeFiles.CreateWorkspace();
            else if (!CreateWorkDirectory(request.WorkDirectory, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot create an exclusive replay workspace.");
            request.OwnWorkDirectory = true;
            request.SelectRecipe(RecipeFor(construction));
            var boundaries = new Dictionary<int, (string Path, ReplayTransportIndex Index)>();
            request.Timing.BoundaryStarted = Stopwatch.GetTimestamp();
            foreach (var part in plan.Parts.Where(p => p.Count > 0 && p.First != 0))
            {
                if (boundaries.Count >= 2) throw new IOException("Replay construction exceeds two partial boundaries.");
                string path = ReplayOutputWorkspace.BoundaryPath(request.Partial, boundaries.Count);
                if (part.Input != preparedInput) run(BoundaryArguments(request.Info, request.Inputs[part.Input].Path, part.First, part.Count, path));
                using var source = request.NativeFiles?.OpenInput(path, request.NativeFiles.Extent(path), false) ?? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
                if (source.Length > BoundaryLimit) throw new IOException("Replay boundary exceeds its recipe envelope.");
                var index = ReplayTransportReader.Read(source, source.Length, request.Info.FramesPerSecond, false, checkStorage);
                if (index.Pictures.Count != part.Count) throw new IOException("Replay boundary encoder changed its physical picture count.");
                if (!index.Sps.Span.SequenceEqual(indices[part.Input].Sps.Span) || !index.Pps.Span.SequenceEqual(indices[part.Input].Pps.Span))
                {
                    // This is a legal codec incompatibility, before assembly or
                    // final output exists. Keep scratch until claim release;
                    // fallback starts its own ordinary output/producer.
                    Log.Write("Replay boundary configuration requires the existing exporter.");
                    request.Timing.BoundaryCompleted = Stopwatch.GetTimestamp();
                    request.SelectRecipe(ReplayExactExport.Recipe);
                    RunFallback(); return;
                }
                boundaries.Add(part.Input, (path, index));
            }
            request.Timing.BoundaryCompleted = Stopwatch.GetTimestamp();
            FreezeProof(request, plan, indices, boundaries, audio, checkStorage);
            request.Timing.MuxStarted = Stopwatch.GetTimestamp();
            request.Closure.BeginPacketMux();
            ReplayPacketMuxExport.Run(request, streams, indices, plan, boundaries, audio, CheckWorkspace);
            request.Timing.MuxCompleted = Stopwatch.GetTimestamp();
        }
        catch (Exception error) { primaryFailure = error; throw; }
        finally
        {
            Exception preparationCloseFailure = null;
            try { if (request.Preparation != null && !request.Preparation.Joined) request.Preparation.Cancel(); }
            catch (Exception error) { preparationCloseFailure = error; }
            if(!request.IndexReaders.Joined || !request.FallbackIndexReaders.Joined || !request.PacketReaders.Joined)
            {
                request.NativeFiles?.RetainUncertain();
                throw new IOException("Replay source readers have not joined; their stream owners remain retained.",primaryFailure);
            }
            if (request.ExternalProducersAbsent?.Invoke() == false)
            {
                request.NativeFiles?.RetainUncertain();
                request.Closure.CompleteSourceClose(false);
                throw new IOException("External replay child absence is unconfirmed; immutable source owners remain retained.", primaryFailure);
            }
            try{CloseSources(request.SourceProtection.Inputs, request.Timing, checkStorage, primaryFailure, request.Closure);}
            finally
            {
                if(request.Closure.SourcesClosed)
                {
                    try{request.SourceProtection.ReleaseDependencies();}
                    catch{request.NativeFiles?.RetainUncertain();throw;}
                }
            }
            if (preparationCloseFailure != null && primaryFailure == null)
                throw new IOException("Staged producer cleanup failed.", preparationCloseFailure);
        }
        // No catch/retry after any assembly/write/hash/producer failure. Its
        // entire unpublished workspace is invalid; ReplayCache owns cleanup.
    }
    internal static void CloseSources(IEnumerable<IDisposable> streams, ReplaySaveTiming timing, Action checkStorage, Exception primaryFailure,
        ReplayConstructionClosure closure = null)
    {
        closure?.BeginSourceClose();
        timing.SourceCloseStarted = Stopwatch.GetTimestamp();
        timing.SourceHandlesClosed = true;
        bool handlesClosed = true;
        Exception closingFailure = null; long checkedAt = Stopwatch.GetTimestamp();
        void CheckClosure()
        {
            try { checkStorage?.Invoke(); }
            catch (Exception error) { closingFailure ??= error; }
            checkedAt = Stopwatch.GetTimestamp();
        }
        CheckClosure();
        foreach (var stream in streams)
        {
            try { stream.Dispose(); }
            catch (Exception error) { handlesClosed = false; timing.SourceHandlesClosed = false; closingFailure ??= error; }
            if (Stopwatch.GetElapsedTime(checkedAt).TotalMilliseconds >= 50) CheckClosure();
        }
        CheckClosure();
        timing.SourceCloseCompleted = Stopwatch.GetTimestamp();
        closure?.CompleteSourceClose(handlesClosed);
        if (closingFailure != null)
        {
            timing.SourceCloseError = ReplayOutputDemand.Bounded(closingFailure.ToString(), 1024);
            // Close every remaining handle and retain the original cause.
            // A successful construction still fails on closure/guard loss.
            if (primaryFailure == null) throw new IOException("Replay source closure or storage handoff failed.", closingFailure);
        }
    }
    static void FreezeProof(ReplayExportRequest request, ReplayHybridPlan plan, IReadOnlyList<ReplayTransportIndex> indices,
        IReadOnlyDictionary<int, (string Path, ReplayTransportIndex Index)> boundaries, ReplayAacCopyPlan audio, Action checkStorage)
    {
            request.Timing.ProofStarted = Stopwatch.GetTimestamp();
            var planning = new ReplayPlanningGuard(checkStorage);
            planning.CheckNow();
            var expected = new List<ReplayExpectedPicture>();
            foreach (var part in plan.Parts.Where(p => p.Count > 0))
            {
                planning.Check();
                var pictures = part.First == 0 ? indices[part.Input].Pictures.Take(part.Count) : boundaries[part.Input].Index.Pictures;
                foreach (var picture in pictures) expected.Add(new(picture.Idr, picture.CodedSha256, picture.PacketSha256));
            }
            if (expected.Count != plan.EndFrame - plan.FirstFrame) throw new IOException("Replay source proof lost physical pictures.");
            request.FreezeIntegrity(new ReplayMp4Expectation(request.Info.FramesPerSecond,
                plan.FirstFrame / (double)request.Info.FramesPerSecond - request.Interval.Start,
                indices[0].Sps.Span, indices[0].Pps.Span, expected, audio?.PrimingSamples ?? 0, audio?.Expected));
            planning.CheckNow();
            request.Timing.ProofCompleted = Stopwatch.GetTimestamp();
    }

    internal static IReadOnlyList<string> BoundaryArguments(ReplaySessionInfo info, string input, int first, int count, string output) => [
        "-hide_banner", "-loglevel", "warning", "-xerror", "-err_detect", "explode", "-nostdin", "-y", "-i", input,
        "-map", "0:v:0", "-an", "-vf", "trim=start_frame=" + first + ":end_frame=" + checked(first + count) + ",setpts=PTS-STARTPTS",
        "-fps_mode", "passthrough", "-enc_time_base:v", "1:48000", "-pix_fmt", "yuv420p", "-c:v", info.Codec, "-preset", "p3", "-tune", "ll", "-rc", "cbr",
        "-b:v", info.BitrateMbps + "M", "-maxrate", info.BitrateMbps + "M", "-bufsize", info.BitrateMbps * 2 + "M",
        "-g", info.FramesPerSecond.ToString(CultureInfo.InvariantCulture), "-bf", "0", "-forced-idr", "1", "-flags", "+cgop", "-f", "mpegts", output ];

}
