using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace F10Recorder;

// Offline product-code construction/validation only. Never starts a recorder,
// capture source, UI or audio playback, or reads/adopts an account catalog.
internal static class ReplayHybridPilot
{
    static readonly JsonSerializerOptions Json = new(WorkerProtocol.Json) { WriteIndented = true };
    const long Cap = 2_500_000_000, Stop = 2_200_000_000, Floor = 60_000_000_000;
    sealed record Case(string Label, double Start, int Seconds);
    sealed record Plan(string Kind, string Root, string Baseline, string Reference, string EngineRoot,
        Dictionary<string, string> Files, Dictionary<string, string> Preserved, string Startup, Case[] Cases,
        long RetainedBytes, string[] RetainedRoots, DateTime CreatedUtc, DateTime ExpiresUtc,
        object Bounds, string Effects, string Claim);
    static void Require(bool value, string reason) { if (!value) throw new IOException(reason); }
    static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    static void Write(string path, object value)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        Require(bytes.Length <= 2_097_152, "Pilot receipt exceeds its bound.");
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read); stream.Write(bytes);
    }
    static string Startup()
    { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"); return key?.GetValue("F10Recorder") as string; }
    static string Repo => Path.GetFullPath(Environment.CurrentDirectory);
    static string ReferenceFile(string reference, string name) => Path.Combine(reference, "original-request-" + name);

    static bool PacketKind(string kind) => kind is "issue83-hybrid-packet-product-pilot-v1" or "issue83-hybrid-packet-batch-product-pilot-v1" or "issue83-hybrid-packet-cancellable-product-pilot-v1";
    internal static int Prepare(string path, string root, string baseline, string engineRoot, string reference, bool packetMux = false,
        bool parallelIndex = false, bool cancellableIndex = false)
    {
        Require(packetMux && parallelIndex && cancellableIndex, "Historical construction routes are frozen evidence; use the cancellable packet route. See tests/replay-latency.md.");
        Require(!parallelIndex || packetMux, "Parallel index experiment requires the explicit packet comparison.");
        Require(!cancellableIndex || parallelIndex, "Cancellable read experiment requires an index batch.");
        root = Path.GetFullPath(root); baseline = Path.GetFullPath(baseline); reference = Path.GetFullPath(reference);
        Require(root.StartsWith(Path.Combine(Repo, "artifacts", "issue83") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !Directory.Exists(root) && !File.Exists(path), "Fresh owned pilot paths required.");
        using var closure = JsonDocument.Parse(File.ReadAllText(Path.Combine(baseline, "closure.json")));
        Require(closure.RootElement.GetProperty("workerJoined").GetBoolean() && closure.RootElement.GetProperty("captureAbsent").GetBoolean()
            && closure.RootElement.GetProperty("workerExit").GetInt32() == 0 && closure.RootElement.GetProperty("error").ValueKind == JsonValueKind.Null,
            "Closed successful baseline required.");
        using var first = JsonDocument.Parse(File.ReadAllText(Path.Combine(baseline, "first-use.json")));
        double original = first.RootElement.GetProperty("selection").GetProperty("Start").GetDouble();
        MediaDependency.RootDirectory = Path.GetFullPath(engineRoot); string engine = MediaDependency.ResolveExecutable();
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Bind(string file) { file = Path.GetFullPath(file); files.Add(file, Hash(file)); }
        string build = Path.GetDirectoryName(typeof(ReplayHybridPilot).Assembly.Location);
        foreach (string name in new[] { "F10Recorder.WorkerChecks.dll", "F10Recorder.Windows.dll", "F10Recorder.Core.dll", "F10Recorder.WorkerChecks.deps.json", "F10Recorder.WorkerChecks.runtimeconfig.json" }) Bind(Path.Combine(build, name));
        Bind(Environment.ProcessPath); Bind(engine); Bind(Path.Combine(Path.GetDirectoryName(engine), "ffprobe.exe"));
        string sdk = Path.GetDirectoryName(Environment.ProcessPath);
        Bind(Path.Combine(sdk, "host", "fxr", Environment.Version.ToString(3), "hostfxr.dll"));
        foreach (string framework in new[] { "Microsoft.NETCore.App", "Microsoft.WindowsDesktop.App" })
            foreach (string file in Directory.EnumerateFiles(Path.Combine(sdk, "shared", framework, Environment.Version.ToString(3)), "*.dll")) Bind(file);
        foreach (string file in new[] { "projects/F10Recorder.WorkerChecks/ReplayHybridPilot.cs", "projects/F10Recorder.WorkerChecks/Program.cs",
            "projects/F10Recorder.Windows/ReplaySaveExport.cs", "projects/F10Recorder.Windows/ReplayOutputWorkspace.cs", "projects/F10Recorder.Windows/ReplayStorage.cs", "projects/F10Recorder.Windows/ReplayCache.cs",
            "projects/F10Recorder.Windows/ReplayExactExport.cs", "projects/F10Recorder.Windows/ReplayExportProcess.cs", "projects/F10Recorder.Core/ReplayHybridPlan.cs", "projects/F10Recorder.Core/ReplayTransportIndex.cs",
            "projects/F10Recorder.CoreChecks/ReplayTransportWriter.cs", "projects/F10Recorder.Core/ReplayAvcPictureLayout.cs",
            "projects/F10Recorder.Core/ReplaySaveTiming.cs", "projects/F10Recorder.Core/ReplayAacCopyPlan.cs",
            "projects/F10Recorder.Core/ReplayCodedPacketHash.cs", "projects/F10Recorder.Core/ReplayMp4Index.cs",
            "projects/F10Recorder.Core/ReplayMp4Integrity.cs", "projects/F10Recorder.Windows/ReplayOutputIntegrity.cs",
            "projects/F10Recorder.Windows/ReplayPublicationLease.cs", "projects/F10Recorder.Windows/ReplayExportLifetime.cs",
            "projects/F10Recorder.Windows/ReplayPacketMuxExport.cs", "projects/F10Recorder.Core/ReplayMp4PacketWriter.cs",
            "projects/F10Recorder.Core/ReplayTransportPacketSamples.cs", "projects/F10Recorder.Core/ReplayTransportBatchIndex.cs",
            "src/ReplayEngine.cs", "src/RecorderMetrics.cs", "src/Native.cs", "tests/replay-hybrid-product.ps1" }) Bind(file);
        Bind(Path.Combine(baseline, "first-use.json")); Bind(Path.Combine(baseline, "closure.json"));
        Bind(Path.Combine(reference, "admitted-plan.json")); Bind(Path.Combine(reference, "original-request", "receipt.json"));
        foreach (string name in new[] { "source-ids.stdout.bin", "source-audio.stdout.bin", "candidate-audio.stdout.bin" }) Bind(ReferenceFile(reference, name));
        var preserved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (string file in new[] { Path.Combine(local, "F10Recorder", "settings.json"), Path.Combine(local, "F10Recorder", "worker-state.json"),
            Path.Combine(local, "F10Recorder.Security", "storage-calibration", "reference-v1.json"),
            Path.Combine(local, "F10Recorder", "application", "F10Recorder.exe"), Path.Combine(local, "F10Recorder", "application", "F10Recorder.Windows.dll"),
            Path.Combine(local, "F10Recorder", "application", "F10Recorder.Core.dll") }) if (File.Exists(file)) preserved.Add(file, Hash(file));
        var retained = new[] { Path.Combine(Repo, "artifacts", "issue83") }.Concat(Directory.GetDirectories(Path.GetTempPath(), "f10-issue83-*"))
            .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        long retainedBytes = ReplayStorage.Bytes(ReplayStorage.Inventory(retained, new ReplayFiles(), 16000, 8192));
        long cap = packetMux ? 1_500_000_000 : Cap, stop = packetMux ? 1_300_000_000 : Stop;
        Require(retainedBytes + cap + 16_000_000 < 22_000_000_000,
            $"Fresh retained+forward+outside-report envelope does not fit22GB: retained={retainedBytes},cap={cap},outside=16000000.");
        var cases = new List<Case> { new("minimum-exact", 203d / 60, 1), new("five-fractional", original, 5), new("reference", original, 300) };
        if (packetMux)
        {
            double aligned = Math.Round(first.RootElement.GetProperty("selection").GetProperty("Inputs")[1].GetProperty("Start").GetDouble() * 60) / 60;
            cases.Add(new("aligned-no-export-child", aligned, 5));
        }
        var now = DateTime.UtcNow;
        var plan = new Plan(cancellableIndex ? "issue83-hybrid-packet-cancellable-product-pilot-v1" : parallelIndex ? "issue83-hybrid-packet-batch-product-pilot-v1" : packetMux ? "issue83-hybrid-packet-product-pilot-v1" : "issue83-hybrid-product-pilot-v1", root, baseline, reference, MediaDependency.RootDirectory, files, preserved, Startup(), cases.ToArray(),
            retainedBytes, retained, now, now.AddMinutes(30), new { mediaChildren = 21, concurrentMediaChildren = 1, parentAndController = 2,
                seconds = 200, childSeconds = 60, outerSeconds = 240, childWorkingSet = 2_147_483_648L, parentWorkingSet = 805_306_368,
                parentHandles = 1536, files = 512, directories = 128, cap, stop, closure = cap - stop, minimumFree = Floor,
                reportBytes = 2_097_152, pcmBytes = 128_000_000, maximumGuardGapMs = 500, aggregate = 22_000_000_000L, outsideGrowthReserve = 16_000_000,
                retainedFiles = 16000, retainedDirectories = 8192, maximumFixedIndexWorkers = parallelIndex ? 4 : 0,
                cancellableIndexReads = cancellableIndex, indexReadDeadlineMs = parallelIndex ? 30000 : 0 },
            (cancellableIndex ? "Source streams use actual asynchronous handles and batch-private cancellable reads with a30s owner deadline, continuous resource checks and mandatory drain/join; no early stream release. " : "") +
            (parallelIndex ? "Protected-source index uses at most4 fixed threads per batch, owner-only resource checks, all workers joined before boundary encoding. No capture work changes. " : "") +
            (packetMux ? "Experimental per-original-chunk direct packet mode:4 managed output writers, at most3 serial NVENC head encoders, zero mux children,4 full validators,4 ID decodes,4 PCM decodes,1 drift remux and5 full publication validators. The aligned case MUST have zero export children and a successful actual managed closure/all-source-hash/native allocation receipt. " : "Retained-source offline only: at most3 serial NVENC head encoders with complete coded tail-prefix copying,3 video/AAC-copy muxes,3 full validators,3 ID decodes,3 audio decodes,1 drift remux,5 positive/negative full validators through ReplayCache. ")
                + "Production integrity checking also checks all outputs and5 controls;5 additional ReplayCache passes exercise its normal integrity gate without media children. Controls are valid,truncated,zeroed packet,deep picture damage and cumulative timing drift. No capture, source window/audio playback, worker/UI/protocol, settings/startup/catalog activation, NVIDIA preference, install or release action.",
            (packetMux ? "Experimental v4 whole exporter: include all protected input indexing, necessary head encoding, proof construction, direct packet extraction/writing/flush/closure and final integrity checking. No assembly or external mux; no per-chunk eligibility bound changes. Ordinary exporter remains v3. " : "Product v3 exporter. ")
                + "Preserve genuine protected AAC preroll and integer-sample tail edits, complete closed-output source-byte/clock/configuration proof, fresh full original validator and bound source-oracle comparisons; test-only rename. Exact PCM sample counts, finite SNR loss<=0.5dB versus accepted reencode and phase correlation>=.97/lag<=4samples are required. No ordinary Saved/capture/resource/percentile/storage qualification. Reused SOURCE oracles never replace fresh OUTPUT decoding. Repeating identical-channel audio proves phase/quality only, not absolute audio identity/channel distinction. Cold OS cache is not claimed. Stop first failure; a slow reference ends feasibility, never starts20-export acceptance.");
        Write(path, plan); Console.WriteLine(Hash(path)); return 0;
    }

    sealed class Runner(Plan plan) : IDisposable
    {
        sealed class GuardFailure(Exception inner) : Exception("Pilot infrastructure/storage guard failed.", inner);
        internal readonly Stopwatch Clock = Stopwatch.StartNew();
        readonly object gate = new();
        internal readonly Native.ProcessJob Job = new();
        readonly List<FileStream> leases = [];
        internal readonly List<object> Processes = [];
        internal readonly List<ReplayExportProcessReceipt> Receipts = [];
        internal readonly List<ReplayOutputInput> Inputs = [];
        Process active;
        int children;
        double lastScan = -1, lastGuard = -1;
        internal double GuardWork, MaximumGuardGap;
        internal long PeakBytes, PeakParentRam;
        internal int PeakParentHandles;
        internal bool ProducerAbsent = true;
        Exception guardFailure;
        internal void ForceObservation() { lastScan = -1; Guard(); }

        internal string HashFile(string path)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var stream = File.OpenRead(path); byte[] buffer = new byte[131072]; int count;
            while ((count = stream.Read(buffer)) != 0) { hash.AppendData(buffer, 0, count); Guard(); }
            return Convert.ToHexString(hash.GetHashAndReset());
        }

        internal void PinSources()
        {
            using var baseline = JsonDocument.Parse(File.ReadAllText(Path.Combine(plan.Baseline, "first-use.json")));
            using var reference = JsonDocument.Parse(File.ReadAllText(Path.Combine(plan.Reference, "admitted-plan.json")));
            var hashes = reference.RootElement.GetProperty("source_inputs").EnumerateArray().ToDictionary(e => Path.GetFullPath(e.GetProperty("path").GetString()), e => e.GetProperty("sha256").GetString(), StringComparer.OrdinalIgnoreCase);
            foreach (var entry in baseline.RootElement.GetProperty("selection").GetProperty("Inputs").EnumerateArray())
            {
                Guard(); var input = entry.Deserialize<ReplayOutputInput>(Json); input = input with { Path = Path.GetFullPath(input.Path) };
                var stream = new FileStream(input.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan); leases.Add(stream);
                var extent = ReplayStorage.ObserveInventory(stream.SafeFileHandle, input.Extent);
                Require(extent.Length == input.Extent.Length && Convert.ToHexString(SHA256.HashData(stream)).Equals(hashes[input.Path], StringComparison.OrdinalIgnoreCase), "Original source changed.");
                Inputs.Add(input);
            }
            Require(Inputs.Count == 301, "Original source cardinality changed.");
        }
        internal void Guard()
        {
            if (guardFailure != null) throw guardFailure;
            try { GuardCore(); }
            catch (Exception error)
            {
                Interlocked.CompareExchange(ref guardFailure, new GuardFailure(error), null);
                throw guardFailure;
            }
        }
        void GuardCore()
        {
            lock (gate)
            {
                double now = Clock.Elapsed.TotalSeconds;
                if (lastGuard >= 0) { MaximumGuardGap = Math.Max(MaximumGuardGap, (now - lastGuard) * 1000); Require(MaximumGuardGap <= 500, "Pilot guard exceeded500ms."); }
                lastGuard = now;
                Require(now < 200, "Pilot deadline with40s outer cleanup reserve.");
                if (now - lastScan < .1) return;
                lastScan = now; long started = Stopwatch.GetTimestamp();
                using var parent = Process.GetCurrentProcess(); PeakParentRam = Math.Max(PeakParentRam, parent.WorkingSet64);
                PeakParentHandles = Math.Max(PeakParentHandles, parent.HandleCount);
                Require(parent.WorkingSet64 < 805_306_368 && parent.HandleCount < 1536,
                    $"Pilot parent resource bound: RAM={parent.WorkingSet64}/805306368,handles={parent.HandleCount}/1536.");
                if (active != null)
                    try
                    {
                        if (!active.HasExited)
                        {
                            bool measured = ReplayExportProcess.TryWorkingSet(active, out long current);
                            Require(measured || active.HasExited, "Live media memory measurement is unavailable.");
                            if (measured) Require(current < 2_147_483_648L, "Pilot media memory bound.");
                        }
                    }
                    catch (InvalidOperationException) { Require(active.HasExited, "Live media identity became unavailable."); }
                var inventory = ReplayStorage.Inventory([plan.Root], new ReplayFiles(), 512, 128);
                long bytes = 0;
                foreach (var entry in inventory)
                {
                    using var handle = ReplayStorage.OpenPinnedInventory(entry.Key);
                    var current = ReplayStorage.ObserveInventory(handle, entry.Value);
                    bytes = checked(bytes + Math.Max(current.Length, current.AllocatedBytes));
                    if (entry.Key.EndsWith(".f32", StringComparison.OrdinalIgnoreCase)) Require(Math.Max(current.Length, current.AllocatedBytes) <= 128_000_000, "Pilot PCM bound.");
                }
                PeakBytes = Math.Max(PeakBytes, bytes);
                long stop = PacketKind(plan.Kind) ? 1_300_000_000 : Stop;
                Require(bytes < stop && plan.RetainedBytes + bytes + 16_000_000 < 22_000_000_000, "Pilot storage early stop.");
                Require(ReplayStorage.ObserveCapacity(plan.Root).AvailableBytes >= Floor, "Pilot caller-free floor.");
                GuardWork += Stopwatch.GetElapsedTime(started).TotalSeconds;
                Require(Stopwatch.GetElapsedTime(started).TotalMilliseconds < 500, "Pilot observation cost bound.");
            }
        }
        internal ReplayExportProcessReceipt Run(string label, IReadOnlyList<string> arguments, Action extra = null, bool probe = false)
        {
            Guard(); Require(++children <= 21, "Prospective media child ceiling.");
            string engine = probe ? Path.Combine(Path.GetDirectoryName(MediaDependency.ExecutablePath), "ffprobe.exe") : MediaDependency.ExecutablePath;
            var receipt = ReplayExportProcess.Run(engine, arguments, plan.Root, Job,
                started: child => { lock (gate) active = child; }, producerStopped: absent => ProducerAbsent = absent,
                timeoutMilliseconds: 60_000, checkStorage: () => { extra?.Invoke(); Guard(); });
            lock (gate) active = null;
            Receipts.Add(receipt);
            Write(Path.Combine(plan.Root, label + ".process.json"), new { arguments, receipt });
            Processes.Add(new { label, receipt.ProcessId, receipt.ProcessStartedUtc, receipt.ExitCode, receipt.Success, receipt.ProducerAbsent,
                receipt.ElapsedMilliseconds, receipt.CpuMilliseconds, receipt.ReadBytes, receipt.WriteBytes, receipt.SampledPeakWorkingSetBytes, receipt.ResourceError });
            ForceObservation();
            Require(receipt.Success, "Pilot child failed: " + label + ": " + receipt.Error); return receipt;
        }
        public void Dispose() { Job.Dispose(); foreach (var lease in leases) lease.Dispose(); }
    }

    static uint[] Ids(string path)
    {
        Require(new FileInfo(path).Length <= 1_000_000, "ID-cell file bound.");
        byte[] bytes = File.ReadAllBytes(path); Require(bytes.Length % 48 == 0, "ID-cell byte count.");
        var ids = new uint[bytes.Length / 48];
        for (int frame = 0; frame < ids.Length; frame++)
            for (int bit = 0; bit < 24; bit++)
            {
                byte first = bytes[frame * 48 + bit], complement = bytes[frame * 48 + 24 + bit];
                Require(!(first > 64 && first < 192) && !(complement > 64 && complement < 192) && (first > 128) != (complement > 128), "Ambiguous source ID/complement.");
                if (first > 128) ids[frame] |= 1u << bit;
            }
        return ids;
    }
    sealed record PhaseResult(int Position, double Correlation, int LagSamples);
    static PhaseResult[] Phase(byte[] expected, byte[] actual, int offset, int samples, Action guard)
    {
        var source = MemoryMarshal.Cast<byte, float>(expected); var candidate = MemoryMarshal.Cast<byte, float>(actual);
        int count = Math.Min(samples, candidate.Length / 2);
        var results = new List<PhaseResult>();
        foreach (int position in new[] { 16, Math.Max(16, count / 2 - 1024), Math.Max(16, count - 4096) })
        {
            guard(); int size = Math.Min(2048, count - position - 16); double best = -1; int bestLag = int.MaxValue;
            Require(size > 0, "Audio phase window is empty.");
            for (int lag = -8; lag <= 8; lag++)
            {
                double cross = 0, a2 = 0, b2 = 0;
                for (int n = 0; n < size * 2; n++)
                {
                    double a = source[(offset + position) * 2 + n], b = candidate[(position + lag) * 2 + n];
                    cross += a * b; a2 += a * a; b2 += b * b;
                }
                double correlation = cross / Math.Sqrt(a2 * b2);
                if (correlation > best) { best = correlation; bestLag = lag; }
            }
            results.Add(new(position, best, bestLag));
        }
        return results.ToArray();
    }
    static double Snr(byte[] expected, byte[] actual, int expectedSample, int sampleCount, int actualSample = 0)
    {
        var source = MemoryMarshal.Cast<byte, float>(expected); var candidate = MemoryMarshal.Cast<byte, float>(actual);
        Require(actualSample >= 0 && actualSample * 2L <= candidate.Length, "PCM candidate oracle offset.");
        int count = Math.Min(sampleCount * 2, candidate.Length - actualSample * 2); Require(expectedSample >= 0 && expectedSample * 2L + count <= source.Length, "PCM oracle range.");
        double signal = 0, error = 0;
        for (int n = 0; n < count; n++) { double a = source[expectedSample * 2 + n], delta = a - candidate[actualSample * 2 + n]; signal += a * a; error += delta * delta; }
        if (!(signal > 0) || !double.IsFinite(signal) || !double.IsFinite(error)) return double.NaN;
        return 10 * Math.Log10(signal / Math.Max(error, double.Epsilon));
    }

    internal static int Run(string path, string expectedHash)
    {
        Require(new FileInfo(path).Length <= 65536 && Hash(path).Equals(expectedHash, StringComparison.OrdinalIgnoreCase), "Frozen pilot plan changed.");
        var plan = JsonSerializer.Deserialize<Plan>(File.ReadAllText(path), Json);
        Require(plan.Kind == "issue83-hybrid-packet-cancellable-product-pilot-v1", "Historical construction plans require their frozen helper/product; see tests/replay-latency.md.");
        bool packetMux = PacketKind(plan.Kind), cancellableIndex = plan.Kind == "issue83-hybrid-packet-cancellable-product-pilot-v1";
        bool parallelIndex = cancellableIndex || plan.Kind == "issue83-hybrid-packet-batch-product-pilot-v1";
        Require((packetMux || plan.Kind == "issue83-hybrid-product-pilot-v1") && DateTime.UtcNow >= plan.CreatedUtc && DateTime.UtcNow.AddSeconds(240) < plan.ExpiresUtc
            && !Directory.Exists(plan.Root) && plan.Root.StartsWith(Path.Combine(Repo, "artifacts", "issue83") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && plan.Cases.Select(c => c.Seconds).SequenceEqual(packetMux ? new[] { 1, 5, 300, 5 } : new[] { 1, 5, 300 }), "Fresh finite pilot required.");
        foreach (string required in new[] { typeof(ReplayHybridPilot).Assembly.Location, typeof(CaptureSession).Assembly.Location, typeof(Settings).Assembly.Location,
            Environment.ProcessPath, Path.Combine(plan.Baseline, "first-use.json"), Path.Combine(plan.Baseline, "closure.json"), Path.Combine(plan.Reference, "admitted-plan.json"),
            Path.Combine(plan.Reference, "original-request", "receipt.json"), ReferenceFile(plan.Reference, "source-ids.stdout.bin"),
            ReferenceFile(plan.Reference, "source-audio.stdout.bin"), ReferenceFile(plan.Reference, "candidate-audio.stdout.bin") })
            Require(plan.Files.ContainsKey(Path.GetFullPath(required)), "Mandatory current runtime/source/oracle role is unbound.");
        foreach (var binding in plan.Files) Require(Hash(binding.Key).Equals(binding.Value, StringComparison.OrdinalIgnoreCase), "Bound input changed: " + binding.Key);
        Directory.CreateDirectory(plan.Root); Log.DirectoryPath = plan.Root; Write(Path.Combine(plan.Root, "admitted-plan.json"), plan);
        MediaDependency.RootDirectory = plan.EngineRoot;
        Require(plan.Files.ContainsKey(MediaDependency.ExecutablePath), "Current engine role is unbound.");
        using var runner = new Runner(plan);
        var results = new List<object>(); string failure = null;
        var expectations = new Dictionary<string, ReplayMp4Expectation>();
        ReplaySaveTiming inProgressTiming = null; string inProgressCase = null; ReplayExportRequest inProgressRequest = null;
        try
        {
            runner.PinSources();
            uint[] expectedIds = Ids(ReferenceFile(plan.Reference, "source-ids.stdout.bin"));
            using var referenceReceipt = JsonDocument.Parse(File.ReadAllText(Path.Combine(plan.Reference, "original-request", "receipt.json")));
            Require(Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(expectedIds.AsSpan()))).Equals(referenceReceipt.RootElement.GetProperty("source_id_sequence_sha256").GetString(), StringComparison.OrdinalIgnoreCase), "Source ID oracle provenance changed.");
            byte[] sourcePcm = File.ReadAllBytes(ReferenceFile(plan.Reference, "source-audio.stdout.bin"));
            byte[] referencePcm = File.ReadAllBytes(ReferenceFile(plan.Reference, "candidate-audio.stdout.bin"));
            foreach (var trial in plan.Cases)
            {
                runner.Guard(); string directory = Path.Combine(plan.Root, trial.Label); Directory.CreateDirectory(directory);
                var interval = new ReplayInterval(trial.Start, trial.Start + trial.Seconds, trial.Seconds);
                var selected = runner.Inputs.Where(i => i.End > interval.Start - ReplayAacCopyPlan.ProtectionSeconds && i.Start < interval.End).ToArray();
                var timing = new ReplaySaveTiming { Frequency = Stopwatch.Frequency, Requested = Stopwatch.GetTimestamp() };
                inProgressCase = trial.Label; inProgressTiming = timing;
                string partial = Path.Combine(directory, "result.partial.mp4"), final = Path.Combine(directory, "result.mp4");
                string concat = Path.Combine(directory, "unused-concat.txt"); File.WriteAllText(concat, "fallback is deliberately ineligible for this pilot");
                var request = new ReplayExportRequest(new(directory, "", trial.Seconds, 20, 60, "h264_nvenc", ".ts", false, true), interval,
                    Array.AsReadOnly(selected), concat, partial, timing);
                inProgressRequest = request;
                int stage = 0; long exportAt = Stopwatch.GetTimestamp();
                ReplaySaveExport.Run(request, (args, guard) => runner.Run(trial.Label + "-export-" + stage++, args, guard), runner.Guard,
                    ReplaySaveExport.DefaultConstruction);
                double exportSeconds = Stopwatch.GetElapsedTime(exportAt).TotalSeconds;
                bool aligned = trial.Label == "aligned-no-export-child";
                Require(timing.ExportRecipe == (ReplaySaveExport.PacketRecipe)
                    && stage == (packetMux ? aligned ? 0 : 1 : 2), "Reference source did not exercise the frozen exporter/head path.");
                if (packetMux) Require(request.ManagedMux is { Closed: true, InputHashesComplete: true, State: "Completed", Error: null, FinalOutput: not null },
                    "Packet candidate has no complete per-request managed writer receipt.");
                if (parallelIndex) Require(request.IndexBatch is { Joined: true, DrainError: null } batch
                    && batch.Workers == Math.Min(4, selected.Length) && batch.PeakActive <= batch.Workers
                    && batch.FinishedSources == selected.Length && batch.UnsupportedSources == 0 && !batch.DeadlineExceeded
                    && batch.CancellableReads == cancellableIndex && batch.ReadDeadlineMilliseconds == 30000,
                    "Parallel index candidate did not join and index every protected source within its worker bound.");
                Require(timing.AsynchronousInputs == (cancellableIndex ? selected.Length : 0), "Actual input handle mode count differs from the admitted construction.");
                Require(request.Integrity != null, "Hybrid output lost its pre-mux coded-byte proof.");
                expectations.Add(trial.Label, request.Integrity);
                long integrityAt = Stopwatch.GetTimestamp(); ReplayIntegrityResult integrity;
                integrity = ReplayOutputIntegrity.Read(request, runner.Guard);
                double integritySeconds = Stopwatch.GetElapsedTime(integrityAt).TotalSeconds;
                var decoded = runner.Run(trial.Label + "-full", ReplayExactExport.DecodeArguments(partial, true, 60));
                var validation = ReplayExactExport.ReadValidation(decoded, interval, 60);
                string cells = Path.Combine(directory, "ids.bin"), pcm = Path.Combine(directory, "audio.f32");
                runner.Run(trial.Label + "-ids", ["-hide_banner", "-v", "error", "-xerror", "-err_detect", "explode", "-ignore_editlist", "1", "-i", partial,
                    "-map", "0:v:0", "-an", "-vf", "crop=576:48:16:8,scale=24:2:flags=area,format=gray", "-fps_mode", "passthrough", "-f", "rawvideo", cells]);
                uint[] actualIds = Ids(cells);
                int pictureOffset = checked((int)(ReplayHybridPlan.FrameAtOrAfter(trial.Start, 60) - ReplayHybridPlan.FrameAtOrAfter(plan.Cases[1].Start, 60)));
                Require(pictureOffset >= 0 && actualIds.Length == trial.Seconds * 60 && actualIds.SequenceEqual(expectedIds.Skip(pictureOffset).Take(actualIds.Length)), "Physical source IDs/boundaries changed.");
                runner.Run(trial.Label + "-audio", ["-hide_banner", "-v", "error", "-xerror", "-err_detect", "explode", "-i", partial,
                    "-map", "0:a:0", "-ar", "48000", "-ac", "2", "-f", "f32le", pcm]);
                byte[] audio = File.ReadAllBytes(pcm); int offset = (int)Math.Round((trial.Start - plan.Cases[1].Start) * 48000);
                Write(Path.Combine(directory, "audio-sample-count.json"), new { bytes = audio.Length, actualSamples = audio.Length / 8d, expectedSamples = trial.Seconds * 48000L });
                Require(audio.Length == trial.Seconds * 48000L * 8, "AAC decoded endpoint/sample count changed.");
                double snr = Snr(sourcePcm, audio, offset, trial.Seconds * 48000);
                var phase = Phase(sourcePcm, audio, offset, trial.Seconds * 48000, runner.Guard);
                double? referenceSnr = Snr(sourcePcm, referencePcm, offset, trial.Seconds * 48000, offset);
                var quality = new { snr = double.IsFinite(snr) ? (double?)snr : null,
                    referenceSnr = referenceSnr.HasValue && double.IsFinite(referenceSnr.Value) ? referenceSnr : null,
                    nonfiniteSnr = !double.IsFinite(snr) || referenceSnr.HasValue && !double.IsFinite(referenceSnr.Value),
                    phase, samples = audio.Length / 8, exactSamples = trial.Seconds * 48000L };
                Write(Path.Combine(directory, "audio-quality.json"), quality);
                Require(double.IsFinite(snr) && (!referenceSnr.HasValue || double.IsFinite(referenceSnr.Value) && snr >= referenceSnr - 0.5), "Full-source audio quality regressed.");
                Require(phase.All(p => double.IsFinite(p.Correlation) && p.Correlation >= .97 && Math.Abs(p.LagSamples) <= 4), "Candidate audio phase regressed.");
                runner.ForceObservation();
                File.Move(partial, final);
                var result = new { trial, status = "offline-test-published", exportSeconds, integrity, integritySeconds,
                    integrityGateTiming = request.IntegrityGateTiming, integrityReadTiming = request.IntegrityReadTiming,
                    validation, timing, indexBatch = request.IndexBatch, managedMux = request.ManagedMux, exportChildren = stage,
                    secondsThroughTestPublication = Stopwatch.GetElapsedTime(timing.Requested).TotalSeconds,
                    frames = actualIds.Length, sourceIdsHash = Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(actualIds.AsSpan()))),
                    snr, referenceSnr, phase, output = final, bytes = new FileInfo(final).Length, sha256 = runner.HashFile(final) };
                Write(Path.Combine(directory, "receipt.json"), result); results.Add(result);
            }
            // Negative controls are run only after all three accurate outputs.
            NegativeControls(plan, runner, expectations[plan.Cases[1].Label], ReplaySaveExport.PacketRecipe);
            Require(plan.Preserved.All(p => Hash(p.Key).Equals(p.Value, StringComparison.OrdinalIgnoreCase)) && Startup() == plan.Startup,
                "Observed installed/profile/startup state changed; do not overwrite user changes.");
        }
        catch (Exception error) { failure = error.ToString(); }
        finally
        {
            try
            {
                var preservation = plan.Preserved.Select(p => new { path = p.Key, unchanged = Hash(p.Key).Equals(p.Value, StringComparison.OrdinalIgnoreCase) }).ToArray();
                bool startupUnchanged = Startup() == plan.Startup;
                Write(Path.Combine(plan.Root, "preservation.json"), new { files = preservation, startupUnchanged, noRestorationOrOverwrite = true });
                if (preservation.Any(p => !p.unchanged) || !startupUnchanged) failure = (failure ?? "") + " Preservation check differs; do not overwrite user state.";
            }
            catch (Exception error) { failure = (failure ?? "") + " Preservation verification incomplete: " + error.Message; }
            Write(Path.Combine(plan.Root, "outcome.json"), new { status = failure == null ? "passed" : "failed", error = failure, results,
                inProgressCase, inProgressTiming, inProgressIndexBatch = inProgressRequest?.IndexBatch,
                inProgressManagedMux = inProgressRequest?.ManagedMux, inProgressIntegrityGateTiming = inProgressRequest?.IntegrityGateTiming,
                inProgressIntegrityReadTiming = inProgressRequest?.IntegrityReadTiming,
                runner.Processes, seconds = runner.Clock.Elapsed.TotalSeconds, runner.PeakBytes, runner.PeakParentRam, runner.PeakParentHandles, runner.GuardWork,
                runner.MaximumGuardGap, runner.ProducerAbsent, noCaptureOrOrdinaryWorkerSaved = true, ordinaryQualification = false });
        }
        Console.WriteLine(failure ?? "Offline hybrid product-code pilot passed; no ordinary Saved or qualification.");
        return failure == null ? 0 : 1;
    }

    sealed class ControlFiles : ReplayFiles
    {
        internal int Publications;
        internal override void Publish(string partial, string final) { base.Publish(partial, final); Publications++; }
        internal override void Publish(ReplayExportRequest request, string final)
        { base.Publish(request, final); if (request.ValidatedOutput != null) Publications++; }
    }
    sealed class ControlSource(ReplaySessionInfo info, ReplayInterval interval, List<Segment> inputs, string payload, Runner runner, string label,
        ReplayMp4Expectation expectation = null, string recipe = null) : IReplaySessionSource
    {
        public ReplaySessionInfo ReplayInfo => info;
        public bool PreserveEvidence => true;
        public bool Stopped => true;
        public string Diagnostics => "Offline closed-source validation/publication control; no ordinary model.";
        public bool OutputProducerStopped => runner.ProducerAbsent;
        public List<Segment> ReadSegments() => inputs;
        public double SaveCutoffAt(long triggerTimestamp) => interval.End;
        public void FreezeAt(long timestamp) { }
        public void StopCapture() { }
        public void CheckCaptureHealth(double end) { }
        public void Export(IReadOnlyList<string> arguments, string snapshot) { File.Copy(payload, arguments[^1]); runner.Guard(); }
        public void Export(ReplayExportRequest request, string snapshot, Action<ReplayProducerProof> changed, Action check)
        {
            Export(request.LegacyArguments, snapshot);
            if (expectation != null) { request.SelectRecipe(recipe ?? ReplaySaveExport.Recipe); request.FreezeIntegrity(expectation); }
        }
        public ReplayValidation ValidateOutput(string path, ReplayInterval requested, Action<ReplayProducerProof> changed, Action check)
            => ReplayExactExport.ReadValidation(runner.Run(label + "-validation", ReplayExactExport.DecodeArguments(path, true, 60), check), requested, 60);
        public ReplayValidation ValidateOutput(ReplayExportRequest request, Action<ReplayProducerProof> changed, Action check)
        {
            if (expectation == null) return ValidateOutput(request.Partial, request.Interval, changed, check);
            long at = Stopwatch.GetTimestamp(); var result = ReplayOutputIntegrity.Read(request, () => { check?.Invoke(); runner.Guard(); }, retainForPublication: true);
            return new(ReplayOutputIntegrity.Kind, 0, Stopwatch.GetElapsedTime(at).TotalMilliseconds, result.CodedPictures, result.AudioPackets);
        }
        public void Dispose() { }
    }
    static void NegativeControls(Plan plan, Runner runner, ReplayMp4Expectation expectation, string recipe)
    {
        var trial = plan.Cases[1]; string valid = Path.Combine(plan.Root, trial.Label, "result.mp4");
        string root = Path.Combine(plan.Root, "controls"); Directory.CreateDirectory(root);
        string truncated = Path.Combine(root, "truncated.mp4"), corrupt = Path.Combine(root, "corrupt.mp4"), drift = Path.Combine(root, "drift.mp4"), deep = Path.Combine(root, "deep.mp4");
        File.Copy(valid, truncated); using (var file = new FileStream(truncated, FileMode.Open, FileAccess.Write, FileShare.Read)) file.SetLength(file.Length * 3 / 4);
        ReplayMp4Sample packet;
        using (var payload = new FileStream(valid, FileMode.Open, FileAccess.Read, FileShare.Read))
            packet = ReplayMp4Reader.Read(payload, payload.Length, new(trial.Start, trial.Start + 5, 5), 60, true, runner.Guard).Tracks.Single(t => t.Kind == "vide").Samples[10];
        long position = packet.Offset; int size = packet.Bytes;
        Require(position >= 0 && size is > 64 and < 1_000_000 && position + size <= new FileInfo(valid).Length, "Corrupt-control packet extent.");
        File.Copy(valid, corrupt);
        using (var file = new FileStream(corrupt, FileMode.Open, FileAccess.Write, FileShare.Read)) { file.Position = position; file.Write(new byte[size]); }
        File.Copy(valid, deep); long damagedAt = -1; int damagedBytes = 0;
        using (var file = new FileStream(deep, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            byte[] bytes = new byte[size]; file.Position = position; file.ReadExactly(bytes);
            for (int offset = 0; offset < size;)
            {
                int count = checked((int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4))); int start = offset + 4;
                Require(count > 0 && count <= size - start, "Deep control NAL extent.");
                if ((bytes[start] & 31) is 1 or 5)
                {
                    Require(count > 512, "Deep control lacks a substantial coded body.");
                    damagedAt = position + start + 256; damagedBytes = count - 256; file.Position = damagedAt; file.Write(new byte[damagedBytes]); break;
                }
                offset = start + count;
            }
        }
        Require(damagedAt >= 0, "Deep control has no VCL body."); runner.ForceObservation();
        runner.Run("control-drift", ["-hide_banner", "-v", "warning", "-xerror", "-i", valid, "-map", "0", "-c", "copy", "-bsf:v",
            "setts=pts=PTS*1.0005:dts=DTS*1.0005", "-avoid_negative_ts", "disabled", "-movflags", "+faststart",
            "-video_track_timescale", "48000", "-movie_timescale", "720000", drift]);
        var interval = new ReplayInterval(trial.Start, trial.Start + 5, 5);
        var inputs = runner.Inputs.Where(i => i.End > interval.Start && i.Start < interval.End)
            .Select(i => new Segment { Path = i.Path, Start = i.Start, End = i.End }).ToList();
        var results = new List<object>();
        foreach (var control in new[] { (Label: "valid", File: valid, Success: true), (Label: "truncated", File: truncated, Success: false),
            (Label: "corrupt", File: corrupt, Success: false), (Label: "deep", File: deep, Success: false), (Label: "drift", File: drift, Success: false) })
        {
            runner.Guard(); string directory = Path.Combine(root, control.Label); Directory.CreateDirectory(directory);
            ReplayIntegrityResult fast = null; string fastError = null;
            try
            {
                using var payload = new FileStream(control.File, FileMode.Open, FileAccess.Read, FileShare.Read);
                fast = expectation.Verify(ReplayMp4Reader.Read(payload, payload.Length, interval, 60, true, runner.Guard), runner.Guard);
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException) { fastError = ex.ToString(); }
            Require((fast != null) == control.Success, "Packet-integrity sensitivity disagrees with the frozen control: " + control.Label);
            if (control.Label == "drift") Require(fastError?.Contains("MP4 cumulative picture timing/cadence drift.", StringComparison.Ordinal) == true,
                "Fast drift control rejected for a different cause than cumulative timing.");
            if (control.Label == "deep") Require(fastError?.Contains("source bytes changed at picture 10.", StringComparison.Ordinal) == true,
                "Deep control did not exercise pre-mux source-byte proof beyond the picture header.");
            var files = new ControlFiles();
            using var source = new ControlSource(new(directory, "", 5, 20, 60, "h264_nvenc", ".ts", false, true), interval, inputs, control.File, runner, control.Label);
            using var cache = new ReplayCache(files, cacheDirectory: directory);
            int previous = runner.Receipts.Count;
            var id = cache.RegisterSession(source); var save = cache.TrySave(id, Stopwatch.GetTimestamp(), Path.Combine(directory, "output"));
            Require(save.Accepted, "Closed-source control admission failed: " + save.Rejection);
            // The save task owns the media Process object and its sampler.
            // Avoid racing that object's disposal from this waiting thread.
            while (!save.Completion.IsCompleted) { Thread.Sleep(25); Require(runner.Clock.Elapsed.TotalSeconds < 200, "Control deadline."); }
            var result = save.Completion.GetAwaiter().GetResult();
            Require(runner.Receipts.Count == previous + 1, "Publication control did not execute exactly one fresh decoder.");
            var receipt = runner.Receipts[^1];
            Require(receipt.ProducerAbsent && receipt.ActualExitObserved && receipt.CaptureComplete && !receipt.TimedOut && !receipt.OutputLimitExceeded
                && (receipt.Failure == null || receipt.Failure == "exit-nonzero"), "Control failed for infrastructure/cleanup instead of media sensitivity.");
            if (control.Label == "drift") Require(receipt.Success && System.Text.RegularExpressions.Regex.Matches(receipt.StandardError,
                @"\bn:\s*\d+\s+pts:\s*-?\d+\s+pts_time:").Count > 1, "Drift control did not exercise the cumulative timing gate.");
            Require(result.Succeeded == control.Success && files.Publications == (control.Success ? 1 : 0), "Full validator/publication sensitivity failed: " + control.Label);
            string normalDirectory = Path.Combine(directory, "normal"); Directory.CreateDirectory(normalDirectory);
            var normalFiles = new ControlFiles();
            using var normalSource = new ControlSource(new(normalDirectory, "", 5, 20, 60, "h264_nvenc", ".ts", false, true), interval, inputs, control.File, runner, control.Label, expectation, recipe);
            using var normalCache = new ReplayCache(normalFiles, cacheDirectory: normalDirectory);
            int beforeNormal = runner.Receipts.Count;
            var normalId = normalCache.RegisterSession(normalSource); var normalSave = normalCache.TrySave(normalId, Stopwatch.GetTimestamp(), Path.Combine(normalDirectory, "output"));
            Require(normalSave.Accepted, "Normal integrity control admission failed: " + normalSave.Rejection);
            while (!normalSave.Completion.IsCompleted) { Thread.Sleep(25); Require(runner.Clock.Elapsed.TotalSeconds < 200, "Normal control deadline."); }
            var normal = normalSave.Completion.GetAwaiter().GetResult(); runner.ForceObservation();
            Require(runner.Receipts.Count == beforeNormal && normal.Succeeded == control.Success && normalFiles.Publications == (control.Success ? 1 : 0),
                "Normal integrity/publication sensitivity failed: " + control.Label);
            if (normal.Succeeded) Require(normal.Validation.Kind == ReplayOutputIntegrity.Kind && normal.Validation.Frames == 0
                && normal.Validation.CodedPictures == 300 && normal.Validation.AudioPackets > 0, "Normal check misreported decoding or coded counts.");
            results.Add(new { control.Label, expectedSuccess = control.Success, fast, fastError, result.Succeeded, files.Publications, result.Error, result.Validation, result.Interval,
                normal = new { normal.Succeeded, normal.Error, normal.Validation, normalFiles.Publications } });
        }
        Write(Path.Combine(root, "receipt.json"), new { positiveAndNegative = results, packetPosition = position, packetBytes = size, damagedAt, damagedBytes,
            timingDrift = "0.05percent cumulative video clock expansion, below one-frame total endpoint tolerance", ordinaryAdmission = false });
    }
}
