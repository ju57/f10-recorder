using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace F10Recorder;

// Offline hypothesis comparisons only. No publication or production model edits.
internal static class ReplayLatencyPilots
{
    const long Cap = 16_000_000_000, Stop = 15_000_000_000, Floor = 60_000_000_000;
    static readonly JsonSerializerOptions Json = new(WorkerProtocol.Json) { WriteIndented = true };
    sealed record Trial(string Label, string Kind, string[] Arguments, string Output);
    static void Require(bool ok, string reason) { if (!ok) throw new IOException(reason); }
    static string Hash(string path) { using var f = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(f)); }
    static void Write(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, Json));
    static string[] Decoder(string[] original, string mode, bool complex)
    {
        var args = original.ToList(); int input = args.IndexOf("-i"); Require(input >= 0, "Input required.");
        args.InsertRange(input, mode == "cuda" ? new[] { "-hwaccel", "cuda" } : new[] { "-threads:v", mode });
        args.InsertRange(0, [complex ? "-filter_complex_threads" : "-filter_threads", "1"]);
        return args.ToArray();
    }
    internal static int Prepare(string planFile, string root, string baseline, string engineRoot, string previousRoot = null)
    {
        Require(!Directory.Exists(root) && !File.Exists(planFile), "Fresh pilot plan/output required.");
        using var closure = JsonDocument.Parse(File.ReadAllText(Path.Combine(baseline, "closure.json")));
        using var admitted = JsonDocument.Parse(File.ReadAllText(Path.Combine(baseline, "admitted-plan.json")));
        var c = closure.RootElement;
        Require(c.GetProperty("error").ValueKind == JsonValueKind.Null && c.GetProperty("workerJoined").GetBoolean()
            && c.GetProperty("captureAbsent").GetBoolean() && c.GetProperty("workerExit").GetInt32() == 0,
            "Complete successful baseline closure required before offline work.");
        using var sample = JsonDocument.Parse(File.ReadAllText(Path.Combine(baseline, "first-use.json"))); var s = sample.RootElement;
        string movie = s.GetProperty("final").GetString();
        var ack = s.GetProperty("ack");
        var interval = new ReplayInterval(ack.GetProperty("ReplayRequestStart").GetDouble(), ack.GetProperty("ReplayRequestEnd").GetDouble(), ack.GetProperty("ReplayRequestedSeconds").GetDouble());
        Require(Math.Abs(interval.Seconds - 300) <= .000001, "Matched five-minute interval required.");
        MediaDependency.RootDirectory = engineRoot; string engine = MediaDependency.ResolveExecutable();
        string[] originalDecode = s.GetProperty("validationArguments").EnumerateArray().Select(x => x.GetString()).ToArray();
        originalDecode[Array.IndexOf(originalDecode, "-i") + 1] = movie;
        Require(originalDecode.SequenceEqual(ReplayExactExport.DecodeArguments(movie, true, 60)), "Original strict validator changed.");
        string[] originalExport = s.GetProperty("arguments").EnumerateArray().Select(x => x.GetString()).ToArray();
        string snapshot = Path.GetDirectoryName(originalExport[Array.IndexOf(originalExport, "-i") + 1]);
        var trials = new List<Trial> { new("D0-original", "decode", originalDecode, null),
            new("D1-decoder1-filter1", "decode", Decoder(originalDecode, "1", true), null),
            new("D4-decoder4-filter1", "decode", Decoder(originalDecode, "4", true), null) };
        foreach (string mode in new[] { "1", "cuda" })
        {
            if (mode == "cuda") trials.Add(new("DG-cuda-filter1", "decode", Decoder(originalDecode, "cuda", true), null));
            string label = mode == "1" ? "E1-decoder1-filter1" : "EG-cuda-filter1";
            string output = Path.Combine(root, label + ".mp4"); var arguments = Decoder(originalExport, mode, false); arguments[^1] = output;
            trials.Add(new(label, "encode", arguments, output));
            trials.Add(new(label + "-validate", "decode", ReplayExactExport.DecodeArguments(output, true, 60).ToArray(), output));
        }
        int priorHelpers = admitted.RootElement.GetProperty("cumulativeNativeHelpersIncludingPreparation").GetInt32();
        if (previousRoot != null)
        {
            using var previous = JsonDocument.Parse(File.ReadAllText(Path.Combine(previousRoot, "admitted-plan.json")));
            using var failed = JsonDocument.Parse(File.ReadAllText(Path.Combine(previousRoot, "failure.json")));
            var f = failed.RootElement;
            Require(f.GetProperty("active").GetProperty("Label").GetString() == "D1-decoder1-filter1"
                && f.GetProperty("process").GetProperty("TimedOut").GetBoolean()
                && f.GetProperty("process").GetProperty("ProducerAbsent").GetBoolean()
                && f.GetProperty("process").GetProperty("ActualExitObserved").GetBoolean()
                && f.GetProperty("children").GetInt32() == 3, "Only the closed failed D1 cohort is this follow-up's predecessor.");
            priorHelpers = previous.RootElement.GetProperty("cumulativeNativeHelpers").GetInt32()
                - previous.RootElement.GetProperty("maximumChildren").GetInt32() + f.GetProperty("children").GetInt32();
            trials.Clear(); trials.Add(new("D4-decoder4-filter1", "decode", Decoder(originalDecode, "4", true), null));
            foreach (string mode in new[] { "original", "4", "cuda" })
            {
                if (mode == "cuda") trials.Add(new("DG-cuda-filter1", "decode", Decoder(originalDecode, "cuda", true), null));
                string label = mode == "original" ? "E0-original" : mode == "4" ? "E4-decoder4-filter1" : "EG-cuda-filter1";
                string output = Path.Combine(root, label + ".mp4");
                string[] arguments = mode == "original" ? originalExport.ToArray() : Decoder(originalExport, mode, false); arguments[^1] = output;
                trials.Add(new(label, "encode", arguments, output));
                trials.Add(new(label + "-validate", "decode", ReplayExactExport.DecodeArguments(output, true, 60).ToArray(), output));
            }
        }
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string build = Path.GetDirectoryName(typeof(ReplayLatencyPilots).Assembly.Location);
        foreach (string file in new[] { Environment.ProcessPath, typeof(ReplayLatencyPilots).Assembly.Location, typeof(CaptureSession).Assembly.Location,
            typeof(Settings).Assembly.Location, Path.Combine(build, "F10Recorder.WorkerChecks.runtimeconfig.json"), Path.Combine(build, "F10Recorder.WorkerChecks.deps.json"),
            engine, movie, Path.Combine(baseline, "closure.json"), Path.Combine(baseline, "first-use.json"), Path.Combine(baseline, "admitted-plan.json"), Path.GetFullPath("projects/F10Recorder.WorkerChecks/ReplayLatencyPilots.cs"),
            Path.GetFullPath("tests/replay-latency-pilots.ps1") }) files[Path.GetFullPath(file)] = Hash(file);
        string manifest = planFile + ".inputs.json"; Require(!File.Exists(manifest), "Fresh input manifest required.");
        var sourceFiles = Directory.GetFiles(snapshot); Require(sourceFiles.Length <= 307, "Snapshot file ceiling.");
        Write(manifest, sourceFiles.Select(f => new { path = Path.GetFullPath(f), bytes = new FileInfo(f).Length, sha256 = Hash(f) }).ToArray());
        files[Path.GetFullPath(manifest)] = Hash(manifest);
        if (previousRoot != null)
            foreach (string name in new[] { "admitted-plan.json", "failure.json", "D0-original.json", "D1-decoder1-filter1.json" })
            { string file = Path.GetFullPath(Path.Combine(previousRoot, name)); files[file] = Hash(file); }
        long retained = checked(Directory.GetFiles(baseline, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length)
            + admitted.RootElement.GetProperty("retainedBytes").GetInt64()
            + (previousRoot == null ? 0 : Directory.GetFiles(previousRoot, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length)));
        long forward = previousRoot == null ? 2_500_000_000 : 4_000_000_000;
        Require(retained + forward + 1_000_000_000 < Stop, "Retained evidence plus complete outputs/closure cannot fit.");
        Directory.CreateDirectory(Path.GetDirectoryName(planFile));
        Write(planFile, new { kind = "issue83-finite-pilot-v1", root, baseline, engineRoot, files, manifest, snapshot, interval, trials,
            maximumChildren = 9, deadlineSeconds = 600, maximumDecoderSeconds = 45, maximumEncoderSeconds = 90, previousRoot,
            phaseSeconds = new { preflightAndSetup = previousRoot == null ? 100 : 55,
                decoders = trials.Count(t => t.Kind == "decode") * 45, encoders = trials.Count(t => t.Kind == "encode") * 90, finalHashAndReceipts = 30, ownedCleanup = 20 },
            cap = Cap, stop = Stop, floor = Floor,
            cumulativeNativeHelpers = priorHelpers + 1 + 9,
            preparationHelpers = 1, acquisitions = 0, priorAcquisitionCeiling = admitted.RootElement.GetProperty("cumulativeAcquisitions").GetInt32(),
            retainedStorageScope = "Successful baseline recursively sized plus unchanged prior failed-root allocation charged by baseline admission; consumed snapshot/movie identities hash-bound separately.",
            retainedLogicalBytes = retained, forwardBytes = forward, closureReserveBytes = 1_000_000_000,
            createdUtc = DateTime.UtcNow,
            claim = "Finite offline stage hypotheses, matched original source/output; no publication, live throughput/percentiles, cold cache or resource qualification. A failed trial stops the remaining ordered set.",
            effects = "One helper, " + trials.Count(t => t.Kind == "encode") + " NVENC exports and " + trials.Count(t => t.Kind == "decode")
                + " complete video/audio decodes to null. No capture, fixture, audio playback, worker/UI, startup/preferences, account catalog activation or installation. CUDA downloads CPU frames for unchanged select/showinfo graph." });
        Console.WriteLine(planFile + " " + Hash(planFile)); return 0;
    }
    internal static int Run(string planFile, string sha)
    {
        var total = Stopwatch.StartNew();
        Require(Hash(planFile) == sha && new FileInfo(planFile).Length <= 131072, "Reviewed plan changed.");
        using var document = JsonDocument.Parse(File.ReadAllText(planFile)); var p = document.RootElement;
        Require(p.GetProperty("kind").GetString() == "issue83-finite-pilot-v1" && p.GetProperty("maximumChildren").GetInt32() == 9
            && p.GetProperty("deadlineSeconds").GetInt32() == 600, "Pilot boundary changed.");
        foreach (var file in p.GetProperty("files").EnumerateObject()) Require(Hash(file.Name) == file.Value.GetString(), "Bound pilot input changed: " + file.Name);
        void VerifySource()
        {
            string manifest = p.GetProperty("manifest").GetString(); Require(new FileInfo(manifest).Length <= 262144, "Source manifest cap.");
            using var inputs = JsonDocument.Parse(File.ReadAllText(manifest)); Require(inputs.RootElement.GetArrayLength() <= 307, "Source file count cap.");
            foreach (var file in inputs.RootElement.EnumerateArray())
            {
                Require(total.Elapsed.TotalSeconds < 580, "Pilot hashing/closure deadline.");
                string path = file.GetProperty("path").GetString();
                Require(new FileInfo(path).Length == file.GetProperty("bytes").GetInt64() && Hash(path) == file.GetProperty("sha256").GetString(), "Protected source changed.");
            }
        }
        VerifySource();
        string root = p.GetProperty("root").GetString(), snapshot = p.GetProperty("snapshot").GetString();
        Require(!Directory.Exists(root) && DateTime.UtcNow - p.GetProperty("createdUtc").GetDateTime() < TimeSpan.FromHours(4), "Fresh/current pilot required.");
        Directory.CreateDirectory(root); File.Copy(planFile, Path.Combine(root, "admitted-plan.json"));
        MediaDependency.RootDirectory = p.GetProperty("engineRoot").GetString();
        var interval = JsonSerializer.Deserialize<ReplayInterval>(p.GetProperty("interval"), Json);
        var trials = JsonSerializer.Deserialize<Trial[]>(p.GetProperty("trials"), Json);
        Require(trials.Length == 8 && trials.Count(t => t.Kind == "encode") is 2 or 3, "Fixed eight-stage comparison required.");
        var results = new List<object>(); int children = 1;
        double lastScan = -1; long retained = p.GetProperty("retainedLogicalBytes").GetInt64();
        Process probe = null; long peakRam = 0; ReplayExportProcessReceipt current = null; Trial active = null;
        using var job = new Native.ProcessJob();
        void Guard()
        {
            Require(total.Elapsed.TotalSeconds < 580, "Pilot deadline;20s closure reserve.");
            if (probe != null) try { probe.Refresh(); peakRam = Math.Max(peakRam, probe.WorkingSet64); } catch (InvalidOperationException) { }
            if (total.Elapsed.TotalSeconds - lastScan >= .2)
            {
                lastScan = total.Elapsed.TotalSeconds;
                long currentBytes = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
                Require(currentBytes + retained < Stop && ReplayStorage.ObserveCapacity(root).AvailableBytes >= Floor, "Pilot storage bound/floor.");
            }
        }
        try
        {
            Require(total.Elapsed.TotalSeconds <= p.GetProperty("phaseSeconds").GetProperty("preflightAndSetup").GetInt32(), "Preflight/setup allowance exhausted before any pilot child.");
            for (int index = 0; index < trials.Length; index++)
            {
                var trial = trials[index];
                int remaining = trials.Skip(index).Sum(t => t.Kind == "encode" ? 90 : 45);
                Require(total.Elapsed.TotalSeconds + remaining + 30 <= 580, "Complete remaining finite pilot schedule no longer fits; no next launch.");
                active = trial; Guard(); Require(++children <= 9, "Pilot child ceiling before launch."); peakRam = 0;
                Write(Path.Combine(root, trial.Label + ".arguments.json"), trial.Arguments);
                current = ReplayExportProcess.Run(MediaDependency.ExecutablePath, trial.Arguments, trial.Kind == "encode" ? snapshot : root, job,
                    started: process => { probe = Process.GetProcessById(process.Id); _ = probe.SafeHandle; },
                    timeoutMilliseconds: trial.Kind == "encode" ? 90000 : 45000, checkStorage: Guard);
                object costs = null;
                if (probe != null)
                {
                    Require(GetProcessIoCounters(probe.SafeHandle.DangerousGetHandle(), out var io), "Process I/O counters failed: " + Marshal.GetLastWin32Error());
                    costs = new { cpuSeconds = probe.TotalProcessorTime.TotalSeconds, sampledPeakWorkingSet = peakRam, io };
                    probe.Dispose(); probe = null;
                }
                ReplayValidation validation = null; string validationError = null;
                if (trial.Kind == "decode")
                    try { validation = ReplayExactExport.ReadValidation(current, interval, 60); }
                    catch (IOException e) { validationError = e.Message; }
                var result = new { trial.Label, trial.Kind, process = current, costs, validation, validationError,
                    outputBytes = trial.Output != null && File.Exists(trial.Output) ? new FileInfo(trial.Output).Length : (long?)null };
                results.Add(result); Write(Path.Combine(root, trial.Label + ".json"), result);
                Require(current.Success && validationError == null, "Pilot stage failed; preserve evidence and do not run remaining trials: " + trial.Label);
            }
            VerifySource(); foreach (var f in p.GetProperty("files").EnumerateObject()) Require(Hash(f.Name) == f.Value.GetString(), "Protected input changed.");
            Write(Path.Combine(root, "result.json"), new { result = "finite-pilots-complete", results, children, elapsedSeconds = total.Elapsed.TotalSeconds,
                publication = false, capture = false, gpuCosts = "Unmeasured; no per-process GPU attribution in this pilot.",
                interpretation = "Ordered n=1 stage comparisons only; no whole-save or percentile acceptance." }); return 0;
        }
        catch (Exception e) { Write(Path.Combine(root, "failure.json"), new { error = e.ToString(), active, process = current, children, results }); Console.Error.WriteLine(e); return 1; }
        finally { probe?.Dispose(); }
    }
    [StructLayout(LayoutKind.Sequential)] struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);
}
