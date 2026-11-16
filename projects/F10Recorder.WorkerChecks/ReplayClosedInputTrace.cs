using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace F10Recorder;

// One closed-input Cache/export reproduction, never a live-worker latency
// result. Uses the prior accepted selection and real private catalog; no
// invented numeric output allowance, capture process or account activation.
internal static class ReplayClosedInputTrace
{
    static readonly JsonSerializerOptions Json = new(WorkerProtocol.Json) { WriteIndented = true };
    static void Need(bool value, string reason) { if (!value) throw new IOException(reason); }
    static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }

    sealed class Source(ReplaySessionInfo info, List<Segment> segments, double end, string catalog, string scope, Action bound) : IReplaySessionSource
    {
        readonly ReplayExportLifetime lifetime = new();
        readonly Native.ProcessJob job = new(recoverable: true);
        bool sealedLaunch = true;
        internal ReplayExportRequest Request;
        internal readonly List<ReplayExportProcessReceipt> Children = [];
        public ReplaySessionInfo ReplayInfo => info;
        public bool PreserveEvidence => true;
        public bool Stopped => true;
        public bool OutputProducerStopped => lifetime.Stopped;
        public ReplayProducerProof ProducerProof => ReplayProducerProof.Current(job.Name, sealedLaunch);
        public string Diagnostics => "Closed protected inputs from a failed ordinary save; no capture health or Saved claim.";
        public string OutputDemandIdentity => ReplayReferenceModel.ExporterIdentity;
        public List<Segment> ReadSegments() => segments.Select(s => new Segment { Path = s.Path, Start = s.Start, End = s.End }).ToList();
        public double InputPrerollSeconds => ReplaySaveExport.Preroll(info);
        public double SaveCutoffAt(long timestamp) => end;
        public void FreezeAt(long timestamp) { }
        public void StopCapture() { }
        public void CheckCaptureHealth(double completedEnd) { }
        public ReplayOutputDemand EstimateOutput(ReplayOutputSelection selection)
            => ReplayCalibrationCatalog.LoadFrom(catalog).Estimate(selection, scope, info.Directory);
        public void Export(IReadOnlyList<string> arguments, string snapshot) => throw new IOException("Request-aware export required.");
        public void Export(ReplayExportRequest request, string snapshot, Action<ReplayProducerProof> changed, Action check)
        {
            Request = request;
            using var activity = lifetime.Begin();
            try
            {
                ReplaySaveExport.Run(request, (arguments, guard) => {
                    Need(Children.Count < 2, "At most two boundary/export children.");
                    sealedLaunch = false; changed?.Invoke(ProducerProof);
                    var child = ReplayExportProcess.Run(MediaDependency.ResolveExecutable(), arguments, snapshot, job,
                        started: _ => { sealedLaunch = true; changed?.Invoke(ProducerProof); },
                        producerStopped: absent => {
                            if (!absent) { sealedLaunch = false; changed?.Invoke(ProducerProof); }
                            lifetime.ChildStopped(absent);
                        }, timeoutMilliseconds: 20000, checkStorage: () => { guard?.Invoke(); bound(); });
                    Children.Add(child);
                    Need(child.Success && child.ProducerAbsent, "Closed-input encoder failed; keep receipt.");
                }, check, ReplaySaveExport.DefaultConstruction);
            }
            finally { lifetime.ObserveRequest(request); }
        }
        public ReplayValidation ValidateOutput(ReplayExportRequest request, Action<ReplayProducerProof> changed, Action check)
        {
            using var activity = lifetime.Begin();
            // This narrow attribution run requires the same packet construction
            // as the failed save; do not silently switch its validation contract.
            Need(request.Integrity != null && request.Recipe != ReplayExactExport.Recipe, "Unexpected fallback in packet attribution.");
            long started = Stopwatch.GetTimestamp();
            var result = ReplayOutputIntegrity.Read(request, check, retainForPublication: true);
            return new(ReplayOutputIntegrity.Kind, 0, Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                result.CodedPictures, result.AudioPackets, request.Recipe, request.Integrity.Fingerprint,
                request.ValidatedOutput?.CheckedIdentity);
        }
        public void Dispose()
        {
            Need(lifetime.Stopped, "Closed-input producer closure unconfirmed; retain ownership until outer termination.");
            job.Dispose();
        }
    }

    internal static int Run(string planFile, string expectedHash)
    {
        Need(new FileInfo(planFile).Length <= 65536 && Hash(planFile) == expectedHash, "Exact closed-input trace plan required.");
        using var plan = JsonDocument.Parse(File.ReadAllBytes(planFile)); var p = plan.RootElement;
        Need(p.GetProperty("kind").GetString() == "issue83-closed-input-trace-v1"
            && DateTime.UtcNow.AddSeconds(100) < p.GetProperty("expiresUtc").GetDateTime(), "Fresh 100second trace admission required.");
        foreach (var f in p.GetProperty("files").EnumerateObject()) Need(Hash(f.Name) == f.Value.GetString(), "Trace binding changed: " + f.Name);
        foreach (string file in new[] { Environment.ProcessPath, typeof(ReplayClosedInputTrace).Assembly.Location,
            typeof(CaptureSession).Assembly.Location, typeof(Settings).Assembly.Location })
            Need(p.GetProperty("files").TryGetProperty(file, out _), "Current trace runtime unbound.");
        string root = Path.GetFullPath(p.GetProperty("root").GetString()), catalog = p.GetProperty("catalog").GetString();
        Need(root.StartsWith(Path.GetFullPath("artifacts") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !Directory.Exists(root) && !File.Exists(root), "Fresh isolated trace root required.");
        MediaDependency.RootDirectory = p.GetProperty("engineRoot").GetString();
        var model = ReplayCalibrationCatalog.LoadFrom(catalog).Model; Need(model != null, "Qualified private catalog required.");
        using var previous = JsonDocument.Parse(File.ReadAllBytes(p.GetProperty("previousStage").GetString()));
        var old = previous.RootElement;
        var selected = old.GetProperty("selection");
        var ack = old.TryGetProperty("attempts", out var attempts)
            ? attempts[0].GetProperty("ack") : old.GetProperty("ack");
        Need(ack.GetProperty("ReplayAccepted").GetBoolean()
            && ack.GetProperty("Status").GetProperty("Metrics").GetProperty("ReplayOutputDemandRevision").GetString() == model.Revision
            && selected.GetProperty("ExporterIdentity").GetString() == ReplayReferenceModel.ExporterIdentity,
            "Original actual-worker admission must bind this exact catalog/exporter.");
        var originals = selected.GetProperty("Inputs").Deserialize<ReplayOutputInput[]>(Json);
        Need(originals is { Length: > 0 and <= 305 } && Math.Abs(selected.GetProperty("End").GetDouble() - selected.GetProperty("Start").GetDouble() - 300) < .000001,
            "Exact prior five-minute selection required.");
        Directory.CreateDirectory(root);
        string priorAuthority = ProfileOwnership.RootDirectory, priorLog = Log.DirectoryPath;
        ProfileOwnership.RootDirectory = Path.Combine(root, "authority"); Log.DirectoryPath = root;
        string inputRoot = Path.Combine(root, "inputs"), outputRoot = Path.Combine(root, "output");
        Directory.CreateDirectory(inputRoot); Directory.CreateDirectory(outputRoot);
        var clock = Stopwatch.StartNew(); using var host = Process.GetCurrentProcess();
        var handles = new List<FileStream>(); var traces = new List<ReplayStorageTrace>();
        var maintenance = new List<ReplayMaintenanceTiming>();
        Source source = null; ReplayCache cache = null; SaveSubmission save = null;
        ReplaySaveResult result = null; Exception failure = null;
        long peakBytes = 0, peakRam = 0, lastBudget = 0, priorRootNativeBytes = 0;
        double budgetMilliseconds = 0, budgetWaitMilliseconds = 0, maximumBudgetWaitMilliseconds = 0;
        object budgetGate = new();
        int maintenanceCalls = 0;
        void Bound()
        {
            long waiting = Stopwatch.GetTimestamp();
            lock (budgetGate)
            {
            double wait = Stopwatch.GetElapsedTime(waiting).TotalMilliseconds;
            budgetWaitMilliseconds += wait; maximumBudgetWaitMilliseconds = Math.Max(maximumBudgetWaitMilliseconds, wait);
            Need(clock.Elapsed.TotalSeconds < 80, "80second closed-input work deadline.");
            if (lastBudget != 0 && Stopwatch.GetElapsedTime(lastBudget).TotalMilliseconds < 500) return;
            lastBudget = Stopwatch.GetTimestamp(); host.Refresh(); peakRam = Math.Max(peakRam, host.WorkingSet64);
            Need(peakRam < 1_073_741_824, "Closed-input host RAM bound.");
            long bytes = ReplayStorage.Bytes(ReplayStorage.Inventory([root], new ReplayFiles(), 4096, 64));
            peakBytes = Math.Max(peakBytes, bytes);
            Need(bytes < 7_000_000_000 && ReplayStorage.ObserveCapacity(root).AvailableBytes >= 60_000_000_000, "Closed-input storage stop/free floor.");
            budgetMilliseconds += Stopwatch.GetElapsedTime(lastBudget).TotalMilliseconds;
            }
        }
        try
        {
            priorRootNativeBytes = ReplayStorage.Bytes(ReplayStorage.Inventory([p.GetProperty("previousRoot").GetString()], new ReplayFiles(), 4096, 64));
            Need(priorRootNativeBytes < 3_000_000_000, "Closed failed-pilot root exceeds the separate 3GB active-input allowance.");
            var segments = new List<Segment>();
            for (int i = 0; i < originals.Length; i++)
            {
                Bound(); var original = originals[i];
                Need(Path.GetFullPath(original.Path).StartsWith(p.GetProperty("previousRoot").GetString() + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase), "Only task-owned retained inputs may be opened.");
                var held = new FileStream(original.Path, FileMode.Open, FileAccess.Read, FileShare.Read); handles.Add(held);
                var current = ReplayStorage.ObserveInventory(held.SafeFileHandle, original.Extent);
                Need(current.Length == original.Extent.Length, "Original retained input EOF changed.");
                string copy = Path.Combine(inputRoot, "chunk-" + i.ToString("D6") + ".ts");
                Need(Native.CreateHardLink(copy, original.Path, IntPtr.Zero), "Closed-input protected alias failed.");
                var alias = ReplayStorage.ObserveInventory(copy, current);
                Need(alias.Length == current.Length, "Protected input alias EOF changed.");
                segments.Add(new() { Path = copy, Start = original.Start, End = original.End });
            }
            var info = new ReplaySessionInfo(inputRoot, Path.Combine(inputRoot, "segments.csv"), 300, 20, 60, "h264_nvenc", ".ts", false, true);
            source = new(info, segments, selected.GetProperty("End").GetDouble(), catalog, model.Scope, Bound);
            cache = new(cacheDirectory: inputRoot, volumeReservations: new ReplayVolumeReservations(Path.Combine(root, "authority"), 60_000_000_000));
            cache.StorageObservedForChecks = row => { Need(traces.Count < 8192, "8192 trace row cap."); traces.Add(row); };
            cache.MaintenanceObservedForChecks = row => { Need(maintenance.Count < 1024, "1024 maintenance cap."); maintenance.Add(row); };
            var id = cache.RegisterSession(source);
            save = cache.TrySave(id, Stopwatch.GetTimestamp(), outputRoot); Need(save.Accepted, save.Rejection);
            while (!save.Completion.IsCompleted) { Bound(); cache.Maintain(id); maintenanceCalls++; Thread.Sleep(100); }
            result = save.Completion.GetAwaiter().GetResult(); Need(result.Succeeded, result.Error);
            Need(result.Validation?.Kind == ReplayOutputIntegrity.Kind, "Expected normal packet gate.");
        }
        catch (Exception error) { failure = error; Console.Error.WriteLine(error); }
        finally
        {
            // Never release producer/input ownership based solely on timeout or
            // reporting. The outer bounded helper controller owns termination.
            if (save?.Completion.IsCompleted == false || source?.OutputProducerStopped == false)
            {
                Console.Error.WriteLine("Incomplete accepted work; retain all owners until outer cutoff.");
                while (true) Thread.Sleep(100);
            }
            if (result == null && save?.Completion.IsCompleted == true) result = save.Completion.GetAwaiter().GetResult();
            object report = new { result = failure == null ? "closed-input-trace-completed" : "failed", error = failure?.ToString(),
                save = result, managedMux = source?.Request?.ManagedMux, timing = source?.Request?.Timing,
                integrity = source?.Request?.IntegrityGateTiming, children = source?.Children, traces, maintenance, maintenanceCalls,
                priorRootNativeBytes, peakBytes, peakRam, budgetMilliseconds, budgetWaitMilliseconds, maximumBudgetWaitMilliseconds, seconds = clock.Elapsed.TotalSeconds,
                scope = "One closed-input actual Cache/export/gate/ledger attribution with100ms maintenance. Source scope inherited from bound original accepted ordinary request and same model/exporter; exact input IDs/EOF pinned. No capture/worker/Saved/distribution/source-lineage or storage-model qualification. Original root remains protected. Observer costs included. No synthetic numeric allowance." };
            try
            {
                var compact = new JsonSerializerOptions(Json) { WriteIndented = false };
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(report, compact);
                if (bytes.Length > 4_194_304)
                {
                    failure ??= new IOException("Complete trace exceeds4MiB; retain partial failure attribution, no successful collection.");
                    bytes = JsonSerializer.SerializeToUtf8Bytes(new { result = "failed-report-overflow", error = failure.ToString(),
                        originalReportBytes = bytes.Length, originalTraceCount = traces.Count, originalMaintenanceCount = maintenance.Count,
                        completeTraceRetained = false, failedTraces = traces.Where(t => t.Error != null).Take(128).ToArray(),
                        firstTraces = traces.Take(16).ToArray(), lastTraces = traces.TakeLast(16).ToArray(),
                        timing = source?.Request?.Timing, managedMux = source?.Request?.ManagedMux, children = source?.Children }, compact);
                }
                Need(bytes.Length <= 4_194_304, "4MiB partial failure report cap.");
                File.WriteAllBytes(Path.Combine(root, "result.json"), bytes);
                lastBudget = 0; Bound();
                Need(ReplayStorage.Bytes(ReplayStorage.Inventory([p.GetProperty("previousRoot").GetString()], new ReplayFiles(), 4096, 64)) < 3_000_000_000,
                    "Final closed-input source-root allowance exceeded.");
            }
            finally
            {
                try { cache?.Dispose(); source?.Dispose(); }
                finally
                {
                    foreach (var held in handles) held.Dispose();
                    ProfileOwnership.RootDirectory = priorAuthority; Log.DirectoryPath = priorLog;
                }
            }
        }
        return failure == null ? 0 : 1;
    }
}
