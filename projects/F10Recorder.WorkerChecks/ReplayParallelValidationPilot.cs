using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace F10Recorder;

// Exploratory full-output decoding. Not used by the product validator. In
// particular, packet-clock coverage prevents a seek/trim from hiding bad PTS.
internal static class ReplayParallelValidationPilot
{
    static readonly JsonSerializerOptions Json = new(WorkerProtocol.Json) { WriteIndented = true };
    const long Cap = 16_000_000_000, Stop = 15_000_000_000, Floor = 60_000_000_000;
    static string Number(double x) => x.ToString("0.#########", CultureInfo.InvariantCulture);
    static void Require(bool ok, string why) { if (!ok) throw new IOException(why); }
    static string Hash(string p) { using var f = File.OpenRead(p); return Convert.ToHexString(SHA256.HashData(f)); }
    static void Write(string p, object o) => File.WriteAllText(p, JsonSerializer.Serialize(o, Json));
    internal static int Prepare(string planFile, string root, string baseline, string engineRoot, string previousRoot, string preparationHistory = null)
    {
        Require(!File.Exists(planFile) && !Directory.Exists(root), "Fresh parallel pilot required.");
        using var s = JsonDocument.Parse(File.ReadAllText(Path.Combine(baseline, "first-use.json")));
        using var previous = JsonDocument.Parse(File.ReadAllText(Path.Combine(previousRoot, "result.json")));
        using var priorPlan = JsonDocument.Parse(File.ReadAllText(Path.Combine(previousRoot, "admitted-plan.json")));
        Require(previous.RootElement.GetProperty("result").GetString() == "finite-pilots-complete", "Complete prior finite comparison required.");
        foreach (var result in previous.RootElement.GetProperty("results").EnumerateArray())
            Require(result.GetProperty("process").GetProperty("Success").GetBoolean()
                && result.GetProperty("process").GetProperty("ProducerAbsent").GetBoolean(), "Previous child not closed successfully.");
        string movie = s.RootElement.GetProperty("final").GetString();
        string inputManifest = priorPlan.RootElement.GetProperty("manifest").GetString();
        MediaDependency.RootDirectory = engineRoot;
        string ffmpeg = MediaDependency.ResolveExecutable(), probe = Path.Combine(Path.GetDirectoryName(ffmpeg), "ffprobe.exe");
        using (MediaDependency.OpenVerifiedExecutable(probe)) { }
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int priorPreparations = 0;
        if (preparationHistory != null)
        {
            using var history = JsonDocument.Parse(File.ReadAllText(preparationHistory));
            priorPreparations = history.RootElement.GetProperty("failedPreparationHelpers").GetInt32();
            Require(priorPreparations is >= 0 and <= 16 && history.RootElement.GetProperty("acquisitions").GetInt32() == 0, "Invalid preparation history.");
            files[Path.GetFullPath(preparationHistory)] = Hash(preparationHistory);
        }
        string build = Path.GetDirectoryName(typeof(ReplayParallelValidationPilot).Assembly.Location);
        foreach (string f in new[] { Environment.ProcessPath, typeof(ReplayParallelValidationPilot).Assembly.Location,
            typeof(CaptureSession).Assembly.Location, typeof(Settings).Assembly.Location,
            Path.Combine(build, "F10Recorder.WorkerChecks.deps.json"), Path.Combine(build, "F10Recorder.WorkerChecks.runtimeconfig.json"),
            ffmpeg, probe, movie, inputManifest, Path.Combine(baseline, "first-use.json"), Path.Combine(previousRoot, "result.json"), Path.Combine(previousRoot, "admitted-plan.json"),
            Path.GetFullPath("projects/F10Recorder.WorkerChecks/ReplayParallelValidationPilot.cs"), Path.GetFullPath("tests/replay-parallel-pilot.ps1") }) files[Path.GetFullPath(f)] = Hash(f);
        long retained = checked(priorPlan.RootElement.GetProperty("retainedLogicalBytes").GetInt64()
            + Directory.GetFiles(previousRoot, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length));
        Require(retained + 2_000_000_000 < Stop, "Retained media plus bounded reports/closure cannot fit.");
        Write(planFile, new { kind = "issue83-parallel-full-pilot-v1", root, movie, engineRoot, files, inputManifest, groups = new[] { 2, 8, 16, 32 },
            maximumChildren = 66, preparationHelpers = 1,
            cumulativeNativeHelpers = priorPlan.RootElement.GetProperty("cumulativeNativeHelpers").GetInt32() + priorPreparations + 1 + 66,
            failedPreparationHelpersCharged = priorPreparations,
            deadlineSeconds = 600, groupDeadlinesSeconds = new[] { 90, 45, 45, 45 },
            phaseSeconds = new { setup = 100, info = 30, packetClock = 45, packetCopyDiagnostic = 45, groups = 225, closure = 55, cleanup = 20 },
            cap = Cap, stop = Stop, floor = Floor, retainedLogicalBytes = retained, maximumSampledChildRamBytes = 6_000_000_000,
            maximumFiles = 1024, maximumDirectories = 64,
            createdUtc = DateTime.UtcNow, acquisitions = 0,
            claim = "Offline finite mechanism measurement only; no product validator replacement, publication or latency acceptance. Full baseline CPU oracle passed; fault-sensitivity controls still required before adoption.",
            effects = "One helper; one approved ffprobe schema, one full video-packet clock scan, one copy-to-null diagnostic,58video decoders across2/8/16/32 groups plus4fullaudio decoders. At most33mediachildren concurrently; CPU/RAM/IO load, noGPU/capture/audio playback/window/worker/startup/preferences/catalog/install. All children owned by one kill-on-close job; failed group stops later groups." });
        Console.WriteLine(planFile + " " + Hash(planFile)); return 0;
    }
    sealed record Packet(long Pts, long Dts, long Duration, bool Key);
    static List<Packet> Packets(string text)
    {
        var packets = new List<Packet>();
        foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            Require(packets.Count < 20000, "Packet row cap.");
            var fields = line.Trim().Split('|').Select(x => x.Split('=', 2)).Where(x => x.Length == 2).ToDictionary(x => x[0], x => x[1]);
            packets.Add(new(long.Parse(fields["pts"], CultureInfo.InvariantCulture), long.Parse(fields["dts"], CultureInfo.InvariantCulture),
                long.Parse(fields["duration"], CultureInfo.InvariantCulture), fields["flags"].Contains('K')));
        }
        Require(packets.Count == 18000 && packets[0].Pts >= -1 && packets[0].Pts <= 801, "Exact complete reference video packet count/start required.");
        for (int i = 0; i < packets.Count; i++)
            Require(Math.Abs(packets[i].Pts - (packets[0].Pts + i * 800L)) <= 1 && packets[i].Dts == packets[i].Pts
                && Math.Abs(packets[i].Duration - 800) <= 1, "Global packet clock/count failed at" + i);
        return packets;
    }
    static string[] ShardArguments(string movie, int begin, int end, long firstPts, bool last)
    {
        double origin = firstPts / 48000.0, start = origin + begin / 60.0, stop = origin + end / 60.0;
        string trim = begin == 0 ? "" : "start=" + Number(start);
        if (!last) trim += (trim.Length == 0 ? "" : ":") + "end=" + Number(stop);
        string graph = "[0:v:0]settb=1/48000," + (trim.Length == 0 ? "" : "trim=" + trim + ",")
            + "split[full][timing];[timing]select='eq(n,0)+gt(abs((pts-prev_pts)*TB-0.016666667),0.0011)"
            + "+gt(abs(pts*TB-" + Number(origin) + "-(n+" + begin + ")/60),0.000022)',showinfo,nullsink";
        var args = new List<string> { "-hide_banner", "-loglevel", "info", "-xerror", "-err_detect", "explode", "-copyts",
            "-filter_complex_threads", "1", "-progress", "pipe:1", "-nostats", "-threads:v", "1" };
        // Seek just after the assigned IDR but retain decoded preroll. Trimming
        // assigns PTS ranges; the complete packet scan checks even discarded PTS.
        if (begin > 0) args.AddRange(["-ss", Number(start + 0.002), "-noaccurate_seek"]);
        args.AddRange(["-i", movie, "-filter_complex", graph, "-map", "[full]", "-an", "-fps_mode", "passthrough", "-f", "null", "-"]);
        return args.ToArray();
    }
    static void ValidateShard(ReplayExportProcessReceipt receipt, int begin, int end, long firstPts)
    {
        var counts = Regex.Matches(receipt.StandardOutput, @"(?m)^frame=(\d+)\s*$");
        var selected = Regex.Matches(receipt.StandardError, @"\bn:\s*\d+\s+pts:\s*(-?\d+)\s+pts_time:");
        Require(receipt.Success && receipt.StandardOutput.Contains("progress=end") && counts.Count > 0
            && long.Parse(counts[^1].Groups[1].Value, CultureInfo.InvariantCulture) == end - begin
            && selected.Count == 1, "Complete shard decode/count/global timing failed.");
        long first = long.Parse(selected[0].Groups[1].Value, CultureInfo.InvariantCulture);
        Require(Math.Abs(first - (firstPts + begin * 800L)) <= 1, "Wrong global shard origin.");
    }
    internal static int Run(string planFile, string expected)
    {
        var total = Stopwatch.StartNew(); Require(Hash(planFile) == expected && new FileInfo(planFile).Length < 65536, "Reviewed parallel plan changed.");
        using var doc = JsonDocument.Parse(File.ReadAllText(planFile)); var p = doc.RootElement;
        Require(p.GetProperty("kind").GetString() == "issue83-parallel-full-pilot-v1" && p.GetProperty("maximumChildren").GetInt32() == 66, "Wrong admission.");
        foreach (var f in p.GetProperty("files").EnumerateObject()) Require(Hash(f.Name) == f.Value.GetString(), "Bound input changed: " + f.Name);
        void VerifySources()
        {
            string manifest = p.GetProperty("inputManifest").GetString(); Require(new FileInfo(manifest).Length <= 262144, "Input manifest cap.");
            using var inputs = JsonDocument.Parse(File.ReadAllText(manifest)); Require(inputs.RootElement.GetArrayLength() <= 307, "Input count cap.");
            foreach (var f in inputs.RootElement.EnumerateArray())
            {
                Require(total.Elapsed.TotalSeconds < 580, "Hashing/deadline allowance."); string path = f.GetProperty("path").GetString();
                Require(new FileInfo(path).Length == f.GetProperty("bytes").GetInt64() && Hash(path) == f.GetProperty("sha256").GetString(), "Protected concat/input changed.");
            }
        }
        VerifySources();
        string root = p.GetProperty("root").GetString(), movie = p.GetProperty("movie").GetString();
        Require(!Directory.Exists(root) && DateTime.UtcNow - p.GetProperty("createdUtc").GetDateTime() < TimeSpan.FromHours(4), "Fresh/current run required.");
        Directory.CreateDirectory(root); File.Copy(planFile, Path.Combine(root, "admitted-plan.json"));
        MediaDependency.RootDirectory = p.GetProperty("engineRoot").GetString(); string probe = Path.Combine(Path.GetDirectoryName(MediaDependency.ExecutablePath), "ffprobe.exe");
        using var job = new Native.ProcessJob(); var gate = new object(); int children = 1; double lastScan = -1; long peakRam = 0;
        var probes = new List<Process>(); var results = new List<object>();
        void Guard()
        {
            lock (gate)
            {
                Require(total.Elapsed.TotalSeconds < 580, "Parallel pilot deadline;20s cleanup reserve.");
                if (total.Elapsed.TotalSeconds - lastScan < .2) return; lastScan = total.Elapsed.TotalSeconds;
                long ram = 0;
                foreach (var process in probes)
                    try
                    {
                        if (process.HasExited) continue;
                        if (GetProcessMemoryInfo(process.SafeHandle.DangerousGetHandle(), out var memory, (uint)Marshal.SizeOf<MemoryCounters>())) ram += (long)memory.WorkingSet;
                        else Require(process.HasExited, "Live child memory query failed; no unmeasured RAM admission.");
                    }
                    catch (InvalidOperationException) { }
                peakRam = Math.Max(peakRam, ram); Require(ram <= 6_000_000_000, "Child working-set cap.");
                var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
                Require(files.Length <= 1024 && Directory.GetDirectories(root, "*", SearchOption.AllDirectories).Length <= 64, "File/directory cap.");
                long bytes = files.Sum(f => new FileInfo(f).Length);
                Require(bytes + p.GetProperty("retainedLogicalBytes").GetInt64() < Stop && ReplayStorage.ObserveCapacity(root).AvailableBytes >= Floor, "Parallel storage stop/floor.");
            }
        }
        ReplayExportProcessReceipt Child(string label, string executable, string[] args, int seconds)
        {
            lock (gate) { Require(++children <= 66, "Child ceiling before launch."); Write(Path.Combine(root, label + ".arguments.json"), args); }
            Process costs = null;
            var receipt = ReplayExportProcess.Run(executable, args, root, job,
                started: process => { costs = Process.GetProcessById(process.Id); _ = costs.SafeHandle; lock (gate) probes.Add(costs); },
                timeoutMilliseconds: seconds * 1000, checkStorage: Guard);
            Write(Path.Combine(root, label + ".process.json"), receipt);
            object usage = null;
            if (costs != null)
            {
                Require(GetProcessIoCounters(costs.SafeHandle.DangerousGetHandle(), out var io), "Child IO query failed:" + Marshal.GetLastWin32Error());
                usage = new { cpuSeconds = costs.TotalProcessorTime.TotalSeconds, io };
                lock (gate) { probes.Remove(costs); costs.Dispose(); }
            }
            Write(Path.Combine(root, label + ".json"), new { process = receipt, usage }); return receipt;
        }
        try
        {
            Require(total.Elapsed.TotalSeconds < 100, "Setup allowance.");
            var info = Child("schema", probe, ["-v", "error", "-show_streams", "-of", "json", movie], 30);
            Require(info.Success, "Schema probe failed.");
            using (var schema = JsonDocument.Parse(info.StandardOutput))
            {
                var streams = schema.RootElement.GetProperty("streams").EnumerateArray().ToArray();
                Require(streams.Length == 2 && streams[0].GetProperty("codec_name").GetString() == "h264"
                    && streams[0].GetProperty("time_base").GetString() == "1/48000" && streams[0].GetProperty("width").GetInt32() == 1920
                    && streams[0].GetProperty("height").GetInt32() == 1080 && streams[1].GetProperty("codec_name").GetString() == "aac"
                    && streams[1].GetProperty("sample_rate").GetString() == "48000" && streams[1].GetProperty("channels").GetInt32() == 2, "Reference schema changed.");
            }
            var clock = Child("packet-clock", probe, ["-v", "error", "-select_streams", "v:0", "-show_packets", "-show_entries", "packet=pts,dts,duration,flags", "-of", "compact=p=0:nk=0", movie], 45);
            Require(clock.Success, "Full packet clock scan failed."); var packets = Packets(clock.StandardOutput);
            // A separate lower bound for demux/probe/I/O, not an exact exporter.
            using var baseline = JsonDocument.Parse(File.ReadAllText(p.GetProperty("files").EnumerateObject().Single(f => Path.GetFileName(f.Name) == "first-use.json").Name));
            var original = baseline.RootElement.GetProperty("arguments").EnumerateArray().Select(x => x.GetString()).ToArray();
            string concat = original[Array.IndexOf(original, "-i") + 1];
            var copy = Child("concat-copy-null", MediaDependency.ExecutablePath, ["-hide_banner", "-loglevel", "warning", "-xerror", "-copyts", "-f", "concat", "-safe", "1", "-protocol_whitelist", "file", "-i", concat, "-map", "0:v:0", "-map", "0:a:0", "-c", "copy", "-f", "null", "-"], 45);
            Require(copy.Success, "Copy-to-null diagnostic failed.");
            int[] sizes = [2, 8, 16, 32];
            for (int group = 0; group < sizes.Length; group++)
            {
                int n = sizes[group], deadline = group == 0 ? 90 : 45;
                Require(total.Elapsed.TotalSeconds + (group == 0 ? 225 : (4 - group) * 45) + 55 < 580, "Complete remaining group schedule no longer fits.");
                var starts = new List<int> { 0 };
                for (int k = 1; k < n; k++)
                { int target = packets.Count * k / n; int at = Enumerable.Range(target, Math.Min(120, packets.Count - target)).First(i => packets[i].Key); starts.Add(at); }
                starts.Add(packets.Count); Require(starts.Zip(starts.Skip(1)).All(x => x.First < x.Second), "Shard ranges overlap/empty.");
                var elapsed = Stopwatch.StartNew();
                var tasks = Enumerable.Range(0, n).Select(i => Task.Factory.StartNew(() =>
                {
                    var receipt = Child("g" + n + "-v" + i.ToString("D2"), MediaDependency.ExecutablePath,
                        ShardArguments(movie, starts[i], starts[i + 1], packets[0].Pts, i == n - 1), deadline);
                    ValidateShard(receipt, starts[i], starts[i + 1], packets[0].Pts); return receipt;
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
                var audio = Task.Factory.StartNew(() => Child("g" + n + "-audio", MediaDependency.ExecutablePath,
                    ["-hide_banner", "-loglevel", "info", "-xerror", "-err_detect", "explode", "-copyts", "-progress", "pipe:1", "-nostats", "-i", movie, "-map", "0:a:0", "-vn", "-f", "null", "-"], deadline),
                    CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                Task.WhenAll(tasks.Cast<Task>().Append(audio)).GetAwaiter().GetResult();
                Require(audio.Result.Success && audio.Result.StandardOutput.Contains("progress=end"), "Full enabled audio decode failed.");
                var result = new { group = n, seconds = elapsed.Elapsed.TotalSeconds, starts, decodedFrames = tasks.Sum(t => starts[Array.IndexOf(tasks, t) + 1] - starts[Array.IndexOf(tasks, t)]),
                    packetClockMilliseconds = clock.ElapsedMilliseconds, fullAudio = true, allProducerAbsent = tasks.All(t => t.Result.ProducerAbsent) && audio.Result.ProducerAbsent };
                results.Add(result); Write(Path.Combine(root, "group-" + n + ".json"), result);
            }
            VerifySources(); foreach (var f in p.GetProperty("files").EnumerateObject()) Require(Hash(f.Name) == f.Value.GetString(), "Protected input changed after groups.");
            Write(Path.Combine(root, "result.json"), new { result = "parallel-full-pilot-complete", results, children, peakRam, totalSeconds = total.Elapsed.TotalSeconds,
                schemaMilliseconds = info.ElapsedMilliseconds, packetClockMilliseconds = clock.ElapsedMilliseconds, concatCopyNullMilliseconds = copy.ElapsedMilliseconds,
                publication = false, gpu = "Not used; CPU decoding only. No product replacement or fault-sensitivity claim." }); return 0;
        }
        catch (Exception e) { Write(Path.Combine(root, "failure.json"), new { error = e.ToString(), children, results, peakRam, totalSeconds = total.Elapsed.TotalSeconds }); Console.Error.WriteLine(e); return 1; }
        finally { lock (gate) foreach (var process in probes) process.Dispose(); }
    }
    [StructLayout(LayoutKind.Sequential)] struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);
    [StructLayout(LayoutKind.Sequential)] struct MemoryCounters { public uint Size, PageFaults; public nuint PeakWorkingSet, WorkingSet, PeakPagedPool, PagedPool, PeakNonPagedPool, NonPagedPool, Pagefile, PeakPagefile; }
    [DllImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo", SetLastError = true)] static extern bool GetProcessMemoryInfo(IntPtr process, out MemoryCounters counters, uint size);
}
