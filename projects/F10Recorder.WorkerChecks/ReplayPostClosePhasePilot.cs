using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace F10Recorder;

// Diagnostic only: unqualified model arithmetic, protected existing inputs,
// simulated NONMEDIA seed validation and a dense NONPLAYABLE second output.
// This never loads/activates a catalog, starts a codec or qualifies Saved.
internal static class ReplayPostClosePhasePilot
{
    const string RefusePublication = "Post-close phase pilot refuses media validation/publication.";
    static readonly JsonSerializerOptions Json = new() { IncludeFields = true, WriteIndented = true };
    static void Need(bool value, string reason) { if (!value) throw new IOException(reason); }
    static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
    static byte[] ReadBounded(string path, int maximum)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Need(input.Length is > 0 && input.Length <= maximum, "Bounded input required.");
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes); return bytes;
    }

    // Finite comparison only, against unchanged product bytes. This prototype
    // changes access rights for growing outputs; it still opens and freshly
    // queries the actual object on EVERY call. It retains no metadata values.
    sealed class RetentionFiles(string mode) : ReplayFiles
    {
        [StructLayout(LayoutKind.Sequential)]
        struct StandardInformation
        {
            internal long AllocationSize, EndOfFile;
            internal uint Links;
            internal byte DeletePending, Directory;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, out StandardInformation information, uint size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder name, uint length, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool GetDiskFreeSpaceW(string root, out uint sectors, out uint sectorBytes, out uint freeClusters, out uint clusters);
        internal int OutputQueries;
        internal double OutputQueryMilliseconds, MaximumOutputQueryMilliseconds;
        internal readonly List<ReplayStorageTrace> OutputObservations = [];
        internal bool OutputTraceOverflow;
        internal override ReplayFileExtent Observe(string path)
        {
            if (!path.EndsWith(".partial.mp4", StringComparison.OrdinalIgnoreCase)
                && !path.Contains(".partial.mp4.work" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return base.Observe(path);
            long started = Stopwatch.GetTimestamp(); OutputQueries++;
            try { return mode == "ordinary" ? base.Observe(path) : Metadata(path); }
            finally
            {
                double ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                OutputQueryMilliseconds += ms; MaximumOutputQueryMilliseconds = Math.Max(MaximumOutputQueryMilliseconds, ms);
            }
        }
        ReplayFileExtent Metadata(string path)
        {
            var trace = new ReplayStorageTrace { Ordinal = OutputQueries, Scope = "OutputMetadata:" + mode, StartedQpc = Stopwatch.GetTimestamp() };
            long phaseAt = trace.StartedQpc; string phase = "Open";
            void Phase(string next)
            {
                long now = Stopwatch.GetTimestamp();
                if (phase != null) trace.Phases.Add(new(phase, phaseAt, now));
                phase = next; phaseAt = now;
            }
            SafeFileHandle file = null;
            try
            {
            file = mode == "data-read"
                ? File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)
                : CreateFileW(Path.GetFullPath(path), mode == "zero-access" ? 0u : 0x80u, 7, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
            if (file.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                if (error == 2) throw new FileNotFoundException("Metadata leaf absent.", path);
                if (error == 3) throw new DirectoryNotFoundException(path);
                throw new Win32Exception(error, path);
            }
            Phase("StandardInfo");
            if (!GetFileInformationByHandleEx(file, 1, out var info, (uint)Marshal.SizeOf<StandardInformation>())) throw new Win32Exception();
            Need(info.Directory == 0, "Metadata output must be a file.");
            Phase("VolumePath");
            var name = new StringBuilder(256);
            uint length = GetFinalPathNameByHandleW(file, name, (uint)name.Capacity, 1);
            if (length >= name.Capacity && length <= 32768)
            {
                name.EnsureCapacity(checked((int)length));
                length = GetFinalPathNameByHandleW(file, name, (uint)name.Capacity, 1);
            }
            Need(length != 0 && length < name.Capacity, "Metadata volume unavailable.");
            string resolved = name.ToString(); int end = resolved.IndexOf("}\\", StringComparison.Ordinal);
            Need(resolved.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase) && end >= 0, "Metadata local volume required.");
            string volume = resolved[..(end + 2)];
            Phase("AllocationUnit");
            if (!GetDiskFreeSpaceW(volume, out uint sectors, out uint sectorBytes, out _, out _)) throw new Win32Exception();
            Phase("Identity");
            return ReplayStorage.Validate(new(volume, Native.DirectoryIdentity(file), info.AllocationSize, info.EndOfFile, checked((long)sectors * sectorBytes)));
            }
            catch (Exception error) { trace.Error = error.ToString(); throw; }
            finally
            {
                Phase("Close"); file?.Dispose(); Phase(null); trace.CompletedQpc = Stopwatch.GetTimestamp();
                if (OutputObservations.Count < 4096) OutputObservations.Add(trace); else OutputTraceOverflow = true;
            }
        }
        internal override ReplayNamespaceIdentity ObserveNamespace(string path, ReplayFileExtent enumerated)
            => new(enumerated.Volume, enumerated.Identity);
        internal override void DeleteDirectory(string path) { }
        internal override void DeleteFile(string path) { }
    }

    sealed class Source(string directory, ReplayReferenceModel unqualifiedModel) : IReplaySessionSource
    {
        public ReplaySessionInfo ReplayInfo { get; } = new(directory, Path.Combine(directory, "segments.csv"),
            300, 20, 60, "h264_nvenc", ".ts", true, false);
        public bool Stopped => true;
        public bool PreserveEvidence => true;
        public bool OutputProducerStopped => !ExportInProgress && !CloseUncertain && (WriterHandle == null || WriterHandle.IsClosed);
        public string OutputDemandIdentity => ReplayReferenceModel.ExporterIdentity;
        public string Diagnostics => "NONMEDIA controlled source; unqualified arithmetic only, no catalog.";
        public double SaveCutoffAt(long timestamp) => 300.5;
        public void FreezeAt(long timestamp) { }
        public void StopCapture() { }
        public void CheckCaptureHealth(double end) { }
        public void Dispose() { }
        internal bool Seed = true, ExportInProgress, CloseUncertain;
        internal SafeFileHandle WriterHandle;
        internal ReplayFileExtent WriterIdentity;
        internal ReplayOutputDemand SeedDemand, ProbeDemand;
        internal int EstimateCalls;
        internal readonly List<object> WriterCloses = [];
        internal Exception CloseWriter(FileStream writer)
        {
            long at = Stopwatch.GetTimestamp(); Exception error = null;
            try { writer.Dispose(); } catch (Exception ex) { error = ex; }
            bool closed = WriterHandle?.IsClosed == true;
            if (!closed) CloseUncertain = true;
            WriterCloses.Add(new { seed = Seed, at, completed = Stopwatch.GetTimestamp(), closed, error = error?.ToString(), CloseUncertain });
            return error;
        }
        internal ReplayExportRequest SeedRequest, ProbeRequest;
        internal Action<ReplayExportRequest, Action> Work;
        public ReplayOutputDemand EstimateOutput(ReplayOutputSelection selection)
        {
            var demand = new ReplayCalibrationAvailability(unqualifiedModel, null).Estimate(selection, unqualifiedModel.Scope, directory);
            EstimateCalls++; if (Seed) SeedDemand = demand; else ProbeDemand = demand;
            Need(demand.Kind == ReplayOutputEvidenceKind.CalibratedEstimate && demand.Revision == unqualifiedModel.Revision
                && demand.SelectionIdentity == selection.Identity && demand.ExporterIdentity == selection.ExporterIdentity
                && demand.Volume == unqualifiedModel.Volume && demand.AllocationUnit == unqualifiedModel.AllocationUnit,
                "Diagnostic availability must exercise the bound model's actual numeric recheck, never an unmeasured bypass.");
            return demand;
        }
        public void Export(IReadOnlyList<string> arguments, string snapshot) => throw new IOException("Request overload required.");
        public void Export(ReplayExportRequest request, string snapshot, Action<ReplayProducerProof> changed, Action check)
        {
            ExportInProgress = true;
            try
            {
                request.SelectRecipe(ReplayExactExport.Recipe);
                if (Seed)
                {
                    SeedRequest = request;
                    FileStream writer = null; Exception primary = null;
                    try
                    {
                        writer = new(request.Partial, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                        WriterHandle = writer.SafeFileHandle;
                        writer.Write(new byte[4096]);
                    }
                    catch (Exception error) { primary = error; throw; }
                    finally
                    {
                        if (writer != null)
                        {
                            var closeError = CloseWriter(writer);
                            if (closeError != null && primary == null) throw new IOException("Seed writer close failed.", closeError);
                        }
                    }
                    check();
                }
                else { ProbeRequest = request; Work(request, check); }
            }
            finally { ExportInProgress = false; }
        }
        public ReplayValidation ValidateOutput(ReplayExportRequest request, Action<ReplayProducerProof> changed, Action check)
        {
            if (!Seed) throw new IOException(RefusePublication);
            // Explicit simulated trust for a dummy file. The real native owner
            // exercises Cache completion/retention, not decoder correctness.
            var lease = new ReplayPublicationLease(request.Partial);
            try
            {
                request.FreezeValidatedOutput(lease);
                return new("SIMULATED-NONMEDIA-SEED-NOT-MEDIA-VALIDATION", 0, 0,
                    ExportRecipe: request.Recipe, CheckedOutput: lease.CheckedIdentity, PublicationIdentityBound: true);
            }
            catch { lease.Dispose(); throw; }
        }
    }

    internal static int Run(string planFile, string expectedHash)
    {
        byte[] raw = ReadBounded(planFile, 65536);
        Need(raw.Length is > 0 and <= 65536 && Convert.ToHexString(SHA256.HashData(raw)) == expectedHash,
            "Exact bounded reviewed post-close plan required.");
        using var document = JsonDocument.Parse(raw); var plan = document.RootElement;
        string observationMode = plan.TryGetProperty("observationMode", out var mode) ? mode.GetString() : "ordinary";
        Need(observationMode is "ordinary" or "metadata-only" or "zero-access" or "data-read", "Unknown finite comparison mode.");
        Need(plan.GetProperty("kind").GetString() == "issue83-post-close-phase-v1"
            && plan.GetProperty("activeSeconds").GetInt32() == 80
            && plan.GetProperty("outputBytes").GetInt64() == 800_000_000
            && plan.GetProperty("writePasses").GetInt32() == 2
            && plan.GetProperty("callbackCadenceMilliseconds").GetInt32() == 100
            && DateTime.UtcNow.AddSeconds(90) < plan.GetProperty("expiresUtc").GetDateTime(), "Finite phase boundary changed/expired.");
        foreach (var file in plan.GetProperty("files").EnumerateObject()) Need(Hash(file.Name) == file.Value.GetString(), "Changed binding: " + file.Name);
        string root = Path.GetFullPath(plan.GetProperty("root").GetString());
        string sourceRoot = Path.GetFullPath(plan.GetProperty("sourceDirectory").GetString());
        string prefix = Path.GetFullPath("artifacts") + Path.DirectorySeparatorChar;
        Need(root.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && sourceRoot.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && !Directory.Exists(root) && !File.Exists(root) && !root.StartsWith(sourceRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !sourceRoot.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Fresh disjoint task scopes required.");
        string modelPath = plan.GetProperty("unqualifiedModel").GetString();
        Need(plan.GetProperty("files").TryGetProperty(modelPath, out var modelHash), "Bound diagnostic model required.");
        byte[] modelBytes = ReadBounded(modelPath, 65536);
        Need(Convert.ToHexString(SHA256.HashData(modelBytes)) == modelHash.GetString(), "Diagnostic model changed before parsing.");
        var model = JsonSerializer.Deserialize<ReplayReferenceModel>(modelBytes, Json);
        Need(model != null, "No diagnostic model.");
        using var owned = PilotStorageObservation.Create(root);
        var clock = Stopwatch.StartNew(); using var process = Process.GetCurrentProcess();
        var traces = new List<ReplayStorageTrace>(); var markers = new List<object>(); var callbackIntervals = new List<object>();
        var diagnostics = new List<string>(); var diagnosticErrors = new List<string>(); var cleanupErrors = new List<string>();
        Exception failure = null; ReplaySaveResult seedResult = null, result = null;
        long written = 0, callbackAt = 0, metricAt = 0; int callbacks = 0, peakHandles = 0; long peakRam = 0;
        long protectedNativeBytes = 0, allocationAt = 0, outputPeak = 0;
        int boundQueries = 0, allocationQueries = 0, seedTraceCount = 0;
        double boundQueryMilliseconds = 0, maximumBoundQueryMilliseconds = 0, allocationMilliseconds = 0, maximumAllocationMilliseconds = 0;
        string stage = "preflight"; int? seedMetadataHandles = null, probeMetadataHandles = null;
        bool guardReproduced = false, tracedEstimate = false;
        ReplayCache cache = null; Source source = null;
        var files = new RetentionFiles(observationMode);
        FrozenCalibrationHistory frozen = null;
        void Bound(bool force = false)
        {
            Need(clock.Elapsed.TotalSeconds < 80 && written <= 1_600_000_000, "Local time/write cap.");
            if (source?.WriterHandle is { IsClosed: false, IsInvalid: false } handle && source.WriterIdentity != null
                && (force || allocationAt == 0 || Stopwatch.GetElapsedTime(allocationAt).TotalMilliseconds >= 100))
            {
                allocationAt = Stopwatch.GetTimestamp(); allocationQueries++;
                try
                {
                    var current = ReplayStorage.RefreshInventoryAllocation(handle, source.WriterIdentity);
                    outputPeak = Math.Max(outputPeak, current.AllocatedBytes);
                    Need(protectedNativeBytes + current.AllocatedBytes + 4096 + 33_554_432 <= 2_100_000_000, "Active native2.1GB component cap.");
                }
                finally
                {
                    double ms = Stopwatch.GetElapsedTime(allocationAt).TotalMilliseconds;
                    allocationMilliseconds += ms; maximumAllocationMilliseconds = Math.Max(maximumAllocationMilliseconds, ms);
                }
            }
            if (force || metricAt == 0 || Stopwatch.GetElapsedTime(metricAt).TotalMilliseconds >= 500)
            {
                metricAt = Stopwatch.GetTimestamp(); boundQueries++;
                try
                {
                    process.Refresh(); peakRam = Math.Max(peakRam, process.WorkingSet64); peakHandles = Math.Max(peakHandles, process.HandleCount);
                    Need(peakRam <= 268_435_456 && peakHandles <= 4096
                        && ReplayStorage.ObserveCapacity(root).AvailableBytes >= 60_000_000_000, "Resource/free-space cap.");
                }
                finally
                {
                    double ms = Stopwatch.GetElapsedTime(metricAt).TotalMilliseconds;
                    boundQueryMilliseconds += ms; maximumBoundQueryMilliseconds = Math.Max(maximumBoundQueryMilliseconds, ms);
                }
            }
        }
        void Write(string name, object value)
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
            Need(bytes.Length <= 8_388_608, "8MiB diagnostic report cap.");
            using var output = new FileStream(Path.Combine(root, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            output.Write(bytes);
        }
        void Diagnose(string name, object value)
        { try { Write(name, value); } catch (Exception error) { diagnosticErrors.Add(error.ToString()); } }
        void Quarantine(string reason)
        {
            Diagnose("incomplete.json", new { stage, written, callbacks, reason, writerCloses = source?.WriterCloses });
            while (true) Thread.Sleep(100); // Outer cutoff terminates; no live owner disposal.
        }
        ReplaySaveResult Finish(SaveSubmission submission)
        {
            Need(submission.Accepted, submission.Rejection);
            if (!submission.Completion.Wait(TimeSpan.FromSeconds(70)))
            {
                Quarantine("Accepted work unconfirmed; outer cutoff owns cleanup.");
            }
            return submission.Completion.GetAwaiter().GetResult();
        }
        string buffer = Path.Combine(root, "buffer"), session = Path.Combine(buffer, "session"), clips = Path.Combine(root, "output");
        try
        {
            if (observationMode != "ordinary")
            {
                string control = Path.Combine(root, "control.partial.mp4"), retired = control + ".retired";
                File.WriteAllBytes(control, new byte[8192]);
                var first = files.Observe(control);
                Need(first == ReplayStorage.Observe(control) && first.Length == 8192, "Closed metadata/data observer equivalence.");
                using (var writer = new FileStream(control, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                {
                    writer.SetLength(16384); writer.Flush();
                    var grown = files.Observe(control);
                    var owner = ReplayStorage.RefreshInventoryAllocation(writer.SafeFileHandle, first);
                    Need(grown == owner && grown.Length == 16384 && grown.Identity == first.Identity,
                        "Metadata must freshly observe growth and agree with the owned writer handle.");
                }
                File.Move(control, retired); File.WriteAllBytes(control, new byte[4096]);
                var replacement = files.Observe(control);
                Need(replacement == ReplayStorage.Observe(control) && replacement.Length == 4096 && replacement.Identity != first.Identity,
                    "Metadata must observe same-name replacement without reusing identity/allocation.");
                File.Delete(control); bool absent = false;
                try { files.Observe(control); } catch (FileNotFoundException) { absent = true; }
                Need(absent, "Metadata missing leaf must preserve FileNotFoundException semantics.");
                File.Delete(retired);
                Write("metadata-controls.json", new { result = "passed", first, replacement,
                    checks = "closed data-observer equivalence, native writer growth/EOF/allocation, same-name replacement, genuine absence" });
            }
            frozen = new([sourceRoot], 2048, 64); protectedNativeBytes = frozen.AllocatedBytes;
            Need(protectedNativeBytes <= 1_200_000_000, "Protected source cap1.2GB.");
            string[] originals = Directory.GetFiles(sourceRoot, "chunk-*.ts").Order(StringComparer.OrdinalIgnoreCase).Take(301).ToArray();
            Need(originals.Length == 301, "301 existing completed originals required.");
            Directory.CreateDirectory(session); Directory.CreateDirectory(clips);
            var lines = new List<string>();
            for (int n = 0; n < originals.Length; n++)
            {
                // The real source excludes chunk zero as startup warm-up. These
                // are already completed inputs, so begin synthetic names at one.
                Bound(); string name = "chunk-" + (n + 1).ToString("D9") + ".ts";
                Need(Native.CreateHardLink(Path.Combine(session, name), originals[n], IntPtr.Zero), "Diagnostic input hardlink failed.");
                lines.Add(FormattableString.Invariant($"{name},{n},{n + 1}"));
            }
            File.WriteAllLines(Path.Combine(session, "segments.csv"), lines);
            source = new(session, model);
            cache = new(files, cacheDirectory: buffer,
                volumeReservations: new ReplayVolumeReservations(Path.Combine(root, "authority"), 60_000_000_000),
                fileMode: ReplayFileMode.NativeInventoryForChecks, diagnostic: message =>
                { Need(diagnostics.Count < 128 && message.Length <= 8192, "Diagnostic text cap."); diagnostics.Add(message); });
            cache.StorageObservedForChecks = trace => { Need(traces.Count < 4096, "4096 correlated trace cap."); traces.Add(trace); };
            var id = cache.RegisterSession(source);
            stage = "simulated-seed"; seedResult = Finish(cache.TrySave(id, Stopwatch.GetTimestamp(), clips));
            Need(seedResult.Succeeded && source.SeedRequest != null, "Simulated NONMEDIA seed failed: " + seedResult.Error);
            seedMetadataHandles = cache.NativeSnapshotHandleCountForChecks;
            Need(Directory.Exists(source.SeedRequest.WorkDirectory) || Directory.Exists(Path.GetDirectoryName(source.SeedRequest.Concat)), "Seed snapshot missing.");
            Need(source.SeedRequest.Inputs.Count == 301 && source.SeedRequest.Inputs.All(i => File.Exists(i.Path))
                && seedMetadataHandles == 301 && traces.Any(t => t.Scope == "CompletedInputMetadataClose" && t.Error == null),
                "Expected301 retained sealed-snapshot observers after original metadata scope closed.");
            Write("seed.json", new { seedResult, seedMetadataHandles, selected = source.SeedRequest.Inputs.Count,
                interpretation = "Dummy trusted-source validation/native lease/rename ONLY. No codec/worker/UI Saved/media acceptance." });
            seedTraceCount = traces.Count;
            source.Seed = false;
            source.Work = (request, check) =>
            {
                var block = new byte[65536]; Array.Fill(block, (byte)0x5a);
                FileStream writer = null; Exception bodyFailure = null;
                void CloseWriter()
                {
                    if (writer == null) return;
                    var closeError = source.CloseWriter(writer);
                    writer = null;
                    if (closeError != null && bodyFailure == null) throw new IOException("Probe writer close failed.", closeError);
                }
                void Mark(string name) => markers.Add(new { name, qpc = Stopwatch.GetTimestamp(), written, callbacks });
                void Callback(bool boundary = false)
                {
                    Bound(force: boundary); long now = Stopwatch.GetTimestamp();
                    if (!boundary && callbackAt != 0 && Stopwatch.GetElapsedTime(callbackAt, now).TotalMilliseconds < 100) return;
                    long start = Stopwatch.GetTimestamp(), previous = callbackAt; callbackAt = start; callbacks++;
                    try { check(); }
                    finally { callbackIntervals.Add(new { start, end = Stopwatch.GetTimestamp(), previous, stage, boundary }); }
                }
                try
                {
                    stage = "first-materialization"; Mark(stage);
                    source.WriterHandle = null; source.WriterIdentity = null;
                    writer = new(request.Partial, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 65536);
                    source.WriterHandle = writer.SafeFileHandle; // Capture BEFORE writes: observing it cannot flush pending data.
                    var volume = ReplayStorage.ObserveCapacity(clips);
                    source.WriterIdentity = new(volume.Volume, Native.DirectoryIdentity(source.WriterHandle), 0, 0, volume.AllocationUnit);
                    _ = ReplayStorage.ObserveInventory(source.WriterHandle, source.WriterIdentity);
                    writer.Write(block); written += block.Length; Callback(true);
                    for (int pass = 0; pass < 2; pass++)
                    {
                        stage = pass == 0 ? "dense-growth" : "same-length-rewrite"; Mark(stage);
                        if (pass != 0) writer.Position = 0;
                        while (writer.Position < 800_000_000)
                        {
                            Bound(); int count = (int)Math.Min(block.Length, 800_000_000 - writer.Position);
                            writer.Write(block, 0, count); written += count; Callback();
                        }
                    }
                    stage = "buffered-flush"; Mark(stage); writer.Flush(); Callback(true);
                    stage = "actual-close"; Mark(stage); CloseWriter();
                    Mark("actual-close-complete"); stage = "forced-post-close-by-cache";
                }
                catch (Exception error) { bodyFailure = error; throw; }
                finally { CloseWriter(); }
            };
            stage = "probe"; result = Finish(cache.TrySave(id, Stopwatch.GetTimestamp(), clips));
            if (!source.OutputProducerStopped) Quarantine("Output owner closure unconfirmed; outer cutoff owns cleanup.");
            guardReproduced = result.Error?.Contains("storage observation exceeded 500 ms", StringComparison.Ordinal) == true;
            Need(!result.Succeeded && (guardReproduced || result.Error?.Contains(RefusePublication, StringComparison.Ordinal) == true), "Unexpected probe outcome: " + result.Error);
            Need(result.Timing.ValidationCompleted == null && result.Timing.Published == null, "Probe reached publication.");
            Need(Directory.GetFiles(clips, "Replay-*.mp4").Count(p => !p.EndsWith(".partial.mp4", StringComparison.OrdinalIgnoreCase)) == 1,
                "Only the explicit dummy seed may have a final name.");
            Need(!files.OutputTraceOverflow, "Output metadata trace exceeded4096 observations; incomplete diagnostic.");
            frozen.Check(); Bound();
            var inventory = ReplayStorage.Inventory([sourceRoot, root], new ReplayFiles(), maximumFiles: 8192, maximumDirectories: 256);
            Need(ReplayStorage.Bytes(inventory) + 33_554_432 <= 2_100_000_000, "Native2.1GB component cap.");
            probeMetadataHandles = cache.NativeSnapshotHandleCountForChecks;
            var probeTraces = traces.Skip(seedTraceCount).ToArray();
            tracedEstimate = probeTraces.Any(t => t.Scope == "FullObservation" && t.Phases.Any(p => p.Name == "EstimateOutput"))
                && probeTraces.Any(t => t.Scope == "Capacity");
            Need(tracedEstimate && source.ProbeDemand?.Kind == ReplayOutputEvidenceKind.CalibratedEstimate,
                "Earlier incomplete failure did not reach the real second-request Capacity/EstimateOutput trace; no completed profile.");
        }
        catch (Exception error) { failure = error; }
        finally
        {
            if (source != null && !source.OutputProducerStopped) Quarantine("Writer closure unknown during failure cleanup; outer cutoff owns cleanup.");
            try { cache?.Dispose(); } catch (Exception error) { cleanupErrors.Add("Cache:" + error); }
            try { frozen?.Check(); } catch (Exception error) { cleanupErrors.Add("SourceCheck:" + error); }
            try { frozen?.Dispose(); } catch (Exception error) { cleanupErrors.Add("FrozenOwners:" + error); }
            try { Bound(force: true); } catch (Exception error) { cleanupErrors.Add("TerminalBound:" + error); }
        }
        if (failure != null || cleanupErrors.Count != 0)
        {
            Diagnose("failure.json", new { stage, primaryError = failure?.ToString(), cleanupErrors, diagnosticErrors,
                observationMode, files.OutputQueries, files.OutputQueryMilliseconds, files.MaximumOutputQueryMilliseconds,
                files.OutputObservations, files.OutputTraceOverflow,
                written, callbacks, seedResult, seedMetadataHandles, seedSelectedInputs = source?.SeedRequest?.Inputs.Count,
                probe = result, markers, writerCloses = source?.WriterCloses, callbackIntervals, traces, diagnostics,
                boundQueries, boundQueryMilliseconds, maximumBoundQueryMilliseconds,
                allocationQueries, allocationMilliseconds, maximumAllocationMilliseconds, outputPeak });
            Console.Error.WriteLine(failure?.Message ?? "Phase cleanup failed."); return 1;
        }
        try
        {
            Write("profile.json", new { result = "closed-diagnostic-profile-awaits-outer-exit-not-qualification", guardReproduced, tracedEstimate,
                observationMode, files.OutputQueries, files.OutputQueryMilliseconds, files.MaximumOutputQueryMilliseconds,
                files.OutputObservations, files.OutputTraceOverflow,
                seedResult, probe = result, source.SeedDemand, source.ProbeDemand, source.EstimateCalls,
                seedMetadataHandles, probeMetadataHandles, handlesAfterCleanup = cache.NativeSnapshotHandleCountForChecks,
                markers, writerCloses = source.WriterCloses, callbackIntervals, traces, diagnostics, written, peakRam, peakHandles, outputPeak,
                boundQueries, boundQueryMilliseconds, maximumBoundQueryMilliseconds,
                allocationQueries, allocationMilliseconds, maximumAllocationMilliseconds, seconds = clock.Elapsed.TotalSeconds,
                scope = "UNQUALIFIED8151 arithmetic. Simulated NONMEDIA seed only; second refused. No codec/globalSampler/ordinarySaved/source validation or581ms-fix claim. Owned-writer allocation queries and other Bound costs are recorded; do not attribute their delays to Cache phases." });
            Bound(force: true);
            Write("completion.json", new { result = "cleanup-and-postserialization-bounds-passed-awaits-outer-exit", primaryError = (string)null,
                cleanupErrors, diagnosticErrors, seconds = clock.Elapsed.TotalSeconds, requiresSuccessfulControllerReceipt = true });
            Bound(force: true);
        }
        catch (Exception error)
        {
            Diagnose("terminal-failure.json", new { primaryError = error.ToString(), cleanupErrors, diagnosticErrors, seconds = clock.Elapsed.TotalSeconds });
            Console.Error.WriteLine(error.Message); return 1;
        }
        Console.WriteLine("Post-close file-only phase profile completed; no qualification or ordinary Saved claim."); return 0;
    }
}
