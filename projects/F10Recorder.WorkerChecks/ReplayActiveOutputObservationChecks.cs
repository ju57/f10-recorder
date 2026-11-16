using System.Diagnostics;

namespace F10Recorder;

// Real tiny native objects and Cache publication/retirement. No media, device,
// codec, catalog, calibrated model, capture, UI or performance claim.
internal static class ReplayActiveOutputObservationChecks
{
    static void Need(bool value, string reason) { if (!value) throw new IOException(reason); }
    static void Reject(Action action, string reason)
    {
        bool rejected = false;
        try { action(); } catch (ReplayOutputIdentityException) { rejected = true; }
        Need(rejected, reason);
    }

    sealed class DelegateFiles(ReplayFileExtent value) : ReplayFiles
    {
        internal int Calls;
        internal override ReplayFileExtent Observe(string path) { Calls++; return value; }
        internal override ReplayVolumeCapacity ObserveCapacity(string path) => new(value.Volume, value.AllocationUnit, 123456);
    }

    sealed class NativeFiles : ReplayFiles
    {
        internal ReplayCache Cache;
        internal int Publications, MainShortOpens;
        internal override ReplayFileExtent Observe(string path)
        { if (path.EndsWith(".partial.mp4", StringComparison.OrdinalIgnoreCase)) MainShortOpens++; return base.Observe(path); }
        internal override void Publish(ReplayExportRequest request, string final)
        {
            Need(!Cache.ActiveOutputOwnsResourcesForChecks && request.ValidatedOutput != null, "Publication retained an owned output observer or lost its validated owner.");
            Need(request.Timing.OutputObserverCloseCompleted.HasValue
                && request.Timing.OutputObserverCloseCompleted >= request.Timing.OutputObserverCloseStarted,
                "Observer closure was not measured before publication.");
            base.Publish(request, final); Publications++;
        }
    }

    sealed class Source(string directory, ReplayVolumeCapacity capacity) : IReplaySessionSource
    {
        public ReplaySessionInfo ReplayInfo { get; } = new(directory, Path.Combine(directory, "segments.csv"), 1, 20, 60, "controlled", ".ts", false, false);
        public bool PreserveEvidence => true;
        public bool Stopped { get; private set; }
        int active;
        internal bool AliasSelectedInput, LeaveWriterUnconfirmed, FailExport;
        FileStream heldWriter;
        internal void ConfirmProducerStopped()
        {
            var handle = heldWriter?.SafeFileHandle;
            heldWriter?.Dispose();
            Need(handle == null || handle.IsClosed, "Controlled writer closure unconfirmed.");
            heldWriter = null; Interlocked.Exchange(ref active, 0);
        }
        public bool OutputProducerStopped => Volatile.Read(ref active) == 0;
        public string OutputDemandIdentity => "NONMEDIA-active-output-owner-controls";
        public string Diagnostics => "Trusted NONMEDIA fixture; no decoder or playable output.";
        public double SaveCutoffAt(long timestamp) => 1;
        public void FreezeAt(long timestamp) { }
        public void StopCapture() => Stopped = true;
        public void CheckCaptureHealth(double end) { }
        public void Dispose() => Need(OutputProducerStopped, "Source disposed while its accepted exporter was active.");
        public ReplayOutputDemand EstimateOutput(ReplayOutputSelection selection) => new(ReplayOutputEvidenceKind.CalibratedEstimate,
            "controlled-numeric-obligation-NOT-a-storage-model", selection.Identity, selection.ExporterIdentity, capacity.Volume, capacity.AllocationUnit,
            16 * capacity.AllocationUnit, true, null);
        internal ManualResetEventSlim Entered, Release;
        public void Export(IReadOnlyList<string> args, string snapshot) => throw new IOException("Request overload required.");
        public void Export(ReplayExportRequest request, string snapshot, Action<ReplayProducerProof> changed, Action check)
        {
            Interlocked.Exchange(ref active, 1);
            FileStream writer = null;
            try
            {
                request.SelectRecipe(ReplayExactExport.Recipe);
                if (AliasSelectedInput)
                {
                    Need(Native.CreateHardLink(request.Partial, request.Inputs[0].Path, IntPtr.Zero), "Controlled main-output input alias failed.");
                    check(); throw new IOException("Input alias escaped the production rejection.");
                }
                writer = new FileStream(request.Partial, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                writer.Write(new byte[4096]); writer.Flush(); check(); Entered?.Set();
                if (LeaveWriterUnconfirmed)
                { heldWriter = writer; writer = null; throw new IOException("controlled-unconfirmed-producer"); }
                if (Release != null && !Release.Wait(TimeSpan.FromSeconds(5))) throw new IOException("Controlled export release deadline.");
                writer.Write(new byte[4096]); writer.Flush(); check();
                if (FailExport) throw new IOException("controlled-export-primary");
            }
            finally { writer?.Dispose(); if (heldWriter == null) Interlocked.Exchange(ref active, 0); }
        }
        public ReplayValidation ValidateOutput(ReplayExportRequest request, Action<ReplayProducerProof> changed, Action check)
        {
            var owner = new ReplayPublicationLease(request.Partial);
            try
            {
                request.FreezeValidatedOutput(owner);
                return new("SIMULATED-NONMEDIA-NOT-DECODED", 0, 0, ExportRecipe: request.Recipe,
                    CheckedOutput: owner.CheckedIdentity, PublicationIdentityBound: true);
            }
            catch { owner.Dispose(); throw; }
        }
    }

