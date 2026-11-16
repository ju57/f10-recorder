using System.Text.Json;

namespace F10Recorder;

// Pure reproduction of the qualification adapter's paired request boundary.
// The live view advances only after the primary request has protected its list.
internal static class ReplayPairedSelectionChecks
{
    sealed class Source(int seconds, List<Segment> segments, string directory = "unused", double cutoff = double.NaN,
        bool manifest = false) : IReplaySessionSource
    {
        internal List<Segment> Current = segments;
        internal int Reads;
        public ReplaySessionInfo ReplayInfo => new(directory, Path.Combine(directory, "segments.csv"), seconds, 20, 60, "h264_nvenc", ".ts", true, true);
        public bool PreserveEvidence => true;
        public bool OutputProducerStopped => true; // This controlled source starts no producer or codec.
        public bool Stopped => true;
        public string Diagnostics => "pure paired-selection trace";
        public List<Segment> ReadSegments() { Reads++; return manifest ? ReplaySelection.Read(ReplayInfo.Manifest, directory) : Current.Select(Copy).ToList(); }
        public double SaveCutoffAt(long _) => double.IsFinite(cutoff) ? cutoff : throw new NotSupportedException();
        public void FreezeAt(long _) { }
        public void StopCapture() { }
        public void CheckCaptureHealth(double _) { }
        public void Export(IReadOnlyList<string> _, string __) => throw new NotSupportedException("No export is part of this pure check.");
        public void Dispose() { }
    }
    static Segment Copy(Segment s) => new() { Path = s.Path, Start = s.Start, End = s.End };

