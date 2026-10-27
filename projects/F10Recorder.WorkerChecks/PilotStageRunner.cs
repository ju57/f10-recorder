using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace F10Recorder;

internal sealed record PilotStagePlan(string StageId, string Mode, string Root, string InputPath, string InputSha256,
    string InputIdentity, string ParentIdentity, string HostPath, string HostSha256, string AssemblyPath,
    SortedDictionary<string, string> BuildSha256, string SourceSha256, string Cutoff, int DeadlineMs);
internal sealed record PilotStageRequest(PilotStagePlan Plan, string PlanSha256, string[] Arguments);
internal sealed record PilotStageCleanup(bool? ProducerAbsent, bool Verified, string Origin);
internal sealed record PilotStagePublication(string Path, string Sha256);

internal sealed class PilotStageResult
{
    public string Kind { get; init; } = "one-nonmedia-stage";
    public string Root { get; set; }
    public string Status { get; set; } = "rejected";
    public string Reason { get; set; }
    public bool Success { get; set; }
    public bool ExecutionEnabled => false;
    public bool NextStageEnabled => false;
    public bool PollReserveHeld { get; set; } = true;
    public int? Pid { get; set; }
    public DateTime? ProcessStartedUtc { get; set; }
    public string OwnedHandle { get; set; }
    public int? ExitCode { get; set; }
    public bool ActualExitObserved { get; set; }
    public bool CleanupVerified { get; set; }
    public bool? ProducerAbsent { get; set; }
    public string CleanupOrigin { get; set; }
    public bool ReceiptCommitted { get; set; }
    public string ReceiptPath { get; set; }
    public string ReceiptSha256 { get; set; }
    public int CapturedBytes { get; set; }
    public bool Truncated { get; set; }
    public bool CaptureComplete { get; set; }
    public bool RequiredProductMatches { get; set; }
    public bool PartialMatches { get; set; }
    public long ElapsedMilliseconds { get; set; }
    public string RootIdentity { get; set; }
    public DateTime StartedUtc { get; init; } = DateTime.UtcNow;
    public DateTime CompletedUtc { get; set; }
    public PilotStageRequest Request { get; set; }
    public PilotStorageSnapshot Before { get; set; }
    public PilotStorageSnapshot AfterClosedPreReceipt { get; set; }
}

// Approved OS/filesystem test boundary, never selected by caller JSON.
internal class PilotStageIo
{
    internal virtual Process StartApprovedProcess(ProcessStartInfo info) => Process.Start(info) ?? throw new IOException("Stage process did not start.");
    internal virtual PilotStageCleanup StopAndObserveOwnedProcess(Process process, bool stop)
    {
        if (stop && !process.HasExited) process.Kill();
        bool exited = process.WaitForExit(3000);
        return new(exited ? true : null, exited, "native-process-handle");
    }
    internal virtual PilotStagePublication PublishEvidence(string root, byte[] bytes)
    {
        if (bytes.Length > 65536) throw new IOException("Stage receipt exceeds64KiB.");
        string pending = Path.Combine(root, "receipt.pending.json"), final = Path.Combine(root, "receipt.json");
        using (var file = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { file.Write(bytes); file.Flush(true); }
        File.Move(pending, final, false);
        using var published = new FileStream(final, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (published.Length > 65536) throw new IOException("Published receipt exceeds64KiB.");
        byte[] read = new byte[published.Length]; published.ReadExactly(read);
        if (!read.SequenceEqual(bytes)) throw new IOException("Stage receipt readback differs.");
        return new(final, Convert.ToHexString(SHA256.HashData(read)));
    }
}

internal static class PilotStageRunner
{
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true, MaxDepth = 16, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    static readonly string[] BuildNames = ["F10Recorder.WorkerChecks.dll", "F10Recorder.WorkerChecks.deps.json", "F10Recorder.WorkerChecks.runtimeconfig.json", "F10Recorder.Windows.dll", "F10Recorder.Core.dll"];
    internal static string Serialize(object value)
    {
        string json = JsonSerializer.Serialize(value, Json);
        if (Encoding.UTF8.GetByteCount(json) > 131072) throw new IOException("Stage/control JSON exceeds128KiB.");
        return json;
    }
    internal static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value));
    static string FileHash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
    static void Require(bool condition, string reason) { if (!condition) throw new IOException(reason); }
    static bool Sha(string value) => value?.Length == 64 && value.All(Uri.IsHexDigit);
    static string[] Arguments(PilotStagePlan plan) => [plan.AssemblyPath, "--pilot-stage-fixture", Path.Combine(plan.Root, "fixture.json")];
    internal static string Digest(PilotStagePlan plan) => Hash(Encoding.UTF8.GetBytes(Serialize(plan with { BuildSha256 = plan.BuildSha256 == null ? null : new(plan.BuildSha256, StringComparer.Ordinal) })));
    internal static object Preview(PilotStageRequest request) => new { kind = "one-stage-preview", execution_enabled = false,
        host = request?.Plan?.HostPath, arguments = request?.Plan?.Root == null ? null : Arguments(request.Plan),
        root = request?.Plan?.Root, stage = request?.Plan?.StageId, required = new[] { "reviewed host/build/input hashes", "exact plan/argv", "canonical fresh root and parent identity", "explicit nonmedia execution" } };
    internal static PilotStageRequest BindFixture(string root, string input, string mode)
    {
        string host = Environment.ProcessPath, assembly = typeof(PilotStageRunner).Assembly.Location;
        var builds = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (string name in BuildNames) builds.Add(name, FileHash(Path.Combine(Path.GetDirectoryName(assembly), name)));
        var plan = new PilotStagePlan("fixture-" + mode, mode, Path.GetFullPath(root), Path.GetFullPath(input), FileHash(input), Native.DirectoryIdentity(input),
            Native.DirectoryIdentity(Path.GetDirectoryName(root)), host, FileHash(host), assembly, builds, new string('b', 64), "302/1", 2000);
        return new(plan, Digest(plan), Arguments(plan));
    }

