using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace F10Recorder;

// One prospective, hash-bound acquisition diagnostic. No export, fitting,
// qualification, automatic retry or default-account catalog write exists here.
internal static class ReplayCalibrationStartup
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    static void Require(bool value, string reason) { if (!value) throw new IOException(reason); }
    static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
    static void Write(string root, string name, object value) => File.WriteAllText(Path.Combine(root, name + ".json"), JsonSerializer.Serialize(value, Json));
    internal static string[] NormalizeCaptureArguments(IReadOnlyList<string> arguments, string cell)
    {
        Require(arguments.Count > 0, "Capture arguments are empty.");
        string target = Path.GetFullPath(arguments[^1]), session = Path.GetDirectoryName(target);
        string buffer = Path.GetFullPath(Path.Combine(cell, "buffer")).TrimEnd(Path.DirectorySeparatorChar);
        Require(string.Equals(Path.GetDirectoryName(session), buffer, StringComparison.OrdinalIgnoreCase), "Startup capture escaped private buffer.");
        var normalized = arguments.Select(v => v.StartsWith(@"\\.\pipe\F10Recorder-", StringComparison.Ordinal)
            ? "<audio-pipe>" : Regex.Replace(v, "setpts='[^']+'", "<clock>")).ToArray();
        normalized[^1] = Path.Combine("<session>", Path.GetFileName(target));
        return normalized;
    }
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    internal static void ValidateResources(CalibrationPlan plan)
    {
        bool original = plan != null && plan.RetainedChildren == 111 && plan.RetainedRoots?.Length == 12 && plan.OfflineDeadlineSeconds == 5400;
        bool corrected = plan != null && plan.RetainedChildren == 113 && plan.RetainedRoots?.Length == 13 && plan.OfflineDeadlineSeconds == 6000;
        Require((original || corrected) && (long)plan.RetainedChildren + 2 <= plan.MaximumChildren && plan.MaximumChildren == 137
            && plan.BudgetBytes == 84_000_000_000 && plan.StopBytes == 82_500_000_000 && plan.FloorBytes == 60_000_000_000
            && plan.DeadlineSeconds == 14400 && double.IsFinite(plan.RetainedElapsedSeconds) && plan.RetainedElapsedSeconds >= 0
            && plan.RetainedElapsedSeconds + 75 < plan.DeadlineSeconds
            && plan.RetainedOfflineSeconds >= 0 && plan.RetainedOfflineSeconds <= plan.RetainedElapsedSeconds
            && plan.RetainedOfflineSeconds + 120 < plan.OfflineDeadlineSeconds
            && plan.RetainedBudgetBytes == 44_000_000_000 && plan.OutsideBuildAllowanceBytes == 1_100_000_000
            && plan.FileLimit == 10240 && plan.DirectoryLimit == 160 && plan.Order?.SequenceEqual(new[] { "T02" }) == true
            && plan.RetainedRoots.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() == plan.RetainedRoots.Length,
            "Startup diagnostic cannot fit its reviewed cumulative boundary.");
    }
    internal static int Run(string path, string hash)
    {
        var total = Stopwatch.StartNew();
        Require(new FileInfo(path).Length <= 65536 && Hash(path) == hash, "Startup diagnostic plan changed.");
        using var document = JsonDocument.Parse(File.ReadAllText(path)); var admission = document.RootElement;
        Require(admission.GetProperty("kind").GetString() == "single-acquisition-startup-v1"
            && admission.GetProperty("newChildren").GetInt32() == 2 && admission.GetProperty("deadlineSeconds").GetInt32() == 75
            && admission.GetProperty("sourceBudgetBytes").GetInt64() == 134_217_728, "Startup diagnostic scope changed.");
        var plan = admission.GetProperty("plan").Deserialize<CalibrationPlan>();
        plan = plan with { Root = Path.GetFullPath(plan.Root) };
        ValidateResources(plan);
        foreach (var file in plan.Files) Require(Hash(file.Key) == file.Value, "Startup diagnostic input changed: " + file.Key);
        string assembly = typeof(ReplayCalibrationStartup).Assembly.Location, runtime = Path.GetDirectoryName(assembly);
        foreach (string file in new[] { Environment.ProcessPath, assembly, Path.Combine(runtime, "F10Recorder.Windows.dll"),
            Path.Combine(runtime, "F10Recorder.Core.dll"), Path.Combine(runtime, "F10Recorder.WorkerChecks.deps.json"), Path.Combine(runtime, "F10Recorder.WorkerChecks.runtimeconfig.json") })
            Require(plan.Files.ContainsKey(Path.GetFullPath(file)), "Unbound startup runtime.");
        MediaDependency.RootDirectory = plan.EngineRoot;
        string engine = MediaDependency.ResolveExecutable();
        Require(plan.Files.GetValueOrDefault(Path.GetFullPath(engine)) == Hash(engine), "Unbound startup engine.");
        using (var endpoint = new LoopbackAudio())
        {
            var expected = admission.GetProperty("environment");
            var bounds = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            Require(expected.GetProperty("os").GetString() == Environment.OSVersion.VersionString
                && expected.GetProperty("machine").GetString() == Environment.MachineName
                && expected.GetProperty("Width").GetInt32() == bounds.Width && bounds.Width == 1920
                && expected.GetProperty("Height").GetInt32() == bounds.Height && bounds.Height == 1080
                && expected.GetProperty("primaryOutput").GetInt32() == Native.PrimaryOutputIndex()
                && expected.GetProperty("EndpointId").GetString() == endpoint.EndpointId
                && expected.GetProperty("SampleRate").GetInt32() == endpoint.SampleRate
                && expected.GetProperty("Channels").GetInt32() == endpoint.Channels
                && expected.GetProperty("FfmpegFormat").GetString() == endpoint.FfmpegFormat, "Startup desktop/audio/host binding changed.");
            var capacity = ReplayStorage.ObserveCapacity(Path.GetDirectoryName(plan.Root));
            Require(capacity.Volume == expected.GetProperty("capacity").GetProperty("Volume").GetString()
                && capacity.AllocationUnit == expected.GetProperty("capacity").GetProperty("AllocationUnit").GetInt64(), "Startup volume/unit binding changed.");
        }
        using var engineLease = MediaDependency.OpenVerifiedExecutable(engine);
        using var owned = PilotStorageObservation.Create(plan.Root);
        Log.DirectoryPath = plan.Root;
        Write(plan.Root, "admitted-plan", new { hash, plan, newChildrenCeiling = 2, noAutomaticRetry = true, modelActivated = false });
        using var sampler = new ReplayCalibrationRun.Sampler(plan, active: false, currentBudgetBytes: 125_829_120);
        string cell = Path.Combine(plan.Root, "T02"); Directory.CreateDirectory(cell);
        Process fixture = null; CaptureSession capture = null; Task<CaptureSession> creating = null;
        using var fixtureJob = new Native.ProcessJob();
        using var cancel = new CancellationTokenSource();
        IntPtr previous = GetForegroundWindow();
        bool success = false; Exception failure = null; int segments = 0;
        void Check()
        {
            sampler.Check(); Require(total.Elapsed.TotalSeconds < 60, "Startup diagnostic active deadline.");
            Require(fixture == null || !fixture.HasExited, "Startup fixture exited before acquisition completed.");
        }
        try
        {
            CalibrationDesktop.RequireLocalDesktop();
            var bounds = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            Require(bounds.Width == 1920 && bounds.Height == 1080, "Startup reference requires primary1920x1080.");
            sampler.BeginActive();
            var start = new ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = false, CreateNoWindow = true };
            if (Path.GetFileNameWithoutExtension(Environment.ProcessPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(assembly);
            foreach (string value in new[] { "--calibration-fixture", cell, "T02" }) start.ArgumentList.Add(value);
            fixture = Process.Start(start); fixtureJob.Add(fixture);
            Write(cell, "fixture-start", new { pid = fixture.Id, utc = fixture.StartTime.ToUniversalTime() });
            var readyClock = Stopwatch.StartNew();
            while (!File.Exists(Path.Combine(cell, "fixture-ready.json")))
            { Check(); Require(readyClock.Elapsed.TotalSeconds < 15, "Startup fixture readiness deadline."); Thread.Sleep(50); }
            using var ready = CalibrationFixturePublication.Read(cell);
            IntPtr hwnd = (IntPtr)ready.RootElement.GetProperty("hwnd").GetInt64();
            Native.GetWindowThreadProcessId(hwnd, out uint owner);
            Require(owner == fixture.Id && ready.RootElement.GetProperty("pid").GetInt32() == fixture.Id
                && ready.RootElement.GetProperty("width").GetInt32() == 1920 && ready.RootElement.GetProperty("height").GetInt32() == 1080,
                "Startup fixture identity/dimensions failed.");
            bool requested = SetForegroundWindow(hwnd); var focus = Stopwatch.StartNew();
            CalibrationForeground.Wait(() => GetForegroundWindow() == hwnd && Native.IsWindowVisible(hwnd), () => fixture.HasExited,
                Check, () => Thread.Sleep(50), () => focus.Elapsed.TotalSeconds);
            Write(cell, "foreground", new { requested, foreground = GetForegroundWindow() == hwnd, hwnd = hwnd.ToInt64() });
            RecorderRuntime.CaptureArgumentObserver = arguments =>
            {
                var normalized = NormalizeCaptureArguments(arguments, cell);
                Require(normalized.SequenceEqual(ReplayCalibrationRun.CaptureTemplate(Native.PrimaryOutputIndex().ToString(CultureInfo.InvariantCulture))), "Startup reference capture recipe changed.");
                Write(cell, "capture-arguments", arguments);
            };
            var settings = new Settings { ReplayMinutes = 5, FramesPerSecond = 60, BitrateMbps = 20, VideoCodec = "h264_nvenc",
                SystemAudio = true, CaptureMode = "monitor", DisplayIndex = -1, OutputDirectory = cell };
            creating = Task.Run(() => new CaptureSession(settings, Path.Combine(cell, "buffer"), false,
                () => cancel.IsCancellationRequested || sampler.Failure != null || GetForegroundWindow() != hwnd));
            var bootstrap = Stopwatch.StartNew();
            while (!creating.IsCompleted)
            { Check(); Require(bootstrap.Elapsed.TotalSeconds < 35 && GetForegroundWindow() == hwnd, "Startup bootstrap/focus deadline."); Thread.Sleep(50); }
            capture = creating.GetAwaiter().GetResult();
            Require(capture.ReferenceScope != null, "Startup source is outside the frozen reference recipe/audio/dimensions.");
            Write(cell, "capture-start", new { pid = capture.ProcessId, utc = DateTime.UtcNow });
            var source = (IReplaySessionSource)capture;
            using (var trace = new StreamWriter(new FileStream(Path.Combine(cell, "health.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read)))
            {
                while (true)
                {
                    Check(); Require(GetForegroundWindow() == hwnd, "Startup fixture lost foreground.");
                    var entries = source.ReadSegments(); segments = entries.Count;
                    source.CheckCaptureHealth(entries.Count == 0 ? 0 : entries[^1].End);
                    var health = capture.ReadHealth(); trace.WriteLine(JsonSerializer.Serialize(health)); trace.Flush();
                    if (!health.Healthy) { Write(cell, "capture-health-failure", health); throw new IOException("Startup capture unhealthy: " + JsonSerializer.Serialize(health)); }
                    if (health.ProgressSeconds >= 8 && health.EncodedFrames >= 120 && segments >= 5) break;
                    Require(bootstrap.Elapsed.TotalSeconds < 25, "Startup source did not deliver the required progress/frames/segments.");
                    Thread.Sleep(100);
                }
            }
            Write(cell, "health-before-stop", capture.ReadHealth());
            success = true;
        }
        catch (Exception ex)
        {
            failure = ex;
            if (capture != null) Write(cell, "health-at-failure-before-cleanup", capture.ReadHealth());
        }
        finally
        {
            cancel.Cancel(); RecorderRuntime.CaptureArgumentObserver = null;
            try
            {
                if (capture == null && creating != null)
                {
                    try { if (creating.Wait(15000)) capture = creating.GetAwaiter().GetResult(); } catch (AggregateException) { }
                    Require(creating.IsCompleted, "Startup bootstrap cleanup unconfirmed.");
                }
                if (capture != null)
                {
                    capture.StopCapture();
                    Write(cell, "capture-exit", new { capture.HasExited, capture.CaptureExitCode, capture.CaptureProducerAbsent, capture.ErrorTail });
                    Require(capture.HasExited && capture.CaptureProducerAbsent, "Startup capture producer absence unconfirmed.");
                    if (success) Require(capture.CaptureExitCode == 0 && string.IsNullOrWhiteSpace(capture.ErrorTail), "Startup capture did not exit normally with empty diagnostics.");
                }
            }
            catch (Exception ex) { success = false; failure = new AggregateException("Startup source cleanup failed.", new[] { failure, ex }.OfType<Exception>()); }
            finally
            {
                try { capture?.Dispose(); }
                catch (Exception ex) { success = false; failure = new AggregateException("Startup source disposal failed.", new[] { failure, ex }.OfType<Exception>()); }
                if (fixture != null)
                {
                    try
                    {
                    bool forced = false;
                    if (!fixture.HasExited) { fixture.CloseMainWindow(); if (!fixture.WaitForExit(5000)) { forced = true; fixture.Kill(); fixture.WaitForExit(5000); } }
                    Write(cell, "fixture-exit", new { exited = fixture.HasExited, exit = fixture.HasExited ? (int?)fixture.ExitCode : null, forced });
                    if (!fixture.HasExited || forced || fixture.ExitCode != 0) { success = false; failure ??= new IOException("Startup fixture did not exit normally."); }
                    if (success)
                    {
                        using var restored = JsonDocument.Parse(File.ReadAllText(Path.Combine(cell, "fixture-display-exit.json")));
                        if (!restored.RootElement.GetProperty("restored").GetBoolean()) { success = false; failure ??= new IOException("Startup fixture wake restoration unconfirmed."); }
                    }
                    }
                    catch (Exception ex) { success = false; failure = new AggregateException("Startup fixture cleanup failed.", new[] { failure, ex }.OfType<Exception>()); }
                    finally { fixture.Dispose(); }
                }
                SetForegroundWindow(previous);
            }
        }
        try { sampler.StopAndCheck(); Require(total.Elapsed.TotalSeconds < 75, "Startup diagnostic total deadline."); }
        catch (Exception ex) { success = false; failure = new AggregateException("Startup observer/deadline failed.", new[] { failure, ex }.OfType<Exception>()); }
        Write(plan.Root, success ? "verification" : "failure", new { result = success ? "passed-startup-acquisition-only" : "failed-no-retry",
            error = failure?.ToString(), elapsedSeconds = total.Elapsed.TotalSeconds, closedSegments = segments, sampler.MaximumActiveGap,
            sampler.PeakUsed, sampler.CurrentAllocatedBytes, conservativeNewChildren = 2, cumulativeChildren = plan.RetainedChildren + 2, modelActivated = false });
        Console.WriteLine(success ? "Startup acquisition passed; no calibration model qualified." : failure?.Message);
        return success ? 0 : 1;
    }
}
