using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;

namespace F10Recorder;

internal static class NativeApiPilot
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    static void Require(bool ok, string why) { if (!ok) throw new IOException(why); }
    static string Hash(string path) { using var f = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(f)); }
    static void Write(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, Json));
    internal static int PrepareOpen(string planFile, string root, string runtime, string previous)
    {
        Require(!File.Exists(planFile) && !Directory.Exists(root), "Fresh OpenH264 plan/root required.");
        using var prior = JsonDocument.Parse(File.ReadAllText(Path.Combine(previous, "admitted-plan.json")));
        using var done = JsonDocument.Parse(File.ReadAllText(Path.Combine(previous, "result.json")));
        Require(done.RootElement.GetProperty("result").GetString() == "slice-counterfactual-complete" && done.RootElement.GetProperty("processes").GetInt32() == 11, "Completed slice predecessor required.");
        var p = prior.RootElement; string libraries = Path.Combine(p.GetProperty("runtime").GetString(), "lib"), host = Path.Combine(runtime, "host"), codec = Path.Combine(runtime, "lib", "openh264-2.6.0-win64.dll");
        Require(Hash(codec) == "2076CB5675EC6C1A4C70E7A2A322552F547B6EEED649D6DFCD9E02A543B24691", "Publisher-authenticated OpenH264 bytes changed.");
        var runtimeFiles = Directory.GetFiles(host).Concat(Directory.GetFiles(libraries, "*.dll")).Append(codec).ToDictionary(Path.GetFullPath, Hash);
        foreach (string file in runtimeFiles.Keys) TrustedStorage.ValidateFile(file);
        var files = new Dictionary<string, string>(runtimeFiles); string build = Path.GetDirectoryName(typeof(NativeApiPilot).Assembly.Location);
        foreach (string file in new[] { Environment.ProcessPath, typeof(NativeApiPilot).Assembly.Location, typeof(CaptureSession).Assembly.Location,
            typeof(Settings).Assembly.Location, Path.Combine(build, "F10Recorder.WorkerChecks.deps.json"), Path.Combine(build, "F10Recorder.WorkerChecks.runtimeconfig.json"),
            Path.GetFullPath("projects/F10Recorder.WorkerChecks/NativeApiPilot.cs"), Path.GetFullPath("tests/NativeReplayPrototype/Program.cs"),
            Path.GetFullPath("tests/native-api-pilot.ps1"), Path.GetFullPath("artifacts/issue83/openh264/private-staging-01.json"),
            Path.GetFullPath("artifacts/issue83/openh264/staging-01/download.json"), Path.GetFullPath("artifacts/issue83/openh264/staging-01/extraction.json"),
            Path.GetFullPath("artifacts/issue83/openh264/staging-01/authenticode.json"), Path.GetFullPath("artifacts/issue83/openh264/staging-01/BINARY_LICENSE.txt"),
            Path.Combine(previous, "admitted-plan.json"), Path.Combine(previous, "result.json"), p.GetProperty("movie").GetString() }) files[Path.GetFullPath(file)] = Hash(file);
        long dependencies = Directory.GetFiles(Path.GetFullPath("artifacts/issue83/native-api"), "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        long openBytes = Directory.GetFiles(Path.GetFullPath("artifacts/issue83/openh264"), "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        Require(dependencies < 1_500_000_000 && openBytes < 20_000_000, "Native test dependency caps.");
        long retained = p.GetProperty("retainedLogicalBytes").GetInt64() + Directory.GetFiles(previous, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length)
            + dependencies - p.GetProperty("dependencyBytes").GetInt64() + openBytes;
        Require(retained + 1_000_000_000 < 15_000_000_000, "Entire OpenH264 cohort/closure cannot fit.");
        Write(planFile, new { kind = "native-full-decode-pilot-v1", root, runtime, libraryRoot = libraries, host, openH264Path = codec,
            movie = p.GetProperty("movie").GetString(), files, runtimeFiles, modes = new[] { 16, 32 }, decoderThreads = 1, modeDeadlines = new[] { 45, 45 },
            deadlineSeconds = 150, maximumChildren = 3, preparationHelpers = 1, additionalMetadataHelpers = 1,
            cumulativeNativeHelpers = p.GetProperty("cumulativeNativeHelpers").GetInt32() + 1 + 1 + 3,
            phaseSeconds = new { setup = 30, twoDecoders = 90, closure = 15, cleanup = 15 },
            capBytes = 16_000_000_000, stopBytes = 15_000_000_000, floorBytes = 60_000_000_000, maximumChildRamBytes = 3_000_000_000,
            retainedLogicalBytes = retained, dependencyBytes = dependencies, openDependencyBytes = openBytes, createdUtc = DateTime.UtcNow,
            claim = "Alternative complete closed-output software decoder counterfactual. Same frozen MP4, strict error/concealment settings, actual I420 reconstruction, full PTS/count/GOP drain and full FFmpeg AAC decode. No corruption-equivalence/source-content/live/percentile acceptance or product adoption.",
            effects = "One coordinator and two serial read-only full-decode helpers,16/32single-thread OpenH264 contexts; actual packet AVCC→AnnexB conversion included. Cisco Authenticode-verified DLL under private test root; existing pinned FFmpeg libraries for demux/audio. No GPU/capture/playback/worker/UI/output media/publication/settings/startup/NVIDIA/catalog/install." });
        Console.WriteLine(planFile + " " + Hash(planFile)); return 0;
    }
    internal static int Prepare(string planFile, string root, string runtime, string previous)
    {
        Require(!File.Exists(planFile) && !Directory.Exists(root), "Fresh native API plan/root required.");
        using var prior = JsonDocument.Parse(File.ReadAllText(Path.Combine(previous, "admitted-plan.json")));
        using var complete = JsonDocument.Parse(File.ReadAllText(Path.Combine(previous, "result.json")));
        bool scaling = complete.RootElement.GetProperty("result").GetString() == "native-api-pilot-complete";
        bool gpu = scaling && prior.RootElement.GetProperty("modes").EnumerateArray().Select(x => x.GetInt32()).SequenceEqual(new[] { 32, 48 });
        Require(scaling || complete.RootElement.GetProperty("result").GetString() == "parallel-full-pilot-complete", "Complete previous cohort required.");
        if (scaling) Require(gpu || prior.RootElement.GetProperty("modes").EnumerateArray().Select(x => x.GetInt32()).SequenceEqual(new[] { 1, 4, 8, 16 }), "Only declared scaling/GPU follow-ups.");
        string movie = prior.RootElement.GetProperty("movie").GetString(), host = Path.Combine(runtime, "host"), libraries = Path.Combine(runtime, "lib");
        var runtimeFiles = Directory.GetFiles(host).Concat(Directory.GetFiles(libraries, "*.dll")).ToDictionary(Path.GetFullPath, Hash);
        foreach (string path in runtimeFiles.Keys) TrustedStorage.ValidateFile(path);
        var files = new Dictionary<string, string>(runtimeFiles);
        if (gpu) foreach (string name in new[] { "nvcuda.dll", "nvcuvid.dll" })
        { string path = Path.Combine(Environment.SystemDirectory, name); files[path] = Hash(path); }
        string build = Path.GetDirectoryName(typeof(NativeApiPilot).Assembly.Location);
        foreach (string f in new[] { Environment.ProcessPath, typeof(NativeApiPilot).Assembly.Location, typeof(CaptureSession).Assembly.Location,
            typeof(Settings).Assembly.Location, Path.Combine(build, "F10Recorder.WorkerChecks.deps.json"), Path.Combine(build, "F10Recorder.WorkerChecks.runtimeconfig.json"),
            movie, Path.Combine(previous, "result.json"), Path.Combine(previous, "admitted-plan.json"),
            Path.GetFullPath("tests/NativeReplayPrototype/Program.cs"), Path.GetFullPath("tests/NativeReplayPrototype/NativeReplayPrototype.csproj"),
            Path.GetFullPath("tests/NativeReplayPrototype/packages.lock.json"), Path.GetFullPath("projects/F10Recorder.WorkerChecks/NativeApiPilot.cs"),
            Path.GetFullPath("tests/native-api-pilot.ps1"), Path.GetFullPath("artifacts/issue83/native-api/private-staging-" + (gpu ? "03" : scaling ? "02" : "01") + ".json") }) files[Path.GetFullPath(f)] = Hash(f);
        string dependencyRoot = Path.GetFullPath("artifacts/issue83/native-api");
        long dependencyBytes = Directory.GetFiles(dependencyRoot, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        Require(dependencyBytes <= 1_500_000_000, "Test dependency staging allowance.");
        long retained = checked(prior.RootElement.GetProperty("retainedLogicalBytes").GetInt64()
            + Directory.GetFiles(previous, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) + dependencyBytes
            - (scaling ? prior.RootElement.GetProperty("dependencyBytes").GetInt64() : 0));
        Require(retained + 1_500_000_000 < 15_000_000_000, "Complete native API reports/closure cannot fit.");
        Write(planFile, new { kind = "native-full-decode-pilot-v1", root, runtime, libraryRoot = libraries, host, movie, files, runtimeFiles,
            modes = gpu ? new[] { 0 } : scaling ? new[] { 32, 48 } : new[] { 1, 4, 8, 16 }, modeDeadlines = gpu ? new[] { 45 } : scaling ? new[] { 45, 45 } : new[] { 120, 60, 45, 45 }, deadlineSeconds = scaling ? 150 : 360,
            phaseSeconds = scaling ? new { setup = 30, fourHelpers = gpu ? 45 : 90, closure = 15, cleanup = 15 } : new { setup = 30, fourHelpers = 270, closure = 30, cleanup = 15 },
            maximumChildren = gpu ? 2 : scaling ? 3 : 5, preparationHelpers = 1, cumulativeNativeHelpers = prior.RootElement.GetProperty("cumulativeNativeHelpers").GetInt32() + 1 + (gpu ? 2 : scaling ? 3 : 5),
            capBytes = 16_000_000_000, stopBytes = 15_000_000_000, floorBytes = 60_000_000_000, maximumChildRamBytes = 3_000_000_000,
            retainedLogicalBytes = retained, dependencyBytes, maximumGopPackets = 120, maximumGopBytes = 16_000_000, queueCapacityPerWorker = 2,
            archiveSha256 = "4D2060A8B34A940AA47D785142055BB92A63053781E55F2ACE4546EDD519A8F5", bindingVersion = "FFmpeg.AutoGen9.0.1.1/ABI63,63,61", createdUtc = DateTime.UtcNow,
            maximumGPUContexts = gpu ? 1 : 0, gpuFramesRemainOnDevice = gpu, gpuVramMeasured = false,
            claim = "Exploratory full CLOSED-output decode with native APIs. No publication, capture, product adoption, percentile or equivalent-fault-sensitivity acceptance.",
            effects = "One job-owning coordinator and " + (gpu ? "one" : scaling ? "two" : "four") + " serial read-only native-codec helper processes. Each reads the full existing MP4; bounded independent-IDR video queues and complete AAC decode. "
                + (gpu ? "One CUDA/NVDEC device context, no CPU pixel download; all frames mapped by decoder, GOP drains plus CUDA synchronization and context teardown timed. GPU VRAM is not sampled; no live-capture resource or corruption equivalence claim. " : "Software video decoding, no GPU. ")
                + "Parent measures runtime authentication/library load/demux/decode/drain/exit; fixture SHA checked outside individual timing and again after all modes. No capture, playback, window/worker, account model, installed/startup/NVIDIA preferences or installation." });
        Console.WriteLine(planFile + " " + Hash(planFile)); return 0;
    }
    internal static int Run(string planFile, string expected)
    {
        var total = Stopwatch.StartNew(); Require(Hash(planFile) == expected && new FileInfo(planFile).Length < 65536, "Reviewed native plan changed.");
        using var doc = JsonDocument.Parse(File.ReadAllText(planFile)); var p = doc.RootElement;
        bool continuous = p.TryGetProperty("continuousInput", out var continuousValue) && continuousValue.GetBoolean();
        bool sourceOnly = p.TryGetProperty("sourceOnly", out var sourceOnlyValue) && sourceOnlyValue.GetBoolean();
        bool openBackend = p.TryGetProperty("openH264Path", out _);
        int maximumChildren = p.GetProperty("maximumChildren").GetInt32(), deadline = p.GetProperty("deadlineSeconds").GetInt32();
        Require(p.GetProperty("kind").GetString() == "native-full-decode-pilot-v1" && (maximumChildren == 5 && deadline == 360 || maximumChildren is 2 or 3 && deadline == 150 || continuous && maximumChildren == 3 && deadline == 180), "Native API admission changed.");
        foreach (var f in p.GetProperty("files").EnumerateObject()) Require(Hash(f.Name) == f.Value.GetString(), "Bound native experiment input changed:" + f.Name);
        using var inputs = continuous ? ReplayContinuousInputPilot.OpenInputs(p) : null;
        string root = p.GetProperty("root").GetString(), host = p.GetProperty("host").GetString();
        Require(!Directory.Exists(root) && DateTime.UtcNow - p.GetProperty("createdUtc").GetDateTime() < TimeSpan.FromHours(4), "Fresh/current native cohort required.");
        Directory.CreateDirectory(root); File.Copy(planFile, Path.Combine(root, "admitted-plan.json"));
        using var job = new Native.ProcessJob(); var results = new List<object>(); int children = 1;
        int[] modes = p.GetProperty("modes").EnumerateArray().Select(x => x.GetInt32()).ToArray(), deadlines = p.GetProperty("modeDeadlines").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        int decoderThreads = p.TryGetProperty("decoderThreads", out var threadsValue) ? threadsValue.GetInt32() : 1;
        bool sourceMode = sourceOnly && modes.Length == 1 && deadlines.SequenceEqual(new[] { 45 })
            && (modes[0] == 32 && decoderThreads == 1 || modes[0] == 4 && decoderThreads == 8 || modes[0] == 8 && decoderThreads == 4);
        Require(openBackend ? maximumChildren == 3 && modes.SequenceEqual(new[] { 16, 32 }) && deadlines.SequenceEqual(new[] { 45, 45 }) && decoderThreads == 1
            : continuous ? modes.SequenceEqual(new[] { 32 }) && deadlines.SequenceEqual(new[] { 45 }) : sourceOnly ? sourceMode : maximumChildren == 5 ? modes.SequenceEqual(new[] { 1, 4, 8, 16 }) && deadlines.SequenceEqual(new[] { 120, 60, 45, 45 })
            : maximumChildren == 3 ? modes.SequenceEqual(new[] { 32, 48 }) && deadlines.SequenceEqual(new[] { 45, 45 })
            : modes.SequenceEqual(new[] { 0 }) && deadlines.SequenceEqual(new[] { 45 }), "Frozen mode order/deadlines changed.");
        Require(total.Elapsed.TotalSeconds < 30, "Native preflight allowance exhausted.");
        try
        {
            if (continuous)
            {
                var stage = ReplayContinuousInputPilot.Run(p, inputs, root, job,
                    () => Require(++children <= maximumChildren, "Continuous mux child ceiling before launch."));
                results.Add(stage); Write(Path.Combine(root, "continuous-stage.json"), stage);
            }
            for (int index = 0; index < modes.Length; index++)
            {
                Require(total.Elapsed.TotalSeconds + deadlines.Skip(index).Sum() + 30 < deadline - 15, "Remaining complete native cohort no longer fits.");
                int mode = modes[index]; string report = Path.Combine(root, "native-" + mode + ".json");
                var elapsed = Stopwatch.StartNew();
                using var child = new Process { StartInfo = new ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = false, CreateNoWindow = true,
                    WorkingDirectory = host, RedirectStandardOutput = true, RedirectStandardError = true } };
                foreach (string a in new[] { Path.Combine(host, "NativeReplayPrototype.dll"), "--decode", Path.GetFullPath(planFile), expected, mode.ToString(), report }) child.StartInfo.ArgumentList.Add(a);
                bool launched = false; int? pid = null; DateTime? started = null;
                Task<string> stdout = null, stderr = null; int capturedBytes = 0;
                using var drainCancellation = new CancellationTokenSource();
                Task<string> Drain(Stream stream) => Task.Run(async () =>
                {
                    using var text = new MemoryStream(); var buffer = new byte[4096];
                    while (true)
                    {
                        int count = await stream.ReadAsync(buffer, drainCancellation.Token).ConfigureAwait(false); if (count == 0) break;
                        Require(Interlocked.Add(ref capturedBytes, count) <= 131072, "Live native diagnostic byte cap."); text.Write(buffer, 0, count);
                    }
                    return Encoding.UTF8.GetString(text.ToArray());
                });
                long peakRam = 0; string failure = null;
                try
                {
                    Require(++children <= maximumChildren, "Native child ceiling before launch.");
                    Require(child.Start(), "Native helper launch failed."); launched = true;
                    pid = child.Id; started = child.StartTime.ToUniversalTime();
                    // Startup, job assignment and stream setup share the same
                    // owned cleanup scope as decoding, including setup failures.
                    job.Add(child); stdout = Drain(child.StandardOutput.BaseStream); stderr = Drain(child.StandardError.BaseStream);
                    while (!child.WaitForExit(50))
                    {
                        Require(!stdout.IsFaulted && !stderr.IsFaulted, "Native diagnostics failed or exceeded their live cap.");
                        Require(total.Elapsed.TotalSeconds < deadline - 15 && elapsed.Elapsed.TotalSeconds < deadlines[index] - 5, "Native helper deadline;5s owned cleanup reserved.");
                        Require(GetProcessMemoryInfo(child.SafeHandle.DangerousGetHandle(), out var memory, (uint)Marshal.SizeOf<MemoryCounters>()) || child.HasExited, "Live native RAM query failed.");
                        peakRam = Math.Max(peakRam, (long)memory.PeakWorkingSet); Require(peakRam <= 3_000_000_000, "Native helper RAM cap.");
                        Require(new DriveInfo(Path.GetPathRoot(root)).AvailableFreeSpace >= 60_000_000_000
                            && new DriveInfo(Path.GetPathRoot(host)).AvailableFreeSpace >= 60_000_000_000, "Native C/D free-space floor.");
                        long bytes = Directory.GetFiles(root).Sum(f => new FileInfo(f).Length);
                        Require(bytes <= (continuous ? 2_500_000_000 : 500_000_000) && bytes + p.GetProperty("retainedLogicalBytes").GetInt64() < 15_000_000_000, "Native report storage cap.");
                    }
                    Require(stdout.Wait(1000) && stderr.Wait(1000), "Native diagnostics did not drain.");
                    Require(stdout.Result.Length + stderr.Result.Length <= 131072, "Native diagnostics cap.");
                    Require(child.ExitCode == 0 && File.Exists(report), "Native helper failed; retain this mode and stop later modes.");
                    using var completed = JsonDocument.Parse(File.ReadAllText(report)); Require(completed.RootElement.GetProperty("result").GetString() == "native-full-decode-complete", "Native decode did not complete.");
                }
                catch (Exception e) { failure = e.ToString(); throw; }
                finally
                {
                    if (launched && !child.HasExited) { child.Kill(); Require(child.WaitForExit(5000), "Native helper owned exit unconfirmed."); }
                    drainCancellation.Cancel();
                    try { if (stdout != null) stdout.Wait(1000); } catch (AggregateException) { }
                    try { if (stderr != null) stderr.Wait(1000); } catch (AggregateException) { }
                    var io = new IoCounters(); bool ioMeasured = launched && GetProcessIoCounters(child.SafeHandle.DangerousGetHandle(), out io);
                    var receipt = new { mode, pid, started, exit = launched ? (int?)child.ExitCode : null, producerAbsent = !launched || child.HasExited, elapsedSeconds = elapsed.Elapsed.TotalSeconds,
                        cpuSeconds = launched ? (double?)child.TotalProcessorTime.TotalSeconds : null, sampledPeakRam = peakRam, ioMeasured,
                        readBytes = ioMeasured ? (ulong?)io.ReadBytes : null, writeBytes = ioMeasured ? (ulong?)io.WriteBytes : null, failure,
                        capturedBytes, stdout = stdout?.IsCompletedSuccessfully == true ? stdout.Result : "Not completely drained", stderr = stderr?.IsCompletedSuccessfully == true ? stderr.Result : "Not completely drained" };
                    results.Add(receipt); Write(Path.Combine(root, "native-" + mode + ".process.json"), receipt);
                }
            }
            foreach (var f in p.GetProperty("files").EnumerateObject()) Require(Hash(f.Name) == f.Value.GetString(), "Native cohort protected input changed.");
            inputs?.Verify();
            Write(Path.Combine(root, "result.json"), new { result = "native-api-pilot-complete", results, children, totalSeconds = total.Elapsed.TotalSeconds, publication = false, capture = false }); return 0;
        }
        catch (Exception e) { Write(Path.Combine(root, "failure.json"), new { error = e.ToString(), results, children, totalSeconds = total.Elapsed.TotalSeconds }); Console.Error.WriteLine(e); return 1; }
    }
    [StructLayout(LayoutKind.Sequential)] struct MemoryCounters { public uint Size, PageFaults; public nuint PeakWorkingSet, WorkingSet, PeakPagedPool, PagedPool, PeakNonPagedPool, NonPagedPool, Pagefile, PeakPagefile; }
    [StructLayout(LayoutKind.Sequential)] struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [DllImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo", SetLastError = true)] static extern bool GetProcessMemoryInfo(IntPtr process, out MemoryCounters counters, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);
}
