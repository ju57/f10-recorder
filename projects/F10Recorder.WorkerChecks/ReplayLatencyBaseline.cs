using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Forms = System.Windows.Forms;
using Microsoft.Win32;

namespace F10Recorder;

// Measurement-only host. Build with QualificationProductDirectory to execute the
// unchanged qualified Windows/Core bytes. Never imports a model into new bytes.
internal static class ReplayLatencyBaseline
{
    const long Cap = 12_000_000_000, Stop = 11_000_000_000, Floor = 60_000_000_000;
    static readonly JsonSerializerOptions Json = new(WorkerProtocol.Json) { WriteIndented = true };
    static string Hash(string path) { using var f = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(f)); }
    static void Require(bool value, string message) { if (!value) throw new IOException(message); }
    static void Write(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, Json));
    static string Startup()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        object value = key?.GetValue("F10Recorder", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return JsonSerializer.Serialize(new { value, kind = value == null ? null : key.GetValueKind("F10Recorder").ToString() });
    }
    internal static int Dispatch(string[] args)
    {
        if (args.Length == 2 && args[0] == "--replay-latency-fixture") return CalibrationPattern.Run(args[1], "H02", lifetimeSeconds: 620);
        if (args.Length == 5 && args[0] == "--replay-latency-pilot-prepare") return ReplayLatencyPilots.Prepare(args[1], args[2], args[3], args[4]);
        if (args.Length == 6 && args[0] == "--replay-latency-pilot-prepare") return ReplayLatencyPilots.Prepare(args[1], args[2], args[3], args[4], args[5]);
        if (args.Length == 3 && args[0] == "--replay-latency-pilot-run") return ReplayLatencyPilots.Run(args[1], args[2]);
        if (args.Length == 6 && args[0] == "--replay-latency-prepare") return Prepare(args[1], args[2], args[3], args[4], args[5]);
        if (args.Length == 7 && args[0] == "--replay-latency-prepare") return Prepare(args[1], args[2], args[3], args[4], args[5], args[6]);
        if (args.Length == 3 && args[0] == "--replay-latency-run") return Run(args[1], args[2]);
        return 2;
    }
    static int Prepare(string planFile, string root, string engineRoot, string product, string productionData, string priorPhase = null)
    {
        foreach (string p in new[] { planFile, root, engineRoot, product, productionData }) Require(Path.IsPathFullyQualified(p), "Absolute paths required.");
        Require(!File.Exists(planFile) && !Directory.Exists(root) && !File.Exists(root), "Fresh plan and root required.");
        MediaDependency.RootDirectory = engineRoot;
        string engine = MediaDependency.ResolveExecutable();
        Require(Hash(Path.Combine(product, "F10Recorder.Windows.dll")) == Hash(typeof(CaptureSession).Assembly.Location)
            && Hash(Path.Combine(product, "F10Recorder.Core.dll")) == Hash(typeof(Settings).Assembly.Location), "Use unchanged qualified product bytes.");
        var model = ReplayCalibrationCatalog.Load(); Require(model.Model != null, model.Reason);
        long forward = checked(2_000_000_000 + 2 * ReplayReferenceModel.SnapshotEnvelope(model.Model.AllocationUnit)
            + 4 * model.Model.PeakBytes(ReplayReferenceModel.MaximumInputBytes) + 268_435_456);
        JsonElement? prior = priorPhase == null ? null : JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(priorPhase));
        long retainedBytes = prior?.GetProperty("retainedLogicalBytes").GetInt64() ?? 0;
        Require(retainedBytes >= 0 && forward + retainedBytes + 1_000_000_000 <= Stop, "Entire two-save schedule, retained attempts and closure reserve must fit before capture.");
        string ancestor = Path.GetDirectoryName(root);
        Require(Directory.Exists(ancestor) && ReplayCalibrationCatalog.IsApplicableToDestination(model.Model, ancestor), "Qualified volume required.");
        var bounds = Forms.Screen.PrimaryScreen.Bounds;
        using var audio = new LoopbackAudio();
        Require(bounds.Width == 1920 && bounds.Height == 1080 && audio.SampleRate == 48000 && audio.Channels == 2, "Unchanged reference display/audio required.");
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string build = Path.GetDirectoryName(typeof(ReplayLatencyBaseline).Assembly.Location);
        foreach (string file in new[] { Environment.ProcessPath, typeof(ReplayLatencyBaseline).Assembly.Location,
            typeof(CaptureSession).Assembly.Location, typeof(Settings).Assembly.Location,
            Path.Combine(build, "F10Recorder.WorkerChecks.deps.json"), Path.Combine(build, "F10Recorder.WorkerChecks.runtimeconfig.json"), engine,
            Path.Combine(TrustedStorage.DefaultRoot, "storage-calibration", ReplayCalibrationCatalog.FileName),
            Path.Combine(productionData, "settings.json"), Path.Combine(productionData, "worker-state.json"),
            Path.GetFullPath("projects/F10Recorder.WorkerChecks/ReplayLatencyBaseline.cs"),
            Path.GetFullPath("projects/F10Recorder.WorkerChecks/CalibrationPattern.cs"), Path.GetFullPath("tests/replay-latency-baseline.ps1") })
            files[Path.GetFullPath(file)] = Hash(file);
        if (priorPhase != null)
        {
            files[Path.GetFullPath(priorPhase)] = Hash(priorPhase);
            foreach (var file in prior.Value.GetProperty("files").EnumerateObject())
            { Require(Hash(file.Name) == file.Value.GetString(), "Prior phase receipt changed."); files[file.Name] = file.Value.GetString(); }
            Require(prior.Value.GetProperty("ownedProducersAbsent").GetBoolean(), "Prior producers must be closed before repeat.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(planFile));
        Write(planFile, new { kind = "issue83-baseline-v1", root, engineRoot, product, productionData, files,
            maximumChildren = 7, maximumAcquisitions = 1, priorPhase = prior,
            priorPreparationHelpers = prior == null ? 1 : 0, preparationHelpers = 1,
            cumulativeNativeHelpersIncludingPreparation = (prior?.GetProperty("nativeHelpersCharged").GetInt32() ?? 1) + 1 + 7,
            cumulativeAcquisitions = (prior?.GetProperty("acquisitionsCharged").GetInt32() ?? 0) + 1,
            retainedBytes,
            captureSeconds = 580, deadlineSeconds = 900,
            budgetBytes = Cap, stopBytes = Stop, minimumFreeBytes = Floor, finalizationReserveBytes = 1_000_000_000,
            expectedForwardBytes = forward, componentBytes = new { capture = 2_000_000_000,
                snapshot = 2 * ReplayReferenceModel.SnapshotEnvelope(model.Model.AllocationUnit),
                fourLogicalOutputAliases = 4 * model.Model.PeakBytes(ReplayReferenceModel.MaximumInputBytes), metadata = 268_435_456 },
            maximumFiles = 8192, maximumDirectories = 512, sampleOrder = new[] { "first-use", "repeat-use" },
            maximumExternalObservationGapMilliseconds = 1000, maximumExternalScanMilliseconds = 500,
            preservation = "Closed pre-validation output hardlink retained (charged again logically); full process diagnostics/arguments retained on failure. An encoder failure before validation may remove its partial under unchanged ordinary cleanup; its inputs/arguments/receipt remain. No failing/slow attempt is retried or omitted.",
            claim = "Two-observation real-worker stage baseline only; no median/p95 or uncached-cold claim. No qualification transferred.",
            environment = new { os = Environment.OSVersion.VersionString, machine = Environment.MachineName, cpus = Environment.ProcessorCount,
                bounds.Width, bounds.Height, audio.EndpointId, audio.SampleRate, audio.Channels, capacity = ReplayStorage.ObserveCapacity(ancestor) },
            effects = "One owned fullscreen moving fixture and quiet chirp, primary monitor pixels/default system audio, one ordinary worker in helper process, two NVENC exports/two full software validators. Private profile and authority. No UI attachment, startup write, installed recorder/profile/vendor change, installation or catalog activation.",
            startup = Startup(), createdUtc = DateTime.UtcNow });
        Console.WriteLine(planFile + " " + Hash(planFile)); return 0;
    }
    static int Run(string planFile, string expectedHash)
    {
        Require(Hash(planFile) == expectedHash && new FileInfo(planFile).Length <= 65536, "Reviewed plan changed.");
        using var document = JsonDocument.Parse(File.ReadAllText(planFile)); var plan = document.RootElement;
        Require(plan.GetProperty("kind").GetString() == "issue83-baseline-v1", "Wrong plan.");
        Require(plan.GetProperty("maximumChildren").GetInt32() == 7 && plan.GetProperty("maximumAcquisitions").GetInt32() == 1
            && plan.GetProperty("captureSeconds").GetInt32() == 580 && plan.GetProperty("deadlineSeconds").GetInt32() == 900
            && plan.GetProperty("budgetBytes").GetInt64() == Cap && plan.GetProperty("stopBytes").GetInt64() == Stop
            && plan.GetProperty("minimumFreeBytes").GetInt64() == Floor
            && plan.GetProperty("sampleOrder").EnumerateArray().Select(x => x.GetString()).SequenceEqual(new[] { "first-use", "repeat-use" }), "Admission boundary changed.");
        Require(Startup() == plan.GetProperty("startup").GetString(), "Startup changed before launch.");
        foreach (var f in plan.GetProperty("files").EnumerateObject()) Require(Hash(f.Name) == f.Value.GetString(), "Bound input changed: " + f.Name);
        string root = plan.GetProperty("root").GetString(), engineRoot = plan.GetProperty("engineRoot").GetString();
        Require(!Directory.Exists(root) && DateTime.UtcNow - plan.GetProperty("createdUtc").GetDateTime() < TimeSpan.FromHours(4), "Fresh root/current admission required.");
        MediaDependency.RootDirectory = engineRoot;
        Require(ReplayCalibrationCatalog.CandidateForChecks == null && ReplayCalibrationCatalog.Load().Model != null, "Ordinary unchanged catalog required.");
        Directory.CreateDirectory(root); File.Copy(planFile, Path.Combine(root, "admitted-plan.json"));
        string data = Path.Combine(root, "profile"), fixtureRoot = Path.Combine(root, "fixture");
        Directory.CreateDirectory(data); Directory.CreateDirectory(fixtureRoot);
        string previousAuthority = ProfileOwnership.RootDirectory;
        ProfileOwnership.RootDirectory = Path.Combine(root, "authority");
        var settings = new Settings { ReplayMinutes = 5, FramesPerSecond = 60, BitrateMbps = 20, VideoCodec = "h264_nvenc", SystemAudio = true,
            CaptureMode = "monitor", DisplayIndex = -1, OutputDirectory = Path.Combine(root, "output"), ReplayCacheDirectory = Path.Combine(root, "cache"),
            ScreenshotDirectory = Path.Combine(root, "screenshots"), SaveModifiers = 7, OverlayModifiers = 7, ScreenshotModifiers = 7,
            SaveKey = 121, OverlayKey = 120, ScreenshotKey = 119 };
        RecordingProfile.Load(data).Commit(settings, true, true, false);
        var total = Stopwatch.StartNew(); int children = 1, acquisitions = 0, exports = 0, validators = 0, workerExit = -1;
        CaptureSession capture = null; Thread worker = null; Process fixture = null; Exception failure = null;
        var ready = new TaskCompletionSource<CaptureSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var results = new List<object>(); var attempts = new List<object>(); var priorFinals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var metrics = new List<object>(); var snapshots = new List<string>();
        long exportAt = 0, validationAt = 0; double lastScan = -1; long maxBytes = 0; int maxFiles = 0;
        long previousObservation = 0; double maximumObservationGap = 0, maximumScan = 0;
        void Child(string role) { Require(Interlocked.Increment(ref children) <= 7, "Child ceiling before launch: " + role); }
        void Guard(bool foreground = true)
        {
            long now = Stopwatch.GetTimestamp();
            bool acquiring = capture != null && !capture.CaptureProducerAbsent;
            if (acquiring && previousObservation != 0)
            {
                double gap = Stopwatch.GetElapsedTime(previousObservation, now).TotalMilliseconds;
                maximumObservationGap = Math.Max(gap, maximumObservationGap);
                Require(gap <= 1000, "External observation gap exceeded one second; result remains failed.");
            }
            previousObservation = acquiring ? now : 0;
            Require(total.Elapsed.TotalSeconds < 880, "Whole baseline deadline (20s cleanup reserved).");
            if (capture != null && !capture.CaptureProducerAbsent) Require(total.Elapsed.TotalSeconds < 580, "Capture deadline.");
            if (foreground && fixture != null) Require(!fixture.HasExited && GetForegroundWindow() == fixture.MainWindowHandle, "Owned source lost foreground; no retry.");
            if (total.Elapsed.TotalSeconds - lastScan >= .5)
            {
                var scan = Stopwatch.StartNew();
                lastScan = total.Elapsed.TotalSeconds;
                var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
                Require(files.Length <= 8192 && Directory.GetDirectories(root, "*", SearchOption.AllDirectories).Length <= 512, "File/directory cap.");
                // Logical aliases are deliberately overcharged; actual product uses native identity accounting.
                long bytes = 0;
                foreach (string f in files)
                    try { bytes = checked(bytes + new FileInfo(f).Length); }
                    catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { /* Worker temp replacement/pruning removed this enumerated path. */ }
                maxBytes = Math.Max(maxBytes, bytes); maxFiles = Math.Max(maxFiles, files.Length);
                Require(bytes + plan.GetProperty("retainedBytes").GetInt64() < Stop && ReplayStorage.ObserveCapacity(root).AvailableBytes >= Floor, "Storage stop/floor.");
                maximumScan = Math.Max(maximumScan, scan.Elapsed.TotalMilliseconds);
                Require(scan.Elapsed.TotalMilliseconds <= 500, "External storage scan cost exceeded 500ms; unqualified observer.");
            }
        }
        WorkerReply Call(string command = "status", bool value = false, long after = -1)
        {
            using var deadline = new CancellationTokenSource(command == "status" ? 1000 : 15000);
            var pending = WorkerProtocol.CallAsync(data, new WorkerRequest { Command = command, Value = value, AfterEvent = after }, deadline.Token);
            while (!pending.IsCompleted) { Guard(command is not ("pause" or "quit")); Thread.Sleep(20); }
            var reply = pending.GetAwaiter().GetResult();
            Require(reply.Error == null, reply.Error); return reply;
        }
        WorkerStatus Poll(long after = -1)
        {
            Guard(); var status = Call(after: after).Status; var m = status.Metrics;
            Require(!m.ReplayStorageSafetyHeld && m.Recoveries == 0 && acquisitions <= 1, "Safety stop/recovery/new acquisition.");
            Require(metrics.Count < 10000, "Metric row cap."); metrics.Add(new { qpc = Stopwatch.GetTimestamp(), status }); return status;
        }
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = false, CreateNoWindow = true };
            if (Path.GetFileNameWithoutExtension(Environment.ProcessPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(ReplayLatencyBaseline).Assembly.Location);
            start.ArgumentList.Add("--replay-latency-fixture"); start.ArgumentList.Add(fixtureRoot);
            Child("fixture"); fixture = Process.Start(start);
            while (!File.Exists(Path.Combine(fixtureRoot, "fixture-ready.json"))) { Guard(false); Require(total.Elapsed.TotalSeconds < 20 && !fixture.HasExited, "Fixture startup deadline."); Thread.Sleep(50); }
            fixture.Refresh(); IntPtr hwnd = fixture.MainWindowHandle;
            Require(hwnd != IntPtr.Zero, "Owned fixture window missing."); ShowWindow(hwnd, 5); bool requestedForeground = SetForegroundWindow(hwnd);
            Require(GetForegroundWindow() == hwnd, "Normal foreground request refused; no capture admitted.");
            Write(Path.Combine(root, "foreground.json"), new { hwnd = hwnd.ToInt64(), fixture.Id, requestedForeground });
            RecorderRuntime.CaptureArgumentObserver = args => { Child("capture"); Require(Interlocked.Increment(ref acquisitions) == 1, "No second acquisition."); Write(Path.Combine(root, "capture-arguments.json"), args); };
            CaptureSession.StartingForChecks = source => { capture = source; source.RetainEvidence = true;
                source.ValidationArgumentObserver = args => { Child("validator"); Require(++validators <= 2, "Validator ceiling."); Interlocked.Exchange(ref validationAt, Stopwatch.GetTimestamp());
                    string partial = args[args.ToList().IndexOf("-i") + 1];
                    Require(Native.CreateHardLink(Path.Combine(root, "closed-output-" + validators + ".mp4"), partial, IntPtr.Zero),
                        "Closed output evidence link failed before validation; no qualification."); }; };
            CaptureSession.StartedForChecks = source => ready.TrySetResult(source);
            RecorderRuntime.ExportArgumentObserver = args => { Child("export"); Require(++exports <= 2, "Export ceiling.");
                Interlocked.Exchange(ref exportAt, Stopwatch.GetTimestamp()); string concat = args[args.ToList().IndexOf("-i") + 1];
                lock (snapshots) snapshots.Add(Path.GetDirectoryName(concat)); };
            worker = new Thread(() => { try { workerExit = RecorderWorker.Run(data, true, setStartup: _ => throw new IOException("Startup forbidden."),
                screenshotSource: (_, _) => throw new IOException("Screenshot forbidden."), repairEngine: () => throw new IOException("Engine provisioning forbidden.")); }
                catch (Exception e) { ready.TrySetException(e); } }); worker.SetApartmentState(ApartmentState.STA); worker.Start();
            while (true) { Guard(); try { Call(); break; } catch (Exception e) when (e is TimeoutException or OperationCanceledException) { Require(total.Elapsed.TotalSeconds < 30, "Worker startup."); } Thread.Sleep(100); }
            Call("pause", false);
            while (!ready.Task.IsCompleted) { Guard(); Require(total.Elapsed.TotalSeconds < 45, "Capture bootstrap deadline."); Thread.Sleep(100); }
            capture = ready.Task.GetAwaiter().GetResult(); int capturePid = capture.ProcessId;
            while (Poll().Metrics.BufferedSeconds < 300) { Thread.Sleep(100); }
            foreach (string label in new[] { "first-use", "repeat-use" })
            {
                var before = Poll(); Require(before.Ready && before.CanSave && !before.Saving, "Unqueued live save required.");
                long sequence = before.Sequence, requestAt = Stopwatch.GetTimestamp();
                var ack = Call("save", after: sequence); long ackAt = Stopwatch.GetTimestamp();
                attempts.Add(new { label, requestAt, ackAt, ack });
                Require(ack.ReplayAccepted == true, ack.ReplayRejection ?? "Save rejected.");
                string final = null; long firstFinalAt = 0, savedAt = 0; WorkerStatus done = null;
                while (true)
                {
                    var status = Poll(sequence);
                    Require(status.Metrics.CaptureProcessId == capturePid && capture.ReadHealth().Healthy, "Capture must continue through Saved.");
                    if (firstFinalAt == 0 && Directory.Exists(settings.OutputDirectory))
                    { var finals = Directory.GetFiles(settings.OutputDirectory, "*.mp4", SearchOption.AllDirectories).Where(f => !f.Contains(".partial.", StringComparison.OrdinalIgnoreCase)).ToArray();
                        var newest = finals.Where(f => !priorFinals.Contains(f)).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault(); if (newest != null) { firstFinalAt = Stopwatch.GetTimestamp(); final = newest; } }
                    var saved = status.Events.FirstOrDefault(e => e.Sequence > sequence && e.Kind == "saved");
                    if (saved != null) { savedAt = Stopwatch.GetTimestamp(); Require(saved.Success, saved.Message); final = saved.Message; done = status; break; }
                    Require(Stopwatch.GetElapsedTime(requestAt).TotalSeconds < 150, "Save deadline."); Thread.Sleep(50);
                }
                Require(File.Exists(final) && done.Metrics.LastSaveValidation == "FullDecode" && capture.LastExport.Success && capture.LastValidation.Success, "Validated publication missing.");
                Require(done.Metrics.CaptureProgressSeconds > before.Metrics.CaptureProgressSeconds
                    && done.Metrics.EncodedFrames > before.Metrics.EncodedFrames, "Capture must progress across each save.");
                Require(!done.Metrics.LastSaveLimitedHistory && Math.Abs(done.Metrics.LastSaveAvailableSeconds - 300) <= .000001,
                    "Full five-minute interval required; warm-up must remain truthful.");
                priorFinals.Add(final);
                var selection = capture.LastOutputSelection;
                results.Add(new { label, requestAt, ackAt, exportAt, validationAt, firstFinalAt, savedAt, final, queueMilliseconds = 0,
                    requestToSavedMilliseconds = Stopwatch.GetElapsedTime(requestAt, savedAt).TotalMilliseconds,
                    ackMilliseconds = Stopwatch.GetElapsedTime(requestAt, ackAt).TotalMilliseconds,
                    requestToExportMilliseconds = Stopwatch.GetElapsedTime(requestAt, exportAt).TotalMilliseconds,
                    export = capture.LastExport, validation = capture.LastValidation, arguments = capture.ExportArguments,
                    validationArguments = capture.ValidationArguments, ack, done, selection,
                    captureFrameDelta = done.Metrics.EncodedFrames - before.Metrics.EncodedFrames,
                    captureDropDelta = done.Metrics.EncoderReportedDrops - before.Metrics.EncoderReportedDrops,
                    captureDuplicateDelta = done.Metrics.EncoderReportedDuplicates - before.Metrics.EncoderReportedDuplicates,
                    stageLimits = "Unchanged product has no internal tail/protection/publication timing hooks. Request→export includes closure+protection+admission; postvalidation→Saved includes release/rename/event/poll. firstFinalAt is a poll upper bound. No component is subtracted from request→Saved." });
                Write(Path.Combine(root, label + ".json"), results[^1]); Thread.Sleep(250);
            }
            Call("pause", true);
            while (!capture.CaptureProducerAbsent) { Guard(false); Thread.Sleep(50); }
            Call("quit"); Require(worker.Join(15000) && workerExit == 0 && capture.CaptureExitCode == 0, "Normal producer/worker closure required.");
            Require(!new ReplayStorageStopLatch(data, ProfileOwnership.RootDirectory).Read().Held, "Durable hold after worker join.");
            fixture.CloseMainWindow(); Require(fixture.WaitForExit(5000) && fixture.ExitCode == 0, "Normal fixture exit required.");
            using (var restored = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixtureRoot, "fixture-display-exit.json"))))
                Require(restored.RootElement.GetProperty("restored").GetBoolean(), "Fixture wake lease not restored.");
            Require(Startup() == plan.GetProperty("startup").GetString()
                && plan.GetProperty("files").EnumerateObject().All(f => File.Exists(f.Name) && Hash(f.Name) == f.Value.GetString()), "Bound inputs/startup changed; no successful baseline.");
            Write(Path.Combine(root, "result.json"), new { result = "baseline-collected", results, children, acquisitions, exports, validators, workerExit, maxBytes, maxFiles,
                captureAbsent = capture.CaptureProducerAbsent, sourcePath = capture.DirectoryPath, retainedSnapshots = snapshots,
                interpretation = "First/repeated product-path observations only. No p95, uncached-cold, independent source-content or optimized-candidate acceptance claim." });
            return 0;
        }
        catch (Exception e) { failure = e; Console.Error.WriteLine(e); return 1; }
        finally
        {
            // Write before teardown as well as after join: failures retain the
            // actual last stage rather than disappearing with ordinary cleanup.
            try { Write(Path.Combine(root, "last-stage.json"), new { failure = failure?.ToString(), attempts,
                    export = capture?.LastExport, validation = capture?.LastValidation,
                    exportArguments = capture?.ExportArguments, validationArguments = capture?.ValidationArguments,
                    selection = capture?.LastOutputSelection }); }
            catch (Exception evidenceError) { Console.Error.WriteLine("Failed to preserve pre-teardown receipt: " + evidenceError.Message); }
            if (worker?.IsAlive == true)
            {
                try { Call("pause", true); } catch (Exception) { }
                try { Call("quit"); } catch (Exception) { }
                if (!worker.Join(15000)) { capture?.StopCapture(); failure ??= new IOException("Worker join failed; outer controller must terminate owned helper."); }
            }
            if (fixture != null) { if (!fixture.HasExited) { fixture.CloseMainWindow(); if (!fixture.WaitForExit(5000)) { fixture.Kill(); fixture.WaitForExit(5000); } } fixture.Dispose(); }
            Write(Path.Combine(root, "metrics.json"), metrics);
            Write(Path.Combine(root, "closure.json"), new { error = failure?.ToString(), workerJoined = worker?.IsAlive != true, workerExit,
                captureAbsent = capture?.CaptureProducerAbsent ?? true, children, acquisitions, exports, validators, maxBytes, maxFiles,
                maximumObservationGap, maximumScan,
                startupPreserved = Startup() == plan.GetProperty("startup").GetString(),
                boundInputsPreserved = plan.GetProperty("files").EnumerateObject().All(f => File.Exists(f.Name) && Hash(f.Name) == f.Value.GetString()) });
            if (worker?.IsAlive != true) { CaptureSession.StartingForChecks = null; CaptureSession.StartedForChecks = null; RecorderRuntime.CaptureArgumentObserver = null;
                RecorderRuntime.ExportArgumentObserver = null; ProfileOwnership.RootDirectory = previousAuthority; }
        }
    }
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr window, int command);
}
