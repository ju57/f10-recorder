using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace F10Recorder;

// Rejection/feasibility experiment only. This never changes the product exporter,
// decodes video, publishes an MP4, acquires audio or changes recorder state.
internal static class ReplayAudioShardPilot
{
    static readonly JsonSerializerOptions Json = new(WorkerProtocol.Json) { WriteIndented = true };
    sealed record Trial(string Label, int Seconds, int Lanes, bool Synthetic);
    sealed record Plan(string Kind, string Root, string EngineRoot, string SourcePcm, Dictionary<string, string> Files,
        Dictionary<string, string> Preserved, string Startup, Trial[] Cases, string[] RetainedRoots, long RetainedBytes,
        DateTime CreatedUtc, DateTime ExpiresUtc, object Bounds, string Effects, string Claim);
    sealed record Lane(int Number, long FirstFrame, long EndFrame, long ReadStart, long ReadEnd, string Input, string Output);
    [StructLayout(LayoutKind.Sequential)]
    struct Io { internal ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetProcessIoCounters(IntPtr process, out Io io);
    sealed record Usage(double CpuMilliseconds, ulong ReadBytes, ulong WriteBytes);
    static Usage Resources()
    {
        using var process = Process.GetCurrentProcess();
        Require(GetProcessIoCounters(process.Handle, out var io), "Parent audio I/O observation unavailable.");
        return new(process.TotalProcessorTime.TotalMilliseconds, io.ReadBytes, io.WriteBytes);
    }
    static Usage Difference(Usage before, Usage after) => new(after.CpuMilliseconds - before.CpuMilliseconds,
        checked(after.ReadBytes - before.ReadBytes), checked(after.WriteBytes - before.WriteBytes));
    static void Require(bool value, string reason) { if (!value) throw new IOException(reason); }
    static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    static string Startup() { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"); return key?.GetValue("F10Recorder") as string; }
    static void Write(string path, object value)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json); Require(bytes.Length <= 2_097_152, "Audio pilot report bound.");
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read); stream.Write(bytes);
    }
    internal static int Prepare(string path, string root, string engineRoot, string productPlan)
    {
        root = Path.GetFullPath(root); string taskRoot = Path.GetFullPath("artifacts/issue83");
        Require(root.StartsWith(taskRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !Directory.Exists(root) && !File.Exists(path), "Fresh task-owned audio pilot paths required.");
        using var previous = JsonDocument.Parse(File.ReadAllText(productPlan));
        string product = previous.RootElement.GetProperty("Root").GetString(), reference = previous.RootElement.GetProperty("Reference").GetString();
        using var outcome = JsonDocument.Parse(File.ReadAllText(Path.Combine(product, "outcome.json")));
        Require(outcome.RootElement.GetProperty("status").GetString() == "passed" && outcome.RootElement.GetProperty("ProducerAbsent").GetBoolean(), "Closed product provenance required.");
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Bind(string file) { file = Path.GetFullPath(file); files.Add(file, Hash(file)); }
        string source = Path.Combine(reference, "original-request-source-audio.stdout.bin");
        Require(new FileInfo(source).Length == 300L * 48000 * 8, "Exact retained stereo PCM extent required.");
        Bind(source); Bind(productPlan); Bind(Path.Combine(product, "outcome.json")); Bind(Path.Combine(product, "reference", "receipt.json"));
        Bind(Path.Combine(reference, "admitted-plan.json"));
        MediaDependency.RootDirectory = Path.GetFullPath(engineRoot); Bind(MediaDependency.ResolveExecutable()); Bind(Environment.ProcessPath);
        string build = Path.GetDirectoryName(typeof(ReplayAudioShardPilot).Assembly.Location);
        foreach (string name in new[] { "F10Recorder.WorkerChecks.dll", "F10Recorder.Windows.dll", "F10Recorder.Core.dll", "F10Recorder.WorkerChecks.deps.json", "F10Recorder.WorkerChecks.runtimeconfig.json" }) Bind(Path.Combine(build, name));
        foreach (string file in new[] { "projects/F10Recorder.WorkerChecks/ReplayAudioShardPilot.cs", "projects/F10Recorder.WorkerChecks/Program.cs",
            "projects/F10Recorder.Windows/ReplayExportProcess.cs", "tests/replay-audio-shard-pilot.ps1" }) Bind(file);
        var preserved = previous.RootElement.GetProperty("Preserved").Deserialize<Dictionary<string, string>>(Json);
        Require(preserved.All(p => Hash(p.Key).Equals(p.Value, StringComparison.OrdinalIgnoreCase))
            && Startup() == previous.RootElement.GetProperty("Startup").GetString(), "Preserved state changed; do not overwrite it.");
        string[] retained = new[] { taskRoot }.Concat(Directory.GetDirectories(Path.GetTempPath(), "f10-issue83-*")).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        long retainedBytes = ReplayStorage.Bytes(ReplayStorage.Inventory(retained, new ReplayFiles(), 20000, 4096));
        Require(retainedBytes + 1_000_000_000 + 16_000_000 < 22_000_000_000L, "Fresh audio forward envelope does not fit.");
        var now = DateTime.UtcNow;
        Write(path, new Plan("issue83-audio-shard-feasibility-v1", root, MediaDependency.RootDirectory, source, files, preserved, Startup(),
            [new("one-synthetic", 1, 2, true), new("five-synthetic", 5, 4, true), new("reference-retained", 300, 8, false)], retained, retainedBytes, now, now.AddMinutes(30),
            new { mediaChildren = 23, maximumConcurrentChildren = 8, seconds = 120, outerSeconds = 160, childSeconds = 30,
                parentWorkingSet = 536870912, childWorkingSet = 268435456, groupWorkingSet = 2147483648L,
                cap = 1_000_000_000, stop = 800_000_000, closure = 200_000_000, minimumFree = 60_000_000_000L,
                aggregate = 22_000_000_000L, outsideGrowthReserve = 16_000_000, files = 256, directories = 16, maximumGuardGapMs = 500,
                prerollFrames = 32, lookaheadFrames = 2, perChannelSnrLossDb = .5, seamRmsFactor = 1.2, seamRmsAllowance = .00001, seamPeakAllowance = .001,
                maximumLagSamples = 4, minimumEnergeticCorrelation = .97 },
            "Audio-only:3 serial AAC controls,14 partition encoders (2/4/8 for1/5/300s respectively),6 fresh complete audio decodes. Generate two short distinct-stereo PCM fixtures with transients on both sides of every splice; copy genuine PCM contexts; merge complete ADTS packets. No playback/capture/video/MP4/Saved/worker/UI/catalog/startup/NVIDIA/install action.",
            "Encoder feasibility only. Retained source PCM is already decoded oracle input, not a save-path optimization or capture predecode. Its production decoding/storage cost remains pending.32-frame context is a hypothesis; exact counts/alignment and global/every-seam per-channel quality must pass against serial ADTS controls. All context-copy/auth/start/encode/merge/decode costs included in parallel cohort wall/CPU/I/O. No MP4 priming/publication equivalence or3/5second claim."));
        Console.WriteLine(Hash(path)); return 0;
    }
    sealed class Harness(Plan plan) : IDisposable
    {
        readonly object gate = new();
        readonly Dictionary<int, (long Bytes, long At, bool Live)> active = [];
        readonly Native.ProcessJob job = new();
        internal readonly Stopwatch Clock = Stopwatch.StartNew();
        internal readonly List<object> Processes = [];
        internal bool ProducerAbsent = true;
        internal long PeakBytes, PeakParent, PeakGroup;
        internal double MaximumGap, GuardSeconds;
        int children; long lastGuard, lastScan; string aborted;
        internal void Guard(Process owned = null)
        {
            lock (gate)
            {
                Require(aborted == null, "Audio cohort cancelled: " + aborted);
                long now = Stopwatch.GetTimestamp();
                if (lastGuard != 0) MaximumGap = Math.Max(MaximumGap, Stopwatch.GetElapsedTime(lastGuard, now).TotalMilliseconds);
                lastGuard = now;
                Require(Clock.Elapsed.TotalSeconds < 120 && MaximumGap <= 500, "Audio pilot time/guard bound.");
                if (owned != null)
                {
                    if (owned.HasExited) active[owned.Id] = (0, now, false);
                    else
                    {
                        bool observed = ReplayExportProcess.TryWorkingSet(owned, out long bytes);
                        Require(observed || owned.HasExited, "Live audio child memory query failed.");
                        Require(bytes < 268435456, "Audio child memory bound.");
                        active[owned.Id] = (bytes, now, observed);
                    }
                }
                long group = 0;
                foreach (var sample in active.Values.Where(s => s.Live))
                { Require(Stopwatch.GetElapsedTime(sample.At, now).TotalMilliseconds <= 500, "Audio group memory sample became stale."); group = checked(group + sample.Bytes); }
                PeakGroup = Math.Max(PeakGroup, group); Require(group < 2147483648L && active.Count <= 8, "Audio group resource bound.");
                if (lastScan != 0 && Stopwatch.GetElapsedTime(lastScan, now).TotalMilliseconds < 100) return;
                lastScan = now; long at = Stopwatch.GetTimestamp();
                using var parent = Process.GetCurrentProcess();
                Require(ReplayExportProcess.TryWorkingSet(parent, out long ram) && ram < 536870912 && parent.HandleCount < 1536, "Audio parent resource bound.");
                PeakParent = Math.Max(PeakParent, ram);
                long total = ReplayStorage.Bytes(ReplayStorage.Inventory([plan.Root], new ReplayFiles(), 256, 16));
                PeakBytes = Math.Max(PeakBytes, total);
                Require(total < 800_000_000 && plan.RetainedBytes + total + 16_000_000 < 22_000_000_000L
                    && ReplayStorage.ObserveCapacity(plan.Root).AvailableBytes >= 60_000_000_000L, "Audio pilot storage bound.");
                GuardSeconds += Stopwatch.GetElapsedTime(at).TotalSeconds;
            }
        }
        internal ReplayExportProcessReceipt Run(string label, IReadOnlyList<string> args)
        {
            Process owned = null; int ownedId = 0; ReplayExportProcessReceipt receipt = null;
            try
            {
                Guard(); lock (gate) Require(++children <= 23, "Audio media child ceiling.");
                receipt = ReplayExportProcess.Run(MediaDependency.ExecutablePath, args, plan.Root, job,
                    started: child => { owned = child; ownedId = child.Id; lock (gate) active.Add(ownedId, (0, Stopwatch.GetTimestamp(), true)); Guard(child); },
                    timeoutMilliseconds: 30000, checkStorage: () => Guard(owned));
                lock (gate)
                {
                    ProducerAbsent &= receipt.ProducerAbsent;
                    Processes.Add(new { label, receipt.ProcessId, receipt.ProcessStartedUtc, receipt.Success, receipt.ProducerAbsent,
                        receipt.CpuMilliseconds, receipt.ReadBytes, receipt.WriteBytes, receipt.SampledPeakWorkingSetBytes, receipt.ElapsedMilliseconds, receipt.ResourceError });
                }
                Write(Path.Combine(plan.Root, label + ".process.json"), new { args, receipt });
                Require(receipt.Success && receipt.ResourceError == null, "Audio child/evidence failed: " + label + ": " + receipt.Error);
                return receipt;
            }
            catch (Exception error) { lock (gate) aborted ??= error.Message; throw; }
            finally { lock (gate) { if (ownedId != 0) active.Remove(ownedId); if (receipt == null && owned != null) ProducerAbsent = false; } }
        }
        public void Dispose() => job.Dispose();
    }
    static void CopyPcm(string source, string output, long first, long end, Harness h)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read); input.Position = checked(first * 8);
        using var target = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        long remaining = checked((end - first) * 8); byte[] buffer = new byte[131072];
        while (remaining > 0) { h.Guard(); int count = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining)); Require(count > 0, "PCM context truncated."); target.Write(buffer, 0, count); remaining -= count; }
    }
    static void Synthetic(string path, int seconds, int lanes, Harness h)
    {
        long samples = seconds * 48000L, frames = (samples + 1023) / 1024;
        var centers = Enumerable.Range(1, lanes - 1).SelectMany(n => new[] { (frames * n / lanes) * 1024 - 256, (frames * n / lanes) * 1024 + 256 }).ToArray();
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read); byte[] bytes = new byte[8192 * 8];
        for (long start = 0; start < samples; start += 8192)
        {
            h.Guard(); int count = (int)Math.Min(8192, samples - start); var values = MemoryMarshal.Cast<byte, float>(bytes.AsSpan());
            for (int n = 0; n < count; n++)
            {
                long sample = start + n; double t = sample / 48000d, pulse = 0;
                foreach (long center in centers) { double d = (sample - center) / 28d; if (Math.Abs(d) < 4) pulse += .08 * Math.Exp(-d * d); }
                values[2 * n] = (float)(.08 * Math.Sin(2 * Math.PI * (233 * t + 17 * t * t)) + .012 * Math.Cos(2 * Math.PI * 1033 * t) + pulse);
                values[2 * n + 1] = (float)(.06 * Math.Sin(2 * Math.PI * (467 * t + 23 * t * t)) + .015 * Math.Sin(2 * Math.PI * 1777 * t) - pulse * .7);
            }
            file.Write(bytes.AsSpan(0, count * 8));
        }
    }
    static IReadOnlyList<ReadOnlyMemory<byte>> Adts(string path)
    {
        Require(new FileInfo(path).Length is > 0 and <= 16_000_000, "ADTS file bound.");
        byte[] bytes = File.ReadAllBytes(path); var frames = new List<ReadOnlyMemory<byte>>();
        for (int offset = 0; offset < bytes.Length;)
        {
            Require(bytes.Length - offset >= 7, "ADTS header truncated."); var b = bytes.AsSpan(offset);
            Require(b[0] == 255 && b[1] == 0xf1 && (b[2] >> 6) == 1 && ((b[2] >> 2) & 15) == 3
                && (((b[2] & 1) << 2) | (b[3] >> 6)) == 2 && (b[6] & 3) == 0, "Expected complete MPEG4 AAC-LC48k stereo frame.");
            int size = ((b[3] & 3) << 11) | (b[4] << 3) | (b[5] >> 5);
            Require(size >= 8 && size <= b.Length && frames.Count < 20000, "ADTS coded frame extent bound.");
            frames.Add(bytes.AsMemory(offset, size)); offset += size;
        }
        return frames;
    }
    static string[] Encode(string source, string output) => ["-hide_banner", "-v", "warning", "-xerror", "-err_detect", "explode", "-nostdin", "-y",
        "-f", "f32le", "-ar", "48000", "-ac", "2", "-i", source, "-map", "0:a:0", "-c:a", "aac", "-b:a", "192k", "-aac_coder", "twoloop", "-f", "adts", output];
    static string[] Decode(string source, string output) => ["-hide_banner", "-v", "warning", "-xerror", "-err_detect", "explode", "-nostdin", "-y",
        "-i", source, "-map", "0:a:0", "-c:a", "pcm_f32le", "-f", "f32le", output];
    sealed record Error(double[] Snr, double[] Rms, double[] Peak);
    sealed record Alignment(int Channel, bool Energetic, int Lag, double? Correlation);
    sealed record Seam(long AtSample, long First, long Count, Error Serial, Error Parallel, Alignment[] Alignment);
    static Alignment[] Phase(string source, string output, long first, long count, Harness h)
    {
        using var input = File.OpenRead(source); using var decoded = File.OpenRead(output);
        input.Position = first * 8; decoded.Position = (1024 + first - 8) * 8;
        byte[] a = new byte[checked((int)count * 8)], b = new byte[checked(((int)count + 16) * 8)];
        input.ReadExactly(a); decoded.ReadExactly(b);
        var x = MemoryMarshal.Cast<byte, float>(a.AsSpan()); var y = MemoryMarshal.Cast<byte, float>(b.AsSpan());
        var results = new List<Alignment>();
        for (int ch = 0; ch < 2; ch++)
        {
            double best = -1, signal = 0; int lag = 0;
            for (int shift = -8; shift <= 8; shift++)
            {
                h.Guard(); double cross = 0, a2 = 0, b2 = 0;
                for (int n = 0; n < count; n++)
                { double left = x[n * 2 + ch], right = y[(n + 8 + shift) * 2 + ch]; cross += left * right; a2 += left * left; b2 += right * right; }
                signal = a2; double correlation = cross / Math.Sqrt(Math.Max(a2 * b2, 1e-60));
                if (correlation > best) { best = correlation; lag = shift; }
            }
            results.Add(new(ch, signal > 1e-20, lag, signal > 1e-20 ? best : null));
        }
        return results.ToArray();
    }
    static Error Compare(string source, string output, long first, long count, Harness h, int shift = 0, bool swap = false)
    {
        using var input = File.OpenRead(source); using var decoded = File.OpenRead(output);
        input.Position = first * 8; decoded.Position = (1024 + first + shift) * 8;
        double[] power = [0, 0], error = [0, 0], peak = [0, 0]; byte[] a = new byte[65536], b = new byte[65536];
        for (long n = 0; n < count;)
        {
            h.Guard(); int samples = (int)Math.Min(a.Length / 8, count - n), size = samples * 8;
            input.ReadExactly(a.AsSpan(0, size)); decoded.ReadExactly(b.AsSpan(0, size));
            var x = MemoryMarshal.Cast<byte, float>(a.AsSpan(0, size)); var y = MemoryMarshal.Cast<byte, float>(b.AsSpan(0, size));
            for (int s = 0; s < samples; s++) for (int ch = 0; ch < 2; ch++)
            {
                double value = x[s * 2 + ch], delta = value - y[s * 2 + (swap ? 1 - ch : ch)];
                Require(double.IsFinite(value) && double.IsFinite(delta), "Nonfinite PCM comparison.");
                power[ch] += value * value; error[ch] += delta * delta; peak[ch] = Math.Max(peak[ch], Math.Abs(delta));
            }
            n += samples;
        }
        return new(Enumerable.Range(0, 2).Select(ch => 10 * Math.Log10(Math.Max(power[ch], 1e-30) / Math.Max(error[ch], 1e-30))).ToArray(),
            error.Select(e => Math.Sqrt(e / count)).ToArray(), peak);
    }
    static void Quality(Error serial, Error parallel, bool local)
    {
        for (int ch = 0; ch < 2; ch++)
            Require(parallel.Snr[ch] >= serial.Snr[ch] - .5 && (!local || parallel.Rms[ch] <= serial.Rms[ch] * 1.2 + .00001
                && parallel.Peak[ch] <= serial.Peak[ch] + .001), "Parallel AAC seam/global quality regressed on channel" + ch + ".");
    }
    internal static int Run(string path, string expected)
    {
        Require(new FileInfo(path).Length < 65536 && Hash(path).Equals(expected, StringComparison.OrdinalIgnoreCase), "Frozen audio plan changed.");
        var plan = JsonSerializer.Deserialize<Plan>(File.ReadAllText(path), Json);
        Require(plan.Kind == "issue83-audio-shard-feasibility-v1" && DateTime.UtcNow >= plan.CreatedUtc && DateTime.UtcNow.AddSeconds(160) < plan.ExpiresUtc
            && !Directory.Exists(plan.Root) && plan.Root.StartsWith(Path.GetFullPath("artifacts/issue83") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Fresh audio admission required.");
        Require(plan.Cases.Select(t => (t.Seconds, t.Lanes, t.Synthetic)).SequenceEqual(new[] { (1, 2, true), (5, 4, true), (300, 8, false) }), "Frozen23-child matrix required.");
        foreach (string role in new[] { Environment.ProcessPath, typeof(ReplayAudioShardPilot).Assembly.Location, typeof(CaptureSession).Assembly.Location, typeof(Settings).Assembly.Location, plan.SourcePcm })
            Require(plan.Files.ContainsKey(Path.GetFullPath(role)), "Unbound audio runtime/input role.");
        foreach (var binding in plan.Files) Require(Hash(binding.Key).Equals(binding.Value, StringComparison.OrdinalIgnoreCase), "Audio binding changed: " + binding.Key);
        Directory.CreateDirectory(plan.Root); Log.DirectoryPath = plan.Root; Write(Path.Combine(plan.Root, "admitted-plan.json"), plan);
        MediaDependency.RootDirectory = plan.EngineRoot; Require(plan.Files.ContainsKey(MediaDependency.ExecutablePath), "Unbound actual engine.");
        using var protectedSource = new FileStream(plan.SourcePcm, FileMode.Open, FileAccess.Read, FileShare.Read);
        Require(Convert.ToHexString(SHA256.HashData(protectedSource)).Equals(plan.Files[plan.SourcePcm], StringComparison.OrdinalIgnoreCase), "Protected PCM source changed.");
        using var h = new Harness(plan); var results = new List<object>(); string failure = null;
        try
        {
            foreach (var trial in plan.Cases)
            {
                h.Guard(); string root = Path.Combine(plan.Root, trial.Label); Directory.CreateDirectory(root);
                long samples = trial.Seconds * 48000L, frameCount = (samples + 1023) / 1024;
                string source = trial.Synthetic ? Path.Combine(root, "source.f32") : plan.SourcePcm;
                if (trial.Synthetic) Synthetic(source, trial.Seconds, trial.Lanes, h);
                string serial = Path.Combine(root, "serial.aac"), serialPcm = Path.Combine(root, "serial.f32");
                var serialResources = Resources(); long serialAt = Stopwatch.GetTimestamp(); h.Run(trial.Label + "-serial", Encode(source, serial));
                double serialEncodeSeconds = Stopwatch.GetElapsedTime(serialAt).TotalSeconds;
                var serialParent = Difference(serialResources, Resources());
                var serialFrames = Adts(serial); Require(serialFrames.Count == frameCount + 1, "Serial ADTS priming/frame count differs from the1024-sample hypothesis.");
                h.Run(trial.Label + "-serial-decode", Decode(serial, serialPcm));
                Require(new FileInfo(serialPcm).Length == (frameCount + 1) * 1024 * 8, "Serial raw AAC decoder priming/end-padding differs.");
                var parallelResources = Resources(); long parallelAt = Stopwatch.GetTimestamp(); var lanes = new List<Lane>();
                for (int lane = 0; lane < trial.Lanes; lane++)
                {
                    long first = frameCount * lane / trial.Lanes, end = frameCount * (lane + 1) / trial.Lanes;
                    long readStart = Math.Max(0, first - 32) * 1024, readEnd = Math.Min(samples, (end + 2) * 1024);
                    string input = Path.Combine(root, "lane-" + lane + ".f32"), output = Path.Combine(root, "lane-" + lane + ".aac");
                    CopyPcm(source, input, readStart, readEnd, h); lanes.Add(new(lane, first, end, readStart, readEnd, input, output));
                }
                Task.WaitAll(lanes.Select(lane => Task.Run(() => h.Run(trial.Label + "-lane-" + lane.Number, Encode(lane.Input, lane.Output)))).ToArray());
                double parallelEncodeAndContextsSeconds = Stopwatch.GetElapsedTime(parallelAt).TotalSeconds;
                var parallelEncodeParent = Difference(parallelResources, Resources());
                string joined = Path.Combine(root, "joined.aac"), joinedPcm = Path.Combine(root, "joined.f32"); var selected = new List<object>();
                using (var target = new FileStream(joined, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                    foreach (var lane in lanes)
                    {
                        h.Guard(); var packets = Adts(lane.Output);
                        long expectedPackets = (lane.ReadEnd - lane.ReadStart + 1023) / 1024 + 1;
                        Require(packets.Count == expectedPackets, "Partition ADTS priming/frame count differs.");
                        int first = lane.Number == 0 ? 0 : checked((int)(lane.FirstFrame - lane.ReadStart / 1024 + 1));
                        int end = checked((int)(lane.EndFrame - lane.ReadStart / 1024 + 1));
                        Require(first >= 0 && end <= packets.Count && first < end, "Partition packet selection escaped genuine context.");
                        for (int n = first; n < end; n++) target.Write(packets[n].Span);
                        selected.Add(new { lane, encodedPackets = packets.Count, firstSelectedPacket = first, endSelectedPacket = end, sha256 = Hash(lane.Output) });
                    }
                Require(Adts(joined).Count == frameCount + 1, "Joined AAC frame count changed.");
                h.Run(trial.Label + "-joined-decode", Decode(joined, joinedPcm));
                double parallelThroughFreshDecodeSeconds = Stopwatch.GetElapsedTime(parallelAt).TotalSeconds;
                var parallelThroughFreshDecodeParent = Difference(parallelResources, Resources());
                Require(new FileInfo(joinedPcm).Length == new FileInfo(serialPcm).Length, "Joined AAC decoded count differs from serial.");
                Write(Path.Combine(root, "construction.json"), new { trial, samples, frameCount, selected,
                    serialEncodeSeconds, parallelEncodeAndContextsSeconds, parallelThroughFreshDecodeSeconds,
                    serialParent, parallelEncodeParent, parallelThroughFreshDecodeParent,
                    serialRawBytes = new FileInfo(serialPcm).Length, joinedRawBytes = new FileInfo(joinedPcm).Length });
                var serialError = Compare(source, serialPcm, 0, samples, h); var joinedError = Compare(source, joinedPcm, 0, samples, h);
                var seams = new List<Seam>();
                foreach (var lane in lanes.Skip(1))
                    foreach (int window in new[] { 65536, 4096 })
                    {
                        long first = Math.Max(0, lane.FirstFrame * 1024 - window / 2), count = Math.Min(window, samples - first);
                        var a = Compare(source, serialPcm, first, count, h); var b = Compare(source, joinedPcm, first, count, h);
                        long phaseFirst = Math.Max(8, first), phaseCount = Math.Min(count, samples - phaseFirst - 8);
                        seams.Add(new(lane.FirstFrame * 1024, first, count, a, b, Phase(source, joinedPcm, phaseFirst, phaseCount, h)));
                    }
                var alignment = new List<Alignment[]>();
                foreach (long first in new[] { 8L, Math.Max(8, samples / 2 - 32768), Math.Max(8, samples - 65544) })
                    alignment.Add(Phase(source, joinedPcm, first, Math.Min(65536, samples - first - 8), h));
                Write(Path.Combine(root, "quality.json"), new { serial = serialError, parallel = joinedError, seams, alignment });
                Quality(serialError, joinedError, false);
                foreach (var seam in seams) Quality(seam.Serial, seam.Parallel, seam.Count <= 4096);
                foreach (var phase in alignment.SelectMany(p => p).Concat(seams.SelectMany(s => s.Alignment)))
                    Require(!phase.Energetic || Math.Abs(phase.Lag) <= 4 && phase.Correlation >= .97, "Parallel audio sample alignment/phase regressed.");
                if (trial.Synthetic)
                    foreach (var control in new[] { (Shift: 16, Swap: false), (Shift: 0, Swap: true) })
                    {
                        var changed = Compare(source, joinedPcm, 0, samples, h, control.Shift, control.Swap);
                        Require(Enumerable.Range(0, 2).Any(ch => changed.Snr[ch] < serialError.Snr[ch] - .5), "Audio quality control failed to reject shifted/swapped content.");
                    }
                var result = new { trial, serialEncodeSeconds, parallelEncodeAndContextsSeconds, parallelThroughFreshDecodeSeconds,
                    sourceSha256 = Hash(source), serialPackets = serialFrames.Count, selected, serial = serialError, parallel = joinedError, seams,
                    decodedRawSamples = new FileInfo(joinedPcm).Length / 8, comparedSamples = samples, analysisOnlyPrimingSamples = 1024,
                    limitation = "Raw AAC feasibility only; PCM analysis excludes confirmed serial priming. No MP4 metadata/publication equivalence or product latency claim." };
                Write(Path.Combine(root, "receipt.json"), result); results.Add(result);
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
            catch (Exception error) { failure = (failure ?? "") + " Preservation verification incomplete: " + error.Message; }
            Write(Path.Combine(plan.Root, "outcome.json"), new { status = failure == null ? "passed-audio-feasibility-only" : "failed", error = failure, results,
                h.Processes, seconds = h.Clock.Elapsed.TotalSeconds, h.PeakBytes, h.PeakParent, h.PeakGroup, h.MaximumGap, h.GuardSeconds, h.ProducerAbsent,
                noMp4PublicationOrSaved = true, ordinaryQualification = false });
        }
        Console.WriteLine(failure ?? "Audio-only feasibility passed; no product adoption or MP4/Saved qualification."); return failure == null ? 0 : 1;
    }
}
