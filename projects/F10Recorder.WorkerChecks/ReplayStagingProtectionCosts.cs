using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;

namespace F10Recorder;

// File-only attribution on the exact selected Windows/Core build. Dummy TS and
// MP4 bytes never enter a codec: this cannot qualify media or worker latency.
internal static class ReplayStagingProtectionCosts
{
    // Reflection is restricted to this otherwise idle, private test Cache. It
    // invokes the unchanged evidence method, never an accepted save or producer.
    // No normal guard/callback is intercepted, skipped or replaced.
    internal static int EvidenceCosts(string root)
    {
        if (!root.StartsWith(Path.GetFullPath("artifacts") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || Directory.Exists(root) || File.Exists(root)) throw new IOException("Fresh isolated component root required.");
        Directory.CreateDirectory(root);
        var clock = Stopwatch.StartNew();
        void Bound() { if (clock.Elapsed.TotalSeconds >= 10) throw new IOException("Evidence component deadline."); }
        using var files = new ReplayRootedFiles(); files.Root(root);
        string partial = Path.Combine(root, "owned.partial.mp4");
        var info = new ReplaySessionInfo(root, Path.Combine(root, "unused.csv"), 300, 20, 60, "h264_nvenc", ".ts", false, false);
        using var preparation = new ReplaySavePreparation(files, info, new(.75, 300.75, 300),
            new Segment { Path = Path.Combine(root, "unused.ts"), Start = 0, End = 1 }, partial, Bound);
        using var cache = new ReplayCache(cacheDirectory: root, cacheBudgetBytes: 1024 * 1024);
        var volume = ReplayStorage.ObserveCapacity(root);
        var demand = new ReplayOutputDemand(ReplayOutputEvidenceKind.SourceBound, "component-only", "unused", "unused",
            volume.Volume, volume.AllocationUnit, 32768, true, null, 131072, new(8192, 8192));
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
        var claimType = typeof(ReplayCache).GetNestedType("OutputClaim", BindingFlags.NonPublic)
            ?? throw new IOException("Exact diagnostic Cache shape changed.");
        var constructor = claimType.GetConstructors(flags).Single();
        object claim = constructor.Invoke([null, root, partial, volume, demand, null]);
        var snapshot = typeof(ReplayCache).GetField("nativeSnapshot", flags) ?? throw new IOException("Native snapshot seam changed.");
        var method = typeof(ReplayCache).GetMethod("ObserveOutputEvidence", flags) ?? throw new IOException("Evidence method changed.");
        var namespaces = new Dictionary<string, ReplayNamespaceIdentity>();
        int expectedMissing = 0;
        void FirstChance(object sender, FirstChanceExceptionEventArgs args)
        {
            if (args.Exception is FileNotFoundException && args.Exception.TargetSite?.DeclaringType == typeof(ReplayCache)
                && args.Exception.TargetSite.Name == "ObserveOutputEvidence") expectedMissing++;
        }
        void Full(ReplayNativeExportScope.Snapshot observed)
        {
            snapshot.SetValue(cache, observed);
            try
            {
                if ((long)method.Invoke(cache, [claim, namespaces]) != 0) throw new IOException("Absent main output allocated bytes.");
            }
            finally { snapshot.SetValue(cache, null); }
        }
        // Establish the exception shape separately; stack metadata inspection is
        // deliberately absent from every timed arm.
        AppDomain.CurrentDomain.FirstChanceException += FirstChance;
        try { for (int i = 0; i < 301; i++) { Bound(); using var observed = preparation.Observe(); Full(observed); } }
        finally { AppDomain.CurrentDomain.FirstChanceException -= FirstChance; }
        if (expectedMissing != 301) throw new IOException("Expected absence attribution shape changed.");
        var rows = new List<object>();
        foreach (bool full in new[] { false, true, true, false })
        {
            long bytes = GC.GetAllocatedBytesForCurrentThread(), started = Stopwatch.GetTimestamp();
            for (int i = 0; i < 301; i++)
            {
                Bound(); using var observed = preparation.Observe();
                if (full) Full(observed);
            }
            rows.Add(new { fullEvidenceMethod = full, calls = 301, milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - bytes, firstChanceObserverAttached = false });
        }
        File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new { result = "collected-native-output-evidence-component",
            rows, untimedShapeCalls = 301, expectedAbsenceExceptions = expectedMissing, seconds = clock.Elapsed.TotalSeconds, realProcesses = 0,
            claim = "Untimed301-throw shape check, then four fixed301-call local/full/full/local timed loops with counter DETACHED. Includes full-arm reflection overhead; excludes global policy, linking and accepted-save/worker/media behavior. Expected-absence exceptions are counted, not faults ignored." },
            new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
        return 0;
    }
    sealed class Source(string directory, bool staged) : IReplaySessionSource
    {
        readonly Native.ProcessJob job = new(recoverable: true);
        ReplaySavePreparation preparation;
        bool childStopped;
        int disposed;
        internal int Preparations, GuardCalls;
        internal double GuardMilliseconds;
        public ReplayProducerProof ProducerProof => ReplayProducerProof.Current(job.Name, true);
        public ReplaySessionInfo ReplayInfo => new(directory, Path.Combine(directory, "unused.csv"), 300, 20, 60, "h264_nvenc", ".ts", false, false);
        public bool SupportsOwnedExport => true;
        public bool SupportsStagedPreparation => staged;
        public bool PreserveEvidence => true;
        public bool Stopped => true;
        public bool OutputProducerStopped => preparation?.Joined ?? true;
        public string OutputDemandIdentity => "controlled-301-input-protection-cost";
        public string Diagnostics => "Dummy file-only native ownership attribution; no codec or capture.";
        public List<Segment> ReadSegments() => Enumerable.Range(0, 302).Select(i => new Segment {
            Path = Path.Combine(directory, $"chunk{i:D3}.ts"), Start = i, End = i + 1 }).ToList();
        public double SaveCutoffAt(long _) => 301.75;
        public void FreezeAt(long _) { }
        public void StopCapture() { }
        public void CheckCaptureHealth(double _) { }
        public ReplayOutputDemand EstimateOutput(ReplayOutputSelection selection)
        {
            var capacity = ReplayStorage.ObserveCapacity(selection.Destination);
            return new(ReplayOutputEvidenceKind.SourceBound, "controlled-dummy-files", selection.Identity, OutputDemandIdentity,
                capacity.Volume, capacity.AllocationUnit, 32768, true, null, 4 * 1024 * 1024, new(8192, 8192));
        }
        public ReplaySavePreparation BeginPreparation(ReplayRootedFiles files, ReplayInterval interval, Segment candidate, string partial,
            Action accepted, Action<ReplayProducerProof> changed, Action check, Action<ReplaySavePreparation> take)
        {
            Preparations++;
            preparation = new(files, ReplayInfo, interval, candidate, partial, check); take(preparation);
            void TracedGuard()
            {
                long start = Stopwatch.GetTimestamp();
                try { check(); }
                finally { GuardCalls++; GuardMilliseconds += Stopwatch.GetElapsedTime(start).TotalMilliseconds; }
            }
            preparation.Start((_, retained) => new ReplayStagedProcess((guard, ready, _) => {
                changed(ProducerProof);
                using var sink = new MemoryStream(); ready(sink, () => Volatile.Write(ref childStopped, true));
                while (!Volatile.Read(ref childStopped)) { guard(); Thread.Sleep(100); }
                return new() { ProducerAbsent = true, ActualExitObserved = true, ExitCode = 0, CaptureComplete = true, ResourcesFinal = true };
            }, accepted, TracedGuard, 10000, retained));
            return preparation;
        }
        public void Export(IReadOnlyList<string> arguments, string snapshot) => throw new IOException("Request-aware control required.");
        public void Export(ReplayExportRequest request, string snapshot, Action<ReplayProducerProof> changed, Action check)
        {
            if (preparation != null) { preparation.Cancel(); request.NativeFiles.AdoptPreparedBoundary(); }
            request.SelectRecipe(ReplayExactExport.Recipe); request.NativeFiles.BeginExternal(request.Partial);
            using (var writer = new FileStream(request.Partial, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            { writer.Write(new byte[2048]); writer.Flush(true); }
            request.NativeFiles.CompleteExternal(request.Partial);
        }
        public ReplayValidation ValidateOutput(ReplayExportRequest request, Action<ReplayProducerProof> changed, Action check)
        {
            request.FreezeValidatedOutput(new ReplayPublicationLease(request.Partial));
            return new("FullDecode", 18000, 0, 60, ExportRecipe: ReplayExactExport.Recipe);
        }
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) job.Dispose(); }
    }

    static double Elapsed(ReplaySaveTiming timing, long? start, long? end) => start.HasValue && end.HasValue
        ? (end.Value - start.Value) * 1000.0 / timing.Frequency : double.NaN;

    internal static int Run(string root)
    {
        string artifacts = Path.GetFullPath("artifacts") + Path.DirectorySeparatorChar;
        if (!root.StartsWith(artifacts, StringComparison.OrdinalIgnoreCase) || Directory.Exists(root) || File.Exists(root))
            throw new IOException("Fresh isolated artifact root required.");
        Directory.CreateDirectory(root);
        var clock = Stopwatch.StartNew();
        void Bound() { if (clock.Elapsed.TotalSeconds >= 25) throw new IOException("Whole attribution deadline."); }
        var rows = new List<object>();
        foreach (var (label, staged) in new[] { ("OFF1", false), ("ON1", true), ("ON2", true), ("OFF2", false) })
        {
            Bound();
            string arm = Path.Combine(root, label), inputs = Path.Combine(arm, "inputs"), output = Path.Combine(arm, "output");
            Directory.CreateDirectory(inputs); Directory.CreateDirectory(output);
            foreach (int i in Enumerable.Range(0, 302)) File.WriteAllBytes(Path.Combine(inputs, $"chunk{i:D3}.ts"), new byte[188]);
            using var source = new Source(inputs, staged);
            var diagnostics = new List<string>();
            var ledger = new ReplayVolumeReservations(Path.Combine(arm, "authority"));
            int closed = 0; ledger.BeforeRequestCloseForChecks = () => Interlocked.Increment(ref closed);
            using var cache = new ReplayCache(cacheDirectory: inputs, cacheBudgetBytes: 8 * 1024 * 1024,
                volumeReservations: ledger, diagnostic: diagnostics.Add);
            var id = cache.RegisterSession(source);
            var submission = cache.TrySave(id, Stopwatch.GetTimestamp(), output);
            if (!submission.Accepted) throw new IOException("Attribution admission rejected: " + submission.Rejection);
            var result = submission.Completion.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            var timing = result.Timing;
            using var claims = JsonDocument.Parse(File.ReadAllText(Path.Combine(arm, "authority", "replay-volume-reservations.json")));
            int remaining = claims.RootElement.GetProperty("Owners").GetArrayLength();
            var row = new { label, staged, result.Succeeded, result.Error, result.Milliseconds, timing,
                protectionMilliseconds = Elapsed(timing, timing.ProtectionStarted, timing.Protected),
                nonLinkProtectionMilliseconds = Elapsed(timing, timing.ProtectionStarted, timing.Protected) - timing.HardLinkMilliseconds,
                hardLinkedInputs = timing.HardLinkedInputs, source.Preparations, source.GuardCalls, source.GuardMilliseconds,
                guardScope = "Simulated worker RPC checks only, not explicit per-link phase observations.", closed, remaining,
                authoritySessionClosed = !cache.AuthoritySessionOwnedForChecks, source.OutputProducerStopped, diagnostics,
                scope = "Actual native Cache with301 hardlinks, canceled simulated stage and controlled2048byte publication. No playable media, decode, worker or device." };
            rows.Add(row);
            File.WriteAllText(Path.Combine(arm, "result.json"), JsonSerializer.Serialize(row, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
            if (!result.Succeeded || timing.HardLinkedInputs != 301 || source.Preparations != (staged ? 1 : 0)
                || !source.OutputProducerStopped || closed != 1 || remaining != 0 || cache.AuthoritySessionOwnedForChecks)
                throw new IOException("Attribution did not preserve the actual known-closed native path: " + result.Error);
        }
        Bound();
        // Direct lower-bound attribution for the extra per-link local observation.
        // Global policy and ObserveOutputEvidence costs are deliberately excluded.
        string local = Path.Combine(root, "local-observation"); Directory.CreateDirectory(local);
        using (var files = new ReplayRootedFiles())
        {
            files.Root(local);
            var info = new ReplaySessionInfo(local, Path.Combine(local, "unused.csv"), 300, 20, 60, "h264_nvenc", ".ts", false, false);
            using var preparation = new ReplaySavePreparation(files, info, new(.75, 300.75, 300),
                new Segment { Path = Path.Combine(local, "unused.ts"), Start = 0, End = 1 }, Path.Combine(local, "owned.partial.mp4"), Bound);
            double total = 0, maximum = 0;
            for (int i = 0; i < 301; i++)
            {
                Bound(); long start = Stopwatch.GetTimestamp();
                using (var observed = preparation.Observe())
                    if (observed.Work.Count != 1 || observed.Main != null || observed.RequiresConservative)
                        throw new IOException("Local attribution workspace changed.");
                double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                total += elapsed; maximum = Math.Max(maximum, elapsed);
            }
            File.WriteAllText(Path.Combine(local, "result.json"), JsonSerializer.Serialize(new { calls = 301, totalMilliseconds = total,
                maximumMilliseconds = maximum, realProcesses = 0, scope = "Actual preparation.Observe plus snapshot disposal only; no global policy/ObserveOutputEvidence, producer or file linking." }));
        }
        long bytes = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        if (bytes >= 32 * 1024 * 1024) throw new IOException("Attribution logical byte bound.");
        File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new { result = "collected-file-only-staging-protection-costs",
            rows, seconds = clock.Elapsed.TotalSeconds, bytes, realProcesses = 0,
            claim = "Prespecified OFF/ON/ON/OFF actual native Cache controls and301 direct local observations. Dummy validation is not decode; no absolute performance threshold, media/worker acceptance or removal of guards." },
            new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
        Console.WriteLine("Collected file-only protection attribution; no worker/media latency claim.");
        return 0;
    }
}
