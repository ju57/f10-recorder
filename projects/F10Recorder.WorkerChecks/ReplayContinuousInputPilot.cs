using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace F10Recorder;

// Offline lower-bound diagnostic. The selected 300 complete GOPs deliberately
// differ from the arbitrary-frame baseline request and cannot be published.
internal static class ReplayContinuousInputPilot
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    static void Require(bool ok, string why) { if (!ok) throw new IOException(why); }
    static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
    static void Write(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, Json));
    internal static int PrepareValidation(string planFile, string root, string previous)
    {
        Require(!File.Exists(planFile) && !Directory.Exists(root), "Fresh closed-diagnostic plan/root required.");
        using var failed = JsonDocument.Parse(File.ReadAllText(Path.Combine(previous, "failure.json")));
        using var mux = JsonDocument.Parse(File.ReadAllText(Path.Combine(previous, "mux-process.json")));
        Require(failed.RootElement.GetProperty("children").GetInt32() == 3
            && failed.RootElement.GetProperty("error").GetString().Contains("Native report storage cap.")
            && mux.RootElement.GetProperty("Success").GetBoolean() && mux.RootElement.GetProperty("ActualExitObserved").GetBoolean()
            && mux.RootElement.GetProperty("ProducerAbsent").GetBoolean(), "Only closed failed report-cap cohort is this follow-up's predecessor.");
        var plan = JsonNode.Parse(File.ReadAllText(Path.Combine(previous, "admitted-plan.json"))).AsObject();
        string movie = plan["movie"].GetValue<string>();
        var files = new JsonObject();
        foreach (var file in plan["runtimeFiles"].AsObject()) files[file.Key] = file.Value.DeepClone();
        string build = Path.GetDirectoryName(typeof(NativeApiPilot).Assembly.Location);
        foreach (string file in new[] { Environment.ProcessPath, typeof(NativeApiPilot).Assembly.Location, typeof(CaptureSession).Assembly.Location,
            typeof(Settings).Assembly.Location, Path.Combine(build, "F10Recorder.WorkerChecks.deps.json"), Path.Combine(build, "F10Recorder.WorkerChecks.runtimeconfig.json"),
            Path.GetFullPath("projects/F10Recorder.WorkerChecks/NativeApiPilot.cs"), Path.GetFullPath("projects/F10Recorder.WorkerChecks/ReplayContinuousInputPilot.cs"),
            Path.GetFullPath("tests/native-api-pilot.ps1"), Path.Combine(previous, "admitted-plan.json"), Path.Combine(previous, "failure.json"),
            Path.Combine(previous, "mux-process.json"), Path.Combine(previous, "continuous-stage.json"), movie }) files[Path.GetFullPath(file)] = Hash(file);
        long dependencies = Directory.GetFiles(Path.GetFullPath("artifacts/issue83/native-api"), "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        long retained = plan["retainedLogicalBytes"].GetValue<long>() + Directory.GetFiles(previous, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length)
            + dependencies - plan["dependencyBytes"].GetValue<long>();
        Require(dependencies < 1_500_000_000 && retained + 500_000_000 < 15_000_000_000, "Complete read-only follow-up cannot fit.");
        plan["root"] = root; plan["continuousInput"] = false; plan["sourceOnly"] = true; plan["files"] = files;
        plan["maximumChildren"] = 2; plan["deadlineSeconds"] = 150;
        plan["cumulativeNativeHelpers"] = plan["cumulativeNativeHelpers"].GetValue<int>() + 1 + 2;
        plan["retainedLogicalBytes"] = retained; plan["dependencyBytes"] = dependencies;
        plan["phaseSeconds"] = JsonSerializer.SerializeToNode(new { setup = 30, decoder = 45, closure = 15, cleanup = 15 });
        plan["forwardBytes"] = 500_000_000; plan["closureReserve"] = 500_000_000; plan["createdUtc"] = JsonSerializer.SerializeToNode(DateTime.UtcNow);
        plan["effects"] = "One coordinator and one read-only32-context software full closed-MP4 decode of retained nonpublishable GOP-aligned mux diagnostic. No new media/GPU/capture/playback/worker/UI/catalog/settings/startup/NVIDIA/install. Prior failed attempt and derivatives remain.";
        Write(planFile, plan); Console.WriteLine(planFile + " " + Hash(planFile)); return 0;
    }
    internal static int Prepare(string planFile, string root, string previous, string baseline, string runtime, string engineRoot)
    {
        Require(!File.Exists(planFile) && !Directory.Exists(root), "Fresh continuous-input plan/root required.");
        using var prior = JsonDocument.Parse(File.ReadAllText(Path.Combine(previous, "admitted-plan.json")));
        using var done = JsonDocument.Parse(File.ReadAllText(Path.Combine(previous, "result.json")));
        Require(done.RootElement.GetProperty("result").GetString() == "native-api-pilot-complete"
            && prior.RootElement.GetProperty("modes").EnumerateArray().Select(x => x.GetInt32()).SequenceEqual(new[] { 0 }), "Closed GPU predecessor required.");
        using var sample = JsonDocument.Parse(File.ReadAllText(Path.Combine(baseline, "first-use.json")));
        string[] original = sample.RootElement.GetProperty("arguments").EnumerateArray().Select(x => x.GetString()).ToArray();
        string concat = original[Array.IndexOf(original, "-i") + 1], snapshot = Path.GetDirectoryName(concat);
        string[] lines = File.ReadAllLines(concat);
        Require(lines.Length == 903 && lines[0] == "file '000000.ts'" && lines[1] == "duration 1.000000" && lines[2].StartsWith("inpoint "), "Original completed snapshot shape changed.");
        double origin = double.Parse(lines[2][8..], CultureInfo.InvariantCulture);
        string manifest = planFile + ".inputs.json"; Require(!File.Exists(manifest), "Fresh source manifest required.");
        var sources = Enumerable.Range(0, 300).Select(i => Path.Combine(snapshot, i.ToString("D6") + ".ts")).ToArray();
        foreach (string file in sources) Require(new FileInfo(file).Length is > 0 and <= 16_000_000 && new FileInfo(file).Length % 188 == 0, "Bounded complete TS required.");
        Write(manifest, sources.Select(path => new { path, bytes = new FileInfo(path).Length, sha256 = Hash(path) }).ToArray());
        var runtimeFiles = Directory.GetFiles(Path.Combine(runtime, "host")).Concat(Directory.GetFiles(Path.Combine(runtime, "lib"), "*.dll")).ToDictionary(Path.GetFullPath, Hash);
        foreach (string file in runtimeFiles.Keys) TrustedStorage.ValidateFile(file);
        var files = new Dictionary<string, string>(runtimeFiles);
        string build = Path.GetDirectoryName(typeof(NativeApiPilot).Assembly.Location);
        MediaDependency.RootDirectory = engineRoot; string engine = MediaDependency.ResolveExecutable();
        foreach (string path in new[] { Environment.ProcessPath, typeof(NativeApiPilot).Assembly.Location, typeof(CaptureSession).Assembly.Location,
            typeof(Settings).Assembly.Location, Path.Combine(build, "F10Recorder.WorkerChecks.deps.json"), Path.Combine(build, "F10Recorder.WorkerChecks.runtimeconfig.json"),
            Path.GetFullPath("projects/F10Recorder.WorkerChecks/NativeApiPilot.cs"), Path.GetFullPath("projects/F10Recorder.WorkerChecks/ReplayContinuousInputPilot.cs"),
            Path.GetFullPath("tests/native-api-pilot.ps1"), Path.Combine(previous, "admitted-plan.json"), Path.Combine(previous, "result.json"),
            Path.Combine(baseline, "first-use.json"), Path.Combine(baseline, "closure.json"), concat, manifest, engine,
            Path.GetFullPath("artifacts/issue83/plans/continuous-input-01.prepare-failure.json") }) files[Path.GetFullPath(path)] = Hash(path);
        long dependencyBytes = Directory.GetFiles(Path.GetFullPath("artifacts/issue83/native-api"), "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        Require(dependencyBytes <= 1_500_000_000, "Dependency staging cap.");
        long retained = prior.RootElement.GetProperty("retainedLogicalBytes").GetInt64()
            + Directory.GetFiles(previous, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length)
            + dependencyBytes - prior.RootElement.GetProperty("dependencyBytes").GetInt64();
        Require(retained + 2_500_000_000 + 500_000_000 < 15_000_000_000, "Entire mux/validation cohort and closure cannot fit.");
        Write(planFile, new { kind = "native-full-decode-pilot-v1", continuousInput = true, root, runtime,
            libraryRoot = Path.Combine(runtime, "lib"), host = Path.Combine(runtime, "host"), movie = Path.Combine(root, "continuous.mp4"),
            files, runtimeFiles, sourceManifest = Path.GetFullPath(manifest), inputOrigin = origin, engineRoot, engine,
            modes = new[] { 32 }, modeDeadlines = new[] { 45 }, deadlineSeconds = 180, maximumChildren = 3, preparationHelpers = 1,
            cumulativeNativeHelpers = prior.RootElement.GetProperty("cumulativeNativeHelpers").GetInt32() + 1 + 1 + 3,
            phaseSeconds = new { preflight = 30, assembly = 30, mux = 30, decode = 45, closure = 20, cleanup = 15 },
            capBytes = 16_000_000_000, stopBytes = 15_000_000_000, floorBytes = 60_000_000_000, maximumChildRamBytes = 3_000_000_000,
            retainedLogicalBytes = retained, dependencyBytes, forwardBytes = 2_500_000_000, closureReserve = 500_000_000,
            createdUtc = DateTime.UtcNow,
            claim = "Nonpublishable lower-bound diagnostic: first300 complete source GOPs, not the frozen arbitrary-frame baseline interval. No exact-cut/audio-priming/source-content or live-capture/percentile acceptance. No index or other work moved into capture.",
            effects = "One coordinator reads300 pinned task-generated TS files, rewrites only transport continuity counters into one temporary TS, then one authenticated FFmpeg stream-copy MP4 mux and one32-context full closed-output software decode. Both derivatives stay isolated and unpublished. No GPU/capture/playback/worker/UI/account catalog/settings/startup/NVIDIA/install." });
        Console.WriteLine(planFile + " " + Hash(planFile)); return 0;
    }
    internal sealed class Inputs : IDisposable
    {
        internal readonly List<(FileStream File, string Hash)> Files = new();
        internal void Verify() { foreach (var item in Files) { item.File.Position = 0; Require(Convert.ToHexString(SHA256.HashData(item.File)) == item.Hash, "Pinned TS changed."); } }
        public void Dispose() { foreach (var item in Files) item.File.Dispose(); }
    }
    internal static Inputs OpenInputs(JsonElement p)
    {
        var inputs = new Inputs();
        try
        {
            string manifest = p.GetProperty("sourceManifest").GetString(); Require(new FileInfo(manifest).Length < 131072, "TS manifest cap.");
            using var list = JsonDocument.Parse(File.ReadAllText(manifest)); Require(list.RootElement.GetArrayLength() == 300, "Exactly300 frozen GOP inputs required.");
            foreach (var item in list.RootElement.EnumerateArray())
            {
                var file = new FileStream(item.GetProperty("path").GetString(), FileMode.Open, FileAccess.Read, FileShare.Read);
                inputs.Files.Add((file, item.GetProperty("sha256").GetString()));
                Require(file.Length == item.GetProperty("bytes").GetInt64(), "TS length changed.");
            }
            inputs.Verify(); return inputs;
        }
        catch { inputs.Dispose(); throw; }
    }
    internal static object Run(JsonElement p, Inputs inputs, string root, Native.ProcessJob job, Action launching)
    {
        var total = Stopwatch.StartNew(); string assembled = Path.Combine(root, "continuous.ts");
        int[] counters = new int[8192]; byte[] data = new byte[188 * 8192];
        Process muxProcess = null;
        void Guard()
        {
            Require(total.Elapsed.TotalSeconds < 65, "Continuous input stage deadline.");
            Require(new DriveInfo(Path.GetPathRoot(root)).AvailableFreeSpace >= 60_000_000_000, "Continuous stage free-space floor.");
            long bytes = Directory.GetFiles(root).Sum(f => new FileInfo(f).Length);
            Require(bytes < 2_500_000_000 && bytes + p.GetProperty("retainedLogicalBytes").GetInt64() < 15_000_000_000, "Continuous stage storage stop.");
            if (muxProcess != null && !muxProcess.HasExited)
                try { muxProcess.Refresh(); Require(muxProcess.PeakWorkingSet64 <= 3_000_000_000, "Mux RAM cap."); }
                catch (InvalidOperationException) when (muxProcess.HasExited) { }
        }
        using (var output = new FileStream(assembled, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        foreach (var item in inputs.Files)
        {
            Guard(); item.File.Position = 0;
            while (item.File.Position < item.File.Length)
            {
                Require(total.Elapsed.TotalSeconds < 30, "TS assembly deadline.");
                int count = (int)Math.Min(data.Length, item.File.Length - item.File.Position); item.File.ReadExactly(data.AsSpan(0, count));
                for (int off = 0; off < count; off += 188)
                {
                    Require(data[off] == 0x47 && (data[off + 1] & 0x80) == 0 && (data[off + 3] & 0x30) != 0, "Malformed transport input.");
                    int pid = (data[off + 1] & 31) * 256 + data[off + 2];
                    int cc = (data[off + 3] & 0x10) != 0 ? counters[pid]++ : counters[pid] - 1;
                    data[off + 3] = (byte)((data[off + 3] & 0xf0) | (cc & 15));
                }
                output.Write(data, 0, count);
            }
        }
        double assemblySeconds = total.Elapsed.TotalSeconds;
        MediaDependency.RootDirectory = p.GetProperty("engineRoot").GetString(); string engine = MediaDependency.ResolveExecutable();
        Require(engine == p.GetProperty("engine").GetString(), "Mux engine changed.");
        string[] arguments = ["-hide_banner", "-loglevel", "warning", "-xerror", "-y", "-copyts", "-itsoffset",
            (-p.GetProperty("inputOrigin").GetDouble()).ToString("R", CultureInfo.InvariantCulture), "-f", "mpegts", "-analyzeduration", "0", "-probesize", "1048576",
            "-i", assembled, "-map", "0:v:0", "-map", "0:a:0", "-c", "copy", "-video_track_timescale", "48000", "-avoid_negative_ts", "disabled", "-movflags", "+faststart", p.GetProperty("movie").GetString()];
        Write(Path.Combine(root, "mux-arguments.json"), arguments); launching();
        var mux = ReplayExportProcess.Run(engine, arguments, root, job, started: process => muxProcess = process,
            timeoutMilliseconds: 30000, outputLimitBytes: 131072, checkStorage: Guard);
        Write(Path.Combine(root, "mux-process.json"), mux); Require(mux.Success, "Continuous input mux failed; retain this attempt.");
        return new { assemblySeconds, mux.ElapsedMilliseconds, totalAssemblyAndMuxSeconds = total.Elapsed.TotalSeconds,
            sourceBytes = inputs.Files.Sum(x => x.File.Length), assembledBytes = new FileInfo(assembled).Length,
            movieBytes = new FileInfo(p.GetProperty("movie").GetString()).Length, mux.ProcessId, mux.ProducerAbsent, mux.ActualExitObserved,
            boundaryAcceptance = false, publication = false };
    }
}
