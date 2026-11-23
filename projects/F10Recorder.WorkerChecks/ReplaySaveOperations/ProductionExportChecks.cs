using System.Diagnostics;
namespace F10Recorder;

internal static class ProductionExportChecks
{
    sealed class ClosingProbe(bool fail) : IDisposable
    {
        internal bool Called;
        public void Dispose() { Called = true; if (fail) throw new IOException("Controlled aggregate close failure."); }
    }
    static void Need(bool condition, string message) { if (!condition) throw new IOException(message); }
    sealed class Source(string directory, string output, byte[] template, ReplayMp4Expectation proof) : IReplaySessionSource
    {
        readonly ReplayExportLifetime lifetime = new();
        internal bool RetainEvidence, CreateWork;
        internal int HealthChecks;
        internal Action<ReplayExportRequest> ObserveExport;
        internal Action<ReplayExportRequest, Action> AfterValidation;
        public bool PreserveEvidence => RetainEvidence;
        public ReplaySessionInfo ReplayInfo { get; } = new(directory, Path.Combine(directory, "segments.csv"), 1, 20, 60, "h264_nvenc", ".ts", false, true);
        public bool SupportsOwnedExport => true;
        public bool Stopped => true;
        public bool OutputProducerStopped => lifetime.Stopped;
        public string Diagnostics => "Synthetic owned-source lifecycle control; no capture or codec.";
        public string OutputDemandIdentity => "TEST-owned-source-lifecycle";
        public double SaveCutoffAt(long timestamp) => 1;
        public void FreezeAt(long timestamp) { }
        public void StopCapture() { }
        public void CheckCaptureHealth(double end) { HealthChecks++; }
        public void Dispose() { }
        public ReplayOutputDemand EstimateOutput(ReplayOutputSelection selection)
        {
            var capacity = ReplayStorage.ObserveCapacity(output);
            return new(ReplayOutputEvidenceKind.SourceBound, "TEST-tiny-synthetic-only", selection.Identity, selection.ExporterIdentity,
                capacity.Volume, capacity.AllocationUnit, 262144, false, "Nonplayable fixture only", null, new(4096, 4096));
        }
        public void Export(IReadOnlyList<string> arguments, string snapshot) => throw new IOException("Request route required.");
        public void Export(ReplayExportRequest request, string snapshot, Action<ReplayProducerProof> changed, Action check)
        {
            using var activity = lifetime.Begin();
            try
            {
                Need(request.NativeFiles != null, "Ordinary owned source did not receive request-scoped operations.");
                ObserveExport?.Invoke(request);
                request.SelectRecipe(ReplaySaveExport.PacketRecipe); request.FreezeIntegrity(proof);
                if (CreateWork)
                {
                    request.NativeFiles.CreateWorkspace();
                    request.NativeFiles.CopyBoundary(request.Inputs[0].Path, ReplayOutputWorkspace.BoundaryPath(request.Partial, 0));
                }
                using (var input = request.NativeFiles.OpenInput(request.Inputs[0].Path, request.Inputs[0].Extent, true))
                {
                    byte[] bytes = new byte[4096]; input.ReadExactlyAsync(bytes).AsTask().GetAwaiter().GetResult();
                    Need(bytes.All(b => b == 0x31), "Protected source changed.");
                }
                request.Closure.BeginPacketMux();
                using (var writer = request.NativeFiles.CreateMain()) { writer.Write(template); writer.Flush(); check(); }
                using var parent = Process.GetCurrentProcess();
                request.FreezeManagedMux(new(Environment.ProcessId, parent.StartTime.ToUniversalTime(), true, true, "Completed", 1, 4096,
                    0, 0, ReplayStorage.Observe(request.Partial), null, null, null, 65536, 0, new(), new(), 0, 0));
            }
            finally { lifetime.ObserveRequest(request); }
        }
        public ReplayValidation ValidateOutput(ReplayExportRequest request, Action<ReplayProducerProof> changed, Action check)
        {
            using var activity = lifetime.Begin();
            try
            {
                var result = ReplayOutputIntegrity.Read(request, check, true);
                AfterValidation?.Invoke(request, check);
                return new(ReplayOutputIntegrity.Kind, 0, 0, result.CodedPictures, result.AudioPackets, request.Recipe,
                    request.Integrity.Fingerprint, request.ValidatedOutput.CheckedIdentity);
            }
            finally { lifetime.ObserveRequest(request); }
        }
    }

