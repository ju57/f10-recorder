using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace F10Recorder;

// Short rejection gate only. No capture recipe changes or save-path claim.
internal static class ReplayIntraShortPilot
{
    static readonly JsonSerializerOptions Json = new(WorkerProtocol.Json) { WriteIndented = true };
    sealed record Plan(string Kind, string Root, string EngineRoot, string Movie, string IdOracle,
        Dictionary<string, string> Files, Dictionary<string, string> Preserved, string Startup,
        long RetainedBytes, DateTime CreatedUtc, DateTime ExpiresUtc, object Bounds, string Effects, string Claim);
    static void Require(bool value, string reason) { if (!value) throw new IOException(reason); }
    static string Hash(string path) { using var f = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(f)); }
    static string Startup() { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"); return key?.GetValue("F10Recorder") as string; }
    static void Write(string path, object value)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json); Require(bytes.Length <= 2_097_152, "Short-intra report bound.");
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read); output.Write(bytes);
    }
    internal static int Prepare(string path, string root, string engineRoot, string productPlan)
    {
        root = Path.GetFullPath(root); string task = Path.GetFullPath("artifacts/issue83");
        Require(root.StartsWith(task + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && !Directory.Exists(root) && !File.Exists(path), "Fresh short-intra paths required.");
        using var previous = JsonDocument.Parse(File.ReadAllText(productPlan));
        string product = previous.RootElement.GetProperty("Root").GetString(), reference = previous.RootElement.GetProperty("Reference").GetString();
        using var outcome = JsonDocument.Parse(File.ReadAllText(Path.Combine(product, "outcome.json")));
        Require(outcome.RootElement.GetProperty("status").GetString() == "passed" && outcome.RootElement.GetProperty("ProducerAbsent").GetBoolean(), "Closed source provenance required.");
        string movie = Path.Combine(product, "reference", "result.mp4"), ids = Path.Combine(reference, "original-request-source-ids.stdout.bin");
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Bind(string file) { file = Path.GetFullPath(file); files.Add(file, Hash(file)); }
        Bind(movie); Bind(ids); Bind(productPlan); Bind(Path.Combine(product, "outcome.json")); Bind(Path.Combine(product, "reference", "receipt.json"));
        MediaDependency.RootDirectory = Path.GetFullPath(engineRoot); Bind(MediaDependency.ResolveExecutable()); Bind(Environment.ProcessPath);
        string build = Path.GetDirectoryName(typeof(ReplayIntraShortPilot).Assembly.Location);
        foreach (string name in new[] { "F10Recorder.WorkerChecks.dll", "F10Recorder.Windows.dll", "F10Recorder.Core.dll", "F10Recorder.WorkerChecks.deps.json", "F10Recorder.WorkerChecks.runtimeconfig.json" }) Bind(Path.Combine(build, name));
        foreach (string file in new[] { "projects/F10Recorder.WorkerChecks/ReplayIntraShortPilot.cs", "projects/F10Recorder.WorkerChecks/Program.cs",
            "projects/F10Recorder.Windows/ReplayExportProcess.cs", "projects/F10Recorder.Windows/ReplayExactExport.cs", "tests/replay-intra-short.ps1" }) Bind(file);
        var preserved = previous.RootElement.GetProperty("Preserved").Deserialize<Dictionary<string, string>>(Json);
        Require(preserved.All(p => Hash(p.Key).Equals(p.Value, StringComparison.OrdinalIgnoreCase)) && Startup() == previous.RootElement.GetProperty("Startup").GetString(), "Preserved state differs; do not overwrite it.");
        string[] retained = new[] { task }.Concat(Directory.GetDirectories(Path.GetTempPath(), "f10-issue83-*")).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        long retainedBytes = ReplayStorage.Bytes(ReplayStorage.Inventory(retained, new ReplayFiles(), 20000, 4096));
        Require(retainedBytes + 350_000_000 + 16_000_000 < 22_000_000_000L, "Fresh short-intra storage envelope does not fit.");
        var now = DateTime.UtcNow;
        Write(path, new Plan("issue83-intra-short-gate-v1", root, MediaDependency.RootDirectory, movie, ids, files, preserved, Startup(), retainedBytes, now, now.AddMinutes(30),
            new { mediaChildren = 10, concurrentChildren = 1, seconds = 40, outerSeconds = 60, childSeconds = 15,
                parentWorkingSet = 536870912, childWorkingSet = 1073741824, cap = 350_000_000, stop = 300_000_000, closure = 50_000_000,
                minimumFree = 60_000_000_000L, aggregate = 22_000_000_000L, outsideGrowthReserve = 16_000_000,
                files = 128, directories = 8, maximumGuardGapMs = 500, firstPhysicalOrdinal = 0, physicalPictures = 300,
                order = new[] { 60, 1 }, bitrateMbps = 20, vbvMegabits = 40, maximumCandidateContainerBytes = 18_548_576, maximumAggregateAndEveryFrameSsimLoss = .0005 },
            "Two serial NVENC encodes, two fresh full original software video validations, two ID decodes, two full300-pair SSIM comparisons and two AnnexB stream copies. Exactly first300 source pictures, normalized timestamps, video-only. No playback/capture/audio/worker/UI/MP4 publication/Saved/profile/catalog/startup/NVIDIA/install change.",
            "Short video-layout rejection gate only on an already-lossy saved MP4. Actual300VCL/AUD pictures,5/300IDRs,allIDs and all300paired SSIM rows required. GOP1 aggregate AND EVERY matched frame must be no worse than control minus.0005. Container byte ceiling is20Mbps*5s plus one40Mb VBV credit plus1MiB; report coded AnnexB bytes too. This is not sustained20Mbps,300s decoder throughput, source-capture quality/resource/storage qualification or adoption. Earlier interrupted intra02 remains failed; no expired admission/model reuse."));
        Console.WriteLine(Hash(path)); return 0;
    }
    sealed class Runner(Plan plan) : IDisposable
    {
        internal readonly Stopwatch Clock = Stopwatch.StartNew();
        readonly Native.ProcessJob job = new();
        internal readonly List<object> Processes = [];
        Process active; int children;
        internal long PeakBytes, PeakParent, PeakChild;
        internal double MaximumGap, GuardSeconds;
        internal bool ProducerAbsent = true;
        double last = -1, scanned = -1;
        internal void Guard()
        {
            double now = Clock.Elapsed.TotalSeconds;
            if (last >= 0) MaximumGap = Math.Max(MaximumGap, (now - last) * 1000); last = now;
            Require(now < 40 && MaximumGap <= 500, "Short-intra time/guard bound.");
            if (now - scanned < .1) return; scanned = now; long at = Stopwatch.GetTimestamp();
            using var parent = Process.GetCurrentProcess();
            Require(ReplayExportProcess.TryWorkingSet(parent, out long ram) && ram < 536870912 && parent.HandleCount < 1536, "Short-intra parent resource bound.");
            PeakParent = Math.Max(PeakParent, ram);
            if (active != null && !active.HasExited)
            {
                bool measured = ReplayExportProcess.TryWorkingSet(active, out long child);
                Require(measured || active.HasExited, "Live short-intra child memory unavailable.");
                Require(child < 1073741824, "Short-intra child memory bound."); PeakChild = Math.Max(PeakChild, child);
            }
            var inventory = ReplayStorage.Inventory([plan.Root], new ReplayFiles(), 128, 8);
            long bytes = ReplayStorage.Bytes(inventory); PeakBytes = Math.Max(PeakBytes, bytes);
            Require(bytes < 300_000_000 && plan.RetainedBytes + bytes + 16_000_000 < 22_000_000_000L
                && ReplayStorage.ObserveCapacity(plan.Root).AvailableBytes >= 60_000_000_000L, "Short-intra storage bound.");
            foreach (var file in inventory) Require(Math.Max(file.Value.Length, file.Value.AllocatedBytes) <= 64_000_000, "Short-intra derivative file bound.");
            GuardSeconds += Stopwatch.GetElapsedTime(at).TotalSeconds;
        }
        internal ReplayExportProcessReceipt Run(string label, IReadOnlyList<string> arguments)
        {
            Guard(); Require(++children <= 10, "Short-intra child ceiling.");
            var receipt = ReplayExportProcess.Run(MediaDependency.ExecutablePath, arguments, plan.Root, job,
                started: child => active = child, timeoutMilliseconds: 15000, checkStorage: Guard);
            active = null; ProducerAbsent &= receipt.ProducerAbsent;
            Write(Path.Combine(plan.Root, label + ".process.json"), new { arguments, receipt });
            Processes.Add(new { label, receipt.ProcessId, receipt.ProcessStartedUtc, receipt.Success, receipt.ProducerAbsent,
                receipt.ElapsedMilliseconds, receipt.CpuMilliseconds, receipt.ReadBytes, receipt.WriteBytes, receipt.SampledPeakWorkingSetBytes, receipt.ResourceError });
            Require(receipt.Success && receipt.ResourceError == null, "Short-intra child/evidence failed: " + label + ": " + receipt.Error); return receipt;
        }
        public void Dispose() => job.Dispose();
    }
    static uint[] Ids(string path)
    {
        Require(new FileInfo(path).Length <= 1_000_000, "Source ID extent bound."); byte[] bytes = File.ReadAllBytes(path);
        Require(bytes.Length % 48 == 0, "Source ID cell count."); var ids = new uint[bytes.Length / 48];
        for (int n = 0; n < ids.Length; n++) for (int bit = 0; bit < 24; bit++)
        {
            byte a = bytes[n * 48 + bit], b = bytes[n * 48 + 24 + bit];
            Require(!(a > 64 && a < 192) && !(b > 64 && b < 192) && (a > 128) != (b > 128), "Ambiguous source ID/complement.");
            if (a > 128) ids[n] |= 1u << bit;
        }
        return ids;
    }
    static object Layout(string path, int gop, Runner runner)
    {
        Require(new FileInfo(path).Length <= 64_000_000, "AnnexB extent bound."); byte[] bytes = File.ReadAllBytes(path);
        int aud = 0, vcl = 0, idrs = 0, inPicture = 0; bool complete = false;
        try
        {
        for (int n = 0; n < bytes.Length - 4; n++)
        {
            if (n % 65536 == 0) runner.Guard();
            if (bytes[n] != 0 || bytes[n + 1] != 0) continue;
            int prefix = bytes[n + 2] == 1 ? 3 : bytes[n + 2] == 0 && bytes[n + 3] == 1 ? 4 : 0;
            if (prefix == 0) continue; int header = bytes[n + prefix], type = header & 31; n += prefix;
            Require((header & 128) == 0 && type is 1 or 5 or 6 or 7 or 8 or 9 or 12, "Unexpected AnnexB NAL configuration.");
            if (type == 9) { if (aud > 0) Require(inPicture == 1, "Expected exactly one coded slice per access unit."); aud++; inPicture = 0; }
            if (type is 1 or 5) { Require(aud > 0 && ++inPicture == 1 && (header & 96) != 0, "Expected one reference VCL per picture."); vcl++; if (type == 5) idrs++; }
        }
        complete = true;
        Require(inPicture == 1 && aud == 300 && vcl == 300 && idrs == (gop == 1 ? 300 : 5), "Actual VCL/AUD/IDR layout does not match declared GOP.");
        return new { aud, vcl, idrs, codedAnnexBBytes = bytes.Length, sha256 = Hash(path) };
        }
        finally { Write(Path.Combine(Path.GetDirectoryName(path), "layout.json"), new { gop, complete, aud, vcl, idrs, inPicture, codedAnnexBBytes = bytes.Length, sha256 = Hash(path) }); }
    }
    static double[] Ssim(string path)
    {
        Require(new FileInfo(path).Length <= 262144, "SSIM report bound.");
        var rows = Regex.Matches(File.ReadAllText(path), @"(?m)^n:(\d+)\s+.*\bAll:([0-9.]+)");
        Require(rows.Count == 300, "Exactly300 matched SSIM rows required.");
        return rows.Select((row, n) =>
        {
            Require(int.Parse(row.Groups[1].Value, CultureInfo.InvariantCulture) == n + 1, "SSIM row order changed.");
            double value = double.Parse(row.Groups[2].Value, CultureInfo.InvariantCulture); Require(value is >= 0 and <= 1, "Invalid SSIM value."); return value;
        }).ToArray();
    }
    internal static int Run(string path, string expected)
    {
        Require(new FileInfo(path).Length <= 65536 && Hash(path).Equals(expected, StringComparison.OrdinalIgnoreCase), "Frozen short-intra plan changed.");
        var plan = JsonSerializer.Deserialize<Plan>(File.ReadAllText(path), Json);
        Require(plan.Kind == "issue83-intra-short-gate-v1" && DateTime.UtcNow >= plan.CreatedUtc && DateTime.UtcNow.AddSeconds(60) < plan.ExpiresUtc
            && !Directory.Exists(plan.Root) && plan.Root.StartsWith(Path.GetFullPath("artifacts/issue83") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Fresh short-intra admission required.");
        foreach (string role in new[] { Environment.ProcessPath, typeof(ReplayIntraShortPilot).Assembly.Location, typeof(CaptureSession).Assembly.Location, typeof(Settings).Assembly.Location, plan.Movie, plan.IdOracle })
            Require(plan.Files.ContainsKey(Path.GetFullPath(role)), "Unbound short-intra role.");
        foreach (var binding in plan.Files) Require(Hash(binding.Key).Equals(binding.Value, StringComparison.OrdinalIgnoreCase), "Short-intra binding changed: " + binding.Key);
        Directory.CreateDirectory(plan.Root); Log.DirectoryPath = plan.Root; Write(Path.Combine(plan.Root, "admitted-plan.json"), plan);
        MediaDependency.RootDirectory = plan.EngineRoot; Require(plan.Files.ContainsKey(MediaDependency.ExecutablePath), "Unbound actual short-intra engine.");
        using var sourceLease = new FileStream(plan.Movie, FileMode.Open, FileAccess.Read, FileShare.Read);
        Require(Convert.ToHexString(SHA256.HashData(sourceLease)).Equals(plan.Files[plan.Movie], StringComparison.OrdinalIgnoreCase), "Protected short-intra source changed.");
        using var runner = new Runner(plan); var results = new List<object>(); double[] control = null; string failure = null;
        try
        {
            uint[] expectedIds = Ids(plan.IdOracle).Take(300).ToArray(); Require(expectedIds.Length == 300, "Source ID oracle is short.");
            foreach (int gop in new[] { 60, 1 })
            {
                string label = "gop" + gop, root = Path.Combine(plan.Root, label); Directory.CreateDirectory(root);
                string movie = Path.Combine(root, "candidate.mp4"), elementary = Path.Combine(root, "coded.h264"), cells = Path.Combine(root, "ids.bin"), stats = Path.Combine(root, "ssim.log");
                var encode = runner.Run(label + "-encode", ["-hide_banner", "-v", "warning", "-xerror", "-err_detect", "explode", "-nostdin", "-y", "-i", plan.Movie,
                    "-map", "0:v:0", "-an", "-vf", "setpts=PTS-STARTPTS", "-frames:v", "300", "-fps_mode", "passthrough", "-enc_time_base:v", "1:48000", "-video_track_timescale", "48000",
                    "-pix_fmt", "yuv420p", "-c:v", "h264_nvenc", "-preset", "p3", "-tune", "ll", "-rc", "cbr", "-b:v", "20M", "-maxrate", "20M", "-bufsize", "40M",
                    "-g", gop.ToString(CultureInfo.InvariantCulture), "-bf", "0", "-aud", "1", "-forced-idr", "1", "-flags", "+cgop", "-movflags", "+faststart", movie]);
                var validation = ReplayExactExport.ReadValidation(runner.Run(label + "-full", ReplayExactExport.DecodeArguments(movie, false, 60)), new(0, 5, 5), 60);
                runner.Run(label + "-ids", ["-hide_banner", "-v", "warning", "-xerror", "-err_detect", "explode", "-nostdin", "-i", movie, "-an",
                    "-vf", "crop=576:48:16:8,scale=24:2:flags=area,format=gray", "-fps_mode", "passthrough", "-f", "rawvideo", cells]);
                Require(Ids(cells).SequenceEqual(expectedIds), "Short-intra source physical-picture identity changed.");
                runner.Run(label + "-layout", ["-hide_banner", "-v", "warning", "-xerror", "-err_detect", "explode", "-nostdin", "-i", movie,
                    "-map", "0:v:0", "-an", "-c:v", "copy", "-bsf:v", "h264_mp4toannexb", "-f", "h264", elementary]);
                object layout = Layout(elementary, gop, runner);
                // The verified engine starts from its protected bin directory.
                // SSIM writes only bounded stdout; this parent persists it in
                // the admitted root. Progress uses the other captured pipe.
                string graph = "[0:v:0]trim=end_frame=300,setpts=PTS-STARTPTS[ref];[1:v:0]setpts=PTS-STARTPTS[test];[ref][test]ssim=stats_file=-:shortest=1:eof_action=endall:repeatlast=0[out]";
                var similarity = runner.Run(label + "-ssim", ["-hide_banner", "-v", "info", "-xerror", "-err_detect", "explode", "-nostdin", "-i", plan.Movie, "-i", movie,
                    "-filter_complex", graph, "-map", "[out]", "-an", "-frames:v", "300", "-fps_mode", "passthrough", "-progress", "pipe:2", "-nostats", "-f", "null", "-"]);
                using (var report = new FileStream(stats, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                    report.Write(System.Text.Encoding.UTF8.GetBytes(similarity.StandardOutput));
                var progress = Regex.Matches(similarity.StandardError, @"(?m)^frame=(\d+)\s*$");
                Require(similarity.StandardError.Contains("progress=end") && progress.Count > 0 && progress[^1].Groups[1].Value == "300",
                    "SSIM did not complete exactly300 paired output pictures.");
                double[] scores = Ssim(stats); if (gop == 60) control = scores;
                var losses = scores.Select((score, n) => new { physicalOrdinal = n, control = control[n], candidate = score, loss = control[n] - score }).ToArray();
                long bytes = new FileInfo(movie).Length;
                var result = new { gop, validation, frames = 300, encodeMilliseconds = encode.ElapsedMilliseconds, layout, containerBytes = bytes,
                    aggregateSsim = scores.Average(), controlAggregateSsim = control.Average(), worstFrame = losses.MaxBy(l => l.loss),
                    frameLosses = losses, sha256 = Hash(movie), noCaptureOrSavedClaim = true };
                Write(Path.Combine(root, "receipt.json"), result); results.Add(result);
                if (gop == 1) Require(bytes <= 18_548_576 && scores.Average() >= control.Average() - .0005 && losses.All(l => l.loss <= .0005),
                    "GOP1 short quality/container-byte gate rejected; no full-length retry justified.");
            }
        }
        catch (Exception error) { failure = error.ToString(); }
        finally
        {
            try
            {
                var files = plan.Preserved.Select(p => new { path = p.Key, unchanged = Hash(p.Key).Equals(p.Value, StringComparison.OrdinalIgnoreCase) }).ToArray();
                bool startupUnchanged = Startup() == plan.Startup; Write(Path.Combine(plan.Root, "preservation.json"), new { files, startupUnchanged, noOverwrite = true });
                if (files.Any(f => !f.unchanged) || !startupUnchanged) failure = (failure ?? "") + " Preserved state differs; do not overwrite it.";
            }
            catch (Exception error) { failure = (failure ?? "") + " Preservation incomplete: " + error.Message; }
            Write(Path.Combine(plan.Root, "outcome.json"), new { status = failure == null ? "passed-short-layout-gate-only" : "failed", error = failure, results,
                runner.Processes, seconds = runner.Clock.Elapsed.TotalSeconds, runner.PeakBytes, runner.PeakParent, runner.PeakChild,
                runner.MaximumGap, runner.GuardSeconds, runner.ProducerAbsent, ordinaryQualification = false });
        }
        Console.WriteLine(failure ?? "Short layout gate passed; no full-length/capture/performance/adoption claim."); return failure == null ? 0 : 1;
    }
}