    internal static List<string> Run(string root)
    {
        Directory.CreateDirectory(root);
        var passed = new List<string>();
        string Cell(string name) { string path = Path.Combine(root, name); Directory.CreateDirectory(path); return path; }
        string Payload(string directory, int bytes = 4096) { string path = Path.Combine(directory, "output.partial.mp4"); File.WriteAllBytes(path, new byte[bytes]); return path; }
        var files = new ReplayFiles();
        string growing = Payload(Cell("growth"));
        using (var observer = new ReplayActiveOutputObservation(files, growing))
        using (var writer = new FileStream(growing, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            var first = observer.Observe(growing); Need(observer.OwnsHandle, "No retained metadata handle after first materialization.");
            writer.SetLength(16384); writer.Flush();
            var grown = observer.Observe(growing);
            Need(grown == ReplayStorage.RefreshInventoryAllocation(writer.SafeFileHandle, first) && grown.Length == 16384,
                "Retained observer cached allocation/EOF instead of querying the native object.");
            writer.SetLength(2048); writer.Flush();
            Need(observer.Observe(growing).Length == 2048, "Truncation hid behind a cached length.");
            passed.Add("active-output-fresh-native-growth-allocation-and-truncation");
        }
        string replaced = Payload(Cell("replacement"));
        using (var observer = new ReplayActiveOutputObservation(files, replaced))
        {
            observer.Observe(replaced); File.Move(replaced, replaced + ".old"); File.WriteAllBytes(replaced, new byte[8192]);
            Reject(() => observer.Observe(replaced), "Same-name replacement credited a retained old object.");
            Need(observer.OwnsHandle, "Failed namespace proof discarded its old observer before cleanup.");
            passed.Add("active-output-replacement-rejects-without-old-allocation-credit");
        }
        string missing = Payload(Cell("missing"));
        using (var observer = new ReplayActiveOutputObservation(files, missing))
        {
            observer.Observe(missing); File.Delete(missing);
            Reject(() => observer.Observe(missing), "Materialized output disappearance became zero allocation.");
            passed.Add("active-output-disappearance-is-uncertain-not-zero");
        }
        string pinnedDirectory = Cell("parent-pins"), pinnedOutput = Payload(pinnedDirectory);
        using (var observer = new ReplayActiveOutputObservation(files, pinnedOutput))
        {
            observer.Observe(pinnedOutput); bool denied = false;
            try { Directory.Move(pinnedDirectory, pinnedDirectory + ".moved"); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { denied = true; }
            Need(denied && Directory.Exists(pinnedDirectory), "Active output observer did not protect its directory ancestry.");
            observer.Dispose(); Directory.Move(pinnedDirectory, pinnedDirectory + ".moved"); Directory.Move(pinnedDirectory + ".moved", pinnedDirectory);
            passed.Add("active-output-parent-pins-release-before-namespace-retirement");
        }
        string owned = Payload(Cell("borrow"));
        using (var observer = new ReplayActiveOutputObservation(files, owned))
        using (var lease = new ReplayPublicationLease(owned))
        {
            observer.Observe(owned); observer.Complete(lease);
            Need(!observer.HasOwnedResources && !observer.OwnsHandle, "Owned observers survived completion.");
            Need(observer.Observe(owned) == ReplayStorage.ObserveInventory(lease.Stream.SafeFileHandle, lease.CheckedIdentity), "Post-close observation did not use fresh actual validated ownership.");
            observer.Dispose();
            Need(!lease.Stream.SafeFileHandle.IsClosed && lease.Stream.CanRead, "Disposing metadata observer closed its borrowed publication owner.");
            bool denied = false;
            try { using var writer = File.Open(owned, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); }
            catch (IOException) { denied = true; }
            Need(denied, "Borrowed validated owner lost write protection.");
            lease.Publish(owned + ".published"); Need(lease.Published, "Observer closure prevented publication of the actual checked object.");
            passed.Add("active-output-closes-before-borrow-and-never-disposes-publication-owner");
        }
        string original = Payload(Cell("wrong-borrow")), other = Payload(Cell("other-borrow"));
        using (var observer = new ReplayActiveOutputObservation(files, original))
        using (var wrong = new ReplayPublicationLease(other))
        {
            observer.Observe(original); Reject(() => observer.Complete(wrong), "Different validated object completed output observation.");
            Need(observer.OwnsHandle, "Rejected wrong-owner transition closed live metadata ownership.");
            passed.Add("active-output-different-validated-owner-rejected-before-close");
        }
        string closed = Payload(Cell("closed-borrow"));
        using (var observer = new ReplayActiveOutputObservation(files, closed))
        {
            observer.Observe(closed); var lease = new ReplayPublicationLease(closed); lease.Dispose();
            Reject(() => observer.Complete(lease), "A stored identity receipt substituted for an open validated owner.");
            Need(observer.OwnsHandle, "Rejected closed-owner transition disposed metadata ownership first.");
            passed.Add("active-output-closed-validated-owner-rejected-before-close");
        }
        var extent = ReplayStorage.Observe(growing); var delegated = new DelegateFiles(extent);
        using (var observer = new ReplayActiveOutputObservation(delegated, owned))
        {
            Need(observer.Observe(growing) == extent && delegated.Calls == 1 && observer.ObserveCapacity(root).AvailableBytes == 123456,
                "Observer bypassed custom unrelated-file or capacity behavior.");
            passed.Add("active-output-other-claims-and-capacity-use-original-provider");
        }
        foreach (bool retiring in new[] { false, true })
        {
            string cell = Cell(retiring ? "retirement" : "overlap"), session = Path.Combine(cell, "buffer", "session"), output = Path.Combine(cell, "clips");
            Directory.CreateDirectory(session); Directory.CreateDirectory(output);
            File.WriteAllBytes(Path.Combine(session, "chunk-000000000.ts"), new byte[4096]);
            File.WriteAllText(Path.Combine(session, "segments.csv"), "chunk-000000000.ts,0,1\n");
            var source = new Source(session, ReplayStorage.ObserveCapacity(output)); var native = new NativeFiles();
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            using var cache = new ReplayCache(native, cacheDirectory: Path.Combine(cell, "buffer"),
                volumeReservations: new ReplayVolumeReservations(Path.Combine(cell, "authority")), fileMode: ReplayFileMode.NativeOutputForChecks);
            native.Cache = cache; var id = cache.RegisterSession(source);
            if (retiring) { source.Entered = entered; source.Release = release; }
            var save = cache.TrySave(id, Stopwatch.GetTimestamp(), output); Need(save.Accepted, save.Rejection);
            Task retirement = null;
            try
            {
                if (retiring)
                {
                    Need(entered.Wait(TimeSpan.FromSeconds(5)) && cache.ActiveOutputOwnsResourcesForChecks, "Accepted export never acquired its output observer.");
                    retirement = cache.CloseAsync();
                    Need(!retirement.IsCompleted && cache.ActiveOutputOwnsResourcesForChecks, "Worker retirement disposed an active accepted exporter observer.");
                }
            }
            finally { release.Set(); }
            Need(save.Completion.Wait(TimeSpan.FromSeconds(10)), "Native controlled save deadline.");
            var result = save.Completion.GetAwaiter().GetResult(); Need(result.Succeeded, result.Error);
            Need(result.Timing.OutputObserverCloseCompleted < result.Timing.Published, "Saved preceded output observer closure.");
            if (retirement != null) retirement.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            else
            {
                var again = cache.TrySave(id, Stopwatch.GetTimestamp(), output); Need(again.Accepted, again.Rejection);
                var second = again.Completion.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                Need(second.Succeeded && second.File != result.File && File.Exists(result.File), "Repeated save consumed history or previous output ownership.");
            }
            Need(native.MainShortOpens == 0 && !cache.ActiveOutputOwnsResourcesForChecks, "Main output reopened under ledger checks or retained owned observers.");
            passed.Add(retiring ? "active-output-worker-retirement-awaits-accepted-writer" : "active-output-real-cache-ledger-close-before-publication-and-overlap");
        }
        foreach (string fault in new[] { "alias", "producer", "cleanup", "close-BeforeHandle", "close-AfterHandle", "close-BeforeParents", "close-AfterParents" })
        {
            string cell = Cell("failure-" + fault), session = Path.Combine(cell, "buffer", "session"), output = Path.Combine(cell, "clips");
            Directory.CreateDirectory(session); Directory.CreateDirectory(output);
            File.WriteAllBytes(Path.Combine(session, "chunk-000000000.ts"), new byte[4096]);
            File.WriteAllText(Path.Combine(session, "segments.csv"), "chunk-000000000.ts,0,1\n");
            var source = new Source(session, ReplayStorage.ObserveCapacity(output))
            { AliasSelectedInput = fault == "alias", LeaveWriterUnconfirmed = fault == "producer", FailExport = fault == "cleanup" };
            var native = new NativeFiles();
            var diagnostics = new List<string>();
            using var cache = new ReplayCache(native, cacheDirectory: Path.Combine(cell, "buffer"),
                volumeReservations: new ReplayVolumeReservations(Path.Combine(cell, "authority")), fileMode: ReplayFileMode.NativeOutputForChecks,
                diagnostic: diagnostics.Add);
            native.Cache = cache; bool injected = false; SafeHandleEvidence evidence = new();
            ReplayActiveOutputObservation captured = null;
            if (fault == "cleanup") cache.ActiveOutputCreatedForChecks = observer =>
            {
                captured = observer;
                observer.ClosingForChecks = phase => { if (phase == ReplayActiveOutputObservation.ClosePhase.BeforeHandle) throw new IOException("controlled-cleanup-secondary"); };
            };
            if (fault.StartsWith("close-", StringComparison.Ordinal))
            {
                var phase = Enum.Parse<ReplayActiveOutputObservation.ClosePhase>(fault[6..]);
                cache.ActiveOutputCreatedForChecks = observer => observer.ClosingForChecks = current =>
                {
                    if (injected || current != phase) return;
                    injected = true; evidence.Handle = observer.OwnedHandleForChecks;
                    evidence.ClosedAtFault = evidence.Handle?.IsClosed;
                    throw new IOException("controlled-" + fault);
                };
            }
            var id = cache.RegisterSession(source); var save = cache.TrySave(id, Stopwatch.GetTimestamp(), output);
            Need(save.Accepted, save.Rejection);
            var result = save.Completion.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            try
            {
                Need(!result.Succeeded && native.Publications == 0 && result.Timing.Published == null && Directory.GetFiles(output, "Replay-*.mp4").All(p => p.EndsWith(".partial.mp4")),
                    "An output observer/producer failure published a dummy output.");
                if (fault == "producer")
                {
                    Need(cache.ActiveOutputOwnsResourcesForChecks && !source.OutputProducerStopped, "Unconfirmed accepted writer lost observer ownership.");
                    Need(!cache.TrySave(id, Stopwatch.GetTimestamp(), output).Accepted, "New save displaced uncertain accepted ownership.");
                    Need(result.Error.Contains("controlled-unconfirmed-producer"), "Cleanup replaced the original producer failure.");
                }
                else if (fault == "cleanup")
                {
                    Need(result.Error.Contains("controlled-export-primary") && diagnostics.Any(m => m.Contains("cleanup failed"))
                        && cache.ActiveOutputOwnsResourcesForChecks && captured.OwnsHandle,
                        "Cleanup failure masked the primary export failure or lost actual observer ownership.");
                    Need(!cache.TrySave(id, Stopwatch.GetTimestamp(), output).Accepted, "New save displaced a failed-close observer.");
                }
                else if (fault == "alias") Need(result.Error.Contains("aliases retained input"), "Protected-input alias rejection changed: " + result.Error);
                else
                {
                    Need(injected && result.Error.Contains("observer closure"), "Close failure did not reject before publication.");
                    Need(diagnostics.Any(m => m.Contains("controlled-" + fault)), "Close error provenance lost its original cause.");
                    if (fault == "close-BeforeHandle") Need(evidence.ClosedAtFault == false, "Before-close injection had no genuinely open native handle.");
                    if (fault == "close-AfterHandle") Need(evidence.ClosedAtFault == true, "After-close injection preceded actual native handle disposal.");
                    Need(evidence.Handle == null || evidence.Handle.IsClosed, "Cleanup retained a confirmed stopped observer's actual native handle.");
                }
            }
            finally { if (captured != null) captured.ClosingForChecks = null; source.ConfirmProducerStopped(); }
            cache.CloseAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            Need(!cache.ActiveOutputOwnsResourcesForChecks, "Confirmed producer teardown kept observer-owned resources.");
            using (var ledger = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(Path.Combine(cell, "authority", "replay-volume-reservations.json"))))
                Need(ledger.RootElement.GetProperty("Owners").GetArrayLength() == 0,
                    "Confirmed producer/observer shutdown leaked the durable storage reservation.");
            passed.Add("active-output-failure-no-publication-" + fault);
        }
        return passed;
    }

    sealed class SafeHandleEvidence
    {
        internal Microsoft.Win32.SafeHandles.SafeFileHandle Handle;
        internal bool? ClosedAtFault;
    }
}