    sealed class IdentityLeases : IDisposable
    {
        readonly List<IDisposable> held = [];
        readonly HashSet<string> parents = new(StringComparer.OrdinalIgnoreCase);
        internal FileStream Pin(string path, string expected, long maximumBytes = long.MaxValue)
        {
            Require(Path.IsPathFullyQualified(path) && path == Path.GetFullPath(path) && Sha(expected), "Missing or noncanonical file identity.");
            for (string parent = Path.GetDirectoryName(path); parent != null; parent = Path.GetDirectoryName(parent))
            {
                if (!parents.Add(parent)) continue;
                Require((File.GetAttributes(parent) & FileAttributes.ReparsePoint) == 0, "Identity ancestry is a reparse point.");
                var anchor = Native.AnchorDirectory(parent); held.Add(anchor);
                Require(string.Equals(Native.FinalDirectoryPath(anchor).TrimEnd('\\'), parent.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase), "Identity ancestry is an unsupported alias.");
            }
            Require((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0, "Identity is not an ordinary file.");
            var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); held.Add(file);
            Require(file.Length <= maximumBytes, "Identity file exceeds its admitted byte bound.");
            Require(string.Equals(Native.FinalDirectoryPath(file.SafeFileHandle), path, StringComparison.OrdinalIgnoreCase), "Identity file is an unsupported alias.");
            Require(Convert.ToHexString(SHA256.HashData(file)).Equals(expected, StringComparison.OrdinalIgnoreCase), "Expected file SHA256 differs.");
            file.Position = 0;
            return file;
        }
        public void Dispose() { for (int i = held.Count - 1; i >= 0; i--) held[i].Dispose(); }
    }

