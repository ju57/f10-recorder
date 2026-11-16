using System.Diagnostics;
using System.Text.Json;

namespace F10Recorder;

internal static partial class ReplayCalibrationRun
{
    internal static int ProbeCompositeHistory(string path, string expectedHash)
    {
        Require(new FileInfo(path).Length <= 65536 && HashFile(path) == expectedHash, "Reviewed history probe changed.");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var plan = document.RootElement.GetProperty("plan").Deserialize<CalibrationPlan>(); ValidateCompositeResources(plan);
        ValidateCompositeOutsideBaseline(plan);
        foreach (var file in plan.Files) Require(HashFile(file.Key) == file.Value, "History probe binding changed: " + file.Key);
        using var owned = PilotStorageObservation.Create(plan.Root); Log.DirectoryPath = plan.Root;
        var clock = Stopwatch.StartNew();
        try
        {
            using var history = new FrozenCalibrationHistory(plan.RetainedRoots, plan.FileLimit, plan.DirectoryLimit);
            Require(history.AllocatedBytes <= plan.RetainedBudgetBytes && history.Volumes.All(v =>
                ReplayStorage.ObserveCapacity(v.Directory).AvailableBytes >= plan.FloorBytes), "History probe native retained/free-space bound exceeded.");
            for (int n = 0; n < 10; n++) { history.Check(); Require(clock.Elapsed.TotalSeconds < 25, "History probe deadline."); Thread.Sleep(100); }
            Write(Path.Combine(plan.Root, "result.json"), new { result = "passed-history-lease-probe", planHash = expectedHash,
                history.FileCount, history.DirectoryCount, history.AllocatedBytes, seconds = clock.Elapsed.TotalSeconds,
                captureStarted = false, mediaChildren = 0, semanticModel = false });
            return 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Write(Path.Combine(plan.Root, "failure.json"), new { result = "failed-no-retry", phase = "history-lease-probe", planHash = expectedHash,
                error = error.ToString(), seconds = clock.Elapsed.TotalSeconds, captureStarted = false, mediaChildren = 0 });
            Console.Error.WriteLine(error); return 1;
        }
    }
    internal static string[] InspectionArguments(string root, string file, double seconds)
    {
        string select = "select='" + string.Join("+", InspectionFrames(seconds).Select(n => "eq(n," + n + ")")) + "'";
        string evidence = Path.Combine(root, Path.GetFileName(file));
        var full = ReplayExactExport.DecodeArguments(file, true, 60);
        return [.. full.Take(full.Count - 3), "-af", "astats=metadata=0:reset=0", "-f", "null", "-",
            "-map", "0:v:0", "-vf", select + ",crop=576:48:16:8,scale=24:2:flags=neighbor,format=gray", "-frames:v", "3", "-fps_mode", "passthrough", "-threads:v", "1", "-f", "rawvideo", evidence + ".barcodes.raw",
            "-map", "0:v:0", "-vf", select + ",crop=1920:984:0:96,scale=64:36:flags=neighbor,format=rgb24", "-frames:v", "3", "-fps_mode", "passthrough", "-threads:v", "1", "-f", "rawvideo", evidence + ".pictures.raw"];
    }
    internal static int RehearseComposite(string path, string expectedHash)
    {
        Require(new FileInfo(path).Length <= 65536 && HashFile(path) == expectedHash, "Reviewed composite rehearsal plan changed.");
        using var admission = JsonDocument.Parse(File.ReadAllText(path));
        bool outputReadOwnership = admission.RootElement.TryGetProperty("outputReadOwnership", out var ownership)
            && ownership.GetString() == "continuous-qualification-shape-v1";
        bool publicationPath = admission.RootElement.TryGetProperty("publicationPath", out var publication);
        Require(!publicationPath || publication.GetString() == "retain-files-validated-owner-v1", "Unknown rehearsal publication path.");
        Require(!publicationPath || outputReadOwnership, "Publication rehearsal requires the continuous qualification owner shape.");
        var plan = admission.RootElement.GetProperty("plan").Deserialize<CalibrationPlan>();
        int parentHandleBudget = CompositeParentHandleBudget(plan);
        ValidateCompositeResources(plan);
        foreach (var file in plan.Files) Require(HashFile(file.Key) == file.Value, "Rehearsal input changed: " + file.Key);
        ValidateCompositeOutsideBaseline(plan);
        Require(plan.Files.ContainsKey(typeof(ReplayCalibrationRun).Assembly.Location)
            && plan.Files.ContainsKey(Path.GetFullPath(Environment.ProcessPath)), "Actual rehearsal helper/host must be bound.");
        using var owned = PilotStorageObservation.Create(plan.Root);
        Log.DirectoryPath = plan.Root;
        using var parent = Process.GetCurrentProcess();
        int sourceFiles = DurationQualification(plan) ? ReferenceSeconds(plan) + 1 : 301;
        long outputBytes = DurationQualification(plan) ? plan.ReplayMinutes switch { 1 => 180_000_000L, 30 => 4_600_000_000L, _ => 800_000_000L } : 800_000_000L;
        long currentBytes = DurationQualification(plan) ? checked(plan.SourceComponentBytes + plan.OutputComponentBytes + QualificationScratchBytes(plan)) : 3_200_000_000L;
        int rehearsalSeconds = DurationQualification(plan) && plan.ReplayMinutes == 30 ? 600 : 180;
        using var sampler = new Sampler(plan, active: false, currentBudgetBytes: currentBytes);
        var clock = Stopwatch.StartNew(); var block = new byte[65536]; Array.Fill(block, (byte)0x5a);
        long checkCount = 0, metricQueryCount = 0, lastMetricQuery = 0, peakRam = 0; int peakHandles = 0;
        double maximumMetricQueryAgeMilliseconds = 0;
        double samplerCheckMilliseconds = 0, processQueryMilliseconds = 0;
        var checkpoints = new List<object>();
        var outputOwners = new List<(FileStream Reader, Microsoft.Win32.SafeHandles.SafeFileHandle Handle, string Final, ReplayFileExtent Extent)>();
        var ownerClosures = new List<object>();
        var publicationControls = new List<object>();
        var cleanup = new List<object>(); var cleanupErrors = new List<string>();
        Exception primaryFailure = null; Func<object> result = null;
        var stages = new Dictionary<string, (int Count, double Total, double Maximum, string Path)>();
        T Stage<T>(string name, string file, Func<T> work)
        {
            long at = Stopwatch.GetTimestamp(); double sampleAt = sampler.ElapsedSeconds * 1000;
            try { return work(); }
            finally
            {
                double milliseconds = Stopwatch.GetElapsedTime(at).TotalMilliseconds;
                var previous = stages.GetValueOrDefault(name);
                stages[name] = (previous.Count + 1, previous.Total + milliseconds, Math.Max(previous.Maximum, milliseconds),
                    milliseconds >= previous.Maximum ? file : previous.Path);
                if (file.Contains(".partial.mp4", StringComparison.OrdinalIgnoreCase) && checkpoints.Count < 64)
                    checkpoints.Add(new { name, file, startQpc = at, endQpc = Stopwatch.GetTimestamp(),
                        samplerStartMilliseconds = sampleAt, samplerEndMilliseconds = sampler.ElapsedSeconds * 1000, milliseconds });
            }
        }
        object Diagnostics() => new { flushPolicy = "buffered-Flush-matches-managed-packet-writer", checkCount, metricQueryCount, requestedMetricQueryIntervalMilliseconds = 100,
            maximumMetricQueryAgeMilliseconds, terminalMetricQueryAgeMilliseconds = lastMetricQuery == 0 ? (double?)null : Stopwatch.GetElapsedTime(lastMetricQuery).TotalMilliseconds,
            samplerCheckMilliseconds, processQueryMilliseconds, peakRam, peakHandles, parentHandleBudget,
            frequency = Stopwatch.Frequency, checkpoints,
            stages = stages.ToDictionary(p => p.Key, p => new { p.Value.Count, p.Value.Total, p.Value.Maximum, p.Value.Path }),
            outputOperations = sampler.OutputOperationTimings };
        void Check()
        {
            checkCount++; long at = Stopwatch.GetTimestamp();
            try { sampler.Check(); } finally { samplerCheckMilliseconds += Stopwatch.GetElapsedTime(at).TotalMilliseconds; }
            Require(clock.Elapsed.TotalSeconds < rehearsalSeconds, "Composite rehearsal process/time bound exceeded.");
            if (lastMetricQuery != 0)
            {
                double age = Stopwatch.GetElapsedTime(lastMetricQuery).TotalMilliseconds;
                maximumMetricQueryAgeMilliseconds = Math.Max(maximumMetricQueryAgeMilliseconds, age);
                if (age < 100) return;
            }
            lastMetricQuery = Stopwatch.GetTimestamp(); metricQueryCount++;
            at = Stopwatch.GetTimestamp();
            try
            {
                parent.Refresh(); peakRam = Math.Max(peakRam, parent.WorkingSet64); peakHandles = Math.Max(peakHandles, parent.HandleCount);
                Require(clock.Elapsed.TotalSeconds < rehearsalSeconds && peakRam < 768L * 1024 * 1024 && peakHandles <= parentHandleBudget,
                    "Composite rehearsal process/time bound exceeded.");
            }
            finally { processQueryMilliseconds += Stopwatch.GetElapsedTime(at).TotalMilliseconds; }
        }
        void Dummy(string file, long bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            var output = Stage("open", file, () => new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536));
            try
            {
                Stage("write", file, () => {
                    while (bytes > 0) { Check(); int count = (int)Math.Min(bytes, block.Length); output.Write(block, 0, count); bytes -= count; }
                    return true;
                });
                Stage("flush", file, () => { output.Flush(); return true; }); Check();
            }
            finally { Stage("close", file, () => { output.Dispose(); return true; }); }
        }
        int RequireWriteDenied(string file)
        {
            try { using var writer = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); }
            catch (IOException error) when ((error.HResult & 0xffff) == 32) { return error.HResult; }
            throw new IOException("Publication owner did not protect dummy output against writes.");
        }
        try
        {
            string cell = Path.Combine(plan.Root, "T01"), session = Path.Combine(cell, "buffer", "session");
            sampler.Check(currentBytes); sampler.BeginActive();
            if (publicationPath)
            {
                // Native negative controls use only tiny non-playable files.
                // A foreign pathname must fail before peak attribution/rename;
                // a later preparation failure must also leave the final absent.
                string partial = Path.Combine(cell, "control-identity.partial.mp4"), original = Path.Combine(cell, "control-identity.original.mp4"),
                    final = Path.Combine(cell, "control-identity.final.mp4");
                Dummy(partial, 4096);
                using (var normalOwner = new ReplayPublicationLease(partial))
                {
                    var request = new ReplayExportRequest(null, null, [], null, partial, null);
                    request.FreezeValidatedOutput(normalOwner);
                    lock (sampler.Gate) File.Move(partial, original);
                    Dummy(partial, 4096);
                    bool rejected = false, retainedForeign = false;
                    try { new RetainFiles(sampler, retainOutput: (_, _) => retainedForeign = true).Publish(request, final); }
                    catch (IOException error) when (error.Message == "Publication bookkeeping observed a different checked output object.") { rejected = true; }
                    Require(rejected && !retainedForeign && !normalOwner.Published && !File.Exists(final)
                        && ReplayStorage.Observe(original).Identity == normalOwner.CheckedIdentity.Identity,
                        "Foreign output pathname was attributed or published.");
                    publicationControls.Add(new { control = "foreign-native-object", rejected, retainedForeign, finalAbsent = !File.Exists(final) });
                }
                partial = Path.Combine(cell, "control-prepare.partial.mp4"); final = Path.Combine(cell, "control-prepare.final.mp4");
                Dummy(partial, 4096);
                using (var normalOwner = new ReplayPublicationLease(partial))
                {
                    var request = new ReplayExportRequest(null, null, [], null, partial, null);
                    request.FreezeValidatedOutput(normalOwner);
                    bool rejected = false;
                    try { new RetainFiles(sampler, retainOutput: (_, _) => throw new IOException("expected-rehearsal-prepare")).Publish(request, final); }
                    catch (IOException error) when (error.Message == "expected-rehearsal-prepare") { rejected = true; }
                    Require(rejected && !normalOwner.Published && !File.Exists(final) && File.Exists(partial), "Failed preparation published dummy output.");
                    int writeDenialHResult = RequireWriteDenied(partial);
                    publicationControls.Add(new { control = "failed-prepare", rejected, finalAbsent = !File.Exists(final), normalOwnerStillProtects = true, writeDenialHResult });
                }
                Check();
            }
            for (int n = 0; n < sourceFiles; n++) Dummy(Path.Combine(session, "chunk-" + n.ToString("D9") + ".ts"), 3_000_000);
            sampler.MarkCaptureStop("T01");
            var observations = new List<object>();
            foreach (string variant in new[] { "packet", "legacy" })
            {
                string partial = Path.Combine(cell, variant + ".partial.mp4"), final = Path.Combine(cell, variant + ".mp4");
                if (variant == "packet") Dummy(ReplayOutputWorkspace.BoundaryPath(partial, 0), 1_000_000);
                Dummy(partial, outputBytes);
                if (publicationPath)
                {
                    // Dummy bytes are intentionally not decodable. Exercise
                    // only the exact publication/ownership/storage call path;
                    // this simulated validation owner is not media validation.
                    var checkedOwner = Stage("simulated-normal-owner-open", partial, () => new ReplayPublicationLease(partial));
                    try
                    {
                        var request = new ReplayExportRequest(null, null, [], null, partial, null);
                        request.FreezeValidatedOutput(checkedOwner);
                        var publicationFiles = new RetainFiles(sampler, hardlinksRequired: true, retainOutput: (current, _) => {
                            var retained = new ReplayPublicationLease(current.Partial);
                            try
                            {
                                Require(retained.CheckedIdentity == current.ValidatedOutput.CheckedIdentity,
                                    "Rehearsal qualification owner changed the simulated checked object.");
                                outputOwners.Add((retained.Stream, retained.Stream.SafeFileHandle, final, retained.CheckedIdentity));
                            }
                            catch { retained.Dispose(); throw; }
                        });
                        Stage("actual-retain-files-publication", partial, () => { publicationFiles.Publish(request, final); return true; });
                    }
                    finally { Stage("simulated-normal-owner-close", final, () => { checkedOwner.Dispose(); return true; }); }
                    int writeDenialHResult = RequireWriteDenied(final);
                    publicationControls.Add(new { control = "continuous-qualification-owner", variant, normalOwnerClosed = true, extraOwnerStillProtects = true, writeDenialHResult });
                }
                else if (outputReadOwnership)
                {
                    var reader = Stage("qualification-read-owner-open", partial, () =>
                        new FileStream(partial, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete));
                    var handle = reader.SafeFileHandle;
                    try
                    {
                        var extent = ReplayStorage.Observe(partial);
                        Require(extent.Identity == Native.DirectoryIdentity(handle) && reader.Length == extent.Length,
                            "Qualification-shaped dummy output owner identity/EOF changed.");
                        outputOwners.Add((reader, handle, final, extent));
                    }
                    catch { reader.Dispose(); throw; }
                }
                if (!publicationPath)
                {
                    Stage("before-publication", partial, () => { sampler.BeforePublication(partial); return true; });
                    Stage("rename", partial, () => { File.Move(partial, final); return true; });
                }
                var peaks = sampler.OutputAndWorkspacePeak(final);
                Require(peaks.Combined >= outputBytes && peaks.Work >= (variant == "packet" ? 1_000_000 : 0)
                    && peaks.Assembly == 0 && peaks.Boundaries[0] >= (variant == "packet" ? 1_000_000 : 0),
                    "Full-size paired dummy output/work accounting lost a stage.");
                observations.Add(new { variant, extent = ReplayStorage.Observe(final), peaks.Combined, peaks.Work, peaks.Assembly, peaks.Boundaries });
            }
            foreach (var owner in outputOwners)
            {
                Check();
                sampler.RequireFreshActiveObservation();
                var before = Stage("qualification-read-owner-before-close", owner.Final, () => ReplayStorage.Observe(owner.Final));
                ownerClosures.Add(new { phase = "before", owner.Final, before });
                Require(before.Identity == owner.Extent.Identity && before.Volume == owner.Extent.Volume
                    && before.Length == owner.Extent.Length && Native.DirectoryIdentity(owner.Handle) == before.Identity,
                    "Qualification-shaped output changed across publication.");
                long closeAt = Stopwatch.GetTimestamp();
                try { Stage("qualification-read-owner-close", owner.Final, () => { owner.Reader.Dispose(); return true; }); }
                finally { ownerClosures.Add(new { phase = "close", owner.Final,
                    closeMilliseconds = Stopwatch.GetElapsedTime(closeAt).TotalMilliseconds, readerClosed = owner.Handle.IsClosed }); }
                Check();
                var after = Stage("qualification-read-owner-after-close", owner.Final, () => ReplayStorage.Observe(owner.Final));
                ownerClosures.Add(new { phase = "after", owner.Final, after });
                Require(owner.Handle.IsClosed && after.Identity == before.Identity && after.Volume == before.Volume
                    && after.Length == before.Length && after.AllocationUnit == before.AllocationUnit,
                    "Qualification-shaped output release changed identity/EOF or remained open.");
                if (publicationPath)
                {
                    Stage("qualification-owner-released-write-probe", owner.Final, () => {
                        // Open permission only: write no bytes. A prior sharing
                        // denial must disappear for this same native object.
                        using var writer = new FileStream(owner.Final, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
                        var current = ReplayStorage.ObserveInventory(writer.SafeFileHandle, after);
                        Require(current.Identity == after.Identity && current.Volume == after.Volume && current.Length == after.Length,
                            "Release write probe changed the dummy output identity/EOF.");
                        return true;
                    });
                    publicationControls.Add(new { control = "qualification-owner-release", owner.Final, writeOpenSucceeded = true, bytesWritten = 0 });
                }
                sampler.RequireFreshActiveObservation();
                Check();
            }
            Check();
            sampler.EndActive(); sampler.StopAndCheck();
            Check();
            result = () => new { result = "passed-composite-storage-rehearsal", planHash = expectedHash,
                sourceFiles, sourceBytes = checked((long)sourceFiles * 3_000_000), outputBytes = checked(2 * outputBytes), boundaryBytes = 1_000_000,
                replayMinutes = plan.ReplayMinutes, rehearsalSeconds, currentByteCeiling = currentBytes,
                observations, outputReadOwnership, publicationPath, publicationControls, ownerClosures, seconds = clock.Elapsed.TotalSeconds, sampler.MaximumActiveGap, sampler.PeakUsed, diagnostics = Diagnostics(),
                mediaChildren = 0, capture = false, empiricalModel = false,
                limits = "Non-playable full-size paired dummy writes and real native accounting only. Optional declared qualification-shaped output reader is released, reobserved and timed inside ACTIVE500ms coverage. Optional actual RetainFiles publication uses a simulated normal owner without a validator: no media-validation claim. Source-pair ownership is not modeled. No encoder, capture, audio, UI, profile or catalog. Does not predict ordinary Save, qualify codec growth or replace live training/heldouts." };
        }
        catch (Exception error)
        {
            primaryFailure = error;
        }
        finally
        {
            foreach (var owner in outputOwners)
            {
                Exception failure = null; long at = Stopwatch.GetTimestamp();
                try { owner.Reader.Dispose(); } catch (Exception error) { failure = error; }
                if (failure != null) cleanupErrors.Add(failure.ToString());
                if (!owner.Handle.IsClosed) cleanupErrors.Add("Output reader closure unconfirmed: " + owner.Final);
                cleanup.Add(new { owner.Final, closed = owner.Handle.IsClosed,
                    milliseconds = Stopwatch.GetElapsedTime(at).TotalMilliseconds, error = failure?.ToString() });
            }
            if (outputReadOwnership)
                try { Write(Path.Combine(plan.Root, "qualification-read-owner-cleanup.json"), new { outputReadOwnership, cleanup }); }
                catch (Exception error) { cleanupErrors.Add("Cleanup receipt: " + error); }
        }
        if (primaryFailure == null && cleanupErrors.Count == 0)
            try { Check(); } catch (Exception error) { primaryFailure = error; }
        if (primaryFailure != null || cleanupErrors.Count != 0)
        {
            try { Write(Path.Combine(plan.Root, "failure.json"), new { result = "failed-no-retry", error = primaryFailure?.ToString(), cleanupErrors,
                outputReadOwnership, ownerClosures, cleanup, seconds = clock.Elapsed.TotalSeconds,
                sampler.MaximumActiveGap, sampler.LastObservation, diagnostics = Diagnostics() }); }
            catch (Exception error) { Console.Error.WriteLine("Failure receipt: " + error); }
            Console.Error.WriteLine(primaryFailure?.ToString()); foreach (string error in cleanupErrors) Console.Error.WriteLine(error); return 1;
        }
        Write(Path.Combine(plan.Root, "rehearsal.json"), result());
        Console.WriteLine("Composite storage rehearsal passed: " + plan.Root); return 0;
    }
    sealed class ProtectedPairSource(CaptureSession source, Action<ReplayExportRequest> protect,
        CalibrationValidationWork.GuardContext validationContext = null, Action<ReplayExportRequest, Action> inspectPublication = null,
        Func<ReplayOutputSelection, ReplayOutputDemand> qualificationDemand = null) : IReplaySessionSource
    {
        readonly IReplaySessionSource inner = source;
        public ReplaySessionInfo ReplayInfo => inner.ReplayInfo;
        public bool Stopped => inner.Stopped;
        public bool PreserveEvidence => true;
        public bool SupportsOwnedExport => inner.SupportsOwnedExport;
        public bool SupportsStagedPreparation => inner.SupportsStagedPreparation;
        public ReplaySavePreparation BeginPreparation(ReplayRootedFiles files, ReplayInterval interval, Segment candidate, string partial,
            Action accepted, Action<ReplayProducerProof> changed, Action check, Action<ReplaySavePreparation> take)
            => inner.BeginPreparation(files, interval, candidate, partial, accepted, changed, check, take);
        public string Diagnostics => inner.Diagnostics;
        public double InputPrerollSeconds => inner.InputPrerollSeconds;
        public string OutputDemandIdentity => qualificationDemand == null ? inner.OutputDemandIdentity : ReplayReferenceModel.ExporterIdentity;
        public ReplayOutputDemand OutputDemandStatus => inner.OutputDemandStatus;
        public bool OutputProducerStopped => inner.OutputProducerStopped;
        public ReplayProducerProof ProducerProof => inner.ProducerProof;
        public List<Segment> ReadSegments() => inner.ReadSegments();
        public ReplayOutputDemand EstimateOutput(ReplayOutputSelection selection) => qualificationDemand?.Invoke(selection) ?? inner.EstimateOutput(selection);
        public ReplayOutputDemand RecheckOutputDemand(ReplayOutputSelection selection) => qualificationDemand?.Invoke(selection) ?? inner.RecheckOutputDemand(selection);
        public double SaveCutoffAt(long timestamp) => inner.SaveCutoffAt(timestamp);
        public void FreezeAt(long timestamp) => inner.FreezeAt(timestamp);
        public void StopCapture() => inner.StopCapture();
        public void CheckCaptureHealth(double end) => inner.CheckCaptureHealth(end);
        public void Export(IReadOnlyList<string> arguments, string snapshot) => inner.Export(arguments, snapshot);
        public void Export(ReplayExportRequest request, string snapshot, Action<ReplayProducerProof> changed, Action check)
        { protect(request); inner.Export(request, snapshot, changed, check); }
        public ReplayValidation ValidateOutput(ReplayExportRequest request, Action<ReplayProducerProof> changed, Action check)
        { using var scope = validationContext?.Enter(check); var result = inner.ValidateOutput(request, changed, check); inspectPublication?.Invoke(request, check); return result; }
        public ReplayValidation ValidateOutput(string path, ReplayInterval interval, Action<ReplayProducerProof> changed, Action check)
        { using var scope = validationContext?.Enter(check); return inner.ValidateOutput(path, interval, changed, check); }
        public void Dispose() => inner.Dispose();
    }
    // A new experiment boundary. Historical exact-only admissions do not opt
    // into this route and cannot produce the paired composite-policy catalog.
    internal static int CompositeParentHandleBudget(int? declared)
    {
        int limit = declared ?? 32768;
        Require(limit is 32768 or 49152, "Unknown composite collector handle budget.");
        return limit;
    }
    static int CompositeParentHandleBudget(CalibrationPlan plan)
    {
        string path = EvidenceRole(plan, "resources");
        Require(new FileInfo(path).Length <= 65536 && HashFile(path) == plan.Files[path], "Composite resources binding changed.");
        using var resources = JsonDocument.Parse(File.ReadAllText(path));
        int? declared = resources.RootElement.TryGetProperty("parentHandles", out var value) ? value.GetInt32() : null;
        return CapacityQualification(plan) && !OwnedHistoryQualification(plan) ? CapacityHandleBudget(declared) : CompositeParentHandleBudget(declared);
    }
    static void ValidateCompositeResources(CalibrationPlan p)
    {
        Require(p != null, "Composite resource proposal is missing.");
        if (CapacityQualification(p)) { ValidateCapacityResources(p); return; }
        Require(p.FreshCandidateAdmission == null, "Unknown fresh capacity admission.");
        _ = CompositeParentHandleBudget(p);
        bool resume = p.CompositeTrainingRoot != null;
        bool historyBudget = p.RetainedBudgetBytes == 30_000_000_000
            || !resume && p.RetainedBudgetBytes == 31_000_000_000;
        Require(p.AdditionalHistoricalReserveBytes >= 0 && p.CopyFallbackReserveBytes == 1_000_000_000,
            "Native qualification must explicitly retain historical reserves and one already-created source-copy fallback.");
        Require(p.AdditionalHistoricalReserveBytes == 0 || !resume && p.RetainedBudgetBytes == 31_000_000_000,
            "Additional historical reserves require the distinct fresh31GB accounting route.");
        try
        {
            long whole = checked(p.RetainedBudgetBytes + p.OutsideBuildAllowanceBytes + p.AdditionalHistoricalReserveBytes
                + p.CopyFallbackReserveBytes + 6 * p.SourceComponentBytes + 6 * p.OutputComponentBytes
                + 1_256_000_000L + 64_000_000L + 256_000_000L + 67_108_864L);
            Require(whole < p.StopBytes, "The complete prospective qualification envelope cannot fit the unchanged early ceiling.");
        }
        catch (OverflowException) { throw new IOException("Qualification conservation arithmetic overflowed."); }
        Require(p.CompositeQualification && p.Order != null && p.Order.SequenceEqual(resume ? Order.Skip(2) : Order)
            && p.ReplayExporterRecipe == ReplaySaveExport.PolicyIdentity && !p.ProductionQualification
            && p.ProductionTrainingRoot == null && p.ExactContinuationRoot == null && p.ExactHeldoutContinuationRoot == null
            && p.RetainedChildren == (resume ? 21 : 0)
            && double.IsFinite(p.RetainedElapsedSeconds)
            && (resume ? p.RetainedElapsedSeconds > 0 && p.RetainedElapsedSeconds < 5400 : p.RetainedElapsedSeconds == 0)
            && p.RetainedOfflineSeconds == (resume ? 70 : 0)
            // Spent21 + new collector1 + repeated engine controls4 +
            // four new paired cases, each at most8 roles. Below fresh61.
            && p.MaximumChildren == (resume ? 21 + 1 + 4 + 4 * 8 : 61) && p.ValidationChildren == (resume ? 12 : 18)
            && p.BudgetBytes == 64_000_000_000 && p.StopBytes == 62_500_000_000 && p.FloorBytes == 60_000_000_000
            && p.DeadlineSeconds == 5400 && p.OfflineDeadlineSeconds == 1200
            && p.SourceComponentBytes == 1_000_000_000 && p.OutputComponentBytes == 2_000_000_000
            && historyBudget
            && p.OutsideBuildAllowanceBytes == 10_000_000_000
            && p.DirectoryLimit == 16384 && p.FileLimit == 32768 && p.FreezeRetainedAllocations
            && p.ReuseCurrentMetadataHandles && !p.RetainActiveOutputHandles && !p.ReadonlyValidationPhases
            && !p.OperatorForegroundHandoff && !p.UserForegroundHandoff && !p.AutomaticFixtureHandoff,
            "Composite qualification requires its distinct finite six-acquisition/twelve-output admission.");
        Require(p.RetainedRoots is { Length: >= 1 and <= 32 }
            && p.RetainedRoots.Distinct(StringComparer.OrdinalIgnoreCase).Count() == p.RetainedRoots.Length
            && p.RetainedRoots.All(Directory.Exists), "Composite qualification retained provenance is incomplete.");
        Require(p.MetadataHandleRoots != null && p.MetadataHandleRoots.SequenceEqual(p.RetainedRoots),
            "Composite qualification requires frozen retained metadata identities.");
        Require(!resume || Path.IsPathFullyQualified(p.CompositeTrainingRoot) && p.RetainedRoots.Contains(p.CompositeTrainingRoot,
            StringComparer.OrdinalIgnoreCase) && !p.CompositeTrainingRoot.Equals(p.Root, StringComparison.OrdinalIgnoreCase),
            "A composite continuation must charge its distinct prior training root.");
    }
}
