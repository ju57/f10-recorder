using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Forms = System.Windows.Forms;
using Microsoft.Win32;

namespace F10Recorder;

// Measurement-only host. Build with QualificationProductDirectory to execute the
// unchanged qualified Windows/Core bytes. Never imports a model into new bytes.
internal static class ReplayWorkerLatencyPilot
{
    const long Cap = 12_000_000_000, Stop = 11_000_000_000, Floor = 60_000_000_000;
    internal const int FixtureLifetimeSeconds = CalibrationForeground.UserHandoffSeconds + 580 + 15;
    static int CaptureSeconds(int minutes) => minutes switch { 1 => 340, 5 => 580, 30 => 4230,
        _ => throw new IOException("No ordinary worker duration cell.") };
    static int FixtureSeconds(int minutes) => CalibrationForeground.UserHandoffSeconds + CaptureSeconds(minutes) + 15;
    static int WholeSeconds(int minutes) => minutes == 30 ? 5400 : 900;
    static int SaveSeconds(int minutes) => minutes == 30 ? 1200 : 130;
    static long Budget(int minutes) => minutes == 30 ? 56_000_000_000L : Cap;
    static long EarlyStop(int minutes) => minutes == 30 ? 55_000_000_000L : Stop;
    static long ReportAllowance(int minutes,bool acceptance) => minutes==30 ? 536_870_912L : acceptance ? 335_544_320L : 268_435_456L;
    static readonly JsonSerializerOptions Json = new(WorkerProtocol.Json) { WriteIndented = true };
    sealed record CpuObservation(long Qpc, double WorkerCpuMilliseconds, double CaptureCpuMilliseconds, long AllocatedBytes, int Gen0, int Gen1, int Gen2);
    internal static string Hash(string path, bool shared = false)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read,
            shared ? FileShare.ReadWrite | FileShare.Delete : FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(f));
    }
    static void Require(bool value, string message) { if (!value) throw new IOException(message); }
    sealed class ReportStream(string path,long? maximumBytes=null) : Stream
    {
        readonly FileStream output = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        readonly long limit = maximumBytes ?? (Path.GetFileName(path) == "metrics.json" ? 64L * 1024 * 1024 : 8L * 1024 * 1024);
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => output.Length;
        public override long Position { get => output.Position; set => throw new NotSupportedException(); }
        public override void Flush() => output.Flush();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (output.Position + buffer.Length > limit) throw new IOException("Pilot report byte bound.");
            output.Write(buffer);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) output.Dispose(); base.Dispose(disposing); }
    }
    internal static void Write(string path, object value,long? maximumBytes=null)
    { Require(maximumBytes==null || maximumBytes is >0 and <=268435456,"Report byte limit."); using var output = new ReportStream(path,maximumBytes); JsonSerializer.Serialize(output, value, Json); }
    internal static string[] RuntimeFiles()
    {
        string core = RuntimeEnvironment.GetRuntimeDirectory();
        string desktop = Path.GetDirectoryName(typeof(Forms.Form).Assembly.Location);
        Require(Path.GetFileName(Environment.ProcessPath).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase), "Pinned dotnet host required.");
        using var process = Process.GetCurrentProcess();
        string fxr = process.Modules.Cast<ProcessModule>().Single(m =>
            Path.GetFileName(m.FileName).Equals("hostfxr.dll", StringComparison.OrdinalIgnoreCase)).FileName;
        return new[] { core, desktop }.Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(p => Directory.GetFiles(p, "*", SearchOption.AllDirectories)).Where(p => Path.GetExtension(p) is ".dll" or ".json" or ".exe")
            .Append(fxr).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    internal static Dictionary<string, string> ParseRuntime(byte[] raw, string expectedHash, string[] selected)
    {
        Require(raw.Length is > 0 and <= 262144 && Convert.ToHexString(SHA256.HashData(raw)) == expectedHash,
            "Bound runtime manifest bytes changed.");
        using var document = JsonDocument.Parse(raw);
        Require(document.RootElement.ValueKind == JsonValueKind.Object, "Runtime manifest must be an object.");
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in document.RootElement.EnumerateObject())
        {
            Require(files.Count < 1024 && Path.IsPathFullyQualified(entry.Name) && entry.Value.ValueKind == JsonValueKind.String
                && files.TryAdd(entry.Name, entry.Value.GetString()), "Runtime manifest member count/path/duplicate is invalid.");
        }
        Require(files.Count > 0 && files.Keys.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(selected, StringComparer.OrdinalIgnoreCase),
            "Exact complete selected framework closure required.");
        return files;
    }
    static Dictionary<string, string> ReadRuntime(string path, string expectedHash)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Require(input.Length is > 0 and <= 262144, "Runtime manifest256KiB bound.");
        byte[] raw = new byte[checked((int)input.Length)]; input.ReadExactly(raw);
        return ParseRuntime(raw, expectedHash, RuntimeFiles());
    }
    internal static string Startup()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        object value = key?.GetValue("F10Recorder", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return JsonSerializer.Serialize(new { value, kind = value == null ? null : key.GetValueKind("F10Recorder").ToString() });
    }
    internal static int Fixture(string root) => CalibrationPattern.Run(root, "H02", manualHandoff: true, lifetimeSeconds: FixtureLifetimeSeconds);
    internal static int DurationFixture(string root,int minutes) => CalibrationPattern.Run(root,"H02",manualHandoff:true,lifetimeSeconds:FixtureSeconds(minutes));

    internal static int PreparePrivateCatalog(string root, string receiptPath, string expectedHash, string engineRoot)
    {
        string artifacts = Path.GetFullPath("artifacts") + Path.DirectorySeparatorChar;
        Require(root.StartsWith(artifacts, StringComparison.OrdinalIgnoreCase) && !Directory.Exists(root) && !File.Exists(root),
            "Fresh task-private catalog required.");
        Require(new FileInfo(receiptPath).Length is > 0 and <= ReplayCalibrationCatalog.CoverageMaximumBytes && Hash(receiptPath) == expectedHash,
            "Exact bounded completed qualification receipt required.");
        MediaDependency.RootDirectory = engineRoot;
        string accountFile = Path.Combine(TrustedStorage.DefaultRoot, "storage-calibration", ReplayCalibrationCatalog.FileName);
        string AccountHash() => File.Exists(accountFile) ? Hash(accountFile) : "absent";
        string before = AccountHash();
        byte[] bytes = File.ReadAllBytes(receiptPath);
        Require(Convert.ToHexString(SHA256.HashData(bytes)) == expectedHash, "Qualification changed during copy.");
        var (name, minutes) = PrivateCatalogShape(bytes);
        TrustedStorage.CreatePrivateDirectory(root);
        using (var output = new FileStream(Path.Combine(root, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        { output.Write(bytes); output.Flush(true); }
        var availability = ReplayCalibrationCatalog.LoadFrom(root,replayMinutes:minutes);
        Require(availability.Model != null, availability.Reason);
        Require(ReplayCalibrationCatalog.IsApplicableToDestination(availability.Model, Path.GetDirectoryName(root)), "Qualified destination mismatch.");
        Require(AccountHash() == before && Hash(receiptPath) == expectedHash, "Ordinary catalog or original qualification changed.");
        Write(Path.Combine(root, "private-catalog-verification.json"), new { result = "verified-task-private-catalog", receiptPath, receiptSha256 = expectedHash,
            availability.Model.Revision, exporterIdentity = ReplayReferenceModel.ExporterIdentity, defaultCatalogUnchanged = true,
            claim = "Fresh protected task-private copy validated by the ordinary loader and actual destination. No account catalog activation, capture, decoder or Saved measurement." });
        return 0;
    }
    internal static (string FileName, int Minutes) PrivateCatalogShape(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        ReplayCalibrationCatalog.CheckUniqueProperties(document.RootElement);
        Require(document.RootElement.ValueKind == JsonValueKind.Object, "Catalog evidence must be an object.");
        if (document.RootElement.GetProperty("Revision").GetString() == ReplayCalibrationCatalog.SameCaptureRevision)
        {
            _ = ReplayCalibrationCatalog.ReadEvidence<ReplaySameCaptureReceipt>(bytes);
            return (ReplayCalibrationCatalog.SameCaptureFileName, 5);
        }
        if (document.RootElement.GetProperty("Revision").GetString() == ReplayCalibrationCatalog.CoverageRevision)
        {
            _ = ReplayCalibrationCatalog.ReadEvidence<ReplayCoverageReceipt>(bytes);
            return (ReplayCalibrationCatalog.CoverageFileName, 5);
        }
        Require(bytes.Length <= ReplayCalibrationCatalog.MaximumBytes, "Legacy receipt size bound.");
        var receipt = ReplayCalibrationCatalog.ReadEvidence<ReplayCalibrationReceipt>(bytes);
        Require(receipt.Model != null && ReplayReferenceModel.SupportedMinutes(receipt.Model.ReplayMinutes), "Unsupported catalog duration.");
        return (ReplayCalibrationCatalog.FileNameFor(receipt.Model.ReplayMinutes), receipt.Model.ReplayMinutes);
    }

    internal static int Prepare(string planFile, string root, string engineRoot, string product, string productionData, string catalog,
        string uiBuild = null, int pairNumber = 0, double requestPhase = 0, object batch = null, string hardwareFile = null,int replayMinutes=5)
    {
        bool acceptance = uiBuild != null;
        bool duration = replayMinutes != 5;
        Require(ReplayReferenceModel.SupportedMinutes(replayMinutes) && (!acceptance || !duration), "Acceptance batch is the five-minute cell; duration cases have distinct plans.");
        if(duration)requestPhase=.25;
        foreach (string p in new[] { planFile, root, engineRoot, product, productionData, catalog }.Concat(acceptance ? [uiBuild] : []))
            Require(Path.IsPathFullyQualified(p), "Absolute paths required.");
        Require(!acceptance || pairNumber is >= 1 and <= 10 && requestPhase >= 0 && requestPhase < 1, "Frozen acceptance pair/phase required.");
        Require(!File.Exists(planFile) && !Directory.Exists(root) && !File.Exists(root), "Fresh plan and root required.");
        string artifacts = Path.GetFullPath("artifacts") + Path.DirectorySeparatorChar;
        Require(root.StartsWith(artifacts, StringComparison.OrdinalIgnoreCase), "Task artifact output required.");
        MediaDependency.RootDirectory = engineRoot;
        string engine = MediaDependency.ResolveExecutable();
        Require(Hash(Path.Combine(product, "F10Recorder.Windows.dll")) == Hash(typeof(CaptureSession).Assembly.Location)
            && Hash(Path.Combine(product, "F10Recorder.Core.dll")) == Hash(typeof(Settings).Assembly.Location), "Use unchanged qualified product bytes.");
        var model = ReplayCalibrationCatalog.LoadFrom(catalog,replayMinutes:replayMinutes); Require(model.Model != null, model.Reason);
        Require(model.Model.CaptureHeadroom != null, "Qualified capture headroom required.");
        long captureEnvelope = checked(2 * ReplayReferenceModel.SnapshotEnvelope(model.Model.AllocationUnit,replayMinutes)
            + model.Model.CaptureHeadroom.PollGrowthBytes + model.Model.CaptureHeadroom.StopGrowthBytes);
        long forward = checked(captureEnvelope + 2 * ReplayReferenceModel.SnapshotEnvelope(model.Model.AllocationUnit,replayMinutes)
            + 2 * model.Model.PeakBytes(model.Model.InputByteLimit) + ReportAllowance(replayMinutes,acceptance));
        Require(forward + 1_000_000_000 <= EarlyStop(replayMinutes), "Entire pair and closure reserve must fit before capture.");
        string ancestor = Path.GetDirectoryName(root);
        // Batch pair parents are intentionally absent until the admitted Run.
        // Qualify their nearest existing ancestor without creating live output.
        while (!Directory.Exists(ancestor) && ancestor != null) ancestor = Path.GetDirectoryName(ancestor);
        Require(Directory.Exists(ancestor) && ReplayCalibrationCatalog.IsApplicableToDestination(model.Model, ancestor), "Qualified volume required.");
        var bounds = Forms.Screen.PrimaryScreen.Bounds;
        using var audio = new LoopbackAudio();
        Require(bounds.Width == 1920 && bounds.Height == 1080 && audio.SampleRate == 48000 && audio.Channels == 2, "Reference display/audio required.");
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string[] sharedFiles = [Path.Combine(productionData, "settings.json"), Path.Combine(productionData, "worker-state.json")];
        string consoleImage = Path.Combine(Environment.SystemDirectory, "conhost.exe");
        string build = Path.GetDirectoryName(typeof(ReplayWorkerLatencyPilot).Assembly.Location);
        Directory.CreateDirectory(Path.GetDirectoryName(planFile));
        string runtimeManifest = Path.Combine(Path.GetDirectoryName(planFile), "runtime-closure.json");
        var runtimeFiles = RuntimeFiles().ToDictionary(p => p, p => Hash(p), StringComparer.OrdinalIgnoreCase);
        Require(runtimeFiles.Count <= 1024, "Runtime closure file-count bound.");
        byte[] runtimeBytes = JsonSerializer.SerializeToUtf8Bytes(runtimeFiles, Json);
        Require(runtimeBytes.Length <= 262144, "Runtime manifest byte bound.");
        using (var output = new FileStream(runtimeManifest, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) output.Write(runtimeBytes);
        foreach (string file in Directory.GetFiles(build).Where(p => Path.GetExtension(p) is ".dll" or ".json")
            .Concat(new[] { Environment.ProcessPath, engine, Path.Combine(catalog, ReplayCalibrationCatalog.EvidenceFileName(catalog, replayMinutes)),
                runtimeManifest, consoleImage,
                Path.Combine(productionData, "settings.json"), Path.Combine(productionData, "worker-state.json"),
                Path.GetFullPath("projects/F10Recorder.WorkerChecks/ReplayWorkerLatencyPilot.cs"),
                Path.GetFullPath("projects/F10Recorder.WorkerChecks/CalibrationPattern.cs"),
                Path.GetFullPath("projects/F10Recorder.WorkerChecks/CalibrationForegroundRelease.cs"),
                Path.GetFullPath("tests/replay-worker-latency-pilot.ps1") })) files[Path.GetFullPath(file)] = Hash(file, sharedFiles.Contains(file, StringComparer.OrdinalIgnoreCase));
        if (acceptance)
        {
            foreach (string file in Directory.GetFiles(uiBuild, "*", SearchOption.AllDirectories)
                .Concat(new[] { Path.GetFullPath("tests/replay-resource-observer.ps1"), Path.GetFullPath("tests/replay-acceptance-pair.ps1"),
                    Path.GetFullPath("tests/replay-acceptance-batch.ps1"), Path.GetFullPath("tests/replay-acceptance-budget.ps1"),
                    Path.GetFullPath("tests/ReplayAcceptanceNative.cs"),
                    Path.GetFullPath("tests/replay-acceptance-accounting.md"),
                    Path.GetFullPath("projects/F10Recorder.WorkerChecks/ReplayAcceptanceBatch.cs") }))
                files[Path.GetFullPath(file)] = Hash(file);
            Require(Path.IsPathFullyQualified(hardwareFile), "Frozen hardware input required."); files[hardwareFile] = Hash(hardwareFile);
            using var hardware = JsonDocument.Parse(File.ReadAllBytes(hardwareFile));
            string controllerHost = hardware.RootElement.GetProperty("controllerHost").GetString();
            Require(Path.IsPathFullyQualified(controllerHost) && Path.GetFileName(controllerHost).Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase), "Exact native PowerShell controller host required.");
            string shellRoot = Path.GetDirectoryName(controllerHost);
            foreach (string tool in new[] { controllerHost, Path.Combine(shellRoot, "pwsh.dll"), Path.Combine(shellRoot, "System.Management.Automation.dll") }
                .Concat(new[] { "Microsoft.PowerShell.Commands.Diagnostics.dll", "Microsoft.PowerShell.ThreadJob.dll", "Microsoft.CodeAnalysis.dll", "Microsoft.CodeAnalysis.CSharp.dll" }
                    .Select(name => Path.Combine(shellRoot, name)).Where(File.Exists))
                .Concat(new[] { "Microsoft.PowerShell.Diagnostics", "Microsoft.PowerShell.ThreadJob" }.SelectMany(name =>
                    Directory.GetFiles(Path.Combine(shellRoot, "Modules", name), "*", SearchOption.AllDirectories).Where(path => Path.GetExtension(path) is ".dll" or ".psd1" or ".psm1"))))
                files[tool] = Hash(tool);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(planFile));
        Write(planFile, new { kind = acceptance ? "issue83-acceptance-pair-v2" : duration ? "issue83-worker-duration-pilot-v1" : "issue83-worker-latency-pilot-v1", replayMinutes, root, engineRoot, product, productionData, catalog, files, runtimeManifest,
            uiBuild, pairNumber, requestPhase, batch, firstRequestSeconds = acceptance || duration ? replayMinutes*60+6 : (int?)null, repeatRequestSeconds = acceptance || duration ? replayMinutes*60+16 : (int?)null,
            requestSchedule = acceptance || duration ? "Client-send media-epoch phase; firstduration+6+phase, repeat next SAME fractional phase at/after maxduration+16+phase, priorSaved+0.25. Poll readiness before each target; retain actual lateness and worker trigger. No post-result sample/window exclusion." : null,
            maximumChildren = acceptance ? 12 : 11, preparationHelpers = 1, outerControllers = 1,
            cumulativeProcessesIncludingPreparationAndController = acceptance ? 15 : 14,
            consoleSupport = new { path = consoleImage, maximumDirectChildren = 1, maximumWorkingSetBytes = 67_108_864 },
            maximumAcquisitions = 1, captureSeconds = CaptureSeconds(replayMinutes), fixtureLifetimeSeconds = FixtureSeconds(replayMinutes), deadlineSeconds = WholeSeconds(replayMinutes),
            saveDeadlineSeconds=SaveSeconds(replayMinutes), budgetBytes = Budget(replayMinutes), stopBytes = EarlyStop(replayMinutes), minimumFreeBytes = Floor, finalizationReserveBytes = 1_000_000_000,
            retainedBytes = 0, expectedForwardBytes = forward, maximumFiles = 8192, maximumDirectories = 512, sharedFiles,
            storageEnvelope = new { captureEnvelope, twoSnapshotEnvelopes = 2 * ReplayReferenceModel.SnapshotEnvelope(model.Model.AllocationUnit,replayMinutes),
                twoOutputWorkPeaks = 2 * model.Model.PeakBytes(model.Model.InputByteLimit), helperFixtureProfileReports = ReportAllowance(replayMinutes,acceptance),
                maximumMetricsRows=replayMinutes==30?75000:10000, maximumMetricsBytes=replayMinutes==30?268435456:67108864,
                reportBounds = replayMinutes==30 ? "One256MiB metrics+atmost10other8MiB reports;64MiB fixture/profile and112MiB controller/resource/log/growth allowance=512MiB. All bounded; logical outer walk supplements product native guards."
                    : "One64MiB metrics+atmost10other8MiB reports=144MiB;64MiB fixture/profile allowance;48MiB log/metadata/growth; acceptance adds64MiB for resource/controller/UI reports. Logical outer walk is supplemental, not actual native-peak evidence. Product native guards/qualified envelopes unchanged." },
            sampleOrder = new[] { "first-use", "repeat-use" }, maximumExternalObservationGapMilliseconds = 1000,
            maximumExternalScanMilliseconds = 500, foregroundHandoffSeconds = CalibrationForeground.UserHandoffSeconds,
            claim = "Two ordinary-worker observations, no percentile or OS-cache-cold claim. Queue zero; scheduling included. Supplemental decodes after BOTH Saved and capture stop are same-identity evidence across a later lease, not continuous publication ownership/source-lineage proof.",
            environment = new { os = Environment.OSVersion.VersionString, machine = Environment.MachineName, cpus = Environment.ProcessorCount,
                bounds.Width, bounds.Height, audio.EndpointId, audio.SampleRate, audio.Channels, capacity = ReplayStorage.ObserveCapacity(ancestor) },
            effects = "One owned fullscreen moving fixture/quiet chirp, primary pixels/default loopback audio; ordinary worker in helper, private profile/authority and read-only private qualified catalog; RetainEvidence=false/default ReplayFiles; NO pre-Saved output observer or helper callback IO. Up to four export children/two normal validators/two supplemental full decoders, one capture/fixture. Acceptance also attaches one actual frozen hidden UI, one counter-sampling runspace in controller (no process). No installed/startup/vendor/catalog activation.",
            startup = Startup(), createdUtc = DateTime.UtcNow });
        Console.WriteLine(planFile + " " + Hash(planFile)); return 0;
    }
    internal static int Run(string planFile, string expectedHash)
    {
        using var inputPlan = new FileStream(planFile, FileMode.Open, FileAccess.Read, FileShare.Read);
        Require(inputPlan.Length is > 0 and <= 65536, "Bounded reviewed plan required.");
        byte[] planBytes = new byte[checked((int)inputPlan.Length)]; inputPlan.ReadExactly(planBytes);
        Require(Convert.ToHexString(SHA256.HashData(planBytes)) == expectedHash, "Reviewed plan changed.");
        using var document = JsonDocument.Parse(planBytes); var plan = document.RootElement;
        bool acceptance = plan.GetProperty("kind").GetString() == "issue83-acceptance-pair-v2";
        bool duration = plan.GetProperty("kind").GetString() == "issue83-worker-duration-pilot-v1";
        bool matched = !acceptance && !duration && plan.TryGetProperty("matchedFinalCandidate",out var matchedFlag) && matchedFlag.GetBoolean();
        bool scheduled=acceptance || duration || matched;
        int replayMinutes=duration?plan.GetProperty("replayMinutes").GetInt32():5;
        Require(acceptance || duration || plan.GetProperty("kind").GetString() == "issue83-worker-latency-pilot-v1", "Wrong plan.");
        Require(!duration || replayMinutes is 1 or 30,"Duration case must select its explicit one/thirty-minute cell.");
        if(duration)Require(plan.GetProperty("firstRequestSeconds").GetInt32()==replayMinutes*60+6
            && plan.GetProperty("repeatRequestSeconds").GetInt32()==replayMinutes*60+16 && plan.GetProperty("requestPhase").GetDouble()==.25,"Duration request schedule changed.");
        Require(!acceptance || plan.GetProperty("firstRequestSeconds").GetInt32() == 306 && plan.GetProperty("repeatRequestSeconds").GetInt32() == 316
            && plan.GetProperty("pairNumber").GetInt32() is >= 1 and <= 10 && plan.GetProperty("requestPhase").GetDouble() >= 0
            && plan.GetProperty("requestPhase").GetDouble() < 1, "Frozen paired request schedule required.");
        Require(!matched || replayMinutes==5 && plan.GetProperty("firstRequestSeconds").GetInt32()==306
            && plan.GetProperty("repeatRequestSeconds").GetInt32()==316 && plan.GetProperty("pairNumber").GetInt32() is >=1 and <=10
            && plan.GetProperty("requestPhase").GetDouble() is >=0 and <1,"Frozen ordinary five-minute matched schedule required.");
        Require(plan.GetProperty("maximumChildren").GetInt32() == (acceptance ? 12 : 11) && plan.GetProperty("maximumAcquisitions").GetInt32() == 1
            && plan.GetProperty("captureSeconds").GetInt32() == CaptureSeconds(replayMinutes) && plan.GetProperty("fixtureLifetimeSeconds").GetInt32() == FixtureSeconds(replayMinutes)
            && plan.GetProperty("deadlineSeconds").GetInt32() == WholeSeconds(replayMinutes)
            && (!duration || plan.GetProperty("saveDeadlineSeconds").GetInt32()==SaveSeconds(replayMinutes))
            && plan.GetProperty("budgetBytes").GetInt64() == Budget(replayMinutes) && plan.GetProperty("stopBytes").GetInt64() == EarlyStop(replayMinutes)
            && plan.GetProperty("minimumFreeBytes").GetInt64() == Floor
            && plan.GetProperty("sampleOrder").EnumerateArray().Select(x => x.GetString()).SequenceEqual(new[] { "first-use", "repeat-use" }), "Admission boundary changed.");
        Require(Startup() == plan.GetProperty("startup").GetString(), "Startup changed before launch.");
        string[] sharedFiles = plan.GetProperty("sharedFiles").EnumerateArray().Select(x => x.GetString()).ToArray();
        string BoundHash(string file) => Hash(file, sharedFiles.Contains(file, StringComparer.OrdinalIgnoreCase));
        foreach (var f in plan.GetProperty("files").EnumerateObject()) Require(BoundHash(f.Name) == f.Value.GetString(), "Bound input changed: " + f.Name);
        string runtimeManifest = plan.GetProperty("runtimeManifest").GetString();
        var runtimeFiles = ReadRuntime(runtimeManifest, plan.GetProperty("files").GetProperty(runtimeManifest).GetString());
        foreach (var f in runtimeFiles) Require(Hash(f.Key) == f.Value, "Runtime dependency changed: " + f.Key);
        bool RuntimePreserved() => runtimeFiles.Keys.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(RuntimeFiles(), StringComparer.OrdinalIgnoreCase)
            && runtimeFiles.All(f => File.Exists(f.Key) && Hash(f.Key) == f.Value);
        string root = plan.GetProperty("root").GetString(), engineRoot = plan.GetProperty("engineRoot").GetString();
        Require(root.StartsWith(Path.GetFullPath("artifacts") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !Directory.Exists(root) && !File.Exists(root)
            && DateTime.UtcNow.AddSeconds(WholeSeconds(replayMinutes)) < plan.GetProperty("createdUtc").GetDateTime().AddHours(4), "Fresh isolated root/full admission required.");
        MediaDependency.RootDirectory = engineRoot;
        Require(ReplayCalibrationCatalog.CandidateForChecks == null, "No previous candidate hook allowed.");
        string catalog = plan.GetProperty("catalog").GetString();
        var qualified = ReplayCalibrationCatalog.LoadFrom(catalog,replayMinutes:replayMinutes); Require(qualified.Model != null, qualified.Reason);
        Require(qualified.Model.CaptureHeadroom != null, "Current capture headroom required.");
        long forward = checked(4 * ReplayReferenceModel.SnapshotEnvelope(qualified.Model.AllocationUnit,replayMinutes)
            + qualified.Model.CaptureHeadroom.PollGrowthBytes + qualified.Model.CaptureHeadroom.StopGrowthBytes
            + 2 * qualified.Model.PeakBytes(qualified.Model.InputByteLimit) + ReportAllowance(replayMinutes,acceptance));
        Require(plan.GetProperty("retainedBytes").GetInt64() == 0 && plan.GetProperty("expectedForwardBytes").GetInt64() == forward
            && forward + 1_000_000_000 <= EarlyStop(replayMinutes), "Complete conservative qualified envelope and closure reserve required.");
        Directory.CreateDirectory(root);
        Require(ReplayCalibrationCatalog.IsApplicableToDestination(qualified.Model, root), "Actual fresh output volume must still match qualification BEFORE fixture/capture.");
        File.Copy(planFile, Path.Combine(root, "admitted-plan.json"));
        string data = Path.Combine(root, "profile"), fixtureRoot = Path.Combine(root, "fixture");
        Directory.CreateDirectory(data); Directory.CreateDirectory(fixtureRoot);
        string previousAuthority = ProfileOwnership.RootDirectory;
        var settings = new Settings { ReplayMinutes = replayMinutes, FramesPerSecond = 60, BitrateMbps = 20, VideoCodec = "h264_nvenc", SystemAudio = true,
            CaptureMode = "monitor", DisplayIndex = -1, OutputDirectory = Path.Combine(root, "output"), ReplayCacheDirectory = Path.Combine(root, "cache"),
            ScreenshotDirectory = Path.Combine(root, "screenshots"), SaveModifiers = 7, OverlayModifiers = 7, ScreenshotModifiers = 7,
            SaveKey = 121, OverlayKey = 120, ScreenshotKey = 119 };
        var total = Stopwatch.StartNew(); var acquisitionClock = new Stopwatch(); int children = 1, acquisitions = 0, exports = 0, validators = 0, workerExit = -1;
        CaptureSession capture = null; Thread worker = null; Process fixture = null, ui = null, captureProcess = null;
        using var workerProcess = Process.GetCurrentProcess();
        bool stoppingUi = false; Exception failure = null;
        var ready = new TaskCompletionSource<CaptureSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var results = new List<object>(); var attempts = new List<object>(); var priorFinals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var readmissions=new List<object>();ReplayOutputSelection previousSelection=null;
        var metrics = new List<object>();
        var sourceWitness=duration?new ReplayOrdinarySourceWitness(Path.Combine(root,"source-audit"),replayMinutes*60+SaveSeconds(replayMinutes)+32):null;
        var cleanupErrors = new List<string>();
        long exportAt = 0, validationAt = 0; int supplementalDecodes = 0; double lastScan = -1; long maxBytes = 0; int maxFiles = 0;
        int fixturePid = 0, uiPid = 0, captureIdentityPid = 0; DateTime? fixtureBirth = null, uiBirth = null, captureBirth = null;
        long captureReadyQpc = 0, startupEndQpc = 0, captureStoppedQpc = 0, workerStoppedQpc = 0, fixtureStoppedQpc = 0;
        CpuObservation captureReadyCpu = null, startupEndCpu = null, captureStoppedCpu = null, workerStoppedCpu = null;
        long pilotEnteredAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true);
        int[] pilotEnteredCollections = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
        double pilotEnteredWorkerCpuMilliseconds = workerProcess.TotalProcessorTime.TotalMilliseconds;
        long previousObservation = 0; double maximumObservationGap = 0, maximumScan = 0;
        void Child(string role) { Require(Interlocked.Increment(ref children) <= (acceptance ? 12 : 11), "Child ceiling before launch: " + role); }
        CpuObservation ObserveCpu()
        {
            Require(captureProcess != null, "Capture CPU identity is not available.");
            workerProcess.Refresh(); captureProcess.Refresh();
            return new(Stopwatch.GetTimestamp(), workerProcess.TotalProcessorTime.TotalMilliseconds,
                captureProcess.TotalProcessorTime.TotalMilliseconds,GC.GetTotalAllocatedBytes(precise:true),GC.CollectionCount(0),GC.CollectionCount(1),GC.CollectionCount(2));
        }
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
            Require(total.Elapsed.TotalSeconds < WholeSeconds(replayMinutes)-20, "Whole baseline deadline (20s cleanup reserved).");
            if (ui != null && !stoppingUi) Require(!ui.HasExited, "Attached real UI exited during measurement.");
            if (capture != null && !capture.CaptureProducerAbsent) Require(acquisitionClock.Elapsed.TotalSeconds < CaptureSeconds(replayMinutes), "Capture deadline.");
            if (foreground && fixture != null) Require(!fixture.HasExited && GetForegroundWindow() == fixture.MainWindowHandle, "Owned source lost foreground; no retry.");
            if (total.Elapsed.TotalSeconds - lastScan >= .5)
            {
                var scan = Stopwatch.StartNew();
                lastScan = total.Elapsed.TotalSeconds;
                // Normal retirement may remove a directory during this outer
                // sampled walk. Missing descendants are expected; a missing
                // root, access denial or reparse is an error. This observer is
                // supplemental to the unchanged product storage guard/reserves.
                long bytes = 0; int fileCount = 0, directoryCount = 0;
                var pending = new Stack<string>(); pending.Push(root);
                while (pending.TryPop(out string directory))
                {
                    string[] entries;
                    try { entries = Directory.GetFileSystemEntries(directory); }
                    catch (DirectoryNotFoundException) when (directory != root) { continue; }
                    Require(++directoryCount <= 512, "Directory cap.");
                    foreach (string entry in entries)
                    {
                        try
                        {
                            var attributes = File.GetAttributes(entry);
                            Require((attributes & FileAttributes.ReparsePoint) == 0, "Unexpected reparse in owned output.");
                            if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                            else { Require(++fileCount <= 8192, "File cap."); bytes = checked(bytes + new FileInfo(entry).Length); }
                        }
                        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { }
                    }
                }
                maxBytes = Math.Max(maxBytes, bytes); maxFiles = Math.Max(maxFiles, fileCount);
                Require(bytes + plan.GetProperty("retainedBytes").GetInt64() < EarlyStop(replayMinutes) && ReplayStorage.ObserveCapacity(root).AvailableBytes >= Floor, "Storage stop/floor.");
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
            Require(metrics.Count < (replayMinutes==30?75000:10000), "Metric row cap."); metrics.Add(new { qpc = Stopwatch.GetTimestamp(), status }); return status;
        }
        try
        {
            ProfileOwnership.RootDirectory = Path.Combine(root, "authority");
            RecordingProfile.Load(data).Commit(settings, true, true, false);
            // Preserve ordinary per-estimate parsing, receipt validation, file
            // leases and host checks. Only the protected catalog path differs.
            ReplayCalibrationCatalog.CandidateForChecks = minutes =>
            {
                Require(minutes==replayMinutes,"Ordinary capture/save catalog lookup changed the selected profile duration.");
                return ReplayCalibrationCatalog.LoadFrom(catalog,replayMinutes:minutes);
            };
            var start = new ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = false, CreateNoWindow = true };
            if (Path.GetFileNameWithoutExtension(Environment.ProcessPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(ReplayWorkerLatencyPilot).Assembly.Location);
            start.ArgumentList.Add(duration?"--replay-worker-duration-pilot-fixture":"--replay-worker-latency-pilot-fixture"); start.ArgumentList.Add(fixtureRoot);
            if(duration)start.ArgumentList.Add(replayMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Child("fixture"); fixture = Process.Start(start); _ = fixture.SafeHandle; fixturePid = fixture.Id; fixtureBirth = fixture.StartTime.ToUniversalTime();
            while (!File.Exists(Path.Combine(fixtureRoot, "fixture-ready.json"))) { Guard(false); Require(total.Elapsed.TotalSeconds < 20 && !fixture.HasExited, "Fixture startup deadline."); Thread.Sleep(50); }
            fixture.Refresh(); IntPtr hwnd = fixture.MainWindowHandle;
            Require(hwnd != IntPtr.Zero, "Owned fixture window missing."); ShowWindow(hwnd, 5); bool requestedForeground = SetForegroundWindow(hwnd);
            var release = CalibrationForegroundRelease.Create("H02", fixture.Id, hwnd.ToInt64());
            Write(Path.Combine(root, "foreground-handoff-ready.json"), new { label = "H02", pid = fixture.Id, hwnd = hwnd.ToInt64(), captureStarted = false, release });
            var handoff = Stopwatch.StartNew();
            CalibrationForeground.WaitForUserHandoff(() => { Native.GetWindowThreadProcessId(hwnd, out uint owner);
                    return owner == fixture.Id && Native.IsWindowVisible(hwnd) && GetForegroundWindow() == hwnd && release.IsReleased(root); },
                () => fixture.HasExited, () => Guard(false), () => Thread.Sleep(50), () => handoff.Elapsed.TotalSeconds);
            Write(Path.Combine(root, "foreground-handoff-complete.json"), new { hwnd = hwnd.ToInt64(), fixture.Id, requestedForeground, waitedMilliseconds = handoff.Elapsed.TotalMilliseconds });
            acquisitionClock.Start();
            RecorderRuntime.CaptureArgumentObserver = args => { Child("capture"); Require(Interlocked.Increment(ref acquisitions) == 1, "No second acquisition."); };
            CaptureSession.StartingForChecks = source => { capture = source; source.RetainEvidence = false;
                source.ValidationArgumentObserver = args => { Child("validator"); Require(Interlocked.Increment(ref validators) <= 2, "Validator ceiling.");
                    Interlocked.Exchange(ref validationAt, Stopwatch.GetTimestamp()); }; };
            CaptureSession.StartedForChecks = source => ready.TrySetResult(source);
            RecorderRuntime.ExportArgumentObserver = args => { Child("export"); Require(Interlocked.Increment(ref exports) <= 4, "Export ceiling.");
                Interlocked.Exchange(ref exportAt, Stopwatch.GetTimestamp()); };
            worker = new Thread(() => { try { workerExit = RecorderWorker.Run(data, true, setStartup: _ => throw new IOException("Startup forbidden."),
                screenshotSource: (_, _) => throw new IOException("Screenshot forbidden."), repairEngine: () => throw new IOException("Engine provisioning forbidden.")); }
                catch (Exception e) { ready.TrySetException(e); } }); worker.SetApartmentState(ApartmentState.STA); worker.Start();
            while (true) { Guard(); try { Call(); break; } catch (Exception e) when (e is TimeoutException or OperationCanceledException) { Require(acquisitionClock.Elapsed.TotalSeconds < 30, "Worker startup."); } Thread.Sleep(100); }
            if (acceptance)
            {
                var initial = Call().Status;
                Require(initial.Paused && initial.SaveRegistered && initial.OverlayRegistered && initial.ScreenshotRegistered, "All3 shortcuts required before actual UI.");
                string uiEvidence = Path.Combine(root, "ui"); Directory.CreateDirectory(uiEvidence);
                using var parent = Process.GetCurrentProcess();
                var uiStart = new ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = false, CreateNoWindow = true };
                foreach (string arg in new[] { Path.Combine(plan.GetProperty("uiBuild").GetString(), "F10Recorder.MigrationTests.dll"), data,
                    ProfileOwnership.RootDirectory, uiEvidence, parent.Id.ToString(), parent.StartTime.ToUniversalTime().Ticks.ToString(), initial.Instance }) uiStart.ArgumentList.Add(arg);
                Child("actual-ui"); ui = Process.Start(uiStart); _ = ui.SafeHandle;
                uiPid = ui.Id; uiBirth = ui.StartTime.ToUniversalTime();
                Write(Path.Combine(uiEvidence, "ui-launched.json"), new { uiPid = ui.Id, uiBirth = ui.StartTime.ToUniversalTime(), workerPid = parent.Id,
                    workerBirth = parent.StartTime.ToUniversalTime(), instance = initial.Instance });
                while (!File.Exists(Path.Combine(uiEvidence, "ui-ready.json"))) { Guard(); Require(acquisitionClock.Elapsed.TotalSeconds < 30, "UI startup deadline."); Thread.Sleep(50); }
                using var attached = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(uiEvidence, "ui-ready.json"))); var a = attached.RootElement;
                Require(a.GetProperty("pid").GetInt32() == ui.Id && a.GetProperty("birthUtc").GetDateTime() == ui.StartTime.ToUniversalTime()
                    && a.GetProperty("workerPid").GetInt32() == parent.Id && a.GetProperty("workerBirthTicks").GetInt64() == parent.StartTime.ToUniversalTime().Ticks
                    && a.GetProperty("instance").GetString() == initial.Instance && a.GetProperty("actualWorkerRecorder").GetBoolean()
                    && !a.GetProperty("settingsEnterObserved").GetBoolean() && a.GetProperty("stickyField").GetString() == "F10Recorder.SettingsPanel.loaded:System.Boolean"
                    && !a.GetProperty("settingsVisible").GetBoolean() && !a.GetProperty("overlayVisible").GetBoolean(), "Actual frozen hidden UI identity required.");
            }
            Call("pause", false);
            while (!ready.Task.IsCompleted) { Guard(); Require(acquisitionClock.Elapsed.TotalSeconds < 45, "Capture bootstrap deadline."); Thread.Sleep(100); }
            capture = ready.Task.GetAwaiter().GetResult(); int capturePid = capture.ProcessId;
            captureProcess = Process.GetProcessById(capturePid); _ = captureProcess.SafeHandle;
            captureBirth = captureProcess.StartTime.ToUniversalTime();
            Require(!captureProcess.HasExited && capture.ProcessId == capturePid && capture.ReadHealth().Healthy, "Exact live capture birth required.");
            captureIdentityPid = capturePid; captureReadyCpu = ObserveCpu(); captureReadyQpc = captureReadyCpu.Qpc;
            while (true)
            {
                sourceWitness?.Mirror(capture.DirectoryPath,check:()=>Guard());
                var warming = Poll();
                if (startupEndCpu == null && Stopwatch.GetElapsedTime(captureReadyQpc).TotalSeconds >= 10)
                { startupEndCpu = ObserveCpu(); startupEndQpc = startupEndCpu.Qpc; }
                if (warming.Metrics.BufferedSeconds >= replayMinutes*60) break;
                Thread.Sleep(100);
            }
            Require(startupEndCpu != null, "Ten-second startup CPU boundary missing.");
            ReplaySaveClock mediaClock = scheduled ? (ReplaySaveClock)typeof(CaptureSession).GetField("saveClock", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(capture) : null;
            Require(!scheduled || mediaClock != null && mediaClock.Frequency == Stopwatch.Frequency, "Actual common acquisition epoch required.");
            long priorSaved = 0;
            foreach (string label in new[] { "first-use", "repeat-use" })
            {
                exportAt = validationAt = 0;
                sourceWitness?.Mirror(capture.DirectoryPath,allowGap:true,check:()=>Guard());
                var before = Poll(); Require(before.Ready && before.CanSave && !before.Saving, "Unqueued live save required.");
                double? targetSeconds = null;
                if (scheduled)
                {
                    double phase = plan.GetProperty("requestPhase").GetDouble();
                    double earliest = label == "first-use" ? replayMinutes*60+6 + phase : Math.Max(replayMinutes*60+16 + phase, mediaClock.PositionAt(priorSaved) + .25);
                    targetSeconds = Math.Ceiling(earliest - phase - 1e-9) + phase;
                    // Never move the first target because readiness was late.
                    // Actual late requests remain observations with their offset.
                    while (mediaClock.PositionAt(Stopwatch.GetTimestamp()) < targetSeconds.Value)
                    { Guard(); sourceWitness?.Mirror(capture.DirectoryPath,check:()=>Guard()); double left = targetSeconds.Value - mediaClock.PositionAt(Stopwatch.GetTimestamp()); Thread.Sleep(left > .04 ? 10 : 1); }
                    before = Poll(); Require(before.Ready && before.CanSave && !before.Saving, "Target-time unqueued live save required.");
                }
                sourceWitness?.Mirror(capture.DirectoryPath,allowGap:true,check:()=>Guard());
                long sequence = before.Sequence; var requestCpu = ObserveCpu(); long requestAt = requestCpu.Qpc;
                var ack = Call("save", after: sequence); long ackAt = Stopwatch.GetTimestamp();
                long savingEventAt = 0, savingObservedAt = 0;
                void ObserveSaving(WorkerStatus status, long observedAt)
                {
                    if (savingObservedAt != 0) return;
                    var started = status?.Events.FirstOrDefault(e => e.Sequence > sequence && e.Kind == "notification"
                        && e.Title == "Saving replay…");
                    if (started != null) { savingEventAt = started.CreatedQpc; savingObservedAt = observedAt; }
                }
                ObserveSaving(ack.Status, ackAt);
                attempts.Add(new { label, requestAt, ackAt, ack });
                Require(ack.ReplayAccepted == true, ack.ReplayRejection ?? "Save rejected.");
                string final = null; long savedAt = 0, eventAt = 0; WorkerStatus done = null; CpuObservation savedCpu = null;
                while (true)
                {
                    var status = Poll(sequence);
                    ObserveSaving(status, Stopwatch.GetTimestamp());
                    Require(status.Metrics.CaptureProcessId == capturePid && capture.ReadHealth().Healthy, "Capture must continue through Saved.");
                    var saved = status.Events.FirstOrDefault(e => e.Sequence > sequence && e.Kind == "saved");
                    if (saved != null)
                    {
                        savedCpu = ObserveCpu(); savedAt = savedCpu.Qpc; eventAt = saved.CreatedQpc;
                        attempts.Add(new { label, observedSavedQpc = savedAt, eventCreatedQpc = eventAt, saved.Success, saved.Message, timing = status.Metrics.LastSaveTiming });
                        Require(Stopwatch.GetElapsedTime(requestAt, savedAt).TotalSeconds < SaveSeconds(replayMinutes), "Late Saved exceeded save deadline; actual event retained.");
                        Require(saved.Success, saved.Message); final = saved.Message; done = status; break;
                    }
                    Require(Stopwatch.GetElapsedTime(requestAt).TotalSeconds < SaveSeconds(replayMinutes), "Save deadline."); Thread.Sleep(50);
                }
                Require(File.Exists(final) && done.Metrics.LastSaveSucceeded && done.Metrics.LastSaveTiming?.CompletionReady != null
                    && (done.Metrics.LastSaveValidation is "FullDecode" or ReplayOutputIntegrity.Kind), "Validated publication missing.");
                Require(done.Metrics.LastSaveTiming.QueueMilliseconds == 0 && eventAt > 0 && eventAt <= savedAt, "Queue or event clock mismatch.");
                Require(requestAt <= savingEventAt && savingEventAt <= eventAt && savingEventAt <= savingObservedAt && savingObservedAt <= savedAt,
                    "Saving notification creation/client observation is missing or has an invalid clock.");
                Require(!capture.RetainEvidence, "Ordinary retirement must remain enabled.");
                Require(done.Metrics.CaptureProgressSeconds > before.Metrics.CaptureProgressSeconds
                    && done.Metrics.EncodedFrames > before.Metrics.EncodedFrames, "Capture must progress across each save.");
                Require(!done.Metrics.LastSaveLimitedHistory && Math.Abs(done.Metrics.LastSaveAvailableSeconds - replayMinutes*60) <= .000001,
                    "Full requested duration required; warm-up must remain truthful.");
                priorFinals.Add(final);
                priorSaved = savedAt;
                var selection = capture.LastOutputSelection;
                sourceWitness?.Record(label,requestAt,selection);
                if(duration && previousSelection!=null)
                    Require(selection.Start<previousSelection.End && selection.End>previousSelection.End,"Successive ordinary duration saves must overlap and advance their immutable endpoints.");
                previousSelection=selection;
                results.Add(new { label, targetSeconds, actualClientMediaSeconds = mediaClock?.PositionAt(requestAt), clientRequestLatenessMilliseconds = targetSeconds.HasValue ? (mediaClock.PositionAt(requestAt) - targetSeconds.Value) * 1000 : (double?)null,
                    requestAt, ackAt, exportAt, validationAt, eventAt, savedAt, final, queueMilliseconds = 0, timing = done.Metrics.LastSaveTiming,
                    savingEventCreatedQpc = savingEventAt, clientObservedSavingQpc = savingObservedAt,
                    requestToSavingEventMilliseconds = Stopwatch.GetElapsedTime(requestAt, savingEventAt).TotalMilliseconds,
                    requestToObservedSavingMilliseconds = Stopwatch.GetElapsedTime(requestAt, savingObservedAt).TotalMilliseconds,
                    eventDeliveryMilliseconds = Stopwatch.GetElapsedTime(eventAt, savedAt).TotalMilliseconds,
                    requestToSavedMilliseconds = Stopwatch.GetElapsedTime(requestAt, savedAt).TotalMilliseconds,
                    cpu = new { request = requestCpu, saved = savedCpu,
                        workerMilliseconds = savedCpu.WorkerCpuMilliseconds - requestCpu.WorkerCpuMilliseconds,
                        captureMilliseconds = savedCpu.CaptureCpuMilliseconds - requestCpu.CaptureCpuMilliseconds },
                    ackMilliseconds = Stopwatch.GetElapsedTime(requestAt, ackAt).TotalMilliseconds,
                    requestToFirstNativeExportMilliseconds = exportAt == 0 ? (double?)null : Stopwatch.GetElapsedTime(requestAt, exportAt).TotalMilliseconds,
                    export = capture.LastExport, exportProcesses = capture.LastExportProcesses,
                    preparation = capture.LastPreparationTiming is { } prepared && prepared.Started >= requestAt ? prepared : null,
                    validation = capture.LastValidation, arguments = capture.ExportArguments,
                    validationArguments = capture.ValidationArguments, ack, done, selection,
                    captureFrameDelta = done.Metrics.EncodedFrames - before.Metrics.EncodedFrames,
                    captureDropDelta = done.Metrics.EncoderReportedDrops - before.Metrics.EncoderReportedDrops,
                    captureDuplicateDelta = done.Metrics.EncoderReportedDuplicates - before.Metrics.EncoderReportedDuplicates,
                    stageLimits = "Timing includes ordinary close/release/finalization/publication and client polling. No output reader/hardlink/helper callback writes before Saved. Source selection records survive normal input retirement; source-media retention is not forced by this pilot." });
                Write(Path.Combine(root, label + ".json"), results[^1]);
                Thread.Sleep(250);
                if(duration)
                {
                    var again=Poll();
                    Require(again.Ready && again.CanSave && !again.Saving && again.Metrics.CaptureProcessId==capturePid,
                        "Ordinary duration save did not re-admit while the same capture continued.");
                    readmissions.Add(new{label,qpc=Stopwatch.GetTimestamp(),again.Ready,again.CanSave,again.Saving,again.Sequence,capturePid});
                }
            }
            Call("pause", true);
            while (!capture.CaptureProducerAbsent) { Guard(false); Thread.Sleep(50); }
            captureStoppedCpu = ObserveCpu(); captureStoppedQpc = captureStoppedCpu.Qpc;
            if (ui != null)
            {
                stoppingUi = true; Write(Path.Combine(root, "ui", "STOP.UI"), new { ownedStop = true });
                while (!ui.WaitForExit(50)) { Guard(false); }
                Require(ui.ExitCode == 0, "Normal actual UI closure required.");
            }
            else Call("quit");
            Require(worker.Join(15000) && workerExit == 0 && capture.CaptureExitCode == 0, "Normal producer/worker closure required.");
            workerProcess.Refresh(); workerStoppedQpc = Stopwatch.GetTimestamp();
            workerStoppedCpu = new(workerStoppedQpc, workerProcess.TotalProcessorTime.TotalMilliseconds,
                captureStoppedCpu.CaptureCpuMilliseconds,GC.GetTotalAllocatedBytes(precise:true),GC.CollectionCount(0),GC.CollectionCount(1),GC.CollectionCount(2));
            Require(!new ReplayStorageStopLatch(data, ProfileOwnership.RootDirectory).Read().Held, "Durable hold after worker join.");
            if(duration)Require(!Directory.Exists(capture.DirectoryPath),"Ordinary capture session was not retired after worker join.");
            fixture.CloseMainWindow(); Require(fixture.WaitForExit(5000) && fixture.ExitCode == 0, "Normal fixture exit required.");
            fixtureStoppedQpc = Stopwatch.GetTimestamp();
            if(sourceWitness!=null)Write(Path.Combine(root,"source-witness.json"),sourceWitness.VerifyClosed(replayMinutes));
            using (var restored = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixtureRoot, "fixture-display-exit.json"))))
                Require(restored.RootElement.GetProperty("restored").GetBoolean(), "Fixture wake lease not restored.");
            // Both measured exports and all capture stop/join work precede any
            // supplemental reader/decoder. These later leases do not establish
            // uninterrupted ownership of the object across the post-Saved gap.
            foreach (string final in priorFinals.Order(StringComparer.OrdinalIgnoreCase))
            {
                Child("supplemental-full-decode"); supplementalDecodes++;
                var input = new ReplayDecodeLease(final);
                ReplayExportProcessReceipt decoded = null;
                try
                {
                    string beforeHash = Hash(final); var identity = input.CheckedIdentity;
                    using var job = new Native.ProcessJob();
                    decoded = ReplayExportProcess.Run(MediaDependency.ResolveExecutable(), ReplayExactExport.DecodeArguments(final, true, 60),
                        Path.GetDirectoryName(final), job, timeoutMilliseconds: replayMinutes==30?350000:50000, checkStorage: () => Guard(false), inputLease: input);
                    Write(Path.Combine(root, "supplemental-decode-" + supplementalDecodes + ".json"), new { final, identity, beforeHash, decoded });
                    ReplayExactExport.ReadValidation(decoded, new(0, replayMinutes*60, replayMinutes*60), 60);
                    input.VerifyUnchanged(); Require(beforeHash == Hash(final), "Supplemental decode input changed.");
                }
                finally { if (decoded == null || decoded.ProducerAbsent) input.Dispose(); }
            }
            Require(Startup() == plan.GetProperty("startup").GetString()
                && plan.GetProperty("files").EnumerateObject().All(f => File.Exists(f.Name) && BoundHash(f.Name) == f.Value.GetString())
                && RuntimePreserved(), "Bound inputs/startup changed; no successful baseline.");
            Write(Path.Combine(root, "result.json"), new { result = "two-save-worker-pilot-collected", results, readmissions, children, acquisitions, exports, validators, supplementalDecodes, workerExit, maxBytes, maxFiles,
                resourceIdentities = new { fixturePid, fixtureBirth, uiPid, uiBirth, capturePid = captureIdentityPid, captureBirth },
                resourceWindows = new { captureReadyQpc, startupEndQpc, captureStoppedQpc, workerStoppedQpc, fixtureStoppedQpc },
                cpuWindows = new { pilotEnteredWorkerCpuMilliseconds,pilotEnteredAllocatedBytes,pilotEnteredCollections, captureReady = captureReadyCpu, startupEnd = startupEndCpu,
                    captureStopped = captureStoppedCpu, workerStopped = workerStoppedCpu },
                captureAbsent = capture.CaptureProducerAbsent, captureArguments = capture.CaptureArguments, sourcePath = capture.DirectoryPath,
                interpretation = "First/repeated product-path observations only. No p95, OS-cache-cold, continuous post-Saved ownership, physical feedback or independent source-content acceptance claim." });
        }
        catch (Exception e) { failure = e; Console.Error.WriteLine(e); }
        finally
        {
            stoppingUi = true;
            if (ui != null && !ui.HasExited)
                try { string marker = Path.Combine(root, "ui", "STOP.UI"); if (!File.Exists(marker)) Write(marker, new { cleanupStop = true }); }
                catch (Exception error) { cleanupErrors.Add("UI stop marker: " + error.Message); }
            // Write before teardown as well as after join: failures retain the
            // actual last stage rather than disappearing with ordinary cleanup.
            try { Write(Path.Combine(root, "last-stage.json"), new { failure = failure?.ToString(), attempts,
                    export = capture?.LastExport, validation = capture?.LastValidation,
                    exportArguments = capture?.ExportArguments, validationArguments = capture?.ValidationArguments,
                    selection = capture?.LastOutputSelection }); }
            catch (Exception evidenceError) { cleanupErrors.Add("last-stage report: " + evidenceError.Message); }
            if (worker?.IsAlive == true)
            {
                // The failing measurement guard must not prevent owned cleanup.
                // Each control call is independently bounded and outer process
                // supervision remains active throughout teardown.
                foreach (string command in new[] { "pause", "quit" })
                    try
                    {
                        using var cutoff = new CancellationTokenSource(2000);
                        WorkerProtocol.CallAsync(data, new WorkerRequest { Command = command, Value = command == "pause" }, cutoff.Token).GetAwaiter().GetResult();
                    }
                    catch (Exception error) { cleanupErrors.Add(command + ": " + error.Message); }
                if (!worker.Join(5000))
                {
                    try { capture?.StopCapture(); } catch (Exception error) { cleanupErrors.Add("capture stop: " + error.Message); }
                    if (!worker.Join(1000)) cleanupErrors.Add("Worker join failed; outer controller must terminate owned helper.");
                }
            }
            if (fixture != null)
                try { if (!fixture.HasExited) { fixture.CloseMainWindow(); if (!fixture.WaitForExit(2000)) { fixture.Kill(); Require(fixture.WaitForExit(2000), "Owned fixture absence unconfirmed."); } } }
                catch (Exception error) { cleanupErrors.Add("fixture: " + error.Message); }
                finally { fixture.Dispose(); }
            if (ui != null)
            {
                try { if (!ui.WaitForExit(2000)) { ui.Kill(); Require(ui.WaitForExit(2000), "Owned UI absence unconfirmed."); cleanupErrors.Add("Owned UI needed forced cleanup."); } }
                catch (Exception error) { cleanupErrors.Add("UI cleanup: " + error.Message); }
                finally { ui.Dispose(); }
            }
            if (cleanupErrors.Count > 0) failure ??= new IOException("Owned cleanup failed: " + string.Join("; ", cleanupErrors));
            try { Write(Path.Combine(root, "metrics.json"), metrics,replayMinutes==30?268435456:67108864); Guard(false); }
            catch (Exception error) { cleanupErrors.Add("terminal report/budget: " + error.Message); failure ??= error; }
            try
            {
                bool startupPreserved = Startup() == plan.GetProperty("startup").GetString();
                bool boundInputsPreserved = plan.GetProperty("files").EnumerateObject().All(f => File.Exists(f.Name) && BoundHash(f.Name) == f.Value.GetString())
                    && RuntimePreserved();
                if (!startupPreserved || !boundInputsPreserved) failure ??= new IOException("Terminal preservation check failed.");
                Write(Path.Combine(root, "closure.json"), new { error = failure?.ToString(), cleanupErrors, workerJoined = worker?.IsAlive != true, workerExit,
                    resourceIdentities = new { fixturePid, fixtureBirth, uiPid, uiBirth, capturePid = captureIdentityPid, captureBirth },
                    resourceWindows = new { captureReadyQpc, startupEndQpc, captureStoppedQpc, workerStoppedQpc, fixtureStoppedQpc },
                    cpuWindows = new { pilotEnteredWorkerCpuMilliseconds,pilotEnteredAllocatedBytes,pilotEnteredCollections, captureReady = captureReadyCpu, startupEnd = startupEndCpu,
                        captureStopped = captureStoppedCpu, workerStopped = workerStoppedCpu },
                    captureAbsent = capture?.CaptureProducerAbsent ?? true, children, acquisitions, exports, validators, supplementalDecodes, maxBytes, maxFiles,
                    maximumObservationGap, maximumScan, startupPreserved, boundInputsPreserved,
                    // ExportRequest fills these in its finally path. Read only
                    // after worker join, without extra I/O in measured work.
                    joinedExportTiming = worker?.IsAlive != true ? capture?.LastExportTiming : null,
                    joinedManagedMux = worker?.IsAlive != true ? capture?.LastManagedMux : null,
                    joinedIntegrityGate = worker?.IsAlive != true ? capture?.LastIntegrityGate : null });
                lastScan = -1; Guard(false); // Include final serialization/metadata in the budget.
            }
            catch (Exception error) { failure ??= error; Console.Error.WriteLine("Terminal closure rejected: " + error.Message); }
            if (worker?.IsAlive != true) { CaptureSession.StartingForChecks = null; CaptureSession.StartedForChecks = null; RecorderRuntime.CaptureArgumentObserver = null;
                RecorderRuntime.ExportArgumentObserver = null; ProfileOwnership.RootDirectory = previousAuthority; ReplayCalibrationCatalog.CandidateForChecks = null; }
            captureProcess?.Dispose();
        }
        return failure == null ? 0 : 1;
    }
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr window, int command);
}