    static void WriteNew(string path, byte[] bytes)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        file.Write(bytes); file.Flush(true);
    }
    static void Validate(PilotStageRequest request)
    {
        var plan = request?.Plan ?? throw new IOException("Stage plan required.");
        Require(plan.Mode is "success" or "missing-product" or "exit7" or "wait" or "flood", "Unknown nonmedia fixture mode.");
        Require(plan.StageId?.Length is > 0 and <= 64 && plan.StageId.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'), "Stage identity required.");
        Require(Sha(request.PlanSha256) && Digest(plan) == request.PlanSha256 && request.Arguments != null && request.Arguments.SequenceEqual(Arguments(plan), StringComparer.Ordinal), "Plan or exact argument binding differs.");
        Require(plan.HostPath == Environment.ProcessPath && Path.GetFileName(plan.HostPath).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase), "Only the current reviewed dotnet host is allowed.");
        Require(plan.AssemblyPath == typeof(PilotStageRunner).Assembly.Location && plan.BuildSha256 != null && plan.BuildSha256.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(BuildNames), "Only the current reviewed fixture build is allowed.");
        Require(Path.IsPathFullyQualified(plan.Root) && plan.Root == Path.GetFullPath(plan.Root) && !Path.EndsInDirectorySeparator(plan.Root), "Stage root must be canonical.");
        Require(Sha(plan.SourceSha256) && !string.IsNullOrWhiteSpace(plan.Cutoff) && plan.Cutoff.Length <= 256 && plan.DeadlineMs is >= 1000 and <= 2000, "Missing source/cutoff/deadline binding.");
        string relative = Path.GetRelativePath(PilotStorageObservation.Artifacts, plan.InputPath);
        Require(!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar) && Path.GetExtension(plan.InputPath) == ".txt", "Input must be an explicit tiny task text artifact.");
    }

    internal static PilotStageResult Run(PilotStageRequest request, CancellationToken token, PilotStageIo io = null)
    {
        var result = new PilotStageResult { Root = request?.Plan?.Root, Request = request };
        if (token.IsCancellationRequested) { result.Status = "cancelled"; result.Reason = "cancelled-before-launch"; result.CompletedUtc = DateTime.UtcNow; return result; }
        io ??= new PilotStageIo();
        Process process = null;
        PilotStorageObservation owner = null;
        bool launchAttempted = false, cleanupAttempted = false, valid = false;
        var elapsed = Stopwatch.StartNew();
        using var leases = new IdentityLeases();
        try
        {
            Validate(request);
            var plan = request.Plan;
            leases.Pin(plan.HostPath, plan.HostSha256);
            foreach (string name in BuildNames) leases.Pin(Path.Combine(Path.GetDirectoryName(plan.AssemblyPath), name), plan.BuildSha256[name]);
            var input = leases.Pin(plan.InputPath, plan.InputSha256, 4096);
            Require(input.Length <= 4096 && Native.DirectoryIdentity(input.SafeFileHandle) == plan.InputIdentity, "Input size/identity differs.");
            using var parentAnchor = Native.AnchorDirectory(Path.GetDirectoryName(plan.Root));
            Require(Native.DirectoryIdentity(parentAnchor) == plan.ParentIdentity && string.Equals(Native.FinalDirectoryPath(parentAnchor), Path.GetDirectoryName(plan.Root), StringComparison.OrdinalIgnoreCase), "Root parent identity differs.");
            Require(ReplayStorage.ObserveCapacity(Path.GetDirectoryName(plan.Root)).AvailableBytes >= 131072, "Tiny-stage capacity prerequisite unavailable.");
            owner = PilotStorageObservation.Create(plan.Root);
            result.RootIdentity = Native.DirectoryIdentity(plan.Root);
            WriteNew(Path.Combine(plan.Root, "plan.json"), Encoding.UTF8.GetBytes(Serialize(request)));
            using (var copy = new FileStream(Path.Combine(plan.Root, "input.txt"), FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { input.CopyTo(copy); copy.Flush(true); }
            using var copyLease = new FileStream(Path.Combine(plan.Root, "input.txt"), FileMode.Open, FileAccess.Read, FileShare.Read);
            string nonce = Guid.NewGuid().ToString("N");
            WriteNew(Path.Combine(plan.Root, "fixture.json"), Encoding.UTF8.GetBytes(Serialize(new PilotFixtureAdmission(plan.Root, result.RootIdentity, nonce, plan.Mode, plan.InputSha256.ToUpperInvariant(), request.PlanSha256))));
            using var admissionLease = new FileStream(Path.Combine(plan.Root, "fixture.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
            var binding = new PilotStorageBinding(request.PlanSha256, plan.SourceSha256, plan.Cutoff);
            result.Before = owner.Observe(binding, ["plan.json", "input.txt", "fixture.json"]);
            Require(result.Before.Complete, "Closed pre-stage storage observation incomplete.");
            using (var stdout = new FileStream(Path.Combine(plan.Root, "stdout.txt"), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            using (var stderr = new FileStream(Path.Combine(plan.Root, "stderr.txt"), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                var info = new ProcessStartInfo(plan.HostPath) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Environment.CurrentDirectory, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (string arg in request.Arguments) info.ArgumentList.Add(arg);
                info.Environment.Clear();
                info.Environment["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot");
                info.Environment["F10_PILOT_STAGE_NONCE"] = nonce;
                info.Environment["PATH"] = Environment.SystemDirectory;
                info.Environment["DOTNET_ROOT"] = Path.GetDirectoryName(plan.HostPath);
                info.Environment["DOTNET_ROOT_X64"] = Path.GetDirectoryName(plan.HostPath);
                launchAttempted = true;
                process = io.StartApprovedProcess(info);
                result.Pid = process.Id; result.ProcessStartedUtc = process.StartTime.ToUniversalTime();
                result.OwnedHandle = process.SafeHandle.DangerousGetHandle().ToString("X");
                WriteNew(Path.Combine(plan.Root, "launch.json"), Encoding.UTF8.GetBytes(Serialize(new { request.PlanSha256, request.Arguments, result.Pid, result.ProcessStartedUtc, result.OwnedHandle })));
                var streams = new[] { process.StandardOutput.BaseStream, process.StandardError.BaseStream };
                var logs = new[] { stdout, stderr };
                var buffers = new[] { new byte[4096], new byte[4096] };
                var reads = new Task<int>[2]; var done = new bool[2];
                var clock = Stopwatch.StartNew();
                string stop = null;
                while (!process.HasExited || !done.All(value => value))
                {
                    if (token.IsCancellationRequested) { stop = "cancelled"; break; }
                    if (clock.ElapsedMilliseconds >= plan.DeadlineMs) { stop = "timeout"; break; }
                    for (int i = 0; i < 2; i++)
                    {
                        if (done[i]) continue;
                        reads[i] ??= streams[i].ReadAsync(buffers[i], 0, buffers[i].Length);
                        if (!reads[i].IsCompleted) continue;
                        int count = reads[i].GetAwaiter().GetResult(); reads[i] = null;
                        if (count == 0) { done[i] = true; continue; }
                        int keep = Math.Min(count, 65536 - result.CapturedBytes);
                        logs[i].Write(buffers[i], 0, keep); result.CapturedBytes += keep;
                        if (keep != count) { result.Truncated = true; stop = "output-limit"; break; }
                    }
                    if (stop != null) break;
                    Thread.Sleep(5);
                }
                result.CaptureComplete = stop == null && done.All(value => value);
                cleanupAttempted = true;
                var cleanup = io.StopAndObserveOwnedProcess(process, stop != null);
                result.ProducerAbsent = cleanup.ProducerAbsent; result.CleanupVerified = cleanup.Verified; result.CleanupOrigin = cleanup.Origin;
                result.ActualExitObserved = process.HasExited;
                if (result.ActualExitObserved) result.ExitCode = process.ExitCode;
                stdout.Flush(true); stderr.Flush(true);
                result.Status = stop ?? "finished";
                result.Reason = stop ?? "required-product-missing";
                if (stop == null && result.ExitCode != 0) result.Reason = "exit-nonzero";
            }
            bool Matches(string leaf, ReadOnlySpan<byte> expected)
            {
                try
                {
                    using var file = new FileStream(Path.Combine(plan.Root, leaf), FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (file.Length != expected.Length) return false;
                    byte[] bytes = new byte[expected.Length]; file.ReadExactly(bytes); return bytes.AsSpan().SequenceEqual(expected);
                }
                catch (FileNotFoundException) { return false; }
            }
            Require(result.ActualExitObserved, "Producer still present/unknown; closed output observation withheld.");
            result.RequiredProductMatches = Matches("product.txt", "stage-ok\n"u8);
            result.PartialMatches = Matches("progress.txt", "partial\n"u8);
            valid = result.Status == "finished" && result.ExitCode == 0 && result.RequiredProductMatches && result.PartialMatches && result.CleanupVerified && result.ProducerAbsent == true && result.CaptureComplete && !result.Truncated;
            if (valid) { result.Status = "succeeded"; result.Reason = null; }
            else if (result.Status == "finished") result.Status = "failed";
            if (!result.CleanupVerified || result.ProducerAbsent != true)
            {
                result.Status = "cleanup-unverified";
                result.Reason = "producer-absence-or-cleanup-unknown";
            }
        }
        catch (Exception error) when (error is IOException or Win32Exception or UnauthorizedAccessException or ArgumentException or InvalidOperationException or JsonException)
        {
            valid = false; result.Status = result.Pid == null ? (launchAttempted ? "launch-failed" : "rejected") : "failed"; result.Reason = error.Message;
        }
        finally
        {
            if (process != null)
            {
                try
                {
                    if (!cleanupAttempted)
                    {
                        var cleanup = io.StopAndObserveOwnedProcess(process, true);
                        result.ProducerAbsent = cleanup.ProducerAbsent; result.CleanupVerified = cleanup.Verified; result.CleanupOrigin = cleanup.Origin;
                    }
                    result.ActualExitObserved = process.HasExited;
                    if (result.ActualExitObserved) result.ExitCode = process.ExitCode;
                }
                catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException)
                {
                    result.CleanupVerified = false; result.ProducerAbsent = null; result.CleanupOrigin = "cleanup-observation-failed";
                    result.Reason = error.Message;
                }
                finally { process.Dispose(); }
            }
        }
        try
        {
            if (owner != null)
            {
                var plan = request.Plan;
                if (result.Pid != null && (!result.CleanupVerified || result.ProducerAbsent != true)) { valid = false; result.Status = "cleanup-unverified"; }
                if (result.ActualExitObserved || !launchAttempted || result.Pid == null)
                {
                    string[] leaves = Directory.EnumerateFileSystemEntries(plan.Root).Select(Path.GetFileName).Where(name => name != PilotStorageObservation.Marker).Take(12).ToArray();
                    Require(leaves.Length <= 9, "Stage retained inventory leaves no bounded publication allowance.");
                    result.AfterClosedPreReceipt = owner.Observe(new(request.PlanSha256, plan.SourceSha256, plan.Cutoff), leaves);
                    Require(result.AfterClosedPreReceipt.Complete, "Closed post-stage storage observation incomplete.");
                }
                result.CompletedUtc = DateTime.UtcNow; result.ElapsedMilliseconds = elapsed.ElapsedMilliseconds;
                var evidence = new { kind = "stage-evidence-requires-publication-readback", candidate_success = valid, stage = result };
                var published = io.PublishEvidence(plan.Root, Encoding.UTF8.GetBytes(Serialize(evidence)));
                Require(published.Path == Path.Combine(plan.Root, "receipt.json") && Sha(published.Sha256), "Publication returned an unsupported receipt identity.");
                using (var receipt = new FileStream(published.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    Require(receipt.Length <= 65536 && Convert.ToHexString(SHA256.HashData(receipt)) == published.Sha256, "Publication readback/hash differs.");
                string[] retained = Directory.EnumerateFileSystemEntries(plan.Root).Take(13).ToArray();
                Require(retained.Length <= 12 && retained.All(path => (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0 && new FileInfo(path).Length <= 1_048_576), "Retained stage inventory exceeds its flat file/byte limits.");
                result.ReceiptPath = published.Path; result.ReceiptSha256 = published.Sha256; result.ReceiptCommitted = true;
                result.Success = valid;
                result.PollReserveHeld = !(result.CleanupVerified && result.ProducerAbsent == true);
            }
        }
        catch (Exception error) when (error is IOException or Win32Exception or UnauthorizedAccessException or ArgumentException or InvalidOperationException or JsonException)
        {
            result.Success = false; result.PollReserveHeld = true; result.Status = "evidence-failed"; result.Reason = error.Message;
        }
        finally { owner?.Dispose(); result.CompletedUtc = DateTime.UtcNow; result.ElapsedMilliseconds = elapsed.ElapsedMilliseconds; }
        return result;
    }

    internal static int Dispatch(string[] args)
    {
        try
        {
            Require(args.Length is 2 or 4 && args[1].Length <= 4096 && args[1] == Path.GetFullPath(args[1]), "Stage route requires one canonical path.");
            if (args.Length == 4)
            {
                Require(args[0] == "--pilot-stage-checks" && args[2] == "--only", "Unknown selected stage check route.");
                return PilotStageRunnerChecks.Run(args[1], args[3]);
            }
            if (args[0] == "--pilot-stage-checks") return PilotStageRunnerChecks.Run(args[1]);
            if (args[0] == "--pilot-stage-fixture") return PilotStageFixture.Run(args[1]);
            if (args[0] == "--pilot-stage-run")
            {
                using var input = new FileStream(args[1], FileMode.Open, FileAccess.Read, FileShare.Read); Require(input.Length <= 65536, "Stage request exceeds64KiB.");
                var request = JsonSerializer.Deserialize<PilotStageRequest>(input, Json);
                var result = Run(request, CancellationToken.None); Console.WriteLine(Serialize(result)); return result.Success ? 0 : 1;
            }
            throw new IOException("Unknown stage-only route.");
        }
        catch (Exception error) when (error is IOException or Win32Exception or UnauthorizedAccessException or ArgumentException or JsonException)
        {
            Console.WriteLine(Serialize(new { kind = "stage-input-rejected", error = error.Message, execution_enabled = false, next_stage_enabled = false })); return 2;
        }
    }
}
