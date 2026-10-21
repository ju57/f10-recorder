using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace F10Recorder
{
    internal static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--dependency-marker") { File.WriteAllText(args[1], "Unapproved engine executed"); return 0; }
            if (args.Length == 3 && args[0] == "--dependency-pipe")
            {
                using (var pipe = new System.IO.Pipes.NamedPipeClientStream(".", args[1], System.IO.Pipes.PipeDirection.In))
                { pipe.Connect(5000); File.WriteAllText(args[2], pipe.ReadByte().ToString()); return 0; }
            }
            RecorderRuntime.MediaEngineResolver = MediaDependency.ResolveExecutable;
            string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "F10Recorder");
            int index = Array.IndexOf(args, "--data-dir");
            if (index >= 0 && index + 1 < args.Length) data = Path.GetFullPath(args[index + 1]);
            Directory.CreateDirectory(data); data = Native.FinalDirectoryPath(data); Log.DirectoryPath = data;
            if (args.Contains("--ensure-media")) return MediaDependency.Install(data, !args.Contains("--no-download"));
            if (args.Contains("--dependency-test")) { int packageIndex = Array.IndexOf(args, "--package"); return DependencyVerification.Run(data, args.Contains("--download"), packageIndex >= 0 ? args[packageIndex + 1] : null); }
            if (args.Contains("--settings-test")) return Verification.SettingsWindow(data);
            if (args.Contains("--overlay-test")) return OverlayVerification.Run(data);
            if (args.Contains("--notification-test")) return NotificationVerification.Run(data);
            if (args.Contains("--screenshot-test")) return ScreenshotVerification.Run(data);
            if (args.Contains("--installed-screenshot-test")) return ScreenshotVerification.Installed(data);
            if (args.Contains("--application-folders-test")) return ApplicationFoldersVerification.Run(data);
            if (args.Contains("--installed-application-folders-test")) return ApplicationFoldersVerification.Installed(data);
            if (args.Contains("--application-fixture")) return ApplicationFoldersVerification.Fixture();
            if (args.Contains("--codec-test")) { int codecArgument = Array.IndexOf(args, "--codec"); return Verification.CodecMedia(data, codecArgument >= 0 && codecArgument + 1 < args.Length ? args[codecArgument + 1] : null); }
            if (args.Contains("--resilience-test")) return Verification.Resilience(data);
            if (args.Contains("--self-test") || args.Contains("--smoke-test")) return Verification.Run(data, args.Contains("--self-test"));
            using (var ownership = TrustedStorage.TryAcquireLock(TrustedStorage.DefaultRoot, "legacy-ui-" + Process.GetCurrentProcess().SessionId))
            {
                if (ownership == null)
                {
                    int command = args.Contains("--quit") ? 4 : args.Contains("--pause") ? 2 : args.Contains("--resume") ? 3 : args.Contains("--overlay") ? 5 : args.Contains("--screenshot") ? 6 : 1;
                    IntPtr window = Native.FindWindow(null, HotkeyWindow.ControlTitle);
                    return window != IntPtr.Zero && Native.PostMessage(window, HotkeyWindow.CommandMessage, new IntPtr(command), IntPtr.Zero) ? 0 : 1;
                }
                Native.SetProcessDPIAware();
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                try { using (TrustedStorage.AcquireLock(TrustedStorage.DefaultRoot, "profile", data, out data)) using (var app = new TrayApplication(data, args.Contains("--paused"), args.Contains("--settings"))) { if (args.Contains("--overlay")) app.ToggleOverlay(); if (args.Contains("--screenshot")) app.TakeScreenshot(); Application.Run(app); } }
                catch (Exception ex) { Log.Write(ex.ToString()); MessageBox.Show(ex.Message, "F10 Recorder", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
            }
            return 0;
        }
    }
    internal sealed class TrayApplication : ApplicationContext
    {
        readonly NotifyIcon tray;
        readonly HotkeyWindow hotkey = new HotkeyWindow();
        readonly Control dispatcher = new Control();
        readonly string data, configPath;
        readonly Icon readyIcon, warningIcon;
        readonly ToolStripMenuItem stateItem, pauseItem;
        readonly NotificationForm notification = new NotificationForm();
        readonly RecorderNotifications notifications;
        readonly ForegroundApplicationTracker foregroundApplication;
        ReplayEngine engine;
        Settings config;
        bool closing, paused, hotkeyAvailable;
        SettingsForm settingsForm;
        OverlayForm overlay;
        string captureState = "Starting replay buffer…";
        bool captureReady;
        bool takingScreenshot;
        string lastScreenshot;
        public TrayApplication(string dataDirectory, bool startPaused = false, bool showSettings = false)
        {
            data = dataDirectory; configPath = Path.Combine(data, "settings.json");
            paused = startPaused;
            config = Settings.Load(configPath);
            foregroundApplication = new ForegroundApplicationTracker();
            foregroundApplication.Changed += WriteStatus;
            notifications = new RecorderNotifications(Notify);
            notification.Changed += WriteStatus;
            dispatcher.CreateControl();
            readyIcon = MakeIcon(UiTheme.Accent); warningIcon = MakeIcon(UiTheme.Warning);
            tray = new NotifyIcon { Icon = warningIcon, Text = "F10 Recorder · starting", Visible = true };
            var menu = new ContextMenuStrip { Renderer = new DarkMenuRenderer(), BackColor = UiTheme.Surface, ForeColor = UiTheme.Text, Font = UiTheme.Font(10), ShowImageMargin = false, Padding = new Padding(6) };
            stateItem = new ToolStripMenuItem("Starting replay buffer…") { Enabled = false }; menu.Items.Add(stateItem);
            menu.Items.Add("Save replay", null, delegate { SaveReplay(); });
            menu.Items.Add("Take screenshot", null, delegate { TakeScreenshot(); });
            menu.Items.Add("Open screenshots", null, delegate { Directory.CreateDirectory(config.ScreenshotDirectory); Process.Start("explorer.exe", CaptureSession.Quote(config.ScreenshotDirectory)); });
            menu.Items.Add("Open clips", null, delegate { Directory.CreateDirectory(config.OutputDirectory); Process.Start("explorer.exe", CaptureSession.Quote(config.OutputDirectory)); });
            menu.Items.Add("Settings…", null, delegate { OpenSettings(); });
            menu.Items.Add("Open overlay", null, delegate { ToggleOverlay(); });
            pauseItem = new ToolStripMenuItem(paused ? "Resume recording" : "Pause recording"); pauseItem.Click += delegate { SetPaused(!paused); }; menu.Items.Add(pauseItem);
            menu.Items.Add("Restart buffer", null, delegate { engine.Restart(); });
            menu.Items.Add("Repair recording engine", null, async delegate
            {
                if (engine.IsSaving || takingScreenshot) { Notify("Repair unavailable", "Wait for saving to finish.", NotificationKind.Warning, 6000); return; }
                int result = await Task.Run(delegate { return MediaDependency.Install(data, true); });
                if (result == 0) { engine.Restart(); Notify("Recording engine ready", "The approved recording engine is available.", NotificationKind.Success, 4000); }
                else Notify("Recording engine repair failed", "Check the connection and retry. Details are in media-install-error.txt.", NotificationKind.Warning, 6000);
            });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Quit", null, delegate { ExitThread(); });
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { OpenSettings(); };
            hotkey.Pressed += SaveReplay;
            hotkey.OverlayPressed += ToggleOverlay;
            hotkey.ScreenshotPressed += TakeScreenshot;
            hotkey.CommandRequested += delegate(int command) { if (command == 1) OpenSettings(); else if (command == 2) SetPaused(true); else if (command == 3) SetPaused(false); else if (command == 4) ExitThread(); else if (command == 5) ToggleOverlay(); else if (command == 6) TakeScreenshot(); };
            engine = new ReplayEngine(config, data, paused);
            AttachEngine();
            try { hotkey.Register(config); hotkeyAvailable = true; Log.Write("Global shortcuts registered: " + config.HotkeyText + "; overlay " + config.OverlayHotkeyText + "; screenshot " + config.ScreenshotHotkeyText); } catch (Exception ex) { hotkeyAvailable = hotkey.SaveRegistered; Log.Write("Hotkey registration failed: " + ex); Notify("Shortcut unavailable", ex.Message, NotificationKind.Warning, 6000); OpenSettings(); }
            SystemEvents.PowerModeChanged += PowerChanged;
            SystemEvents.DisplaySettingsChanged += DisplayChanged;
            SystemEvents.SessionSwitch += SessionChanged;
            if (!File.Exists(configPath)) { config.Save(configPath); OpenSettings(); }
            if (showSettings) OpenSettings();
            WriteStatus();
        }
        void SetPaused(bool value) { if (paused == value) return; paused = value; pauseItem.Text = paused ? "Resume recording" : "Pause recording"; engine.Pause(paused); notifications.PauseChanged(paused); UpdateSettingsState(); Log.Write(paused ? "Recording paused" : "Recording resumed"); WriteStatus(); }
        void UpdateSettingsState() { if (settingsForm != null && !settingsForm.IsDisposed) settingsForm.SetRecordingState(paused, captureState, captureReady); }
        void WriteStatus()
        {
            try { File.WriteAllText(Path.Combine(data, "status.json"), new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new { Paused = paused, SettingsVisible = overlay != null && !overlay.IsDisposed && overlay.Visible || settingsForm != null && !settingsForm.IsDisposed && Native.IsWindowVisible(settingsForm.Handle), SettingsWindow = settingsForm == null || settingsForm.IsDisposed ? 0 : settingsForm.Handle.ToInt64(), OverlayVisible = overlay != null && !overlay.IsDisposed && overlay.Visible, OverlayWindow = overlay == null || overlay.IsDisposed ? 0 : overlay.Handle.ToInt64(), OverlayShortcutRegistered = hotkey.OverlayRegistered, OverlayShortcut = config.OverlayHotkeyText, NotificationVisible = notification.IsPresented, NotificationTitle = notification.NotificationTitle, ScreenshotShortcutRegistered = hotkey.ScreenshotRegistered, ScreenshotShortcut = config.ScreenshotHotkeyText, ScreenshotInProgress = takingScreenshot, LastScreenshot = lastScreenshot, CurrentApplication = foregroundApplication.ApplicationName, ApplicationFolder = ApplicationFolders.FolderName(foregroundApplication.ApplicationName) })); } catch (IOException) { }
        }
        void Post(Action action) { if (!closing && !dispatcher.IsDisposed) try { dispatcher.BeginInvoke(action); } catch (InvalidOperationException) { } }
        void AttachEngine()
        {
            engine.StateChanged += delegate(string message, bool ready)
            {
                Post(delegate
                {
                    string status = ready && !hotkeyAvailable ? "Buffering · save hotkey unavailable; open Settings" : message;
                    captureState = status; captureReady = ready && hotkeyAvailable; UpdateSettingsState();
                    stateItem.Text = status; tray.Text = status.Substring(0, Math.Min(63, status.Length)); tray.Icon = ready && hotkeyAvailable ? readyIcon : warningIcon;
                    notifications.StateChanged(message, ready, paused, config, hotkeyAvailable);
                });
            };
            engine.SaveStarted += delegate { Post(notifications.SaveStarted); };
            engine.SaveFinished += delegate(string message, bool success) { Post(delegate { notifications.SaveFinished(message, success); }); };
        }
        void Notify(string title, string message, NotificationKind kind, int milliseconds)
        {
            if (closing) return;
            notification.Present(title, message, kind, milliseconds, Screen.FromHandle(Native.GetForegroundWindow()), overlay == null || overlay.IsDisposed ? (Rectangle?)null : overlay.Bounds);
        }
        internal void TakeScreenshot()
        {
            if (closing || takingScreenshot) return;
            takingScreenshot = true;
            if (overlay != null && !overlay.IsDisposed) overlay.Dismiss();
            notification.Dismiss(); WriteStatus();
            int selectedDisplay = config.DisplayIndex; string folder = config.ScreenshotDirectory;
            Task.Run(delegate
            {
                string saved = null, failure = null;
                try { Thread.Sleep(150); saved = ScreenshotCapture.Capture(selectedDisplay, folder); }
                catch (Exception ex) { failure = ex.Message; Log.Write("Screenshot failed: " + ex); }
                Post(delegate
                {
                    takingScreenshot = false;
                    if (saved != null) { lastScreenshot = saved; Notify("Screenshot saved", Path.GetFileName(saved), NotificationKind.Success, 4000); }
                    else Notify("Screenshot not saved", failure, NotificationKind.Warning, 6000);
                    WriteStatus();
                });
            });
        }
        void SaveReplay() { string applicationName = foregroundApplication.Sample(); Log.Write("Replay save application: " + applicationName); engine.Save(applicationName); }
        void PowerChanged(object sender, PowerModeChangedEventArgs args) { if (args.Mode == PowerModes.Resume || args.Mode == PowerModes.Suspend) engine.Restart(); }
        void DisplayChanged(object sender, EventArgs args) { engine.Restart(); }
        void SessionChanged(object sender, SessionSwitchEventArgs args) { if (args.Reason == SessionSwitchReason.SessionUnlock || args.Reason == SessionSwitchReason.SessionLock) engine.Restart(); }
        void OpenSettings()
        {
            if (overlay != null && !overlay.IsDisposed) { overlay.ShowForUser(); return; }
            EnsureSettings();
            if (settingsForm != null && !settingsForm.IsDisposed) { settingsForm.ShowForUser(); UpdateSettingsState(); WriteStatus(); }
        }
        internal void ToggleOverlay()
        {
            if (overlay != null && !overlay.IsDisposed) { overlay.Dismiss(); return; }
            foregroundApplication.Sample();
            EnsureSettings();
            if (settingsForm == null || settingsForm.IsDisposed) return;
            try
            {
                overlay = new OverlayForm(settingsForm, config.OverlayHotkeyText, SaveReplay, TakeScreenshot);
                overlay.Dismissed += delegate { overlay = null; if (notification.IsPresented) notification.Reposition(Screen.FromHandle(Native.GetForegroundWindow())); WriteStatus(); };
                overlay.ShowForUser(); if (notification.IsPresented) notification.Reposition(Screen.FromControl(overlay), overlay.Bounds); UpdateSettingsState(); WriteStatus(); Log.Write("Overlay opened");
            }
            catch (Exception ex) { Log.Write("Overlay could not open: " + ex); if (overlay != null) overlay.Dispose(); overlay = null; Notify("Overlay could not open", ex.Message, NotificationKind.Warning, 6000); }
        }
        void EnsureSettings()
        {
            try
            {
                if (settingsForm == null || settingsForm.IsDisposed)
                {
                    settingsForm = new SettingsForm(config, data, delegate(Settings next, bool startup)
                    {
                        next.Validate();
                        try { hotkey.Register(next); hotkeyAvailable = true; } catch { try { hotkey.Register(config); } catch { } hotkeyAvailable = hotkey.SaveRegistered; throw; }
                        next.Save(configPath);
                        Startup.SetEnabled(startup);
                        engine.Dispose(); config = next; engine = new ReplayEngine(config, data, paused);
                        AttachEngine(); WriteStatus();
                    }, SetPaused, paused);
                    settingsForm.FormClosed += delegate { WriteStatus(); };
                }
            }
            catch (Exception ex) { Log.Write("Settings could not open: " + ex); Notify("Settings could not open", ex.Message, NotificationKind.Warning, 6000); }
        }
        internal static Icon MakeIcon(Color color)
        {
            using (var bitmap = new Bitmap(32, 32))
            using (var graphics = Graphics.FromImage(bitmap))
            using (var brush = new SolidBrush(color))
            using (var font = new Font("Segoe UI", 10, FontStyle.Bold))
            {
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                graphics.FillEllipse(brush, 3, 3, 26, 26);
                graphics.DrawString("10", font, Brushes.White, 5, 7);
                IntPtr handle = bitmap.GetHicon();
                try { using (Icon borrowed = Icon.FromHandle(handle)) return (Icon)borrowed.Clone(); }
                finally { DestroyIcon(handle); }
            }
        }
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
        protected override void ExitThreadCore()
        {
            closing = true; SystemEvents.PowerModeChanged -= PowerChanged; SystemEvents.DisplaySettingsChanged -= DisplayChanged; SystemEvents.SessionSwitch -= SessionChanged;
            if (overlay != null) overlay.Dispose();
            notification.Dispose();
            foregroundApplication.Dispose();
            hotkey.Dispose(); engine.Dispose(); tray.Visible = false; tray.Dispose(); readyIcon.Dispose(); warningIcon.Dispose(); dispatcher.Dispose();
            if (settingsForm != null) settingsForm.Dispose(); base.ExitThreadCore();
        }
    }
    internal static class Startup
    {
        const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
        internal static bool Enabled { get { try { using (var key = Registry.CurrentUser.OpenSubKey(Key)) return key != null && key.GetValue("F10Recorder") != null; } catch (System.Security.SecurityException) { return false; } } }
        internal static void SetEnabled(bool enabled)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(Key))
            { if (enabled) key.SetValue("F10Recorder", CaptureSession.Quote(Application.ExecutablePath), RegistryValueKind.String); else key.DeleteValue("F10Recorder", false); }
        }
    }
}
