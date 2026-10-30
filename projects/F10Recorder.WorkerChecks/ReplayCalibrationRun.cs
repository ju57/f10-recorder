using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Forms = System.Windows.Forms;

namespace F10Recorder;

internal sealed record CalibrationPlan(string Root, string EngineRoot, string Environment,
    Dictionary<string, string> Files, string[] Order, string[] RetainedRoots, double RetainedElapsedSeconds, int RetainedChildren,
    int MaximumChildren = 46, long BudgetBytes = 64_000_000_000,
    long StopBytes = 62_500_000_000, long FloorBytes = 60_000_000_000, int DeadlineSeconds = 9000,
    double? RetainedOfflineSeconds = null, string RehearsalEvidenceRoot = null, double RehearsalElapsedSeconds = 0,
    string[] MetadataHandleRoots = null, bool ReuseCurrentMetadataHandles = false,
    int DirectoryLimit = 48, long OutputComponentBytes = 1_032_000_000, long RetainedBudgetBytes = 32_000_000_000,
    int FileLimit = 4096, int OfflineDeadlineSeconds = 1800, long OutsideBuildAllowanceBytes = 1_000_000_000);

// Purpose-specific controller. It never provisions tools, edits ordinary profiles,
// runs games, changes NVIDIA/display/audio preferences or retries a failed batch.
internal static class ReplayCalibrationRun
{
    internal const int MainInspectionSeconds = 60, ShortInspectionSeconds = 30, ArchiveSeconds = 60;
    internal const int CompleteOfflineBatchSeconds = 6 * MainInspectionSeconds + 2 * ShortInspectionSeconds + 8 * ArchiveSeconds;
    internal const int ReplacementVerificationSeconds = 60 + 120; // Preflight plus retained-output/preservation audit.
    internal const int FullReplacementVerificationSeconds = 150 + 240;
    internal const int FullReplacementWallSeconds = 4200;
    const int InspectionCleanupSeconds = 10;
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, IncludeFields = true };
    static readonly string[] Order = ["T01", "T02", "T03", "H02", "H01", "H03"];
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    static void Require(bool value, string message) { if (!value) throw new IOException(message); }
    static string HashFile(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
    static void Write(string path, object value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        Require(bytes.Length <= 131072, "Calibration JSON exceeds128KiB.");
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        output.Write(bytes); output.Flush(true);
    }
    internal static int Prepare(string path, string engineRoot)
    {
        string assembly = typeof(ReplayCalibrationRun).Assembly.Location;
        string build = Path.GetDirectoryName(assembly);
        MediaDependency.RootDirectory = Path.GetFullPath(engineRoot);
        string engine = MediaDependency.ResolveExecutable(); // Read-only verification, no probe or repair.
        var files = new[] { Environment.ProcessPath, assembly, Path.Combine(build, "F10Recorder.Windows.dll"), Path.Combine(build, "F10Recorder.Core.dll"),
            Path.Combine(build, "F10Recorder.WorkerChecks.deps.json"), Path.Combine(build, "F10Recorder.WorkerChecks.runtimeconfig.json"), engine }
            .ToDictionary(Path.GetFullPath, HashFile, StringComparer.OrdinalIgnoreCase);
        string admission = Path.GetDirectoryName(Path.GetFullPath(path));
        foreach (string evidence in new[] { "source-manifest.json", "host-facts.json", "preservation-before.json", "RUN.md", "retained-attempt.json", "resources.json", "rehearsal.json" })
        {
            string file = Path.Combine(admission, evidence);
            Require(File.Exists(file), "Missing frozen admission evidence: " + file);
            files.Add(file, HashFile(file));
        }
        string dotnet = Path.GetDirectoryName(Environment.ProcessPath);
        foreach (string directory in new[] { Path.Combine(dotnet, "host", "fxr"), Path.Combine(dotnet, "shared", "Microsoft.NETCore.App"),
            Path.Combine(dotnet, "shared", "Microsoft.WindowsDesktop.App") })
            foreach (string name in new[] { "hostfxr.dll", "coreclr.dll", "hostpolicy.dll", "System.Private.CoreLib.dll", "System.Windows.Forms.dll", "System.Drawing.Common.dll", "System.Windows.Extensions.dll" })
                foreach (string file in Directory.GetFiles(directory, name, SearchOption.AllDirectories)) files.TryAdd(file, HashFile(file));
        using var endpoint = new LoopbackAudio(); // Mix-format query only, never Start.
        var bounds = Forms.Screen.PrimaryScreen.Bounds;
        Require(bounds.Width == 1920 && bounds.Height == 1080, "Reference requires current primary1920x1080; display changes are not permitted.");
        using var resourceRecord = JsonDocument.Parse(File.ReadAllText(Path.Combine(admission, "resources.json")));
        string root = Path.GetFullPath(resourceRecord.RootElement.GetProperty("experimentRoot").GetString());
        Require(!Directory.Exists(root) && !File.Exists(root), "Calibration needs its approved fresh experiment root.");
        var capacity = ReplayStorage.ObserveCapacity(Path.GetDirectoryName(root));
        Require(capacity.AvailableBytes >= checked(resourceRecord.RootElement.GetProperty("budgetBytes").GetInt64() + 60_000_000_000L),
            "Insufficient caller-free floor plus whole proposed batch budget.");
        var environment = new { os = Environment.OSVersion.VersionString, machine = Environment.MachineName,
            bounds.Width, bounds.Height, primaryOutput = Native.PrimaryOutputIndex(), endpoint.EndpointId, endpoint.SampleRate, endpoint.Channels, endpoint.FfmpegFormat, capacity };
        using var retained = JsonDocument.Parse(File.ReadAllText(Path.Combine(admission, "retained-attempt.json")));
        string[] retainedRoots = retained.RootElement.GetProperty("roots").EnumerateArray().Select(v => Path.GetFullPath(v.GetString())).ToArray();
        double retainedSeconds = retained.RootElement.GetProperty("elapsedSeconds").GetDouble();
        int retainedChildren = retained.RootElement.GetProperty("retainedChildren").GetInt32();
        double offline = retained.RootElement.GetProperty("offlineSeconds").GetDouble();
        var plan = new CalibrationPlan(root, MediaDependency.RootDirectory, ReplayReferenceModel.Hash(environment), files, Order, retainedRoots, retainedSeconds, retainedChildren,
            MaximumChildren: resourceRecord.RootElement.GetProperty("maximumChildren").GetInt32(),
            RetainedOfflineSeconds: offline, MetadataHandleRoots: retainedRoots, ReuseCurrentMetadataHandles: true,
            DirectoryLimit: resourceRecord.RootElement.TryGetProperty("directoryLimit", out var dl) ? dl.GetInt32() : 48,
            OutputComponentBytes: resourceRecord.RootElement.TryGetProperty("outputComponentBytes", out var ob) ? ob.GetInt64() : 1_032_000_000,
            RetainedBudgetBytes: resourceRecord.RootElement.TryGetProperty("retainedBudgetBytes", out var rb) ? rb.GetInt64() : 32_000_000_000,
            FileLimit: resourceRecord.RootElement.TryGetProperty("fileLimit", out var fl) ? fl.GetInt32() : 4096,
            OfflineDeadlineSeconds: resourceRecord.RootElement.GetProperty("offlineDeadlineSeconds").GetInt32(),
            OutsideBuildAllowanceBytes: resourceRecord.RootElement.GetProperty("outsideBuildAllowanceBytes").GetInt64());
        plan = plan with { BudgetBytes = resourceRecord.RootElement.GetProperty("budgetBytes").GetInt64(),
            StopBytes = resourceRecord.RootElement.GetProperty("stopBytes").GetInt64(), DeadlineSeconds = resourceRecord.RootElement.GetProperty("deadlineSeconds").GetInt32() };
        ValidateResources(plan);
        using var resources = JsonDocument.Parse(File.ReadAllText(Path.Combine(admission, "resources.json")));
        Require(resources.RootElement.GetProperty("budgetBytes").GetInt64() == plan.BudgetBytes
            && resources.RootElement.GetProperty("maximumChildren").GetInt32() == plan.MaximumChildren
            && resources.RootElement.GetProperty("deadlineSeconds").GetInt32() == plan.DeadlineSeconds, "Approved resource record differs from plan.");
        using var rehearsal = JsonDocument.Parse(File.ReadAllText(Path.Combine(admission, "rehearsal.json")));
        Require(rehearsal.RootElement.GetProperty("result").GetString() == "passed-full-nonmedia-rehearsal", "A complete successful full-size rehearsal is required.");
        Write(path, new { plan, environment, effects = Effects(plan), recipe = Recipe(plan), execution_requires_review = true });
        Console.WriteLine(path); return 0;
    }
    static object Effects(CalibrationPlan plan) => new { realPrimaryMonitor = true, realDefaultSystemAudio = true, quietChirpPlayback = true,
        gpuNvenc = true, fullScreenControlledFixture = true, startupWrites = false, installedProfileWrites = false,
        vendorPreferencesWrites = false, displayChanges = false, sourceSeconds = 310, sources = 6, maximumChildren = plan.MaximumChildren,
        hardBytes = plan.BudgetBytes, minimumFreeBytes = plan.FloorBytes, deadlineSeconds = plan.DeadlineSeconds };
    internal static void ValidateResources(CalibrationPlan plan)
    {
        bool cadenceReplacement = plan != null && plan.MaximumChildren == 423 && plan.RetainedChildren == 391
            && plan.BudgetBytes == 144_000_000_000 && plan.StopBytes == 142_500_000_000
            && plan.DeadlineSeconds == 46800 && plan.DirectoryLimit == 448 && plan.FileLimit == 24576 && plan.OfflineDeadlineSeconds == 28800
            && plan.OutputComponentBytes == 2_000_000_000 && plan.RetainedBudgetBytes == 124_000_000_000
            && plan.OutsideBuildAllowanceBytes == 2_000_000_000;
        bool foregroundReplacement = cadenceReplacement || plan != null && plan.MaximumChildren == 374 && plan.RetainedChildren == 342
            && plan.BudgetBytes == 128_000_000_000 && plan.StopBytes == 126_500_000_000
            && plan.DeadlineSeconds == 39600 && plan.DirectoryLimit == 352 && plan.FileLimit == 21504 && plan.OfflineDeadlineSeconds == 25200
            && plan.OutputComponentBytes == 2_000_000_000 && plan.RetainedBudgetBytes == 106_000_000_000
            && plan.OutsideBuildAllowanceBytes == 2_000_000_000;
        bool lifecycleReplacement = foregroundReplacement || plan != null && plan.MaximumChildren == 342 && plan.RetainedChildren == 310
            && plan.BudgetBytes == 112_000_000_000 && plan.StopBytes == 110_500_000_000
            && plan.DeadlineSeconds == 36000 && plan.DirectoryLimit == 320 && plan.FileLimit == 20480 && plan.OfflineDeadlineSeconds == 21600
            && plan.OutputComponentBytes == 2_000_000_000 && plan.RetainedBudgetBytes == 92_000_000_000
            && plan.OutsideBuildAllowanceBytes == 2_000_000_000;
        bool evidenceReplacement = lifecycleReplacement || plan != null && plan.MaximumChildren == 288 && plan.BudgetBytes == 112_000_000_000 && plan.StopBytes == 110_500_000_000
            && plan.DeadlineSeconds == 28800 && plan.DirectoryLimit == 224 && plan.FileLimit == 16384 && plan.OfflineDeadlineSeconds == 16200
            && plan.OutputComponentBytes == 2_000_000_000 && plan.RetainedBudgetBytes == 72_000_000_000
            && plan.OutsideBuildAllowanceBytes == 1_400_000_000;
        bool exportReplacement = evidenceReplacement || plan != null && plan.MaximumChildren == 256 && plan.BudgetBytes == 96_000_000_000 && plan.StopBytes == 94_500_000_000
            && plan.DeadlineSeconds == 23400 && plan.DirectoryLimit == 192 && plan.FileLimit == 12288 && plan.OfflineDeadlineSeconds == 12600
            && plan.OutputComponentBytes == 2_000_000_000 && plan.RetainedBudgetBytes == 56_000_000_000
            && plan.OutsideBuildAllowanceBytes == 1_200_000_000;
        bool readinessReplacement = exportReplacement || plan != null && plan.MaximumChildren == 179 && plan.BudgetBytes == 84_000_000_000 && plan.StopBytes == 82_500_000_000
            && plan.DeadlineSeconds == 18000 && plan.DirectoryLimit == 160 && plan.FileLimit == 10240 && plan.OfflineDeadlineSeconds == 9000
            && plan.OutputComponentBytes == 2_000_000_000 && plan.RetainedBudgetBytes == 44_000_000_000
            && plan.OutsideBuildAllowanceBytes == 1_200_000_000;
        bool startupReplacement = readinessReplacement || plan != null && plan.MaximumChildren == 147 && plan.BudgetBytes == 84_000_000_000 && plan.StopBytes == 82_500_000_000
            && plan.DeadlineSeconds == 18000 && plan.DirectoryLimit == 160 && plan.FileLimit == 10240 && plan.OfflineDeadlineSeconds == 8400
            && plan.OutputComponentBytes == 2_000_000_000 && plan.RetainedBudgetBytes == 44_000_000_000
            && plan.OutsideBuildAllowanceBytes == 1_100_000_000;
        bool contentionReplacement = startupReplacement || plan != null && plan.MaximumChildren == 137 && plan.BudgetBytes == 84_000_000_000 && plan.StopBytes == 82_500_000_000
            && plan.DeadlineSeconds == 14400 && plan.DirectoryLimit == 160 && plan.FileLimit == 10240 && plan.OfflineDeadlineSeconds == 5400
            && plan.OutputComponentBytes == 2_000_000_000 && plan.RetainedBudgetBytes == 44_000_000_000
            && plan.OutsideBuildAllowanceBytes == 1_100_000_000;
        bool replacement = contentionReplacement || plan != null && plan.MaximumChildren == 115 && plan.BudgetBytes == 72_000_000_000 && plan.StopBytes == 70_500_000_000
            && plan.DeadlineSeconds == 11700 && plan.DirectoryLimit == 128 && plan.FileLimit == 8192 && plan.OfflineDeadlineSeconds == 4500
            && plan.OutputComponentBytes == 2_000_000_000 && plan.RetainedBudgetBytes == 34_000_000_000
            && plan.OutsideBuildAllowanceBytes == 1_100_000_000;
        bool fresh = replacement || plan != null && plan.MaximumChildren == 108 && plan.BudgetBytes == 72_000_000_000 && plan.StopBytes == 70_500_000_000
            && plan.DeadlineSeconds == 10800 && plan.DirectoryLimit == 128 && plan.FileLimit == 8192 && plan.OfflineDeadlineSeconds == 3600
            && plan.OutputComponentBytes == 2_000_000_000 && plan.RetainedBudgetBytes == 32_000_000_000;
        Require(plan != null && plan.Order != null && plan.Order.SequenceEqual(Order)
            && plan.FloorBytes == 60_000_000_000 && (replacement || plan.OutsideBuildAllowanceBytes == 1_000_000_000)
            && (fresh || plan.BudgetBytes == 64_000_000_000 && plan.StopBytes == 62_500_000_000 && plan.DeadlineSeconds == 9000
            && ((plan.MaximumChildren is 46 or 51 && plan.DirectoryLimit == 48 && plan.OutputComponentBytes == 1_032_000_000 && plan.RetainedBudgetBytes == 32_000_000_000
                    || plan.MaximumChildren == 57 && plan.DirectoryLimit == 56 && plan.OutputComponentBytes == 2_000_000_000 && plan.RetainedBudgetBytes == 24_000_000_000)
                    && plan.FileLimit == 4096 && plan.OfflineDeadlineSeconds == 1800
                || plan.MaximumChildren == 74 && plan.DirectoryLimit == 72 && plan.FileLimit == 5120 && plan.OfflineDeadlineSeconds == 2700
                    && plan.OutputComponentBytes == 2_000_000_000 && plan.RetainedBudgetBytes == 24_000_000_000)),
            "Calibration limits or frozen six-source order differ from an admitted historical or lifecycle replacement boundary.");
        Require(plan.RetainedRoots != null && plan.RetainedRoots.Length >= 4 && plan.RetainedRoots.Length <= (cadenceReplacement ? 28 : foregroundReplacement ? 25 : lifecycleReplacement ? 24 : evidenceReplacement ? 17 : exportReplacement ? 16 : readinessReplacement ? 15 : startupReplacement ? 14 : contentionReplacement ? 11 : fresh ? 10 : 8)
            && plan.RetainedRoots.Distinct(StringComparer.OrdinalIgnoreCase).Count() == plan.RetainedRoots.Length
            && plan.RetainedRoots.All(r => Directory.Exists(r) && !Path.GetFullPath(r).Equals(Path.GetFullPath(plan.Root), StringComparison.OrdinalIgnoreCase))
            && double.IsFinite(plan.RetainedElapsedSeconds) && plan.RetainedElapsedSeconds >= 0 && plan.RetainedElapsedSeconds < plan.DeadlineSeconds
            && plan.RetainedChildren >= 0 && (long)plan.RetainedChildren + 32 <= plan.MaximumChildren
            && double.IsFinite(plan.RehearsalElapsedSeconds) && plan.RehearsalElapsedSeconds is >= 0 and < 1800
            && plan.RetainedOfflineSeconds >= 0 && plan.RetainedOfflineSeconds <= plan.OfflineDeadlineSeconds && plan.RetainedOfflineSeconds <= plan.RetainedElapsedSeconds,
            "Retained evidence, children or cumulative phase time cannot fit the approved resource boundary.");
        if (plan.MaximumChildren == 74 || fresh)
            Require(plan.RetainedOfflineSeconds + CompleteOfflineBatchSeconds + (startupReplacement ? FullReplacementVerificationSeconds : contentionReplacement ? 210 : replacement ? ReplacementVerificationSeconds : 0) <= plan.OfflineDeadlineSeconds,
                "The fresh batch offline ceilings and replacement verification reserve cannot fit; do not start capture.");
        if (fresh) Require(plan.RetainedElapsedSeconds + (startupReplacement ? FullReplacementWallSeconds : 4080) <= plan.DeadlineSeconds,
            "Fresh calibration requires its full conservative phase/closeout envelope before capture.");
    }
    static object Recipe(CalibrationPlan plan) => new { version = "reference-bytes-v1", baseline = "sum-selected-TS-EOF",
        residual = "max(0,max(training-finalEOF-inputEOF))", guard = "max(16MiB,ceil(base/20))",
        peakExtra = "max-training(sampled-peakAllocation-round(finalEOF))", rounding = "existing-production-keyframe-selection; actual-t298..300.1",
        heldoutMustPass = "zero-final-or-observed-peak-exceedances; no-refit; no-universal-bound",
        sampleIntervalMs = 100, maximumActiveGapMs = 500, observationContract = "active-offline-v1", expectedBytes = 18_000_000_000L,
        fileLimit = plan.FileLimit, directoryLimit = plan.DirectoryLimit, offlineDeadlineSeconds = plan.OfflineDeadlineSeconds,
        completeOfflineBatchCeilingSeconds = CompleteOfflineBatchSeconds,
        additionalReplacementVerificationSeconds = plan.MaximumChildren is 147 or 179 or 256 or 288 or 342 or 374 or 423 ? FullReplacementVerificationSeconds : plan.MaximumChildren == 137 ? 210 : plan.MaximumChildren == 115 ? ReplacementVerificationSeconds : 0,
        phaseLimitsSeconds = new { captureSetup = 330, exportController = 130, exportChild = 120,
            foregroundAdmission = CalibrationForeground.DeadlineSeconds, mainInspection = MainInspectionSeconds, mainDecodeChild = MainInspectionSeconds - InspectionCleanupSeconds,
            shortInspection = ShortInspectionSeconds, shortDecodeChild = ShortInspectionSeconds - InspectionCleanupSeconds,
            archive = ArchiveSeconds, engineControls = 40 },
        componentBytes = new { sources = 6_000_000_000L, copyFallback = 6_000_000_000L, outputs = checked(6 * plan.OutputComponentBytes),
            snapshotArchives = 6_000_000_000L, controls = 64_000_000L, builds = plan.OutsideBuildAllowanceBytes, evidence = 256_000_000L },
        captureArguments = CaptureTemplate("<primary-index>"),
        exportArguments = new[] { "-hide_banner", "-loglevel", "warning", "-y", "-f", "concat", "-safe", "1", "-i", "<owned-concat>", "-t", "<production-rounded-seconds>", "-c", "copy", "-avoid_negative_ts", "make_zero", "-movflags", "+faststart", "-map", "0:v:0", "-map", "0:a:0", "<owned-partial>" },
        analysisArguments = new[] { "-hide_banner", "-loglevel", "info", "-nostdin", "-xerror", "-i", "<owned-final>", "-map", "0:v:0", "-map", "0:a:0", "-af", "astats=metadata=0:reset=0", "-f", "null", "-",
            "-map", "0:v:0", "-vf", "<select three ascending frames within rounded duration>,crop=576:48:16:8,scale=24:2:flags=neighbor,format=gray", "-frames:v", "3", "-fps_mode", "passthrough", "-threads:v", "1", "-f", "rawvideo", "<owned-barcode-raw>",
            "-map", "0:v:0", "-vf", "<select three ascending frames within rounded duration>,crop=1920:984:0:96,scale=64:36:flags=neighbor,format=rgb24", "-frames:v", "3", "-fps_mode", "passthrough", "-threads:v", "1", "-f", "rawvideo", "<owned-picture-raw>" },
        dynamicTokens = "task-owned paths/nonces, actual acquisition-clock epoch, observed source keyframe-rounded duration only; exact arrays persisted before each launch",
        analysis = "one bounded all-track full decode with audio astats per export; no packet predictor/index prerequisite" };

    internal static string[] CaptureTemplate(string primary) => ["-hide_banner", "-loglevel", "warning", "-y", "-stats_period", "0.25", "-progress", "pipe:1",
        "-copyts", "-thread_queue_size", "64", "-probesize", "32", "-analyzeduration", "0", "-ch_layout", "stereo", "-f", "matroska", "-i", "<audio-pipe>",
        "-thread_queue_size", "32", "-f", "lavfi", "-i", "ddagrab=framerate=240:output_idx=" + primary + ":draw_mouse=1,<clock>,fps=fps=60:round=near",
        "-map", "1:v:0", "-map", "0:a:0", "-af", AudioPacketClock.ResamplerFilter, "-ac", "2", "-ar", "48000", "-c:a", "aac", "-b:a", "192k",
        "-fps_mode", "passthrough", "-c:v", "h264_nvenc", "-preset", "p3", "-tune", "ll", "-rc", "cbr", "-b:v", "20M", "-maxrate", "20M", "-bufsize", "40M",
        "-g", "60", "-bf", "0", "-forced-idr", "1", "-force_key_frames", "expr:gte(t,n_forced*1)", "-flags", "+cgop", "-f", "segment", "-segment_time", "1",
        "-segment_format", "mpegts", "-reset_timestamps", "1", .. ReplayManifestStream.OutputArguments(), Path.Combine("<session>", "chunk-%09d.ts")];

    internal static int Controls(string root, string engineRoot)
    {
        Require(!Directory.Exists(root) && !File.Exists(root), "Controls need a fresh root.");
        Directory.CreateDirectory(root); MediaDependency.RootDirectory = engineRoot;
        string engine = MediaDependency.ResolveExecutable();
        var cases = new[]
        {
            (Name: "version", Args: new[] { "-version" }, Ms: 5000, Bytes: 2097152),
            (Name: "nonzero", Args: new[] { "-this-option-is-not-real" }, Ms: 5000, Bytes: 2097152),
            (Name: "timeout", Args: new[] { "-hide_banner", "-nostdin", "-re", "-f", "lavfi", "-i", "anullsrc", "-t", "10", "-f", "null", "-" }, Ms: 200, Bytes: 2097152),
            (Name: "output-limit", Args: new[] { "-version" }, Ms: 5000, Bytes: 128)
        };
        foreach (var item in cases)
        {
            var receipt = ReplayExportProcess.Run(engine, item.Args, root, null, timeoutMilliseconds: item.Ms, outputLimitBytes: item.Bytes);
            Write(Path.Combine(root, item.Name + ".json"), new { arguments = item.Args, receipt });
            Require(receipt.ProducerAbsent && receipt.ActualExitObserved && receipt.CapturedBytes <= item.Bytes, "Engine control cleanup/capture boundary failed.");
            Require(item.Name switch { "version" => receipt.Success, "nonzero" => receipt.ExitCode != 0 && !receipt.Success,
                "timeout" => receipt.TimedOut && !receipt.Success, _ => receipt.OutputLimitExceeded && !receipt.Success }, "Engine control did not demonstrate " + item.Name);
        }
        Console.WriteLine("Bounded engine controls passed: " + root); return 0;
    }

    internal static int SamplingWaitMilliseconds(double started, double now)
    {
        double elapsed = now - started;
        Require(double.IsFinite(elapsed) && elapsed >= 0, "Storage sampling clock moved backwards or is invalid.");
        return elapsed >= 100 ? 0 : (int)Math.Ceiling(100 - elapsed);
    }

    internal static double SamplingGap(double previous, double now)
    {
        double gap = now - previous;
        Require(double.IsFinite(gap) && gap >= 0 && gap <= 500, "Storage polling gap exceeded500ms or clock moved backwards.");
        return gap;
    }

    internal static void ValidateMediaAttributes(string path, FileAttributes attributes)
    {
        if (path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            Require((attributes & (FileAttributes.Compressed | FileAttributes.SparseFile)) == 0, "Unsupported media allocation attributes.");
    }

    internal sealed class Sampler : IDisposable
    {
        sealed class TimedFiles : ReplayInventoryReader
        {
            readonly string atomicControlPrefix;
            internal TimedFiles(IEnumerable<string> roots, Func<string, bool> mayRetain, string currentRoot) : base(roots, mayRetain)
            { atomicControlPrefix = Path.GetFullPath(Path.Combine(currentRoot, "calibration-ownership")) + Path.DirectorySeparatorChar; }
            internal double LeafMilliseconds, MaximumLeafMilliseconds;
            internal string MaximumLeafPath;
            internal int LeafCount;
            internal void Reset()
            { BeginScan(); LeafMilliseconds = MaximumLeafMilliseconds = 0; MaximumLeafPath = null; LeafCount = 0; }
            internal override ReplayFileExtent ObserveInventory(string path, ReplayFileExtent enumerated)
            {
                long at = Stopwatch.GetTimestamp();
                try
                {
                    // Guard/ledger/pending files are atomically replaced under
                    // this task-private authority. Even a delete-sharing metadata
                    // handle can reject Windows replacement. Fresh anchored
                    // directory entries supply allocation/EOF/identity without
                    // opening those volatile leaves, just as for segment lists.
                    if (Path.GetFullPath(path).StartsWith(atomicControlPrefix, StringComparison.OrdinalIgnoreCase)
                        || CalibrationFixturePublication.IsVolatile(path))
                        return ReplayStorage.Validate(enumerated);
                    return base.ObserveInventory(path, enumerated);
                }
                finally
                {
                    double elapsed = Stopwatch.GetElapsedTime(at).TotalMilliseconds;
                    LeafMilliseconds += elapsed;
                    if (elapsed > MaximumLeafMilliseconds) { MaximumLeafMilliseconds = elapsed; MaximumLeafPath = path; }
                    LeafCount++;
                }
            }
        }
        internal readonly object Gate = new();
        readonly string root;
        readonly string rootPrefix;
        readonly CalibrationPlan plan;
        readonly string[] retainedPrefixes;
        readonly string traceName;
        readonly long? currentBudgetBytes;
        internal long CurrentAllocatedBytes;
        readonly TimedFiles inventoryFiles;
        readonly HashSet<string> closedCases = new(StringComparer.OrdinalIgnoreCase);
        readonly Thread thread;
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly Dictionary<string, long> peaks = new(StringComparer.OrdinalIgnoreCase);
        readonly ManualResetEventSlim ready = new();
        volatile bool stop;
        internal volatile string Failure;
        internal long Used;
        internal long PeakUsed;
        internal string SlowestLeafPath;
        internal double SlowestLeafMilliseconds;
        internal double MaximumGap;
        internal int Observations;
        internal int MetadataHandles;
        double lastCompletion, lastObservationStarted, activeStarted, offlineDeadline, offlineStarted, offlineElapsed;
        double RetainedOfflineSeconds => plan.RetainedOfflineSeconds ?? plan.RetainedElapsedSeconds;
        double MaximumOfflineSeconds => plan.OfflineDeadlineSeconds;
        bool activeCoverage;
        string phase;

        sealed class CaptureGrowth
        {
            internal readonly Dictionary<string, long> Previous = new(StringComparer.OrdinalIgnoreCase);
            internal readonly Queue<(double At, long Bytes)> Window = new();
            internal long WindowBytes, PeakWindowBytes, Total, StopAt = -1;
            internal string Volume;
        }
        readonly Dictionary<string, CaptureGrowth> captureGrowth = Order.ToDictionary(label => label, _ => new CaptureGrowth());

        internal double MaximumActiveGap, MaximumOfflineGap;
        internal int MaximumFiles, MaximumDirectories;
        internal double OfflineSeconds
        {
            get { lock (Gate) return RetainedOfflineSeconds + offlineElapsed
                + (offlineDeadline == 0 ? 0 : clock.Elapsed.TotalSeconds - offlineStarted); }
        }
        internal double ElapsedSeconds => clock.Elapsed.TotalSeconds;
        internal Sampler(CalibrationPlan plan, bool active = true, string traceName = "storage.csv", long? currentBudgetBytes = null)
        {
            plan = plan with { Root = Path.GetFullPath(plan.Root), RetainedRoots = plan.RetainedRoots.Select(Path.GetFullPath).ToArray(),
                MetadataHandleRoots = plan.MetadataHandleRoots?.Select(Path.GetFullPath).ToArray() };
            this.plan = plan; root = plan.Root; rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            Require(currentBudgetBytes == null || currentBudgetBytes > 0, "Current source byte ceiling must be positive.");
            this.currentBudgetBytes = currentBudgetBytes;
            retainedPrefixes = plan.RetainedRoots.Select(r => Path.GetFullPath(r).TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar).ToArray();
            Require((plan.MetadataHandleRoots ?? []).All(r => plan.RetainedRoots.Select(Path.GetFullPath)
                .Contains(Path.GetFullPath(r), StringComparer.OrdinalIgnoreCase)), "Reused metadata handles must belong to declared retained roots.");
            Require((plan.MetadataHandleRoots ?? []).All(r => !(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar)
                .StartsWith(Path.GetFullPath(r).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !(Path.GetFullPath(r).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar)
                .StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)),
                "Current mutable experiment data cannot use retained metadata handles.");
            Require(Regex.IsMatch(traceName, "^[a-z0-9-]{1,48}\\.csv$"), "Storage trace needs a bounded local filename.");
            this.traceName = traceName;
            Require(double.IsFinite(RetainedOfflineSeconds) && RetainedOfflineSeconds >= 0
                && RetainedOfflineSeconds <= plan.RetainedElapsedSeconds && RetainedOfflineSeconds <= MaximumOfflineSeconds,
                "Retained offline time is missing a valid cumulative bound.");
            activeCoverage = active; phase = active ? "active" : "boundary";
            inventoryFiles = new TimedFiles(plan.MetadataHandleRoots ?? [], CanRetainMetadata, root);
            if (plan.ReuseCurrentMetadataHandles) inventoryFiles.RetainHandlesUnder(root);
            thread = new Thread(Loop) { IsBackground = true, Name = "bounded-calibration-storage" };
            thread.Start();
            Require(ready.Wait(5000), "Initial storage observation timed out.");
            Check();
        }
        void Loop()
        {
            try
            {
                using var files = inventoryFiles;
                using var trace = new StreamWriter(new FileStream(Path.Combine(root, traceName), FileMode.CreateNew, FileAccess.Write, FileShare.Read));
                trace.WriteLine("elapsed_ms,observation_ms,start_gap_ms,completion_gap_ms,allocated_bytes,caller_free_bytes,files,lock_ms,inventory_ms,leaf_ms,leaf_count,max_leaf_ms,phase,max_leaf_path,native_opens,native_handles,accounting_ms,capacity_ms");
                double previous = 0, previousCompletion = 0;
                while (!stop)
                {
                    double at = clock.Elapsed.TotalMilliseconds, gap = at - previous; previous = at;
                    MaximumGap = Math.Max(MaximumGap, gap);
                    Require(double.IsFinite(gap) && gap >= 0, "Storage clock moved backwards or is invalid.");
                    lock (Gate)
                    {
                        double entered = clock.Elapsed.TotalMilliseconds;
                        Require(offlineDeadline == 0 || clock.Elapsed.TotalSeconds <= offlineDeadline, "Offline storage phase deadline exceeded.");
                        files.Reset();
                        // Inspect fresh attributes from the same anchored native
                        // enumeration as identity/EOF/allocation, avoiding a second
                        // path-based query for every media entry on every scan.
                        var inventory = ReplayStorage.Inventory([root, .. plan.RetainedRoots], files,
                            maximumFiles: plan.FileLimit, maximumDirectories: plan.DirectoryLimit, inspectAttributes: ValidateMediaAttributes);
                        files.EndScan(); // Release absent names before allocation/capacity accounting.
                        Volatile.Write(ref MetadataHandles, files.HandleCount);
                        double inventoried = clock.Elapsed.TotalMilliseconds;
                        if (files.MaximumLeafMilliseconds > SlowestLeafMilliseconds)
                        { SlowestLeafMilliseconds = files.MaximumLeafMilliseconds; SlowestLeafPath = files.MaximumLeafPath; }
                        Require(inventory.Count <= plan.FileLimit, "Calibration inventory exceeded the approved file limit.");
                        MaximumFiles = Math.Max(MaximumFiles, inventory.Count);
                        MaximumDirectories = Math.Max(MaximumDirectories, new[] { root }.Concat(plan.RetainedRoots)
                            .Sum(r => 1 + Directory.GetDirectories(r, "*", SearchOption.AllDirectories).Length));
                        Used = checked(ReplayStorage.Bytes(inventory) + plan.OutsideBuildAllowanceBytes);
                        var retainedInventory = inventory.Where(i => retainedPrefixes.Any(prefix => i.Key.StartsWith(prefix,
                            StringComparison.OrdinalIgnoreCase))).ToDictionary(i => i.Key, i => i.Value);
                        if (currentBudgetBytes.HasValue)
                        {
                            string currentPrefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                            CurrentAllocatedBytes = ReplayStorage.Bytes(inventory.Where(i => i.Key.StartsWith(currentPrefix, StringComparison.OrdinalIgnoreCase))
                                .ToDictionary(i => i.Key, i => i.Value));
                            Require(CurrentAllocatedBytes < currentBudgetBytes.Value, "Startup diagnostic source byte stop reached.");
                        }
                        Require(ReplayStorage.Bytes(retainedInventory) <= plan.RetainedBudgetBytes, "Retained evidence envelope exceeded.");
                        PeakUsed = Math.Max(PeakUsed, Used);
                        foreach (string label in Order)
                        {
                            string prefix = Path.Combine(root, label) + Path.DirectorySeparatorChar;
                            var members = inventory.Where(i => i.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                            Require(members.Where(i => Path.GetFileName(i.Key).StartsWith("chunk-")).Sum(i => i.Value.AllocatedBytes) <= 1_000_000_000L,
                                "Source component exceeded1GB.");
                            Require(members.Where(i => i.Key.EndsWith(".mp4")).Sum(i => i.Value.AllocatedBytes) <= plan.OutputComponentBytes, "Output component exceeded limit.");
                        }
                        double accounted = clock.Elapsed.TotalMilliseconds;
                        var capacity = ReplayStorage.ObserveCapacity(root);
                        foreach (string retained in plan.RetainedRoots)
                            Require(ReplayStorage.ObserveCapacity(retained).AvailableBytes >= plan.FloorBytes + 1_256_000_000L,
                                "A retained evidence volume no longer has its caller-free reserve.");
                        Require(Used < plan.StopBytes && capacity.AvailableBytes >= plan.FloorBytes + 1_256_000_000L,
                            "Calibration byte stop or free-space reserve breached.");
                        Require(clock.Elapsed.TotalSeconds + plan.RetainedElapsedSeconds < plan.DeadlineSeconds, "Calibration cumulative wall deadline exceeded.");
                        foreach (var item in inventory.Where(i => i.Key.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)))
                            peaks[item.Value.Identity] = Math.Max(peaks.GetValueOrDefault(item.Value.Identity), item.Value.AllocatedBytes);
                        double observed = clock.Elapsed.TotalMilliseconds;
                        if (activeCoverage) ObserveCaptureGrowth(inventory, observed);
                        trace.WriteLine(FormattableString.Invariant($"{at},{observed - at},{gap},{observed - previousCompletion},{Used},{capacity.AvailableBytes},{inventory.Count},{entered - at},{inventoried - entered},{files.LeafMilliseconds},{files.LeafCount},{files.MaximumLeafMilliseconds},{phase},\"{files.MaximumLeafPath?.Replace("\"", "\"\"")}\",{files.OpenCount},{files.HandleCount},{accounted - inventoried},{observed - accounted}"));
                        trace.Flush();
                        double completed = clock.Elapsed.TotalMilliseconds;
                        MaximumGap = Math.Max(MaximumGap, Math.Max(completed - at, completed - previousCompletion));
                        if (activeCoverage)
                        {
                            double work = completed - Math.Max(at, activeStarted), coverage = completed - Math.Max(previousCompletion, activeStarted);
                            if (double.IsFinite(work) && double.IsFinite(coverage) && work >= 0 && coverage >= 0)
                                MaximumActiveGap = Math.Max(MaximumActiveGap, Math.Max(work, coverage));
                            SamplingGap(Math.Max(at, activeStarted), completed);
                            SamplingGap(Math.Max(previousCompletion, activeStarted), completed);
                        }
                        else MaximumOfflineGap = Math.Max(MaximumOfflineGap, completed - previousCompletion);
                        previousCompletion = completed;
                        lastCompletion = completed;
                        Volatile.Write(ref lastObservationStarted, at);
                        Interlocked.Increment(ref Observations);
                        ready.Set();
                    }
                    Thread.Sleep(SamplingWaitMilliseconds(at, clock.Elapsed.TotalMilliseconds));
                }
            }
            catch (Exception ex) { Failure = ex.Message; ready.Set(); }
        }

        internal void SealClosedCase(string label)
        {
            lock (Gate)
            {
                Require(!activeCoverage && offlineDeadline == 0 && Order.Contains(label), "Only a closed case between producer/offline phases may retain metadata handles.");
                string cell = Path.Combine(root, label);
                Require(Directory.Exists(cell), "Closed metadata case is absent.");
                closedCases.Add(cell + Path.DirectorySeparatorChar);
                // Caller has confirmed source/export/inspection absence and
                // retired snapshots. No further mutations occur in this case
                // before this sampler is disposed. Allocation is still queried
                // natively on every scan, never inferred from closure.
                inventoryFiles.RetainHandlesUnder(cell);
            }
        }
        bool CanRetainMetadata(string path)
        {
            if (!path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
                || closedCases.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase))) return true;
            // Holding a handle through producer close can delay filesystem
            // allocation trimming. Current captured chunks and MP4s therefore
            // use short-lived observations until the case is fully closed.
            return !Path.GetFileName(path).StartsWith("chunk-", StringComparison.OrdinalIgnoreCase)
                && !path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);
        }
        internal bool RetainsMetadata(string path) { lock (Gate) return inventoryFiles.Retains(path); }
        internal void ReleaseInventoryHandles()
        {
            Require(Monitor.IsEntered(Gate), "Native handle release must share the inventory mutation gate.");
            inventoryFiles.ReleaseHandles(); Volatile.Write(ref MetadataHandles, 0);
        }

        void ObserveCaptureGrowth(Dictionary<string, ReplayFileExtent> inventory, double at)
        {
            foreach (var label in Order)
            {
                var state = captureGrowth[label];
                string prefix = Path.Combine(root, label, "buffer") + Path.DirectorySeparatorChar;
                var current = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                foreach (var member in inventory.Where(i => i.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && (Path.GetFileName(i.Key).StartsWith("chunk-", StringComparison.Ordinal) || Path.GetFileName(i.Key) == "segments.csv")))
                {
                    string volume = member.Value.Volume, identity = volume + ":" + member.Value.Identity;
                    Require(state.Volume == null || state.Volume == volume, "Capture allocation volume changed.");
                    state.Volume = volume;
                    current[identity] = Math.Max(current.GetValueOrDefault(identity), member.Value.AllocatedBytes);
                }
                long growth = 0;
                foreach (var member in current)
                    growth = checked(growth + Math.Max(0, member.Value - state.Previous.GetValueOrDefault(member.Key)));
                state.Previous.Clear(); foreach (var member in current) state.Previous.Add(member.Key, member.Value);
                Interlocked.Exchange(ref state.Total, checked(Interlocked.Read(ref state.Total) + growth));
                state.Window.Enqueue((at, growth)); state.WindowBytes = checked(state.WindowBytes + growth);
                while (state.Window.Count > 1 && state.Window.Peek().At < at - 500)
                    state.WindowBytes -= state.Window.Dequeue().Bytes;
                state.PeakWindowBytes = Math.Max(state.PeakWindowBytes, state.WindowBytes);
            }
        }
        internal void MarkCaptureStop(string label)
        {
            // Never delay owned cleanup behind observer I/O. The latest completed
            // allocation prefix is deliberately conservative for the drain interval.
            var state = captureGrowth[label];
            Interlocked.CompareExchange(ref state.StopAt, Interlocked.Read(ref state.Total), -1);
        }
        internal (long PollGrowth, long StopGrowth, string Volume) CaptureMetrics(string label)
        {
            lock (Gate)
            {
                var state = captureGrowth[label];
                Require(!activeCoverage && state.StopAt >= 0 && state.Volume != null, "Capture growth evidence is incomplete.");
                return (state.PeakWindowBytes, checked(state.Total - state.StopAt), state.Volume);
            }
        }

        void FreshObservation()
        {
            double requested = clock.Elapsed.TotalMilliseconds;
            var wait = Stopwatch.StartNew();
            while (Volatile.Read(ref lastObservationStarted) < requested)
            {
                Check();
                Require(wait.ElapsedMilliseconds < 5000, "Terminal storage observation did not complete.");
                Thread.Sleep(5);
            }
        }
        internal void BeginActive()
        {
            FreshObservation();
            lock (Gate)
            {
                Check();
                Require(!activeCoverage && offlineDeadline == 0, "Previous calibration phase has not closed.");
                activeStarted = clock.Elapsed.TotalMilliseconds;
                activeCoverage = true; phase = "active";
            }
        }
        internal void EndActive()
        {
            // Caller confirmed all producers absent. Require a scan that began later.
            FreshObservation();
            lock (Gate)
            {
                Check(); Require(activeCoverage, "There is no active measurement to close.");
                double gap = SamplingGap(Math.Max(lastCompletion, activeStarted), clock.Elapsed.TotalMilliseconds);
                MaximumActiveGap = Math.Max(MaximumActiveGap, gap);
                activeCoverage = false; phase = "boundary";
            }
        }
        internal void BeginOffline(string name, long allocationBytes, int deadlineSeconds)
        {
            Require(Regex.IsMatch(name, "^[a-z0-9-]{1,32}$") && allocationBytes >= 0 && deadlineSeconds is > 0 and <= 300,
                "Offline calibration phase is not bounded.");
            FreshObservation();
            RequireTime(deadlineSeconds);
            lock (Gate)
            {
                Require(!activeCoverage && offlineDeadline == 0, "Media coverage or another offline phase is still open.");
                Check(allocationBytes);
                Require(RetainedOfflineSeconds + offlineElapsed + deadlineSeconds <= MaximumOfflineSeconds,
                    "The complete offline phase deadline cannot fit the cumulative analysis allowance.");
                offlineStarted = clock.Elapsed.TotalSeconds;
                offlineDeadline = offlineStarted + deadlineSeconds; phase = name;
            }
        }
        internal void EndOffline()
        {
            FreshObservation();
            lock (Gate)
            {
                Require(!activeCoverage && offlineDeadline != 0, "No offline phase is admitted.");
                Check(); offlineElapsed += clock.Elapsed.TotalSeconds - offlineStarted;
                offlineDeadline = 0; phase = "boundary";
            }
        }
        internal void Check(long next = 0)
        {
            Require(Failure == null, Failure);
            Require(offlineDeadline == 0 || clock.Elapsed.TotalSeconds <= offlineDeadline, "Offline storage phase deadline exceeded.");
            Require(RetainedOfflineSeconds + offlineElapsed + (offlineDeadline == 0 ? 0 : clock.Elapsed.TotalSeconds - offlineStarted) <= MaximumOfflineSeconds,
                "Cumulative offline analysis exceeded its approved allowance.");
            Require(Used + next + 1_256_000_000L <= plan.BudgetBytes, "Next stage cannot fit retained bytes plus closure/poll reserves.");
            var capacity = ReplayStorage.ObserveCapacity(root);
            Require(capacity.AvailableBytes >= plan.FloorBytes + Math.Max(next + 1_256_000_000L, plan.BudgetBytes - Used),
                "Fresh caller-free cannot hold remaining declared batch commitments plus floor.");
        }
        internal void RequireTime(int seconds)
        {
            Check();
            Require(seconds > 0 && clock.Elapsed.TotalSeconds + plan.RetainedElapsedSeconds + seconds <= plan.DeadlineSeconds,
                "The full stage deadline cannot fit the remaining cumulative experiment wall time.");
        }
        internal long Peak(string output)
        {
            lock (Gate)
            {
                var extent = ReplayStorage.Observe(output);
                return Math.Max(extent.AllocatedBytes, peaks.GetValueOrDefault(extent.Identity));
            }
        }
        internal void StopAndCheck()
        {
            if (activeCoverage) EndActive();
            Require(offlineDeadline == 0, "Offline phase must close before the sampler.");
            stop = true;
            Require(thread.Join(5000), "Storage observer did not stop.");
            double stopped = clock.Elapsed.TotalMilliseconds;
            MaximumGap = Math.Max(MaximumGap, stopped - lastCompletion);
            if (activeCoverage) SamplingGap(lastCompletion, stopped);
            Check();
        }
        public void Dispose() { stop = true; Require(thread.Join(5000), "Storage observer did not stop."); ready.Dispose(); }
    }
    sealed class RetainFiles(Sampler sampler) : ReplayFiles
    {
        internal override void DeleteDirectory(string path) { } // All successes and failures are retained.
        internal override void DeleteFile(string path) { }
        internal override void Publish(string partial, string final) { lock (sampler.Gate) base.Publish(partial, final); }
    }
    sealed class ShortSource(CaptureSession source, int seconds, double end) : IReplaySessionSource
    {
        readonly IReplaySessionSource inner = source;
        public ReplaySessionInfo ReplayInfo => inner.ReplayInfo with { ReplaySeconds = seconds };
        public bool Stopped => true;
        public string Diagnostics => inner.Diagnostics;
        public string OutputDemandIdentity => inner.OutputDemandIdentity;
        public bool OutputProducerStopped => inner.OutputProducerStopped;
        public ReplayOutputDemand EstimateOutput(ReplayOutputSelection selection) => inner.EstimateOutput(selection);
        public double SaveCutoffAt(long timestamp) => end;
        public void FreezeAt(long timestamp) { }
        public void StopCapture() { }
        public void CheckCaptureHealth(double completedEnd) { }
        public void Export(IReadOnlyList<string> arguments, string snapshot) => inner.Export(arguments, snapshot);
        public void Dispose() { } // Original source registration owns its job.
    }
    static object SaveProcess(string root, string prefix, ReplayExportProcessReceipt receipt)
    {
        string stdout = Path.Combine(root, prefix + ".stdout.txt"), stderr = Path.Combine(root, prefix + ".stderr.txt");
        foreach (var item in new[] { (Path: stdout, Text: receipt.StandardOutput), (Path: stderr, Text: receipt.StandardError) })
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(item.Text);
            Require(bytes.Length <= 2_097_152, "Bounded process log exceeded cap.");
            using var output = new FileStream(item.Path, FileMode.CreateNew, FileAccess.Write, FileShare.Read); output.Write(bytes);
        }
        return new { receipt.ProcessId, receipt.ProcessStartedUtc, receipt.ExitCode, receipt.ProducerAbsent, receipt.ActualExitObserved,
            receipt.TimedOut, receipt.OutputLimitExceeded, receipt.CaptureComplete, receipt.CapturedBytes, receipt.ElapsedMilliseconds,
            receipt.Failure, receipt.Error, stdout, stderr };
    }
    sealed record Sample(string Label, string Scope, string File, long InputBytes, long FinalBytes, long FinalAllocation,
        long PeakAllocation, double RequestedEnd, double ExportSeconds, string SelectionIdentity, ReplayOutputDemand Demand,
        object Process, string[] Arguments);

    static Sample Collect(CalibrationPlan plan, string label, Sampler sampler, ReplayReferenceModel model, List<object> processes)
    {
        sampler.RequireTime(330);
        sampler.Check(3_100_000_000L);
        string root = Path.Combine(plan.Root, label); Directory.CreateDirectory(root);
        var previous = GetForegroundWindow();
        using var fixtureJob = new Native.ProcessJob();
        Process fixture = null; CaptureSession capture = null; ReplayCache cache = null;
        CalibrationCaptureIdentity captureIdentity = null;
        ReplayStorageCoordinator captureStorage = null;
        ReplayStorageStopLatch captureLatch = null;
        ReplayVolumeReservations sharedClaims = null;
        ReplaySessionId id = default;
        var total = Stopwatch.StartNew();
        int exports = 0;
        bool successful = false;
        using var bootstrapCancel = new CancellationTokenSource();
        Task<CaptureSession> create = null;
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = false, CreateNoWindow = true };
            if (Path.GetFileNameWithoutExtension(Environment.ProcessPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(typeof(ReplayCalibrationRun).Assembly.Location);
            foreach (string value in new[] { "--calibration-fixture", root, label }) start.ArgumentList.Add(value);
            fixture = Process.Start(start); fixtureJob.Add(fixture);
            processes.Add(new { role = "fixture", label, pid = fixture.Id, start = fixture.StartTime.ToUniversalTime() });
            while (!File.Exists(Path.Combine(root, "fixture-ready.json")))
            {
                sampler.Check(); Require(!fixture.HasExited && total.Elapsed.TotalSeconds < 15, "Controlled fixture did not become ready."); Thread.Sleep(100);
            }
            using var ready = CalibrationFixturePublication.Read(root);
            IntPtr hwnd = (IntPtr)ready.RootElement.GetProperty("hwnd").GetInt64();
            Require(ready.RootElement.GetProperty("width").GetInt32() == 1920 && ready.RootElement.GetProperty("height").GetInt32() == 1080, "Fixture dimensions changed.");
            uint windowProcess; Native.GetWindowThreadProcessId(hwnd, out windowProcess);
            Require(windowProcess == fixture.Id && ready.RootElement.GetProperty("pid").GetInt32() == fixture.Id,
                "Fixture window does not belong to the task-owned process; no capture admitted.");
            Native.ShowWindow(hwnd, 5); // Show only our own window; no preference or input changes.
            bool requested = SetForegroundWindow(hwnd);
            var focusClock = Stopwatch.StartNew();
            CalibrationForeground.Wait(() => { Native.GetWindowThreadProcessId(hwnd, out uint currentOwner);
                    return currentOwner == fixture.Id && GetForegroundWindow() == hwnd && Native.IsWindowVisible(hwnd); },
                () => fixture.HasExited, () => { sampler.Check(); Require(total.Elapsed.TotalSeconds < 330, "Source setup deadline."); },
                () => Thread.Sleep(50), () => focusClock.Elapsed.TotalSeconds);
            Write(Path.Combine(root, "foreground-admission.json"), new { fixturePid = fixture.Id, hwnd = hwnd.ToInt64(), requested,
                visible = Native.IsWindowVisible(hwnd), foreground = GetForegroundWindow() == hwnd, waitedMilliseconds = focusClock.Elapsed.TotalMilliseconds });
            RecorderRuntime.CaptureArgumentObserver = a =>
            {
                var normalized = ReplayCalibrationStartup.NormalizeCaptureArguments(a, root);
                Require(normalized.SequenceEqual(CaptureTemplate(Native.PrimaryOutputIndex().ToString(CultureInfo.InvariantCulture))), "Real capture arguments differ from frozen reference recipe.");
                Write(Path.Combine(root, "capture-arguments.json"), a);
            };
            RecorderRuntime.ExportArgumentObserver = a => Write(Path.Combine(root, "export-arguments-" + ++exports + ".json"), a);
            var settings = new Settings { ReplayMinutes = 5, FramesPerSecond = 60, BitrateMbps = 20, VideoCodec = "h264_nvenc",
                SystemAudio = true, CaptureMode = "monitor", DisplayIndex = -1, OutputDirectory = root };
            Write(Path.Combine(root, "settings.json"), settings);
            if (model != null)
            {
                string profile = Path.Combine(root, "storage-profile"); Directory.CreateDirectory(profile);
                string authority = Path.Combine(plan.Root, "calibration-ownership");
                sharedClaims = new ReplayVolumeReservations(authority, ReplayStorageSafety.MinimumBytes);
                captureLatch = new ReplayStorageStopLatch(profile, authority);
                captureStorage = new ReplayStorageCoordinator(captureLatch, sharedClaims, catalog: () => new(model, null));
                captureStorage.RequireAdmission(Path.Combine(root, "buffer"), root, settings);
            }
            create = Task.Run(() => new CaptureSession(settings, Path.Combine(root, "buffer"), false,
                () => bootstrapCancel.IsCancellationRequested || sampler.Failure != null || GetForegroundWindow() != hwnd,
                null, beforeLaunch: source =>
                {
                    if (captureStorage == null) return;
                    captureStorage.BeginCapture(source.ReferenceScope, source.DirectoryPath, root, () =>
                    {
                        bootstrapCancel.Cancel();
                        _ = Task.Run(source.StopCapture);
                    }, producerProof: source.ProducerProof);
                    captureStorage.StartSupervision();
                }, afterLaunch: source => captureStorage?.SealProducer(source.ProducerProof)));
            var bootstrapClock = Stopwatch.StartNew();
            while (!create.IsCompleted) { sampler.Check(); Require(GetForegroundWindow() == hwnd && !fixture.HasExited,
                "Controlled fixture lost foreground during bootstrap."); Require(bootstrapClock.Elapsed.TotalSeconds < 35
                && total.Elapsed.TotalSeconds < 330, "Capture bootstrap deadline."); Thread.Sleep(100); }
            capture = create.GetAwaiter().GetResult();
            capture.ObserveOutputDemand = true; capture.OutputCalibration = model;
            Require(capture.ReferenceScope != null, "Real reference recipe or audio/dimensions are unqualified.");
            using (var ownedCapture = Process.GetProcessById(capture.ProcessId))
                captureIdentity = new(ownedCapture.Id, ownedCapture.StartTime.ToUniversalTime(), capture.DirectoryPath);
            processes.Add(new { role = "capture", label, pid = captureIdentity.Pid, start = captureIdentity.StartedUtc, sessionDirectory = captureIdentity.SessionDirectory });
            cache = new ReplayCache(new RetainFiles(sampler), cacheDirectory: Path.Combine(root, "buffer"), volumeReservations: sharedClaims);
            id = cache.RegisterSession(capture);
            double triggerAt = label is "T01" or "H01" ? 304 : label is "T02" or "H03" ? 302.25 : 302.75;
            while (((IReplaySessionSource)capture).SaveCutoffAt(Stopwatch.GetTimestamp()) < triggerAt)
            {
                sampler.Check();
                IntPtr foreground = GetForegroundWindow();
                if (fixture.HasExited || foreground != hwnd)
                {
                    GetWindowThreadProcessId(foreground, out uint foregroundOwner);
                    Write(Path.Combine(root, "foreground-failure.json"), new { utc = DateTime.UtcNow, label,
                        expectedWindow = hwnd.ToInt64(), actualWindow = foreground.ToInt64(), actualOwner = foregroundOwner,
                        fixturePid = fixture.Id, fixtureExited = fixture.HasExited,
                        effects = "Foreground owner identity only; interrupted source remains invalid and is never repaired into a passing sample." });
                    throw new IOException("Controlled source lost foreground or exited.");
                }
                var bounds = Native.DisplayBounds(IntPtr.Zero); Require(bounds.Width == 1920 && bounds.Height == 1080, "Display dimensions changed during capture.");
                var health = capture.ReadHealth();
                if (total.Elapsed.TotalSeconds >= 330 || !health.Healthy)
                {
                    Write(Path.Combine(root, "capture-health-failure.json"), health);
                    throw new IOException("Capture source failed or exceeded deadline: " + JsonSerializer.Serialize(health));
                }
                cache.Maintain(id); Thread.Sleep(100);
            }
            if (label is "T01" or "H01")
            {
                sampler.MarkCaptureStop(label);
                cache.StopAcquisitionAsync(id).GetAwaiter().GetResult();
            }
            sampler.RequireTime(130);
            var save = cache.TrySave(id, Stopwatch.GetTimestamp(), settings.OutputDirectory);
            Require(save.Accepted, save.Rejection);
            Write(Path.Combine(root, "acceptance-demand.json"), save.AdmissionDemand);
            if (label is "H02" or "H03") Require(save.AdmissionDemand?.CoversOpenTail == true
                && save.AdmissionDemand.SnapshotPeakAllocatedBytes.HasValue, "Held-out live press did not exercise reserved unfinished-tail acceptance.");
            var saveClock = Stopwatch.StartNew();
            while (!save.Completion.IsCompleted)
            {
                sampler.Check(); Require(saveClock.Elapsed.TotalSeconds < 130, "Export controller deadline.");
                if (((IReplaySessionSource)capture).SaveCutoffAt(Stopwatch.GetTimestamp()) >= 310)
                {
                    sampler.MarkCaptureStop(label);
                    cache.StopAcquisitionAsync(id).GetAwaiter().GetResult();
                }
                if (!((IReplaySessionSource)capture).Stopped)
                { Require(GetForegroundWindow() == hwnd && !fixture.HasExited, "Source lost during live export."); cache.Maintain(id); }
                Thread.Sleep(100);
            }
            var result = save.Completion.GetAwaiter().GetResult();
            sampler.MarkCaptureStop(label); cache.StopAcquisitionAsync(id).GetAwaiter().GetResult();
            Require(capture.HasExited && capture.CaptureExitCode == 0 && string.IsNullOrWhiteSpace(capture.ErrorTail),
                "Capture did not exit normally with empty diagnostics; source is not eligible.");
            Require(captureStorage?.Stopped != true, "Production storage supervision stopped this held-out source; no qualification.");
            Require(result.Succeeded, result.Error);
            var selection = capture.LastOutputSelection;
            Require(selection.Closed && selection.Inputs.Count >= 298 && capture.LastExport.Success, "Export selection/process incomplete.");
            double exportSeconds = selection.End - selection.Inputs[0].Start;
            Require(exportSeconds is >= 298 and <= 300.1, "Requested-five-minute selection outside frozen production rounding envelope.");
            var extent = ReplayStorage.Observe(result.File);
            using (var output = new StreamWriter(new FileStream(Path.Combine(root, "selection.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read)))
                foreach (var input in selection.Inputs)
                {
                    string line = JsonSerializer.Serialize(input); Require(line.Length < 4096, "Selection evidence record too large."); output.WriteLine(line);
                }
            var sample = new Sample(label, capture.ReferenceScope, result.File, selection.Inputs.Sum(i => i.Extent.Length), result.Bytes,
                extent.AllocatedBytes, sampler.Peak(result.File), selection.End, exportSeconds, selection.Identity,
                capture.LastOutputDemand, SaveProcess(root, "main-export", capture.LastExport), capture.ExportArguments);
            Write(Path.Combine(root, "sample.json"), sample);
            if (label == "T02")
                foreach (var control in new[] { (Seconds: 1, End: 4.0), (Seconds: 2, End: 7.0) })
                {
                    sampler.Check(32_000_000);
                    sampler.RequireTime(130);
                    var shortId = cache.RegisterSession(new ShortSource(capture, control.Seconds, control.End));
                    var submission = cache.TrySave(shortId, 0, settings.OutputDirectory);
                    Require(submission.Accepted, submission.Rejection);
                    Require(submission.Completion.Wait(130000), "Short control export deadline.");
                    var shortResult = submission.Completion.GetAwaiter().GetResult(); Require(shortResult.Succeeded, shortResult.Error);
                    Write(Path.Combine(root, "control-" + control.Seconds + ".json"), new { shortResult,
                        process = SaveProcess(root, "control-export-" + control.Seconds, capture.LastExport), arguments = capture.ExportArguments });
                }
            successful = true; return sample;
        }
        finally
        {
            RecorderRuntime.CaptureArgumentObserver = null; RecorderRuntime.ExportArgumentObserver = null;
            try
            {
            bootstrapCancel.Cancel();
            if (capture == null && create != null)
            {
                try { if (create.Wait(15000)) capture = create.GetAwaiter().GetResult(); }
                catch (AggregateException) { } // Constructor already disposed its owned job on failure.
                Require(create.IsCompleted, "Capture bootstrap cleanup unconfirmed; no further stages permitted.");
            }
            if (capture != null)
            {
                sampler.MarkCaptureStop(label);
                capture.StopCapture();
                Require(capture.CaptureProducerAbsent && capture.HasExited && capture.CaptureExitCode.HasValue, "Capture or audio producer absence is unconfirmed; offline work is blocked.");
                if (successful) Require(capture.CaptureExitCode == 0, "Capture did not exit normally.");
                Write(Path.Combine(root, "capture-exit.json"), captureIdentity != null
                    ? CalibrationCaptureEvidence.Closure(captureIdentity, capture.HasExited, capture.CaptureProducerAbsent, capture.CaptureExitCode, capture.ErrorTail)
                    : new { pid = capture.ProcessId, identityUnavailable = true, exited = capture.HasExited, producersAbsent = capture.CaptureProducerAbsent, exit = capture.CaptureExitCode, diagnostics = capture.ErrorTail });
            }
            if (captureStorage != null)
            {
                captureStorage.CompleteCapture(capture?.CaptureProducerAbsent == true);
                if (successful) Require(!captureStorage.CapturePending && !captureStorage.Stopped && !captureLatch.Read().Held,
                    "Held-out capture claim/guard did not complete normally.");
                captureStorage.Dispose();
            }
            try { if (cache != null) cache.CloseAsync().GetAwaiter().GetResult(); else capture?.Dispose(); }
            finally
            {
                // A controller failure can race an active export. Its receipt is
                // available only after that save has drained during close.
                if (capture?.LastExport != null)
                {
                    Write(Path.Combine(root, "last-export-exit.json"), SaveProcess(root, "last-export", capture.LastExport));
                    Require(capture.LastExport.ProducerAbsent && capture.LastExport.ActualExitObserved,
                        "Export producer absence is unconfirmed; offline work is blocked.");
                }
            }
            }
            finally
            {
                if (fixture != null)
                {
                    bool forced = false;
                    if (!fixture.HasExited) { fixture.CloseMainWindow(); if (!fixture.WaitForExit(5000)) { forced = true; fixture.Kill(); Require(fixture.WaitForExit(5000), "Fixture exit unconfirmed."); } }
                    int code = fixture.ExitCode;
                    var exit = new { role = "fixture-exit", label, pid = fixture.Id, exited = fixture.HasExited, exit = code, forced };
                    processes.Add(exit); Write(Path.Combine(root, "fixture-exit.json"), exit); fixture.Dispose();
                    SetForegroundWindow(previous);
                    if (successful) Require(!forced && code == 0, "Fixture did not exit normally; source is not eligible.");
                }
                SetForegroundWindow(previous);
            }
        }
    }


    sealed record Inspection(ReplayExportProcessReceipt Process, int[] Ids, string[] Hashes, double AudioRmsDb);

    static ReplayCalibrationSourceReceipt QualificationSource(Sample sample, Inspection inspection, string root, Sampler sampler)
    {
        var growth = sampler.CaptureMetrics(sample.Label);
        double activeGap = sampler.MaximumActiveGap;
        string samplePath = Path.Combine(root, "sample.json");
        string capturePath = Path.Combine(root, "capture-exit.json");
        string sourcePath = Path.Combine(root, "fixture-exit.json");
        using var capture = JsonDocument.Parse(File.ReadAllText(capturePath));
        using var source = JsonDocument.Parse(File.ReadAllText(sourcePath));
        var c = capture.RootElement; var f = source.RootElement;
        Require(c.GetProperty("pid").GetInt32() > 0 && c.GetProperty("startedUtc").GetDateTime().Kind == DateTimeKind.Utc
            && c.GetProperty("startedUtc").GetDateTime() > DateTime.UnixEpoch
            && Path.GetFullPath(c.GetProperty("sessionDirectory").GetString()).StartsWith(Path.Combine(root, "buffer") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "Capture closure is missing its independent actual-process/session identity.");
        Require(c.GetProperty("exited").GetBoolean() && c.GetProperty("producersAbsent").GetBoolean() && c.GetProperty("exit").GetInt32() == 0
            && f.GetProperty("exited").GetBoolean() && f.GetProperty("exit").GetInt32() == 0 && !f.GetProperty("forced").GetBoolean(),
            "Reference producers did not exit normally before offline inspection.");
        var export = JsonSerializer.SerializeToElement(sample.Process);
        bool exportNormal = export.GetProperty("ProducerAbsent").GetBoolean() && export.GetProperty("ActualExitObserved").GetBoolean()
            && export.GetProperty("ExitCode").GetInt32() == 0 && !export.GetProperty("TimedOut").GetBoolean()
            && !export.GetProperty("OutputLimitExceeded").GetBoolean() && export.GetProperty("CaptureComplete").GetBoolean();
        Require(exportNormal && inspection.Process.Success, "Export/decode process evidence is incomplete.");
        int segments = File.ReadLines(Path.Combine(root, "selection.jsonl")).Count();
        var decode = inspection.Process;
        return new(sample.Label, ReplayReferenceModel.Hash(new { sample.Scope, capture = HashFile(capturePath), source = HashFile(sourcePath) }),
            HashFile(samplePath), sample.Scope, ReplayStorage.ObserveCapacity(root).Volume,
            ReplayStorage.ObserveCapacity(root).AllocationUnit, sample.InputBytes, sample.FinalBytes, sample.FinalAllocation,
            sample.PeakAllocation, sample.ExportSeconds, segments, true,
            new(HashFile(capturePath), true, true, 0, false, false, false),
            new(HashFile(sourcePath), true, true, 0, false, false, false),
            new(HashFile(samplePath), true, true, 0, false, false, false),
            new(HashFile(Path.Combine(root, Path.GetFileName(sample.File) + ".decode.json")),
                decode.ProducerAbsent, decode.ActualExitObserved, decode.ExitCode ?? -1, decode.TimedOut, decode.OutputLimitExceeded, !decode.Success),
            sample.SelectionIdentity, HashFile(Path.Combine(root, "capture-arguments.json")),
            ReplayReferenceModel.Hash(sample.Arguments), HashFile(sample.File + ".source-pictures.json"),
            true, true, true, true, true, activeGap, inspection.Ids, inspection.Hashes, inspection.AudioRmsDb,
            1920, 1080, 48000, 2, growth.PollGrowth, growth.StopGrowth, growth.Volume);
    }

    internal static int[] InspectionFrames(double seconds)
    {
        Require(double.IsFinite(seconds) && seconds is >= 0.05 and <= 310, "Inspection duration cannot supply three reference frames.");
        int frames = (int)Math.Floor(seconds * 60), inset = Math.Min(15, frames / 4);
        int[] selected = [inset, frames / 2, Math.Min(frames - 1, frames - inset)];
        Require(selected[0] < selected[1] && selected[1] < selected[2] && selected[2] < frames,
            "Inspection frame selection is not three distinct in-range frames.");
        return selected;
    }

    static Inspection Inspect(string engine, string root, string file, double seconds, string label, int timeoutSeconds)
    {
        int[] frames = InspectionFrames(seconds);
        string select = "select='" + string.Join("+", frames.Select(n => "eq(n," + n + ")")) + "'";
        string evidence = Path.Combine(root, Path.GetFileName(file));
        string barcode = evidence + ".barcodes.raw", pictures = evidence + ".pictures.raw";
        string[] args = ["-hide_banner", "-loglevel", "info", "-nostdin", "-xerror", "-i", file,
            "-map", "0:v:0", "-map", "0:a:0", "-af", "astats=metadata=0:reset=0", "-f", "null", "-",
            "-map", "0:v:0", "-vf", select + ",crop=576:48:16:8,scale=24:2:flags=neighbor,format=gray", "-frames:v", "3", "-fps_mode", "passthrough", "-threads:v", "1", "-f", "rawvideo", barcode,
            "-map", "0:v:0", "-vf", select + ",crop=1920:984:0:96,scale=64:36:flags=neighbor,format=rgb24", "-frames:v", "3", "-fps_mode", "passthrough", "-threads:v", "1", "-f", "rawvideo", pictures];
        Write(Path.Combine(root, Path.GetFileName(file) + ".decode-arguments.json"), args);
        var receipt = ReplayExportProcess.Run(engine, args, root, null, timeoutMilliseconds: checked(timeoutSeconds * 1000));
        Write(Path.Combine(root, Path.GetFileName(file) + ".decode.json"), SaveProcess(root, Path.GetFileName(file) + ".decode", receipt));
        Require(receipt.Success && receipt.StandardError.Contains("1920x1080") && receipt.StandardError.Contains("48000 Hz") && receipt.StandardError.Contains("stereo"), "All-track full decode or reference stream format failed.");
        Require(receipt.StandardError.Contains("RMS level dB:") && !receipt.StandardError.Contains("RMS level dB: -inf"), "Actual loopback audio is absent/silent; not an eligible reference source.");
        byte[] codes = File.ReadAllBytes(barcode), pixels = File.ReadAllBytes(pictures);
        Require(codes.Length == 144 && pixels.Length == 3 * 64 * 36 * 3, "Sampled source pictures missing.");
        int baseId = (label.StartsWith('T') ? 13579 : 24680) * 256;
        var ids = new int[3]; var hashes = new string[3];
        for (int sample = 0; sample < 3; sample++)
        {
            for (int bit = 0; bit < 24; bit++)
            {
                byte value = codes[sample * 48 + bit], inverse = codes[sample * 48 + 24 + bit];
                Require(value is <= 70 or >= 180 && inverse is <= 70 or >= 180 && (value >= 180) != (inverse >= 180), "Source barcode/complement ambiguous.");
                if (value >= 180) ids[sample] |= 1 << bit;
            }
            hashes[sample] = Convert.ToHexString(SHA256.HashData(pixels.AsSpan(sample * 6912, 6912)));
            Require(ids[sample] >= baseId && ids[sample] < baseId + 25000, "Decoded source ID has wrong seed/workload lineage.");
        }
        if (label.EndsWith('1')) Require(ids.All(id => id == baseId), "Static source changed its ID.");
        else Require(ids[0] < ids[1] && ids[1] < ids[2] && hashes.Distinct().Count() >= 2, "Motion/cut source samples did not progress.");
        Write(evidence + ".source-pictures.json", new { ids, hashes, sampleFrames = frames,
            limits = "Three sampled source IDs/complement rows and coarse images; no full picture cadence or A/V alignment claim." });
        var audio = Regex.Matches(receipt.StandardError, @"RMS level dB:\s*([-+]?[0-9]+(?:\.[0-9]+)?)")
            .Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToArray();
        Require(audio.Length > 0 && audio.All(double.IsFinite), "Reference RMS evidence is unavailable.");
        return new(receipt, ids, hashes, audio.Max());
    }

    internal static int InspectExisting(string path, string expectedPlanHash)
    {
        Require(new FileInfo(path).Length <= 65536 && HashFile(path) == expectedPlanHash, "Offline inspection plan identity mismatch.");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var p = document.RootElement;
        Require(p.GetProperty("kind").GetString() == "two-retained-short-controls", "Unknown offline inspection scope.");
        string engine = p.GetProperty("engine").GetString(), root = p.GetProperty("root").GetString();
        double retainedWall = p.GetProperty("retainedWallSeconds").GetDouble(), retainedOffline = p.GetProperty("retainedOfflineSeconds").GetDouble();
        int retainedChildren = p.GetProperty("retainedChildren").GetInt32();
        Require(double.IsFinite(retainedWall) && retainedWall >= 0 && retainedWall + 60 <= 9000
            && double.IsFinite(retainedOffline) && retainedOffline >= 0 && retainedOffline <= retainedWall && retainedOffline + 60 <= 1800
            && retainedChildren >= 0 && retainedChildren + 2 <= 57, "Offline inspection cannot fit existing resource allowance.");
        Require(HashFile(engine) == p.GetProperty("engineSha256").GetString(), "Reviewed engine changed.");
        MediaDependency.RootDirectory = Path.GetFullPath(p.GetProperty("engineRoot").GetString());
        Require(MediaDependency.ResolveExecutable().Equals(Path.GetFullPath(engine), StringComparison.OrdinalIgnoreCase), "Offline inspection engine is outside the verified managed package.");
        var cases = p.GetProperty("cases").EnumerateArray().ToArray();
        Require(cases.Length == 2 && cases.Select(c => Path.GetFullPath(c.GetProperty("file").GetString())).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2,
            "Offline correction needs exactly two distinct retained short exports.");
        foreach (var c in cases)
        {
            string file = c.GetProperty("file").GetString();
            Require(c.GetProperty("label").GetString() == "T02" && c.GetProperty("seconds").GetDouble() is >= 0.05 and <= 2.1
                && Path.GetExtension(file).Equals(".mp4", StringComparison.OrdinalIgnoreCase)
                && new FileInfo(file).Length is > 0 and <= 10_000_000 && HashFile(file) == c.GetProperty("sha256").GetString(),
                "Retained short export binding or duration is invalid.");
        }
        Require(ReplayStorage.ObserveCapacity(Path.GetDirectoryName(Path.GetFullPath(root))).AvailableBytes >= 61_256_000_000,
            "Offline inspection free-space reserve is absent.");
        using var owned = PilotStorageObservation.Create(root);
        Write(Path.Combine(root, "admitted-plan.json"), new { planHash = expectedPlanHash, plan = p.Clone(), maximumChildren = 2,
            limits = "Two retained MP4s read only; no capture/UI/audio playback/GPU. New raw outputs under1MB; child20sec, controller60sec. Failed batch remains failed." });
        var timer = Stopwatch.StartNew(); var results = new List<object>();
        try
        {
            foreach (var c in cases)
            {
                Require(timer.Elapsed.TotalSeconds + 20 <= 60, "Complete remaining decode child cannot fit offline correction deadline.");
                var inspection = Inspect(engine, root, c.GetProperty("file").GetString(), c.GetProperty("seconds").GetDouble(), "T02", 20);
                results.Add(new { file = c.GetProperty("file").GetString(), inspection.Process, inspection.Ids, inspection.Hashes, inspection.AudioRmsDb });
            }
            Write(Path.Combine(root, "result.json"), new { result = "passed-two-retained-short-inspections", elapsedSeconds = timer.Elapsed.TotalSeconds,
                results, failedBatchRemainsFailed = true, modelActivated = false });
            Console.WriteLine("Both retained short inspections passed: " + root); return 0;
        }
        catch (Exception ex)
        {
            Write(Path.Combine(root, "failure.json"), new { error = ex.ToString(), elapsedSeconds = timer.Elapsed.TotalSeconds, results });
            Console.Error.WriteLine(ex.Message); return 1;
        }
    }

    internal static int Run(string path, string expectedPlanHash)
    {
        var preflight = Stopwatch.StartNew();
        Require(new FileInfo(path).Length <= 65536 && HashFile(path) == expectedPlanHash, "Reviewed plan identity mismatch.");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var plan = document.RootElement.GetProperty("plan").Deserialize<CalibrationPlan>();
        ValidateResources(plan);
        foreach (var file in plan.Files) Require(HashFile(file.Key) == file.Value, "Runtime identity changed: " + file.Key);
        Require(plan.Files.ContainsKey(Path.GetFullPath(typeof(ReplayCalibrationRun).Assembly.Location))
            && plan.Files.ContainsKey(Path.GetFullPath(Environment.ProcessPath)), "Own assembly/host is not bound.");
        string build = Path.GetDirectoryName(typeof(ReplayCalibrationRun).Assembly.Location);
        foreach (string required in new[] { "F10Recorder.Windows.dll", "F10Recorder.Core.dll", "F10Recorder.WorkerChecks.deps.json", "F10Recorder.WorkerChecks.runtimeconfig.json" })
            Require(plan.Files.ContainsKey(Path.GetFullPath(Path.Combine(build, required))), "Required runtime dependency is unbound.");
        MediaDependency.RootDirectory = plan.EngineRoot;
        string engine = MediaDependency.ResolveExecutable();
        Require(plan.Files.GetValueOrDefault(Path.GetFullPath(engine)) == MediaDependency.ExecutableSha256.ToUpperInvariant(), "Managed engine identity missing.");
        using (var currentEndpoint = new LoopbackAudio())
        {
            var prior = document.RootElement.GetProperty("environment");
            var bounds = Forms.Screen.PrimaryScreen.Bounds;
            Require(bounds.Width == 1920 && bounds.Height == 1080 && prior.GetProperty("os").GetString() == Environment.OSVersion.VersionString
                && prior.GetProperty("machine").GetString() == Environment.MachineName
                && prior.GetProperty("EndpointId").GetString() == currentEndpoint.EndpointId
                && prior.GetProperty("SampleRate").GetInt32() == currentEndpoint.SampleRate
                && prior.GetProperty("Channels").GetInt32() == currentEndpoint.Channels
                && prior.GetProperty("FfmpegFormat").GetString() == currentEndpoint.FfmpegFormat, "Host/display/audio binding changed after review.");
            var capacity = ReplayStorage.ObserveCapacity(Path.GetDirectoryName(plan.Root));
            Require(capacity.Volume == prior.GetProperty("capacity").GetProperty("Volume").GetString()
                && capacity.AllocationUnit == prior.GetProperty("capacity").GetProperty("AllocationUnit").GetInt64(), "Volume binding changed after review.");
            Require(new DriveInfo(Path.GetPathRoot(plan.Root)).DriveFormat == "NTFS", "Only local NTFS is qualified by this run.");
        }
        using var engineLease = MediaDependency.OpenVerifiedExecutable(engine);
        using var owned = PilotStorageObservation.Create(plan.Root);
        Log.DirectoryPath = plan.Root;
        Write(Path.Combine(plan.Root, "admitted-plan.json"), new { plan, planHash = expectedPlanHash, effects = Effects(plan), recipe = Recipe(plan), authorization = "External task approval is required; the plan hash alone is not execution authorization." });
        using var sampler = new Sampler(plan with { RetainedElapsedSeconds = plan.RetainedElapsedSeconds + preflight.Elapsed.TotalSeconds }, active: false);
        var samples = new List<Sample>(); var processes = new List<object>();
        var qualifications = new List<ReplayCalibrationSourceReceipt>();
        ReplayReferenceModel model = null;
        try
        {
            sampler.RequireTime(40);
            sampler.BeginActive();
            Controls(Path.Combine(plan.Root, "controls"), plan.EngineRoot);
            sampler.EndActive();
            foreach (string label in Order)
            {
                sampler.BeginActive();
                var sample = Collect(plan, label, sampler, model, processes);
                sampler.EndActive(); samples.Add(sample);
                sampler.BeginOffline("inspection", 32_000_000, MainInspectionSeconds);
                // Capture has stopped before decoding. Held-out observations never fit the model.
                string caseRoot = Path.Combine(plan.Root, label);
                var inspection = Inspect(engine, caseRoot, sample.File, sample.ExportSeconds, label, MainInspectionSeconds - InspectionCleanupSeconds);
                sampler.EndOffline();
                foreach (string control in Directory.GetFiles(caseRoot, "control-?.json"))
                {
                    using var entry = JsonDocument.Parse(File.ReadAllText(control));
                    var arguments = entry.RootElement.GetProperty("arguments").EnumerateArray().Select(v => v.GetString()).ToArray();
                    sampler.BeginOffline("short-inspection", 4_000_000, ShortInspectionSeconds);
                    Inspect(engine, caseRoot, entry.RootElement.GetProperty("shortResult").GetProperty("File").GetString(),
                        double.Parse(arguments[Array.IndexOf(arguments, "-t") + 1], CultureInfo.InvariantCulture), label, ShortInspectionSeconds - InspectionCleanupSeconds);
                    sampler.EndOffline();
                }
                qualifications.Add(QualificationSource(sample, inspection, caseRoot, sampler));
                Write(Path.Combine(caseRoot, "qualification-source.json"), qualifications[^1]);
                // All source/export/inspection children of this case have exited.
                // Keep complete snapshot bytes and provenance in verified archives
                // before retiring only these new duplicate directory entries.
                foreach (string snapshot in Directory.GetDirectories(Path.Combine(caseRoot, "buffer"), "save-*", SearchOption.AllDirectories))
                {
                    sampler.BeginOffline("archive", 1_068_194_304, ArchiveSeconds);
                    var archived = ReplayCalibrationArchive.Create(caseRoot, snapshot, bytes => sampler.Check(bytes), sampler.Gate);
                    ReplayCalibrationArchive.Retire(caseRoot, archived, sampler.Gate, sampler.ReleaseInventoryHandles);
                    sampler.EndOffline();
                }
                if (label == "T03")
                {
                    Require(samples.Select(s => s.Scope).Distinct().Count() == 1, "Training source/endpoint/recipe changed.");
                    var target = ReplayStorage.ObserveCapacity(caseRoot);
                    long residual = Math.Max(0, samples.Max(s => s.FinalBytes - s.InputBytes));
                    long extra = Math.Max(0, samples.Max(s => s.PeakAllocation - ReplayReferenceModel.Round(s.FinalBytes, target.AllocationUnit)));
                    string[] receipts = Order.Take(3).Select(l => HashFile(Path.Combine(plan.Root, l, "sample.json"))).ToArray();
                    var training = qualifications.Take(3).ToArray();
                    Require(training.All(q => q.CaptureVolume == target.Volume), "Capture/output volume is outside the first reference cell.");
                    var headroom = new ReplayCaptureHeadroom(sample.Scope, target.Volume, target.AllocationUnit,
                        ReplayReferenceModel.CaptureAllowance(training.Max(q => q.CapturePollGrowthBytes.Value), target.AllocationUnit),
                        ReplayReferenceModel.CaptureAllowance(training.Max(q => q.CaptureStopGrowthBytes.Value), target.AllocationUnit));
                    string revision = ReplayCalibrationCatalog.ModelRevision(sample.Scope, target.Volume, target.AllocationUnit, residual, extra, receipts, captureHeadroom: headroom);
                    model = new(revision, sample.Scope, target.Volume, target.AllocationUnit, residual, extra, receipts, headroom);
                    Write(Path.Combine(plan.Root, "frozen-training-model.json"), model);
                }
                else if (label.StartsWith('H'))
                {
                    Require(sample.Scope == model.Scope && sample.Demand.Kind == ReplayOutputEvidenceKind.CalibratedEstimate, "Held-out real provider was unmeasured.");
                    var captureMetrics = qualifications[^1];
                    Require(captureMetrics.CaptureVolume == model.Volume && captureMetrics.CapturePollGrowthBytes <= model.CaptureHeadroom.PollGrowthBytes
                        && captureMetrics.CaptureStopGrowthBytes <= model.CaptureHeadroom.StopGrowthBytes, "Held-out capture growth exceeds the frozen headroom; no refit.");
                    Require(model.CoversObservations(sample.InputBytes, sample.FinalBytes, sample.FinalAllocation, sample.PeakAllocation),
                        "Held-out exceedance rejects frozen model; no refit.");
                }
                sampler.SealClosedCase(label);
            }
            sampler.StopAndCheck();
            Write(Path.Combine(plan.Root, "qualification-sources.json"), qualifications.ToArray());
            Write(Path.Combine(plan.Root, "observation-summary.json"), new { sampler.MaximumActiveGap, sampler.MaximumOfflineGap, sampler.MaximumGap,
                sampler.Used, sampler.PeakUsed, sampler.MaximumFiles, sampler.MaximumDirectories, offlineSeconds = sampler.OfflineSeconds });
            var qualification = ReplayCalibrationCatalog.CreateReceipt(model, qualifications.ToArray(), sampler.MaximumActiveGap,
                HashFile(Path.Combine(plan.Root, "storage.csv")), expectedPlanHash);
            Write(Path.Combine(plan.Root, "qualified-reference.json"), qualification);
            Write(Path.Combine(plan.Root, "result.json"), new { result = "passed-finite-reference-calibration", samples, model,
                limits = "Empirical six-source cell; sampled peak is not a physical upper bound. Held-out captures/exports join a task-private production ledger; unfinished-tail acceptance reserves the complete input/output envelope. No account catalog activation, speed/A-V/game/release acceptance.",
                sampler.Used, sampler.MaximumGap, sampler.MaximumActiveGap, sampler.MaximumOfflineGap, processes });
            Console.WriteLine("Calibration passed: " + plan.Root); return 0;
        }
        catch (Exception ex)
        {
            Write(Path.Combine(plan.Root, "failure.json"), new { result = "failed-no-retry", error = ex.ToString(), samples, model, processes, sampler.Used, sampler.MaximumGap,
                offlineSeconds = sampler.OfflineSeconds });
            Console.Error.WriteLine(ex.Message + " Evidence: " + plan.Root); return 1;
        }
    }
}
