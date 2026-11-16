using System.Diagnostics;
using System.Text.Json;

namespace F10Recorder;

// Exercises actual SaveAsync with native request files and an unmeasured source.
// No encoder, playable media, acquisition, audio or UI is involved.
internal static class ReplayStagingAdmissionChecks
{
    sealed class TailWaitSource(string directory, string output) : IReplaySessionSource
    {
        internal int Exports, Validations;
        internal bool PreparationStarted => preparation != null;
        internal ReplayFileExtent[] ExportInputs;
        readonly Native.ProcessJob job = new(recoverable: true);
        ReplaySavePreparation preparation;
        bool childStopped;
        bool stopped;
        public ReplaySessionInfo ReplayInfo => new(directory, Path.Combine(directory, "segments.csv"), 1, 20, 60, "h264_nvenc", ".ts", false, false);
        public bool SupportsOwnedExport => true;
        public bool SupportsStagedPreparation => true;
        public bool PreserveEvidence => true;
        public bool Stopped => stopped;
        public bool OutputProducerStopped => preparation?.Joined ?? true;
        public ReplayProducerProof ProducerProof => ReplayProducerProof.Current(job.Name, true);
        public string OutputDemandIdentity => "controlled-reserved-staging-tail-wait";
        public string Diagnostics => "Tiny reserved-startup/post-tail protection control; no media or capture.";
        public List<Segment> ReadSegments() => ReplaySelection.Read(ReplayInfo.Manifest, directory);
        public double SaveCutoffAt(long _) => 1.75;
        public void FreezeAt(long _) { }
        public void StopCapture() => stopped = true;
        public void CheckCaptureHealth(double _) { }
        public ReplayOutputDemand EstimateOutput(ReplayOutputSelection selection)
        {
            var capacity = ReplayStorage.ObserveCapacity(output);
            return new(ReplayOutputEvidenceKind.SourceBound, "controlled-reserved-staging-tail-wait-v1",
                selection.Identity, OutputDemandIdentity, capacity.Volume, capacity.AllocationUnit, 32768,
                true, null, 16384, new(8192, 8192));
        }
        public ReplaySavePreparation BeginPreparation(ReplayRootedFiles files, ReplayInterval interval, Segment candidate, string partial,
            Action accepted, Action<ReplayProducerProof> changed, Action check, Action<ReplaySavePreparation> take)
        {
            childStopped = false;
            preparation = new(files, ReplayInfo, interval, candidate, partial, check);
            take(preparation);
            preparation.Start((_, retained) => new ReplayStagedProcess((guard, ready, _) => {
                changed(ProducerProof);
                using var sink = new MemoryStream();
                ready(sink, () => Volatile.Write(ref childStopped, true));
                while (!Volatile.Read(ref childStopped)) { guard(); Thread.Sleep(1); }
                return new() { ProducerAbsent = true, ActualExitObserved = true, ExitCode = 0, CaptureComplete = true, ResourcesFinal = true };
            }, accepted, check, 3000, retained));
            return preparation;
        }
        public void Export(IReadOnlyList<string> arguments, string snapshot) => throw new IOException("Request-aware control required.");
        public void Export(ReplayExportRequest request, string snapshot, Action<ReplayProducerProof> changed, Action check)
        {
            Exports++;
            ExportInputs = request.Inputs.Select(input => input.Extent).ToArray();
            preparation.Cancel();
            request.SelectRecipe(ReplayExactExport.Recipe);
            request.NativeFiles.BeginExternal(request.Partial);
            using (var writer = new FileStream(request.Partial, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            { writer.Write(new byte[2048]); writer.Flush(true); }
            request.NativeFiles.CompleteExternal(request.Partial);
        }
        public ReplayValidation ValidateOutput(ReplayExportRequest request, Action<ReplayProducerProof> changed, Action check)
        {
            Validations++;
            var lease = new ReplayPublicationLease(request.Partial);
            request.FreezeValidatedOutput(lease);
            return new("FullDecode", 120, 0, 120, ExportRecipe: ReplayExactExport.Recipe);
        }
        public void Dispose() => job.Dispose();
    }

    sealed class Source(string directory, bool reserved = false) : IReplaySessionSource
    {
        internal int Exports, Preparations, Stops;
        readonly Native.ProcessJob job = reserved ? new(recoverable: true) : null;
        ReplaySavePreparation preparation;
        bool childStopped;
        public ReplayProducerProof ProducerProof => job == null ? null : ReplayProducerProof.Current(job.Name, true);
        public ReplaySessionInfo ReplayInfo => new(directory, Path.Combine(directory, "segments.csv"), 1, 20, 60, "h264_nvenc", ".ts", false, false);
        public bool SupportsOwnedExport => true;
        public bool SupportsStagedPreparation => true;
        public bool PreserveEvidence => true;
        public bool Stopped => true;
        public bool OutputProducerStopped => preparation?.Joined ?? true;
        public string OutputDemandIdentity => reserved ? "controlled-staging-reservation" : null;
        public ReplayOutputDemand EstimateOutput(ReplayOutputSelection selection)
        {
            if (!reserved) return ReplayOutputDemand.Unmeasured("Controlled unmeasured source.");
            var capacity = ReplayStorage.ObserveCapacity(selection.Destination);
            return new(ReplayOutputEvidenceKind.SourceBound, "controlled-tiny-source", selection.Identity, OutputDemandIdentity,
                capacity.Volume, capacity.AllocationUnit, 32768, true, null, 131072, new(8192, 8192));
        }
        public string Diagnostics => "Unmeasured admission control; no process.";
        public List<Segment> ReadSegments() => [new() { Path = Path.Combine(directory, "chunk0.ts"), Start = 0, End = 1 },
            new() { Path = Path.Combine(directory, "chunk1.ts"), Start = 1, End = 2 }];
        public double SaveCutoffAt(long _) => 1.75;
        public void FreezeAt(long _) { }
        public void StopCapture() { Interlocked.Increment(ref Stops); }
        public void CheckCaptureHealth(double _) { }
        public ReplaySavePreparation BeginPreparation(ReplayRootedFiles files, ReplayInterval interval, Segment candidate, string partial,
            Action accepted, Action<ReplayProducerProof> changed, Action check, Action<ReplaySavePreparation> take)
        {
            Preparations++;
            if (!reserved) throw new IOException("Unmeasured source must not start preparation.");
            childStopped = false;
            preparation = new(files, ReplayInfo, interval, candidate, partial, check);
            take(preparation);
            preparation.Start((_, retained) => new ReplayStagedProcess((guard, ready, _) => {
                // Actual Cache callback, from the launch worker, with the same
                // held request authority session as the save owner.
                changed(ProducerProof);
                using var sink = new MemoryStream();
                ready(sink, () => Volatile.Write(ref childStopped, true));
                while (!Volatile.Read(ref childStopped)) { guard(); Thread.Sleep(1); }
                return new() { ProducerAbsent = true, ActualExitObserved = true, ExitCode = 0, CaptureComplete = true, ResourcesFinal = true };
            }, accepted, check, 3000, retained));
            return preparation;
        }
        public void Export(IReadOnlyList<string> arguments, string snapshot) => throw new IOException("Request-aware control expected.");
        public void Export(ReplayExportRequest request, string snapshot, Action<ReplayProducerProof> changed, Action check)
        {
            if (!reserved && request.Preparation != null) throw new IOException("Unmeasured request received preparation.");
            Exports++;
            if (reserved)
            {
                preparation.Cancel();
            }
            throw new IOException("EXPECTED-UNSTAGED-EXPORT-ENTRY");
        }
        public void Dispose() { job?.Dispose(); }
    }
    static object ProducerCallback(string root)
    {
        string inputs = Path.Combine(root, "inputs"), output = Path.Combine(root, "output"), authority = Path.Combine(root, "authority");
        Directory.CreateDirectory(inputs); Directory.CreateDirectory(output);
        File.WriteAllBytes(Path.Combine(inputs, "chunk0.ts"), new byte[188]);
        File.WriteAllBytes(Path.Combine(inputs, "chunk1.ts"), new byte[188]);
        using var source = new Source(inputs, reserved: true);
        var ledger = new ReplayVolumeReservations(authority);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        int closed = 0;
        ledger.BeforeSetProducerForChecks = () => { entered.Set(); if (!release.Wait(3000)) throw new IOException("Producer callback release deadline."); };
        ledger.BeforeRequestCloseForChecks = () => Interlocked.Increment(ref closed);
        var diagnostics = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var cache = new ReplayCache(cacheDirectory: inputs, cacheBudgetBytes: 1_000_000, volumeReservations: ledger, diagnostic: diagnostics.Enqueue);
        var id = cache.RegisterSession(source);
        var submission = cache.TrySave(id, Stopwatch.GetTimestamp(), output);
        if (!submission.Accepted) throw new IOException("Reserved staging admission failed: " + submission.Rejection);
        double stopMilliseconds;
        bool claimHeld, sessionHeld;
        try
        {
            if (!entered.Wait(1000)) throw new IOException("Actual started-worker producer callback was not reached.");
            var stopping = Stopwatch.StartNew();
            cache.StopAcquisitionAsync(id).WaitAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            stopMilliseconds = stopping.Elapsed.TotalMilliseconds;
            using var currentLedger = JsonDocument.Parse(File.ReadAllText(Path.Combine(authority, "replay-volume-reservations.json")));
            claimHeld = currentLedger.RootElement.GetProperty("Owners").GetArrayLength() > 0;
            sessionHeld = Volatile.Read(ref closed) == 0 && !submission.Completion.IsCompleted;
            if (!claimHeld || !sessionHeld || source.Stops == 0) throw new IOException("Blocked callback released ownership or blocked the stop request.");
        }
        finally { release.Set(); }
        var saved = submission.Completion.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        File.WriteAllLines(Path.Combine(root, "cache-diagnostics.log"), diagnostics);
        if (saved.Succeeded || saved.Error?.Contains("EXPECTED-UNSTAGED-EXPORT-ENTRY", StringComparison.Ordinal) != true
            || source.Preparations != 1 || source.Exports != 1 || Volatile.Read(ref closed) != 1 || !source.OutputProducerStopped)
            throw new IOException("Staged callback did not join and close its actual authority session normally: " + saved.Error);
        var repeated = cache.TrySave(id, Stopwatch.GetTimestamp(), output);
        if (!repeated.Accepted) throw new IOException("Known canceled preparation still blocked the next request: " + repeated.Rejection);
        var repeatedResult = repeated.Completion.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        if (repeatedResult.Succeeded || repeatedResult.Error?.Contains("EXPECTED-UNSTAGED-EXPORT-ENTRY", StringComparison.Ordinal) != true
            || source.Preparations != 2 || source.Exports != 2 || Volatile.Read(ref closed) != 2 || !source.OutputProducerStopped
            || Directory.EnumerateFiles(output, "*.mp4").Any())
            throw new IOException("Known canceled preparation did not preserve failed/unpublished result and release ownership.");
        File.WriteAllLines(Path.Combine(root, "cache-diagnostics-after-repeat.log"), diagnostics);
        return new { passed = true, stopMilliseconds, claimHeld, sessionHeld, requestSessionsClosed = closed,
            knownCanceledFailureReadmitted = true, bothUnpublished = true, realChildren = 0 };
    }

    static object ReservedStagingDuringTailWait(string root)
    {
        string inputs = Path.Combine(root, "inputs"), output = Path.Combine(root, "output");
        Directory.CreateDirectory(inputs); Directory.CreateDirectory(output);
        string first = Path.Combine(inputs, "chunk0.ts"), tail = Path.Combine(inputs, "chunk1.ts");
        File.WriteAllBytes(first, new byte[4096]);
        File.WriteAllText(Path.Combine(inputs, "segments.csv"), "chunk0.ts,0,1\n");
        var firstExtent = ReplayStorage.Observe(first);
        int waits = 0; bool prefixProtectedDuringWait = false, preparationStartedDuringWait = false;
        using var source = new TailWaitSource(inputs, output);
        Task FinishTail()
        {
            Interlocked.Increment(ref waits);
            preparationStartedDuringWait |= source.PreparationStarted;
            var snapshots = Directory.GetDirectories(inputs, "save-*");
            if (snapshots.Length == 1)
            {
                string protectedPrefix = Path.Combine(snapshots[0], "000000.ts");
                if (File.Exists(protectedPrefix))
                {
                    var observed = ReplayStorage.Observe(protectedPrefix);
                    prefixProtectedDuringWait = observed.Volume == firstExtent.Volume
                        && observed.Identity == firstExtent.Identity && observed.Length == firstExtent.Length;
                }
            }
            if (!File.Exists(tail))
            {
                File.WriteAllBytes(tail, new byte[4096]);
                File.AppendAllText(Path.Combine(inputs, "segments.csv"), "chunk1.ts,1,2\n");
            }
            return Task.CompletedTask;
        }
        using var cache = new ReplayCache(waitForSegment: FinishTail, cacheDirectory: inputs, cacheBudgetBytes: 1_000_000,
            volumeReservations: new ReplayVolumeReservations(Path.Combine(root, "authority")), diagnostic: _ => { });
        var id = cache.RegisterSession(source);
        var submission = cache.TrySave(id, Stopwatch.GetTimestamp(), output);
        if (!submission.Accepted) throw new IOException("Closed-prefix overlap control was not admitted: " + submission.Rejection);
        var saved = submission.Completion.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        bool completeProtectedSet = source.ExportInputs?.Length == 2
            && source.ExportInputs[0].Identity == firstExtent.Identity
            && source.ExportInputs[1].Identity == ReplayStorage.Observe(tail).Identity;
        bool postTailProtection = saved.Timing?.PreTailProtectedInputs == 0 && saved.Timing.PreTailProtectedBytes == 0
            && saved.Timing.PreTailProtectionMilliseconds == 0
            && saved.Timing.ProtectionStarted >= saved.Timing.TailClosed;
        if (prefixProtectedDuringWait || !preparationStartedDuringWait || !saved.Succeeded
            || source.Exports != 1 || source.Validations != 1 || !completeProtectedSet || !postTailProtection)
            throw new IOException("Reserved staging must start during tail wait; complete input protection must follow tail closure: "
                + JsonSerializer.Serialize(new { prefixProtectedDuringWait, preparationStartedDuringWait, saved.Succeeded, saved.Error,
                    waits, source.Exports, source.Validations, completeProtectedSet, postTailProtection }));
        return new { prefixProtectedDuringWait, preparationStartedDuringWait, saved = saved.Succeeded, waits,
            source.Exports, source.Validations, completeProtectedSet, postTailProtection };

    }

    internal static int Run(string root)
    {
        if (Directory.Exists(root) || File.Exists(root)) throw new IOException("Fresh private control root required.");
        Directory.CreateDirectory(root);
        string inputs = Path.Combine(root, "inputs"), output = Path.Combine(root, "output");
        Directory.CreateDirectory(inputs); Directory.CreateDirectory(output);
        File.WriteAllBytes(Path.Combine(inputs, "chunk0.ts"), new byte[188]);
        File.WriteAllBytes(Path.Combine(inputs, "chunk1.ts"), new byte[188]);
        var source = new Source(inputs);
        using var cache = new ReplayCache(cacheDirectory: root, cacheBudgetBytes: 1_000_000, diagnostic: _ => { });
        var session = cache.RegisterSession(source);
        var submission = cache.TrySave(session, Stopwatch.GetTimestamp(), output);
        if (!submission.Accepted) throw new IOException("Control admission failed: " + submission.Rejection);
        var saved = submission.Completion.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        bool passed = !saved.Succeeded && saved.Error?.Contains("EXPECTED-UNSTAGED-EXPORT-ENTRY", StringComparison.Ordinal) == true
            && source.Exports == 1 && source.Preparations == 0 && !Directory.EnumerateFiles(output, "*.mp4").Any();
        object workspace = null;
        if (passed)
        {
            var deadline = Stopwatch.StartNew();
            workspace = ReplayStagedWorkspaceChecks.Run(Path.Combine(root, "workspace"), () => {
                if (deadline.ElapsedMilliseconds > 5000) throw new IOException("Staged workspace control deadline.");
            });
        }
        var producer = passed ? ProducerCallback(Path.Combine(root, "producer-callback")) : null;
        var capacityHandoff = passed ? ReplayStagingCapacityHandoffChecks.Run(Path.Combine(root, "capacity-handoff")) : null;
        var reservedStagingDuringTailWait = passed ? ReservedStagingDuringTailWait(Path.Combine(root, "reserved-staging-tail-wait")) : null;
        File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new { passed, saved.Error, source.Exports, source.Preparations, workspace, producer, capacityHandoff, reservedStagingDuringTailWait,
            claim = "Actual Cache accepted unmeasured source and reached ordinary export without launching preparation or falsely publishing." }));
        Console.WriteLine(passed ? "Passed: unmeasured native Cache retains unstaged export path." : "FAILED: " + saved.Error);
        return passed ? 0 : 1;
    }
}
