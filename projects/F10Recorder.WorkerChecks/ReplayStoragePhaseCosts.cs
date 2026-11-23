using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace F10Recorder;

// A finite metadata component comparison, never a catalog or a playable save.
// Both arms execute actual Cache checks; only the private fixture ledger varies.
internal static class ReplayStoragePhaseCosts
{
    const string StopBeforePublication = "Controlled phase measurement ends before validation/publication.";
    static readonly JsonSerializerOptions Json = new() { IncludeFields = true, WriteIndented = true };
    static void Need(bool value, string message) { if (!value) throw new IOException(message); }
    static string Hash(string path) { using var input = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(input)); }
    sealed class Source(string directory, ReplayVolumeCapacity volume) : IReplaySessionSource
    {
        public ReplaySessionInfo ReplayInfo { get; } = new(directory, Path.Combine(directory, "segments.csv"), 300, 20, 60, "h264_nvenc", ".ts", false, false);
        public bool PreserveEvidence => true;
        public bool Stopped => true;
        public bool OutputProducerStopped => true;
        public string OutputDemandIdentity => "controlled-storage-phase-fixture-v1";
        public string Diagnostics => "Numeric metadata fixture, not a qualified recording source.";
        public double SaveCutoffAt(long timestamp) => 300.5;
        public void FreezeAt(long timestamp) { }
        public void StopCapture() { }
        public void CheckCaptureHealth(double end) { }
        public void Dispose() { }
        public ReplayOutputDemand EstimateOutput(ReplayOutputSelection selection) => new(ReplayOutputEvidenceKind.CalibratedEstimate,
            "controlled-numeric-fixture-not-model", selection.Identity, selection.ExporterIdentity, volume.Volume, volume.AllocationUnit,
            16_777_216, true, null, ReplayReferenceModel.Round(1_100_000_000, volume.AllocationUnit));
        internal Action<ReplayExportRequest, Action> Work;
        public void Export(IReadOnlyList<string> arguments, string snapshot) => throw new IOException("Request overload required.");
        public void Export(ReplayExportRequest request, string snapshot, Action<ReplayProducerProof> changed, Action check) => Work(request, check);
        public ReplayValidation ValidateOutput(ReplayExportRequest request, Action<ReplayProducerProof> changed, Action check)
            => throw new IOException(StopBeforePublication);
    }

    internal static int Run(string planFile, string expectedHash)
    {
        using var planStream = File.OpenRead(planFile);
        Need(planStream.Length is > 0 and <= 65536, "Bounded phase plan required.");
        var raw = new byte[checked((int)planStream.Length)]; planStream.ReadExactly(raw);
        Need(Convert.ToHexString(SHA256.HashData(raw)) == expectedHash, "Exact phase admission required.");
        using var document = JsonDocument.Parse(raw); var p = document.RootElement;
        bool compareMetadata = p.GetProperty("kind").GetString() == "issue83-storage-input-metadata-reuse-v1";
        bool[] order = compareMetadata ? [false, true, true, false] : [true, false, false, true];
        Need((compareMetadata || p.GetProperty("kind").GetString() == "issue83-storage-capacity-phases-v1")
            && p.GetProperty("seconds").GetInt32() == 60 && p.GetProperty("callbacks").GetInt32() == 16
            && p.GetProperty("order").EnumerateArray().Select(x => x.GetBoolean()).SequenceEqual(order)
            && DateTime.UtcNow.AddSeconds(80) < p.GetProperty("expiresUtc").GetDateTime(), "Finite ABBA component admission required.");
        foreach (var f in p.GetProperty("files").EnumerateObject()) Need(Hash(f.Name) == f.Value.GetString(), "Phase binding changed: " + f.Name);
        Need(p.GetProperty("files").TryGetProperty(typeof(ReplayStoragePhaseCosts).Assembly.Location, out _)
            && p.GetProperty("files").TryGetProperty(typeof(ReplayCache).Assembly.Location, out _), "Actual helper/product must be bound.");
        string root = Path.GetFullPath(p.GetProperty("root").GetString()), sourceRoot = Path.GetFullPath(p.GetProperty("sourceRoot").GetString());
        string prefix = Path.GetFullPath("artifacts") + Path.DirectorySeparatorChar;
        Need(root.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && sourceRoot.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && !Directory.Exists(root) && !File.Exists(root) && !sourceRoot.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !root.StartsWith(sourceRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !root.Equals(sourceRoot, StringComparison.OrdinalIgnoreCase), "Fresh disjoint task-owned component root required.");
        using var owned = PilotStorageObservation.Create(root);
        using var parent = Process.GetCurrentProcess();
        var clock = Stopwatch.StartNew(); var trials = new List<object>(); Exception failure = null;
        long peakRam = 0, metricAt = 0; int peakHandles = 0, metricQueries = 0; double observerMilliseconds = 0;
        void Bound(bool force = false)
        {
            Need(clock.Elapsed.TotalSeconds < 60, "Phase comparison whole deadline.");
            if (!force && metricAt != 0 && Stopwatch.GetElapsedTime(metricAt).TotalMilliseconds < 500) return;
            metricAt = Stopwatch.GetTimestamp(); parent.Refresh(); metricQueries++;
            peakRam = Math.Max(peakRam, parent.WorkingSet64); peakHandles = Math.Max(peakHandles, parent.HandleCount);
            Need(peakRam <= 268_435_456 && peakHandles <= 4096, "Phase comparison memory/handle bound.");
            Need(ReplayStorage.ObserveCapacity(root).AvailableBytes >= 60_000_000_000, "Phase comparison free-space floor.");
            observerMilliseconds += Stopwatch.GetElapsedTime(metricAt).TotalMilliseconds;
        }
        try
        {
            using var frozen = new FrozenCalibrationHistory([sourceRoot], 1024, 32);
            Need(frozen.AllocatedBytes <= 1_200_000_000, "Source native allocation bound.");
            string[] originals = Directory.GetFiles(sourceRoot, "chunk-*.ts").Order(StringComparer.OrdinalIgnoreCase).Take(301).ToArray();
            Need(originals.Length == 301, "Exactly301 original objects required.");
            int ordinal = 0;
            foreach (bool arm in order)
            {
                bool ledger = compareMetadata || arm, reuse = compareMetadata && arm;
                Bound(); frozen.Check();
                string cell = Path.Combine(root, "trial-" + ++ordinal), buffer = Path.Combine(cell, "buffer"), input = Path.Combine(buffer, "session"), output = Path.Combine(cell, "output");
                Directory.CreateDirectory(input); Directory.CreateDirectory(output);
                var lines = new List<string>();
                for (int n = 0; n < originals.Length; n++)
                {
                    string name = "chunk-" + n.ToString("D9") + ".ts";
                    Need(Native.CreateHardLink(Path.Combine(input, name), originals[n], IntPtr.Zero), "Phase input hardlink failed.");
                    lines.Add(FormattableString.Invariant($"{name},{n},{n + 1}"));
                }
                File.WriteAllLines(Path.Combine(input, "segments.csv"), lines);
                var source = new Source(input, ReplayStorage.ObserveCapacity(output));
                var diagnostics = new List<string>();
                using var cache = new ReplayCache(diagnostic: message => {
                    Need(diagnostics.Count < 128 && message.Length <= 8192, "Phase diagnostic bound."); diagnostics.Add(message);
                }, cacheDirectory: buffer, reuseCompletedInputMetadata: reuse,
                    volumeReservations: ledger ? new ReplayVolumeReservations(Path.Combine(cell, "authority"), 60_000_000_000) : null);
                var traces = new List<ReplayStorageTrace>(); bool exporting = false; long preparationTraces = 0;
                cache.StorageObservedForChecks = trace => {
                    if (!exporting) { preparationTraces++; return; }
                    Need(traces.Count < 4096, "Phase trace row cap."); traces.Add(trace);
                };
                var id = cache.RegisterSession(source); int attempted = 0, completed = 0; double callbacksMs = 0;
                int? handlesAtExport = null;
                source.Work = (request, check) => {
                    File.WriteAllBytes(request.Partial, new byte[4096]); exporting = true;
                    handlesAtExport = cache.NativeSnapshotHandleCountForChecks;
                    for (int n = 0; n < 16; n++)
                    {
                        Bound(); attempted++; long at = Stopwatch.GetTimestamp();
                        try { check(); completed++; }
                        finally { callbacksMs += Stopwatch.GetElapsedTime(at).TotalMilliseconds; }
                        Thread.Sleep(110); // Finite steady-state full observations, not capture maintenance.
                    }
                };
                var save = cache.TrySave(id, Stopwatch.GetTimestamp(), output); Need(save.Accepted, save.Rejection);
                if (!save.Completion.Wait(TimeSpan.FromSeconds(55)))
                {
                    try { File.WriteAllText(Path.Combine(root, "incomplete.json"), JsonSerializer.Serialize(new { reason = "accepted-work-unconfirmed", ordinal, attempted, completed }, Json)); }
                    catch (Exception error) { try { Console.Error.WriteLine("Incomplete diagnostic failed: " + error); } catch { } }
                    finally
                    {
                        // Reporting failure must never unwind live owners.
                        // The hash-bound outer cutoff terminates this helper.
                        while (true) Thread.Sleep(100);
                    }
                }
                var result = save.Completion.GetAwaiter().GetResult();
                object row = new { ordinal, ledger, reuseCompletedInputs = reuse, attempted, completed, callbacksMs, preparationTraces,
                    handlesAtExport, handlesAfterCompletion = cache.NativeSnapshotHandleCountForChecks, timing = result.Timing, traces, diagnostics,
                    succeeded = result.Succeeded, error = result.Error };
                trials.Add(row);
                byte[] rowBytes = JsonSerializer.SerializeToUtf8Bytes(row, Json);
                Need(rowBytes.Length <= 2_097_152, "Phase trial report byte cap.");
                File.WriteAllBytes(Path.Combine(cell, "phase-trace.json"), rowBytes);
                Need(!result.Succeeded && result.Error?.Contains(StopBeforePublication, StringComparison.Ordinal) == true
                    && attempted == 16 && completed == 16 && traces.Any(t => t.Scope == "FullObservation")
                    && !Directory.GetFiles(output, "*.mp4").Any(f => !f.EndsWith(".partial.mp4", StringComparison.OrdinalIgnoreCase)),
                    "Unexpected component failure/publication; no next arm: " + result.Error);
                foreach (var trace in traces)
                {
                    Need(trace.CompletedQpc >= trace.StartedQpc && trace.Phases.All(s => s.StartedQpc >= trace.StartedQpc
                        && s.CompletedQpc >= s.StartedQpc && s.CompletedQpc <= trace.CompletedQpc), "Invalid correlated trace clock.");
                    if (trace.ParentOrdinal != 0) Need(traces.Any(t => t.Ordinal == trace.ParentOrdinal), "Trace parent missing.");
                }
                frozen.Check(); Bound();
            }
        }
        catch (Exception error) { failure = error; }
        try { Bound(force: true); } catch (Exception error) { failure ??= error; }
        byte[] report = JsonSerializer.SerializeToUtf8Bytes(new { result = failure == null ? compareMetadata ? "measured-completed-input-metadata-phases" : "measured-storage-capacity-phases" : "failed-no-retry",
            trials, seconds = clock.Elapsed.TotalSeconds, peakRam, peakHandles, metricQueries, observerMilliseconds, error = failure?.ToString(),
            interpretation = (compareMetadata ? "Finite exact-completed-input metadata-handle ABBA explicitOFF/ON/ON/OFF,ALLprivatefixtureledgersON. Lazyopens/scopeclosesinsideacceptedrequest;productiondefaultselectsonlyexistingnative-eligibleproviders. "
                : "Finite native Cache component ABBA with/without private numeric fixture ledger. ")
                + "16pacedcallbacks each,4KiBnonmediaoutput,defaultnativeprovider,sealed-snapshotreader,no maintenance or intrusive observer walk. Nested phases overlap; callbacks include instrumentation; preparation traces counted but not retained. Intentional refusal before validation/publication. No recording, decoder, real model, ordinarySaved/cold-cache or777ms-reproduction claim." }, Json);
        Need(report.Length <= 4_194_304, "Phase combined report cap.");
        File.WriteAllBytes(Path.Combine(root, "result.json"), report);
        try { Bound(force: true); }
        catch (Exception error)
        {
            failure ??= error;
            File.WriteAllText(Path.Combine(root, "final-check-failure.json"), JsonSerializer.Serialize(new {
                result = "failed-after-report-no-acceptance", error = failure.ToString(), seconds = clock.Elapsed.TotalSeconds,
                interpretation = "Original result alone is insufficient; final budget and outer exit must both pass." }, Json));
        }
        if (failure != null) Console.Error.WriteLine(failure); return failure == null ? 0 : 1;
    }
}
