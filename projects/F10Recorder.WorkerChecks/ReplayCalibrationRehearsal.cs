using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace F10Recorder;

// Same native sampler/phase/archive code as collection, with in-process dummy
// writers. No recording engine, child, audio endpoint, window or GPU is opened.
internal static class ReplayCalibrationRehearsal
{
    static FileStream OpenDummy(string path, long bytes, bool preallocate) => new(path, new FileStreamOptions {
        Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.ReadWrite | FileShare.Delete,
        BufferSize = 1_048_576,
        // Tiny NTFS files may otherwise be resident in the MFT and report zero
        // separate data allocation. The fixture deliberately requires a real
        // ordinary cluster for every positive payload, including144-byte barcodes.
        PreallocationSize = preallocate ? bytes : bytes is > 0 and < 4096 ? 4096 : 0 });
    const int ChunkBytes = 3_000_000, Chunks = 310, Selected = 300;
    static readonly string[] Order = ["T01", "T02", "T03", "H02", "H01", "H03"];
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    sealed class DummyFiles(ReplayCalibrationRun.Sampler sampler, Action<long> copied) : ReplayFiles
    {
        internal override bool TryHardLink(string copy, string original) => false; // Exercise real fallback, not a second hard-link name.
        internal override void Copy(string original, string copy) { base.Copy(original, copy); copied(new FileInfo(copy).Length); }
        internal override void DeleteDirectory(string path) { } // Only verified archival retires dummy duplicates.
        internal override void DeleteFile(string path) { }
        internal override void Publish(string partial, string final) { lock (sampler.Gate) base.Publish(partial, final); }
    }
    sealed class DummySource(string session, int seconds, double cutoff, Action<IReadOnlyList<string>, string> export, int bitrate = 20) : IReplaySessionSource
    {
        public ReplaySessionInfo ReplayInfo => new(session, Path.Combine(session, "segments.csv"), seconds, bitrate, 60, "h264_nvenc", ".ts", false, true);
        public bool Stopped { get; private set; }
        public string Diagnostics => "Nonmedia fixed-length producer; not encoded video.";
        public string OutputDemandIdentity => "dummy-fixed-bytes-v1";
        public bool OutputProducerStopped { get; private set; } = true;
        public double SaveCutoffAt(long timestamp) => cutoff;
        public void FreezeAt(long timestamp) { }
        public void StopCapture() => Stopped = true;
        public void CheckCaptureHealth(double end) { }
        public ReplayOutputDemand EstimateOutput(ReplayOutputSelection selection)
        {
            var capacity = ReplayStorage.ObserveCapacity(selection.Destination);
            return new(ReplayOutputEvidenceKind.SourceBound, "dummy-exact-selected-bytes-v1", selection.Identity, selection.ExporterIdentity,
                capacity.Volume, capacity.AllocationUnit, ReplayReferenceModel.Round(selection.Inputs.Sum(i => i.Extent.Length), capacity.AllocationUnit), false, null);
        }
        public void Export(IReadOnlyList<string> arguments, string snapshot)
        {
            OutputProducerStopped = false;
            try { export(arguments, snapshot); }
            finally { OutputProducerStopped = true; } // Callback's owned synchronous file writer has closed.
        }
        public void Dispose() { }
    }
    static void MaintainIfGrowing(ReplayCache cache, ReplaySessionId id, IReplaySessionSource source)
    { if (!source.Stopped) cache.Maintain(id); }
    static void Require(bool value, string reason) { if (!value) throw new IOException(reason); }
    static string Hash(string file) { using var stream = File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(stream)); }
    static void Write(string file, object value)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        Require(bytes.Length <= 131072, "Rehearsal receipt exceeds128KiB.");
        using var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        output.Write(bytes); output.Flush(true);
    }
    static void BoundPath(string root, string path)
    {
        Require(Path.GetFullPath(path).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Dummy operation escaped its owned absolute root.");
        for (string at = path; at != null; at = Path.GetDirectoryName(at))
            if (File.Exists(at) || Directory.Exists(at)) Require((File.GetAttributes(at) & FileAttributes.ReparsePoint) == 0, "Dummy operation traverses a reparse point.");
    }
    internal static int Run(string planFile, string expectedHash)
    {
        Require(new FileInfo(planFile).Length <= 65536 && Hash(planFile) == expectedHash, "Rehearsal plan identity changed.");
        var plan = JsonSerializer.Deserialize<CalibrationPlan>(File.ReadAllText(planFile));
        ReplayCalibrationRun.ValidateResources(plan);
        foreach (var file in plan.Files) Require(Hash(file.Key) == file.Value, "Rehearsal runtime/input identity changed: " + file.Key);
        Require(plan.Files.ContainsKey(typeof(ReplayCalibrationRehearsal).Assembly.Location), "Rehearsal does not bind its own assembly.");
        Require(plan.Files.ContainsKey(Path.GetFullPath(Environment.ProcessPath)), "Rehearsal does not bind its actual dotnet host.");
        if (plan.RehearsalEvidenceRoot != null)
        {
            string evidence = plan.RehearsalEvidenceRoot;
            Require(plan.RetainedRoots.Contains(evidence, StringComparer.OrdinalIgnoreCase), "Rehearsal evidence destination was not charged.");
            using var marker = JsonDocument.Parse(File.ReadAllText(Path.Combine(evidence, PilotStorageObservation.Marker)));
            using var closure = JsonDocument.Parse(File.ReadAllText(Path.Combine(evidence, "dummy-closure.json")));
            Require(marker.RootElement.GetProperty("root_identity").GetString() == Native.DirectoryIdentity(evidence)
                && closure.RootElement.GetProperty("result").GetString() == "closed-dummy-evidence-preserved",
                "Rehearsal evidence destination is not the verified dummy-only closure root.");
            Require(ReplayStorage.Bytes(ReplayStorage.Inventory([evidence], new ReplayFiles(), 4096, 48)) <= 24_000_000_000,
                "Retained closed dummy evidence exceeds its24GB allowance.");
        }
        string run = Path.GetFullPath(plan.Root), raw = Path.Combine(run, "raw"), compact = Path.Combine(run, "compact");
        Require(!Directory.Exists(run) && !File.Exists(run), "Rehearsal needs a fresh owned root.");
        var clock = Stopwatch.StartNew();
        using var ownership = PilotStorageObservation.Create(run); // Atomic fresh-root ownership precedes every write.
        Directory.CreateDirectory(raw); Directory.CreateDirectory(compact);
        string ownerIdentity = Native.DirectoryIdentity(run);
        var stages = new List<object>();
        var block = new byte[1_048_576]; RandomNumberGenerator.Fill(block);
        ReplayCalibrationRun.Sampler sampler = null;
        string[] historical = plan.RetainedRoots.Where(r => !string.Equals(Path.GetFullPath(r),
            plan.RehearsalEvidenceRoot == null ? null : Path.GetFullPath(plan.RehearsalEvidenceRoot), StringComparison.OrdinalIgnoreCase)).ToArray();
        long nativeHistory = ReplayStorage.Bytes(ReplayStorage.Inventory(historical, new ReplayFiles(), 4096, 48));
        Require(nativeHistory <= 8_000_000_000, "Retained history exceeds its approved8GB allocation allowance.");
        double rawOffline = 0; int maximumFiles = 0, maximumDirectories = 0;
        double maximumActive = 0, maximumOffline = 0; long maximumUsed = 0;
        try
        {
            var rawPlan = plan with { Root = raw, RetainedRoots = [.. plan.RetainedRoots, compact] };
            sampler = new(rawPlan, active: false);
            for (int n = 0; n < 5; n++) Dummy(Path.Combine(raw, $"dummy-global-record-{n}.bin"), 131072);
            string controls = Path.Combine(raw, "controls"); Directory.CreateDirectory(controls);
            for (int n = 0; n < 4; n++) Dummy(Path.Combine(controls, $"engine-control-{n}.json"), 2_097_152);
            bool rejectedCapacity = false;
            try { sampler.BeginOffline("deliberate-shortage", plan.BudgetBytes, 5); }
            catch (IOException ex) when (ex.Message.Contains("cannot fit", StringComparison.Ordinal)) { rejectedCapacity = true; }
            Require(rejectedCapacity, "Full-budget shortage control was incorrectly admitted.");
            foreach (string label in Order)
            {
                Deadline();
                string cell = Path.Combine(raw, label), session = Path.Combine(cell, "buffer", "session-dummy");
                Directory.CreateDirectory(session);
                foreach (string name in new[] { "fixture-ready.json", "settings.json", "capture-arguments.json", "export-arguments-1.json",
                    "selection.jsonl", "sample.json", "capture-exit.json", "last-export-exit.json", "fixture-exit.json" })
                    Dummy(Path.Combine(cell, name), 131072);
                foreach (string name in new[] { "main-export", "last-export" })
                { Dummy(Path.Combine(cell, name + ".stdout.txt"), 0); Dummy(Path.Combine(cell, name + ".stderr.txt"), 2_097_152); }
                sampler.Check(3_100_000_000);
                AdmitStage(330);
                sampler.BeginActive();
                bool concurrent = label is not ("T01" or "H01");
                for (int n = 0; n < (concurrent ? Selected : Chunks); n++)
                {
                    Dummy(Path.Combine(session, $"chunk-{n:D9}.ts"), ChunkBytes);
                    File.AppendAllText(Path.Combine(session, "segments.csv"), $"chunk-{n:D9}.ts,{n},{n + 1}\n");
                }
                var snapshots = new List<string>();
                var outputs = new List<(string Name, long Bytes)>();
                using var tailCancel = new CancellationTokenSource();
                Task tail = null; int outputActive = 0, overlappingWrites = 0; long outputWritten = 0;
                using var cache = new ReplayCache(new DummyFiles(sampler, bytes => Interlocked.Add(ref outputWritten, bytes)),
                    cacheDirectory: Path.Combine(cell, "buffer"), diagnostic: text =>
                    {
                        string log = Path.Combine(cell, "replay-diagnostics.log");
                        if (text.Length <= 65536 && (!File.Exists(log) || new FileInfo(log).Length + text.Length * 4 < 2_097_152))
                            File.AppendAllText(log, text + Environment.NewLine);
                    });
                try
                {
                    if (concurrent) tail = Task.Run(() =>
                    {
                        for (int n = Selected; n < Chunks && !tailCancel.IsCancellationRequested; n++)
                        {
                            Dummy(Path.Combine(session, $"chunk-{n:D9}.ts"), ChunkBytes);
                            File.AppendAllText(Path.Combine(session, "segments.csv"), $"chunk-{n:D9}.ts,{n},{n + 1}\n");
                            if (Volatile.Read(ref outputActive) != 0 && Interlocked.Read(ref outputWritten) > 0) Interlocked.Increment(ref overlappingWrites);
                            Thread.Sleep(100);
                        }
                    });
                    else sampler.MarkCaptureStop(label);
                    Export("main", Selected, 0);
                    if (label == "T02") { Export("short-1", 1, 0); Export("short-2", 2, 3); }
                    Volatile.Write(ref outputActive, 0);
                    tail?.GetAwaiter().GetResult();
                    if (concurrent) sampler.MarkCaptureStop(label);
                }
                finally
                {
                    Volatile.Write(ref outputActive, 0); tailCancel.Cancel();
                    Require(tail == null || tail.Wait(5000), "Owned in-process source writer absence is unconfirmed.");
                }
                Require(!concurrent || overlappingWrites > 0, "Concurrent capture/export dummy writes were not observed.");
                Require(Directory.GetFiles(session, "chunk-*.ts").Length == Chunks, "Dummy source did not produce its full310-file history.");
                sampler.EndActive();
                string output = Path.Combine(cell, "main.mp4");
                foreach (var inspected in outputs)
                {
                    bool main = inspected.Name == "main"; int seconds = main ? 180 : 150;
                    AdmitStage(seconds); sampler.BeginOffline(main ? "inspection" : "short-inspection", main ? 32_000_000 : 4_000_000, seconds);
                    string file = Path.Combine(cell, inspected.Name + ".mp4");
                    string beforeHash = Hash(file), afterHash = Hash(file);
                    Require(beforeHash == afterHash && new FileInfo(file).Length == inspected.Bytes, "Full dummy inspection changed bytes.");
                    foreach (string suffix in new[] { ".decode-arguments.json", ".decode.json", ".source-pictures.json" }) Dummy(file + suffix, 131072);
                    Dummy(file + ".decode.stdout.txt", 0); Dummy(file + ".decode.stderr.txt", 2_097_152);
                    Dummy(file + ".barcodes.raw", 144); Dummy(file + ".pictures.raw", 20736);
                    sampler.EndOffline();
                }
                foreach (string snapshot in snapshots)
                {
                    AdmitStage(180);
                    sampler.BeginOffline("archive", 1_068_194_304, 180);
                    var archive = ReplayCalibrationArchive.Create(cell, snapshot, bytes => sampler.Check(bytes), sampler.Gate);
                    ReplayCalibrationArchive.Retire(cell, archive, sampler.Gate, sampler.ReleaseInventoryHandles);
                    Require(!Directory.Exists(snapshot), "Verified dummy snapshot duplicate was not retired.");
                    sampler.EndOffline();
                }
                maximumUsed = Math.Max(maximumUsed, sampler.PeakUsed);
                stages.Add(new { label, sourceFiles = Chunks, sourceBytes = (long)Chunks * ChunkBytes, selectedFiles = Selected,
                    copiedFallback = true, concurrent, overlappingWrites, exports = snapshots.Count, inspections = outputs.Count, outputBytes = new FileInfo(output).Length,
                    sampler.Used, sampler.MaximumActiveGap, sampler.MaximumOfflineGap, sampler.MaximumFiles, sampler.MaximumDirectories });
                Console.WriteLine($"Rehearsal {label}: {sampler.Used} allocated bytes; active gap {sampler.MaximumActiveGap:F1}ms");
                sampler.SealClosedCase(label);

                void Export(string name, int count, int offset)
                {
                    AdmitStage(130);
                    Interlocked.Exchange(ref outputWritten, 0);
                    Volatile.Write(ref outputActive, 1);
                    double cutoff = count == Selected ? Selected : count == 1 ? 4 : 7;
                    var source = new DummySource(session, count, cutoff, (arguments, snapshot) =>
                    {
                        try
                        {
                            Require(Directory.GetFiles(snapshot, "*.ts").Length == count, "Actual replay module selected the wrong dummy input count.");
                            string partial = arguments[^1];
                            Dummy(partial, (long)count * ChunkBytes, written => Interlocked.Add(ref outputWritten, written), preallocate: true);
                            using (var file = new FileStream(partial, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                            { var header = block.AsSpan(0, 4096).ToArray(); header[0] ^= 0x5A; file.Write(header); file.Flush(); }
                            snapshots.Add(snapshot);
                        }
                        finally { Volatile.Write(ref outputActive, 0); }
                    });
                    if (!concurrent || name != "main") source.StopCapture();
                    var id = cache.RegisterSession(source);
                    var accepted = cache.TrySave(id, 0, cell); Require(accepted.Accepted, accepted.Rejection);
                    var elapsed = Stopwatch.StartNew();
                    while (!accepted.Completion.IsCompleted)
                    { Deadline(); sampler.Check(); Require(elapsed.Elapsed.TotalSeconds < 130, "Actual dummy replay/export deadline exceeded."); MaintainIfGrowing(cache, id, source); Thread.Sleep(20); }
                    var saved = accepted.Completion.GetAwaiter().GetResult(); Require(saved.Succeeded, saved.Error);
                    string final = Path.Combine(cell, name + ".mp4");
                    lock (sampler.Gate) File.Move(saved.File, final);
                    outputs.Add((name, (long)count * ChunkBytes));
                    if (name != "main")
                    {
                        foreach (string suffix in new[] { "-export-arguments.json", "-control.json" }) Dummy(Path.Combine(cell, name + suffix), 131072);
                        Dummy(Path.Combine(cell, name + "-export.stdout.txt"), 0); Dummy(Path.Combine(cell, name + "-export.stderr.txt"), 2_097_152);
                    }
                }
            }
            AdmitStage(5); sampler.BeginOffline("offline-delay-control", 0, 5);
            lock (sampler.Gate) Thread.Sleep(650);
            sampler.EndOffline();
            Require(sampler.MaximumOfflineGap > 500, "Full-size offline delay was not observed.");
            sampler.StopAndCheck();
            rawOffline = sampler.OfflineSeconds; maximumFiles = sampler.MaximumFiles; maximumDirectories = sampler.MaximumDirectories;
            maximumActive = sampler.MaximumActiveGap; maximumOffline = sampler.MaximumOfflineGap;
            sampler.Dispose(); sampler = null;
            bool activeDelayRejected = false;
            using (var sensitivity = new ReplayCalibrationRun.Sampler(rawPlan with {
                RetainedElapsedSeconds = plan.RetainedElapsedSeconds + clock.Elapsed.TotalSeconds,
                RetainedOfflineSeconds = rawOffline }, active: false, traceName: "storage-active-sensitivity.csv"))
            {
                sensitivity.BeginActive();
                lock (sensitivity.Gate) Thread.Sleep(650);
                try { sensitivity.EndActive(); }
                catch (IOException ex) when (ex.Message.Contains("500", StringComparison.Ordinal)) { activeDelayRejected = true; }
            }
            Require(activeDelayRejected, "Full-size active650ms control was not rejected.");
            // Raw trace and outputs are now immutable. A second instance of the
            // SAME observer watches archive growth plus all raw/history bytes.
            var compactPlan = plan with { Root = compact, RetainedRoots = [.. plan.RetainedRoots, raw],
                RetainedElapsedSeconds = plan.RetainedElapsedSeconds + clock.Elapsed.TotalSeconds, RetainedOfflineSeconds = rawOffline,
                MetadataHandleRoots = [.. plan.MetadataHandleRoots ?? [], raw] };
            sampler = new(compactPlan, active: false);
            AdmitStage(300); sampler.BeginOffline("dummy-compaction", 24_000_000_000, 300);
            string archivePath = Compact(raw, compact, sampler, Deadline, compress: true);
            string archiveHash = Hash(archivePath);
            sampler.EndOffline();
            maximumUsed = Math.Max(maximumUsed, sampler.PeakUsed);
            maximumFiles = Math.Max(maximumFiles, sampler.MaximumFiles); maximumDirectories = Math.Max(maximumDirectories, sampler.MaximumDirectories);
            sampler.StopAndCheck();
            double totalOffline = sampler.OfflineSeconds;
            maximumOffline = Math.Max(maximumOffline, sampler.MaximumOfflineGap);
            sampler.Dispose(); sampler = null;
            // Keep the retained rehearsal flat so all three live T02 snapshot
            // directories still fit the unchanged aggregate48-directory limit.
            string prefix = Path.GetFileName(run);
            foreach (var file in Directory.GetFiles(compact))
            {
                string target = Path.Combine(run, prefix + "-" + Path.GetFileName(file)); BoundPath(run, file); BoundPath(run, target);
                File.Move(file, target);
            }
            foreach (string file in Directory.GetFiles(raw))
            { BoundPath(run, file); File.Move(file, Path.Combine(run, prefix + "-raw-" + Path.GetFileName(file))); }
            BoundPath(run, raw); Directory.Delete(raw, false); BoundPath(run, compact); Directory.Delete(compact, false);
            Require(Native.DirectoryIdentity(run) == ownerIdentity, "Rehearsal owned root changed.");
            string evidence = plan.RehearsalEvidenceRoot ?? run;
            if (!string.Equals(Path.GetFullPath(evidence), run, StringComparison.OrdinalIgnoreCase))
            {
                Require(plan.RetainedRoots.Contains(evidence, StringComparer.OrdinalIgnoreCase), "Rehearsal evidence destination was not charged.");
                using var marker = JsonDocument.Parse(File.ReadAllText(Path.Combine(evidence, PilotStorageObservation.Marker)));
                using var closure = JsonDocument.Parse(File.ReadAllText(Path.Combine(evidence, "dummy-closure.json")));
                Require(marker.RootElement.GetProperty("root_identity").GetString() == Native.DirectoryIdentity(evidence)
                    && closure.RootElement.GetProperty("result").GetString() == "closed-dummy-evidence-preserved",
                    "Rehearsal evidence destination is not the verified dummy-only closure root.");
                long existing = ReplayStorage.Bytes(ReplayStorage.Inventory([evidence], new ReplayFiles(), 4096, 48));
                long incoming = Directory.GetFiles(run).Sum(file => ReplayStorage.Observe(file).CopyBytes);
                Require(existing + incoming + 1_048_576 <= 24_000_000_000, "Combined closed dummy evidence exceeds its24GB allowance.");
                foreach (string file in Directory.GetFiles(run))
                {
                    string name = Path.GetFileName(file);
                    string target = Path.Combine(evidence, name.StartsWith(prefix + "-", StringComparison.Ordinal) ? name : prefix + "-" + name.TrimStart('.'));
                    BoundPath(run, file); Require(!File.Exists(target), "Rehearsal evidence destination already exists."); File.Move(file, target);
                }
                ownership.Dispose(); Directory.Delete(run, false);
            }
            Write(Path.Combine(evidence, prefix + "-rehearsal.json"), new { result = "passed-full-nonmedia-rehearsal", planHash = expectedHash,
                elapsedSeconds = clock.Elapsed.TotalSeconds, retainedElapsedSeconds = plan.RetainedElapsedSeconds + clock.Elapsed.TotalSeconds,
                offlineSeconds = totalOffline, retainedChildren = plan.RetainedChildren, newMediaChildren = 0,
                nativeHistory, maximumUsed, maximumFiles, maximumDirectories, maximumActive, maximumOffline, stages,
                controls = new { rejectedCapacity, activeDelayRejected, offlineDelayAccepted = true },
                outputAllocationPolicy = "Ordinary full physical preallocation bounds the deterministic dummy producer; EOF then grows and the same payload is rewritten. No engine allocation margin is inferred.",
                archive = Path.Combine(evidence, prefix + "-" + Path.GetFileName(archivePath)), archiveHash,
                limits = "Allocated dummy data and real native observation/archive/retirement; no empirical codec, GPU, capture, audio, native UI or release acceptance." });
            Console.WriteLine("Full nonmedia rehearsal passed: " + evidence); return 0;
        }
        catch (Exception ex)
        {
            string failureRoot = Directory.Exists(run) ? run : plan.RehearsalEvidenceRoot;
            string failureName = Directory.Exists(run) ? "failure.json" : Path.GetFileName(run) + "-failure.json";
            Write(Path.Combine(failureRoot, failureName), new { result = "failed-full-nonmedia-rehearsal", planHash = expectedHash,
                elapsedSeconds = clock.Elapsed.TotalSeconds, stages, error = ex.ToString(), samplerFailure = sampler?.Failure,
                offlineSeconds = sampler?.OfflineSeconds });
            Console.Error.WriteLine(ex.Message + "; retained: " + run); return 1;
        }
        finally { sampler?.Dispose(); }

        void Deadline()
        {
            Require(plan.RehearsalElapsedSeconds + clock.Elapsed.TotalSeconds <= 1800 && plan.RetainedElapsedSeconds + clock.Elapsed.TotalSeconds < plan.DeadlineSeconds,
                "Rehearsal or cumulative experiment deadline exceeded.");
        }
        void AdmitStage(int seconds)
        {
            Deadline(); sampler.RequireTime(seconds);
            Require(plan.RehearsalElapsedSeconds + clock.Elapsed.TotalSeconds + seconds <= 1800, "The full stage deadline cannot fit the remaining rehearsal wall time.");
        }
        void Dummy(string file, long bytes, Action<int> progress = null, bool preallocate = false)
        {
            BoundPath(run, file);
            using var output = OpenDummy(file, bytes, preallocate);
            for (long written = 0; written < bytes;)
            {
                int count = (int)Math.Min(block.Length, bytes - written); output.Write(block, 0, count); written += count; progress?.Invoke(count);
                if (written % (64L * block.Length) == 0) { Deadline(); sampler.Check(); }
            }
            output.Flush(); Deadline(); sampler.Check();
            var extent = ReplayStorage.Observe(file);
            Require(extent.Length == bytes && extent.AllocatedBytes >= extent.CopyBytes
                && (File.GetAttributes(file) & (FileAttributes.SparseFile | FileAttributes.Compressed)) == 0,
                "Dummy data must have real ordinary allocation, not sparse logical EOF.");
        }
    }

    sealed record Member(string Path, ReplayFileExtent Extent, string Hash, DateTime LastWriteUtc);
    internal static int Checks(string root)
    {
        Require(!Directory.Exists(root), "Compaction controls require fresh evidence.");
        string raw = Path.Combine(root, "raw"), compact = Path.Combine(root, "compact");
        Directory.CreateDirectory(raw); Directory.CreateDirectory(compact);
        string barcode = Path.Combine(root, "tiny-barcode.raw");
        using (var tiny = OpenDummy(barcode, 144, false))
        {
            tiny.Write(new byte[144]); tiny.Flush(); var extent = ReplayStorage.Observe(barcode);
            Require(extent.Length == 144 && extent.AllocatedBytes >= extent.CopyBytes
                && (File.GetAttributes(barcode) & (FileAttributes.SparseFile | FileAttributes.Compressed)) == 0,
                "Tiny dummy payload did not receive ordinary physical data allocation.");
        }
        var closedTiny = ReplayStorage.Observe(barcode);
        Require(closedTiny.Length == 144 && closedTiny.AllocatedBytes >= closedTiny.CopyBytes,
            "Tiny dummy allocation did not survive writer closure.");
        string file = Path.Combine(raw, "dummy.ts"); File.WriteAllBytes(file, new byte[2_097_152]);
        var plan = new CalibrationPlan(compact, "unused", "nonmedia-control", new(), [], [raw], 0, 0,
            BudgetBytes: 3_000_000_000, StopBytes: 2_500_000_000, FloorBytes: 0, DeadlineSeconds: 30, RetainedOfflineSeconds: 0);
        using var sampler = new ReplayCalibrationRun.Sampler(plan, active: false);
        sampler.BeginOffline("compact-control", 32_000_000, 10);
        string archive = Compact(raw, compact, sampler, () => { });
        Require(File.Exists(archive) && Directory.GetFiles(raw).Length == 0, "Verified compaction did not preserve archive and retire only its dummy originals.");
        sampler.EndOffline();
        File.WriteAllText(file, "Preserve this collision-control original.");
        bool rejected = false;
        sampler.BeginOffline("collision-control", 32_000_000, 10);
        try { Compact(raw, compact, sampler, () => { }); } catch (IOException) { rejected = true; }
        Require(rejected && File.ReadAllText(file) == "Preserve this collision-control original.", "Archive collision lost an original or overwrote prior evidence.");
        sampler.EndOffline();
        var repeatedBlock = new byte[1_048_576]; RandomNumberGenerator.Fill(repeatedBlock);
        using (var repeated = File.Create(Path.Combine(raw, "repeated-dummy.bin")))
            for (int n = 0; n < 3; n++) repeated.Write(repeatedBlock);
        sampler.BeginOffline("compressed-full-readback-control", 32_000_000, 10);
        string compressedDestination = Path.Combine(compact, "compressed"); Directory.CreateDirectory(compressedDestination);
        string compressed = Compact(raw, compressedDestination, sampler, () => { }, compress: true);
        Require(new FileInfo(compressed).Length < 2_097_152 && Directory.GetFiles(raw).Length == 0,
            "Compressed dummy archive did not preserve and fully read back the repeated production-sized block fixture.");
        sampler.EndOffline();
        string replay = Path.Combine(root, "replay"), session = Path.Combine(replay, "buffer", "session-dummy"); Directory.CreateDirectory(session);
        for (int n = 0; n < 2; n++) File.WriteAllBytes(Path.Combine(session, $"chunk-{n:D9}.ts"), new byte[4096]);
        File.WriteAllText(Path.Combine(session, "segments.csv"), "chunk-000000000.ts,0,1\nchunk-000000001.ts,1,2\n");
        long copied = 0;
        using (var cache = new ReplayCache(new DummyFiles(sampler, bytes => copied += bytes), cacheDirectory: Path.Combine(replay, "buffer")))
        {
            sampler.BeginActive();
            var source = new DummySource(session, 2, 2, (args, snapshot) => File.WriteAllBytes(args[^1], new byte[8192]));
            var id = cache.RegisterSession(source); var save = cache.TrySave(id, 0, replay);
            Require(save.Accepted, save.Rejection);
            Require(save.Completion.Wait(5000), "Actual nonmedia replay control did not finish.");
            var result = save.Completion.GetAwaiter().GetResult();
            Require(result.Succeeded && copied == 8192 && File.Exists(result.File) && cache.Status.OutputDemand.Kind == ReplayOutputEvidenceKind.SourceBound,
                "Actual replay module failed its fixed dummy producer/copy/output-bound control: " + result.Error);
            sampler.EndActive();
        }
        string shortSession = Path.Combine(replay, "buffer", "session-short-budget"); Directory.CreateDirectory(shortSession);
        for (int n = 0; n < 8; n++) File.WriteAllBytes(Path.Combine(shortSession, $"chunk-{n:D9}.ts"), new byte[1_048_576]);
        File.WriteAllText(Path.Combine(shortSession, "segments.csv"), string.Concat(Enumerable.Range(0, 8).Select(n => $"chunk-{n:D9}.ts,{n},{n + 1}\n")));
        using (var cache = new ReplayCache(new DummyFiles(sampler, _ => { }), cacheDirectory: Path.Combine(replay, "buffer")))
        {
            var source = new DummySource(shortSession, 1, 8, (arguments, _) => File.WriteAllBytes(arguments[^1], new byte[1_048_576]), bitrate: 1);
            var id = cache.RegisterSession(source); bool growingRejected = false;
            try { MaintainIfGrowing(cache, id, source); } catch (ReplayCacheBudgetExceededException) { growingRejected = true; }
            Require(growingRejected, "Short-control fixture did not exceed the active one-second source budget.");
            source.StopCapture(); MaintainIfGrowing(cache, id, source);
            var saved = cache.TrySave(id, 0, replay); Require(saved.Accepted && saved.Completion.Wait(5000) && saved.Completion.Result.Succeeded,
                "Stopped short control was incorrectly maintained against its one-second history allowance.");
        }
        sampler.StopAndCheck();
        Write(Path.Combine(root, "compaction-checks.json"), new { result = "passed", roundtrip = true, collisionPreserved = true, actualReplayCopyAndPublication = true,
            tinyOrdinaryAllocation = true, compressedFullReadback = true, stoppedShortMaintenance = true,
            effects = "One2MiB ordinary dummy and tiny native metadata/observer/archive controls; no child, engine, capture, GPU, audio, UI or startup effects." });
        Console.WriteLine("Dummy compaction/replay controls: 6/6 passed"); return 0;
    }
    sealed class ReadPins : IDisposable
    {
        readonly List<Microsoft.Win32.SafeHandles.SafeFileHandle> handles = [];
        internal ReadPins(IEnumerable<string> paths)
        {
            try { foreach (string path in paths) handles.Add(File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete)); }
            catch { Dispose(); throw; }
        }
        public void Dispose() { foreach (var handle in handles) handle.Dispose(); }
    }
    internal static string Compact(string raw, string compact, ReplayCalibrationRun.Sampler sampler, Action deadline, bool compress = false)
    {
        var paths = Directory.GetFiles(raw, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
        Require(paths.Length <= 4096, "Dummy compaction membership exceeds4096files.");
        foreach (string path in paths) BoundPath(raw, path);
        using var originalPins = new ReadPins(paths);
        var members = new List<Member>();
        foreach (string file in paths)
        { BoundPath(raw, file); deadline(); members.Add(new(file, ReplayStorage.Observe(file), Hash(file), File.GetLastWriteTimeUtc(file))); }
        long payload = members.Sum(m => m.Extent.CopyBytes);
        long reserve = checked(payload + 16_777_216 + (compress ? payload / 100 : 0));
        Require(reserve <= 24_000_000_000, "Dummy compaction exceeds24GB."); sampler.Check(reserve);
        string archive = Path.Combine(compact, compress ? "dummy-evidence.tar.br" : "dummy-evidence.tar");
        string manifest = Path.Combine(compact, "dummy-members.jsonl");
        Require(!Path.Exists(archive) && !Path.Exists(manifest), "Dummy archive or member manifest already exists.");
        using (var output = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            using (var compressor = compress ? new BrotliStream(output, new BrotliCompressionOptions { Quality = 4 }, leaveOpen: true) : null)
            using (var tar = new TarWriter((Stream)compressor ?? output, TarEntryFormat.Pax, leaveOpen: true))
                foreach (var member in members)
                {
                    deadline(); using var input = File.OpenRead(member.Path);
                    tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, Path.GetRelativePath(raw, member.Path).Replace('\\', '/'))
                        { DataStream = input, ModificationTime = member.LastWriteUtc });
                    Require(output.Length <= reserve, "Dummy compaction exceeded admitted allocation."); sampler.Check();
                }
            output.Flush(true);
            Require(output.Length <= reserve, "Final dummy archive exceeded admitted allocation.");
        }
        using var archivePin = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read);
        var expected = members.ToDictionary(m => Path.GetRelativePath(raw, m.Path).Replace('\\', '/'), StringComparer.Ordinal);
        using (var input = File.OpenRead(archive))
        using (var decompressor = compress ? new BrotliStream(input, CompressionMode.Decompress, leaveOpen: true) : null)
        using (var reader = new TarReader((Stream)decompressor ?? input))
        {
            TarEntry entry;
            while ((entry = reader.GetNextEntry()) != null)
            {
                deadline(); Require(entry.EntryType == TarEntryType.RegularFile, "Dummy archive contains a non-file member.");
                Require(expected.TryGetValue(entry.Name, out var member), "Dummy archive membership changed."); expected.Remove(entry.Name);
                Require(entry.Length == member.Extent.Length && Convert.ToHexString(SHA256.HashData(entry.DataStream ?? Stream.Null)) == member.Hash
                    && ReplayStorage.Observe(member.Path) == member.Extent && Hash(member.Path) == member.Hash
                    && File.GetLastWriteTimeUtc(member.Path) == member.LastWriteUtc, "Dummy archive/original equivalence failed.");
            }
            Require(expected.Count == 0, "Dummy archive is incomplete.");
        }
        using (var writer = new StreamWriter(new FileStream(manifest, FileMode.CreateNew, FileAccess.Write, FileShare.Read)))
            foreach (var member in members) writer.WriteLine(JsonSerializer.Serialize(member));
        lock (sampler.Gate)
        {
            sampler.ReleaseInventoryHandles();
            Require(Directory.GetFiles(raw, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).SequenceEqual(paths), "Dummy membership changed before retirement.");
            foreach (var member in members)
            { BoundPath(raw, member.Path); Require(ReplayStorage.Observe(member.Path) == member.Extent, "Dummy identity changed before retirement."); }
            foreach (var member in members) File.Delete(member.Path);
            foreach (string directory in Directory.GetDirectories(raw, "*", SearchOption.AllDirectories).OrderByDescending(p => p.Length))
            { BoundPath(raw, directory); Directory.Delete(directory, false); }
        }
        return archive;
    }
}
