using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace F10Recorder;

// Independent behavioral fixtures. No playable media or native producer exists.
internal static class ReplayOutputValidationChecks
{
    static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    static async Task<ReplaySaveResult> Finish(SaveSubmission save)
    {
        Require(save.Accepted, "Expected accepted request: " + save.Rejection);
        return await save.Completion.WaitAsync(Deadline);
    }

    sealed class Source : IReplaySessionSource
    {
        public ReplaySessionInfo ReplayInfo { get; }
        public bool Stopped { get; private set; }
        public string Diagnostics => "Independent nonmedia source";
        public string OutputDemandIdentity => Exporter;
        public bool OutputProducerStopped => ProducerAbsent;
        public double InputPrerollSeconds => Preroll;
        public ReplayValidation ValidateOutput(string path, ReplayInterval interval, Action<ReplayProducerProof> changed, Action check)
        {
            ValidationAction?.Invoke();
            return new("ControlledNonmedia", (long)Math.Round(interval.Seconds * ReplayInfo.FramesPerSecond), 0);
        }
        public ReplayValidation ValidateOutput(ReplayExportRequest request, Action<ReplayProducerProof> changed, Action check)
            => RequestValidation == null ? ValidateOutput(request.Partial, request.Interval, changed, check) : RequestValidation(request, check);
        internal bool ProducerAbsent = true;
        internal double Preroll;
        internal string Exporter = "independent-exporter-v1";
        internal int Estimates, Exports;
        internal Action<IReadOnlyList<string>, string> ExportAction;
        internal Action<ReplayExportRequest, Action> CompositeExport;
        internal Action<ReplayExportRequest, Action<ReplayProducerProof>, Action> ProducerExport;
        internal Action ValidationAction;
        internal Func<ReplayExportRequest, Action, ReplayValidation> RequestValidation;
        internal Func<ReplayOutputSelection, int, ReplayOutputDemand> Estimate;
        internal Source(string directory)
        {
            Directory.CreateDirectory(directory);
            ReplayInfo = new(directory, Path.Combine(directory, "segments.csv"), 60, 5, 60, "h264_nvenc", ".ts", false, false);
            Segments(1);
        }
        internal string Chunk(int n) => Path.Combine(ReplayInfo.Directory, "chunk-" + n.ToString("D9") + ".ts");
        internal void Segments(int count)
        {
            var rows = new List<string>();
            for (int i = 0; i < count; i++)
            {
                File.WriteAllBytes(Chunk(i), new byte[2048]);
                rows.Add(Path.GetFileName(Chunk(i)) + "," + i.ToString(CultureInfo.InvariantCulture) + "," + (i + 1).ToString(CultureInfo.InvariantCulture));
            }
            File.WriteAllText(ReplayInfo.Manifest, string.Join("\n", rows) + "\n");
        }
        public ReplayOutputDemand EstimateOutput(ReplayOutputSelection selection) => Estimate(selection, ++Estimates);
        public double SaveCutoffAt(long triggerTimestamp) => triggerTimestamp / 1000.0;
        public void FreezeAt(long timestamp) { }
        public void StopCapture() => Stopped = true;
        public void CheckCaptureHealth(double completedEnd) { }
        public void Export(IReadOnlyList<string> arguments, string snapshot)
        {
            Exports++;
            ExportAction?.Invoke(arguments, snapshot);
            if (!File.Exists(arguments[^1])) File.WriteAllBytes(arguments[^1], new byte[2048]);
        }
        public void Export(ReplayExportRequest request, string snapshot, Action<ReplayProducerProof> changed, Action check)
        {
            check?.Invoke();
            if (ProducerExport != null) { Exports++; ProducerExport(request, changed, check); }
            else if (CompositeExport == null) Export(request.LegacyArguments, snapshot);
            else { Exports++; CompositeExport(request, check); }
            check?.Invoke();
        }
        public void Dispose() { }
    }

    sealed class NativeRetentionFiles : ReplayFiles
    {
        internal override ReplayNamespaceIdentity ObserveNamespace(string path, ReplayFileExtent enumerated)
            => new(enumerated.Volume, enumerated.Identity);
    }

    sealed class Files : ReplayFiles
    {
        internal readonly string Root, Destination;
        internal bool SameVolume, FailCleanup;
        internal int Copies, Publications;
        internal long CacheFree = 1_000_000, OutputFree = 1_000_000;
        internal string OutputVolume = "volume-B";
        internal string OutputIdentitySuffix = "";
        internal long OutputUnit = 4096;
        internal Action<string> Copied;
        internal Action<string> BeforeCapacity;
        internal Action<ReplayExportRequest, string> BeforeRequestPublication;
        internal readonly ConcurrentQueue<string> Observations = new();
        internal Files(string root, string destination) { Root = root; Destination = destination; }
        bool IsOutput(string path) => Path.GetFullPath(path).StartsWith(Destination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, Destination, StringComparison.OrdinalIgnoreCase);
        bool IsSnapshot(string path) => path.Contains(Path.DirectorySeparatorChar + "save-", StringComparison.OrdinalIgnoreCase);
        long Allocated(string path) => ((new FileInfo(path).Length + 4095) / 4096) * 4096;
        internal override ReplayFileExtent Observe(string path)
        {
            Observations.Enqueue("extent:" + path);
            return new(IsOutput(path) && !SameVolume ? OutputVolume : "volume-A", Path.GetFullPath(path) + (IsOutput(path) ? OutputIdentitySuffix : ""), Allocated(path), new FileInfo(path).Length,
                IsOutput(path) ? OutputUnit : 4096);
        }
        internal override ReplayFileExtent ObserveInventory(string path, ReplayFileExtent enumerated) => Observe(path);
        internal override ReplayVolumeCapacity ObserveCapacity(string directory)
        {
            Volatile.Read(ref BeforeCapacity)?.Invoke(directory);
            Observations.Enqueue("capacity:" + directory);
            bool output = IsOutput(directory);
            long writtenCache = Directory.GetFiles(Root, "*", SearchOption.AllDirectories).Where(IsSnapshot).Sum(Allocated);
            long writtenOutput = Directory.GetFiles(Destination, "*", SearchOption.AllDirectories)
                .Where(p => p.Contains(".partial.mp4", StringComparison.OrdinalIgnoreCase)).Sum(Allocated);
            var observed = new ReplayVolumeCapacity(output && !SameVolume ? OutputVolume : "volume-A", output ? OutputUnit : 4096,
                SameVolume ? CacheFree - writtenCache - writtenOutput : output ? OutputFree - writtenOutput : CacheFree - writtenCache);
            return observed;
        }
        internal override bool TryHardLink(string copy, string original) => false;
        internal override void Copy(string original, string copy) { base.Copy(original, copy); Copies++; Copied?.Invoke(copy); }
        internal override void Publish(string partial, string final) { base.Publish(partial, final); Publications++; }
        internal override void Publish(ReplayExportRequest request, string final)
        { BeforeRequestPublication?.Invoke(request, final); base.Publish(request, final); if (request.ValidatedOutput != null) Publications++; }
        internal override void DeleteDirectory(string path)
        {
            if (FailCleanup) throw new IOException("Independent cleanup failure");
            base.DeleteDirectory(path);
        }
        internal override void DeleteFile(string path)
        {
            if (FailCleanup) throw new IOException("Independent cleanup failure");
            base.DeleteFile(path);
        }
    }

    sealed class Fixture : IDisposable
    {
        internal readonly string Destination, Existing;
        internal readonly Source Source;
        internal readonly Files Files;
        internal readonly ReplayCache Cache;
        internal readonly ReplaySessionId Id;
        internal readonly ReplayVolumeReservations Reservations;
        internal readonly ConcurrentQueue<string> Diagnostics = new();
        internal Fixture(string root, Func<Task> wait = null, bool sharedLedger = false)
        {
            root = Path.GetFullPath(root);
            Destination = Path.Combine(root, "clips"); Directory.CreateDirectory(Destination);
            Existing = Path.Combine(Destination, "existing.mp4"); File.WriteAllText(Existing, "independent-preservation-sentinel");
            Files = new(root, Destination);
            Source = new(Path.Combine(root, "buffer", "session"));
            Source.Estimate = (s, _) => Demand(s);
            Reservations = sharedLedger ? new ReplayVolumeReservations(Path.Combine(root, "private-claims")) : null;
            Cache = new(Files, waitForSegment: wait, diagnostic: Diagnostics.Enqueue, cacheDirectory: Path.Combine(root, "buffer"), volumeReservations: Reservations);
            Id = Cache.RegisterSession(Source);
        }
        internal ReplayOutputDemand Demand(ReplayOutputSelection s, long peak = 8192) => new(ReplayOutputEvidenceKind.CalibratedEstimate,
            "independent-fixture-r1", s.Identity, s.ExporterIdentity, Files.SameVolume ? "volume-A" : "volume-B", 4096, peak, true, null,
            s.Closed ? null : 12288);
        internal SaveSubmission Save(long end = 1000) => Cache.TrySave(Id, end, Destination);
        internal void Preserved()
        {
            Require(File.ReadAllText(Existing) == "independent-preservation-sentinel", "Pre-existing saved file changed.");
            Require(File.ReadAllBytes(Source.Chunk(0)).SequenceEqual(new byte[2048]), "Protected original changed or disappeared.");
        }
        public void Dispose() { Files.FailCleanup = false; Source.ProducerAbsent = true; Cache.Dispose(); }
    }

