using System.Globalization;

namespace F10Recorder;

internal static partial class ReplayCalibrationRun
{
    sealed class NativeEvidence(string cell, string sourceDirectory, Func<long> controllerClock = null, Func<List<Segment>> completedSegments = null, bool excludeInitial = false)
    {
        readonly object evidenceGate = new();
        readonly string archive = Path.Combine(cell, "raw-evidence");
        readonly Dictionary<string, Segment> raw = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> outputs = new(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> rawIdentities = new(StringComparer.OrdinalIgnoreCase);
        readonly List<object> operations = new();
        long lastPoll; double maximumPollGap;
        string archiveVolume; long allocationUnit;
        internal IReadOnlyCollection<string> OutputAliases { get { lock (evidenceGate) return outputs.Values.ToArray(); } }
        internal void RecordTiming(string operation, long at)
        {
            double milliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(at).TotalMilliseconds;
            lock (evidenceGate) { Require(operations.Count < 1024, "Evidence operation timing cap exceeded."); operations.Add(new { operation, atQpc = at, endedQpc = System.Diagnostics.Stopwatch.GetTimestamp(), milliseconds }); }
        }
        internal void WriteTimings() { lock (evidenceGate) Write(Path.Combine(cell, "evidence-timings.json"), new { operations, aliases = outputs }); }
        internal void CaptureClosed(bool acquiring = false)
        {
            lock (evidenceGate)
            {
            long now = controllerClock?.Invoke() ?? System.Diagnostics.Stopwatch.GetTimestamp();
            if (acquiring)
            {
                if (lastPoll != 0)
                {
                    double gap = System.Diagnostics.Stopwatch.GetElapsedTime(lastPoll, now).TotalMilliseconds;
                    maximumPollGap = Math.Max(maximumPollGap, gap); Require(gap < 5000, "Raw archive controller polling exceeded five seconds.");
                }
                lastPoll = now;
            }
            Directory.CreateDirectory(archive); bool changed = false;
            foreach (var segment in completedSegments?.Invoke() ?? ReplaySelection.Read(Path.Combine(sourceDirectory, "segments.csv"), sourceDirectory))
            {
                string name = Path.GetFileName(segment.Path);
                if (excludeInitial && name.Equals("chunk-000000000.ts", StringComparison.OrdinalIgnoreCase)) continue;
                if (raw.ContainsKey(name)) continue;
                Require(raw.Count < 1024, "Raw evidence segment cap exceeded.");
                string link = Path.Combine(archive, name);
                Require(Native.CreateHardLink(link, segment.Path, IntPtr.Zero), "Completed eligible raw evidence link failed for " + name
                    + " (Win32 " + System.Runtime.InteropServices.Marshal.GetLastWin32Error() + ").");
                var original = ReplayStorage.Observe(segment.Path); var saved = ReplayStorage.Observe(link);
                Require(original.Volume == saved.Volume && original.Identity == saved.Identity && original.Length == saved.Length,
                    "Raw evidence identity changed while linking.");
                archiveVolume ??= saved.Volume; if (allocationUnit == 0) allocationUnit = saved.AllocationUnit;
                Require(saved.Volume == archiveVolume && saved.AllocationUnit == allocationUnit, "Raw evidence volume/unit changed.");
                raw.Add(name, new Segment { Path = link, Start = segment.Start, End = segment.End }); changed = true;
                rawIdentities.Add(saved.Volume + ":" + saved.Identity);
            }
            if (changed)
                File.WriteAllText(Path.Combine(archive, "segments.csv"), string.Concat(raw.Values.OrderBy(s => s.Start).Select(s =>
                    Path.GetFileName(s.Path) + "," + s.Start.ToString("R", CultureInfo.InvariantCulture) + "," + s.End.ToString("R", CultureInfo.InvariantCulture) + "\n")));
            }
        }
        internal void RequireNativeSnapshot(IReadOnlyList<string> args, ReplayOutputSelection binding)
        {
            lock (evidenceGate)
            {
            CaptureClosed(); int input = args.ToList().IndexOf("-i"); Require(input >= 0 && input + 1 < args.Count, "Snapshot input is absent.");
            string snapshot = Path.GetDirectoryName(args[input + 1]);
            Require(binding is { Closed: true } && binding.Inputs.Count is >= 1 and <= 305, "Actual closed snapshot binding is absent.");
            for (int i = 0; i < binding.Inputs.Count; i++)
            {
                var item = binding.Inputs[i]; var extent = item.Extent;
                Require(item.Path.Equals(Path.Combine(snapshot, i.ToString("D6", CultureInfo.InvariantCulture) + ".ts"), StringComparison.OrdinalIgnoreCase), "Snapshot binding belongs to another input directory/order.");
                Require(rawIdentities.Contains(extent.Volume + ":" + extent.Identity), "Snapshot used payload-copy fallback; abort before encoder launch and admit no next source.");
            }
            }
        }
        internal void PreserveOutput(string path)
        {
            long at = System.Diagnostics.Stopwatch.GetTimestamp();
            lock (evidenceGate)
            {
            if (outputs.ContainsKey(path) || !File.Exists(path)) return;
            Require(Path.GetFullPath(path).StartsWith(Path.GetFullPath(cell).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) && outputs.Count < 16, "Evidence output is outside this owned case or exceeds its cap.");
            string alias = Path.Combine(cell, "evidence-output-" + outputs.Count + ".mp4");
            Require(Native.CreateHardLink(alias, path, IntPtr.Zero), "Output evidence hardlink failed.");
            // CreateHardLink supplies the retained physical object. Opening its
            // finalizing MP4 metadata here can stall the product's freshness
            // window. Verify identities after accepted work is terminal.
            outputs.Add(path, alias);
            }
            RecordTiming("preserve-output-hardlink", at);
        }
        internal void Seal(string published = null)
        {
            lock (evidenceGate)
            {
            CaptureClosed();
            var outputProof = outputs.Select(pair =>
            {
                string original = File.Exists(pair.Key) ? pair.Key : published;
                var saved = ReplayStorage.Observe(pair.Value);
                Require(original != null && File.Exists(original), "Terminal output identity needs its retained partial or published file.");
                var extent = ReplayStorage.Observe(original);
                Require(saved.Identity == extent.Identity && saved.Volume == extent.Volume && saved.Length == extent.Length, "Terminal output evidence identity changed.");
                return new { original, alias = pair.Value, saved.Identity, saved.Volume, saved.Length };
            }).ToArray();
            Write(Path.Combine(archive, "receipt.json"), new { sourceDirectory, archiveVolume, allocationUnit, completed = raw.Count, maximumPollGap,
                files = raw.Values.Select(s => { var extent = ReplayStorage.Observe(s.Path); return new { name = Path.GetFileName(s.Path), s.Start, s.End, extent.Identity, extent.Length, extent.AllocatedBytes }; }), outputs, outputProof, operations });
            }
        }
    }
    sealed class EvidenceSource(IReplaySessionSource inner, NativeEvidence evidence) : IReplaySessionSource
    {
        public ReplaySessionInfo ReplayInfo => inner.ReplayInfo;
        public bool PreserveEvidence => true;
        public bool Stopped => inner.Stopped;
        public bool OutputProducerStopped => inner.OutputProducerStopped;
        public ReplayProducerProof ProducerProof => inner.ProducerProof;
        public string OutputDemandIdentity => inner.OutputDemandIdentity;
        public ReplayOutputDemand OutputDemandStatus => inner.OutputDemandStatus;
        public string Diagnostics => inner.Diagnostics;
        public double SaveCutoffAt(long at) => inner.SaveCutoffAt(at);
        public void FreezeAt(long at) => inner.FreezeAt(at);
        public void StopCapture() => inner.StopCapture();
        public void CheckCaptureHealth(double end) => inner.CheckCaptureHealth(end);
        public List<Segment> ReadSegments() => inner.ReadSegments();
        public ReplayOutputDemand EstimateOutput(ReplayOutputSelection selection) => inner.EstimateOutput(selection);
        public void Export(IReadOnlyList<string> args, string snapshot) => throw new IOException("Guarded evidence export required.");
        public void Export(IReadOnlyList<string> args, string snapshot, Action<ReplayProducerProof> changed, Action check)
        { try { inner.Export(args, snapshot, proof => { long at = System.Diagnostics.Stopwatch.GetTimestamp(); try { changed(proof); } finally { evidence.RecordTiming("export-producer-changed", at); } }, check); } finally { evidence.PreserveOutput(args[^1]); } }
        public ReplayValidation ValidateOutput(string path, ReplayInterval interval, Action<ReplayProducerProof> changed, Action check)
        { try { return inner.ValidateOutput(path, interval, proof => { long at = System.Diagnostics.Stopwatch.GetTimestamp(); try { changed(proof); } finally { evidence.RecordTiming("validation-producer-changed", at); } }, check); } finally { evidence.PreserveOutput(path); } }
        public void Dispose() => inner.Dispose();
    }

