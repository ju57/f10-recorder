namespace F10Recorder;

internal sealed class WorkerRecorder : IWindowsRecorder
{
    readonly Action<Action> post;
    readonly HotkeyWindow uiControl;
    readonly CancellationTokenSource lifetime = new();
    readonly HashSet<string> retiredInstances = [];
    readonly Func<WorkerRequest, Task<WorkerReply>> send;
    WorkerStatus status;
    long sequence;
    long capabilityRevision;
    bool disposed, stopping, applying;
    bool modeKnown = true;
    string pendingApply, applyInstance;
    public WorkerRecorder(string data, Action<Action> dispatch, bool paused, string controlTitle)
    {
        DataDirectory = WorkerIdentity.DataPath(data); post = dispatch;
        var reply = WorkerProtocol.EnsureWorker(DataDirectory, paused);
        status = reply.Status ?? throw new IOException(reply.Error ?? "Recorder did not return status."); sequence = status.Sequence;
        uiControl = new HotkeyWindow(controlTitle + ".UI");
        uiControl.CommandRequested += command => { switch (command) { case 1: OpenSettingsRequested?.Invoke(); break; case 2: Pause(true); break; case 3: Pause(false); break; case 4: QuitRequested?.Invoke(); break; case 5: OverlayRequested?.Invoke(); break; case 6: TakeScreenshot(); break; } };
        _ = Poll();
    }
    // Headless transport checks exercise the production acknowledgement policy
    // without constructing native windows or starting a worker.
    internal WorkerRecorder(WorkerStatus initial, Func<WorkerRequest, Task<WorkerReply>> transport, string dataDirectory = null)
    {
        status = initial; send = transport; post = action => action(); DataDirectory = dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "F10Recorder");
    }
    public Settings Config => status.Config;
    public ProfileStatus ProfileStatus => status.ProfileStatus;
    public string DataDirectory { get; }
    public bool Paused => status.Paused;
    public bool Ready => status.Ready;
    public CapturePhase Phase => status.Phase;
    public bool CanSave => status.CanSave;
    public bool Saving => status.Saving;
    public bool Applying => applying || status.Applying;
    public bool CaptureModeKnown => modeKnown && !Applying;
    public bool ScreenshotInProgress => status.ScreenshotInProgress;
    public string LastScreenshot => status.LastScreenshot;
    public string State => status.State;
    public bool SaveRegistered => status.SaveRegistered;
    public bool OverlayRegistered => status.OverlayRegistered;
    public bool ScreenshotRegistered => status.ScreenshotRegistered;
    public string ApplicationName => status.ApplicationName;
    public int CaptureProcessId => status.Metrics?.CaptureProcessId ?? 0;
    public RecorderMetrics Metrics => status.Metrics;
    public string EngineCapabilityContext => status.Instance + ":" + status.EngineCapabilityContext;
    public event Action Changed, OpenSettingsRequested, OverlayRequested, QuitRequested, PreparingScreenshot;
    public event Action<string, string, NotificationKind, int> Notification;
    public event Action<string, bool> Saved;
    internal void Update(WorkerStatus next)
    {
        if (disposed || next == null) return;
        if (retiredInstances.Contains(next.Instance)) return;
        string previousContext = EngineCapabilityContext;
        if (next.Instance != status.Instance) { retiredInstances.Add(status.Instance); status = next; sequence = next.Sequence; }
        else if (next.Revision >= status.Revision) status = next;
        else return;
        if (previousContext != EngineCapabilityContext || status.Applying || status.Phase is CapturePhase.Recovering or CapturePhase.Reconnecting or CapturePhase.Unknown) capabilityRevision++;
        if (!status.Applying && pendingApply != null && (status.CompletedApply == pendingApply || status.Instance != applyInstance))
        { modeKnown = true; pendingApply = null; }
        if (pendingApply == null) modeKnown = !status.Applying;
        foreach (var item in next.Events)
        {
            if (item.Sequence <= sequence) continue;
            sequence = item.Sequence;
            switch (item.Kind)
            {
                case "settings": OpenSettingsRequested?.Invoke(); break;
                case "overlay": OverlayRequested?.Invoke(); break;
                case "quit": QuitRequested?.Invoke(); break;
                case "prepareScreenshot": PreparingScreenshot?.Invoke(); Fire("screenshotPrepared"); break;
                case "notification": Notification?.Invoke(item.Title, item.Message, (NotificationKind)item.NotificationKind, item.Duration); break;
                case "saved": Saved?.Invoke(item.Message, item.Success); break;
            }
        }
        Changed?.Invoke();
    }
    async Task Poll()
    {
        while (!lifetime.IsCancellationRequested)
        {
            try
            {
                var reply = await WorkerProtocol.CallAsync(DataDirectory, new WorkerRequest { AttachUi = true, AfterEvent = sequence }, lifetime.Token).ConfigureAwait(false);
                post(() => Update(reply.Status));
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
                if (lifetime.IsCancellationRequested) break;
                Log.Write("Recorder connection unavailable: " + ex.Message);
                post(() => { if (!disposed) { capabilityRevision++; modeKnown = false; status.Ready = false; status.CanSave = false; status.Phase = CapturePhase.Reconnecting; status.State = "Recorder unavailable · reconnecting…"; Changed?.Invoke(); } });
                if (ex is WorkerProtocolMismatchException)
                {
                    post(() => { if (!disposed) { status.State = ex.Message; Notification?.Invoke("Recorder build mismatch", ex.Message, NotificationKind.Warning, 6000); Changed?.Invoke(); } });
                    break;
                }
                if (!stopping && WorkerProtocol.WasStopped(DataDirectory))
                {
                    stopping = true; post(() => QuitRequested?.Invoke()); break;
                }
                if (!stopping)
                {
                    try { var reply = WorkerProtocol.EnsureWorker(DataDirectory, Paused); post(() => Update(reply.Status)); }
                    catch (Exception retry) { Log.Write("Recorder reconnect failed: " + retry.Message); }
                }
            }
            try { await Task.Delay(250, lifetime.Token).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
        }
    }
    async Task Command(WorkerRequest request)
    {
        var reply = await (send == null ? WorkerProtocol.CallAsync(DataDirectory, request, lifetime.Token) : send(request));
        Update(reply.Status);
        if (reply.Error != null) throw new InvalidOperationException(reply.Error);
    }
    async void Fire(string command, bool value = false)
    {
        if (disposed || stopping) return;
        try { await Command(new WorkerRequest { Command = command, Value = value }); }
        catch (Exception ex) { if (!disposed) Notification?.Invoke("Recorder command failed", ex.Message, NotificationKind.Warning, 6000); Log.Write(ex.ToString()); }
    }
    public void SampleForeground() { }
    public void SaveReplay() { if (!Applying) Fire("save"); }
    public void Pause(bool value) { if (!Applying) Fire("pause", value); }
    public void Restart() { if (!Applying) Fire("restart"); }
    public void TakeScreenshot() { if (!Applying && !ScreenshotInProgress) Fire("screenshot"); }
    public async Task ApplyAsync(Settings next, bool startup)
    {
        await ApplyCommand(new WorkerRequest { Command = "apply", Settings = next, Value = startup });
    }
    public Task RepairMediaAsync() => ApplyCommand(new WorkerRequest { Command = "repairMedia" });
    public async Task<RecordingEngineCapabilities> ProbeMediaAsync()
    {
        long revision = capabilityRevision;
        string instance = status.Instance, context = status.EngineCapabilityContext;
        var request = new WorkerRequest { Command = "probeMedia" };
        var reply = await (send == null ? WorkerProtocol.CallAsync(DataDirectory, request, lifetime.Token) : send(request));
        if (reply.Error != null) throw new InvalidOperationException(reply.Error);
        if (revision != capabilityRevision || status.Instance != instance || status.EngineCapabilityContext != context ||
            reply.Status == null || reply.Status.Instance != instance || reply.Status.EngineCapabilityContext != context)
            throw new IOException("Recorder changed during the engine check. Check again.");
        Update(reply.Status);
        if (revision != capabilityRevision || status.Applying || status.Phase is CapturePhase.Recovering or CapturePhase.Reconnecting or CapturePhase.Unknown)
            throw new IOException("Recorder became unavailable during the engine check. Check again.");
        return reply.Capabilities ?? throw new IOException("Recorder did not return recording engine capabilities. Reconnect to the current build.");
    }
    async Task ApplyCommand(WorkerRequest request)
    {
        if (Applying) throw new InvalidOperationException("Wait for the current settings change.");
        request.OperationId = Guid.NewGuid().ToString("N"); pendingApply = request.OperationId; applyInstance = status.Instance; modeKnown = false;
        string instance = status.Instance; long revision = status.Revision;
        applying = true; Changed?.Invoke();
        try
        {
            var reply = await (send == null ? WorkerProtocol.CallAsync(DataDirectory, request, lifetime.Token) : send(request));
            // A completion from before this operation cannot release its privacy
            // hold. Newer polls and replacement-worker status keep normal ordering.
            if (reply.Status != null && (reply.Status.Instance != instance || reply.Status.Revision > revision)) Update(reply.Status);
            if (reply.Error != null) throw new InvalidOperationException(reply.Error);
            if (reply.Status == null || reply.Status.Instance != instance || reply.Status.Revision <= revision ||
                reply.Status.CompletedApply != request.OperationId || reply.Status.Applying ||
                status.Instance != instance || status.CompletedApply != request.OperationId || status.Applying)
                throw new IOException("The recorder did not confirm this settings change. Your edits are retained; reconnect and check the applied settings before retrying.");
        }
        finally { applying = false; Changed?.Invoke(); }
    }
    public async Task StopAsync()
    {
        stopping = true;
        try
        {
            using var worker = System.Diagnostics.Process.GetProcessById(status.WorkerProcessId);
            if (worker.HasExited) return;
            await Command(new WorkerRequest { Command = "quit" });
        }
        catch (ArgumentException) { }
        catch { stopping = false; throw; }
    }
    // UI shutdown detaches only. Explicit Quit separately acknowledges worker shutdown.
    public void Dispose() { if (disposed) return; disposed = true; lifetime.Cancel(); uiControl?.Dispose(); }
}
