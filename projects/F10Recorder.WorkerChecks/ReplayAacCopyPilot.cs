using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace F10Recorder;

// Finite 5-second rejection gate for copying AAC with genuine preroll. This
// never captures, installs, activates a catalog or publishes a user recording.
internal static class ReplayAacCopyPilot
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    sealed record Plan(string Kind, string Root, string EngineRoot, string Input, string SourcePcm,
        string ReferencePcm, Dictionary<string, string> Files, Dictionary<string, string> Preserved,
        string Startup, DateTime CreatedUtc, DateTime ExpiresUtc, object Bounds, string Decision, long RetainedBytes = 0);
    sealed class AudioQuality
    {
        public int SourceSamples { get; set; }
        public int BaselineSamples { get; set; }
        public int ActualSamples { get; set; }
        public bool EndpointPassed { get; set; }
        public bool Finite { get; set; }
        public bool Passed { get; set; }
        public string Error { get; set; }
        public List<object> Channels { get; } = [];
    }
    static void Require(bool value, string why) { if (!value) throw new IOException(why); }
    static string Hash(string file) { using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); return Convert.ToHexString(SHA256.HashData(stream)); }
    static void Write(string file, object value) => File.WriteAllText(file, JsonSerializer.Serialize(value, Json));
    static string Startup()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        object value = key?.GetValue("F10Recorder", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return value == null ? "absent" : JsonSerializer.Serialize(new { value, kind = key.GetValueKind("F10Recorder").ToString() });
    }
    internal static int Prepare(string path, string root, string productPlan, bool packetMux = false)
    {
        path = Path.GetFullPath(path); root = Path.GetFullPath(root); productPlan = Path.GetFullPath(productPlan);
        Require(!File.Exists(path) && !Directory.Exists(root) && root.StartsWith(Path.GetFullPath("artifacts/issue83") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Fresh AAC-copy plan/output required.");
        using var previous = JsonDocument.Parse(File.ReadAllText(productPlan)); var old = previous.RootElement;
        string product = old.GetProperty("Root").GetString(), reference = old.GetProperty("Reference").GetString();
        using (var priorOutcome = JsonDocument.Parse(File.ReadAllText(Path.Combine(product, "outcome.json"))))
            Require(priorOutcome.RootElement.GetProperty("status").GetString() == "passed"
                && priorOutcome.RootElement.GetProperty("error").ValueKind == JsonValueKind.Null, "Closed successful product source required.");
        string input = Path.Combine(product, "five-fractional", "result.partial.mp4.work", "input.ts");
        string pcm = Path.Combine(reference, "original-request-source-audio.stdout.bin"), baseline = Path.Combine(product, "five-fractional", "audio.f32");
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); void Bind(string f) { f = Path.GetFullPath(f); files[f] = Hash(f); }
        foreach (string f in new[] { input, pcm, baseline, productPlan, Path.Combine(product, "outcome.json"), Path.Combine(product, "five-fractional", "receipt.json"),
            "projects/F10Recorder.WorkerChecks/ReplayAacCopyPilot.cs", "projects/F10Recorder.WorkerChecks/Program.cs", "projects/F10Recorder.Core/ReplayTransportIndex.cs",
            "projects/F10Recorder.Core/ReplayMp4Index.cs", "projects/F10Recorder.Core/ReplayMp4Integrity.cs", "projects/F10Recorder.Core/ReplayCodedPacketHash.cs",
            "projects/F10Recorder.Windows/ReplayExactExport.cs", "projects/F10Recorder.Windows/ReplayExportProcess.cs", "projects/F10Recorder.Windows/ReplayStorage.cs" }) Bind(f);
        Bind("tests/replay-aac-copy-pilot.ps1");
        if (packetMux)
            foreach (string file in new[] { "projects/F10Recorder.Core/ReplayMp4PacketWriter.cs", "projects/F10Recorder.Core/ReplayTransportPacketSamples.cs",
                "projects/F10Recorder.Core/ReplayAvcPictureLayout.cs", "projects/F10Recorder.Core/ReplayAacCopyPlan.cs", "projects/F10Recorder.Core/ReplayHybridPlan.cs" }) Bind(file);
        string build = Path.GetDirectoryName(typeof(ReplayAacCopyPilot).Assembly.Location);
        foreach (string name in new[] { "F10Recorder.WorkerChecks.dll", "F10Recorder.WorkerChecks.deps.json", "F10Recorder.WorkerChecks.runtimeconfig.json", "F10Recorder.Windows.dll", "F10Recorder.Core.dll" }) Bind(Path.Combine(build, name));
        Bind(Environment.ProcessPath); MediaDependency.RootDirectory = old.GetProperty("EngineRoot").GetString(); Bind(MediaDependency.ResolveExecutable());
        string sdk = Path.GetDirectoryName(Environment.ProcessPath);
        Bind(Path.Combine(sdk, "host", "fxr", Environment.Version.ToString(3), "hostfxr.dll"));
        foreach (string framework in new[] { "Microsoft.NETCore.App", "Microsoft.WindowsDesktop.App" })
            foreach (string file in Directory.EnumerateFiles(Path.Combine(sdk, "shared", framework, Environment.Version.ToString(3)), "*.dll")) Bind(file);
        var preserved = old.GetProperty("Preserved").EnumerateObject().ToDictionary(p => p.Name, p => Hash(p.Name), StringComparer.OrdinalIgnoreCase);
        foreach (string f in preserved.Keys) Bind(f);
        Require(new FileInfo(input).Length < 32_000_000 && new FileInfo(baseline).Length == 1_920_000 && new FileInfo(pcm).Length >= 1_920_000, "Bounded known 5s source/oracle required.");
        long retained = 0;
        if (packetMux)
        {
            string[] roots = new[] { Path.GetFullPath("artifacts/issue83") }.Concat(Directory.GetDirectories(Path.GetTempPath(), "f10-issue83-*")).ToArray();
            retained = ReplayStorage.Bytes(ReplayStorage.Inventory(roots, new ReplayFiles(), 16000, 8192));
            Require(retained + 64_000_000 + 16_000_000 < 22_000_000_000L, "Direct mux retained/forward envelope exceeds22GB.");
        }
        var now = DateTime.UtcNow;
        Write(path, new Plan(packetMux ? "issue83-direct-packet-mux-short-v1" : "issue83-aac-copy-short-gate-v1", root, MediaDependency.RootDirectory, input, pcm, baseline, files, preserved, Startup(), now, now.AddMinutes(30),
            new { mediaChildren = packetMux ? 2 : 3, concurrentMedia = 1, parentSeconds = 60, childSeconds = 12, outerSeconds = 75, parentMemory = 268435456, childMemory = 536870912,
                expectedBytes = 24_000_000, capBytes = 64_000_000, stopBytes = 48_000_000, closureReserve = 16_000_000, pollGrowthReserve = 16_000_000,
                minimumFreeBytes = 60_000_000_000L, maximumGuardGapMs = 500, outputFiles = 32, outputDirectories = 1, childDiagnostics = 1048576,
                aggregateBytes = 22_000_000_000L, outsideGrowthReserve = 16_000_000, retainedFiles = 16000, retainedDirectories = 8192 },
            (packetMux ? "One managed direct packet-to-MP4 5s writer, zero mux/encoding children; exactly one original full software validator and one PCM decoder. Record managed parent identity, CPU, active/finalized/flushed/closed states, final native identity/EOF/allocation and complete input rehash. Require exact240000 decoded stereo PCM frames and byte-identical PCM to the v3 copy-mux reference. " : "Exactly one copied-video/copied-AAC 5s mux, original fresh full software output validator, one full PCM decode. ")
                + "No capture/NVENC/GPU/playback/worker/UI/catalog/install/publication. Read source/AAC proofs before mux; preserve genuine>=2-frame AAC preroll, exact source packet bytes/order and explicit priming/tail durations. Both-track integrity proof and original full validation must pass. Per-channel SNR may lose<=0.5dB vs accepted baseline; phase correlation>=.97 and lag<=4samples. First mismatch stops without tuning; no300s follow-up or fault qualification from this admission. Preassembled input excludes prior head/assembly work; not whole-save performance.", retained));
        Console.WriteLine(Hash(path)); return 0;
    }
    internal static int Run(string path, string expected)
    {
        byte[] planBytes;
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        { Require(file.Length <= 131072, "AAC-copy plan byte bound."); planBytes = new byte[(int)file.Length]; file.ReadExactly(planBytes); }
        Require(Convert.ToHexString(SHA256.HashData(planBytes)).Equals(expected, StringComparison.OrdinalIgnoreCase), "Frozen AAC-copy plan changed.");
        var plan = JsonSerializer.Deserialize<Plan>(planBytes, Json);
        bool packetMux = plan.Kind == "issue83-direct-packet-mux-short-v1";
        Require((packetMux || plan.Kind == "issue83-aac-copy-short-gate-v1") && (!packetMux || plan.RetainedBytes > 0) && DateTime.UtcNow >= plan.CreatedUtc && DateTime.UtcNow.AddSeconds(75) < plan.ExpiresUtc
            && !Directory.Exists(plan.Root) && plan.Root.StartsWith(Path.GetFullPath("artifacts/issue83") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Fresh AAC-copy admission required.");
        Require(plan.Files.Count <= 768, "AAC-copy binding count bound.");
        foreach (string role in new[] { Environment.ProcessPath, typeof(ReplayAacCopyPilot).Assembly.Location, typeof(CaptureSession).Assembly.Location, typeof(Settings).Assembly.Location })
            Require(plan.Files.ContainsKey(Path.GetFullPath(role)), "Current AAC-copy runtime/helper unbound.");
        Directory.CreateDirectory(plan.Root); Log.DirectoryPath = plan.Root; File.WriteAllBytes(Path.Combine(plan.Root, "admitted-plan.json"), planBytes);
        using var job = new Native.ProcessJob(); var streams = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase);
        var receipts = new List<object>(); var clock = Stopwatch.StartNew(); Process active = null; bool producersAbsent = true;
        double lastGuard = -1, lastScan = -1, maximumGap = 0; long peakBytes = 0, peakParent = 0;
        void Guard()
        {
            double now = clock.Elapsed.TotalSeconds;
            if (lastGuard >= 0) maximumGap = Math.Max(maximumGap, (now - lastGuard) * 1000); lastGuard = now;
            Require(now < 60 && maximumGap <= 500, "AAC-copy time/guard gap.");
            if (now - lastScan < .1) return; lastScan = now;
            using var parent = Process.GetCurrentProcess(); Require(ReplayExportProcess.TryWorkingSet(parent, out long ram), "AAC-copy parent memory unavailable.");
            peakParent = Math.Max(peakParent, ram); Require(ram < 268435456, "AAC-copy parent memory bound.");
            if (active != null && !active.HasExited)
            { Require(ReplayExportProcess.TryWorkingSet(active, out long childRam) || active.HasExited, "AAC-copy child memory unavailable."); Require(childRam < 536870912, "AAC-copy child memory bound."); }
            Require(!Directory.EnumerateDirectories(plan.Root).Any(), "Unexpected AAC-copy subdirectory.");
            var names = Directory.EnumerateFiles(plan.Root).ToArray(); Require(names.Length <= 32, "AAC-copy output file bound.");
            long bytes = 0; var files = new ReplayFiles();
            foreach (string file in names)
            {
                var listed = files.Observe(file); using var handle = ReplayStorage.OpenPinnedInventory(file);
                var current = ReplayStorage.ObserveInventory(handle, listed); bytes = checked(bytes + Math.Max(current.Length, current.AllocatedBytes));
            }
            peakBytes = Math.Max(peakBytes, bytes);
            Require(bytes < 48_000_000 && ReplayStorage.ObserveCapacity(plan.Root).AvailableBytes >= 60_000_000_000, "AAC-copy storage/free-space bound.");
            if (packetMux) Require(plan.RetainedBytes + bytes + 16_000_000 < 22_000_000_000L, "Direct mux aggregate storage bound.");
        }
        ReplayExportProcessReceipt RunChild(string label, IReadOnlyList<string> args)
        {
            Guard(); Require(receipts.Count < (packetMux ? 2 : 3), "AAC-copy child ceiling.");
            ReplayExportProcessReceipt receipt; bool absent = true;
            try { receipt = ReplayExportProcess.Run(MediaDependency.ExecutablePath, args, plan.Root, job, started: p => { active = p; absent = false; }, producerStopped: stopped => absent = stopped,
                timeoutMilliseconds: 12000, outputLimitBytes: 1048576, checkStorage: Guard); }
            finally { producersAbsent &= absent; active = null; }
            producersAbsent &= receipt.ProducerAbsent; Write(Path.Combine(plan.Root, label + ".process.json"), new { arguments = args, receipt }); receipts.Add(new { label, receipt });
            Require(receipt.Success && receipt.ResourceError == null, "AAC-copy child failed: " + label + ": " + receipt.Error); Guard(); return receipt;
        }
        string failure = null; AudioQuality quality = null; ReplayIntegrityResult integrity = null; ReplayValidation validation = null; double muxSeconds = 0, sourceIndexSeconds = 0;
        object managedWriter = null;
        try
        {
            long total = 0; byte[] hashBuffer = new byte[65536];
            foreach (var file in plan.Files)
            {
                Guard(); bool preservationOnly = plan.Preserved.ContainsKey(file.Key);
                var source = new FileStream(file.Key, FileMode.Open, FileAccess.Read, preservationOnly ? FileShare.ReadWrite | FileShare.Delete : FileShare.Read, 65536, FileOptions.SequentialScan);
                if (!preservationOnly) streams.Add(file.Key, source);
                try
                {
                    total = checked(total + source.Length); Require(total < 512_000_000, "AAC-copy input byte cap."); using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); int read;
                    while ((read = source.Read(hashBuffer)) != 0) { hash.AppendData(hashBuffer, 0, read); Guard(); }
                    Require(Convert.ToHexString(hash.GetHashAndReset()) == file.Value, "AAC-copy bound bytes changed.");
                }
                finally { if (preservationOnly) source.Dispose(); }
            }
            MediaDependency.RootDirectory = plan.EngineRoot; Require(plan.Files.ContainsKey(MediaDependency.ExecutablePath), "AAC-copy actual engine is unbound.");
            var sourceTs = streams[plan.Input]; sourceTs.Position = 0;
            var indexClock = Stopwatch.StartNew(); var sourceIndex = ReplayTransportReader.Read(sourceTs, sourceTs.Length, 60, true, Guard); sourceIndexSeconds = indexClock.Elapsed.TotalSeconds;
            var selectedAudio = sourceIndex.AudioFrames.Where(f => f.Pts >= 895500 && f.Pts < 1350000).ToArray();
            Require(sourceIndex.Pictures.Count == 300 && selectedAudio.Length > 0 && selectedAudio[0].Pts < 900000 - 3840, "AAC-copy frozen5s source/preroll unavailable.");
            long priming = (long)Math.Round((900000 - selectedAudio[0].Pts) * 48000d / 90000);
            var audioProof = selectedAudio.Select((f, n) => new ReplayExpectedAudio(n + 1 < selectedAudio.Length ? 1024 : 240000 + priming - n * 1024L, f.PayloadSha256)).ToArray();
            if (packetMux)
            {
                var samplePlan = ReplayAacCopyPlan.Create([new(0, 5, sourceIndex)], new(0, 300, 0, [new(0, 0, 0, 0, true)]), new(0, 5, 5), Guard);
                Require(samplePlan.PrimingSamples == priming && samplePlan.Expected.SequenceEqual(audioProof), "Short direct mux differs from the integer-sample source plan.");
            }
            var proof = new ReplayMp4Expectation(60, (sourceIndex.Pictures[0].Pts - 900000) / 90000d, sourceIndex.Sps.Span, sourceIndex.Pps.Span,
                sourceIndex.Pictures.Select(p => new ReplayExpectedPicture(p.Idr, p.CodedSha256, p.PacketSha256)), priming, audioProof);
            Write(Path.Combine(plan.Root, "expected.json"), new { sourceIndex.Sha256, priming, audioProof,
                pictures = sourceIndex.Pictures.Select(p => new ReplayExpectedPicture(p.Idr, p.CodedSha256, p.PacketSha256)),
                firstVideoSeconds = (sourceIndex.Pictures[0].Pts - 900000) / 90000d,
                selectedAudio = selectedAudio.Select(f => new { f.Pts, f.Bytes, f.PayloadSha256 }) });
            string output = Path.Combine(plan.Root, "copy.mp4"), pcm = Path.Combine(plan.Root, "copy.f32");
            if (packetMux)
            {
                var muxClock = Stopwatch.StartNew();
                managedWriter = DirectMux(output, sourceTs, sourceIndex, priming, audioProof, selectedAudio[0].Pts, selectedAudio[^1].Pts,
                    Guard, () => { lastScan = -1; Guard(); }, Path.Combine(plan.Root, "managed-writer.json"));
                muxSeconds = muxClock.Elapsed.TotalSeconds;
            }
            else
            {
            var mux = RunChild("mux", ["-hide_banner", "-v", "warning", "-nostdin", "-xerror", "-err_detect", "explode", "-copyts", "-i", plan.Input,
                "-map", "0:v:0", "-c:v", "copy", "-bsf:v", "setts=pts=PTS-10/TB:dts=DTS-10/TB", "-map", "0:a:0", "-c:a", "copy", "-bsf:a",
                "noise=amount=0:drop='lt(pts*tb,9.95)+gte(pts*tb,15)',setts=pts=PTS-10/TB:dts=DTS-10/TB:duration='min(DURATION,15/TB-PTS)'",
                "-avoid_negative_ts", "disabled", "-moov_size", "1048576", "-video_track_timescale", "48000", "-movie_timescale", "720000", output]); muxSeconds = mux.ElapsedMilliseconds / 1000d;
            }
            using var movie = new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.Read);
            var interval = new ReplayInterval(0, 5, 5); var inspected = ReplayMp4Reader.Read(movie, movie.Length, interval, 60, true, Guard);
            Write(Path.Combine(plan.Root, "actual-index.json"), inspected); integrity = proof.Verify(inspected, Guard);
            validation = ReplayExactExport.ReadValidation(RunChild("full", ReplayExactExport.DecodeArguments(output, true, 60)), interval, 60);
            RunChild("audio", ["-hide_banner", "-v", "error", "-nostdin", "-xerror", "-err_detect", "explode", "-i", output, "-map", "0:a:0", "-vn", "-ar", "48000", "-ac", "2", "-f", "f32le", pcm]);
            var original = streams[plan.SourcePcm]; original.Position = 0; byte[] sourceBytes = new byte[1920000]; original.ReadExactly(sourceBytes);
            var baseline = streams[plan.ReferencePcm]; baseline.Position = 0; byte[] baselineBytes = new byte[1920000]; baseline.ReadExactly(baselineBytes);
            byte[] actual = File.ReadAllBytes(pcm); quality = new AudioQuality();
            Quality(sourceBytes, baselineBytes, actual, Guard, quality);
            Write(Path.Combine(plan.Root, "quality.json"), quality);
            if (packetMux)
            {
                Require(actual.Length == 1_920_000, "Direct mux decoded PCM endpoint is not exactly240000 stereo samples.");
                bool exactPcm = actual.AsSpan().SequenceEqual(baselineBytes);
                Write(Path.Combine(plan.Root, "pcm-identity.json"), new { exactPcm, actualSha256 = Convert.ToHexString(SHA256.HashData(actual)),
                    baselineSha256 = Convert.ToHexString(SHA256.HashData(baselineBytes)) });
                Require(exactPcm, "Packet-only mux changed the decoded PCM relative to its v3 copy-mux reference.");
            }
            Require(quality.Passed, "AAC-copy decoded sample endpoint, quality or phase rejected; numeric observations retained."); Guard();
        }
        catch (Exception ex) { failure = ex.ToString(); }
        finally
        {
            foreach (var stream in streams.Values) stream.Dispose();
            var cleanup = Stopwatch.StartNew(); var preservation = new List<object>(); string preservationError = null; bool? startupUnchanged = null;
            try
            {
                byte[] buffer = new byte[65536]; long bytes = 0; bool allUnchanged = true;
                foreach (var file in plan.Preserved)
                {
                    using var source = new FileStream(file.Key, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); int read;
                    while ((read = source.Read(buffer)) != 0) { bytes += read; Require(bytes < 256_000_000 && cleanup.Elapsed.TotalSeconds < 5, "AAC-copy bounded preservation check."); digest.AppendData(buffer, 0, read); }
                    bool unchanged = Convert.ToHexString(digest.GetHashAndReset()) == file.Value; allUnchanged &= unchanged;
                    preservation.Add(new { path = file.Key, unchanged });
                }
                startupUnchanged = Startup() == plan.Startup;
                Require(startupUnchanged == true && allUnchanged, "Observed recorder/profile/startup changed; no restoration or overwrite.");
            }
            catch (Exception ex) { preservationError = ex.ToString(); failure ??= preservationError; }
            Write(Path.Combine(plan.Root, "preservation.json"), new { files = preservation, startupUnchanged, error = preservationError, noRestorationOrOverwrite = true });
            Write(Path.Combine(plan.Root, "outcome.json"), new { status = failure == null ? (packetMux ? "passed-short-direct-mux-gate" : "passed-short-aac-copy-gate") : "failed", error = failure, receipts, integrity, validation, quality,
                muxSeconds, sourceIndexSeconds, managedWriter, secondsIncludingPreparationAndOracles = clock.Elapsed.TotalSeconds, peakBytes, peakParent, maximumGap,
                ProducerAbsent = producersAbsent, parentPid = Environment.ProcessId, capture = false, publication = false,
                limitation = "Single5s retained-source rejection gate; preassembled TS is an isolated mux input, not preparation-free ordinary Saved performance or 300s/native-capture qualification." });
        }
        if (failure != null) { Console.Error.WriteLine(failure); return 1; } return 0;
    }
    static object DirectMux(string path, FileStream source, ReplayTransportIndex index, long priming, ReplayExpectedAudio[] expectedAudio,
        long firstAudioPts, long lastAudioPts, Action guard, Action forceGuard, string receiptPath)
    {
        var clock = Stopwatch.StartNew(); using var parent = Process.GetCurrentProcess(); double initialCpu = parent.TotalProcessorTime.TotalMilliseconds;
        FileStream output = null; ReplayMp4PacketWriter writer = null; Exception failure = null; string closeError = null, observationError = null;
        bool inputComplete = false, closed = false; ReplayFileExtent finalOutput = null;
        try
        {
            output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536, FileOptions.SequentialScan);
            writer = new ReplayMp4PacketWriter(output, 60, (index.Pictures[0].Pts - ReplayHybridPlan.OutputEpoch) / 90000d,
                index.Sps.Span, index.Pps.Span, index.Pictures.Count, priming, 240000, expectedAudio, guard);
            source.Position = 0;
            ReplayTransportPacketSamples.Read(source, index, (picture, bytes) => writer.Video(bytes, picture.Idr),
                (frame, bytes) => { if (frame.Pts >= firstAudioPts && frame.Pts <= lastAudioPts) writer.Audio(bytes); }, guard);
            inputComplete = true; writer.Complete();
        }
        catch (Exception error) { failure = error; writer?.Abort(); }
        try { if (output != null) { output.Dispose(); closed = true; } }
        catch (Exception error) { closeError = error.ToString(); failure ??= error; writer?.Abort(); }
        try
        {
            forceGuard();
            if (output != null)
            {
                var listed = ReplayStorage.Observe(path); using var handle = ReplayStorage.OpenPinnedInventory(path);
                finalOutput = ReplayStorage.ObserveInventory(handle, listed);
            }
        }
        catch (Exception error) { observationError = error.ToString(); failure ??= error; writer?.Abort(); }
        parent.Refresh();
        var report = new { parentPid = Environment.ProcessId, parentStartedUtc = parent.StartTime.ToUniversalTime(),
            active = false, streamCreated = output != null, closed, state = writer?.State.ToString(), inputComplete, finalOutput,
            seconds = clock.Elapsed.TotalSeconds, cpuMilliseconds = parent.TotalProcessorTime.TotalMilliseconds - initialCpu,
            nativeMuxOrEncodeChildren = 0, inputBytesRehashed = inputComplete ? index.Bytes : (long?)null,
            error = failure?.ToString(), closeError, observationError,
            meaning = "Managed writer owns its stream; completed state requires metadata/flush, closed requires disposal, inputComplete requires final source hash. Final native allocation is forced before output validation." };
        Write(receiptPath, report);
        if (failure != null) throw new IOException("Managed packet mux failed; writer receipt retained.", failure);
        Require(closed && inputComplete && writer?.State == ReplayMp4WriterState.Completed, "Managed packet mux has no successful closed producer receipt.");
        return report;
    }
    static void Quality(byte[] sourceBytes, byte[] baselineBytes, byte[] actualBytes, Action guard, AudioQuality report)
    {
        report.SourceSamples = sourceBytes.Length / 8; report.BaselineSamples = baselineBytes.Length / 8; report.ActualSamples = actualBytes.Length / 8;
        report.EndpointPassed = Math.Abs(report.ActualSamples - 240000) <= 800 && actualBytes.Length % 8 == 0;
        if (actualBytes.Length % 8 != 0) { report.Error = "Unaligned decoded PCM."; return; }
        float[] Floats(byte[] b) { var f = new float[b.Length / 4]; Buffer.BlockCopy(b, 0, f, 0, b.Length); return f; }
        var source = Floats(sourceBytes); var baseline = Floats(baselineBytes); var actual = Floats(actualBytes); int samples = Math.Min(240000, actual.Length / 2);
        report.Finite = source.All(float.IsFinite) && baseline.All(float.IsFinite) && actual.All(float.IsFinite);
        if (!report.Finite || samples < 4128) { report.Error = "Nonfinite or insufficient decoded PCM."; return; }
        bool passed = report.EndpointPassed;
        for (int ch = 0; ch < 2; ch++)
        {
            double signal = 0, serialError = 0, copiedError = 0;
            for (int n = 0; n < samples; n++) { if ((n & 4095) == 0) guard(); double x = source[n * 2 + ch], a = baseline[n * 2 + ch], b = actual[n * 2 + ch]; signal += x * x; serialError += (x - a) * (x - a); copiedError += (x - b) * (x - b); }
            bool channelPassed = signal > 0 && copiedError <= serialError * Math.Pow(10, .5 / 10);
            var windows = new List<object>();
            foreach (int first in new[] { 16, samples / 2 - 2048, samples - 4112 })
            {
                double? best = null; int? lag = null;
                for (int offset = -8; offset <= 8; offset++)
                {
                    guard(); double xy = 0, xx = 0, yy = 0;
                    for (int n = first; n < first + 4096; n++) { double x = source[n * 2 + ch], y = actual[(n + offset) * 2 + ch]; xy += x * y; xx += x * x; yy += y * y; }
                    double correlation = xy / Math.Sqrt(xx * yy); if (double.IsFinite(correlation) && (!best.HasValue || correlation > best.Value)) { best = correlation; lag = offset; }
                }
                bool windowPassed = best.HasValue && best.Value >= .97 && lag.HasValue && Math.Abs(lag.Value) <= 4;
                channelPassed &= windowPassed; windows.Add(new { first, correlation = best, lag, passed = windowPassed });
            }
            passed &= channelPassed;
            report.Channels.Add(new { ch, signal, serialError, copiedError,
                baselineSnr = signal <= 0 || serialError == 0 ? (double?)null : 10 * Math.Log10(signal / serialError),
                copySnr = signal <= 0 || copiedError == 0 ? (double?)null : 10 * Math.Log10(signal / copiedError), exactPcm = copiedError == 0, windows, passed = channelPassed });
        }
        report.Passed = passed;
    }
}
