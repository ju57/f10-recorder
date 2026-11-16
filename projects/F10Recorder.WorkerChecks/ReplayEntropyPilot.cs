using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace F10Recorder;

// Finite OFFLINE counterfactual. No capture recipe or product is changed.
internal static class ReplayEntropyPilot
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    static void Require(bool ok, string why) { if (!ok) throw new IOException(why); }
    static string Hash(string path) { using var f = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(f)); }
    static void Write(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, Json));
    internal static int PrepareSlices(string planFile, string root, string previous, string runtime, string decoderCoordinator)
    {
        Require(!File.Exists(planFile) && !Directory.Exists(root), "Fresh multi-slice experiment required.");
        using var done = JsonDocument.Parse(File.ReadAllText(Path.Combine(previous, "result.json")));
        Require(done.RootElement.GetProperty("result").GetString() == "entropy-counterfactual-complete" && done.RootElement.GetProperty("processes").GetInt32() == 9, "Completed entropy predecessor required.");
        var plan = JsonNode.Parse(File.ReadAllText(Path.Combine(previous, "admitted-plan.json"))).AsObject();
        var runtimeFiles = Directory.GetFiles(Path.Combine(runtime, "host")).Concat(Directory.GetFiles(Path.Combine(runtime, "lib"), "*.dll")).ToDictionary(Path.GetFullPath, Hash);
        foreach (string file in runtimeFiles.Keys) TrustedStorage.ValidateFile(file);
        var files = new Dictionary<string, string>(runtimeFiles);
        string build = Path.GetDirectoryName(typeof(ReplayEntropyPilot).Assembly.Location), decoderBuild = Path.GetDirectoryName(decoderCoordinator);
        string retirement = Path.GetFullPath("artifacts/issue83/plans/retire-duplicate-exports-01.receipt.json");
        using var retired = JsonDocument.Parse(File.ReadAllText(retirement)); var retiredFiles = retired.RootElement.GetProperty("files").EnumerateArray().ToArray();
        Require(retiredFiles.Length == 3 && retiredFiles.All(f => f.GetProperty("confirmedAbsent").GetBoolean() && !File.Exists(f.GetProperty("path").GetString())), "Reviewed duplicate retirement required.");
        long retiredBytes = retiredFiles.Sum(f => f.GetProperty("priorBytes").GetInt64());
        foreach (string path in new[] { Environment.ProcessPath, typeof(ReplayEntropyPilot).Assembly.Location, typeof(CaptureSession).Assembly.Location,
            typeof(Settings).Assembly.Location, Path.Combine(build, "F10Recorder.WorkerChecks.deps.json"), Path.Combine(build, "F10Recorder.WorkerChecks.runtimeconfig.json"),
            decoderCoordinator, Path.Combine(decoderBuild, "F10Recorder.WorkerChecks.deps.json"), Path.Combine(decoderBuild, "F10Recorder.WorkerChecks.runtimeconfig.json"),
            Path.Combine(decoderBuild, "F10Recorder.Windows.dll"), Path.Combine(decoderBuild, "F10Recorder.Core.dll"),
            Path.GetFullPath("projects/F10Recorder.WorkerChecks/ReplayEntropyPilot.cs"), Path.GetFullPath("projects/F10Recorder.WorkerChecks/NativeApiPilot.cs"),
            Path.GetFullPath("tests/NativeReplayPrototype/Program.cs"), Path.GetFullPath("artifacts/issue83/native-api/private-staging-04.json"),
            Path.GetFullPath("tests/replay-entropy-pilot.ps1"), Path.Combine(previous, "admitted-plan.json"), Path.Combine(previous, "result.json"),
            retirement, Path.GetFullPath("artifacts/issue83/plans/retire-duplicate-exports-01.json"),
            Path.GetFullPath("artifacts/issue83/checks/replay-ownership-dispatch-failure-01.process.json"), Path.GetFullPath("artifacts/issue83/checks/replay-ownership.process.json"),
            Path.GetFullPath("artifacts/issue83/checks/replay-output-validation.process.json"), plan["movie"].GetValue<string>(), plan["engine"].GetValue<string>() }) files[Path.GetFullPath(path)] = Hash(path);
        long dependencies = Directory.GetFiles(Path.GetFullPath("artifacts/issue83/native-api"), "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        long retained = plan["retainedLogicalBytes"].GetValue<long>() + Directory.GetFiles(previous, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length)
            + Directory.GetFiles(Path.GetFullPath("artifacts/issue83/checks"), "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length)
            + dependencies - plan["dependencyBytes"].GetValue<long>() - retiredBytes;
        Require(dependencies < 1_500_000_000 && retained + 2_000_000_000 + 500_000_000 < 15_000_000_000, "Entire multi-slice cohort and closure cannot fit.");
        plan["kind"] = "offline-slice-counterfactual-v1"; plan["root"] = root; plan["runtime"] = runtime;
        plan["runtimeFiles"] = JsonSerializer.SerializeToNode(runtimeFiles); plan["files"] = JsonSerializer.SerializeToNode(files); plan["decoderCoordinator"] = Path.GetFullPath(decoderCoordinator);
        plan["order"] = JsonSerializer.SerializeToNode(new[] { "slice1-encode", "slice1-full32x1", "slice8-encode", "slice8-full4x8", "slice8-full8x4", "slice1-ssim", "slice8-ssim" });
        plan["maximumRunProcesses"] = 11; plan["cumulativeNativeHelpers"] = plan["cumulativeNativeHelpers"].GetValue<int>() + 3 + 1 + 11;
        plan["additionalPriorFileOnlyHelpers"] = 3; plan["retiredLogicalBytes"] = retiredBytes;
        plan["maximumSimultaneousChildProcesses"] = 2; plan["maximumSimultaneousNativeProcessesIncludingController"] = 3; plan["maximumCodecThreads"] = 32;
        plan.Remove("maximumSimultaneousNativeProcesses"); plan["deadlineSeconds"] = 540;
        plan["phaseSeconds"] = JsonSerializer.SerializeToNode(new { setup = 30, twoEncodes = 180, threeDecoderTrees = 135, twoQuality = 120, closure = 30, cleanup = 15 });
        plan["retainedLogicalBytes"] = retained; plan["dependencyBytes"] = dependencies; plan["createdUtc"] = JsonSerializer.SerializeToNode(DateTime.UtcNow);
        plan["claim"] = "Matched1/8-slice monolithic-input H264 NVENC reencodes; complete software decode32contexts x1thread control and4x8/8x4 explicit slice threads; paired SSIM. No capture recipe adoption, quality/source-lineage/storage/latency qualification. No unmeasured capture work.";
        plan["effects"] = "Offline2serial NVENC encodes,3serial software full-decoder coordinator+leaf trees,2paired full SSIM passes. Two child processes concurrently (three including controller),<=32codec threads. No capture/playback/worker/UI/publication/catalog/settings/startup/NVIDIA/install change.";
        Write(planFile, plan); Console.WriteLine(planFile + " " + Hash(planFile)); return 0;
    }
    internal static int Prepare(string planFile, string root, string previous, string decoderCoordinator)
    {
        Require(!File.Exists(planFile) && !Directory.Exists(root), "Fresh entropy experiment required.");
        using var prior = JsonDocument.Parse(File.ReadAllText(Path.Combine(previous, "admitted-plan.json")));
        using var done = JsonDocument.Parse(File.ReadAllText(Path.Combine(previous, "result.json")));
        Require(done.RootElement.GetProperty("result").GetString() == "native-api-pilot-complete" && prior.RootElement.GetProperty("sourceOnly").GetBoolean(), "Completed aligned diagnostic decode required.");
        var p = prior.RootElement; string movie = p.GetProperty("movie").GetString(), runtime = p.GetProperty("runtime").GetString();
        var runtimeFiles = p.GetProperty("runtimeFiles").EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetString());
        var files = new Dictionary<string, string>(runtimeFiles);
        string build = Path.GetDirectoryName(typeof(ReplayEntropyPilot).Assembly.Location);
        string decoderBuild = Path.GetDirectoryName(decoderCoordinator);
        MediaDependency.RootDirectory = p.GetProperty("engineRoot").GetString(); string engine = MediaDependency.ResolveExecutable();
        foreach (string path in new[] { Environment.ProcessPath, typeof(ReplayEntropyPilot).Assembly.Location, typeof(CaptureSession).Assembly.Location,
            typeof(Settings).Assembly.Location, Path.Combine(build, "F10Recorder.WorkerChecks.deps.json"), Path.Combine(build, "F10Recorder.WorkerChecks.runtimeconfig.json"),
            decoderCoordinator, Path.Combine(decoderBuild, "F10Recorder.WorkerChecks.deps.json"), Path.Combine(decoderBuild, "F10Recorder.WorkerChecks.runtimeconfig.json"),
            Path.Combine(decoderBuild, "F10Recorder.Windows.dll"), Path.Combine(decoderBuild, "F10Recorder.Core.dll"),
            Path.GetFullPath("projects/F10Recorder.WorkerChecks/ReplayEntropyPilot.cs"), Path.GetFullPath("tests/replay-entropy-pilot.ps1"),
            Path.Combine(previous, "admitted-plan.json"), Path.Combine(previous, "result.json"), Path.Combine(previous, "native-32.json"), movie, engine }) files[Path.GetFullPath(path)] = Hash(path);
        long dependencies = Directory.GetFiles(Path.GetFullPath("artifacts/issue83/native-api"), "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        long retained = p.GetProperty("retainedLogicalBytes").GetInt64() + Directory.GetFiles(previous, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length)
            + dependencies - p.GetProperty("dependencyBytes").GetInt64();
        Require(dependencies < 1_500_000_000 && retained + 2_000_000_000 + 500_000_000 < 15_000_000_000, "Entire entropy cohort and closure cannot fit.");
        Write(planFile, new { kind = "offline-entropy-counterfactual-v1", root, movie, runtime, runtimeFiles, files,
            decoderCoordinator = Path.GetFullPath(decoderCoordinator), engineRoot = p.GetProperty("engineRoot").GetString(), engine,
            order = new[] { "cabac-encode", "cabac-full32", "cavlc-encode", "cavlc-full32", "cabac-ssim", "cavlc-ssim" },
            maximumRunProcesses = 9, cumulativeNativeHelpers = p.GetProperty("cumulativeNativeHelpers").GetInt32() + 1 + 9,
            maximumSimultaneousNativeProcesses = 2, maximumSimultaneousNvencEncoders = 1, acquisitions = 0,
            deadlineSeconds = 480, phaseSeconds = new { setup = 30, twoEncodes = 180, twoDecoderTrees = 90, twoQuality = 120, closure = 30, cleanup = 15 },
            capBytes = 16_000_000_000, stopBytes = 15_000_000_000, floorBytes = 60_000_000_000, maximumChildRamBytes = 3_000_000_000,
            retainedLogicalBytes = retained, dependencyBytes = dependencies, forwardBytes = 2_000_000_000, closureReserveBytes = 500_000_000,
            createdUtc = DateTime.UtcNow,
            claim = "Matched monolithic-input NVENC reencodes differing only coder cabac/cavlc. Measure encode/full-output-decode/SSIM costs and quality tradeoffs. No adoption or source-lineage/quality threshold/capture/storage qualification/latency acceptance claim.",
            effects = "Offline only:2 serial NVENC H2641080p60/20Mbps/GOP60/BF0/p3/ll/CBR transcodes with audio copied,2 serial coordinator+full32 software decoder trees,2 full paired SSIM decodes. All generated outputs isolated and unpublished. No capture/playback/worker/UI/catalog/settings/startup/NVIDIA/install changes." });
        Console.WriteLine(planFile + " " + Hash(planFile)); return 0;
    }
    internal static int Run(string planFile, string expected)
    {
        var total = Stopwatch.StartNew(); Require(new FileInfo(planFile).Length < 32768 && Hash(planFile) == expected, "Reviewed entropy plan required.");
        using var doc = JsonDocument.Parse(File.ReadAllText(planFile)); var p = doc.RootElement;
        bool sliced = p.GetProperty("kind").GetString() == "offline-slice-counterfactual-v1";
        int maximumProcesses = sliced ? 11 : 9, activeDeadline = sliced ? 525 : 465;
        Require((sliced || p.GetProperty("kind").GetString() == "offline-entropy-counterfactual-v1") && p.GetProperty("maximumRunProcesses").GetInt32() == maximumProcesses, "Entropy boundary changed.");
        foreach (var f in p.GetProperty("files").EnumerateObject()) Require(Hash(f.Name) == f.Value.GetString(), "Bound entropy input changed:" + f.Name);
        string root = p.GetProperty("root").GetString(), movie = p.GetProperty("movie").GetString();
        Require(!Directory.Exists(root) && DateTime.UtcNow - p.GetProperty("createdUtc").GetDateTime() < TimeSpan.FromHours(4), "Fresh/current entropy experiment required.");
        Directory.CreateDirectory(root); File.Copy(planFile, Path.Combine(root, "admitted-plan.json"));
        using var source = new FileStream(movie, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var job = new Native.ProcessJob(); var results = new List<object>(); int processes = 1;
        MediaDependency.RootDirectory = p.GetProperty("engineRoot").GetString();
        Require(MediaDependency.ResolveExecutable() == p.GetProperty("engine").GetString(), "Entropy engine changed.");
        Process active = null;
        void Guard()
        {
            Require(total.Elapsed.TotalSeconds < activeDeadline, "Entropy whole-cohort deadline;cleanup reserved.");
            Require(new DriveInfo(Path.GetPathRoot(root)).AvailableFreeSpace >= 60_000_000_000
                && new DriveInfo(Path.GetPathRoot(planFile)).AvailableFreeSpace >= 60_000_000_000, "Entropy C/D free-space floor.");
            long bytes = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
            Require(bytes <= 2_000_000_000 && bytes + p.GetProperty("retainedLogicalBytes").GetInt64() < 15_000_000_000, "Entropy storage stop.");
            if (active != null && !active.HasExited)
                try { active.Refresh(); Require(active.PeakWorkingSet64 <= 3_000_000_000, "Entropy child RAM cap."); }
                catch (InvalidOperationException) when (active.HasExited) { }
        }
        ReplayExportProcessReceipt Media(string label, string[] arguments, int seconds, int remainingSeconds)
        {
            Require(total.Elapsed.TotalSeconds + remainingSeconds + 30 < activeDeadline, "Complete remaining entropy suffix cannot fit.");
            Require(++processes <= maximumProcesses, "Entropy process ceiling before media launch."); Write(Path.Combine(root, label + "-arguments.json"), arguments);
            try
            {
                var receipt = ReplayExportProcess.Run(p.GetProperty("engine").GetString(), arguments, root, job, started: process => active = process,
                    timeoutMilliseconds: seconds * 1000, outputLimitBytes: 131072, checkStorage: Guard);
                Write(Path.Combine(root, label + "-process.json"), receipt); Require(receipt.Success, "Entropy media stage failed:" + label); return receipt;
            }
            finally { active = null; }
        }
        void Decode(string label, string output, int remainingSeconds, int contexts = 32, int decoderThreads = 1, int expectedSlices = 1)
        {
            Require(total.Elapsed.TotalSeconds + remainingSeconds + 30 < activeDeadline, "Remaining entropy decoder suffix cannot fit.");
            string decoder = p.GetProperty("decoderCoordinator").GetString(), decoderPlan = Path.Combine(root, label + "-decoder-plan.json"), decoderRoot = Path.Combine(root, label + "-full32");
            var bound = p.GetProperty("runtimeFiles").EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetString());
            bound[output] = Hash(output); bound[Path.GetFullPath(planFile)] = expected;
            string decoderBuild = Path.GetDirectoryName(decoder);
            foreach (string path in new[] { Environment.ProcessPath, decoder, Path.Combine(decoderBuild, "F10Recorder.WorkerChecks.deps.json"), Path.Combine(decoderBuild, "F10Recorder.WorkerChecks.runtimeconfig.json"), Path.Combine(decoderBuild, "F10Recorder.Windows.dll"), Path.Combine(decoderBuild, "F10Recorder.Core.dll") }) bound[Path.GetFullPath(path)] = Hash(path);
            Write(decoderPlan, new { kind = "native-full-decode-pilot-v1", sourceOnly = true, root = decoderRoot, movie = output, files = bound,
                runtimeFiles = p.GetProperty("runtimeFiles"), libraryRoot = Path.Combine(p.GetProperty("runtime").GetString(), "lib"), host = Path.Combine(p.GetProperty("runtime").GetString(), "host"),
                modes = new[] { contexts }, decoderThreads, modeDeadlines = new[] { 45 }, deadlineSeconds = 150, maximumChildren = 2,
                retainedLogicalBytes = p.GetProperty("retainedLogicalBytes").GetInt64() + Directory.GetFiles(root, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length), createdUtc = DateTime.UtcNow });
            Require((processes += 2) <= maximumProcesses, "Entropy nested process ceiling before launch.");
            var elapsed = Stopwatch.StartNew(); using var child = new Process { StartInfo = new ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(decoder) } };
            foreach (string arg in new[] { decoder, "--native-api-run", decoderPlan, Hash(decoderPlan) }) child.StartInfo.ArgumentList.Add(arg);
            bool launched = false; int? pid = null; Task<string> stdout = null, stderr = null; int bytes = 0; string failure = null;
            using var cancel = new CancellationTokenSource();
            Task<string> Drain(Stream stream) => Task.Run(async () => { using var content = new MemoryStream(); byte[] buffer = new byte[4096]; while (true) { int count = await stream.ReadAsync(buffer, cancel.Token); if (count == 0) break; Require(Interlocked.Add(ref bytes, count) <= 131072, "Entropy live diagnostic cap."); content.Write(buffer, 0, count); } return System.Text.Encoding.UTF8.GetString(content.ToArray()); });
            try
            {
                Require(child.Start(), "Entropy decoder launch failed."); launched = true; pid = child.Id; job.Add(child); active = child;
                stdout = Drain(child.StandardOutput.BaseStream); stderr = Drain(child.StandardError.BaseStream);
                while (!child.WaitForExit(50)) { Guard(); Require(elapsed.Elapsed.TotalSeconds < 40 && !stdout.IsFaulted && !stderr.IsFaulted, "Entropy decoder tree deadline/log failure."); }
                Require(stdout.Wait(1000) && stderr.Wait(1000) && child.ExitCode == 0, "Entropy complete decoder failed.");
                using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(decoderRoot, "result.json")));
                Require(result.RootElement.GetProperty("result").GetString() == "native-api-pilot-complete", "Entropy decoder result incomplete.");
                if (sliced)
                {
                    using var details = JsonDocument.Parse(File.ReadAllText(Path.Combine(decoderRoot, "native-" + contexts + ".json")));
                    var d = details.RootElement.GetProperty("details");
                    Require(d.GetProperty("minimumVclNals").GetInt32() == expectedSlices && d.GetProperty("maximumVclNals").GetInt32() == expectedSlices
                        && d.GetProperty("decoderThreads").GetInt32() == decoderThreads, "Actual coded-slice/thread configuration differs from the frozen comparison.");
                }
            }
            catch (Exception e) { failure = e.ToString(); throw; }
            finally
            {
                active = null;
                if (launched && !child.HasExited) { child.Kill(true); Require(child.WaitForExit(5000), "Entropy decoder tree exit unconfirmed."); }
                cancel.Cancel(); try { stdout?.Wait(1000); stderr?.Wait(1000); } catch (AggregateException) { }
                var receipt = new { label, pid, exit = launched ? (int?)child.ExitCode : null, absent = !launched || child.HasExited, seconds = elapsed.Elapsed.TotalSeconds, failure,
                    stdout = stdout?.IsCompletedSuccessfully == true ? stdout.Result : "Not completely drained", stderr = stderr?.IsCompletedSuccessfully == true ? stderr.Result : "Not completely drained" };
                results.Add(receipt); Write(Path.Combine(root, label + "-decoder-tree.json"), receipt);
            }
        }
        try
        {
            Require(total.Elapsed.TotalSeconds < 30, "Entropy setup allowance exhausted.");
            string[] variants = sliced ? new[] { "slice1", "slice8" } : new[] { "cabac", "cavlc" };
            foreach (string coder in variants)
            {
                string output = Path.Combine(root, coder + ".mp4");
                var receipt = Media(coder + "-encode", ["-hide_banner", "-loglevel", "warning", "-xerror", "-y", "-copyts", "-i", movie,
                    "-map", "0:v:0", "-map", "0:a:0", "-fps_mode", "passthrough", "-enc_time_base:v", "1:48000", "-video_track_timescale", "48000",
                    "-c:v", "h264_nvenc", "-preset", "p3", "-tune", "ll", "-rc", "cbr", "-b:v", "20M", "-maxrate", "20M", "-bufsize", "40M", "-g", "60", "-bf", "0", "-coder", sliced ? "cabac" : coder,
                    .. (sliced ? new[] { "-slices", coder == "slice1" ? "1" : "8" } : Array.Empty<string>()),
                    "-c:a", "copy", "-avoid_negative_ts", "disabled", "-movflags", "+faststart", output], 90, sliced ? coder == "slice1" ? 435 : 300 : coder == "cabac" ? 390 : 255);
                results.Add(new { coder, encodeSeconds = receipt.ElapsedMilliseconds / 1000.0, bytes = new FileInfo(output).Length });
                if (!sliced || coder == "slice1") Decode(coder, output, sliced ? 345 : coder == "cabac" ? 300 : 165);
                else { Decode("slice8-4x8", output, 210, 4, 8, 8); Decode("slice8-8x4", output, 165, 8, 4, 8); }
            }
            foreach (string coder in variants)
            {
                var receipt = Media(coder + "-ssim", ["-hide_banner", "-loglevel", "info", "-xerror", "-err_detect", "explode", "-progress", "pipe:1", "-nostats", "-i", movie, "-i", Path.Combine(root, coder + ".mp4"),
                    "-filter_complex_threads", "1", "-filter_complex", "[0:v:0]setpts=PTS-STARTPTS[a];[1:v:0]setpts=PTS-STARTPTS[b];[a][b]ssim", "-an", "-fps_mode", "passthrough", "-f", "null", "-"], 60, coder == variants[0] ? 120 : 60);
                var counts = Regex.Matches(receipt.StandardOutput, @"(?m)^frame=(\d+)\s*$");
                var score = Regex.Match(receipt.StandardError, @"SSIM Y:.*All:([0-9.]+)");
                Require(counts.Count > 0 && counts[^1].Groups[1].Value == "18000" && receipt.StandardOutput.Contains("progress=end") && score.Success, "Full paired SSIM result missing.");
                results.Add(new { coder, ssimAll = double.Parse(score.Groups[1].Value, CultureInfo.InvariantCulture), seconds = receipt.ElapsedMilliseconds / 1000.0 });
            }
            foreach (var file in p.GetProperty("files").EnumerateObject()) Require(Hash(file.Name) == file.Value.GetString(), "Entropy protected input changed.");
            Write(Path.Combine(root, "result.json"), new { result = sliced ? "slice-counterfactual-complete" : "entropy-counterfactual-complete", processes, results, seconds = total.Elapsed.TotalSeconds, adoption = false, capture = false, publication = false }); return 0;
        }
        catch (Exception e) { Write(Path.Combine(root, "failure.json"), new { error = e.ToString(), processes, results, seconds = total.Elapsed.TotalSeconds }); Console.Error.WriteLine(e); return 1; }
    }
}
