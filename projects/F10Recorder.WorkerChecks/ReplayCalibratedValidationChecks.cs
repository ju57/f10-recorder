using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace F10Recorder;

internal static partial class ReplayCalibrationRun
{
    sealed class CalibratedClosedSource(string session, string media, string engine, ReplayReferenceModel model,
        bool reencode = false, NativeEvidence evidence = null) : IReplaySessionSource
    {
        readonly Native.ProcessJob job = new(recoverable: true);
        readonly List<object> guards = new(), estimates = new();
        bool stopped = true, sealedLaunch;
        internal ReplayExportProcessReceipt Receipt;
        internal ReplayExportProcessReceipt EncoderReceipt;
        internal ReplayOutputSelection LastSelection;
        internal ReplayValidation Validation;
        internal bool ValidatorAttempted;
        internal string Preserved;
        internal object Telemetry => new { guards, estimates };
        internal object SaveTelemetry(string root)
        {
            string[] Persist(string kind, List<object> values)
            {
                var names = new List<string>(); int offset = 0;
                foreach (var chunk in values.Chunk(128))
                {
                    string name = kind + "-" + names.Count.ToString("D2") + ".json";
                    Write(Path.Combine(root, name), new { offset, rows = chunk }); names.Add(name); offset += chunk.Length;
                }
                Require(offset == values.Count, "Guard telemetry rows were lost."); return names.ToArray();
            }
            return new { guardCount = guards.Count, estimateCount = estimates.Count,
                guardFiles = Persist("guards", guards), estimateFiles = Persist("estimates", estimates) };
        }
        public ReplaySessionInfo ReplayInfo => new(session, Path.Combine(session, "segments.csv"), 300, 20, 60, "h264_nvenc", ".ts", true, true);
        public bool PreserveEvidence => true;
        public bool Stopped => true;
        public bool OutputProducerStopped => stopped;
        public ReplayProducerProof ProducerProof => ReplayProducerProof.Current(job.Name, sealedLaunch);
        public string OutputDemandIdentity => ReplayReferenceModel.ExporterIdentity;
        public string Diagnostics => "Existing MP4 / frozen calibrated model / real durable ledger diagnostic; no acquisition or encoder.";
        public double SaveCutoffAt(long _) => ReplaySelection.Read(ReplayInfo.Manifest, session)[^1].End;
        public void FreezeAt(long _) { }
        public void StopCapture() { }
        public void CheckCaptureHealth(double _) { }
        public ReplayOutputDemand EstimateOutput(ReplayOutputSelection selection)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                LastSelection = selection;
                var demand = new ReplayCalibrationAvailability(model, null).Estimate(selection, model.Scope, session);
                Require(demand.Kind == ReplayOutputEvidenceKind.CalibratedEstimate, "Actual frozen model became unmeasured: " + demand.Reason);
                return demand;
            }
            finally { Require(estimates.Count < 1024, "Estimate telemetry cap."); estimates.Add(new { ms = watch.Elapsed.TotalMilliseconds }); }
        }
        void Check(Action check)
        {
            long at = Stopwatch.GetTimestamp(); var watch = Stopwatch.StartNew(); string error = null;
            try { check(); } catch (Exception failure) { error = failure.Message; throw; }
            finally { Require(guards.Count < 1024, "Guard telemetry cap."); guards.Add(new { atQpc = at, endedQpc = Stopwatch.GetTimestamp(), ms = watch.Elapsed.TotalMilliseconds, error }); }
        }
        public void Export(IReadOnlyList<string> arguments, string snapshot) => throw new IOException("Guarded export required.");
        public void Export(IReadOnlyList<string> arguments, string snapshot, Action<ReplayProducerProof> changed, Action check)
        {
            if (reencode)
            {
                evidence.RequireNativeSnapshot(arguments, LastSelection);
                EncoderReceipt = ReplayExportProcess.Run(engine, arguments, session, job,
                    started: _ => { sealedLaunch = true; changed(ProducerProof); },
                    producerStopped: absent => { if (!absent) { sealedLaunch = false; changed(ProducerProof); } stopped = absent; },
                    timeoutMilliseconds: 120000, checkStorage: () => Check(check));
                Require(EncoderReceipt.Success, "Retained calibrated encoder failed: " + EncoderReceipt.Error);
                return;
            }
            string partial = arguments[^1]; stopped = false; sealedLaunch = false; changed(ProducerProof);
            try
            {
                {
                    using var input = File.OpenRead(media);
                    using var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    byte[] block = new byte[1_048_576]; int n; var cadence = Stopwatch.StartNew();
                    while ((n = input.Read(block)) != 0) { output.Write(block, 0, n); if (cadence.ElapsedMilliseconds >= 100) { output.Flush(); Check(check); cadence.Restart(); } }
                }
            }
            finally
            {
                stopped = true; sealedLaunch = true;
                changed(ProducerProof);
                Preserve(partial);
            }
            Check(check);
        }
        void Preserve(string partial)
        {
            if (!File.Exists(partial) || Preserved != null) return;
            Preserved = Path.Combine(Path.GetDirectoryName(partial), "diagnostic-preserved.mp4");
            Require(Native.CreateHardLink(Preserved, partial, IntPtr.Zero), "Diagnostic partial preservation failed.");
        }
        public ReplayValidation ValidateOutput(string path, ReplayInterval interval, Action<ReplayProducerProof> changed, Action check)
        {
            Require(stopped, "The bounded copy writer is not closed.");
            // Keep the real product guard and native observations active. This
            // comparison must not spend their freshness window on harness-only
            // FreshObservation phase transitions before decoder startup.
            ValidatorAttempted = true;
            Receipt = ReplayExportProcess.Run(engine, ReplayExactExport.DecodeArguments(path, true, 60), session, job,
                started: _ => { sealedLaunch = true; changed(ProducerProof); },
                producerStopped: absent => { if (!absent) { sealedLaunch = false; changed(ProducerProof); } stopped = absent; },
                timeoutMilliseconds: 45000, checkStorage: () => Check(check));
            Validation = ReplayExactExport.ReadValidation(Receipt, interval, 60);
            throw new IOException(ClosedValidationSource.Terminal);
        }
        public void Dispose() => job.Dispose();
    }

    internal static int CalibratedValidationChecks(string path, string hash)
    {
        Require(new FileInfo(path).Length <= 262144 && HashFile(path) == hash, "Calibrated validation plan changed.");
        using var doc = JsonDocument.Parse(File.ReadAllText(path)); var p = doc.RootElement;
        bool reencode = p.GetProperty("kind").GetString() == "issue14-calibrated-transition-v1";
        Require((reencode || p.GetProperty("kind").GetString() == "issue14-calibrated-validation-v1") && p.GetProperty("maximumChildren").GetInt32() == 3
            && p.GetProperty("deadlineSeconds").GetInt32() == 210, "Calibrated diagnostic boundary changed.");
        foreach (var binding in p.GetProperty("files").EnumerateObject()) Require(HashFile(binding.Name) == binding.Value.GetString(), "Calibrated diagnostic binding changed: " + binding.Name);
        Require(p.GetProperty("files").TryGetProperty(Environment.ProcessPath, out _) && p.GetProperty("files").TryGetProperty(typeof(ReplayCalibrationRun).Assembly.Location, out _), "Actual diagnostic host/consumer unbound.");
        string unknown = "--issue14-unknown-route-" + Guid.NewGuid().ToString("N");
        Require(WorkerChecks.Main([unknown, p.GetProperty("root").GetString(), "1"]) == 2 && !Directory.Exists(unknown),
            "Unknown command fell through into profile/engine work.");
        string root = p.GetProperty("root").GetString(), family = Path.GetDirectoryName(root), original = p.GetProperty("capturedSource").GetString();
        var model = JsonSerializer.Deserialize<ReplayReferenceModel>(File.ReadAllText(p.GetProperty("model").GetString()));
        MediaDependency.RootDirectory = p.GetProperty("engineRoot").GetString(); string engine = MediaDependency.ResolveExecutable();
        Require(model.Revision == ReplayCalibrationCatalog.ModelRevision(model.Scope, model.Volume, model.AllocationUnit,
            model.FinalResidual, model.SampledFinalizationExtra, model.TrainingReceipts, captureHeadroom: model.CaptureHeadroom), "Frozen training model/binary identity changed.");
        var segments = ReplaySelection.Read(Path.Combine(original, "segments.csv"), original);
        Require(segments.Count > 300 && segments.All(s => p.GetProperty("files").TryGetProperty(s.Path, out _)), "Closed source inputs unbound.");
        using var owned = PilotStorageObservation.Create(root); Log.DirectoryPath = root;
        var clock = Stopwatch.StartNew(); var results = new List<object>(); bool nativePassed = false;
        foreach (bool native in reencode ? new[] { true } : new[] { false, true })
        {
            string mode = native ? "native" : "adapter", current = Path.Combine(root, mode); Directory.CreateDirectory(current);
            var history = Directory.GetDirectories(family).Where(h => Path.GetFullPath(h) != Path.GetFullPath(root))
                .Concat(native && !reencode ? new[] { Path.Combine(root, "adapter") } : []).ToArray();
            var plan = new CalibrationPlan(current, MediaDependency.RootDirectory, "calibrated-validation", new(), Order, history, clock.Elapsed.TotalSeconds, 0,
                BudgetBytes: reencode ? 38_000_000_000 : 32_000_000_000, StopBytes: reencode ? 36_000_000_000 : 30_000_000_000,
                FloorBytes: 60_000_000_000, DeadlineSeconds: 205,
                RetainedOfflineSeconds: 0, MetadataHandleRoots: history, ReuseCurrentMetadataHandles: true, FileLimit: 32768, DirectoryLimit: 4096,
                RetainedBudgetBytes: 27_000_000_000, OutputComponentBytes: 1_100_000_000, OutsideBuildAllowanceBytes: 268_435_456,
                FreezeRetainedAllocations: true, RetainActiveOutputHandles: true);
            using var sampler = new Sampler(plan, active: false);
            string cell = Path.Combine(current, "T01"), session = Path.Combine(cell, "buffer", "session-closed"); Directory.CreateDirectory(session);
            foreach (var segment in segments) Require(Native.CreateHardLink(Path.Combine(session, Path.GetFileName(segment.Path)), segment.Path, IntPtr.Zero), "Diagnostic TS link failed.");
            File.Copy(Path.Combine(original, "segments.csv"), Path.Combine(session, "segments.csv"));
            var evidence = reencode ? new NativeEvidence(cell, session) : null;
            evidence?.CaptureClosed();
            var source = new CalibratedClosedSource(session, p.GetProperty("media").GetString(), engine, model, reencode, evidence);
            var claims = new ReplayVolumeReservations(Path.Combine(current, "calibration-ownership"), ReplayStorageSafety.MinimumBytes);
            using var cache = new ReplayCache(native ? new ReplayFiles() : new RetainFiles(sampler, true), cacheDirectory: Path.Combine(cell, "buffer"), volumeReservations: claims);
            bool nativeReader = typeof(ReplayCache).GetField("snapshotInventory", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(cache) != null;
            Require(nativeReader == native, "Diagnostic provider did not select the expected snapshot reader.");
            string failure = null; ReplaySaveResult result = null;
            try
            {
                sampler.BeginActive(); var id = cache.RegisterSession(reencode ? new EvidenceSource(source, evidence) : source); var submission = cache.TrySave(id, 0, cell);
                Require(submission.Accepted && submission.AdmissionDemand.Kind == ReplayOutputEvidenceKind.CalibratedEstimate, submission.Rejection ?? "Diagnostic did not obtain calibrated admission.");
                while (!submission.Completion.IsCompleted) { Require(clock.Elapsed.TotalSeconds < 200, "Calibrated diagnostic deadline."); sampler.Check(); Thread.Sleep(25); }
                result = submission.Completion.GetAwaiter().GetResult();
                Require(!result.Succeeded && result.Error.Contains(ClosedValidationSource.Terminal) && source.Validation?.Frames == 18000
                    && source.Receipt?.Success == true && (!reencode || source.EncoderReceipt?.Success == true), "Actual calibrated ledger validation failed: " + result.Error);
            }
            catch (Exception error) { failure = error.ToString(); }
            finally
            {
                try { cache.CloseAsync().GetAwaiter().GetResult(); if (sampler.ActiveCoverage) sampler.EndActive(); }
                catch (Exception error) { failure = (failure ?? "") + "\nCleanup: " + error; }
                try { evidence?.WriteTimings(); }
                catch (Exception error) { failure = (failure ?? "") + "\nEvidence timing cleanup: " + error; }
                try
                {
                    if (reencode && source.EncoderReceipt is { ProducerAbsent: true, ActualExitObserved: true }
                        && source.Receipt is { ProducerAbsent: true, ActualExitObserved: true })
                    {
                        sampler.BeginOffline("output-release", 0, 30);
                        var releases = sampler.ReleaseClosedOutputHandles(Directory.GetFiles(cell, "*.mp4"));
                        Require(releases.All(r => r.MetadataHandleAbsent && r.Before.Identity == r.After.Identity && r.Before.Length == r.After.Length),
                            "Diagnostic output alias release changed identity/bytes or retained metadata.");
                        evidence.Seal(); sampler.EndOffline();
                    }
                    sampler.StopAndCheck();
                }
                catch (Exception error) { failure = (failure ?? "") + "\nCleanup: " + error; }
            }
            var modeResult = new { mode, nativeReader, result, failure, source.Validation, source.Preserved,
                telemetry = reencode ? source.SaveTelemetry(current) : source.Telemetry,
                encoder = source.EncoderReceipt == null ? null : SaveProcess(current, "encoder", source.EncoderReceipt),
                evidenceAliases = evidence?.OutputAliases,
                decoder = source.Receipt == null ? null : SaveProcess(current, "decoder", source.Receipt), sampler.MaximumActiveGap,
                sampler.LastObservation, outputOperations = sampler.OutputOperationTimings, trace = sampler.TraceReceipt };
            Write(Path.Combine(current, "result.json"), modeResult);
            bool neverLaunched = source.Receipt is { ProducerAbsent: true, ProcessId: null, Failure: "launch-failed" };
            Require(!source.ValidatorAttempted || source.Receipt is { ProducerAbsent: true, ActualExitObserved: true } || neverLaunched,
                "Attempted decoder cleanup is unconfirmed; no next mode or diagnostic pass is admitted.");
            Require(!reencode || source.EncoderReceipt is { ProducerAbsent: true, ActualExitObserved: true }
                || source.EncoderReceipt is { ProducerAbsent: true, ProcessId: null, Failure: "launch-failed" }, "Encoder closure is unconfirmed.");
            // Independent copies avoid mutating a frozen prior output's link
            // metadata while observing the second provider.
            if (native) nativePassed = failure == null;
            results.Add(modeResult);
        }
        Write(Path.Combine(root, "verification.json"), new { result = nativePassed ? "passed-native-path" : "failed", results,
            frozenRevision = model.Revision, unknownRouteRejected = true, elapsedSeconds = clock.Elapsed.TotalSeconds,
            limits = reencode ? "Actual retained TS encode, frozen calibrated model/applicability, private durable ledger, native snapshot reader, corrected evidence hooks and full decode; continuous500ms observations. Three processes,210seconds,38GBcap36GBstop60GBfloor. No acquisition/UI/playback/publication/model activation. Prior failed live batches stay failed."
                : "Actual calibrated model/catalog applicability and private durable ledger. Copy of existing MP4; no capture/encoder/publication/model activation. Comparison is diagnostic, not a qualification sample or causal proof. Prior failed native batch remains failed." });
        Console.WriteLine("Calibrated native provider validation: " + (nativePassed ? "passed" : "failed")); return nativePassed ? 0 : 1;
    }
}
