using Microsoft.Win32;
using System.Diagnostics;

namespace F10Recorder;

// All members except encoder callbacks run on the host's owner thread.
// NativeWindow is used only for its Win32 message dispatch, never visual controls.
internal sealed class RecorderHost : IWindowsRecorder
{
    readonly RecorderDispatch owner;
    readonly string settingsPath;
    readonly bool synthetic;
    readonly Func<int, string, string> captureScreenshot;
    readonly Action<bool> startupWriter;
    readonly bool customStartupWriter;
    readonly Func<int> repairMedia;
    readonly HotkeyWindow shortcuts;
    readonly ForegroundApplicationTracker foreground;
    readonly RecorderNotifications notices;
    readonly ReplayStorageCoordinator storage;
    ReplayEngine engine;
    RecordingProfile profile;
    WindowCaptureTarget windowTarget;
    bool disposed, ready;
    bool savedPauseIntent;
    bool storageWarningPresented, storageHoldPresented;
    public Settings Config { get; private set; }
    public ProfileStatus ProfileStatus => profile.Status;
    public string DataDirectory { get; }
    public bool Paused { get; private set; }
    public bool Ready { get => ready && !storage.Stopped; private set => ready = value; }
    public CapturePhase Phase { get; private set; } = CapturePhase.Starting;
    public bool CanSave => !Applying && !Paused && engine?.CanSave == true;
    public bool Applying { get; private set; }
    public bool CaptureModeKnown => !Applying && ProfileStatus.CanWrite && !ProfileStatus.RequiresCorrection;
    public bool ScreenshotInProgress { get; private set; }
    public string LastScreenshot { get; private set; }
    public string State { get; private set; } = "Starting replay buffer…";
    public bool SaveRegistered => shortcuts.SaveRegistered;
    public bool OverlayRegistered => shortcuts.OverlayRegistered;
    public bool ScreenshotRegistered => shortcuts.ScreenshotRegistered;
    public string ApplicationName => Config.CaptureMode == "window" ? Path.GetFileNameWithoutExtension(Config.WindowExecutable) : foreground.ApplicationName;
    public int CaptureProcessId => engine?.CaptureProcessId ?? 0;
    public RecorderMetrics Metrics
    {
        get { var result = engine?.Metrics ?? new RecorderMetrics(); storage.WriteMetrics(result); return result; }
    }
    public string EngineCapabilityContext { get; private set; }
    public bool Saving => engine?.IsSaving == true;
    internal Func<Task> PrepareRemoteScreenshot;
    internal void ExcludeUiProcess(int id) => foreground.ExcludeProcess(id);
    public event Action Changed;
    public event Action OpenSettingsRequested;
    public event Action OverlayRequested;
    public event Action QuitRequested;
    public event Action PreparingScreenshot;
    public event Action<string, string, NotificationKind, int> Notification;
    public event Action<string, bool> Saved;

    internal static void Initialize(string data)
    {
        Directory.CreateDirectory(data); Log.DirectoryPath = data;
    }

