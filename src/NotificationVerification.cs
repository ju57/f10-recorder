using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace F10Recorder
{
    internal static class NotificationVerification
    {
        [StructLayout(LayoutKind.Explicit, Size = 40)] struct Input
        {
            [FieldOffset(0)] public uint Type;
            [FieldOffset(8)] public ushort Key;
            [FieldOffset(12)] public uint Flags;
            [FieldOffset(20)] public uint MouseFlags;
        }
        [DllImport("user32.dll")] static extern uint SendInput(uint count, Input[] inputs, int size);
        [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point point);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
        static void Wait(Func<bool> condition, string failure, int milliseconds = 15000)
        {
            var watch = Stopwatch.StartNew(); while (watch.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); if (condition()) return; Thread.Sleep(20); } throw new Exception(failure);
        }
        internal static int Run(string data)
        {
            var report = new Dictionary<string, object>();
            try
            {
                Native.SetProcessDPIAware(); Application.EnableVisualStyles();
                using (var host = new Form { Text = "F10 Notification Fullscreen Test", TopMost = true, KeyPreview = true, FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, Bounds = Screen.PrimaryScreen.Bounds, BackColor = Color.FromArgb(28, 36, 46) })
                using (var toast = new NotificationForm())
                using (var dispatcher = new Control())
                {
                    dispatcher.CreateControl(); int keys = 0, clicks = 0;
                    host.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.J) keys++; };
                    host.MouseDown += delegate { clicks++; };
                    host.Show(); Native.ShowWindow(host.Handle, 9); Native.SetForegroundWindow(host.Handle); Application.DoEvents();
                    if (Native.GetForegroundWindow() != host.Handle)
                    {
                        Point point = new Point(host.Left + 32, host.Top + host.Height / 2); Point previous = Cursor.Position;
                        if (WindowFromPoint(point) != host.Handle) throw new Exception("Test focus click would target another application.");
                        SetCursorPos(point.X, point.Y); SendInput(2, new[] { new Input { MouseFlags = 2 }, new Input { MouseFlags = 4 } }, 40); SetCursorPos(previous.X, previous.Y);
                    }
                    Wait(delegate { return Native.GetForegroundWindow() == host.Handle; }, "Temporary fullscreen application could not receive focus."); clicks = 0;
                    toast.Present("Replay saved", "Replay-2026-10-13-14-00-00.mp4", NotificationKind.Success, 0, Screen.PrimaryScreen);
                    Application.DoEvents();
                    report["notificationWindow"] = toast.Handle.ToInt64(); report["hostWindow"] = host.Handle.ToInt64(); report["foregroundAfterPresent"] = Native.GetForegroundWindow().ToInt64(); report["nativeVisibleAfterPresent"] = Native.IsWindowVisible(toast.Handle); report["windowStyle"] = GetWindowLongPtr(toast.Handle, -20).ToInt64();
                    if (!Native.IsWindowVisible(toast.Handle) || Native.GetForegroundWindow() != host.Handle) throw new Exception("Notification stayed hidden or took focus.");
                    long style = GetWindowLongPtr(toast.Handle, -20).ToInt64();
                    if ((style & 0x08080028) != 0x08080028 || toast.ShowInTaskbar) throw new Exception("Notification is missing non-activation, layered, topmost, or click-through styles.");
                    report["visibleAboveBorderlessFullscreenWithoutActivation"] = true;
                    Point click = new Point(toast.Left + toast.Width / 2, toast.Top + toast.Height / 2); Point oldCursor = Cursor.Position;
                    if (WindowFromPoint(click) != host.Handle) throw new Exception("Notification intercepts hit testing.");
                    SetCursorPos(click.X, click.Y); SendInput(2, new[] { new Input { MouseFlags = 2 }, new Input { MouseFlags = 4 } }, 40); SetCursorPos(oldCursor.X, oldCursor.Y);
                    Wait(delegate { return clicks == 1; }, "Click through notification did not reach the fullscreen application.");
                    SendInput(2, new[] { new Input { Type = 1, Key = 74 }, new Input { Type = 1, Key = 74, Flags = 2 } }, 40);
                    Wait(delegate { return keys == 1; }, "Notification interrupted keyboard input.");
                    report["mouseAndKeyboardRemainWithFullscreenApp"] = true;
                    using (var bitmap = new Bitmap(toast.Width, toast.Height)) { toast.DrawToBitmap(bitmap, new Rectangle(0, 0, toast.Width, toast.Height)); bitmap.Save(Path.Combine(data, "notification-preview.png")); }
                    toast.Present("Replay recording paused", "Replay buffering is off until you resume.", NotificationKind.Warning, 200, Screen.PrimaryScreen);
                    Wait(delegate { return !toast.IsPresented; }, "Notification did not dismiss automatically.");
                    if (Native.GetForegroundWindow() != host.Handle) throw new Exception("Dismissing notification changed focus.");
                    report["automaticDismissalPreservesFocus"] = true;

                    var titles = new List<string>(); string clip = null; bool paused = false;
                    var notifications = new RecorderNotifications(delegate(string title, string message, NotificationKind kind, int duration)
                    {
                        titles.Add(title); toast.Present(title, message, kind, duration, Screen.PrimaryScreen);
                    });
                    var config = new Settings { ReplayMinutes = 1, FramesPerSecond = 30, BitrateMbps = 5, OutputDirectory = Path.Combine(data, "clips") };
                    using (var engine = new ReplayEngine(config, data, false, true))
                    {
                        engine.StateChanged += delegate(string message, bool ready) { dispatcher.BeginInvoke(new Action(delegate { notifications.StateChanged(message, ready, paused, config, true); })); };
                        engine.SaveStarted += delegate { dispatcher.BeginInvoke(new Action(notifications.SaveStarted)); };
                        engine.SaveFinished += delegate(string message, bool success) { dispatcher.BeginInvoke(new Action(delegate { if (success) clip = message; notifications.SaveFinished(message, success); })); };
                        Wait(delegate { return titles.Contains("Replay recording active"); }, "Actual capture readiness did not notify.");
                        var watch = Stopwatch.StartNew(); while (watch.ElapsedMilliseconds < 1200) { Application.DoEvents(); Thread.Sleep(20); }
                        if (titles.Count(title => title == "Replay recording active") != 1) throw new Exception("Periodic buffering updates repeated the start notification.");
                        engine.Save(); Wait(delegate { return clip != null; }, "Actual replay save did not complete.");
                        int started = titles.IndexOf("Saving replay…"), saved = titles.IndexOf("Replay saved");
                        if (started < 0 || saved <= started || !File.Exists(clip)) throw new Exception("Actual save notifications were missing or out of order.");
                        report["actualCaptureAndSaveEvents"] = true; report["clip"] = clip; report["repeatedBufferUpdatesStayQuiet"] = true;
                        paused = true; engine.Pause(true); notifications.PauseChanged(true);
                        Wait(delegate { return engine.CaptureProcessId == 0; }, "Recorder did not pause.");
                        engine.Save(); Wait(delegate { return toast.NotificationTitle == "Replay not saved"; }, "Saving while paused did not notify.");
                        if (!toast.NotificationDetail.Contains("paused")) throw new Exception("Paused save error was inaccurate.");
                        paused = false; notifications.PauseChanged(false); engine.Pause(false);
                        Wait(delegate { return titles.Count(title => title == "Replay recording active") == 2; }, "Resumed recording did not confirm readiness.");
                        report["pauseResumeAndSaveError"] = true;
                    }
                    notifications.StateChanged("Recovering: test capture failure", false, false, config, true);
                    notifications.StateChanged("Recovering: repeated failure", false, false, config, true);
                    notifications.StateChanged("Buffering", true, false, config, true);
                    if (titles.Count(title => title == "Recording is recovering") != 1 || toast.NotificationTitle != "Replay recording recovered") throw new Exception("Recovery notifications were not throttled or completed.");
                    report["recoveryThrottlingAndCompletion"] = true;
                    if (Native.GetForegroundWindow() != host.Handle) throw new Exception("Lifecycle notifications stole foreground focus.");
                    using (var settings = new SettingsForm(config, data, delegate { }))
                    using (var overlay = new OverlayForm(settings, config.OverlayHotkeyText, delegate { }))
                    {
                        overlay.ShowForUser(); Application.DoEvents();
                        toast.Present("Replay saved", "The Settings overlay stays interactive.", NotificationKind.Success, 4000, Screen.PrimaryScreen, overlay.Bounds);
                        if (Native.GetForegroundWindow() != overlay.Handle || toast.Bounds.IntersectsWith(overlay.Bounds)) throw new Exception("Notification took overlay focus or covered its controls.");
                        overlay.Dismiss(); toast.Reposition(Screen.PrimaryScreen); Application.DoEvents();
                        if (Native.GetForegroundWindow() != host.Handle) throw new Exception("Notification repositioning changed foreground focus.");
                        report["settingsOverlayCoexistence"] = true;
                    }
                    report["result"] = "passed";
                }
            }
            catch (Exception ex) { report["result"] = "failed"; report["error"] = ex.ToString(); }
            File.WriteAllText(Path.Combine(data, "verification.json"), new JavaScriptSerializer().Serialize(report)); return report["result"].ToString() == "passed" ? 0 : 1;
        }
    }
}
