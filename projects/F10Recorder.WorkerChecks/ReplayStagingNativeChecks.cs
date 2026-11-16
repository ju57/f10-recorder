using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace F10Recorder;

// Generated closed media only: the real staged pipe/process, Cache ownership,
// fallback/full decode/publication and cancellation. No acquisition or devices.
internal static class ReplayStagingNativeChecks
{
    const long Cap = 536_870_912, Stop = 402_653_184, Floor = 60_000_000_000;
    const string CancelReason = "CONTROLLED-NATIVE-STAGED-CANCEL";
    static void Need(bool value, string reason) { if (!value) throw new IOException(reason); }
    static bool SamePublishedOutput(ReplayFileExtent a, ReplayFileExtent b) => a != null && b != null
        && a.Volume == b.Volume && a.Identity == b.Identity && a.Length == b.Length && a.AllocationUnit == b.AllocationUnit;
    internal static int IdentityChecks()
    {
        var expected = new ReplayFileExtent("volume", "file-id", 8192, 5000, 4096); int passed = 0;
        foreach (var (observed, matches) in new (ReplayFileExtent, bool)[] {
            (expected, true), (expected with { AllocatedBytes = 4096 }, true), (expected with { AllocatedBytes = 16384 }, true),
            (expected with { Volume = "other" }, false), (expected with { Identity = "other" }, false),
            (expected with { Length = 4999 }, false), (expected with { AllocationUnit = 8192 }, false), (null, false) })
        { Need(SamePublishedOutput(expected, observed) == matches, "Published-object identity control failed."); passed++; }
        Console.WriteLine($"Passed {passed} published-object identity controls; no native/process/media effects."); return 0;
    }
    static void Write(string root, string name, object value)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions(WorkerProtocol.Json) { WriteIndented = true });
        Need(bytes.Length <= 2 * 1024 * 1024, "Native-control report bound.");
        using var output = new FileStream(Path.Combine(root, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        output.Write(bytes); output.Flush(true);
    }
    internal static int Prepare(string planFile, string root, string engineRoot, string product, string productionData)
    {
        foreach (string path in new[] { planFile, root, engineRoot, product, productionData }) Need(Path.IsPathFullyQualified(path), "Absolute paths required.");
        Need(!File.Exists(planFile) && !Directory.Exists(root) && !File.Exists(root)
            && root.StartsWith(Path.GetFullPath("artifacts") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Fresh isolated admission/output required.");
        MediaDependency.RootDirectory = engineRoot; string engine = MediaDependency.ResolveExecutable();
        Need(ReplayWorkerLatencyPilot.Hash(Path.Combine(product, "F10Recorder.Windows.dll")) == ReplayWorkerLatencyPilot.Hash(typeof(CaptureSession).Assembly.Location)
            && ReplayWorkerLatencyPilot.Hash(Path.Combine(product, "F10Recorder.Core.dll")) == ReplayWorkerLatencyPilot.Hash(typeof(Settings).Assembly.Location), "Frozen product bytes required.");
        Need(new DriveInfo(Path.GetPathRoot(root)).DriveFormat == "NTFS" && ReplayStorage.ObserveCapacity(Path.GetDirectoryName(root)).AvailableBytes >= Floor + Cap, "Native NTFS and full new promise required.");
        string admission = Path.GetDirectoryName(planFile); Directory.CreateDirectory(admission);
        string runtimeManifest = Path.Combine(admission, "runtime-closure.json");
        Write(admission, "runtime-closure.json", ReplayWorkerLatencyPilot.RuntimeFiles().ToDictionary(p => p, p => ReplayWorkerLatencyPilot.Hash(p)));
        Need(new FileInfo(runtimeManifest).Length <= 262144, "Runtime manifest bound.");
        string[] shared = [Path.Combine(productionData, "settings.json"), Path.Combine(productionData, "worker-state.json")];
        string build = Path.GetDirectoryName(typeof(ReplayStagingNativeChecks).Assembly.Location);
        var files = Directory.GetFiles(build).Where(p => Path.GetExtension(p) is ".dll" or ".json")
            .Concat(new[] { Environment.ProcessPath, engine, runtimeManifest, Path.Combine(Environment.SystemDirectory, "conhost.exe"),
                Path.GetFullPath("projects/F10Recorder.WorkerChecks/ReplayStagingNativeChecks.cs"),
                Path.GetFullPath("projects/F10Recorder.WorkerChecks/ReplayWorkerLatencyPilot.cs"), Path.GetFullPath("tests/replay-staging-native.ps1") })
            .Concat(shared).Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(p => p, p => ReplayWorkerLatencyPilot.Hash(p, shared.Contains(p)), StringComparer.OrdinalIgnoreCase);
        Write(admission, Path.GetFileName(planFile), new { kind = "issue83-native-staging-fallback-v1", root, engineRoot, product, productionData, files, sharedFiles = shared, runtimeManifest,
            maximumChildren = 9, cumulativeProcessesIncludingPreparationAndController = 12,
            consoleSupport = new { path = Path.Combine(Environment.SystemDirectory, "conhost.exe"), maximumDirectChildren = 1, maximumWorkingSetBytes = 67_108_864 },
            captureSeconds = 0, fixtureLifetimeSeconds = 0, deadlineSeconds = 120,
            budgetBytes = Cap, stopBytes = Stop, minimumFreeBytes = Floor, expectedForwardBytes = 385_875_968,
            storageEnvelope = new { twoRetainedSourceLimits = 75_497_472, snapshotPeak = 134_217_728, mainPeak = 83_886_080,
                scratchPeak = 50_331_648, reportsMetadata = 25_165_824, margin = 16_777_216 },
            maximumFiles = 512, maximumDirectories = 64, maximumMediaChildren = 8, generatedMediaSeconds = 14,
            sampleOrder = new[] { "unsupported-long-interior-gop-fallback", "cancel-before-feed", "cancel-during-paced-real-feed" },
            startup = ReplayWorkerLatencyPilot.Startup(), createdUtc = DateTime.UtcNow,
            effects = "Two7s generated H264/AAC closed fixtures:320x192 absolute-picture IDs/long interior GOP,1920x1080 noise/1sGOP. Up to8 verified FFmpeg children (two generators,three actual staged children,one exact fallback,one full validator,one postSaved source-ID decoder). Private SourceBound test envelope, real native ledger/Cache/pipe/process; NO calibrated-source claim. No desktop/system/microphone acquisition, playback/UI/worker/catalog/settings/startup/vendor/install changes. Generated sources/failures retained.",
            decision = "Fallback must cancel/join real waiting staged child, run actual exact exporter/full decode before publication, then independent ordered picture IDs. Pre-feed and paced-real-feed cancellation must publish noMP4 and join actual child/feeder before claim release. Paced cancellation changes only that test child's input pacing/probe arguments; no live/latency/resource/qualified-media claim." });
        Console.WriteLine(planFile + " " + ReplayWorkerLatencyPilot.Hash(planFile)); return 0;
    }

    sealed class Source(string directory, string mode, Action guard, Action<string> admit,
        Action<ReplayExportProcessReceipt> record) : IReplaySessionSource
    {
        readonly Native.ProcessJob job = new(recoverable: true);
        internal ReplaySavePreparation Preparation;
        internal ReplaySaveTiming Timing;
        internal ReplayExportProcessReceipt Validation;
        internal string Recipe;
        internal int Preparations, Validations, CancellationInjected;
        internal long CancelAt, FeedStartedAtCancel, FeedCompletedAtCancel, FeedBytesAtCancel, CandidateInputBytes;
        internal bool ActualStageAliveAtCancel;
        internal int? StagePid;
        internal DateTime? StageBirth;
        internal Process StageWitness;
        int active, unknown; bool sealedLaunch = true;
        public ReplaySessionInfo ReplayInfo => new(directory, Path.Combine(directory, "segments.csv"), 6, 20, 60, "h264_nvenc", ".ts", false, true);
        public bool SupportsOwnedExport => true;
        public bool SupportsStagedPreparation => true;
        public bool PreserveEvidence => true;
        public bool Stopped => true;
        public bool OutputProducerStopped => Volatile.Read(ref active) == 0 && Volatile.Read(ref unknown) == 0 && (Preparation?.Stopped ?? true);
        public ReplayProducerProof ProducerProof => ReplayProducerProof.Current(job.Name, sealedLaunch);
        public string OutputDemandIdentity => "controlled-generated-native-staging-v1";
        public string Diagnostics => "Closed generated native-staging fixture; no capture or catalog qualification.";
        public double SaveCutoffAt(long _) => 6.75;
        public void FreezeAt(long _) { }
        public void StopCapture() { }
        public void CheckCaptureHealth(double _) { }
        public ReplayOutputDemand EstimateOutput(ReplayOutputSelection selection)
        {
            var capacity = ReplayStorage.ObserveCapacity(selection.Destination);
            long sourceLimit = mode == "feed-cancel" ? 67_108_864 : 8_388_608;
            Need(capacity.AllocationUnit == 4096 && Directory.GetFiles(directory, "chunk-*.ts").Sum(p => new FileInfo(p).Length) <= sourceLimit,
                "Controlled fixture exceeds its fixed native envelope.");
            return new(ReplayOutputEvidenceKind.SourceBound, "controlled-native-fixture-only", selection.Identity, OutputDemandIdentity,
                capacity.Volume, capacity.AllocationUnit, 134_217_728, true, null, 134_217_728, new(33_554_432, 8_388_608));
        }
        void Check(Action product)
        {
            product(); guard();
            var timing = Preparation?.Timing;
            long fed = timing == null ? 0 : Interlocked.Read(ref timing.FedBytes);
            if (mode == "feed-cancel" && timing != null && Volatile.Read(ref timing.FeedStarted) != 0 && Volatile.Read(ref timing.FeedCompleted) == 0
                && fed >= 65536 && fed < CandidateInputBytes && StageWitness != null && !StageWitness.HasExited
                && Interlocked.CompareExchange(ref CancellationInjected, 1, 0) == 0)
            {
                CancelAt = Stopwatch.GetTimestamp(); FeedStartedAtCancel = timing.FeedStarted; FeedCompletedAtCancel = timing.FeedCompleted;
                FeedBytesAtCancel = fed; ActualStageAliveAtCancel = true;
                throw new OperationCanceledException(CancelReason);
            }
        }
        public ReplaySavePreparation BeginPreparation(ReplayRootedFiles files, ReplayInterval interval, Segment candidate, string partial,
            Action accepted, Action<ReplayProducerProof> changed, Action check, Action<ReplaySavePreparation> take)
        {
            Preparations++; CandidateInputBytes = new FileInfo(candidate.Path).Length;
            Preparation = new(files, ReplayInfo, interval, candidate, partial, () => Check(check)); take(Preparation);
            Preparation.RecordReceipt = record;
            Preparation.Start((arguments, retained) =>
            {
                var actual = arguments.ToList();
                if (mode == "feed-cancel")
                {
                    int input = actual.IndexOf("-i"); Need(input >= 0, "Staged input argument missing.");
                    actual.InsertRange(input, ["-readrate", "0.05", "-probesize", "32", "-analyzeduration", "0"]);
                }
                admit("stage-" + mode); sealedLaunch = false; changed(ProducerProof);
                return new ReplayStagedProcess(MediaDependency.ExecutablePath, actual, Path.GetDirectoryName(partial), job, accepted, () => Check(check),
                    started: child =>
                    {
                        StagePid = child.Id; StageBirth = child.StartTime.ToUniversalTime(); StageWitness = Process.GetProcessById(child.Id); _ = StageWitness.SafeHandle;
                        Need(StageWitness.StartTime.ToUniversalTime() == StageBirth && !child.HasExited, "Exact retained staged child birth required.");
                        sealedLaunch = true; changed(ProducerProof);
                    },
                    producerStopped: absent => { if (!absent) { sealedLaunch = false; changed(ProducerProof); } },
                    retainedOwner: new object[] { this, retained, files }, timeoutMilliseconds: 10000, outputLimitBytes: 16384);
            });
            return Preparation;
        }
        ReplayExportProcessReceipt Invoke(IReadOnlyList<string> arguments, Action<ReplayProducerProof> changed, Action check, IDisposable input)
        {
            admit("export-or-validator"); Interlocked.Increment(ref active); sealedLaunch = false; changed(ProducerProof);
            var receipt = ReplayExportProcess.Run(MediaDependency.ExecutablePath, arguments, directory, job,
                started: _ => { sealedLaunch = true; changed(ProducerProof); }, producerStopped: absent => { if (!absent) { sealedLaunch = false; changed(ProducerProof); } },
                timeoutMilliseconds: 20000, outputLimitBytes: 16384, checkStorage: () => Check(check), inputLease: input);
            record(receipt); if (!receipt.ProducerAbsent) Interlocked.Exchange(ref unknown, 1); Interlocked.Decrement(ref active);
            Need(receipt.Success, "Actual media child failed: " + receipt.Failure + " " + receipt.Error + " " + receipt.StandardError); return receipt;
        }
        public void Export(IReadOnlyList<string> arguments, string snapshot) => throw new IOException("Request-aware native control required.");
        public void Export(ReplayExportRequest request, string snapshot, Action<ReplayProducerProof> changed, Action check)
        {
            Need(request.Preparation == Preparation && Preparation != null, "Actual reserved preparation was not selected.");
            Preparation.WaitForReady(); Need(Preparation.Timing.Ready > 0 && StagePid > 0, "Real staged process never became ready.");
            Timing = request.Timing;
            if (mode == "before-feed")
            {
                Preparation.Cancel(); Interlocked.Exchange(ref CancellationInjected, 1); CancelAt = Stopwatch.GetTimestamp();
                throw new OperationCanceledException(CancelReason);
            }
            request.ExternalProducersAbsent = () => OutputProducerStopped;
            try { ReplaySaveExport.Run(request, (args, callback) => Invoke(args, changed, callback, request.SourceProtection), () => Check(check), ReplaySaveExport.Construction.PacketCopyBatchCancellable); }
            finally { Recipe = request.Recipe; }
        }
        public ReplayValidation ValidateOutput(ReplayExportRequest request, Action<ReplayProducerProof> changed, Action check)
        {
            Need(mode == "fallback" && request.Recipe == ReplayExactExport.Recipe, "Only actual exact fallback may validate.");
            var lease = new ReplayDecodeLease(request.Partial); bool absent = false;
            try
            {
                lease.BindObservation(request); Validation = Invoke(ReplayExactExport.DecodeArguments(request.Partial, true, 60), changed, check, lease);
                absent = OutputProducerStopped; Need(absent, "Validator absence is not confirmed.");
                var result = ReplayExactExport.ReadValidation(Validation, request.Interval, 60); lease.VerifyUnchanged(); check();
                var identity = lease.CheckedIdentity; lease.TransferTo(request); Validations++;
                return result with { ExportRecipe = request.Recipe, CheckedOutput = identity };
            }
            finally { lease.EndObservation(uncertain: !absent); if (absent) lease.Dispose(); }
        }
        public void Dispose() { if (OutputProducerStopped) { StageWitness?.Dispose(); job.Dispose(); } }
    }

    internal static int Run(string planFile, string expectedHash)
    {
        Need(new FileInfo(planFile).Length <= 65536 && ReplayWorkerLatencyPilot.Hash(planFile) == expectedHash, "Exact native plan required.");
        using var document = JsonDocument.Parse(File.ReadAllBytes(planFile)); var p = document.RootElement;
        Need(p.GetProperty("kind").GetString() == "issue83-native-staging-fallback-v1" && p.GetProperty("maximumChildren").GetInt32() == 9
            && p.GetProperty("deadlineSeconds").GetInt32() == 120 && p.GetProperty("captureSeconds").GetInt32() == 0
            && p.GetProperty("budgetBytes").GetInt64() == Cap && p.GetProperty("stopBytes").GetInt64() == Stop && p.GetProperty("minimumFreeBytes").GetInt64() == Floor
            && DateTime.UtcNow.AddSeconds(120) < p.GetProperty("createdUtc").GetDateTime().AddHours(4), "Current finite native control required.");
        string[] shared = p.GetProperty("sharedFiles").EnumerateArray().Select(v => v.GetString()).ToArray();
        void Bindings()
        {
            foreach (var file in p.GetProperty("files").EnumerateObject()) Need(ReplayWorkerLatencyPilot.Hash(file.Name, shared.Contains(file.Name)) == file.Value.GetString(), "Native binding changed: " + file.Name);
            string manifest = p.GetProperty("runtimeManifest").GetString();
            Need(new FileInfo(manifest).Length <= 262144, "Runtime byte bound.");
            var runtime = ReplayWorkerLatencyPilot.ParseRuntime(File.ReadAllBytes(manifest), p.GetProperty("files").GetProperty(manifest).GetString(), ReplayWorkerLatencyPilot.RuntimeFiles());
            foreach (var file in runtime) Need(ReplayWorkerLatencyPilot.Hash(file.Key) == file.Value, "Runtime binding changed.");
            Need(ReplayWorkerLatencyPilot.Startup() == p.GetProperty("startup").GetString(), "Startup changed.");
        }
        Bindings(); MediaDependency.RootDirectory = p.GetProperty("engineRoot").GetString();
        string root = p.GetProperty("root").GetString(); Need(Path.IsPathFullyQualified(root) && !Directory.Exists(root) && !File.Exists(root)
            && root.StartsWith(Path.GetFullPath("artifacts") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Fresh private native root required.");
        Directory.CreateDirectory(root); Write(root, "admitted-plan.json", p);
        var timer = Stopwatch.StartNew(); var receipts = new List<ReplayExportProcessReceipt>(); var results = new List<object>();
        var argumentsLog = new List<object>(); int admitted = 0; long peakBytes = 0; int peakFiles = 0; double lastScan = -1;
        string priorAuthority = ProfileOwnership.RootDirectory, priorLog = Log.DirectoryPath; Exception failure = null;
        void Guard()
        {
            Need(timer.Elapsed.TotalSeconds < 100, "Native whole work deadline;20s cleanup reserved.");
            if (timer.Elapsed.TotalSeconds - lastScan < .1) return; lastScan = timer.Elapsed.TotalSeconds;
            var inventory = ReplayStorage.Inventory([root], new ReplayFiles(), 512, 64); long bytes = ReplayStorage.Bytes(inventory);
            peakBytes = Math.Max(peakBytes, bytes); peakFiles = Math.Max(peakFiles, inventory.Count);
            Need(bytes < Stop && ReplayStorage.ObserveCapacity(root).AvailableBytes >= Floor, "Native storage/floor bound.");
        }
        void Admit(string role) { Guard(); Need(++admitted <= 8, "Media child ceiling: " + role); }
        void Record(ReplayExportProcessReceipt receipt) { receipts.Add(receipt); Need(receipt.ProducerAbsent && receipt.ActualExitObserved && receipt.ResourcesFinal, "Actual child exit/resources remain unknown."); }
        ReplayExportProcessReceipt RunMedia(IReadOnlyList<string> args, IDisposable input = null)
        {
            Admit("generator-or-independent-ids"); argumentsLog.Add(args);
            using var job = new Native.ProcessJob();
            var receipt = ReplayExportProcess.Run(MediaDependency.ExecutablePath, args, root, job, timeoutMilliseconds: 20000, outputLimitBytes: 16384, checkStorage: Guard, inputLease: input);
            Record(receipt); Need(receipt.Success, "Generated/native oracle process failed: " + receipt.StandardError); return receipt;
        }
        string Generate(string name, bool longGops)
        {
            string directory = Path.Combine(root, name); Directory.CreateDirectory(directory);
            string bit = "mod(floor(N/pow(2,floor(X/16))),2)";
            string picture = longGops ? "nullsrc=s=320x192:r=60,geq=lum='if(lt(Y,16),16+219*" + bit + ",if(lt(Y,32),235-219*" + bit + ",96))':cb=128:cr=128"
                : "testsrc2=size=1920x1080:rate=60,noise=alls=100:allf=t+u";
            var args = new List<string> { "-hide_banner", "-loglevel", "warning", "-y", "-f", "lavfi", "-i", picture, "-f", "lavfi", "-i", "sine=frequency=1000:sample_rate=48000",
                "-t", "7", "-map", "0:v:0", "-map", "1:a:0", "-c:v", "h264_nvenc", "-preset", "p3", "-tune", "ll", "-rc", "cbr", "-b:v", longGops ? "500k" : "20M",
                "-g", longGops ? "180" : "60", "-bf", "0", "-flags", "+cgop", "-forced-idr", "1", "-force_key_frames", longGops ? "0,1,4" : "expr:gte(t,n_forced*1)",
                "-c:a", "aac", "-b:a", "64k", "-ar", "48000", "-ac", "2", "-f", "segment", "-segment_format", "mpegts", "-reset_timestamps", "0", "-avoid_negative_ts", "disabled",
                "-segment_format_options", "mpegts_copyts=1:avoid_negative_ts=disabled", "-segment_list_type", "csv", "-segment_list", Path.Combine(directory, "segments.csv") };
            args.AddRange(longGops ? ["-segment_times", "1,4"] : ["-segment_time", "1"]); args.Add(Path.Combine(directory, "chunk-%09d.ts")); RunMedia(args); return directory;
        }
        try
        {
            ProfileOwnership.RootDirectory = Path.Combine(root, "authority"); Log.DirectoryPath = root;
            string unsupported = Generate("long-gop-source", true), regular = Generate("regular-source", false);
            foreach (string mode in new[] { "fallback", "before-feed", "feed-cancel" })
            {
                string directory = mode == "feed-cancel" ? regular : unsupported;
                string output = Path.Combine(root, mode); Directory.CreateDirectory(output);
                var originals = Directory.GetFiles(directory, "chunk-*.ts").ToDictionary(path => path, path => ReplayWorkerLatencyPilot.Hash(path));
                using var source = new Source(directory, mode, Guard, Admit, Record);
                var segments = ReplaySelection.Read(source.ReplayInfo.Manifest, directory);
                Need(segments.Count >= 3 && segments[0].End - segments[0].Start <= 2.001
                    && (mode == "feed-cancel" ? segments.All(s => s.End - s.Start <= 2.001) && new FileInfo(segments[0].Path).Length >= 131072 : segments.Any(s => s.End - s.Start > 2.001)), "Generated staging/GOP control shape is wrong.");
                using var cache = new ReplayCache(cacheDirectory: directory, cacheBudgetBytes: Cap, volumeReservations: new ReplayVolumeReservations(ProfileOwnership.RootDirectory));
                var id = cache.RegisterSession(source); cache.PreserveSession(id);
                var accepted = cache.TrySave(id, Stopwatch.GetTimestamp(), output); Need(accepted.Accepted, "Native Save rejected: " + accepted.Rejection);
                var saved = accepted.Completion.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
                Need(source.Preparations == 1 && source.Preparation.Joined && source.OutputProducerStopped, "Actual staged users did not join.");
                if (mode == "fallback")
                {
                    Need(saved.Succeeded && saved.Validation?.Kind == "FullDecode" && saved.Validation.Frames == 360 && source.Validations == 1
                        && source.Recipe == ReplayExactExport.Recipe && source.Preparation.Timing.Aborted && source.Preparation.Timing.FeedStarted == 0,
                        "Valid unsupported GOPs did not cancel real preparation and fully validate exact fallback before publication: " + saved.Error);
                    string pixels = Path.Combine(output, "ids.gray");
                    var oracle = new ReplayDecodeLease(saved.File); ReplayExportProcessReceipt decoded = null;
                    try
                    {
                        bool immutableIdentityMatch = SamePublishedOutput(oracle.CheckedIdentity, saved.Validation.CheckedOutput);
                        Write(root, "fallback-publication-before-oracle.json", new { saved, reopenedOracleIdentity = oracle.CheckedIdentity,
                            source.StagePid, source.StageBirth, preparation = source.Preparation.Timing,
                            publicationBound = saved.Validation.PublicationIdentityBound, immutableIdentityMatch,
                            allExtentFieldsEqual = oracle.CheckedIdentity == saved.Validation.CheckedOutput,
                            allocationDelta = oracle.CheckedIdentity.AllocatedBytes - (saved.Validation.CheckedOutput?.AllocatedBytes ?? 0) });
                        Need(saved.Validation.PublicationIdentityBound && immutableIdentityMatch, "Independent oracle must read the checked published object.");
                        decoded = RunMedia(["-hide_banner", "-loglevel", "error", "-xerror", "-ignore_editlist", "1", "-i", saved.File, "-map", "0:v:0", "-an", "-vf", "crop=320:32:0:0,scale=20:2:flags=area,format=gray",
                            "-fps_mode", "passthrough", "-pix_fmt", "gray", "-f", "rawvideo", pixels], oracle);
                        oracle.VerifyUnchanged();
                    }
                    finally { if (decoded?.ProducerAbsent == true) oracle.Dispose(); }
                    byte[] raw = File.ReadAllBytes(pixels); Need(raw.Length == 360 * 40, "Fallback ID geometry/count differs.");
                    for (int n = 0; n < 360; n++)
                    {
                        int value = 0;
                        for (int b = 0; b < 20; b++) { byte a = raw[n * 40 + b], complement = raw[n * 40 + 20 + b]; Need((a < 70 || a > 180) && (complement < 70 || complement > 180) && (a > 128) != (complement > 128), "Ambiguous fallback picture ID."); if (a > 128) value |= 1 << b; }
                        Need(value == 45 + n, "Fallback changed ordered physical source picture IDs.");
                    }
                }
                else Need(!saved.Succeeded && source.CancellationInjected == 1 && saved.Error?.Contains(CancelReason, StringComparison.Ordinal) == true
                    && !Directory.EnumerateFiles(output, "*.mp4", SearchOption.AllDirectories).Any()
                    && source.Preparation.Timing.Aborted && (mode != "feed-cancel" || source.FeedStartedAtCancel > 0 && source.FeedCompletedAtCancel == 0
                        && source.FeedBytesAtCancel >= 65536 && source.FeedBytesAtCancel < source.CandidateInputBytes && source.ActualStageAliveAtCancel && source.Preparation.Timing.FeedJoined),
                    "Native staged cancellation published or failed to join actual process/feed: " + saved.Error);
                cache.CloseAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                Need(originals.All(pair => ReplayWorkerLatencyPilot.Hash(pair.Key) == pair.Value), "Generated original input changed.");
                using (var ledger = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(ProfileOwnership.RootDirectory, "replay-volume-reservations.json"))))
                    Need(ledger.RootElement.GetProperty("Owners").GetArrayLength() == 0, "Joined native control retained a reservation owner.");
                results.Add(new { mode, saved, source.StagePid, source.StageBirth, source.Preparations, source.Validations, source.CancellationInjected, source.CancelAt,
                    source.FeedStartedAtCancel, source.FeedCompletedAtCancel, source.FeedBytesAtCancel, source.CandidateInputBytes, source.ActualStageAliveAtCancel,
                    preparation = source.Preparation.Timing, source.Timing, source.Recipe,
                    inputsPreserved = true, actualChildAndFeedJoined = true, actualIndependentSourceIds = mode == "fallback" });
                Write(root, mode + ".json", results[^1]); Guard();
            }
            Bindings(); Write(root, "result.json", new { result = "native-staged-fallback-and-cancellation-passed", results, receipts, admitted, peakBytes, peakFiles,
                interpretation = "Generated closed fixtures and actual staged FFmpeg processes/pipes, Cache/native ownership, full fallback validation/publication/source IDs and nonpublication on controlled cancellations. Paced-feed args are test-only. No desktop/audio acquisition, ordinary-worker latency, empirical catalog transfer or resource qualification." });
        }
        catch (Exception error) { failure = error; Console.Error.WriteLine(error); }
        finally
        {
            try { Write(root, "closure.json", new { error = failure?.ToString(), receipts, admitted, allActualChildrenAbsent = receipts.All(r => r.ProducerAbsent && r.ActualExitObserved), argumentsLog, peakBytes, peakFiles }); Bindings(); Guard(); }
            catch (Exception error) { failure ??= error; Console.Error.WriteLine(error); }
            ProfileOwnership.RootDirectory = priorAuthority; Log.DirectoryPath = priorLog;
        }
        return failure == null ? 0 : 1;
    }
}