    public RecorderHost(string data, Action<Action> dispatch, bool paused, string controlTitle, bool syntheticSource = false, Action<bool> setStartup = null, Func<int, string, string> screenshotSource = null, Func<int> repairEngine = null)
    {
        data = WorkerIdentity.DataPath(data);
        Initialize(data); owner = new RecorderDispatch(dispatch); synthetic = syntheticSource; customStartupWriter = setStartup != null; startupWriter = setStartup ?? WindowsStartup.SetEnabled; captureScreenshot = screenshotSource ?? ScreenshotCapture.Capture;
        DataDirectory = data; settingsPath = Path.Combine(data, "settings.json"); profile = RecordingProfile.Load(data); Config = profile.Settings;
        storage = new ReplayStorageCoordinator(new ReplayStorageStopLatch(data),
            new ReplayVolumeReservations(ProfileOwnership.RootDirectory, ReplayStorageSafety.MinimumBytes));
        Paused = storage.Hold.Held || paused || profile.State.Paused || profile.State.UserStopped || ProfileStatus.ActivationRequired;
        savedPauseIntent = paused || profile.State.Paused;
        repairMedia = repairEngine ?? (() => MediaDependency.Install(DataDirectory, true));
        shortcuts = new HotkeyWindow(controlTitle); foreground = new ForegroundApplicationTracker();
        notices = new RecorderNotifications((title, message, kind, duration) => Notification?.Invoke(title, message, kind, duration));
        shortcuts.Pressed += () => ShortcutAction(SaveReplay); shortcuts.OverlayPressed += () => OverlayRequested?.Invoke(); shortcuts.ScreenshotPressed += TakeScreenshot;
        shortcuts.EnvironmentChanged += Restart;
        shortcuts.CommandRequested += command => ShortcutAction(() => { switch (command) { case 1: OpenSettingsRequested?.Invoke(); break; case 2: Pause(true); break; case 3: Pause(false); break; case 4: QuitRequested?.Invoke(); break; case 5: OverlayRequested?.Invoke(); break; case 6: TakeScreenshot(); break; } });
        if (ProfileStatus.CanWrite && !ProfileStatus.RequiresCorrection) Register(Config);
        StartEngine();
    }
    void ShortcutAction(Action action)
    {
        try { action(); }
        catch (Exception ex) { Notification?.Invoke("Recording profile needs attention", ex.Message, NotificationKind.Warning, 6000); Log.Write(ex.Message); }
    }
    void Register(Settings settings)
    {
        try { shortcuts.Register(settings); }
        catch (Exception ex) { State = ex.Message; Log.Write(ex.Message); }
    }
    void StartEngine(bool explicitStorageResume = false)
    {
        var Dispatch = owner.NextGeneration();
        EngineCapabilityContext = Guid.NewGuid().ToString("N");
        windowTarget?.Dispose(); windowTarget = null;
        var hold = storage.Hold;
        if (!explicitStorageResume && hold.Held) Paused = true;
        if (ProfileStatus.ActivationRequired || Paused)
        {
            engine = null; Paused = true; Ready = false; Phase = CapturePhase.Paused; State = hold.Held ? "Storage safety hold: " + hold.Reason : ProfileStatus.ActivationRequired ? ProfileStatus.Message : "Paused";
            Changed?.Invoke(); return;
        }
        try { storage.RequireAdmission(ReplayCacheLocations.Current(Config, DataDirectory), Config.OutputDirectory, Config); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or OverflowException)
        {
            storage.StopForStorageFailure(error.Message);
            Paused = true; Ready = false; Phase = CapturePhase.Paused; State = "Storage safety hold: " + error.Message;
            Changed?.Invoke(); return;
        }
        if (Config.CaptureMode == "window" && !synthetic) windowTarget = new WindowCaptureTarget(Config);
        engine = new ReplayEngine(Config, DataDirectory, Paused, synthetic, windowTarget == null ? null : windowTarget.Read, storage, explicitStorageResume);
        var current = engine;
        current.StorageWarning += reason => Dispatch(() =>
        {
            if (!storageWarningPresented) Notification?.Invoke("Replay storage is low", reason, NotificationKind.Warning, 6000);
            storageWarningPresented = true;
        });
        current.CaptureStatusChanged += () => Dispatch(() => PresentCaptureStatus(current.ReadCaptureStatus()));
        PresentCaptureStatus(current.ReadCaptureStatus());
        engine.SaveStarted += () => Dispatch(notices.SaveStarted);
        engine.SaveFinished += (message, success) => Dispatch(() => { notices.SaveFinished(message, success); Saved?.Invoke(message, success); Changed?.Invoke(); });
    }
    void PresentCaptureStatus(CaptureStatus status)
        => PresentCaptureStatus(status, Metrics, engine?.StorageHeld == true, engine?.Paused ?? Paused);
    internal void PresentCaptureStatus(CaptureStatus status, RecorderMetrics metrics, bool storageHeld, bool capturePaused)
    {
        State = status.Ready && !shortcuts.SaveRegistered ? "Buffering · save shortcut unavailable" : status.Message;
        Phase = status.Phase; Ready = status.Ready;
        if (metrics.ReplayStorageSafetyWarning && !storageWarningPresented)
            Notification?.Invoke("Replay storage is low", "Unreserved replay storage is below five decimal GB.", NotificationKind.Warning, 6000);
        storageWarningPresented = metrics.ReplayStorageSafetyWarning;
        if (storageHeld)
        {
            // The previous guard remains durable until the replacement arms.
            // Preserve the engine's explicit Resume intent during that interval;
            // otherwise a later Pause is skipped as already satisfied.
            Paused = capturePaused; Ready = false; Phase = CapturePhase.Paused;
            State = "Storage safety hold: " + metrics.ReplayStorageSafetyReason;
            if (!storageHoldPresented) Notification?.Invoke("Replay recording paused for storage", metrics.ReplayStorageSafetyReason, NotificationKind.Warning, 0);
            storageHoldPresented = true;
        }
        else storageHoldPresented = false;
        notices.CaptureChanged(status.Phase, status.Message, Ready, Paused, Config, shortcuts.SaveRegistered);
        Changed?.Invoke();
    }
    public void SampleForeground() => foreground.Sample();
    public void SaveReplay() { if (!Applying && !disposed) { if (engine == null) throw new InvalidOperationException(ProfileStatus.Message); engine.Save(Config.CaptureMode == "window" ? ApplicationName : foreground.Sample()); } }
    public void Pause(bool value)
    {
        if (Applying || disposed) return;
        bool held = storage.Hold.Held;
        if (!value && (Paused || held))
        {
            if (Saving || ScreenshotInProgress) throw new IOException("Wait for the accepted replay or screenshot to finish saving.");
            engine?.RequireResumeReady();
            storage.RequireAdmission(ReplayCacheLocations.Current(Config, DataDirectory), Config.OutputDirectory, Config);
        }
        if (!value && held)
        {
            if (engine != null) engine.RequireStorageResume();
            else
            {
                storage.RequireResume(ReplayCacheLocations.Current(Config, DataDirectory), Config.OutputDirectory);
            }
        }
        if (value) savedPauseIntent = true;
        if (Paused == value && !held) return;
        if (!value && ProfileStatus.ActivationRequired)
        {
            if (!ProfileStatus.CanWrite || ProfileStatus.RequiresCorrection) throw new InvalidOperationException(ProfileStatus.Message);
            try { profile.Commit(Config, paused: false, userStopped: false, activationRequired: false); }
            catch { profile = RecordingProfile.Load(DataDirectory); State = ProfileStatus.Message; Changed?.Invoke(); throw; }
            Config = profile.Settings; Paused = false; StartEngine(held);
        }
        else
        {
            Paused = value;
            if (engine == null && !value) StartEngine(held);
            else engine?.Pause(value);
        }
        savedPauseIntent = value;
        if (engine != null) PresentCaptureStatus(engine.ReadCaptureStatus());
        else Changed?.Invoke();
        notices.PauseChanged(value);
    }
    public void Restart()
    {
        if (Applying || disposed) return;
        if (engine == null) return;
        engine.Restart(); PresentCaptureStatus(engine.ReadCaptureStatus());
    }
    internal void PersistState(bool userStopped) { if (!ProfileStatus.ActivationRequired) profile.PersistState(savedPauseIntent, userStopped || Paused && profile.State.UserStopped); }
    public async Task ApplyAsync(Settings next, bool startup)
    {
        if (Applying || disposed) throw new InvalidOperationException("Wait for the current settings change.");
        if (Saving || ScreenshotInProgress) throw new InvalidOperationException("Wait for the current replay or screenshot to finish saving.");
        if (!ProfileStatus.CanWrite) throw new InvalidOperationException(ProfileStatus.Message);
        next.Validate();
        using var cacheSelection = ReplayCacheLocations.PinSelection(next);
        if (next.ReplayCacheBudgetBytes < Config.ReplayCacheBudgetBytes || !string.Equals(ReplayCacheLocations.Current(next, DataDirectory), ReplayCacheLocations.Current(Config, DataDirectory), StringComparison.OrdinalIgnoreCase))
            ValidateBudgetReduction(next.ReplayCacheBudgetBytes, next);
        if (!Paused) storage.RequireAdmission(ReplayCacheLocations.Current(next, DataDirectory), next.OutputDirectory, next);
        // Reject an unavailable/untrusted engine before changing shortcuts or stopping capture.
        // Every subsequent process launch still verifies and leases the executable again.
        MediaDependency.ResolveExecutable();
        try { shortcuts.Register(next); }
        catch { Register(Config); throw; }
        Applying = true; Changed?.Invoke();
        try
        {
            bool previousStartup = WindowsStartup.Enabled;
            // Stop the old capture before replacing it, preserving the pause state.
            if (engine != null) await Task.Run(engine.Dispose);
            bool settingsCommitted = false;
            try
            {
                // Current UI editors know represented fields only. Preserve opaque future
                // preferences/device identities rather than dropping them on correction.
                next.AdditionalPreferences = new(profile.Settings.AdditionalPreferences);
                ReplayCacheLocations.PreservePrevious(next, Config, DataDirectory);
                if (ProfileStatus.ActivationRequired) profile.Commit(next, paused: profile.State.Paused, userStopped: profile.State.UserStopped, activationRequired: true);
                else next.Save(settingsPath);
                settingsCommitted = true;
                if (customStartupWriter || startup != previousStartup) startupWriter(startup);
                Config = next; profile = RecordingProfile.Load(DataDirectory);
            }
            catch
            {
                // A startup-write failure must not leave disk and live configuration different.
                profile = RecordingProfile.Load(DataDirectory);
                // A failed pre-commit check must not rewrite the original profile
                // using salvaged/defaulted in-memory values as a "rollback".
                if (settingsCommitted && ProfileStatus.CanWrite) Config.Save(settingsPath);
                if (WindowsStartup.Enabled != previousStartup) startupWriter(previousStartup);
                throw;
            }
            finally { StartEngine(); }
        }
        finally { Applying = false; Register(Config); Changed?.Invoke(); }
    }
    void ValidateBudgetReduction(long proposedBudgetBytes, Settings next)
    {
        try
        {
            if (engine != null) engine.ValidateReplayCacheBudget(proposedBudgetBytes);
            // Opaque history is worker-owned; a client supplies only the newly
            // selected represented folder, never extra roots to inspect.
            using var retained = new ReplayCache(retainedDirectories: ReplayCacheLocations.Roots(Config, DataDirectory).Append(ReplayCacheLocations.Current(next, DataDirectory)));
            retained.ValidateBudget(proposedBudgetBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or OverflowException)
        { throw new IOException("Replay-cache budget reduction could not be validated: " + ex.Message, ex); }
    }
    public async Task RepairMediaAsync()
    {
        if (Applying || disposed || Saving || ScreenshotInProgress) throw new InvalidOperationException("Wait for settings or saving to finish before repairing the recording engine.");
        Applying = true; Ready = false; Phase = CapturePhase.Recovering; State = "Repairing recording engine…"; Changed?.Invoke();
        try
        {
            if (engine != null) await Task.Run(engine.Dispose);
            try
            {
                if (await Task.Run(repairMedia) != 0)
                    throw new IOException("Recording engine repair failed. Check the connection and retry. Details are in media-install-error.txt.");
                MediaDependency.ResolveExecutable();
                Phase = Paused ? CapturePhase.Paused : CapturePhase.Starting;
                State = Paused ? "Paused" : "Starting replay buffer…";
            }
            catch (Exception ex) { Phase = CapturePhase.Recovering; State = ex.Message; throw; }
            finally { StartEngine(); }
        }
        finally { Applying = false; Changed?.Invoke(); }
    }
    public async Task<RecordingEngineCapabilities> ProbeMediaAsync()
    {
        if (Applying || disposed) throw new InvalidOperationException("Wait for the current recording engine operation.");
        string current = EngineCapabilityContext;
        var receipt = await Task.Run(EncoderCapabilities.ProbeManaged);
        if (disposed || Applying || current != EngineCapabilityContext) throw new IOException("Recording engine changed during the check. Check again.");
        return receipt;
    }
    public async void TakeScreenshot()
    {
        if (disposed || ScreenshotInProgress || Applying) return;
        if (ProfileStatus.ActivationRequired) { Notification?.Invoke("Recording profile needs attention", ProfileStatus.Message, NotificationKind.Warning, 6000); return; }
        ScreenshotInProgress = true; PreparingScreenshot?.Invoke(); Changed?.Invoke();
        int display = Config.DisplayIndex; string folder = Config.ScreenshotDirectory;
        try
        {
            // A paused compatible profile still supports screenshots without
            // constructing a replay engine or clearing its retained inputs.
            if (Config.CaptureMode == "window" && !synthetic) windowTarget ??= new WindowCaptureTarget(Config);
            var selected = Config.CaptureMode == "window" ? windowTarget?.ReadForScreenshot() : null;
            if (PrepareRemoteScreenshot != null) await PrepareRemoteScreenshot();
            await Task.Delay(150);
            string file = await Task.Run(() =>
            {
                if (Config.CaptureMode != "window") return captureScreenshot(display, folder);
                var verified = windowTarget?.ReadForScreenshot();
                if (!ScreenshotCapture.SameTarget(selected, verified)) throw new IOException("No screenshot was saved. " + (selected != null && !selected.Available ? selected.Reason : verified != null && !verified.Available ? verified.Reason : "The selected window changed."));
                return ScreenshotCapture.CaptureSource(CaptureSession.WindowVideoSource(verified, 1).Replace(":capture_cursor=1", ":capture_cursor=0"), folder, true,
                    () => !disposed && ScreenshotCapture.SameTarget(verified, windowTarget?.ReadForScreenshot()));
            });
            if (!disposed) { LastScreenshot = file; Notification?.Invoke("Screenshot saved", Path.GetFileName(file), NotificationKind.Success, 4000); }
        }
        catch (Exception ex) { Log.Write(ex.ToString()); if (!disposed) Notification?.Invoke("Screenshot not saved", ex.Message, NotificationKind.Warning, 6000); }
        finally { ScreenshotInProgress = false; if (!disposed) Changed?.Invoke(); }
    }
    public static void OpenFolder(string path) { Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
    public void Dispose()
    {
        if (disposed) return; disposed = true; owner.Dispose();
        shortcuts.Dispose(); foreground.Dispose(); engine?.Dispose(); storage.Dispose(); windowTarget?.Dispose();
    }
    public Task StopAsync()
    {
        if (!disposed && (Applying || Saving || ScreenshotInProgress)) throw new InvalidOperationException("Wait for settings, replay or screenshot saving to finish before quitting.");
        Dispose(); return Task.CompletedTask;
    }
}

internal static class WindowsStartup
{
    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static bool Enabled
    {
        get
        {
            try { using var key = Registry.CurrentUser.OpenSubKey(Key); return key?.GetValue("F10Recorder") != null; }
            catch (System.Security.SecurityException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
    }
    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Key);
        if (enabled) key.SetValue("F10Recorder", CaptureSession.Quote(Environment.ProcessPath), RegistryValueKind.String);
        else key.DeleteValue("F10Recorder", false);
    }
}
