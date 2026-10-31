using System.Reflection;
using System.Text.Json;

namespace F10Recorder;

// Tiny nonmedia sources exercise the production supervisor retirement boundary.
// The engine stays paused; no capture/session constructor or engine process runs.
internal static class ReplayRetirementChecks
{
    enum FailureStage { None, ManifestDrain, DiagnosticsDrain, Disposal }
    static string Diagnostic(FailureStage stage) => stage switch
    {
        FailureStage.None => null,
        FailureStage.ManifestDrain => "Replay manifest publication failed.",
        FailureStage.DiagnosticsDrain => "Capture diagnostics did not drain before the shutdown deadline.",
        FailureStage.Disposal => "Source disposal failed.",
        _ => throw new ArgumentOutOfRangeException(nameof(stage))
    };
    sealed class Source(string directory, FailureStage stage, bool failExport) : IReplaySessionSource
    {
        internal readonly ManualResetEventSlim ExportEntered = new(), ReleaseExport = new();
        internal bool Disposed;
        public ReplaySessionInfo ReplayInfo => new(directory, Path.Combine(directory, "segments.csv"), 2, 20, 60, "h264_nvenc", ".ts", false, false);
        public bool Stopped { get; private set; }
        public string Diagnostics => "Controlled retirement failure; not media.";
        public string OutputDemandIdentity => "retirement-check-exporter";
        public bool OutputProducerStopped => true;
        public double SaveCutoffAt(long timestamp) => 2;
        public void FreezeAt(long timestamp) { }
        public void StopCapture() { Stopped = true; if (stage is FailureStage.ManifestDrain or FailureStage.DiagnosticsDrain) throw new IOException(Diagnostic(stage)); }
        public void CheckCaptureHealth(double end) { }
        public ReplayOutputDemand EstimateOutput(ReplayOutputSelection selection)
        {
            var capacity = ReplayStorage.ObserveCapacity(directory);
            return new(ReplayOutputEvidenceKind.CalibratedEstimate, "retirement-check", selection.Identity, selection.ExporterIdentity,
                capacity.Volume, capacity.AllocationUnit, 8_388_608, false, null);
        }
        public void Export(IReadOnlyList<string> arguments, string snapshot)
        {
            ExportEntered.Set();
            if (!ReleaseExport.Wait(TimeSpan.FromSeconds(8))) throw new IOException("Controlled export deadline.");
            if (Disposed || !Directory.Exists(snapshot)) throw new IOException("Retirement disposed accepted export inputs.");
            if (failExport) throw new IOException("Controlled accepted export failed truthfully.");
            File.WriteAllBytes(arguments[^1], new byte[2048]);
        }
        public void Dispose() { Disposed = true; if (stage == FailureStage.Disposal) throw new IOException(Diagnostic(stage)); }
    }
    static T Field<T>(ReplayEngine engine, string name) => (T)typeof(ReplayEngine)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(engine);
    internal static int Run(string root)
    {
        if (Directory.Exists(root)) throw new IOException("Fresh retirement evidence root required.");
        Directory.CreateDirectory(root);
        string previousAuthority = ProfileOwnership.RootDirectory, previousLog = Log.DirectoryPath;
        var results = new List<object>(); int failed = 0;
        try
        {
            ProfileOwnership.RootDirectory = Path.Combine(root, "authority"); Log.DirectoryPath = root;
            foreach (var (stage, failExport) in new[] { (FailureStage.ManifestDrain, false),
                (FailureStage.DiagnosticsDrain, false), (FailureStage.Disposal, false),
                (FailureStage.ManifestDrain, true), (FailureStage.None, false) })
            {
                string path = Path.Combine(root, "case-" + results.Count), profile = Path.Combine(path, "profile"), output = Path.Combine(path, "output");
                Directory.CreateDirectory(profile); Directory.CreateDirectory(output);
                var latch = new ReplayStorageStopLatch(profile, ProfileOwnership.RootDirectory);
                using var coordinator = new ReplayStorageCoordinator(latch, new ReplayVolumeReservations(ProfileOwnership.RootDirectory));
                var engine = new ReplayEngine(new Settings { OutputDirectory = output }, profile, true, true, null, coordinator);
                var source = new Source(Path.Combine(profile, "buffer", "session-test"), stage, failExport);
                Directory.CreateDirectory(source.ReplayInfo.Directory);
                string original = Path.Combine(source.ReplayInfo.Directory, "chunk-000000000.ts");
                File.WriteAllText(original, "Retained nonmedia original");
                File.WriteAllText(source.ReplayInfo.Manifest, "chunk-000000000.ts,0,2\n");
                var cache = Field<ReplayCache>(engine, "replays");
                var id = cache.RegisterSession(source);
                Exception failure = null;
                try
                {
                    var save = cache.TrySave(id, 0, output);
                    Require(save.Accepted, save.Rejection);
                    Require(source.ExportEntered.Wait(TimeSpan.FromSeconds(3)), "Accepted save did not enter controlled export.");
                    var retiring = Task.Run(() => typeof(ReplayEngine).GetMethod("RetireCapture", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(engine, [id]));
                    Thread.Sleep(150);
                    Require(!retiring.IsCompleted && !source.Disposed, "Retirement escaped before draining the accepted save.");
                    source.ReleaseExport.Set();
                    retiring.WaitAsync(TimeSpan.FromSeconds(4)).GetAwaiter().GetResult();
                    var result = save.Completion.GetAwaiter().GetResult();
                    Require(result.Succeeded == !failExport, "Accepted save lost its truthful outcome: " + result.Error);
                    Require(failExport ? result.Error.Contains("Controlled accepted export failed") : File.Exists(result.File),
                        "Accepted save did not publish or report its controlled failure.");
                    Require(source.Disposed && Field<Thread>(engine, "worker").IsAlive, "Supervisor did not retain a live paused worker after draining.");
                    Require(latch.Read().Held == (stage != FailureStage.None), "Only failed retirement should latch a storage hold.");
                    Require(File.Exists(original) == (stage != FailureStage.None), "Failed session originals must survive; healthy retirement must still clean up.");
                }
                catch (Exception ex) { failure = ex; }
                finally
                {
                    source.ReleaseExport.Set();
                    try { engine.Dispose(); } catch (Exception ex) { failure ??= ex; }
                }
                if (failure != null) failed++;
                results.Add(new { name = Diagnostic(stage) ?? "normal-retirement", failExport, passed = failure == null, error = failure?.ToString() });
            }
        }
        finally { ProfileOwnership.RootDirectory = previousAuthority; Log.DirectoryPath = previousLog; }
        File.WriteAllText(Path.Combine(root, "verification.json"), JsonSerializer.Serialize(new { result = failed == 0 ? "passed" : "failed", failed, results,
            effects = "Tiny nonmedia files, isolated authority, paused production supervisor and controlled export thread only. No capture, engine child, audio, model activation, user settings/startup or installation." }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Retirement checks: {results.Count - failed}/{results.Count} passed."); return failed == 0 ? 0 : 1;
    }
    static void Require(bool yes, string reason) { if (!yes) throw new IOException(reason); }
}
