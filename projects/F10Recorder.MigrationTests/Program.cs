using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Forms = System.Windows.Forms;

namespace F10Recorder;

internal static class MigrationChecks
{
    static string data;
    static readonly Dictionary<string, object> report = new();
    static bool failStartup;
    [STAThread]
    static int Main(string[] args)
    {
        data = args[0]; Directory.CreateDirectory(data);
        if (args.Contains("--fixture")) { Fixture(); return 0; }
        WindowsRecorder.Initialize(data);
        report["mediaEngine"] = MediaDependency.ResolveExecutable();
        var config = new Settings { ReplayMinutes = 1, FramesPerSecond = 30, BitrateMbps = 5, OutputDirectory = Path.Combine(data, "clips"), ScreenshotDirectory = Path.Combine(data, "screenshots"), SaveKey = 114, SaveModifiers = 7, OverlayKey = 117, OverlayModifiers = 7, ScreenshotKey = 118, ScreenshotModifiers = 7 };
        config.Save(Path.Combine(data, "settings.json"));
        App.DataDirectory = data; App.Arguments = ["--paused", "--settings"]; App.ControlTitle = "F10Recorder.MigrationCheck." + Guid.NewGuid();
        App.RecorderFactory = (root, post, paused, title) => new WindowsRecorder(root, post, paused, title, true, enabled => { if (failStartup) { failStartup = false; throw new IOException("Controlled startup-write failure."); } report["startupChoiceSubmitted"] = enabled; });
        App.Started = app => _ = CheckAsync(app);
        try { Program.BuildAvaloniaApp().StartWithClassicDesktopLifetime([]); }
        catch (Exception ex) { report["result"] = "failed"; report["error"] = ex.ToString(); File.WriteAllText(Path.Combine(data, "verification.json"), JsonSerializer.Serialize(report)); }
        return report.TryGetValue("result", out var result) && (string)result == "passed" ? 0 : 1;
    }
    static void Require(bool condition, string error) { if (!condition) throw new Exception(error); }
    static async Task Until(Func<bool> condition, string error, int milliseconds = 15000)
    {
        var timer = Stopwatch.StartNew(); while (!condition() && timer.ElapsedMilliseconds < milliseconds) await Task.Delay(50); Require(condition(), error);
    }
    static T Control<T>(SettingsPanel panel, string name) where T : Avalonia.Controls.Control => panel.GetLogicalDescendants().OfType<T>().Single(c => c.Name == name);
    static void Render(Avalonia.Controls.Control control, string name)
    {
        using var bitmap = new RenderTargetBitmap(new Avalonia.PixelSize((int)control.Bounds.Width, (int)control.Bounds.Height), new Avalonia.Vector(96, 96)); bitmap.Render(control); bitmap.Save(Path.Combine(data, name + ".png"), PngBitmapEncoderOptions.Default);
    }
    static async Task CheckAsync(App app)
    {
        Process fixture = null;
        try
        {
            await Until(() => app.SettingsWindow.Panel.CodecCheckFinished, "GPU codec check did not complete.");
            var panel = app.SettingsWindow.Panel; var recorder = app.Recorder;
            report["theme"] = app.SettingsWindow.ActualThemeVariant.ToString();
            Require(Native.IsWindowVisible(app.SettingsWindow.TryGetPlatformHandle().Handle), "Hidden startup left Settings invisible.");
            Require(recorder.CaptureProcessId == 0 && recorder.Paused, "Paused launch started capture."); report["hiddenSettingsAndPausedLaunch"] = true;
            var available = Control<ComboBox>(panel, "VideoCodec").Items.Cast<EncoderOption>().ToArray(); Require(available.Length > 0, "No usable GPU encoders."); report["availableCodecs"] = available.Select(c => c.Codec).ToArray();
            foreach (int page in Enumerable.Range(0, 4)) { panel.SelectPage(page); await Task.Delay(100); Render(panel, "settings-" + new[] { "capture", "audio", "controls", "storage" }[page]); }
            app.SettingsWindow.WindowState = WindowState.Minimized; app.OpenSettings(); await Task.Delay(100); Require(app.SettingsWindow.WindowState == WindowState.Normal && Native.IsWindowVisible(app.SettingsWindow.TryGetPlatformHandle().Handle), "Minimized Settings was not restored.");
            app.SettingsWindow.Width = 780; app.SettingsWindow.Height = 620; panel.SelectPage(0); await Task.Delay(100); Render(panel, "settings-minimum");
            var apply = Control<Button>(panel, "ApplySettings"); Require(apply.TranslatePoint(new Avalonia.Point(0, apply.Bounds.Height), panel)?.Y <= panel.Bounds.Height, "Apply footer is outside the minimum window.");
            app.SettingsWindow.Width = 900; app.SettingsWindow.Height = 820;
            Control<NumericUpDown>(panel, "ReplayMinutes").Value = 7; Control<NumericUpDown>(panel, "BitrateMbps").Value = 31; Control<ComboBox>(panel, "FramesPerSecond").SelectedItem = 120;
            Control<ComboBox>(panel, "VideoCodec").SelectedItem = available.Last();
            panel.SelectPage(1); Control<CheckBox>(panel, "SystemAudio").IsChecked = false;
            panel.SelectPage(2); Control<CheckBox>(panel, "StartWithWindows").IsChecked = false;
            Control<ComboBox>(panel, "SaveKey").SelectedIndex = 3; Control<ComboBox>(panel, "OverlayKey").SelectedIndex = 3;
            Require(!await panel.ApplyAsync() && panel.Error.Contains("different shortcuts"), "Duplicate shortcuts were accepted.");
            Control<ComboBox>(panel, "OverlayKey").SelectedIndex = 4; Control<ComboBox>(panel, "ScreenshotKey").SelectedIndex = 7;
            panel.SelectPage(3); Control<TextBox>(panel, "ScreenshotDirectory").Text = Path.Combine(data, "custom screenshots");
            // Dismissing and reopening the overlay must preserve pending edits and pause.
            app.ToggleOverlay(); await Task.Delay(100); Require(app.SettingsWindow.OverlayVisible, "Overlay did not open."); app.ToggleOverlay(); await Task.Delay(100);
            panel.SelectPage(0); Require(Control<NumericUpDown>(panel, "ReplayMinutes").Value == 7 && recorder.Paused, "Overlay lost edits or changed pause."); report["overlayPreservesEditsAndPause"] = true;
            failStartup = true; Require(!await panel.ApplyAsync() && recorder.Config.ReplayMinutes == 1 && Settings.Load(Path.Combine(data, "settings.json")).ReplayMinutes == 1 && recorder.Paused, "A failed startup update changed live or persisted capture configuration."); report["failedApplyRollback"] = true;
            Require(await panel.ApplyAsync(), "Settings submission failed: " + panel.Error);
            Require(recorder.Config.ReplayMinutes == 7 && recorder.Config.FramesPerSecond == 120 && recorder.Config.BitrateMbps == 31 && !recorder.Config.SystemAudio && recorder.Config.ScreenshotDirectory.EndsWith("custom screenshots") && recorder.Paused, "An edited value was lost.");
            Require(recorder.Config.SaveKey == 115 && recorder.Config.OverlayKey == 116 && recorder.Config.ScreenshotKey == 119, "Shortcut edits were lost.");
            Require(Settings.Load(Path.Combine(data, "settings.json")).VideoCodec == available.Last().Codec, "Modern JSON reload lost the codec.");
            report["settingsSubmissionAndLegacySchema"] = true;
            fixture = Process.Start(new ProcessStartInfo(Environment.ProcessPath, CaptureSession.Quote(data) + " --fixture") { UseShellExecute = false, CreateNoWindow = true });
            await Until(() => fixture.MainWindowHandle != IntPtr.Zero, "External fixture window did not appear."); ActivateFixture(fixture);
            recorder.SampleForeground(); string expectedApplication = recorder.ApplicationName;
            SendKey(116, 7); await Until(() => app.SettingsWindow.OverlayVisible, "Registered overlay shortcut failed.");
            SendKey(27, 0); await Until(() => !app.SettingsWindow.OverlayVisible, "Escape did not dismiss the overlay.");
            Require(Native.GetForegroundWindow() == fixture.MainWindowHandle, "Overlay did not return focus."); report["globalOverlayShortcutAndFocus"] = true;
            app.Notice.Present("Migration check", "Passive notification", NotificationKind.Success, 1200, app.SettingsWindow); await Task.Delay(150);
            Require(Native.GetForegroundWindow() == fixture.MainWindowHandle, "Notification stole focus.");
            long styles = WindowStyles.Get(app.Notice.TryGetPlatformHandle().Handle, -20).ToInt64(); Require((styles & 0x08080020) == 0x08080020, "Passive notification styles are missing.");
            var point = new Point { X = app.Notice.Position.X + 60, Y = app.Notice.Position.Y + 50 };
            Require(WindowFromPoint(point) == fixture.MainWindowHandle, "Notification intercepts mouse hit testing.");
            Render(app.Notice, "notification"); await Until(() => !app.Notice.IsVisible, "Notification did not dismiss.", 3000); report["passiveNotificationAndDismissal"] = true;
            SendKey(119, 7); await Until(() => recorder.LastScreenshot != null, "Screenshot shortcut did not save.", 20000);
            using (var image = System.Drawing.Image.FromFile(recorder.LastScreenshot)) Require(image.Width == Forms.Screen.PrimaryScreen.Bounds.Width && image.Height == Forms.Screen.PrimaryScreen.Bounds.Height, "Screenshot lost native resolution.");
            Require(recorder.Paused && recorder.LastScreenshot.StartsWith(recorder.Config.ScreenshotDirectory), "Screenshot destination or paused state changed."); report["pausedScreenshotShortcut"] = recorder.LastScreenshot;
            string firstScreenshot = recorder.LastScreenshot; app.ToggleOverlay(); Control<Button>(panel, "QuickScreenshot").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => recorder.LastScreenshot != firstScreenshot, "Overlay screenshot action failed.", 20000);
            Require(!app.SettingsWindow.OverlayVisible && File.Exists(firstScreenshot), "Screenshot lost the previous image or left the overlay open."); report["overlayScreenshot"] = recorder.LastScreenshot;
            // Replay action shares the production engine and native save shortcut.
            Control<ToggleSwitch>(panel, "RecordingEnabled").IsChecked = true; await Until(() => recorder.Ready, "Replay switch did not resume capture.", 25000); await Task.Delay(5500);
            string saved = null; bool? success = null; recorder.Saved += (file, ok) => { saved = file; success = ok; };
            ActivateFixture(fixture); recorder.SampleForeground(); expectedApplication = recorder.ApplicationName; SendKey(115, 7);
            await Until(() => success != null, "Replay shortcut did not finish saving.", 20000); Require(success == true, "Replay failed: " + saved);
            Require(Path.GetFileName(Path.GetDirectoryName(saved)) == ApplicationFolders.FolderName(expectedApplication), "Save lost the foreground application folder.");
            report["hotkeyReplayClip"] = saved; report["applicationName"] = expectedApplication; int capture = recorder.CaptureProcessId; await Task.Delay(1000); Require(capture != 0 && recorder.CaptureProcessId == capture, "Capture did not continue after save.");
            success = null; ActivateFixture(fixture); app.ToggleOverlay(); Control<Button>(panel, "QuickSaveReplay").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); app.ToggleOverlay();
            await Until(() => success != null, "Overlay Save replay did not finish.", 20000);
            Require(success == true && Path.GetFileName(Path.GetDirectoryName(saved)) == ApplicationFolders.FolderName(expectedApplication), "Overlay save lost the foreground application."); report["overlayReplayClip"] = saved;
            // Recovery must replace only this test recorder's encoder.
            Control<ToggleSwitch>(panel, "RecordingEnabled").IsChecked = false; await Until(() => recorder.CaptureProcessId == 0 && recorder.Paused, "Replay switch did not pause capture."); recorder.Pause(false); await Until(() => recorder.Ready, "Resume did not become ready.", 25000);
            capture = recorder.CaptureProcessId; using (var encoder = Process.GetProcessById(capture)) encoder.Kill();
            await Until(() => recorder.CaptureProcessId != 0 && recorder.CaptureProcessId != capture && recorder.Ready, "Modern supervisor did not recover after encoder failure.", 30000); report["encoderRecovery"] = true;
            capture = recorder.CaptureProcessId; Native.PostMessage(Native.FindWindow(null, App.ControlTitle), 0x007E, IntPtr.Zero, IntPtr.Zero);
            await Until(() => recorder.CaptureProcessId != 0 && recorder.CaptureProcessId != capture && recorder.Ready, "Native display-change notification did not rebuild capture.", 25000); report["displayChangeRouting"] = true;
            recorder.Pause(true); await Until(() => recorder.CaptureProcessId == 0, "Final pause did not stop capture.");
            var media = await Task.Run(() => VerifyMedia(data)); report["media"] = media;
            report["result"] = "passed";
        }
        catch (Exception ex) { report["result"] = "failed"; report["error"] = ex.ToString(); Environment.ExitCode = 1; }
        finally
        {
            if (fixture != null) { if (!fixture.HasExited) { fixture.CloseMainWindow(); if (!fixture.WaitForExit(2000)) fixture.Kill(); } fixture.Dispose(); }
            File.WriteAllText(Path.Combine(data, "verification.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true })); app.Quit();
        }
    }
    static List<string> VerifyMedia(string root)
    {
        var files = new List<string>(); var choices = EncoderCapabilities.Probe(MediaDependency.ResolveExecutable());
        foreach (var choice in choices)
        {
            var config = new Settings { ReplayMinutes = 1, FramesPerSecond = choice.Codec == "h264_nvenc" ? 24 : 60, BitrateMbps = 5, VideoCodec = choice.Codec };
            using var session = new CaptureSession(config, Path.Combine(root, "codec-cache"), true);
            for (int i = 0; i < 14; i++) { Thread.Sleep(500); session.Maintain(); Require(!session.HasExited, session.ErrorTail); }
            files.Add(session.Save(session.Position, Path.Combine(root, "codec-clips", choice.Codec)));
        }
        // Exercise .NET 10's COM/WASAPI interop with real display + system audio.
        var actual = new Settings { ReplayMinutes = 1, FramesPerSecond = 30, BitrateMbps = 5, SystemAudio = true };
        using (var session = new CaptureSession(actual, Path.Combine(root, "desktop-cache"), false))
        {
            for (int i = 0; i < 22; i++) { Thread.Sleep(500); session.Maintain(); Require(!session.HasExited, session.ErrorTail); }
            files.Add(session.Save(session.Position, Path.Combine(root, "desktop-clip")));
        }
        return files;
    }
    static void Fixture()
    {
        Forms.Application.EnableVisualStyles(); using var form = new Forms.Form { Text = "F10 migration fullscreen fixture", FormBorderStyle = Forms.FormBorderStyle.None, StartPosition = Forms.FormStartPosition.Manual, Bounds = Forms.Screen.PrimaryScreen.Bounds, BackColor = System.Drawing.Color.FromArgb(32, 72, 52) };
        form.Controls.Add(new Forms.Label { Text = "F10 Recorder · migration verification", AutoSize = true, ForeColor = System.Drawing.Color.White, Font = new System.Drawing.Font("Segoe UI", 28), Location = new System.Drawing.Point(80, 120) }); Forms.Application.Run(form);
    }
    static void ActivateFixture(Process fixture)
    {
        fixture.Refresh(); var handle = fixture.MainWindowHandle; Native.SetForegroundWindow(handle);
        if (Native.GetForegroundWindow() != handle)
        {
            var point = new Point { X = Forms.Screen.PrimaryScreen.Bounds.Left + 15, Y = Forms.Screen.PrimaryScreen.Bounds.Top + 150 };
            Require(WindowFromPoint(point) == handle, "Controlled fixture is not at the activation point."); SetCursorPos(point.X, point.Y); SendMouse(); Native.SetForegroundWindow(handle);
        }
        Require(Native.GetForegroundWindow() == handle, "Controlled fixture could not become foreground.");
    }
    static void SendKey(ushort key, int modifiers)
    {
        ushort[] keys = [(ushort)((modifiers & 2) != 0 ? 0x11 : 0), (ushort)((modifiers & 1) != 0 ? 0x12 : 0), (ushort)((modifiers & 4) != 0 ? 0x10 : 0)];
        var inputs = keys.Where(k => k != 0).Select(k => Keyboard(k, false)).ToList(); inputs.Add(Keyboard(key, false)); inputs.Add(Keyboard(key, true)); inputs.AddRange(keys.Where(k => k != 0).Reverse().Select(k => Keyboard(k, true))); Require(SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<Input>()) == inputs.Count, "Keyboard stimulus failed.");
    }
    static Input Keyboard(ushort key, bool up) => new() { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { Key = key, Flags = up ? 2u : 0u } } };
    static void SendMouse() { var inputs = new[] { new Input { Type = 0, Data = new InputUnion { Mouse = new MouseInput { Flags = 2 } } }, new Input { Type = 0, Data = new InputUnion { Mouse = new MouseInput { Flags = 4 } } } }; Require(SendInput(2, inputs, Marshal.SizeOf<Input>()) == 2, "Fixture activation click failed."); }
    [StructLayout(LayoutKind.Sequential)] struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] struct InputUnion { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] struct MouseInput { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
    [DllImport("user32.dll")] static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
}