    internal static void Run(string root, Action<string, object> passed, Action bound)
    {
        Directory.CreateDirectory(root);
        var failing = new ClosingProbe(true); var healthy = new ClosingProbe(false);
        var composite = new ReplayExportInputLease(); composite.Add(failing); composite.Add(healthy);
        ReplayExportProcess.CloseRetainedLeases([composite]);
        Need(failing.Called && healthy.Called && ReplayExportProcess.RetainsFailedLeaseForChecks(composite),
            "Aggregate input-close failure escaped or released its strong owner.");
        passed("production-exited-child-composite-input-close-failure-retained", null);
        foreach (bool index in new[] { false, true })
        {
            string input = Path.Combine(root, index ? "unjoined-index.bin" : "unjoined-packet.bin");
            File.WriteAllBytes(input, new byte[16]);
            var info = new ReplaySessionInfo(root, "unused", 1, 20, 60, "h264_nvenc", ".ts", false, false);
            var request = new ReplayExportRequest(info, new(0, 1, 1),
                [new(input, 0, 1, ReplayStorage.Observe(input))], Path.Combine(root, "unused-concat"),
                Path.Combine(root, "unused-output"), new());
            var readers = index ? request.IndexReaders : request.PacketReaders;
            readers.Begin(); // Controlled unknown join; no actual background reader escapes.
            request.IndexBatch = new() { Joined = true };
            request.PacketMuxWork = new() { Joined = true };
            var original = new IOException("Expected controlled export failure.");
            bool sourcesRetained = false;
            try
            {
                ReplaySaveExport.Run(request, (_, _) => throw original, bound, ReplaySaveExport.Construction.ExactFallback);
            }
            catch (IOException error) { sourcesRetained = ReferenceEquals(error.InnerException, original); }
            try
            {
                Need(sourcesRetained && !request.Closure.SourcesClosed
                    && request.SourceProtection.Inputs.OfType<FileStream>().Single().CanRead,
                    "Exporter disposed sources before actual reader join, or trusted diagnostic Joined.");
            }
            finally
            {
                readers.Complete(true);
                ReplaySaveExport.CloseSources(request.SourceProtection.Inputs, request.Timing, bound, null, request.Closure);
            }
            Need(request.Closure.SourcesClosed, "Controlled joined source did not close.");
            passed(index ? "production-unjoined-index-retains-sources" : "production-unjoined-packet-retains-sources", null);
        }
        string output = Path.Combine(root, "external"); Directory.CreateDirectory(output);
        using (var files = new ReplayRootedFiles())
        {
            files.Root(output);
            var info = new ReplaySessionInfo(output, Path.Combine(output, "unused.csv"), 1, 20, 60, "h264_nvenc", ".ts", false, false);
            var request = new ReplayExportRequest(info, new(0, 1, 1), [], "unused", Path.Combine(output, "checked.partial.mp4"), new() { Frequency = Stopwatch.Frequency });
            request.SelectRecipe(ReplayExactExport.Recipe);
            using var native = new ReplayNativeExportScope(files, request); request.NativeFiles = native;
            native.BeginExternal(request.Partial);
            Need(!native.CanDispose, "Pending external writer permitted registry closure.");
            using (var writer = new FileStream(request.Partial, FileMode.Truncate, FileAccess.Write, FileShare.ReadWrite))
            { writer.Write(Enumerable.Repeat((byte)0x29, 4096).ToArray()); writer.Flush(); Need(native.Observe().Main.Extent.Length == 4096, "Held external observer missed growth."); }
            native.CompleteExternal(request.Partial); native.CompleteExport();
            passed("production-external-writer-sharing-growth-and-closure", null);
            using (var decoder = new ReplayDecodeLease(request.Partial))
            { decoder.BindObservation(request); decoder.VerifyUnchanged(); decoder.TransferTo(request); }
            native.BindDecodedPublication(request.ValidatedOutput);
            string final = Path.Combine(output, "checked.mp4"); files.Publish(request, final);
            Need(request.ValidatedOutput.Published && ReplayStorage.Observe(final).Identity == request.ValidatedOutput.CheckedIdentity.Identity,
                "Decoded rooted publication lost its checked object.");
            request.ValidatedOutput.Dispose(); native.Dispose();
            passed("production-decoder-lease-to-rooted-checked-publication", null);
        }
        bound();
        string unknownTree = Path.Combine(root, "unknown-retirement"); Directory.CreateDirectory(unknownTree);
        string unknown = Path.Combine(unknownTree, "foreign.bin"), known = Path.Combine(unknownTree, "chunk-000000000.ts");
        File.WriteAllText(unknown, "foreign-sentinel"); File.WriteAllText(known, "known-sentinel");
        bool rejected = false;
        try { ReplayCacheFileOperations.DeleteTree(unknownTree); } catch (IOException) { rejected = true; }
        Need(rejected && File.ReadAllText(unknown) == "foreign-sentinel" && File.ReadAllText(known) == "known-sentinel",
            "Unknown ordinary retirement member was deleted or authorized partial cleanup.");
        passed("production-retirement-rejects-unknown-ordinary-members", null);
        string session = Path.Combine(root, "buffer", "session"), saves = Path.Combine(root, "saves");
        Directory.CreateDirectory(session); Directory.CreateDirectory(saves);
        File.WriteAllBytes(Path.Combine(session, "chunk-000000000.ts"), Enumerable.Repeat((byte)0x31, 4096).ToArray());
        string manifest = Path.Combine(session, "segments.csv"); File.WriteAllText(manifest, "chunk-000000000.ts,0,1\n");
        byte[] template = global::ReplayMp4Checks.PacketMuxFixture();
        var indexed = ReplayMp4Reader.Read(new MemoryStream(template), template.Length, new ReplayInterval(0, 1, 1), 60, true);
        var video = indexed.Tracks.Single(t => t.Kind == "vide"); var audio = indexed.Tracks.Single(t => t.Kind == "soun");
        var proof = new ReplayMp4Expectation(60, 0, video.Sps, video.Pps,
            video.Samples.Select(s => new ReplayExpectedPicture(s.Idr, s.CodedSha256, s.PacketSha256)), 1024,
            audio.Samples.Select(s => new ReplayExpectedAudio(s.Duration, s.PayloadSha256)));
        using (var cache = new ReplayCache(cacheDirectory: Path.GetDirectoryName(session), cacheBudgetBytes: 1048576,
            volumeReservations: new ReplayVolumeReservations(Path.Combine(root, "authority"))))
        {
            var id = cache.RegisterSession(new Source(session, saves, template, proof));
            for (int n = 0; n < 2; n++)
            {
                // Actual atomic manifest replacement between requests must not
                // inherit a stale file owner or process-long registry counters.
                File.WriteAllText(manifest + ".tmp", "chunk-000000000.ts,0,1\n"); File.Move(manifest + ".tmp", manifest, true);
                var submission = cache.TrySave(id, Stopwatch.GetTimestamp(), saves);
                Need(submission.Accepted, "Owned-source request rejected: " + submission.Rejection);
                var result = submission.Completion.GetAwaiter().GetResult();
                Need(result.Succeeded && result.Validation.PublicationIdentityBound, "Owned-source save failed: " + result.Error);
                Need(!cache.Status.Saving && !Directory.GetDirectories(session, "save-*").Any(), "Joined save retained live registry/snapshot work.");
                bound();
            }
        }
        passed("production-request-registry-repeated-saves-and-manifest-replacement", null);
        string bridgeSession = Path.Combine(root, "bridge-buffer", "session"), bridgeSaves = Path.Combine(root, "bridge-saves");
        Directory.CreateDirectory(bridgeSession); Directory.CreateDirectory(bridgeSaves);
        File.WriteAllBytes(Path.Combine(bridgeSession, "chunk-000000000.ts"), Enumerable.Repeat((byte)0x31, 4096).ToArray());
        File.WriteAllText(Path.Combine(bridgeSession, "segments.csv"), "chunk-000000000.ts,0,1\n");
        ReplayPublicationLease retained = null; IDisposable observer = null; ReplayExportRequest bridgeRequest = null;
        object observerGate = new(); int mutations = 0; bool eligibleAtExport = false, checkedBeforePublication = false;
        try
        {
            using var cache = new ReplayCache(cacheDirectory: Path.GetDirectoryName(bridgeSession), cacheBudgetBytes: 1048576,
                volumeReservations: new ReplayVolumeReservations(Path.Combine(root, "bridge-authority")));
            cache.RetainSourceNamespaceForQualificationChecks = true;
            cache.SerializeNamespaceMutationForChecks = mutation => {
                lock (observerGate) { observer?.Dispose(); observer = null; mutations++; mutation(); }
            };
            var source = new Source(bridgeSession, bridgeSaves, template, proof) { RetainEvidence = true, CreateWork = true };
            source.ObserveExport = request => { bridgeRequest = request; eligibleAtExport = request.NativeFiles.Eligible; };
            source.AfterValidation = (request, check) => CalibrationValidationWork.Run(() => {
                lock (observerGate)
                {
                    Need(Directory.Exists(request.WorkDirectory) && File.Exists(request.Partial), "Prepublication observation lost main/work namespace.");
                    retained = new ReplayPublicationLease(request.Partial);
                    Need(retained.CheckedIdentity == request.ValidatedOutput.CheckedIdentity, "Extra checked-output owner differs.");
                    string boundary = ReplayOutputWorkspace.BoundaryPath(request.Partial, 0);
                    var pinned = ReplayStorage.OpenPinnedInventory(boundary);
                    observer = pinned;
                    Need(!pinned.IsInvalid && ReplayStorage.ObserveInventory(pinned, ReplayStorage.Observe(boundary)).Length == 4096,
                        "Bridge control did not acquire a real identity-bound no-delete workspace observer.");
                    checkedBeforePublication = true;
                }
            }, check);
            var id = cache.RegisterSession(source);
            var submission = cache.TrySave(id, Stopwatch.GetTimestamp(), bridgeSaves);
            Need(submission.Accepted, "Native qualification bridge rejected admission.");
            var result = submission.Completion.GetAwaiter().GetResult();
            Need(result.Succeeded && eligibleAtExport && checkedBeforePublication && mutations >= 2
                && observer == null && bridgeRequest.NativeFiles.Closed && !Directory.Exists(bridgeRequest.WorkDirectory)
                && Directory.GetDirectories(bridgeSession, "save-*").Length == 1
                && retained.CheckedIdentity.Identity == ReplayStorage.Observe(result.File).Identity && retained.Stream.CanRead,
                "Native bridge failed observed publication/cleanup or continuous output ownership: " + result.Error);
            bound();
        }
        finally { observer?.Dispose(); retained?.Dispose(); }
        passed("production-qualification-serialized-publication-work-cleanup-and-continuous-owner", new { mutations });
        string retainedSession = Path.Combine(root, "retained-buffer", "session"); Directory.CreateDirectory(retainedSession);
        foreach (int n in new[] { 0, 20 }) File.WriteAllBytes(Path.Combine(retainedSession, $"chunk-{n:D9}.ts"), new byte[4096]);
        File.WriteAllText(Path.Combine(retainedSession, "segments.csv"), "chunk-000000000.ts,0,1\nchunk-000000020.ts,20,21\n");
        using (var cache = new ReplayCache(cacheDirectory: Path.GetDirectoryName(retainedSession), cacheBudgetBytes: 1048576))
        {
            var source = new Source(retainedSession, bridgeSaves, template, proof) { RetainEvidence = true };
            cache.RetainSourceNamespaceForQualificationChecks = true;
            var id = cache.RegisterSession(source); cache.Maintain(id);
            Need(File.Exists(Path.Combine(retainedSession, "chunk-000000000.ts")) && source.HealthChecks > 0,
                "Explicit qualification source retention skipped health or deleted protected history.");
            cache.RetainSourceNamespaceForQualificationChecks = false; cache.Maintain(id);
            Need(!File.Exists(Path.Combine(retainedSession, "chunk-000000000.ts")) && source.HealthChecks > 1,
                "Default source pruning did not resume after qualification retention ended.");
        }
        passed("production-qualification-source-retention-preserves-health-and-default-pruning", null);
        Need(ReplayCalibrationCatalogChecks.Run(Path.Combine(root, "catalog")) == 0, "Native-v2 catalog fixtures failed.");
        passed("production-native-v2-catalog-positive-and-negative-controls", null);
        string expirySession = Path.Combine(root, "expiry-buffer", "session"), expirySaves = Path.Combine(root, "expiry-saves");
        Directory.CreateDirectory(expirySession); Directory.CreateDirectory(expirySaves);
        File.WriteAllBytes(Path.Combine(expirySession, "chunk-000000000.ts"), Enumerable.Repeat((byte)0x31, 4096).ToArray());
        File.WriteAllText(Path.Combine(expirySession, "segments.csv"), "chunk-000000000.ts,0,1\n");
        using (var cache = new ReplayCache(cacheDirectory: Path.GetDirectoryName(expirySession), cacheBudgetBytes: 1048576,
            volumeReservations: new ReplayVolumeReservations(Path.Combine(root, "expiry-authority"))))
        {
            cache.OwnedRequestCreatedForChecks = _ => Thread.Sleep(ReplayCapacityPolicy.CoverageMilliseconds + 10);
            var id = cache.RegisterSession(new Source(expirySession, expirySaves, template, proof));
            var submission = cache.TrySave(id, Stopwatch.GetTimestamp(), expirySaves);
            Need(submission.Accepted, "Expiry setup control did not reach accepted work.");
            var result = submission.Completion.GetAwaiter().GetResult();
            Need(!result.Succeeded && !Directory.GetFiles(expirySaves, "*.mp4").Any(), "Slow native setup refreshed expired evidence or published output.");
        }
        passed("production-rooted-setup-checking-time-is-fail-closed", null);
    }
}
