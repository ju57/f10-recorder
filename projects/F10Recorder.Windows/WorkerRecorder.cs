namespace F10Recorder;

internal sealed class WorkerRecorder : IWindowsRecorder
{
    readonly Action<Action> post;
    readonly HotkeyWindow uiControl;
    readonly CancellationTokenSource lifetime = new();
    readonly HashSet<string> retiredInstances = [];
    WorkerStatus status;
    long sequence;
    bool disposed, stopping, applying;
    public WorkerRecorder(string data, Action<Action> dispatch, bool paused, string controlTitle)
    {
        DataDirectory = WorkerIdentity.DataPath(data); post = dispatch;
        var reply = WorkerProtocol.EnsureWorker(DataDirectory, paused);
        status = reply.Status ?? throw new IOException(reply.Error ?? "Recorder did not return status."); sequence = status.Sequence;
        uiControl = new HotkeyWindow(controlTitle + ".UI");
        uiControl.CommandRequested += command => { switch (command) { case 1: OpenSettingsRequested?.Invoke(); break; case 2: Pause(true); break; case 3: Pause(false); break; case 4: QuitRequested?.Invoke(); break; case 5: OverlayRequested?.Invoke(); break; case 6: TakeScreenshot(); break; } };
        _ = Poll();
    }
    public Settings Config => status.Config;
    public string DataDirectory { get; }
    public bool Paused => status.Paused;
    public bool Ready => status.Ready;
    public bool Applying => applying || status.Applying;
    public bool ScreenshotInProgress => status.ScreenshotInProgress;
    public string LastScreenshot => status.LastScreenshot;
    public string State => status.State;
    public bool SaveRegistered => status.SaveRegistered;
    public bool OverlayRegistered => status.OverlayRegistered;
    public bool ScreenshotRegistered => status.ScreenshotRegistered;
    public string ApplicationName => status.ApplicationName;
    public int CaptureProcessId => status.Metrics?.CaptureProcessId ?? 0;
    public RecorderMetrics Metrics => status.Metrics;
    public event Action Changed, OpenSettingsRequested, OverlayRequested, QuitRequested, PreparingScreenshot;
    public event Action<string, string, NotificationKind, int> Notification;
    public event Action<string, bool> Saved;
    void Update(WorkerStatus next)
    {
        if (disposed || next == null) return;
        if (retiredInstances.Contains(next.Instance)) return;
        if (next.Instance != status.Instance) { retiredInstances.Add(status.Instance); status = next; sequence = next.Sequence; }
        else if (next.Revision >= status.Revision) status = next;
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
                post(() => { if (!disposed) { status.Ready = false; status.State = "Recorder unavailable · reconnecting…"; Changed?.Invoke(); } });
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
        var reply = await WorkerProtocol.CallAsync(DataDirectory, request, lifetime.Token);
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
        if (Applying) throw new InvalidOperationException("Wait for the current settings change.");
        applying = true; Changed?.Invoke();
        try { await Command(new WorkerRequest { Command = "apply", Settings = next, Value = startup }); }
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
    public void Dispose() { if (disposed) return; disposed = true; lifetime.Cancel(); uiControl.Dispose(); }
}
