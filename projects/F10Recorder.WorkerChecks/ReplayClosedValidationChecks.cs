using System.Diagnostics;
using System.Text.Json;

namespace F10Recorder;

internal static partial class ReplayCalibrationRun
{
    sealed class ClosedValidationSource(string session, string media, string engine, bool offline, Sampler sampler, string reuse,
        bool reencode = false, double cutoff = 303.5) : IReplaySessionSource
    {
        internal const string Terminal = "Diagnostic full decode complete; publication intentionally suppressed.";
        internal ReplayExportProcessReceipt Receipt;
        internal ReplayExportProcessReceipt EncoderReceipt;
        internal ReplayValidation Validation;
        internal string Partial;
        internal bool OfflineEntered;
        bool stopped = true;
        public ReplaySessionInfo ReplayInfo => new(session, Path.Combine(session, "segments.csv"), 300, 20, 60, "h264_nvenc", ".ts", reencode, true);
        public bool PreserveEvidence => true;
        public bool Stopped => true;
        public bool OutputProducerStopped => stopped;
        public string Diagnostics => "Closed-file validation diagnostic; no acquisition or encoder.";
        public string OutputDemandIdentity => "closed-validation-sourcebound-v1";
        public double SaveCutoffAt(long _) => cutoff;
        public void FreezeAt(long _) { }
        public void StopCapture() { }
        public void CheckCaptureHealth(double _) { }
        public void Dispose() { }
        public ReplayOutputDemand EstimateOutput(ReplayOutputSelection selection)
        { var target = ReplayStorage.ObserveCapacity(selection.Destination); return new(ReplayOutputEvidenceKind.SourceBound, OutputDemandIdentity,
            selection.Identity, selection.ExporterIdentity, target.Volume, target.AllocationUnit, ReplayReferenceModel.Round(1_100_000_000, target.AllocationUnit), false, null); }
        public void Export(IReadOnlyList<string> args, string _) => throw new IOException("Guarded export required.");
        public void Export(IReadOnlyList<string> args, string _, Action<ReplayProducerProof> changed, Action check)
        {
            Partial = args[^1]; stopped = false;
            try
            {
                if (reencode)
                {
                    using var job = new Native.ProcessJob();
                    EncoderReceipt = ReplayExportProcess.Run(engine, args, session, job, producerStopped: absent => stopped = absent,
                        timeoutMilliseconds: 90000, checkStorage: check);
                    Require(EncoderReceipt.Success, "Actual diagnostic encoder failed: " + EncoderReceipt.Error);
                }
                else if (reuse != null) Require(Native.CreateHardLink(Partial, reuse, IntPtr.Zero), "Owned diagnostic copy could not be linked.");
                else
                {
                    using var input = File.OpenRead(media);
                    using var output = new FileStream(Partial, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    byte[] block = new byte[1_048_576]; var cadence = Stopwatch.StartNew(); int n;
                    while ((n = input.Read(block)) != 0) { output.Write(block, 0, n); if (cadence.ElapsedMilliseconds >= 100) { output.Flush(); check(); cadence.Restart(); } }
                }
            }
            finally { stopped = !reencode || EncoderReceipt?.ProducerAbsent == true; }
            check();
        }
        public ReplayValidation ValidateOutput(string file, ReplayInterval interval, Action<ReplayProducerProof> changed, Action check)
        {
            Require(stopped, "Read-only classification requires the owned copy writer to be closed.");
            if (reencode) Require(EncoderReceipt is { Success: true, ActualExitObserved: true, ProducerAbsent: true },
                "Read-only classification requires the actual encoder's successful closed receipt.");
            if (offline)
            {
                sampler.EndActive(); sampler.BeginOffline("closed-runtime-validation", 32_000_000, 60); OfflineEntered = true;
            }
            using var job = new Native.ProcessJob();
            Receipt = ReplayExportProcess.Run(engine, ReplayExactExport.DecodeArguments(file, true, 60), session, job,
                producerStopped: absent => stopped = absent, timeoutMilliseconds: 45000, checkStorage: check);
            Validation = ReplayExactExport.ReadValidation(Receipt, interval, 60);
            throw new IOException(Terminal); // Preserve the partial; this is not publication acceptance.
        }
    }
    internal static int ClosedValidationChecks(string path, string hash)
    {
        Require(new FileInfo(path).Length < 65536 && HashFile(path) == hash, "Closed-validation plan changed.");
        using var document = JsonDocument.Parse(File.ReadAllText(path)); var p = document.RootElement;
        Require(p.GetProperty("kind").GetString() == "issue14-closed-validation-v1" && p.GetProperty("maximumChildren").GetInt32() == 3
            && p.GetProperty("deadlineSeconds").GetInt32() == 210 && p.GetProperty("budgetBytes").GetInt64() == 26_000_000_000,
            "Closed-validation diagnostic boundary changed.");
        foreach (var binding in p.GetProperty("files").EnumerateObject()) Require(HashFile(binding.Name) == binding.Value.GetString(), "Closed-validation input changed.");
        Require(p.GetProperty("files").TryGetProperty(Path.GetFullPath(Environment.ProcessPath), out _)
            && p.GetProperty("files").TryGetProperty(typeof(ReplayCalibrationRun).Assembly.Location, out _), "Diagnostic host/consumer unbound.");
        string root = Path.GetFullPath(p.GetProperty("root").GetString()), family = Path.GetDirectoryName(root);
        string sourceRoot = p.GetProperty("dummySource").GetString(), media = p.GetProperty("media").GetString();
        MediaDependency.RootDirectory = p.GetProperty("engineRoot").GetString(); string engine = MediaDependency.ResolveExecutable();
        Require(p.GetProperty("files").GetProperty(engine).GetString() == MediaDependency.ExecutableSha256.ToUpperInvariant(), "Diagnostic engine unbound.");
        using var owned = PilotStorageObservation.Create(root); Log.DirectoryPath = root; Log.FileName = "closed-validation.log";
        var clock = Stopwatch.StartNew(); var results = new List<object>(); string reused = null; bool proposed = false;
        foreach (bool offline in new[] { false, true })
        {
            string mode = offline ? "readonly" : "active", current = Path.Combine(root, mode); Directory.CreateDirectory(current);
            string[] history = Directory.GetDirectories(family).Where(r => Path.GetFullPath(r) != root)
                .Concat(offline ? new[] { Path.Combine(root, "active") } : []).ToArray();
            var plan = new CalibrationPlan(current, MediaDependency.RootDirectory, "closed-validation", new(), Order, history, clock.Elapsed.TotalSeconds, 0,
                BudgetBytes: 26_000_000_000, StopBytes: 24_000_000_000, FloorBytes: 60_000_000_000, DeadlineSeconds: 205,
                RetainedOfflineSeconds: 0, MetadataHandleRoots: history, ReuseCurrentMetadataHandles: true, FileLimit: 24576, DirectoryLimit: 3072,
                RetainedBudgetBytes: 18_000_000_000, OutputComponentBytes: 1_100_000_000, OutsideBuildAllowanceBytes: 268_435_456,
                FreezeRetainedAllocations: true, RetainActiveOutputHandles: true);
            using var sampler = new Sampler(plan, active: false);
            string cell = Path.Combine(current, "T01"), session = Path.Combine(cell, "buffer", "session-dummy"); Directory.CreateDirectory(session);
            for (int n = 0; n < 310; n++) { string name = $"chunk-{n:D9}.ts"; Require(Native.CreateHardLink(Path.Combine(session, name), Path.Combine(sourceRoot, name), IntPtr.Zero), "Diagnostic input link failed."); }
            File.Copy(Path.Combine(sourceRoot, "segments.csv"), Path.Combine(session, "segments.csv"));
            var diagnostic = new ClosedValidationSource(session, media, engine, offline, sampler, reused);
            string failure = null; ReplaySaveResult result = null;
            using var cache = new ReplayCache(new RetainFiles(sampler, true), cacheDirectory: Path.Combine(cell, "buffer"));
            try
            {
                sampler.BeginActive(); var warm = Stopwatch.StartNew();
                while (warm.Elapsed.TotalSeconds < 30) { Deadline(); sampler.Check(); Thread.Sleep(25); }
                var id = cache.RegisterSession(diagnostic); var save = cache.TrySave(id, 0, cell); Require(save.Accepted, save.Rejection);
                while (!save.Completion.IsCompleted) { Deadline(); sampler.Check(); Thread.Sleep(10); }
                result = save.Completion.GetAwaiter().GetResult();
                Require(!result.Succeeded && result.Error.Contains(ClosedValidationSource.Terminal)
                    && diagnostic.Validation is { Kind: "FullDecode", Frames: >= 17999 and <= 18001 } && diagnostic.Receipt.Success,
                    "Closed-file diagnostic did not complete through the actual product validation/storage guard.");
                cache.CloseAsync().GetAwaiter().GetResult();
                if (diagnostic.OfflineEntered) sampler.EndOffline(); else sampler.EndActive();
            }
            catch (Exception error) { failure = error.ToString(); }
            finally
            {
                try { cache.CloseAsync().GetAwaiter().GetResult(); } catch (Exception error) { failure = (failure ?? "") + "\nCleanup: " + error; }
                try { sampler.StopAndCheck(); } catch (Exception error) { failure = (failure ?? "") + "\nCleanup: " + error; }
            }
            if (diagnostic.Partial != null && File.Exists(diagnostic.Partial)) reused ??= diagnostic.Partial;
            if (offline && failure == null) proposed = true;
            object receipt = diagnostic.Receipt == null ? null : SaveProcess(current, "validator", diagnostic.Receipt);
            var evidence = new { mode, result = failure == null ? "passed" : "failed", failure, diagnostic.Validation,
                process = receipt, lastStorageCheckWorkMilliseconds = diagnostic.Receipt?.LastStorageCheckWorkMilliseconds,
                lastStorageCheckIdleMilliseconds = diagnostic.Receipt?.LastStorageCheckIdleMilliseconds,
                sampler.MaximumActiveGap, sampler.MaximumOfflineGap, sampler.LastObservation, operations = sampler.OutputOperationTimings,
                diagnostic.OfflineEntered, publicationSuppressed = true };
            Write(Path.Combine(current, "verification.json"), evidence); results.Add(evidence);
        }
        Write(Path.Combine(root, "verification.json"), new { result = proposed ? "passed" : "failed", results, elapsedSeconds = clock.Elapsed.TotalSeconds,
            effects = "One helper/two full software decoders; one owned copy plus hardlink of existing closed MP4; real ReplayCache storage/validation callbacks; no acquisition, encoder, UI, device, startup/preferences or catalog activation. Failed live cases remain failed." });
        Console.WriteLine("Closed-file runtime diagnostic: " + (proposed ? "passed" : "failed")); return proposed ? 0 : 1;
        void Deadline() => Require(clock.Elapsed.TotalSeconds < 205, "Closed-validation deadline; cleanup reserve remains.");
    }
}