    internal static object NativeEvidenceChecks(string root)
    {
        Require(!Directory.Exists(root), "Fresh evidence control root required.");
        string source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "chunk-000000000.ts"), "closed-zero");
        File.WriteAllText(Path.Combine(source, "chunk-000000001.ts"), "closed-one");
        string manifest = Path.Combine(source, "segments.csv"); File.WriteAllText(manifest, "chunk-000000000.ts,0,1\nchunk-000000001.ts,1,2");
        long controllerClock = 1;
        var evidence = new NativeEvidence(root, source, () => controllerClock); evidence.CaptureClosed();
        Require(File.Exists(Path.Combine(root, "raw-evidence", "chunk-000000000.ts")) && !File.Exists(Path.Combine(root, "raw-evidence", "chunk-000000001.ts")), "Incomplete manifest row was archived.");
        File.AppendAllText(manifest, "\n");
        Task.WhenAll(Task.Run(() => { for (int n = 0; n < 10; n++) evidence.CaptureClosed(); }),
            Task.Run(() => { for (int n = 0; n < 10; n++) evidence.CaptureClosed(); })).GetAwaiter().GetResult();
        string snapshot = Path.Combine(root, "save-control"); Directory.CreateDirectory(snapshot);
        string pinned = Path.Combine(snapshot, "000000.ts"), original = Path.Combine(source, "chunk-000000000.ts");
        Require(Native.CreateHardLink(pinned, original, IntPtr.Zero), "Native snapshot control link failed.");
        var binding = new ReplayOutputSelection("control", "control", root, 1, true,
            Array.AsReadOnly(new[] { new ReplayOutputInput(pinned, 0, 1, ReplayStorage.Observe(pinned)) }));
        string[] args = ["-i", Path.Combine(snapshot, "concat.txt")];
        evidence.RequireNativeSnapshot(args, binding);
        string copied = Path.Combine(root, "copied.ts"); File.Copy(original, copied);
        int rejectedSnapshots = 0;
        foreach (var invalid in new[] { binding with { Closed = false },
            binding with { Inputs = Array.AsReadOnly(new[] { binding.Inputs[0] with { Path = copied } }) },
            binding with { Inputs = Array.AsReadOnly(new[] { binding.Inputs[0] with { Extent = ReplayStorage.Observe(copied) } }) } })
        {
            bool rejected = false; try { evidence.RequireNativeSnapshot(args, invalid); } catch (IOException) { rejected = true; }
            Require(rejected, "Open, misbound or copied snapshot passed native identity admission."); rejectedSnapshots++;
        }
        File.Delete(Path.Combine(source, "chunk-000000000.ts"));
        Require(File.ReadAllText(Path.Combine(root, "raw-evidence", "chunk-000000000.ts")) == "closed-zero", "Ordinary pruning destroyed raw evidence.");
        string partial = Path.Combine(root, "owned.mp4.partial.mp4"); File.WriteAllBytes(partial, new byte[4096]); evidence.PreserveOutput(partial);
        var inventory = ReplayStorage.Inventory([root], new ReplayFiles());
        Require(ReplayStorage.Bytes(inventory.Where(i => i.Key.EndsWith(".mp4")).ToDictionary(i => i.Key, i => i.Value)) == 4096,
            "Evidence MP4 aliases were charged as duplicate payload.");
        File.Delete(partial); Require(File.Exists(evidence.OutputAliases.Single()), "Ordinary failed-partial cleanup destroyed evidence.");
        evidence.CaptureClosed(acquiring: true); controllerClock += 6 * System.Diagnostics.Stopwatch.Frequency; evidence.CaptureClosed();
        bool controllerGapRejected = false; try { evidence.CaptureClosed(acquiring: true); } catch (IOException) { controllerGapRejected = true; }
        Require(controllerGapRejected, "An export sweep hid a stale acquiring-controller poll.");
        string first = evidence.OutputAliases.Single(), second = Path.Combine(root, "alias-two.mp4");
        Require(Native.CreateHardLink(second, first, IntPtr.Zero), "Alias-release control link failed.");
        string history = Path.Combine(Path.GetDirectoryName(root), "metadata-history"); Directory.CreateDirectory(history);
        var plan = new CalibrationPlan(root, "unused", "alias-release-control", new(), Order, [history], 0, 0,
            BudgetBytes: 10_000_000_000, StopBytes: 9_000_000_000, DeadlineSeconds: 20, RetainedOfflineSeconds: 0,
            MetadataHandleRoots: [history], ReuseCurrentMetadataHandles: true, RetainedBudgetBytes: 1_000_000,
            FileLimit: 64, DirectoryLimit: 16, OfflineDeadlineSeconds: 3, OutsideBuildAllowanceBytes: 0, FreezeRetainedAllocations: true, RetainActiveOutputHandles: true);
        object releaseReceipt;
        using (var sampler = new Sampler(plan, active: false))
        {
            sampler.BeginActive(); var wait = System.Diagnostics.Stopwatch.StartNew();
            while (!sampler.RetainsMetadata(first) || !sampler.RetainsMetadata(second))
            { sampler.Check(); Require(wait.ElapsedMilliseconds < 2000, "Both output aliases were not observed."); Thread.Sleep(10); }
            sampler.EndActive(); sampler.BeginOffline("alias-release", 0, 3);
            var releases = sampler.ReleaseClosedOutputHandles([first, second]);
            Require(releases.All(r => r.MetadataHandleAbsent && r.Before.Identity == r.After.Identity && r.Before.Length == r.After.Length), "An alias metadata handle survived release.");
            sampler.EndOffline(); sampler.StopAndCheck(); releaseReceipt = releases;
        }
        string live = Path.Combine(root, "coordinated-manifest"); Directory.CreateDirectory(live);
        var stream = new ReplayManifestStream(live, ".ts", 305);
        var coordinated = new NativeEvidence(Path.Combine(root, "coordinated-archive"), live, completedSegments: stream.Read);
        using var stop = new CancellationTokenSource();
        using var readerStarted = new ManualResetEventSlim(); using var readDuringPublication = new ManualResetEventSlim();
        int publishing = 0;
        var reader = Task.Run(() => { readerStarted.Set(); while (!stop.IsCancellationRequested)
            { coordinated.CaptureClosed(); if (Volatile.Read(ref publishing) == 1
                && File.Exists(Path.Combine(root, "coordinated-archive", "raw-evidence", "chunk-000000000.ts"))) readDuringPublication.Set(); Thread.Yield(); } });
        try
        {
            Require(readerStarted.Wait(2000), "Concurrent archive reader did not start."); Volatile.Write(ref publishing, 1);
            for (int n = 0; n < 200; n++)
            {
                string name = "chunk-" + n.ToString("D9", CultureInfo.InvariantCulture) + ".ts";
                File.WriteAllBytes(Path.Combine(live, name), new byte[16]);
                Require(stream.Accept(name + "," + n + "," + (n + 1)), "Coordinated archive publisher rejected a complete entry.");
                stream.CheckHealthy();
                if (n == 100) Require(readDuringPublication.Wait(2000), "Archive reader never completed during the publication loop.");
            }
        }
        finally { Volatile.Write(ref publishing, 0); stop.Cancel(); Require(reader.Wait(5000), "Coordinated archive reader failed to stop."); }
        stream.Complete(); stream.WaitForCompletion(100); coordinated.CaptureClosed(); coordinated.Seal();
        Require(Directory.GetFiles(Path.Combine(root, "coordinated-archive", "raw-evidence"), "*.ts").Length == 200,
            "Coordinated native archive lost published inputs.");
        string startup = Path.Combine(root, "discarded-startup"); Directory.CreateDirectory(startup);
        File.WriteAllText(Path.Combine(startup, "segments.csv"), "chunk-000000000.ts,0,1\nchunk-000000001.ts,1,2\n");
        File.WriteAllText(Path.Combine(startup, "chunk-000000001.ts"), "eligible-after-startup");
        var eligible = new NativeEvidence(Path.Combine(root, "eligible-archive"), startup, excludeInitial: true);
        eligible.CaptureClosed(); eligible.Seal();
        Require(Directory.GetFiles(Path.Combine(root, "eligible-archive", "raw-evidence"), "*.ts").Select(Path.GetFileName)
            .SequenceEqual(new[] { "chunk-000000001.ts" }), "An already-discarded excluded startup segment blocked eligible archival.");
        return new { result = "passed", completeOnly = true, concurrentArchives = true, nativeSnapshotAccepted = true, rejectedSnapshots,
            controllerGapRejected, coordinatedManifestPublications = 200, archiveReadDuringPublication = readDuringPublication.IsSet,
            discardedStartupSkipped = true,
            sourcePruningPreserved = true, partialCleanupPreserved = true, aliasesDeduplicated = true,
            releaseReceipt,
            effects = "Tiny task-owned files and hardlinks; removes only the control's original names after preserving aliases. No processes, capture or media decoding." };
    }
}