    internal static int Run(string root) => RunAsync(Path.GetFullPath(root)).GetAwaiter().GetResult();
    internal static int RunCapacity(string root) => RunCapacityAsync(Path.GetFullPath(root)).GetAwaiter().GetResult();
    static async Task<int> RunCapacityAsync(string root)
    {
        Require(!Directory.Exists(root) && !File.Exists(root), "Capacity export checks need a fresh private root.");
        Directory.CreateDirectory(root); int failures = 0; var rows = new List<object>();
        async Task Check(string name, Func<string, Task> body)
        {
            try { await body(Path.Combine(root, name)); rows.Add(new { name, passed = true }); }
            catch (Exception error) { failures++; rows.Add(new { name, passed = false, error = error.ToString() }); }
        }
        foreach (bool blocked in new[] { false, true })
        await Check(blocked ? "blocked-export-capacity-retains-claims-and-stops" : "609ms-export-capacity-delay-publishes", async path =>
        {
            using var f = new Fixture(path, sharedLedger: true);
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); using var queryExited = new ManualResetEventSlim();
            using var producerStopped = new ManualResetEventSlim();
            f.Source.CompositeExport = (request, check) =>
            {
                f.Source.ProducerAbsent = false;
                try
                {
                    File.WriteAllBytes(request.Partial, new byte[2048]);
                    f.Files.BeforeCapacity = _ =>
                    {
                        entered.Set();
                        try { Require(release.Wait(5000), "Controlled export capacity release timed out."); }
                        finally { queryExited.Set(); }
                    };
                    var elapsed = System.Diagnostics.Stopwatch.StartNew();
                    while (elapsed.ElapsedMilliseconds < (blocked ? 4000 : 1200)) { check(); Thread.Sleep(5); }
                }
                finally { f.Source.ProducerAbsent = true; producerStopped.Set(); }
            };
            var save = f.Save(); Require(save.Accepted, "Controlled capacity save was not accepted: " + save.Rejection);
            try
            {
                Require(entered.Wait(1500), "Periodic export capacity query did not start.");
                if (!blocked)
                {
                    await Task.Delay(609); f.Files.BeforeCapacity = null; release.Set();
                    var result = await save.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                    Require(result.Succeeded && File.Exists(result.File) && f.Files.Publications == 1,
                        "A covered609ms query must preserve successful Cache publication: " + result.Error);
                }
                else
                {
                    var stopped = f.Cache.StopAcquisitionAsync(f.Id);
                    await stopped.WaitAsync(TimeSpan.FromMilliseconds(500));
                    Require(f.Source.Stopped, "Blocked capacity must not hold the Cache lock needed to stop acquisition.");
                    var result = await save.Completion.WaitAsync(TimeSpan.FromSeconds(4));
                    Require(!result.Succeeded && producerStopped.IsSet && f.Files.Publications == 0,
                        "A prolonged monitoring outage must fail unpublished output and stop its producer.");
                    string ledger = File.ReadAllText(Path.Combine(path, "private-claims", "replay-volume-reservations.json"));
                    Require(ledger.Contains("partial.mp4", StringComparison.Ordinal), "Pending query closure must retain the accepted output claim.");
                    f.Preserved();
                }
            }
            finally
            {
                f.Files.BeforeCapacity = null; release.Set();
                Require(queryExited.Wait(1000), "Controlled export capacity query did not leave.");
                await save.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            }
        });
        await Check("blocked-export-seal-does-not-own-stop-lock", async path =>
        {
            using var f = new Fixture(path, sharedLedger: true);
            using var job = new Native.ProcessJob(recoverable: true); // Empty job; no assigned or started process.
            var proof = ReplayProducerProof.Current(job.Name, true);
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            Task seal = null;
            f.Reservations.BeforeSetProducerForChecks = () => { entered.Set(); Require(release.Wait(5000), "Controlled export seal was not released."); };
            f.Source.ProducerExport = (request, changed, check) =>
            {
                f.Source.ProducerAbsent = false;
                try
                {
                    File.WriteAllBytes(request.Partial, new byte[2048]);
                    seal = Task.Run(() => changed(proof));
                    while (!seal.IsCompleted) { check(); Thread.Sleep(5); }
                    seal.GetAwaiter().GetResult();
                }
                finally { f.Source.ProducerAbsent = true; }
            };
            var save = f.Save();
            try
            {
                Require(save.Accepted && entered.Wait(1500), "Export producer seal did not enter.");
                await f.Cache.StopAcquisitionAsync(f.Id).WaitAsync(TimeSpan.FromMilliseconds(500));
                Require(f.Source.Stopped, "Blocked seal must not hold the Cache stop lock.");
                var result = await save.Completion.WaitAsync(TimeSpan.FromSeconds(4));
                Require(!result.Succeeded && f.Files.Publications == 0, "Unknown producer sealing cannot publish Saved.");
                Require(File.ReadAllText(Path.Combine(path, "private-claims", "replay-volume-reservations.json")).Contains("partial.mp4", StringComparison.Ordinal),
                    "Pending producer seal must retain its accepted claim.");
                f.Preserved();
            }
            finally
            {
                f.Reservations.BeforeSetProducerForChecks = null; release.Set();
                if (seal != null) try { await seal.WaitAsync(TimeSpan.FromSeconds(1)); } catch (IOException) { }
                await save.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            }
        });
        foreach (string fault in new[] { "write", "finalization", "cleanup-after-publication" })
        await Check(fault, async path =>
        {
            using var f = new Fixture(path, sharedLedger: true);
            if (fault == "write") f.Source.CompositeExport = (request, _) =>
            { File.WriteAllBytes(request.Partial, new byte[1024]); throw new IOException("Controlled real write completion failure."); };
            else if (fault == "finalization") f.Source.ValidationAction = () => throw new IOException("Controlled finalization failure.");
            else f.Files.BeforeRequestPublication = (_, _) => f.Files.FailCleanup = true;
            var result = await Finish(f.Save());
            Require(result.Succeeded == (fault == "cleanup-after-publication"), "Saved must reflect checked publication, independently of write/finalization or later cleanup failure.");
            Require(f.Files.Publications == (result.Succeeded ? 1 : 0), "A failed unpublished output emitted publication.");
            f.Preserved();
        });
        File.WriteAllText(Path.Combine(root, "capacity-export-checks.json"), JsonSerializer.Serialize(new { failures, checks = rows }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Capacity export checks: {rows.Count - failures}/{rows.Count} passed."); return failures == 0 ? 0 : 1;
    }
    sealed class CloseProbe(Action close) : IDisposable
    {
        public void Dispose() => close();
    }
    static async Task<int> RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        var results = new List<object>();
        int failed = 0;
        async Task Check(string name, Func<string, Task> action)
        {
            try { await action(Path.Combine(root, name)); results.Add(new { name, result = "passed" }); }
            catch (Exception ex) { failed++; results.Add(new { name, result = "failed", error = ex.ToString() }); }
        }
        foreach (bool originalFailure in new[] { false, true })
            await Check("source-cleanup-closes-all-and-preserves-" + (originalFailure ? "original" : "guard") + "-failure", path =>
            {
                var primary = new IOException("Original construction failure.");
                var guard = new IOException("Sticky guard failure.");
                int closed = 0, checks = 0; var timing = new ReplaySaveTiming();
                IDisposable[] sources = [new CloseProbe(() => closed++), new CloseProbe(() =>
                    { closed++; throw new IOException("Injected close failure."); }), new CloseProbe(() => closed++)];
                Exception observed = null;
                try
                {
                    try { if (originalFailure) throw primary; }
                    finally { ReplaySaveExport.CloseSources(sources, timing, () => { checks++; throw guard; }, originalFailure ? primary : null); }
                }
                catch (Exception error) { observed = error; }
                Require(closed == 3 && checks >= 2 && timing.SourceCloseCompleted >= timing.SourceCloseStarted && timing.SourceHandlesClosed == false,
                    "A checkpoint or failed close skipped a remaining source or final handoff check.");
                Require(originalFailure ? ReferenceEquals(observed, primary) : observed is IOException && ReferenceEquals(observed.InnerException, guard),
                    "Cleanup hid the original failure or discarded the first guard failure.");
                Require(timing.SourceCloseError.Contains(guard.Message, StringComparison.Ordinal) && timing.SourceCloseError.Length <= 1024,
                    "Cleanup failure evidence was lost or unbounded.");
                return Task.CompletedTask;
            });
        foreach (bool guardFails in new[] { false, true })
            await Check("source-cleanup-real-handles-" + (guardFails ? "guard-and-close" : "close-only") + "-failure", path =>
            {
                Directory.CreateDirectory(path);
                var streams = Enumerable.Range(0, 3).Select(n => new FileStream(Path.Combine(path, n + ".fixture"),
                    FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read)).ToArray();
                var handles = streams.Select(s => s.SafeFileHandle).ToArray();
                var closeFailure = new IOException("Injected error after actual handle closure.");
                var guardFailure = new IOException("Injected real-handle guard error.");
                IDisposable[] owners = streams.Select((s, n) => (IDisposable)new CloseProbe(() =>
                    { s.Dispose(); if (n == 1) throw closeFailure; })).ToArray();
                Exception observed = null; var timing = new ReplaySaveTiming(); int checkpoints = 0;
                try
                {
                    ReplaySaveExport.CloseSources(owners, timing, () => { checkpoints++; if (guardFails) throw guardFailure; }, null);
                }
                catch (Exception error) { observed = error; }
                try
                {
                    Require(observed is IOException && ReferenceEquals(observed.InnerException, guardFails ? guardFailure : closeFailure),
                        "Real-handle cleanup lost its first failure.");
                    Require(checkpoints >= 2 && handles.All(h => h.IsClosed) && timing.SourceHandlesClosed == false,
                        "A failed cleanup leaked a native source handle or falsely confirmed an exceptional close.");
                    foreach (var stream in streams)
                    {
                        using var exclusive = new FileStream(stream.Name, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                        Require(exclusive.Length == 0, "Cleanup changed a source fixture.");
                    }
                }
                finally { foreach (var stream in streams) stream.Dispose(); }
                return Task.CompletedTask;
            });
        await Check("managed-export-activity-outlives-stopped-child", path =>
        {
            var lifetime = new ReplayExportLifetime();
            Require(lifetime.Stopped, "Idle source begins with an unknown producer.");
            using (lifetime.Begin())
            {
                lifetime.ChildStopped(false); Require(!lifetime.Stopped, "Live child was reported absent.");
                lifetime.ChildStopped(true); Require(!lifetime.Stopped, "Stopped head child hid remaining parent-owned export work.");
                bool rejected = false; try { lifetime.Begin().Dispose(); } catch (IOException) { rejected = true; }
                Require(rejected && !lifetime.Stopped, "Concurrent entry released the existing owner's activity.");
                lifetime.ObserveManaged(true, true, true);
            }
            Require(lifetime.Stopped, "Confirmed joined/closed zero-child completion did not release activity.");
            using (lifetime.Begin()) { lifetime.ObserveManaged(false, false, true); }
            Require(lifetime.Stopped, "A request with no managed writer acquired phantom uncertainty.");
            return Task.CompletedTask;
        });
        foreach (string uncertainty in new[] { "child", "writer", "readers" })
            await Check("producer-uncertainty-survives-new-request-" + uncertainty, path =>
            {
                var lifetime = new ReplayExportLifetime();
                using (lifetime.Begin())
                {
                    if (uncertainty == "child") lifetime.ChildStopped(false);
                    else lifetime.ObserveManaged(uncertainty == "writer", false, uncertainty != "readers");
                }
                Require(!lifetime.Stopped, "Unconfirmed producer/reader closure was reported stopped.");
                lifetime.ObserveManaged(true, true, true); // A later receipt cannot erase uncertainty.
                for (int n = 0; n < 2; n++)
                {
                    bool rejected = false; try { lifetime.Begin().Dispose(); } catch (IOException) { rejected = true; }
                    Require(rejected && !lifetime.Stopped, "Receipt reset/new request erased an unknown producer or stranded owner.");
                }
                return Task.CompletedTask;
            });
        await Check("source-close-guard-failure-keeps-actual-handle-proof", path =>
        {
            Directory.CreateDirectory(path); var timing = new ReplaySaveTiming(); var error = new IOException("Guard lost, handles still closed.");
            var stream = new FileStream(Path.Combine(path, "source.fixture"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
            var handle = stream.SafeFileHandle;
            try { ReplaySaveExport.CloseSources([stream], timing, () => throw error, null); }
            catch (IOException observed) { Require(ReferenceEquals(observed.InnerException, error), "Cleanup replaced its guard error."); }
            finally { stream.Dispose(); }
            Require(timing.SourceHandlesClosed == true && handle.IsClosed && timing.SourceCloseError != null,
                "Guard-only error was confused with actual source-handle closure uncertainty.");
            return Task.CompletedTask;
        });
        foreach (string defect in new[] { "none", "source-close", "source-close-incomplete", "reader-join", "writer-close", "missing-receipt" })
            await Check("request-derived-producer-closure-" + defect, path =>
            {
                var timing = new ReplaySaveTiming { ExportRecipe = ReplaySaveExport.PacketRecipe, MuxStarted = 1,
                    SourceCloseStarted = 1, SourceCloseCompleted = 2, SourceHandlesClosed = defect != "source-close" };
                if (defect == "source-close-incomplete") timing.SourceCloseCompleted = null;
                var request = new ReplayExportRequest(new(path, "", 1, 20, 60, "h264_nvenc", ".ts", false, false), new(0, 1, 1), [], "unused", "unused", timing);
                request.Closure.BeginPacketMux();
                request.Closure.BeginSourceClose();
                if (defect != "source-close-incomplete") request.Closure.CompleteSourceClose(defect != "source-close");
                request.IndexBatch = new() { Workers = 4, Joined = defect != "reader-join" };
                if (defect != "missing-receipt") request.FreezeManagedMux(new(Environment.ProcessId, DateTime.UtcNow, defect != "writer-close", true,
                    "Completed", 1, 1, 0, 0, new("fixture-volume", "fixture-id", 4096, 1024, 4096), null, null, null, 0, 0, new(), new(), 0, 0));
                var lifetime = new ReplayExportLifetime();
                using (lifetime.Begin()) { lifetime.ObserveRequest(request); }
                Require(lifetime.Stopped == (defect == "none"), "Actual request receipts lost source/writer/reader uncertainty.");
                return Task.CompletedTask;
            });
        await Check("integrity-initial-guard-error-keeps-timed-evidence", path =>
        {
            var request = new ReplayExportRequest(new(path, "", 1, 20, 60, "h264_nvenc", ".ts", false, false),
                new(0, 1, 1), [], "unused", Path.Combine(path, "absent.partial.mp4"), new());
            request.SelectRecipe(ReplaySaveExport.Recipe);
            request.Timing.ExportRecipe = null; // The real gate still reaches its guard with diagnostic data omitted.
            string digest = new('A', 64);
            request.FreezeIntegrity(new ReplayMp4Expectation(60, 0, new byte[] { 0x67 }, new byte[] { 0x68 },
                Enumerable.Range(0, 60).Select(n => new ReplayExpectedPicture(n == 0, digest, digest))));
            var injected = new IOException("Initial guard failure before any output open."); Exception observed = null;
            try { ReplayOutputIntegrity.Read(request, () => throw injected); } catch (Exception error) { observed = error; }
            var timing = request.IntegrityGateTiming;
            Require(ReferenceEquals(observed, injected) && timing.Attempts == 1 && timing.InitialGuardMilliseconds > 0
                && timing.TotalMilliseconds >= timing.InitialGuardMilliseconds && timing.LeaseMilliseconds == null && timing.ReaderMilliseconds == null
                && timing.LeaseTransfers == 0 && timing.LeaseDisposals == 0,
                "Diagnostic timing hid the original guard error or claimed an unrun stage.");
            using var recorded = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(new { gate = timing }));
            var gate = recorded.RootElement.GetProperty("gate");
            Require(gate.GetProperty("Attempts").GetInt32() == 1 && gate.GetProperty("InitialGuardMilliseconds").GetDouble() > 0
                && gate.GetProperty("ReaderMilliseconds").ValueKind == JsonValueKind.Null && gate.GetProperty("LeaseOpen").TryGetProperty("ReadHandleOpenMilliseconds", out _),
                "Actual failed-gate serialization lost diagnostic scopes.");
            return Task.CompletedTask;
        });
        await Check("producer-to-gate-identity-mismatch-retains-both-objects-without-ledger", async path =>
        {
            using var f = new Fixture(path); string partial = null, original = null; ReplayExportRequest observedRequest = null;
            f.Source.Estimate = (_, _) => ReplayOutputDemand.Unmeasured("Bounded standalone fixture without an ordinary output claim.");
            f.Source.CompositeExport = (request, _) =>
            {
                observedRequest = request;
                partial = request.Partial; original = partial + ".held";
                request.SelectRecipe(ReplaySaveExport.PacketRecipe);
                File.WriteAllBytes(partial, new byte[4096]); var extent = ReplayStorage.Observe(partial);
                request.FreezeManagedMux(new(Environment.ProcessId, DateTime.UtcNow, true, true, "Completed", 1, 1, 0, 0,
                    extent, null, null, null, 0, 0, new(), new(), 0, 0));
                string digest = new('A', 64);
                request.FreezeIntegrity(new(60, 0, new byte[] { 0x67 }, new byte[] { 0x68 },
                    Enumerable.Range(0, 60).Select(n => new ReplayExpectedPicture(n == 0, digest, digest))));
                ReplayOutputIntegrity.RequireReportedOutput(request.ManagedMux with { FinalOutput = extent with { AllocatedBytes = extent.AllocatedBytes + 4096 } }, extent);
                File.Move(partial, original); File.WriteAllBytes(partial, new byte[4096]);
            };
            f.Source.RequestValidation = (request, check) =>
            {
                ReplayOutputIntegrity.Read(request, check, retainForPublication: true);
                throw new InvalidOperationException("Substituted producer output reached parsing or publication.");
            };
            var saved = await Finish(f.Save());
            Require(!saved.Succeeded && f.Files.Publications == 0 && saved.Error.Contains("identity", StringComparison.OrdinalIgnoreCase),
                "Same-content foreign object was not rejected specifically by producer identity.");
            Require(observedRequest.IntegrityGateTiming.ReaderMilliseconds == null && observedRequest.IntegrityGateTiming.LeaseDisposals == 1
                && observedRequest.IntegrityGateTiming.LeaseTransfers == 0, "Identity failure reached parsing or leaked/transferred the foreign read lease.");
            Require(File.Exists(partial) && File.Exists(original) && File.ReadAllBytes(partial).SequenceEqual(new byte[4096])
                && File.ReadAllBytes(original).SequenceEqual(new byte[4096]), "Identity rejection deleted or changed a potentially foreign object.");
            await f.Cache.RetireSessionAsync(f.Id).WaitAsync(Deadline);
            Require(File.Exists(partial) && File.Exists(original), "Retirement discarded preserved identity-failure evidence.");
        });
        await Check("context-reserved-without-extending-request", async path =>
        {
            using var f = new Fixture(path); f.Source.Segments(62); f.Source.Preroll = 0.05;
            f.Source.CompositeExport = (request, check) =>
            {
                Require(request.Interval.Start == 1 && request.Interval.End == 61 && request.Interval.RequestedSeconds == 60,
                    "Input context changed the original requested interval.");
                Require(request.Inputs.Count == 61 && request.Inputs[0].Start == 0 && request.Inputs[^1].End == 61
                    && request.Inputs.All(i => i.Path.Contains(Path.DirectorySeparatorChar + "save-", StringComparison.OrdinalIgnoreCase)),
                    "Context was not copied/pinned with the actual protected export inputs.");
                File.WriteAllBytes(request.Partial, new byte[2048]); check();
            };
            var result = await Finish(f.Save(61000));
            Require(result.Succeeded && result.Interval.Start == 1 && f.Files.Publications == 1, "Protected context prevented exact publication.");
            f.Preserved();
        });
        foreach (bool sufficient in new[] { false, true })
            await Check("combined-output-scratch-" + (sufficient ? "exact" : "overrun"), async path =>
            {
                using var f = new Fixture(path); string workspace = null;
                f.Source.Estimate = (s, _) => f.Demand(s, sufficient ? 12288 : 8192);
                f.Source.CompositeExport = (request, check) =>
                {
                    workspace = request.WorkDirectory; Directory.CreateDirectory(workspace); request.OwnWorkDirectory = true;
                    File.WriteAllBytes(Path.Combine(workspace, "boundary-000000.ts"), new byte[2048]);
                    File.WriteAllBytes(Path.Combine(workspace, "input.ts"), new byte[2048]);
                    File.WriteAllBytes(request.Partial, new byte[2048]); check();
                };
                var result = await Finish(f.Save());
                Require(result.Succeeded == sufficient && f.Files.Publications == (sufficient ? 1 : 0), "Scratch and final MP4 were not charged together.");
                Require(Directory.Exists(workspace) != sufficient, "Claim failure lost scratch evidence or successful publication leaked owned scratch.");
                f.Preserved();
            });
        foreach (string change in new[] { "identity", "disappearance", "unexpected" })
            await Check("scratch-" + change + "-rejects-publication", async path =>
            {
                using var f = new Fixture(path); string scratch = null;
                f.Source.Estimate = (s, _) => f.Demand(s, 32768);
                f.Source.CompositeExport = (request, check) =>
                {
                    Directory.CreateDirectory(request.WorkDirectory); request.OwnWorkDirectory = true;
                    scratch = Path.Combine(request.WorkDirectory, "input.ts"); File.WriteAllBytes(scratch, new byte[2048]); check();
                    if (change == "identity") f.Files.OutputIdentitySuffix = "-changed";
                    else if (change == "disappearance") File.Delete(scratch);
                    else File.WriteAllText(Path.Combine(request.WorkDirectory, "foreign.txt"), "unregistered");
                    File.WriteAllBytes(request.Partial, new byte[2048]); check();
                };
                var result = await Finish(f.Save());
                Require(!result.Succeeded && f.Files.Publications == 0, "Changed scratch namespace/identity reached publication.");
                f.Preserved();
            });
        foreach (bool retainedNativeProvider in new[] { false, true })
        foreach (bool scratchAlias in new[] { false, true })
            await Check("native-late-" + (scratchAlias ? "scratch" : "output") + "-alias-rejected-on-next-callback"
                + (retainedNativeProvider ? "-explicit-native-retention" : ""), async path =>
            {
                string buffer = Path.Combine(path, "buffer"), clips = Path.Combine(path, "clips"); Directory.CreateDirectory(clips);
                var source = new Source(Path.Combine(buffer, "session-native"));
                var capacity = ReplayStorage.ObserveCapacity(path);
                source.Estimate = (s, _) => new(ReplayOutputEvidenceKind.CalibratedEstimate, "native-namespace-control",
                    s.Identity, s.ExporterIdentity, capacity.Volume, capacity.AllocationUnit,
                    16 * capacity.AllocationUnit, true, null, s.Closed ? null : 4 * capacity.AllocationUnit,
                    new ReplayOutputScratchDemand(2 * capacity.AllocationUnit, 2 * capacity.AllocationUnit));
                bool rejectedImmediately = false; ReplayCache cache = null;
                source.CompositeExport = (request, check) =>
                {
                    string artifact = request.Partial;
                    if (scratchAlias)
                    {
                        Directory.CreateDirectory(request.WorkDirectory); request.OwnWorkDirectory = true;
                        artifact = ReplayOutputWorkspace.AssemblyPath(request.Partial);
                    }
                    File.WriteAllBytes(artifact, new byte[2048]); check();
                    Require(cache.NativeSnapshotHandleCountForChecks > 0,
                        "Native alias control did not exercise an opened sealed-snapshot inventory reader.");
                    Require(Native.CreateHardLink(source.Chunk(99), artifact, IntPtr.Zero), "Native late-alias control needs an actual hard link.");
                    try { check(); } catch (IOException) { rejectedImmediately = true; throw; }
                    throw new InvalidOperationException("Late retained alias was invisible to the immediate callback.");
                };
                using (cache = new ReplayCache(retainedNativeProvider ? new NativeRetentionFiles() : null,
                    cacheDirectory: buffer, fileMode: retainedNativeProvider ? ReplayFileMode.NativeInventoryForChecks : null))
                {
                    var id = cache.RegisterSession(source);
                    var result = await Finish(cache.TrySave(id, 1000, clips));
                    Require(rejectedImmediately && !result.Succeeded && result.Error.Contains("alias", StringComparison.OrdinalIgnoreCase)
                        && !Directory.GetFiles(clips, "Replay-*.mp4").Any(file => !file.EndsWith(".partial.mp4", StringComparison.Ordinal)),
                        "A real new hard-link alias after first materialization must reject on the next callback, not just a later timer/final check.");
                }
            });
        await Check("native-namespace-overlapping-roots-preserve-identity", path =>
        {
            string parent = Path.Combine(path, "parent"), child = Path.Combine(parent, "child"); Directory.CreateDirectory(child);
            File.WriteAllBytes(Path.Combine(parent, "first.ts"), new byte[16]);
            File.WriteAllBytes(Path.Combine(child, "second.ts"), new byte[16]);
            var files = new ReplayFiles(); var once = ReplayStorage.InventoryNamespaces([parent], files);
            var overlap = ReplayStorage.InventoryNamespaces([parent, child], files);
            Require(once.Count == 2 && overlap.Count == once.Count && once.All(e => overlap.GetValueOrDefault(e.Key) == e.Value),
                "Overlapping roots must report the same fresh native namespace without duplicate errors or different identities.");
            return Task.CompletedTask;
        });
        foreach (string name in new[] { "boundary-000002.ts", "boundary-999999.ts" })
            await Check("workspace-consumers-reject-unregistered-boundary-" + name, path =>
            {
                string partial = Path.Combine(path, "output.partial.mp4");
                Directory.CreateDirectory(partial + ".work");
                File.WriteAllBytes(Path.Combine(partial + ".work", name), [1]);
                bool readerRejected = false, observationRejected = false;
                using (var reader = new ReplayOutputWorkspace.Reader(partial))
                    try { reader.Observe(); } catch (IOException) { readerRejected = true; }
                try { ReplayOutputWorkspace.Observe(partial, new ReplayFiles()); } catch (IOException) { observationRejected = true; }
                Require(readerRejected && observationRejected, "Workspace consumers disagree on the two registered boundary names.");
                return Task.CompletedTask;
            });
        await Check("unowned-workspace-preserved-on-failure", async path =>
        {
            using var f = new Fixture(path); string sentinel = null;
            f.Source.CompositeExport = (request, _) =>
            {
                Directory.CreateDirectory(request.WorkDirectory); sentinel = Path.Combine(request.WorkDirectory, "preexisting.txt");
                File.WriteAllText(sentinel, "preserve-unowned-workspace");
                throw new IOException("Workspace belongs to another owner.");
            };
            var result = await Finish(f.Save());
            Require(!result.Succeeded && File.ReadAllText(sentinel) == "preserve-unowned-workspace" && f.Files.Publications == 0,
                "Failure cleanup deleted a workspace without ownership.");
            f.Preserved();
        });
        await Check("shared-ledger-scratch-discharged-once", async path =>
        {
            using var f = new Fixture(path, sharedLedger: true); f.Files.OutputFree = 28672;
            f.Source.Estimate = (s, _) => f.Demand(s, 24576) with { Scratch = new(8192, 4096) };
            f.Source.CompositeExport = (request, check) =>
            {
                Directory.CreateDirectory(request.WorkDirectory); request.OwnWorkDirectory = true;
                File.WriteAllBytes(ReplayOutputWorkspace.BoundaryPath(request.Partial, 0), new byte[2048]);
                File.WriteAllBytes(ReplayOutputWorkspace.AssemblyPath(request.Partial), new byte[6144]);
                File.WriteAllBytes(request.Partial, new byte[6144]);
                check();
                // 28KiBinitial -20KiBmaterialized -4KiBstill reserved=4KiB.
                // Charging the entire24KiB against only the final8KiB would fail.
                var available = f.Reservations.ObserveUnreserved(f.Destination, f.Files);
                Require(available.Bytes == 4096, "Shared ledger charged scratch twice or discharged more than its actual allocation.");
            };
            var result = await Finish(f.Save());
            Require(result.Succeeded && f.Files.Publications == 1, "A fully reserved save failed as its scratch materialized.");
            f.Preserved();
        });
        await Check("stopped-before-accepted-tail-fails-without-shortening", async path =>
        {
            var waiting = Signal(); var release = Signal();
            using var f = new Fixture(path, () => { waiting.TrySetResult(); return release.Task; });
            var accepted = f.Save(1500); // Last closed end=1; original request end=1.5.
            Require(accepted.Accepted, "Controlled unfinished-tail request was not accepted.");
            try
            {
                await waiting.Task.WaitAsync(Deadline);
                f.Source.StopCapture();
            }
            finally { release.TrySetResult(); }
            var result = await Finish(accepted);
            Require(!result.Succeeded && result.Error.Contains("requested", StringComparison.OrdinalIgnoreCase)
                && f.Source.Exports == 0 && f.Files.Copies == 0 && f.Files.Publications == 0,
                "Stopped acquisition silently shortened and published the accepted endpoint.");
            f.Preserved();
            await f.Cache.RetireSessionAsync(f.Id).WaitAsync(Deadline);
            f.Preserved();
        });
        foreach (bool same in new[] { false, true })
            foreach (bool sufficient in new[] { false, true })
                await Check((same ? "same" : "different") + "-volume-" + (sufficient ? "exact" : "one-unit-short"), async path =>
                {
                    using var f = new Fixture(path); f.Files.SameVolume = same;
                    // One 4 KiB copy + one 4 KiB concat + 8 KiB output on A; only 8 KiB output on B.
                    f.Files.CacheFree = same ? (sufficient ? 16384 : 12288) : 8192;
                    f.Files.OutputFree = sufficient ? 8192 : 4096;
                    var submission = f.Save();
                    if (sufficient)
                    {
                        var result = await Finish(submission);
                        Require(result.Succeeded && f.Files.Publications == 1, "Exact capacity failed: " + result.Error);
                    }
                    else Require(!submission.Accepted && f.Source.Exports == 0 && f.Files.Copies == 0, "Insufficient combined/target capacity admitted writes.");
                    f.Preserved();
                });

        foreach (bool insufficient in new[] { false, true })
            await Check("partial-materialization-" + (insufficient ? "later-short" : "exact-remainder"), async path =>
            {
                using var f = new Fixture(path); f.Files.CacheFree = 8192; f.Files.OutputFree = 8192;
                f.Source.ExportAction = (args, _) =>
                {
                    File.WriteAllBytes(args[^1], new byte[4096]);
                    while (f.Files.Observations.TryDequeue(out _)) { }
                    if (insufficient) f.Files.OutputFree = 8191; // Fresh free 4095, remaining output 4096.
                    f.Cache.Maintain(f.Id);
                    var events = f.Files.Observations.ToArray();
                    int prefixIndex = Array.IndexOf(events, "extent:" + args[^1]);
                    int capacityIndex = Array.IndexOf(events, "capacity:" + f.Destination);
                    Require(prefixIndex >= 0 && capacityIndex > prefixIndex,
                        "Fresh capacity was sampled before observing partial allocation.");
                };
                var result = await Finish(f.Save());
                Require(result.Succeeded != insufficient && f.Files.Publications == (insufficient ? 0 : 1), "Remaining allocation capacity decision was wrong: " + result.Error);
                f.Preserved();
            });

        foreach (bool sufficient in new[] { false, true })
            await Check("tail-extension-" + (sufficient ? "exact" : "denied"), async path =>
            {
                var waiting = Signal(); var release = Signal();
                using var f = new Fixture(path, () => { waiting.TrySetResult(); return release.Task; });
                f.Files.CacheFree = 12288; f.Files.OutputFree = sufficient ? 16384 : 12288;
                f.Source.Estimate = (s, _) => f.Demand(s, s.Closed ? 16384 : 8192);
                var save = f.Save(2000);
                try
                {
                    Require(save.Accepted, save.Rejection); await waiting.Task.WaitAsync(Deadline);
                    Require(f.Files.Copies == 0, "Open-tail save wrote before closure.");
                    f.Source.Segments(2); release.TrySetResult();
                    var result = await Finish(save);
                    Require(result.Succeeded == sufficient && f.Source.Exports == (sufficient ? 1 : 0), "Tail extension decision was wrong: " + result.Error);
                    if (!sufficient) Require(f.Files.Copies == 0, "Rejected tail demand copied inputs before extension.");
                    f.Preserved();
                }
                finally { release.TrySetResult(); }
            });

        foreach (string change in new[] { "unmeasured", "revision", "exporter", "selection" })
            await Check("binding-change-" + change, async path =>
            {
                using var f = new Fixture(path);
                f.Source.Estimate = (s, call) =>
                {
                    var demand = f.Demand(s);
                    if (call == 1) return demand;
                    return change switch
                    {
                        "unmeasured" => ReplayOutputDemand.Unmeasured("Independent qualification withdrawal"),
                        "revision" => demand with { Revision = "independent-r2" },
                        "exporter" => demand with { ExporterIdentity = "other-exporter" },
                        _ => demand with { SelectionIdentity = "stale-selection" }
                    };
                };
                var result = await Finish(f.Save());
                Require(!result.Succeeded && f.Files.Copies == 0 && f.Source.Exports == 0 && f.Files.Publications == 0,
                    "Changed reservation evidence proceeded to writes."); f.Preserved();
            });

        foreach (string change in new[] { "volume", "unit", "capacity" })
            await Check("later-output-" + change, async path =>
            {
                using var f = new Fixture(path);
                f.Files.Copied = _ =>
                {
                    if (change == "volume") f.Files.OutputVolume = "volume-C";
                    else if (change == "unit") f.Files.OutputUnit = 8192;
                    else f.Files.OutputFree = 4096;
                };
                var result = await Finish(f.Save());
                Require(!result.Succeeded && f.Source.Exports == 0 && f.Files.Publications == 0, "Later target/capacity failure reached exporter.");
                f.Preserved();
            });

        await Check("publication-survives-cleanup", async path =>
        {
            using var f = new Fixture(path); f.Files.FailCleanup = true;
            var result = await Finish(f.Save());
            Require(result.Succeeded && File.Exists(result.File) && f.Files.Publications == 1, "Cleanup reversed publication success.");
            Require(f.Diagnostics.Any(s => s.Contains("cleanup failed", StringComparison.OrdinalIgnoreCase)), "Cleanup failure had no diagnostic.");
            f.Preserved();
        });

        await Check("unknown-producer-retains-claims-and-files", async path =>
        {
            using var f = new Fixture(path);
            string snapshot = null, partial = null;
            f.Source.ExportAction = (args, snap) =>
            {
                snapshot = snap; partial = args[^1]; File.WriteAllBytes(partial, new byte[2048]);
                f.Source.ProducerAbsent = false;
                throw new IOException("Independent producer absence unknown");
            };
            var result = await Finish(f.Save());
            Require(!result.Succeeded && f.Files.Publications == 0, "Unknown producer published success.");
            Require(File.Exists(partial) && Directory.Exists(snapshot), "Unknown producer lost owned output/snapshot material.");
            Require(!f.Save().Accepted, "Unknown producer released future claims and accepted another save.");
            await f.Cache.RetireSessionAsync(f.Id).WaitAsync(Deadline);
            Require(Directory.Exists(f.Source.ReplayInfo.Directory) && Directory.Exists(snapshot) && File.Exists(partial),
                "Retirement deleted resources while producer absence remained unknown.");
            f.Preserved();
        });

        await Check("observed-peak-overrun-retains-evidence", async path =>
        {
            using var f = new Fixture(path);
            f.Source.Estimate = (s, _) => f.Demand(s, 12288);
            string snapshot = null, partial = null;
            f.Source.ExportAction = (args, snap) =>
            {
                snapshot = snap; partial = args[^1];
                File.WriteAllBytes(partial, new byte[16384]);
            };
            var result = await Finish(f.Save());
            Require(!result.Succeeded && f.Files.Publications == 0, "Observed peak overrun published a qualified result.");
            Require(File.Exists(partial) && Directory.Exists(snapshot), "Observed peak overrun lost investigation evidence.");
            f.Preserved();
            f.Source.ExportAction = null;
            var next = await Finish(f.Save());
            Require(next.Succeeded, "Confirmed absent producer left a blanket admission lock after overrun: " + next.Error);
            Require(File.Exists(partial) && Directory.Exists(snapshot), "Later save deleted earlier rejected evidence.");
            await f.Cache.RetireSessionAsync(f.Id).WaitAsync(Deadline);
            Require(File.Exists(partial) && Directory.Exists(snapshot), "Retirement deleted earlier overrun evidence.");
            var retained = f.Cache.Status;
            Require(retained.StorageError == null && retained.CacheBytes > 0, "Retired overrun evidence became unobservable or uncharged.");
            f.Preserved();
        });

        await Check("materialized-output-identity-change", async path =>
        {
            using var f = new Fixture(path);
            string snapshot = null, partial = null;
            f.Source.ExportAction = (args, snap) =>
            {
                snapshot = snap; partial = args[^1];
                File.WriteAllBytes(partial, new byte[4096]);
                f.Cache.Maintain(f.Id); // First materialization establishes physical identity.
                f.Files.OutputIdentitySuffix = "-replacement";
                f.Cache.Maintain(f.Id); // Same pathname, volume, unit and bytes; different file.
            };
            var result = await Finish(f.Save());
            Require(!result.Succeeded && f.Files.Publications == 0, "Changed materialized physical identity was credited and published.");
            Require(File.Exists(partial) && Directory.Exists(snapshot), "Identity invalidation deleted investigation evidence.");
            f.Preserved();
            f.Source.ExportAction = null;
            Require((await Finish(f.Save())).Succeeded, "Confirmed absent producer left admission locked after identity invalidation.");
            Require(File.Exists(partial) && Directory.Exists(snapshot), "Later save deleted identity invalidation evidence.");
            await f.Cache.RetireSessionAsync(f.Id).WaitAsync(Deadline);
            Require(File.Exists(partial) && Directory.Exists(snapshot), "Retirement deleted earlier identity invalidation evidence.");
            var retained = f.Cache.Status;
            Require(retained.StorageError == null && retained.CacheBytes > 0, "Retired identity evidence became unobservable or uncharged.");
            f.Preserved();
        });

        await Check("validation-failure-prevents-publication", async path =>
        {
            using var f = new Fixture(path);
            f.Source.ValidationAction = () => throw new IOException("Independent corrupt MP4/full decode failure");
            var saved = await Finish(f.Save());
            Require(!saved.Succeeded && f.Files.Publications == 0 && saved.Validation == null,
                "An existing large partial file or successful encoder was mistaken for validated publication.");
            f.Preserved();
        });
        await Check("same-request-reaches-validation-with-honest-counts", async path =>
        {
            using var f = new Fixture(path); ReplayExportRequest exported = null;
            f.Source.CompositeExport = (request, _) => { exported = request; File.WriteAllBytes(request.Partial, new byte[4096]); };
            f.Source.ValidationAction = () => throw new Exception("The request-aware gate was bypassed.");
            f.Source.RequestValidation = (request, check) =>
            {
                Require(ReferenceEquals(request, exported), "The export request/proof was replaced before checking.");
                Require(f.Files.Publications == 0 && File.Exists(request.Partial), "Output published before request validation.");
                check?.Invoke(); return new(ReplayOutputIntegrity.Kind, 0, 1, 60, 0);
            };
            var saved = await Finish(f.Save());
            Require(saved.Succeeded && f.Files.Publications == 1 && saved.Validation.Kind == ReplayOutputIntegrity.Kind
                && saved.Validation.Frames == 0 && saved.Validation.CodedPictures == 60,
                "A coded-source check was misreported as decoding or not published after success.");
            f.Preserved();
        });
        await Check("request-integrity-failure-prevents-publication", async path =>
        {
            using var f = new Fixture(path);
            f.Source.RequestValidation = (_, _) => throw new IOException("Independent source-byte or packet-clock mismatch.");
            var saved = await Finish(f.Save());
            Require(!saved.Succeeded && f.Files.Publications == 0 && saved.Validation == null, "Request integrity failure published an output.");
            f.Preserved();
        });
        await Check("legacy-full-decode-without-checked-lease-prevents-publication", async path =>
        {
            using var f = new Fixture(path);
            f.Source.CompositeExport = (request, _) => { request.SelectRecipe(ReplayExactExport.Recipe); File.WriteAllBytes(request.Partial, new byte[4096]); };
            f.Source.RequestValidation = (_, _) => new("FullDecode", 60, 1, 60, ExportRecipe: ReplayExactExport.Recipe);
            var saved = await Finish(f.Save());
            Require(!saved.Succeeded && f.Files.Publications == 0 && saved.Error.Contains("lease", StringComparison.OrdinalIgnoreCase),
                "A full decode success without same-object lease was published.");
            f.Preserved();
        });
        foreach (string recipe in new[] { ReplayExactExport.Recipe, ReplaySaveExport.PacketRecipe })
            await Check("diagnostic-recipe-erasure-cannot-bypass-publication-lease-" + recipe, async path =>
            {
                using var f = new Fixture(path);
                f.Source.CompositeExport = (request, _) => {
                    request.SelectRecipe(recipe);
                    request.Timing.ExportRecipe = null; // Diagnostic consumers cannot choose the checking contract.
                    File.WriteAllBytes(request.Partial, new byte[4096]);
                };
                f.Source.RequestValidation = (_, _) => new("ControlledNonmedia", 60, 1, 60, ExportRecipe: recipe);
                var saved = await Finish(f.Save());
                Require(!saved.Succeeded && f.Files.Publications == 0 && saved.Error.Contains("lease", StringComparison.OrdinalIgnoreCase),
                    "Diagnostic recipe erasure bypassed the required same-object publication lease.");
                f.Preserved();
            });
        await Check("validated-lease-blocks-writes-through-cache-publication", async path =>
        {
            using var f = new Fixture(path); bool writeDenied = false;
            f.Source.CompositeExport = (request, _) => { request.SelectRecipe(ReplaySaveExport.Recipe); File.WriteAllBytes(request.Partial, new byte[4096]); };
            f.Source.RequestValidation = (request, _) =>
            {
                request.FreezeValidatedOutput(new ReplayPublicationLease(request.Partial));
                try { using var writer = File.OpenWrite(request.Partial); } catch (IOException) { writeDenied = true; }
                return new("ControlledLease", 0, 0, 60, 0, request.Timing.ExportRecipe, new string('A', 64), request.ValidatedOutput.CheckedIdentity);
            };
            var saved = await Finish(f.Save());
            Require(writeDenied && saved.Succeeded && f.Files.Publications == 1 && File.ReadAllBytes(saved.File).SequenceEqual(new byte[4096]),
                "Validated file was writable, replaced or not atomically published: " + saved.Error);
            Require(saved.Validation.PublicationIdentityBound && saved.Validation.SourceProofHash == new string('A', 64)
                && saved.Validation.ExportRecipe == ReplaySaveExport.Recipe
                && ReplayStorage.Observe(saved.File).Identity == saved.Validation.CheckedOutput.Identity
                && saved.Timing.ValidationLeaseCloseCompleted >= saved.Timing.ValidationLeaseCloseStarted
                && saved.Timing.CompletionReady >= saved.Timing.ValidationLeaseCloseCompleted,
                "Publication receipt lost its source/output identity or hid lease closure beyond completion.");
            // Lease must also close on success, rather than stranding the clip.
            using var finalWriter = File.OpenWrite(saved.File);
        });
        await Check("full-decode-input-pins-path-and-immutable-bytes", path =>
        {
            Directory.CreateDirectory(path); string partial = Path.Combine(path, "input"), final = Path.Combine(path, "final");
            File.WriteAllText(partial, "protected full decode fixture");
            var info = new ReplaySessionInfo(path, Path.Combine(path, "segments.csv"), 60, 5, 60, "h264_nvenc", ".ts", false, false);
            var request = new ReplayExportRequest(info, new(0, 1, 1), [], "unused", partial, new());
            using (var decoded = new ReplayDecodeLease(partial))
            {
                bool writeDenied = false, renameDenied = false, deleteDenied = false;
                try { using var writer = File.OpenWrite(partial); } catch (IOException) { writeDenied = true; }
                try { File.Move(partial, final); } catch (IOException) { renameDenied = true; }
                try { File.Delete(partial); } catch (IOException) { deleteDenied = true; }
                Require(writeDenied && renameDenied && deleteDenied && File.Exists(partial), "Decoder input lost byte/path protection.");
                decoded.VerifyUnchanged(); decoded.TransferTo(request);
            }
            using (request.ValidatedOutput)
            {
                bool writeDenied = false;
                try { using var writer = File.OpenWrite(partial); } catch (IOException) { writeDenied = true; }
                Require(writeDenied, "Releasing decoder pin exposed writable checked bytes.");
                request.ValidatedOutput.Publish(final);
                Require(request.ValidatedOutput.Published && ReplayStorage.Observe(final).Identity == request.ValidatedOutput.CheckedIdentity.Identity,
                    "Full decode transfer failed same-object publication.");
            }
            using var finalWriter = File.OpenWrite(final);
            return Task.CompletedTask;
        });
        await Check("decoder-pin-rejects-pre-pin-path-substitution", path =>
        {
            Directory.CreateDirectory(path); string input = Path.Combine(path, "input"), held = input + ".held";
            File.WriteAllText(input, "original");
            using var lease = new ReplayPublicationLease(input);
            File.Move(input, held); File.WriteAllText(input, "replaced");
            bool rejected = false;
            try { using var pin = lease.PinDecoderPath(); } catch (IOException) { rejected = true; }
            Require(rejected && File.ReadAllText(input) == "replaced" && File.ReadAllText(held) == "original",
                "A decoder pin authenticated a substituted path or changed either object.");
            return Task.CompletedTask;
        });
        await Check("paired-qualification-holds-same-sources-across-distinct-snapshots", path =>
        {
            Directory.CreateDirectory(path);
            var originals = new[] { Path.Combine(path, "first"), Path.Combine(path, "second") };
            foreach (string file in originals) File.WriteAllBytes(file, new byte[32]);
            var aliases = originals.Select(p => p + ".other-snapshot").ToArray();
            var info = new ReplaySessionInfo(path, "unused", 300, 20, 60, "h264_nvenc", ".ts", false, false);
            ReplayExportRequest Request(string[] files, ReplayInterval interval = null) => new(info, interval ?? new(0, 2, 2),
                files.Select((p, n) => new ReplayOutputInput(p, n, n + 1, ReplayStorage.Observe(p))).ToArray(), "unused", "unused", new());
            var first = Request(originals); string scope = new string('A', 64);
            using (var pair = new ReplayPairedSelectionLease(first, scope, null))
            {
                // The real second Cache creates links AFTER the first request's
                // immutable/no-delete owner exists, not before protection.
                for (int n = 0; n < 2; n++) Require(Native.CreateHardLink(aliases[n], originals[n], IntPtr.Zero), "New snapshot hardlink while source pinned failed.");
                var second = Request(aliases);
                pair.Verify(second, scope, null);
                bool denied = false; try { using var writer = File.OpenWrite(aliases[0]); } catch (IOException) { denied = true; }
                Require(denied && first.Inputs[0].Path != second.Inputs[0].Path, "Paired proof lost immutability or required identical snapshot names.");
                File.WriteAllBytes(Path.Combine(path, "foreign-same-bytes"), new byte[32]);
                foreach (var changed in new[] { Request([Path.Combine(path, "foreign-same-bytes"), aliases[1]]),
                    Request(aliases.Reverse().ToArray()), Request(aliases, new(1, 3, 2)) })
                {
                    bool rejected = false; try { pair.Verify(changed, scope, null); } catch (IOException) { rejected = true; }
                    Require(rejected, "A changed source object, ordinal or interval passed paired acquisition proof.");
                }
            }
            using var exclusive = new FileStream(originals[0], FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Require(exclusive.Length == 32, "Paired owner failed to close actual source handles.");
            return Task.CompletedTask;
        });
        await Check("failed-full-decode-owner-closes-both-local-leases", path =>
        {
            Directory.CreateDirectory(path); string input = Path.Combine(path, "input"); File.WriteAllText(input, "failure fixture");
            try { using var decoded = new ReplayDecodeLease(input); throw new IOException("Injected semantic validation failure after confirmed absence."); }
            catch (IOException) { }
            using var exclusive = new FileStream(input, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Require(exclusive.Length > 0, "Confirmed failure left decoder input pinned or changed it.");
            return Task.CompletedTask;
        });
        await Check("qualification-owner-spans-cache-completion-and-decoder-handoff", async path =>
        {
            using var f = new Fixture(path); ReplayPublicationLease retained = null;
            f.Source.CompositeExport = (request, _) => { request.SelectRecipe(ReplaySaveExport.Recipe); File.WriteAllBytes(request.Partial, new byte[4096]); };
            f.Source.RequestValidation = (request, _) =>
            {
                request.FreezeValidatedOutput(new ReplayPublicationLease(request.Partial));
                return new("ControlledLease", 0, 0, 60, CheckedOutput: request.ValidatedOutput.CheckedIdentity);
            };
            f.Files.BeforeRequestPublication = (request, _) =>
            {
                retained = new ReplayPublicationLease(request.Partial);
                Require(retained.CheckedIdentity == request.ValidatedOutput.CheckedIdentity, "Qualification handoff changed checked native identity.");
            };
            try
            {
                var saved = await Finish(f.Save());
                Require(saved.Succeeded && retained != null && saved.Validation.PublicationIdentityBound, "Controlled qualified publication failed.");
                bool denied = false; try { using var writer = File.OpenWrite(saved.File); } catch (IOException) { denied = true; }
                Require(denied, "Normal completion opened an unprotected write interval before independent qualification.");
                using (var decoder = new ReplayDecodeLease(saved.File, retained))
                {
                    retained = null; decoder.VerifyUnchanged();
                    Require(decoder.CheckedIdentity == saved.Validation.CheckedOutput, "Independent decoder handoff lost the normally checked file object.");
                }
                using var exclusive = new FileStream(saved.File, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                Require(exclusive.Length == 4096, "Qualified-output owner did not close after confirmed local completion.");
            }
            finally { retained?.Dispose(); }
        });
        await Check("validated-identity-replacement-rejects-publication", async path =>
        {
            using var f = new Fixture(path); string partial = null;
            f.Source.CompositeExport = (request, _) => { request.SelectRecipe(ReplaySaveExport.Recipe); File.WriteAllBytes(request.Partial, new byte[4096]); };
            f.Source.RequestValidation = (request, _) =>
            {
                partial = request.Partial;
                request.FreezeValidatedOutput(new ReplayPublicationLease(request.Partial));
                File.Move(request.Partial, request.Partial + ".held"); File.WriteAllBytes(request.Partial, Enumerable.Repeat((byte)1, 4096).ToArray());
                return new("ControlledLease", 0, 0, 60);
            };
            var saved = await Finish(f.Save());
            Require(!saved.Succeeded && f.Files.Publications == 0, "Path replacement after checking was published.");
            void AssertRetained()
            {
                Require(File.ReadAllBytes(partial).All(b => b == 1) && File.ReadAllBytes(partial + ".held").All(b => b == 0),
                    "Publication failure deleted or changed the replacement or checked original.");
            }
            AssertRetained();
            await f.Cache.RetireSessionAsync(f.Id).WaitAsync(Deadline); AssertRetained();
        });
        await Check("validated-handle-publication-never-overwrites", path =>
        {
            Directory.CreateDirectory(path); string partial = Path.Combine(path, "input"), final = Path.Combine(path, "existing");
            File.WriteAllText(partial, "checked"); File.WriteAllText(final, "preserve");
            var timing = new ReplayPublicationLease.OpenTiming();
            using var lease = new ReplayPublicationLease(partial, timing); bool rejected = false;
            using var recorded = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(new { open = timing }));
            Require(recorded.RootElement.GetProperty("open").GetProperty("ReadHandleOpenMilliseconds").GetDouble() > 0
                && timing.IdentityMilliseconds > 0 && timing.ExtentObservationMilliseconds > 0,
                "Actual publication-lease open timings were not persisted.");
            try { lease.Publish(final); } catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode is 80 or 183) { rejected = true; }
            Require(rejected && File.ReadAllText(partial) == "checked" && File.ReadAllText(final) == "preserve", "Handle publication overwrote an existing target.");
            return Task.CompletedTask;
        });
        await Check("adaptive-native-path-buffers-preserve-long-path-identity", path =>
        {
            foreach (string directory in new[] { Path.Combine(path, "short"), Path.Combine(path, new string('a', 120), new string('b', 120)) })
            {
                Directory.CreateDirectory(directory); string file = Path.Combine(directory, "extent.bin"); File.WriteAllBytes(file, new byte[32]);
                using var anchor = Native.AnchorDirectory(directory);
                Require(Native.FinalDirectoryPath(anchor).Equals(Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase),
                    "Adaptive native path query truncated or changed an anchored directory.");
                var observed = ReplayStorage.Observe(file); using var handle = ReplayStorage.OpenPinnedInventory(file);
                var verified = ReplayStorage.ObserveInventory(handle, observed);
                Require(verified.Identity == observed.Identity && verified.Volume == observed.Volume && verified.Length == 32,
                    "Adaptive volume path query changed identity/EOF or lost a long path.");
            }
            return Task.CompletedTask;
        });
        await Check("native-path-observation-allocation-is-bounded", path =>
        {
            Directory.CreateDirectory(path); string file = Path.Combine(path, "extent.bin"); File.WriteAllBytes(file, new byte[32]);
            var initial = ReplayStorage.Observe(file); using var handle = ReplayStorage.OpenPinnedInventory(file);
            for (int n = 0; n < 10; n++) ReplayStorage.ObserveInventory(handle, initial);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int n = 0; n < 200; n++)
                Require(ReplayStorage.ObserveInventory(handle, initial) == initial, "Repeated native observation changed stable metadata.");
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Require(allocated < 2_000_000, "Native path observation allocated maximum-size buffers per query: " + allocated);
            return Task.CompletedTask;
        });
        await Check("coded-and-decoded-counts-remain-distinct-on-wire", async path =>
        {
            Directory.CreateDirectory(path); using var wire = new MemoryStream();
            await WorkerProtocol.WriteAsync(wire, new WorkerReply { Status = new WorkerStatus { Metrics = new RecorderMetrics {
                LastSaveValidation = ReplayOutputIntegrity.Kind, LastSaveDecodedFrames = 0, LastSaveCodedPictures = 18000, LastSaveAudioPackets = 14065
            } } }, CancellationToken.None);
            wire.Position = 0; var decoded = await WorkerProtocol.ReadAsync<WorkerReply>(wire, CancellationToken.None);
            Require(decoded.Status.Metrics.LastSaveDecodedFrames == 0 && decoded.Status.Metrics.LastSaveCodedPictures == 18000
                && decoded.Status.Metrics.LastSaveAudioPackets == 14065 && decoded.Status.Metrics.LastSaveValidation == ReplayOutputIntegrity.Kind,
                "Worker wire converted checked coded packets into freshly decoded frames.");
        });
        await Check("subsecond-rejects-before-export", path =>
        {
            using var f = new Fixture(path);
            Require(!f.Save(999).Accepted && f.Source.Exports == 0 && f.Files.Publications == 0,
                "A request shorter than one second was accepted.");
            f.Preserved(); return Task.CompletedTask;
        });
        await Check("combined-video-endpoint-rejects-two-frame-overrun", path =>
        {
            Directory.CreateDirectory(path);
            var receipt = new ReplayExportProcessReceipt { ProducerAbsent = true, ActualExitObserved = true, ExitCode = 0, CaptureComplete = true,
                StandardOutput = "frame=61\nprogress=end\n", StandardError = "n: 0 pts: 800 pts_time:0.0166667" };
            bool rejected = false;
            try { ReplayExactExport.ReadValidation(receipt, new ReplayInterval(0, 1, 1), 60); } catch (IOException) { rejected = true; }
            Require(rejected, "Independent first-frame/count allowances combined into two frames of endpoint error.");
            receipt.StandardOutput = "frame=60\nprogress=end\n";
            receipt.StandardError = "n: 0 pts: 840 pts_time:0.0175";
            rejected = false;
            try { ReplayExactExport.ReadValidation(receipt, new ReplayInterval(0, 1, 1), 60); } catch (IOException) { rejected = true; }
            Require(rejected, "A first picture later than one output frame passed using a loose millisecond allowance.");
            return Task.CompletedTask;
        });
        await Check("worker-admission-is-distinct-from-publication", async path =>
        {
            Directory.CreateDirectory(path);
            using var wire = new MemoryStream();
            await WorkerProtocol.WriteAsync(wire, new WorkerReply { ReplayAccepted = true,
                Status = new WorkerStatus { Saving = true, Metrics = new RecorderMetrics { ReplaySaveState = "Accepted" } } }, CancellationToken.None);
            wire.Position = 0;
            var acknowledgement = await WorkerProtocol.ReadAsync<WorkerReply>(wire, CancellationToken.None);
            Require(acknowledgement.ReplayAccepted == true && acknowledgement.Status.Metrics.ReplaySaveState == "Accepted"
                && !acknowledgement.Status.Metrics.LastSaveSucceeded && acknowledgement.Status.Metrics.LastSaveValidation == null,
                "Worker admission acquired a published/validated success state.");
        });
        await Check("large-validation-diagnostics-survive-worker-wire", async path =>
        {
            Directory.CreateDirectory(path);
            var receipt = new ReplayExportProcessReceipt { ProducerAbsent = true, ActualExitObserved = true, ExitCode = 0, CaptureComplete = true,
                StandardOutput = "frame=60\nprogress=end\n", Error = new string('\uFFFF', 600000), StandardError = new string('\uFFFF', 1400000) };
            string message = null;
            try { ReplayExactExport.ReadValidation(receipt, new ReplayInterval(0, 1, 1), 60); }
            catch (IOException error) { message = error.Message; }
            Require(message != null && message.Length <= 1024 && receipt.StandardError.Length == 1400000 && receipt.Error.Length == 600000,
                "Public detail was not bounded or the full diagnostic receipt was lost.");
            using var wire = new MemoryStream();
            await WorkerProtocol.WriteAsync(wire, new WorkerReply { Status = new WorkerStatus { Metrics = new RecorderMetrics { ReplaySaveState = "Failed" },
                Events = Enumerable.Range(1, 64).Select(n => new WorkerEvent { Sequence = n, Kind = n % 2 == 0 ? "saved" : "notification",
                    Message = message, Title = "Replay not saved", Success = false }).ToArray() } }, CancellationToken.None);
            wire.Position = 0; var reply = await WorkerProtocol.ReadAsync<WorkerReply>(wire, CancellationToken.None);
            Require(reply.Status.Events.Length == 64 && reply.Status.Events.All(e => !e.Success && e.Message == message)
                && reply.Status.Metrics.ReplaySaveState == "Failed", "Large-diagnostic failure was lost or became success over the wire.");
        });
        await Check("completion-notices-keep-own-history", async path =>
        {
            Directory.CreateDirectory(path);
            var delayed = new TaskCompletionSource<ReplaySaveResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var seen = new List<ReplaySaveResult>(); var notices = new ReplaySaveNotifications();
            var warm = new ReplaySaveResult(true, "warm.mp4", null, 2048, 1, new ReplayInterval(0, 1.25, 300));
            var full = new ReplaySaveResult(true, "full.mp4", null, 2048, 1, new ReplayInterval(3, 303, 300));
            var first = notices.Observe(delayed.Task, null, null, seen.Add);
            var second = notices.Observe(Task.FromResult(full), null, null, seen.Add);
            delayed.SetResult(warm); await Task.WhenAll(first, second).WaitAsync(Deadline);
            Require(seen.Count == 2 && seen[0] == warm && seen[1] == full && seen[0].Interval.LimitedHistory && !seen[1].Interval.LimitedHistory,
                "A delayed warm-up completion used a later save's history.");
        });
        await Check("legacy-live-calibration-rejects-before-device-work", path =>
        {
            Directory.CreateDirectory(path); string plan = Path.Combine(path, "legacy.json");
            File.WriteAllText(plan, JsonSerializer.Serialize(new { plan = new CalibrationPlan("unused-root", "unused-engine", "unused-environment",
                new Dictionary<string, string>(), [], [], 0, 0) }));
            string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(plan)));
            bool rejected = false;
            try { ReplayCalibrationRun.Run(plan, hash); }
            catch (IOException ex) { rejected = ex.Message.StartsWith("Legacy copy-export calibration admission"); }
            Require(rejected, "Legacy child/resource admission reached device work for a changed exporter.");
            return Task.CompletedTask;
        });
        File.WriteAllText(Path.Combine(root, "verification.json"), JsonSerializer.Serialize(new
        {
            result = failed == 0 ? "passed" : "failed", checks = results,
            attribution = "Initial capacity/binding/tail/preservation behaviors frozen before patch read. Producer-absence, overrun and materialized-identity cases added after contract clarification during integration; later retirement assertions follow independent Standards/Spec reviewer findings. Parent executes this check source.",
            effects = "Tiny nonmedia files and finite fake source only; no engine, capture, GPU, UI, worker, startup or installation.",
            limits = "Synthetic actual-volume observations validate integration, not physical filesystem qualification, calibrated bounds, producer control, queue/manual/global claims or live acceptance."
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Independent replay output validation: {results.Count - failed}/{results.Count} passed. {root}");
        return failed == 0 ? 0 : 1;
    }
}