    internal static int RunNative(string root)
    {
        if (Directory.Exists(root) || File.Exists(root)) throw new IOException("Preserve previous paired-selection fixture.");
        Directory.CreateDirectory(root);
        string directory = Path.Combine(root, "buffer", "session-fixture"), clips = Path.Combine(root, "clips");
        Directory.CreateDirectory(directory); Directory.CreateDirectory(clips);
        var segments = Enumerable.Range(0, 3).Select(n => new Segment
            { Path = Path.Combine(directory, $"chunk-{n + 1:D9}.ts"), Start = n, End = n + 1 }).ToList();
        foreach (var segment in segments) File.WriteAllBytes(segment.Path, Enumerable.Repeat((byte)0x5a, 1024).ToArray());
        void Manifest(IEnumerable<Segment> selected) => File.WriteAllText(Path.Combine(directory, "segments.csv"),
            string.Join('\n', selected.Select(s => $"{Path.GetFileName(s.Path)},{s.Start},{s.End}")) + "\n");
        Manifest(segments);
        ReplayPairedSelectionLease held = null;
        ReplayExportRequest first = null;
        object report = null;
        var observations = new List<object>();
        using var source = new Source(2, segments, directory, 2.75, manifest: true);
        using var cache = new ReplayCache(new ReplayFiles(ReplayFileMode.NativeInventoryForChecks), cacheDirectory: Path.Combine(root, "buffer"), cacheBudgetBytes: 1_000_000);
        ReplaySaveResult Save(ReplayCalibrationRun.ShortSource wrapped)
        {
            var id = cache.RegisterSession(wrapped);
            var submission = cache.TrySave(id, 0, clips);
            if (!submission.Accepted) throw new IOException(submission.Rejection);
            return submission.Completion.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
        try
        {
            using var primary = new ReplayCalibrationRun.ShortSource(source, 2, 2.75, exactFallback: true, protect: request => {
                first = request; held = new ReplayPairedSelectionLease(request, "controlled-pair", null);
                throw new IOException("controlled-primary-protected-before-codec");
            }, preserveEvidence: true);
            var initial = Save(primary);
            if (held == null || first == null || initial.Succeeded || initial.Error != "controlled-primary-protected-before-codec")
                throw new IOException("Primary request did not reach actual protected-input ownership.");
            Manifest(segments.Skip(1)); // Same failure pattern: inventory advances; protected objects remain.
            bool matched = false, wrongIntervalRejected = false, wrongIdentityRejected = false;
            using var paired = new ReplayCalibrationRun.ShortSource(source, 2, 2.75, exactFallback: true, protect: request => {
                held.Verify(request, "controlled-pair", null); matched = true;
                try { held.Verify(request with { Interval = request.Interval with { Start = 1 } }, "controlled-pair", null); }
                catch (IOException e) when (e.Message == "Paired requests do not select the same ordered protected source objects and interval.") { wrongIntervalRejected = true; }
                var wrong = request.Inputs.ToArray();
                wrong[0] = wrong[0] with { Extent = wrong[0].Extent with { Identity = "controlled-wrong-object" } };
                try { held.Verify(request with { Inputs = wrong }, "controlled-pair", null); }
                catch (IOException e) when (e.Message == "Paired requests do not select the same ordered protected source objects and interval.") { wrongIdentityRejected = true; }
                observations.Add(new { first.Interval, pairedInterval = request.Interval, inputs = request.Inputs.Count,
                    nativeIdentities = request.Inputs.Select(i => i.Extent.Identity).ToArray() });
                throw new IOException("controlled-pair-verified-before-codec");
            }, preserveEvidence: true, protectedSegments: held.ReadSegments);
            var result = Save(paired);
            if (!matched || !wrongIntervalRejected || !wrongIdentityRejected || result.Succeeded || result.Error != "controlled-pair-verified-before-codec")
                throw new IOException("Actual cache paired selection/strict negative did not complete: " + result.Error);
            bool changedViewRejected = false;
            using var unprotected = new ReplayCalibrationRun.ShortSource(source, 2, 2.75, exactFallback: true,
                protect: request => held.Verify(request, "controlled-pair", null), preserveEvidence: true);
            var rejected = Save(unprotected);
            changedViewRejected = !rejected.Succeeded && rejected.Error == "Paired requests do not select the same ordered protected source objects and interval.";
            if (!changedViewRejected) throw new IOException("Unprotected changed view was not rejected.");
            report = new {
                result = "passed-native-cache-paired-selection-protection", matched, wrongIntervalRejected, wrongIdentityRejected, changedViewRejected,
                observations, attemptedSaves = 3, actualMediaExports = 0, childProcesses = 0, playableMedia = false,
                scope = "Actual Cache snapshot/hardlink and actual paired lease/strict guard with 3x1024B nonplayable inputs. Stops before codec; no publication/decoder/capture qualification claim."
            };
        }
        finally { held?.Dispose(); cache.CloseAsync().GetAwaiter().GetResult(); }
        File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new { checks = report, cacheCloseCompleted = true },
            new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Passed native Cache paired-selection protection and strict negatives; no codec/capture ran.");
        return 0;
    }

    internal static int Run()
    {
        int failed = 0;
        foreach (var c in new[] { (Seconds: 2, End: 2.75, Origin: 0d, Count: 3, Removed: 1),
            (Seconds: 1800, End: 1802.7849148, Origin: 1.983333, Count: 1801, Removed: 3) })
        {
            var original = Enumerable.Range(0, c.Count).Select(n => new Segment
                { Path = $"chunk-{n:D9}.ts", Start = c.Origin + n, End = c.Origin + n + 1 }).ToList();
            var expected = ReplayInterval.AtRequest(original, c.End, c.Seconds);
            expected.RequireCoverage(original);
            var protectedInputs = original.Select(Copy).ToList();
            using var source = new Source(c.Seconds, original);
            // The real failing order: primary protection, normal save/maintenance,
            // source stop, then construction of the paired qualification source.
            source.Current = original.Skip(c.Removed).Select(Copy).ToList();
            int protectedReads = 0;
            using var paired = new ReplayCalibrationRun.ShortSource(source, c.Seconds, c.End, exactFallback: true,
                protectedSegments: () => { protectedReads++; return protectedInputs.Select(Copy).ToList(); });
            var observedInputs = paired.ReadSegments();
            var observed = ReplayInterval.AtRequest(observedInputs, paired.SaveCutoffAt(0), paired.ReplayInfo.ReplaySeconds);
            bool same = observed == expected && observedInputs.Count == protectedInputs.Count
                && observedInputs.Zip(protectedInputs).All(p => p.First.Path == p.Second.Path && p.First.Start == p.Second.Start && p.First.End == p.Second.End);
            Console.WriteLine(JsonSerializer.Serialize(new { c.Seconds, expected, observed, same, liveReads = source.Reads, protectedReads }));
            if (!same) failed++;
            using var unprotected = new ReplayCalibrationRun.ShortSource(source, c.Seconds, c.End);
            var liveInterval = ReplayInterval.AtRequest(unprotected.ReadSegments(), c.End, c.Seconds);
            bool honestLimitedHistory = liveInterval != expected && liveInterval.LimitedHistory;
            Console.WriteLine(JsonSerializer.Serialize(new { c.Seconds, liveInterval, honestLimitedHistory }));
            if (!honestLimitedHistory) failed++;
        }
        if (failed != 0) { Console.Error.WriteLine($"Paired selection changed after live-source pruning in {failed} cases."); return 1; }
        Console.WriteLine("Passed qualification paired-selection preservation checks.");
        return 0;
    }
}
