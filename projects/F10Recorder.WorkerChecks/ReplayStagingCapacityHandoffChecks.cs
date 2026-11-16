using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace F10Recorder;

// Actual native Cache lifecycle with tiny dummy output and canceled simulated
// transport. This is not staged-media construction or decoder qualification.
internal static class ReplayStagingCapacityHandoffChecks
{
    sealed class Source(string directory) : IReplaySessionSource
    {
        readonly Native.ProcessJob job = new(recoverable: true);
        internal readonly ManualResetEventSlim QueryEntered = new(), QueryRelease = new();
        internal int PreparationOwner, Validations, Stops, QueriesCompleted;
        internal ReplayFileExtent CheckedOutput;
        ReplaySavePreparation preparation;
        bool childStopped, stopped;
        int blockNext;
        public ReplayProducerProof ProducerProof => ReplayProducerProof.Current(job.Name, true);
        public ReplaySessionInfo ReplayInfo => new(directory, Path.Combine(directory, "segments.csv"), 1, 20, 60, "h264_nvenc", ".ts", false, false);
        public bool SupportsOwnedExport => true;
        public bool SupportsStagedPreparation => true;
        public bool PreserveEvidence => true;
        public bool Stopped => stopped;
        public bool OutputProducerStopped => preparation?.Joined ?? true;
        public string OutputDemandIdentity => "controlled-staging-capacity-handoff";
        public string Diagnostics => "Tiny native lifecycle control, no media or capture.";
        public List<Segment> ReadSegments() => [new() { Path = Path.Combine(directory, "chunk0.ts"), Start = 0, End = 1 },
            new() { Path = Path.Combine(directory, "chunk1.ts"), Start = 1, End = 2 }];
        public double SaveCutoffAt(long _) => 1.75;
        public void FreezeAt(long _) { }
        public void StopCapture() { stopped = true; Interlocked.Increment(ref Stops); }
        public void CheckCaptureHealth(double _) { }
        public ReplayOutputDemand EstimateOutput(ReplayOutputSelection selection)
        {
            var capacity = ReplayStorage.ObserveCapacity(selection.Destination);
            return new(ReplayOutputEvidenceKind.SourceBound, "controlled-tiny-source", selection.Identity, OutputDemandIdentity,
                capacity.Volume, capacity.AllocationUnit, 32768, true, null, 131072, new(8192, 8192));
        }
        public ReplayOutputDemand RecheckOutputDemand(ReplayOutputSelection selection)
        {
            if (Interlocked.CompareExchange(ref blockNext, 0, 1) == 1)
            {
                QueryEntered.Set();
                if (!QueryRelease.Wait(1500)) throw new IOException("Controlled late capacity query release deadline.");
                Interlocked.Increment(ref QueriesCompleted);
            }
            return EstimateOutput(selection);
        }
        public ReplaySavePreparation BeginPreparation(ReplayRootedFiles files, ReplayInterval interval, Segment candidate, string partial,
            Action accepted, Action<ReplayProducerProof> changed, Action check, Action<ReplaySavePreparation> take)
        {
            PreparationOwner = Environment.CurrentManagedThreadId; childStopped = false;
            preparation = new(files, ReplayInfo, interval, candidate, partial, check); take(preparation);
            preparation.Start((_, retained) => new ReplayStagedProcess((guard, ready, _) => {
                changed(ProducerProof);
                using var sink = new MemoryStream(); ready(sink, () => Volatile.Write(ref childStopped, true));
                while (!Volatile.Read(ref childStopped)) { guard(); Thread.Sleep(1); }
                return new() { ProducerAbsent = true, ActualExitObserved = true, ExitCode = 0, CaptureComplete = true, ResourcesFinal = true };
            }, accepted, check, 3000, retained));
            return preparation;
        }
        public void Export(IReadOnlyList<string> arguments, string snapshot) => throw new IOException("Request-aware control required.");
        public void Export(ReplayExportRequest request, string snapshot, Action<ReplayProducerProof> changed, Action check)
        {
            preparation.Cancel(); request.NativeFiles.AdoptPreparedBoundary();
            request.SelectRecipe(ReplayExactExport.Recipe); request.NativeFiles.BeginExternal(request.Partial);
            using (var writer = new FileStream(request.Partial, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            { writer.Write(new byte[2048]); writer.Flush(true); }
            request.NativeFiles.CompleteExternal(request.Partial);
        }
        public ReplayValidation ValidateOutput(ReplayExportRequest request, Action<ReplayProducerProof> changed, Action check)
        {
            var lease = new ReplayPublicationLease(request.Partial); CheckedOutput = lease.CheckedIdentity;
            request.FreezeValidatedOutput(lease);
            if (Interlocked.Increment(ref Validations) == 1) Volatile.Write(ref blockNext, 1);
            return new("FullDecode", 60, 0, 60, ExportRecipe: ReplayExactExport.Recipe);
        }
        public void Dispose() { QueryRelease.Set(); job.Dispose(); QueryEntered.Dispose(); QueryRelease.Dispose(); }
    }
    internal static object Run(string root)
    {
        Directory.CreateDirectory(root);
        string inputs = Path.Combine(root, "inputs"), output = Path.Combine(root, "output");
        Directory.CreateDirectory(inputs); Directory.CreateDirectory(output);
        File.WriteAllBytes(Path.Combine(inputs, "chunk0.ts"), new byte[188]);
        File.WriteAllBytes(Path.Combine(inputs, "chunk1.ts"), new byte[188]);
        using var source = new Source(inputs);
        using var queue = new BlockingCollection<TaskCompletionSource>();
        var completionThreads = new ConcurrentBag<int>();
        var completion = new Thread(() => {
            foreach (var pending in queue.GetConsumingEnumerable())
            { Thread.Sleep(10); completionThreads.Add(Environment.CurrentManagedThreadId); pending.TrySetResult(); }
        }) { IsBackground = true, Name = "controlled-capacity-await-completion" };
        completion.Start();
        var diagnostics = new ConcurrentQueue<string>();
        int delayCalls = 0, requestClosures = 0, publicationThread = 0;
        var ledger = new ReplayVolumeReservations(Path.Combine(root, "authority"));
        ledger.BeforeRequestCloseForChecks = () => Interlocked.Increment(ref requestClosures);
        using var cache = new ReplayCache(cacheDirectory: inputs, cacheBudgetBytes: 1_000_000,
            volumeReservations: ledger, diagnostic: diagnostics.Enqueue);
        cache.CapacityDelayForChecks = () => {
            Interlocked.Increment(ref delayCalls);
            var pending = new TaskCompletionSource(); // Intentionally inline on the dedicated completion thread.
            queue.Add(pending); return pending.Task;
        };
        cache.SerializeNamespaceMutationForChecks = action => { publicationThread = Environment.CurrentManagedThreadId; action(); };
        var id = cache.RegisterSession(source);
        SaveSubmission first = null;
        try
        {
            first = cache.TrySave(id, Stopwatch.GetTimestamp(), output);
            if (!first.Accepted || !source.QueryEntered.Wait(2000)) throw new IOException("Actual late read-only capacity policy did not block.");
            var stop = Stopwatch.StartNew(); cache.StopAcquisitionAsync(id).WaitAsync(TimeSpan.FromMilliseconds(500)).GetAwaiter().GetResult();
            double stopMilliseconds = stop.Elapsed.TotalMilliseconds;
            Thread.Sleep(100); source.QueryRelease.Set();
            var saved = first.Completion.WaitAsync(TimeSpan.FromSeconds(6)).GetAwaiter().GetResult();
            int firstOwner = source.PreparationOwner, firstPublicationThread = publicationThread;
            var extent = saved.File == null ? null : ReplayStorage.Observe(saved.File);
            bool checkedPublicationPreserved = saved.Succeeded && extent != null && extent.Identity == source.CheckedOutput.Identity
                && extent.Volume == source.CheckedOutput.Volume && extent.Length == 2048;
            var second = cache.TrySave(id, Stopwatch.GetTimestamp(), output);
            ReplaySaveResult repeated = second.Accepted ? second.Completion.WaitAsync(TimeSpan.FromSeconds(6)).GetAwaiter().GetResult() : null;
            using var claims = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "authority", "replay-volume-reservations.json")));
            int remainingClaims = claims.RootElement.GetProperty("Owners").GetArrayLength();
            bool authoritySessionClosed = !cache.AuthoritySessionOwnedForChecks;
            var rows = diagnostics.ToArray();
            var result = new { checkedPublicationPreserved, firstSaved = saved.Succeeded, saved.Error, stopMilliseconds, source.Stops,
                originalOwnerThread = firstOwner, firstPublicationThread, delayCalls, completionThreads = completionThreads.ToArray(),
                actualQueryCompletions = source.QueriesCompleted, secondAccepted = second.Accepted, second.Rejection,
                repeatedSaved = repeated?.Succeeded, requestClosures, remainingClaims, authoritySessionClosed, diagnostics = rows,
                scope = "Actual native Cache/reservation/publication/cleanup; canceled simulated staged transport and controlled dummy validation, not media/feed/decoder qualification." };
            File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            if (!checkedPublicationPreserved || !second.Accepted || repeated?.Succeeded != true || requestClosures != 2
                || delayCalls != 0 || firstPublicationThread != firstOwner || source.QueriesCompleted != 1
                || remainingClaims != 0 || !authoritySessionClosed || !source.OutputProducerStopped
                || rows.Any(s => s.Contains("closure uncertain", StringComparison.Ordinal) || s.Contains("registry remains retained", StringComparison.Ordinal)))
                throw new IOException("Staged late-capacity cleanup/re-admission regression: " + JsonSerializer.Serialize(result));
            return result;
        }
        finally
        {
            source.QueryRelease.Set();
            if (first?.Completion != null) try { first.Completion.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult(); } catch (Exception) { }
            queue.CompleteAdding();
            if (!completion.Join(2000)) throw new IOException("Controlled capacity completion thread did not join.");
        }
    }
}
