using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace F10Recorder;

internal static class RecorderWorker
{
    internal static int Run(string data, bool paused, string uiExecutable = null, bool synthetic = false, Action<bool> setStartup = null, Func<int, string, string> screenshotSource = null, Func<int> repairEngine = null)
    {
        data = WorkerIdentity.DataPath(data); RecorderHost.Initialize(data);
        Log.FileName = "worker.log";
        IDisposable profileOwnership;
        try { profileOwnership = ProfileOwnership.Acquire(data, out string bound); data = bound; Log.DirectoryPath = data; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write("Recording profile ownership could not be established. Another session may own it, or private ownership storage needs repair. " + ex.Message);
            return 1;
        }
        using var profileLifetime = profileOwnership;
        var loop = new WorkerLoop();
        using var host = new RecorderHost(data, loop.Post, paused, WorkerIdentity.ControlTitle(data), synthetic, setStartup, screenshotSource, repairEngine);
        using var lifetime = new CancellationTokenSource();
        var events = new Queue<WorkerEvent>();
        long sequence = 0;
        long revision = 0;
        string instance = Guid.NewGuid().ToString("N");
        string completedApply = null;
        Process attachedUi = null;
        bool stopping = false;
        TaskCompletionSource screenshotPrepared = null;
        int ticks = 0;
        var metricsClock = Stopwatch.StartNew();
        double workerCpuAt = 0, workerCpuSeconds = 0, workerCpuPercent = 0;
        double uiCpuAt = 0, uiCpuSeconds = 0, uiCpuPercent = 0;
        void Event(string kind, string message = null, bool success = false, string title = null, int notificationKind = 0, int duration = 0)
        {
            events.Enqueue(new WorkerEvent { Sequence = ++sequence, Kind = kind, Message = message, Success = success, Title = title, NotificationKind = notificationKind, Duration = duration });
            while (events.Count > 64) events.Dequeue();
        }
        bool UiAlive()
        {
            try { return attachedUi != null && !attachedUi.HasExited; } catch (InvalidOperationException) { return false; }
        }
        void UiAction(string kind, string argument)
        {
            if (UiAlive()) { Event(kind); return; }
            var info = new ProcessStartInfo(uiExecutable ?? Environment.ProcessPath) { UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add("--data-dir"); info.ArgumentList.Add(data); info.ArgumentList.Add(argument);
            try { using var child = Process.Start(info); } catch (Exception ex) { Log.Write("UI could not open: " + ex.Message); }
        }
        void PersistPause(bool userStopped = false)
        {
            try
            {
                host.PersistState(userStopped);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Write("Pause state could not persist: " + ex.Message);
                Event("notification", "The current recording state could not be saved for the next launch.", title: "Recording state not persisted", notificationKind: (int)NotificationKind.Warning, duration: 6000);
            }
        }
        WorkerStatus Snapshot(long after = -1)
        {
            var metrics = host.Metrics;
            metrics.WorkerProcessId = Environment.ProcessId;
            double now = metricsClock.Elapsed.TotalSeconds;
            using (var process = Process.GetCurrentProcess())
            {
                metrics.WorkerWorkingSetBytes = process.WorkingSet64;
                if (now - workerCpuAt >= 1)
                {
                    double seconds = process.TotalProcessorTime.TotalSeconds;
                    workerCpuPercent = (seconds - workerCpuSeconds) / (now - workerCpuAt) / Environment.ProcessorCount * 100;
                    workerCpuAt = now; workerCpuSeconds = seconds;
                }
                metrics.WorkerCpuPercent = workerCpuPercent;
            }
            if (UiAlive())
            {
                try
                {
                    attachedUi.Refresh(); metrics.UiWorkingSetBytes = attachedUi.WorkingSet64;
                    if (now - uiCpuAt >= 1)
                    {
                        double seconds = attachedUi.TotalProcessorTime.TotalSeconds;
                        uiCpuPercent = (seconds - uiCpuSeconds) / (now - uiCpuAt) / Environment.ProcessorCount * 100;
                        uiCpuAt = now; uiCpuSeconds = seconds;
                    }
                    metrics.UiCpuPercent = uiCpuPercent;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
            return new WorkerStatus { Instance = instance, CompletedApply = completedApply, EngineCapabilityContext = host.EngineCapabilityContext, Revision = ++revision, Config = host.Config, ProfileStatus = host.ProfileStatus, Paused = host.Paused, Ready = host.Ready && metrics.CaptureProcessId != 0, Phase = host.Phase, CanSave = host.CanSave, Applying = host.Applying, Saving = host.Saving, ScreenshotInProgress = host.ScreenshotInProgress,
                SaveRegistered = host.SaveRegistered, OverlayRegistered = host.OverlayRegistered, ScreenshotRegistered = host.ScreenshotRegistered,
                State = host.State, LastScreenshot = host.LastScreenshot, ApplicationName = host.ApplicationName, WorkerProcessId = Environment.ProcessId,
                Sequence = sequence, Metrics = metrics, Stopping = stopping, Events = after < 0 ? [] : events.Where(e => e.Sequence > after).Take(16).ToArray() };
        }
        void Stop()
        {
            if (stopping) return;
            try { host.StopAsync().GetAwaiter().GetResult(); stopping = true; PersistPause(true); loop.Stop(); }
            catch (Exception ex) { Log.Write(ex.ToString()); }
        }
        host.OpenSettingsRequested += () => UiAction("settings", "--settings");
        host.OverlayRequested += () => UiAction("overlay", "--overlay");
        host.QuitRequested += () => { if (UiAlive()) Event("quit"); else Stop(); };
        host.Saved += (message, success) => Event("saved", message, success);
        host.Notification += (title, message, kind, duration) => Event("notification", message, false, title, (int)kind, duration);
        host.PrepareRemoteScreenshot = async () =>
        {
            if (!UiAlive()) return;
            screenshotPrepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Event("prepareScreenshot");
            try { await screenshotPrepared.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
            finally { screenshotPrepared = null; }
        };
        async Task<WorkerReply> Handle(WorkerRequest request, int clientPid)
        {
            string error = null;
            RecordingEngineCapabilities capabilities = null;
            try
            {
                if (request.Version != WorkerProtocol.Version) throw new InvalidDataException("Unsupported recorder protocol version.");
                if (request.AttachUi)
                {
                    if (attachedUi == null || attachedUi.Id != clientPid || !UiAlive())
                    {
                        attachedUi?.Dispose(); attachedUi = Process.GetProcessById(clientPid);
                        uiCpuAt = metricsClock.Elapsed.TotalSeconds; uiCpuSeconds = attachedUi.TotalProcessorTime.TotalSeconds; uiCpuPercent = 0;
                        host.ExcludeUiProcess(clientPid);
                    }
                }
                if (stopping && request.Command != "status") throw new InvalidOperationException("Recorder is stopping.");
                if (host.Applying && request.Command is "save" or "pause" or "restart" or "apply" or "repairMedia" or "probeMedia" or "screenshot" or "quit") throw new InvalidOperationException("Wait for the current settings change.");
                switch (request.Command)
                {
                    case "status": break;
                    case "save": host.SaveReplay(); break;
                    case "pause": host.Pause(request.Value); PersistPause(); break;
                    case "restart": host.Restart(); break;
                    case "apply": await host.ApplyAsync(request.Settings ?? throw new ArgumentException("Capture settings are required."), request.Value); break;
                    case "repairMedia": await host.RepairMediaAsync(); break;
                    case "probeMedia": capabilities = await host.ProbeMediaAsync(); break;
                    case "screenshot": host.TakeScreenshot(); break;
                    case "screenshotPrepared": screenshotPrepared?.TrySetResult(); break;
                    case "quit": await host.StopAsync(); stopping = true; PersistPause(true); break;
                    default: throw new ArgumentException("Unknown recorder command.");
                }
            }
            catch (Exception ex) { error = ex.Message; Log.Write(ex.ToString()); }
            if (request.Command is "apply" or "repairMedia") completedApply = request.OperationId;
            return new WorkerReply { Error = error, Status = Snapshot(request.AfterEvent), Capabilities = capabilities };
        }
        loop.Tick = () =>
        {
            host.SampleForeground();
            if (++ticks % 4 != 0) return;
            try
            {
                string status = Path.Combine(data, "worker-status.json");
                File.WriteAllText(status + ".tmp", JsonSerializer.Serialize(Snapshot(), WorkerProtocol.Json));
                File.Move(status + ".tmp", status, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Write("Worker status could not update: " + ex.Message); }
        };
        async Task Serve(NamedPipeServerStream pipe)
        {
            bool stopAfterReply = false;
            using (pipe)
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    var request = await WorkerProtocol.ReadAsync<WorkerRequest>(pipe, deadline.Token).ConfigureAwait(false);
                    if (request.Command is "apply" or "quit" or "probeMedia") deadline.CancelAfter(TimeSpan.FromSeconds(40));
                    if (request.Command == "repairMedia") deadline.CancelAfter(TimeSpan.FromSeconds(180));
                    if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out uint pid)) throw new IOException("Recorder client identity unavailable.");
                    var reply = await loop.InvokeAsync(() => Handle(request, checked((int)pid))).WaitAsync(deadline.Token).ConfigureAwait(false);
                    stopAfterReply = request.Command == "quit" && reply.Error == null;
                    await WorkerProtocol.WriteAsync(pipe, reply, deadline.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException or ArgumentException) { Log.Write("Recorder connection ended: " + ex.Message); }
                finally { if (stopAfterReply) loop.Stop(); }
            }
        }
        async Task Accept()
        {
            var pending = new List<Task>();
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    pending.RemoveAll(t => t.IsCompleted);
                    if (pending.Count >= 8) { await Task.WhenAny(pending).ConfigureAwait(false); continue; }
                    var pipe = new NamedPipeServerStream(WorkerIdentity.PipeName(data), PipeDirection.InOut, 8, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 8192, 8192);
                    try { await pipe.WaitForConnectionAsync(lifetime.Token).ConfigureAwait(false); }
                    catch { pipe.Dispose(); throw; }
                    pending.Add(Serve(pipe));
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception ex) { Log.Write(ex.ToString()); loop.Stop(); }
            finally { await Task.WhenAll(pending).ConfigureAwait(false); }
        }
        var server = Accept();
        try { loop.Run(); return 0; }
        finally { lifetime.Cancel(); attachedUi?.Dispose(); try { server.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException ex) { Log.Write(ex.ToString()); } }
    }
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint processId);
}
