using System.Diagnostics;
using System.Text.Json;

namespace F10Recorder;

internal static partial class ReplayCalibrationRun
{
    // Same RecorderWorker/Host/Engine, native acquisition, protocol and ledger as
    // the app. Only the candidate catalog and account authority are task-private.
    static void RequireWorkerHealth(WorkerStatus status, int acquisitions) =>
        Require(status != null && !status.Metrics.ReplayStorageSafetyHeld && acquisitions == 1,
            "Real worker entered a storage safety hold or started another acquisition.");
    sealed class Issue14WorkerCase : IDisposable
    {
        readonly string data, root, previousAuthority;
        readonly Thread thread;
        readonly TaskCompletionSource<CaptureSession> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int acquisitions, exit = -1;
        CaptureSession source;
        int healthChecks;
        internal Task<CaptureSession> Ready => ready.Task;
        internal static string CacheRoot(string cell) => ReplayCacheLocations.Current(
            new Settings { ReplayCacheDirectory = Path.Combine(cell, "buffer") }, Path.Combine(cell, "worker-profile"));
        internal Issue14WorkerCase(string root, Settings settings, ReplayReferenceModel model)
        {
            this.root = root; data = Path.Combine(root, "worker-profile"); Directory.CreateDirectory(data);
            settings = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings, WorkerProtocol.Json), WorkerProtocol.Json);
            settings.ReplayCacheDirectory = Path.Combine(root, "buffer");
            settings.ScreenshotDirectory = Path.Combine(root, "screenshots");
            settings.SaveModifiers = settings.OverlayModifiers = settings.ScreenshotModifiers = 7;
            settings.SaveKey = 121; settings.OverlayKey = 120; settings.ScreenshotKey = 119;
            RecordingProfile.Load(data).Commit(settings, false, false, false);
            previousAuthority = ProfileOwnership.RootDirectory;
            ProfileOwnership.RootDirectory = Path.Combine(Path.GetDirectoryName(root), "calibration-ownership");
            ReplayCalibrationCatalog.CandidateForChecks = () => new(model, null);
            CaptureSession.StartingForChecks = capture =>
            {
                Require(Interlocked.Increment(ref acquisitions) == 1, "No additional native acquisition is admitted on worker recovery.");
                source = capture;
                AdmitNativeChild("capture:H03-worker"); capture.RetainEvidence = true;
                capture.ValidationArgumentObserver = _ => AdmitNativeChild("runtime-validation:H03-worker");
            };
            CaptureSession.StartedForChecks = capture => { source = capture; ready.TrySetResult(capture); };
            thread = new Thread(() =>
            {
                try { exit = RecorderWorker.Run(data, false, setStartup: _ => throw new IOException("Native worker must not write startup."),
                    screenshotSource: (_, _) => throw new IOException("No screenshot admitted."), repairEngine: () => throw new IOException("No engine installation admitted."));
                    if (!ready.Task.IsCompleted) ready.TrySetException(new IOException("Worker exited before capture became ready: " + exit)); }
                catch (Exception ex) { ready.TrySetException(ex); }
            });
            thread.SetApartmentState(ApartmentState.STA); thread.Start();
        }
        WorkerReply Call(string command, bool value = false, long after = -1) => WorkerProtocol.CallAsync(data,
            new WorkerRequest { Command = command, Value = value, AfterEvent = after }).GetAwaiter().GetResult();
        internal void AbortCapture() => source?.Dispose();
        internal void CheckHealth()
        {
            var status = Call("status").Status;
            // While acquiring, the durable capture guard is deliberately held;
            // the live coordinator distinguishes it from a safety stop. Require
            // the durable record to clear after confirmed normal retirement.
            RequireWorkerHealth(status, acquisitions);
            if (Interlocked.Increment(ref healthChecks) == 1)
                Write(Path.Combine(root, "worker-initial-health.json"), new { status, durableGuardProofDeferredUntilWorkerExit = true });
        }
        internal void StopCapture()
        {
            var reply = Call("pause", true); Require(reply.Error == null, reply.Error);
            var wait = Stopwatch.StartNew();
            while (source?.CaptureProducerAbsent != true) { Require(wait.Elapsed.TotalSeconds < 12, "Owned worker capture did not stop."); Thread.Sleep(25); }
            CheckHealth();
            // A separate latch instance would compete with the coordinator's
            // exclusive lock, even for Read(). Producer absence precedes storage
            // retirement; prove the durable guard only after worker/host join.
            string stopped = Path.Combine(root, "worker-stopped-health.json");
            if (!File.Exists(stopped)) Write(stopped, new { status = Call("status").Status,
                source.CaptureProducerAbsent, durableGuardProofDeferredUntilWorkerExit = true });
        }
        internal SaveSubmission Save()
        {
            CheckHealth(); var before = Call("status"); long sequence = before.Status.Sequence;
            var ack = Call("save", after: sequence);
            Write(Path.Combine(root, "worker-admission.json"), ack);
            Require(ack.Error == null && ack.ReplayAccepted == true && ack.ReplayRequestStart.HasValue && ack.ReplayRequestEnd.HasValue,
                "Real worker did not explicitly accept the replay.");
            var interval = new ReplayInterval(ack.ReplayRequestStart.Value, ack.ReplayRequestEnd.Value, ack.ReplayRequestedSeconds.Value);
            var demand = source.LastOutputDemand with { CoversOpenTail = ack.ReplayAdmissionCoversOpenTail == true,
                SnapshotPeakAllocatedBytes = ack.ReplayAdmissionSnapshotPeakBytes };
            var completion = Task.Run(async () =>
            {
                var wait = Stopwatch.StartNew(); WorkerReply current = ack;
                while (true)
                {
                    Require(current.Status != null && !current.Status.Metrics.ReplayStorageSafetyHeld,
                        "Real worker storage supervision held the native source during save.");
                    var done = current.Status.Events.LastOrDefault(e => e.Sequence > sequence && e.Kind == "saved");
                    if (done != null)
                    {
                        var m = current.Status.Metrics;
                        Write(Path.Combine(root, "worker-publication.json"), current);
                        return new ReplaySaveResult(done.Success, done.Success ? done.Message : null, done.Success ? null : done.Message,
                            m.LastSaveBytes, m.LastSaveMilliseconds, interval,
                            done.Success ? new ReplayValidation(m.LastSaveValidation, m.LastSaveDecodedFrames, m.LastSaveValidationMilliseconds) : null);
                    }
                    Require(current.Error == null && wait.Elapsed.TotalSeconds < 130 && acquisitions == 1, "Real worker save failed, recovered or timed out.");
                    await Task.Delay(100); current = Call("status", after: sequence);
                }
            });
            return new(null, completion, demand, interval);
        }
        public void Dispose()
        {
            try
            {
                if (thread.IsAlive) { try { Call("quit"); } catch (IOException) { } }
                Require(thread.Join(15000), "Owned in-process recorder worker did not close.");
                Require(exit == 0 && acquisitions == 1, "Owned worker did not exit normally with one capture.");
                var hold = new ReplayStorageStopLatch(data, ProfileOwnership.RootDirectory).Read();
                Require(!hold.Held,
                    "Real worker retained a durable safety hold after closure.");
                Write(Path.Combine(root, "worker-exit.json"), new { exit, acquisitions, workerJoined = true,
                    durableGuardReadAfterJoin = true, hold, defaultCatalogActivated = false });
            }
            finally
            {
                // A failed join leaves isolation/admission hooks installed until
                // the outer controller terminates its own collector process.
                if (!thread.IsAlive)
                {
                    CaptureSession.StartingForChecks = null; CaptureSession.StartedForChecks = null;
                    ReplayCalibrationCatalog.CandidateForChecks = null; ProfileOwnership.RootDirectory = previousAuthority;
                }
            }
        }
    }
}
