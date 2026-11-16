using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Forms = System.Windows.Forms;

namespace F10Recorder;

// A fault-injection host for the unchanged, already qualified product. This is
// neither a save benchmark nor permission to record: Run requires a new admission.
internal static class ReplayCapacityDrainPilot
{
    internal const int CaptureSeconds = 15, StopCleanupSeconds = 25, FixtureSeconds = 235, DeadlineSeconds = 300;
    const long Cap = 268_435_456, Stop = 201_326_592, Floor = 60_000_000_000;
    const string ExpiryReason = "Capture capacity monitoring exceeded its qualified coverage; stopping with claims retained.";
    static void Require(bool value, string reason) { if (!value) throw new IOException(reason); }
    static long Tick() => Stopwatch.GetTimestamp();
    static void Write(string root, string name, object value)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions(WorkerProtocol.Json) { WriteIndented = true });
        Require(bytes.Length <= (name == "runtime-closure.json" ? 262144 : 65536), "Bounded drain report required.");
        WriteBytes(root, name, bytes);
    }
    static void WriteBytes(string root, string name, byte[] bytes)
    {
        Require(bytes.Length <= 8 * 1024 * 1024, "Bounded ledger snapshot required.");
        using var output = new FileStream(Path.Combine(root, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        output.Write(bytes); output.Flush(true);
    }
    static byte[] Read(string path, int maximum)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Require(input.Length is > 0 && input.Length <= maximum, "Bounded input required: " + path);
        byte[] bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes); return bytes;
    }
    internal static int Fixture(string root) => CalibrationPattern.Run(root, "H02", manualHandoff: true, lifetimeSeconds: FixtureSeconds);
    internal static int Prepare(string planFile, string root, string engineRoot, string product, string productionData, string catalog)
    {
        foreach (string path in new[] { planFile, root, engineRoot, product, productionData, catalog }) Require(Path.IsPathFullyQualified(path), "Absolute paths required.");
        Require(!File.Exists(planFile) && !Directory.Exists(root) && !File.Exists(root), "Fresh admission and output root required.");
        Require(root.StartsWith(Path.GetFullPath("artifacts") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Task artifact root required.");
        MediaDependency.RootDirectory = engineRoot;
        string engine = MediaDependency.ResolveExecutable();
        Require(ReplayWorkerLatencyPilot.Hash(Path.Combine(product, "F10Recorder.Windows.dll")) == ReplayWorkerLatencyPilot.Hash(typeof(CaptureSession).Assembly.Location)
            && ReplayWorkerLatencyPilot.Hash(Path.Combine(product, "F10Recorder.Core.dll")) == ReplayWorkerLatencyPilot.Hash(typeof(Settings).Assembly.Location), "Unchanged qualified product required.");
        var available = ReplayCalibrationCatalog.LoadFrom(catalog); Require(available.Model?.CaptureHeadroom != null, available.Reason ?? "Capture headroom required.");
        Require(ReplayCalibrationCatalog.IsApplicableToDestination(available.Model, Path.GetDirectoryName(root)), "Qualified destination required.");
        var bounds = Forms.Screen.PrimaryScreen.Bounds;
        using var audio = new LoopbackAudio();
        Require(bounds.Width == 1920 && bounds.Height == 1080 && audio.SampleRate == 48000 && audio.Channels == 2, "Reference display and audio required.");
        long growth = checked(available.Model.CaptureHeadroom.PollGrowthBytes + available.Model.CaptureHeadroom.StopGrowthBytes);
        long forward = checked((CaptureSeconds + StopCleanupSeconds) * 4_194_304L + 25_165_824);
        Require(forward < Stop && Stop + growth + 8_388_608 < Cap, "Source, growth and closure must fit the complete envelope.");
        Require(ReplayStorage.ObserveCapacity(Path.GetDirectoryName(root)).AvailableBytes >= Floor + Cap, "Full new promise and physical floor required.");
        string admission = Path.GetDirectoryName(planFile); Directory.CreateDirectory(admission);
        string runtimeManifest = Path.Combine(admission, "runtime-closure.json");
        var runtime = ReplayWorkerLatencyPilot.RuntimeFiles().ToDictionary(p => p, p => ReplayWorkerLatencyPilot.Hash(p), StringComparer.OrdinalIgnoreCase);
        Require(runtime.Count is > 0 and <= 1024, "Runtime count bound.");
        Write(admission, "runtime-closure.json", runtime);
        Require(new FileInfo(runtimeManifest).Length <= 262144, "Runtime byte bound.");
        string build = Path.GetDirectoryName(typeof(ReplayCapacityDrainPilot).Assembly.Location);
        string[] shared = [Path.Combine(productionData, "settings.json"), Path.Combine(productionData, "worker-state.json")];
        var files = Directory.GetFiles(build).Where(p => Path.GetExtension(p) is ".dll" or ".json")
            .Concat(new[] { Environment.ProcessPath, engine, runtimeManifest, Path.Combine(Environment.SystemDirectory, "conhost.exe"),
                Path.Combine(catalog, ReplayCalibrationCatalog.FileName),
                Path.GetFullPath("projects/F10Recorder.WorkerChecks/ReplayCapacityDrainPilot.cs"),
                Path.GetFullPath("projects/F10Recorder.WorkerChecks/ReplayWorkerLatencyPilot.cs"),
                Path.GetFullPath("projects/F10Recorder.WorkerChecks/CalibrationPattern.cs"),
                Path.GetFullPath("projects/F10Recorder.WorkerChecks/CalibrationForegroundRelease.cs"),
                Path.GetFullPath("tests/replay-capacity-drain.ps1") }).Concat(shared)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(p => p, p => ReplayWorkerLatencyPilot.Hash(p, shared.Contains(p)), StringComparer.OrdinalIgnoreCase);
        Write(admission, Path.GetFileName(planFile), new { kind = "issue83-real-capacity-drain-v1", root, engineRoot, product, productionData, catalog, files, sharedFiles = shared, runtimeManifest,
            maximumChildren = 8, cumulativeProcessesIncludingPreparationAndController = 11,
            consoleSupport = new { path = Path.Combine(Environment.SystemDirectory, "conhost.exe"), maximumDirectChildren = 1, maximumWorkingSetBytes = 67_108_864 },
            captureSeconds = CaptureSeconds, stopAndCleanupSeconds = StopCleanupSeconds, fixtureLifetimeSeconds = FixtureSeconds, deadlineSeconds = DeadlineSeconds, foregroundHandoffSeconds = 180,
            budgetBytes = Cap, stopBytes = Stop, minimumFreeBytes = Floor, expectedForwardBytes = forward, qualifiedGrowthBytes = growth,
            maximumFiles = 1024, maximumDirectories = 128, maximumBlockedQueryMilliseconds = 8000,
            startup = ReplayWorkerLatencyPilot.Startup(), createdUtc = DateTime.UtcNow,
            effects = "Separate authorization required: one primary-desktop/default-loopback capture,15s acquisition deadline plus25s reserved owned stop/cleanup,300s whole including handoff. Owned fullscreen moving pattern/quiet chirp. Actual ReplayEngine and real private catalog/volume observations. One delayed actual capacity query; no save, synthetic capacity/epoch, installation/startup/vendor/account-catalog change. Private media retained. Deadlines and sampled stops are finite controller policy, not an OS quota or universal absence guarantee.",
            decision = "Require advancing FFmpeg and live audio before arming. Observe real pause fence, exact retained FFmpeg exit and actual audio absence separately. Keep exact growth claim while query is blocked, then release and explicitly retry closure with actual absence if required. Retain durable safety hold. Timing is sampled, includes this observer, and does not certify universal drain latency or save/resource acceptance." });
        Console.WriteLine(planFile + " " + ReplayWorkerLatencyPilot.Hash(planFile)); return 0;
    }

    sealed class DelayedCapacity : ReplayFiles
    {
        internal readonly ManualResetEventSlim Entered = new(), Release = new(), Returned = new();
        internal int Armed, Blocked, Calls;
        internal long EnteredAt, ReturnedAt;
        internal bool TimedOut;
        internal ReplayVolumeCapacity Actual;
        internal string Error;
        internal override ReplayVolumeCapacity ObserveCapacity(string path)
        {
            Interlocked.Increment(ref Calls);
            bool delay = Volatile.Read(ref Armed) != 0 && Interlocked.CompareExchange(ref Blocked, 1, 0) == 0;
            if (!delay) return base.ObserveCapacity(path);
            EnteredAt = Tick(); Entered.Set();
            try
            {
                if (!Release.Wait(8000)) { TimedOut = true; throw new IOException("Controlled query release timed out; evidence is failed."); }
                Actual = base.ObserveCapacity(path); return Actual;
            }
            catch (Exception error) { Error = error.ToString(); throw; }
            finally { ReturnedAt = Tick(); Returned.Set(); }
        }
    }
    internal static void RequireClaim(byte[] bytes, string holdId, string directory, ReplayCaptureHeadroom headroom, ReplayProducerProof producer, string anchorIdentity)
    {
        Require(bytes.Length is > 0 and <= 8 * 1024 * 1024, "Ledger byte bound.");
        using var ledger = JsonDocument.Parse(bytes);
        var owners = ledger.RootElement.GetProperty("Owners").EnumerateArray().Where(o => o.GetProperty("Owner").GetString() == holdId).ToArray();
        Require(owners.Length == 1 && owners[0].GetProperty("Producer").Deserialize<ReplayProducerProof>() == producer && producer.Sealed, "Exact sealed capture owner required.");
        var claims = owners[0].GetProperty("Claims").EnumerateArray().ToArray();
        Require(claims.Length == 1, "One exact capture growth claim required.");
        var claim = claims[0].GetProperty("Claim").Deserialize<ReplayVolumeClaim>();
        Require(claim == new ReplayVolumeClaim(Path.Combine(directory, "capture-growth-" + holdId), directory, headroom.Volume, headroom.AllocationUnit,
            checked(headroom.PollGrowthBytes + headroom.StopGrowthBytes), ReplayVolumeClaimKind.UnmaterializedGrowth), "Capture growth path/volume/unit/peak/kind changed.");
        Require(claims[0].GetProperty("Identity").ValueKind == JsonValueKind.Null
            && claims[0].GetProperty("Anchor").GetString() == directory
            && !string.IsNullOrEmpty(anchorIdentity) && claims[0].GetProperty("AnchorIdentity").GetString() == anchorIdentity, "Native growth anchor changed.");
    }

    static (double Entry, double Pause) RequireExpiry(long epoch, long entered, long pause, string reason)
    {
        double entryMs = Stopwatch.GetElapsedTime(epoch, entered).TotalMilliseconds;
        double pauseMs = Stopwatch.GetElapsedTime(epoch, pause).TotalMilliseconds;
        Require(reason == ExpiryReason && epoch <= entered && entered < pause
            && entryMs <= ReplayStorageCoordinator.MaximumObservationGapMilliseconds
            && pauseMs > ReplayStorageCoordinator.MaximumObservationGapMilliseconds,
            "A genuine covered-to-expired capacity stop is required; another stop cannot pass.");
        return (entryMs, pauseMs);
    }
    internal static int ClaimChecks()
    {
        string directory = @"D:\private\capture", hold = "owned-capture", anchor = "native-directory-identity";
        var headroom = new ReplayCaptureHeadroom(new('A', 64), "volume", 4096, 32768, 16384, 2000);
        var producer = new ReplayProducerProof(7, 1234, ReplayProducerProof.JobPrefix + new string('a', 32), true);
        var claim = new ReplayVolumeClaim(Path.Combine(directory, "capture-growth-" + hold), directory, headroom.Volume, headroom.AllocationUnit, 49152, ReplayVolumeClaimKind.UnmaterializedGrowth);
        int passed = 0;
        foreach (string change in new[] { "valid", "owner", "worker-birth", "unsealed", "path", "directory", "volume", "unit", "peak", "kind", "identity", "anchor", "anchor-identity", "duplicate-owner", "duplicate-claim" })
        {
            var selected = change switch
            {
                "path" => claim with { Path = claim.Path + "-other" }, "directory" => claim with { Directory = directory + "-other" },
                "volume" => claim with { Volume = "other-volume" }, "unit" => claim with { AllocationUnit = 8192 },
                "peak" => claim with { PeakAllocatedBytes = 49151 }, "kind" => claim with { Kind = ReplayVolumeClaimKind.FilePeak }, _ => claim
            };
            var proof = change == "worker-birth" ? producer with { WorkerStartedUtcTicks = 1235 } : change == "unsealed" ? producer with { Sealed = false } : producer;
            var held = new { Claim = selected, Identity = change == "identity" ? "unexpected-file" : null,
                Anchor = change == "anchor" ? directory + "-other" : directory, AnchorIdentity = change == "anchor-identity" ? anchor + "-other" : anchor };
            var owner = new { Owner = change == "owner" ? hold + "-other" : hold, Producer = proof, Claims = Enumerable.Repeat(held, change == "duplicate-claim" ? 2 : 1).ToArray() };
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new { Version = 3, Owners = Enumerable.Repeat(owner, change == "duplicate-owner" ? 2 : 1).ToArray() });
            bool rejected = false; try { RequireClaim(bytes, hold, directory, headroom, producer, anchor); } catch (IOException) { rejected = true; }
            Require(rejected == (change != "valid"), "Growth witness control failed: " + change); passed++;
        }
        foreach (string change in new[] { "valid", "not-expired", "entry-expired", "pause-before-entry", "other-stop" })
        {
            long at = Stopwatch.Frequency, entered = at + Stopwatch.Frequency / 20;
            long pause = at + Stopwatch.Frequency * 2001 / 1000;
            if (change == "not-expired") pause = at + Stopwatch.Frequency * 2;
            if (change == "entry-expired") { entered = pause; pause += Stopwatch.Frequency; }
            if (change == "pause-before-entry") pause = entered - 1;
            bool rejected = false; try { RequireExpiry(at, entered, pause, change == "other-stop" ? "another failure" : ExpiryReason); } catch (IOException) { rejected = true; }
            Require(rejected == (change != "valid"), "Expiry witness control failed: " + change); passed++;
        }
        Console.WriteLine($"Passed {passed} exact capture-growth/expiry witness checks; no native/process/media effects."); return 0;
    }

    internal static int Run(string planFile, string expectedHash)
    {
        byte[] raw = Read(planFile, 65536);
        Require(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(raw)) == expectedHash, "Exact reviewed plan required.");
        using var document = JsonDocument.Parse(raw); var plan = document.RootElement;
        Require(plan.GetProperty("kind").GetString() == "issue83-real-capacity-drain-v1" && plan.GetProperty("captureSeconds").GetInt32() == CaptureSeconds
            && plan.GetProperty("deadlineSeconds").GetInt32() == DeadlineSeconds && plan.GetProperty("maximumChildren").GetInt32() == 8
            && plan.GetProperty("stopAndCleanupSeconds").GetInt32() == StopCleanupSeconds && plan.GetProperty("fixtureLifetimeSeconds").GetInt32() == FixtureSeconds
            && plan.GetProperty("budgetBytes").GetInt64() == Cap && plan.GetProperty("stopBytes").GetInt64() == Stop && plan.GetProperty("minimumFreeBytes").GetInt64() == Floor
            && plan.GetProperty("maximumFiles").GetInt32() == 1024 && plan.GetProperty("maximumDirectories").GetInt32() == 128
            && plan.GetProperty("maximumBlockedQueryMilliseconds").GetInt32() == 8000
            && DateTime.UtcNow.AddSeconds(DeadlineSeconds) < plan.GetProperty("createdUtc").GetDateTime().AddHours(4), "Current finite drain admission required.");
        string[] shared = plan.GetProperty("sharedFiles").EnumerateArray().Select(p => p.GetString()).ToArray();
        void Bindings()
        {
            foreach (var file in plan.GetProperty("files").EnumerateObject()) Require(ReplayWorkerLatencyPilot.Hash(file.Name, shared.Contains(file.Name)) == file.Value.GetString(), "Binding changed: " + file.Name);
            string manifest = plan.GetProperty("runtimeManifest").GetString();
            var closure = ReplayWorkerLatencyPilot.ParseRuntime(Read(manifest, 262144), plan.GetProperty("files").GetProperty(manifest).GetString(), ReplayWorkerLatencyPilot.RuntimeFiles());
            foreach (var file in closure) Require(ReplayWorkerLatencyPilot.Hash(file.Key) == file.Value, "Runtime binding changed.");
            Require(ReplayWorkerLatencyPilot.Startup() == plan.GetProperty("startup").GetString(), "Startup changed.");
        }
        Bindings();
        string root = plan.GetProperty("root").GetString(), catalog = plan.GetProperty("catalog").GetString();
        Require(Path.IsPathFullyQualified(root) && root.StartsWith(Path.GetFullPath("artifacts") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !Directory.Exists(root) && !File.Exists(root), "Fresh private root required.");
        MediaDependency.RootDirectory = plan.GetProperty("engineRoot").GetString();
        var qualification = ReplayCalibrationCatalog.LoadFrom(catalog); Require(qualification.Model?.CaptureHeadroom != null, "Current real catalog required.");
        string data = Path.Combine(root, "profile"), fixtureRoot = Path.Combine(root, "fixture"), authority = Path.Combine(root, "authority");
        Directory.CreateDirectory(data); Directory.CreateDirectory(fixtureRoot);
        Write(root, "admitted-plan.json", plan);
        string previousAuthority = ProfileOwnership.RootDirectory, previousLog = Log.DirectoryPath, previousLogFile = Log.FileName;
        var files = new DelayedCapacity(); var total = Stopwatch.StartNew(); var captureClock = new Stopwatch();
        var ready = new TaskCompletionSource<CaptureSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        ReplayEngine engine = null; ReplayStorageCoordinator coordinator = null; ReplayStorageStopLatch latch = null;
        CaptureSession source = null; LoopbackAudio audio = null; Process ffmpeg = null, fixture = null;
        Task disposal = null; Exception failure = null; var cleanup = new List<string>();
        int acquisitions = 0; long pauseAt = 0, videoAbsentAt = 0, audioAbsentAt = 0, closureAt = 0, epoch = 0;
        bool claimWitness = false, explicitClosureRetry = false, disposed = false; long peakBytes = 0;
        double lastScan = -1, maximumScanMs = 0; int peakFiles = 0;
        void Guard(bool foreground = true, bool closing = false)
        {
            Require(total.Elapsed.TotalSeconds < (closing ? 295 : 275), "Whole deadline; cleanup reserved.");
            if (captureClock.IsRunning && source?.CaptureProducerAbsent != true) Require(captureClock.Elapsed.TotalSeconds < CaptureSeconds, "Actual capture deadline.");
            if (foreground && fixture != null) Require(!fixture.HasExited && GetForegroundWindow() == fixture.MainWindowHandle, "Owned source lost foreground.");
            if (total.Elapsed.TotalSeconds - lastScan < .5) return;
            lastScan = total.Elapsed.TotalSeconds; var scan = Stopwatch.StartNew();
            long bytes = 0; int count = 0, directories = 0; var pending = new Stack<string>(); pending.Push(root);
            while (pending.TryPop(out string directory))
            {
                Require(++directories <= 128, "Directory cap.");
                string[] entries;
                try { entries = Directory.GetFileSystemEntries(directory); }
                catch (DirectoryNotFoundException) when (directory != root) { continue; }
                foreach (string path in entries)
                {
                    try
                    {
                        var attributes = File.GetAttributes(path); Require((attributes & FileAttributes.ReparsePoint) == 0, "Unexpected output reparse.");
                        if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
                        else { Require(++count <= 1024, "File cap."); bytes = checked(bytes + new FileInfo(path).Length); }
                    }
                    catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { }
                }
            }
            peakBytes = Math.Max(peakBytes, bytes); peakFiles = Math.Max(peakFiles, count);
            Require(bytes < Stop && ReplayStorage.ObserveCapacity(root).AvailableBytes >= Floor, "Storage stop/floor.");
            maximumScanMs = Math.Max(maximumScanMs, scan.Elapsed.TotalMilliseconds); Require(maximumScanMs <= 500, "Storage observer cost exceeded admission.");
        }
        void Wait(Func<bool> condition, int milliseconds, string reason, bool foreground = true)
        {
            var clock = Stopwatch.StartNew();
            while (!condition()) { Guard(foreground); Require(clock.ElapsedMilliseconds < milliseconds, reason); Thread.Sleep(5); }
        }
        try
        {
            ProfileOwnership.RootDirectory = authority; Log.DirectoryPath = data; Log.FileName = "capacity-drain.log";
            ReplayCalibrationCatalog.CandidateForChecks = minutes => ReplayCalibrationCatalog.LoadFrom(catalog, replayMinutes: minutes);
            var settings = new Settings { ReplayMinutes = 5, FramesPerSecond = 60, BitrateMbps = 20, VideoCodec = "h264_nvenc", SystemAudio = true,
                CaptureMode = "monitor", DisplayIndex = -1, OutputDirectory = Path.Combine(root, "output"), ReplayCacheDirectory = Path.Combine(root, "cache"), ScreenshotDirectory = Path.Combine(root, "screenshots") };
            RecordingProfile.Load(data).Commit(settings, true, true, false);
            var start = new ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add(typeof(ReplayCapacityDrainPilot).Assembly.Location); start.ArgumentList.Add("--replay-capacity-drain-fixture"); start.ArgumentList.Add(fixtureRoot);
            fixture = Process.Start(start);
            Wait(() => File.Exists(Path.Combine(fixtureRoot, "fixture-ready.json")), 20000, "Fixture startup deadline.", false);
            fixture.Refresh(); IntPtr hwnd = fixture.MainWindowHandle; Require(hwnd != IntPtr.Zero, "Owned fixture window required.");
            var release = CalibrationForegroundRelease.Create("H02", fixture.Id, hwnd.ToInt64());
            Write(root, "foreground-handoff-ready.json", new { label = "H02", pid = fixture.Id, hwnd = hwnd.ToInt64(), captureStarted = false, release });
            var handoff = Stopwatch.StartNew();
            CalibrationForeground.WaitForUserHandoff(() => { Native.GetWindowThreadProcessId(hwnd, out uint owner); return owner == fixture.Id && Native.IsWindowVisible(hwnd) && GetForegroundWindow() == hwnd && release.IsReleased(root); },
                () => fixture.HasExited, () => Guard(false), () => Thread.Sleep(50), () => handoff.Elapsed.TotalSeconds);
            Write(root, "foreground-handoff-complete.json", new { allUiCallsReturned = true, completedUtc = DateTime.UtcNow });
            latch = new(data, authority); coordinator = new(latch, new ReplayVolumeReservations(authority), files, () => ReplayCalibrationCatalog.LoadFrom(catalog));
            Directory.CreateDirectory(settings.OutputDirectory); Directory.CreateDirectory(settings.ReplayCacheDirectory);
            coordinator.RequireAdmission(settings.ReplayCacheDirectory, settings.OutputDirectory, settings);
            CaptureSession.StartingForChecks = capture => { source = capture; source.RetainEvidence = true; captureClock.Start(); };
            CaptureSession.StartedForChecks = capture => ready.TrySetResult(capture);
            RecorderRuntime.CaptureArgumentObserver = _ => Require(Interlocked.Increment(ref acquisitions) == 1, "A second acquisition is forbidden.");
            RecorderRuntime.ExportArgumentObserver = _ => throw new IOException("This control cannot export.");
            engine = new ReplayEngine(settings, data, true, false, null, coordinator);
            engine.CaptureStatusChanged += () => { if (coordinator.Stopped && engine.Paused) Interlocked.CompareExchange(ref pauseAt, Tick(), 0); };
            engine.Pause(false); Wait(() => ready.Task.IsCompleted, 15000, "Real capture bootstrap deadline."); source = ready.Task.GetAwaiter().GetResult();
            audio = (LoopbackAudio)typeof(CaptureSession).GetField("audio", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(source);
            var sourceProcess = (Process)typeof(CaptureSession).GetField("process", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(source);
            DateTime birth = sourceProcess.StartTime.ToUniversalTime();
            ffmpeg = Process.GetProcessById(sourceProcess.Id); _ = ffmpeg.SafeHandle;
            Require(ffmpeg.StartTime.ToUniversalTime() == birth && !sourceProcess.HasExited, "Exact retained source process birth required.");
            Require(string.Equals(ffmpeg.MainModule.FileName, MediaDependency.ResolveExecutable(), StringComparison.OrdinalIgnoreCase), "Exact retained FFmpeg image required.");
            Wait(() => source.Metrics.EncodedFrames >= 60 && source.Metrics.CaptureProgressSeconds >= 2, 5000, "Capture must advance before the fault.");
            Require(audio != null && !audio.ProducerAbsent && !ffmpeg.HasExited && !coordinator.Stopped && !engine.Paused, "Actual video/audio-live witness required.");
            var before = source.Metrics; string anchorIdentity = Native.DirectoryIdentity(source.DirectoryPath); Volatile.Write(ref files.Armed, 1);
            Wait(() => files.Entered.IsSet, 1000, "Next actual watcher capacity query did not enter.");
            var epochField = typeof(ReplayStorageCoordinator).GetField("lastFresh", BindingFlags.Instance | BindingFlags.NonPublic);
            epoch = (long)epochField.GetValue(coordinator); Require(epoch <= files.EnteredAt, "Genuine capacity epoch must precede blocked query.");
            Wait(() =>
            {
                if (ffmpeg.HasExited && videoAbsentAt == 0) videoAbsentAt = Tick();
                if (audio.ProducerAbsent && audioAbsentAt == 0) audioAbsentAt = Tick();
                return pauseAt != 0 && videoAbsentAt != 0 && audioAbsentAt != 0 && source.CaptureProducerAbsent;
            }, 7000, "Real video/audio drain did not finish while the query remained blocked.");
            Require(!files.Returned.IsSet && !files.TimedOut && coordinator.Stopped && coordinator.CapturePending && engine.Paused
                && (long)epochField.GetValue(coordinator) == epoch, "Pending query/ownership/freshness witness changed.");
            var firstHold = coordinator.Hold;
            var ages = RequireExpiry(epoch, files.EnteredAt, pauseAt, firstHold.Reason);
            string holdId = firstHold.HoldId;
            byte[] blockedLedger = Read(Path.Combine(authority, "replay-volume-reservations.json"), 8 * 1024 * 1024);
            var producer = source.ProducerProof;
            RequireClaim(blockedLedger, holdId, source.DirectoryPath, qualification.Model.CaptureHeadroom, producer, anchorIdentity);
            long claimObservedAt = Tick();
            WriteBytes(root, "blocked-ledger.json", blockedLedger);
            string blockedLedgerSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(blockedLedger));
            Write(root, "blocked-claim-witness.json", new { claimObservedAt, epoch, files.EnteredAt, pauseAt, videoAbsentAt, audioAbsentAt,
                holdId, producer, sourceDirectory = source.DirectoryPath, anchorIdentity, headroom = qualification.Model.CaptureHeadroom,
                ffmpegPid = ffmpeg.Id, ffmpegBirthUtc = birth, blockedLedgerSha256, firstHold,
                queryReturned = files.Returned.IsSet, queryTimedOut = files.TimedOut, capturePending = coordinator.CapturePending,
                ffmpegAbsent = ffmpeg.HasExited, audioAbsent = audio.ProducerAbsent });
            Require(!files.Returned.IsSet && !files.TimedOut && coordinator.CapturePending && (long)epochField.GetValue(coordinator) == epoch,
                "Query/claim witness expired during bounded evidence writes.");
            string blockedWitnessSha256 = ReplayWorkerLatencyPilot.Hash(Path.Combine(root, "blocked-claim-witness.json"));
            claimWitness = true; files.Release.Set();
            Wait(() => files.Returned.IsSet, 1000, "Released real query did not return.");
            Require(!files.TimedOut && files.Error == null && files.Actual != null && files.Actual.Volume == qualification.Model.Volume
                && files.Actual.AllocationUnit == qualification.Model.AllocationUnit && files.Actual.AvailableBytes >= Floor, "Released BASE query must actually succeed; timeout/failure invalidates this control.");
            // Closure retry is explicit, uses genuine producer absence, and may
            // race the engine's own bounded closer. It is not an auto-close claim.
            Wait(() => { if (coordinator.CapturePending) { explicitClosureRetry = true; coordinator.CompleteCapture(source.CaptureProducerAbsent); } return !coordinator.CapturePending; }, 3000, "Actual capacity observer did not close.");
            closureAt = Tick();
            var durable = new ReplayStorageStopLatch(data, authority).Read(); Require(durable.Held && durable.Durable && durable.HoldId == holdId && durable.Reason == ExpiryReason, "Exact durable expiry stop must survive full closure.");
            byte[] closedLedger = Read(Path.Combine(authority, "replay-volume-reservations.json"), 8 * 1024 * 1024);
            using (var closed = JsonDocument.Parse(closedLedger)) Require(!closed.RootElement.GetProperty("Owners").EnumerateArray().Any(o => o.GetProperty("Owner").GetString() == holdId), "Closed capture growth claim was not released.");
            WriteBytes(root, "closed-ledger.json", closedLedger);
            string closedLedgerSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(closedLedger));
            disposal = Task.Run(engine.Dispose); Wait(() => disposal.IsCompleted, 3000, "Engine retirement did not join."); disposal.GetAwaiter().GetResult(); disposed = true;
            Require(ffmpeg.HasExited && ffmpeg.ExitCode == 0 && audio.ProducerAbsent && acquisitions == 1, "Normal real producer closure required.");
            Require(fixture.CloseMainWindow() && fixture.WaitForExit(5000) && fixture.ExitCode == 0, "Normal fixture closure required.");
            using (var restored = JsonDocument.Parse(Read(Path.Combine(fixtureRoot, "fixture-display-exit.json"), 65536))) Require(restored.RootElement.GetProperty("restored").GetBoolean(), "Fixture wake lease restoration required.");
            Bindings(); Guard(false);
            Write(root, "result.json", new { result = "real-capacity-expiry-drain-observed", frequency = Stopwatch.Frequency, epoch, files.EnteredAt, pauseAt, videoAbsentAt, audioAbsentAt, files.ReturnedAt, closureAt,
                ffmpegPid = ffmpeg.Id, ffmpegBirthUtc = birth, ffmpegExitCode = ffmpeg.ExitCode, actualAudioAbsent = audio.ProducerAbsent, source.CaptureProducerAbsent,
                before, ageAtEntryMs = ages.Entry, ageAtPauseMs = ages.Pause, firstStopReason = firstHold.Reason, files.Calls, files.Blocked, files.TimedOut, files.Error, files.Actual,
                blockedLedgerSha256, blockedWitnessSha256, closedLedgerSha256, claimObservedAt, claimWitness, explicitClosureRetry, durable, acquisitions, peakBytes, peakFiles, maximumScanMs,
                interpretation = "Actual engine pause fence, exact retained FFmpeg exit and audio-thread absence are separate sampled observations. One real query was delayed; its result never renews an expired epoch. Closure-only retry is disclosed. No universal drain bound, saved-output accuracy, save latency or resource acceptance." });
        }
        catch (Exception error) { failure = error; Console.Error.WriteLine(error); }
        finally
        {
            files.Release.Set();
            if (!disposed && engine != null)
            {
                try { disposal ??= Task.Run(engine.Dispose); if (!disposal.Wait(15000)) throw new IOException("Engine cleanup remained unjoined."); disposal.GetAwaiter().GetResult(); disposed = true; }
                catch (Exception error) { cleanup.Add("engine: " + error.Message); }
            }
            if (fixture != null)
                try { if (!fixture.HasExited) { fixture.CloseMainWindow(); if (!fixture.WaitForExit(2000)) throw new IOException("Fixture cleanup remained live; outer retained owner must stop it."); } }
                catch (Exception error) { cleanup.Add("fixture: " + error.Message); }
            if (cleanup.Count != 0) failure ??= new IOException(string.Join("; ", cleanup));
            try
            {
                Guard(false, true); Bindings();
                Write(root, "closure.json", new { error = failure?.ToString(), cleanup, engineJoined = disposed, captureAbsent = source?.CaptureProducerAbsent ?? true,
                    ffmpegAbsent = ffmpeg?.HasExited ?? true, audioAbsent = audio?.ProducerAbsent ?? true, fixtureAbsent = fixture?.HasExited ?? true,
                    queryReturned = files.Blocked == 0 || files.Returned.IsSet, files.TimedOut, claimWitness, explicitClosureRetry, peakBytes, peakFiles, maximumScanMs });
                lastScan = -1; Guard(false, true);
            }
            catch (Exception error) { failure ??= error; Console.Error.WriteLine("Terminal closure rejected: " + error.Message); }
            if (disposed || engine == null)
            {
                CaptureSession.StartingForChecks = null; CaptureSession.StartedForChecks = null; RecorderRuntime.CaptureArgumentObserver = null; RecorderRuntime.ExportArgumentObserver = null;
                ReplayCalibrationCatalog.CandidateForChecks = null; ProfileOwnership.RootDirectory = previousAuthority; Log.DirectoryPath = previousLog; Log.FileName = previousLogFile;
            }
            ffmpeg?.Dispose(); fixture?.Dispose(); coordinator?.Dispose();
        }
        return failure == null ? 0 : 1;
    }
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
}
