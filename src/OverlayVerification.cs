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
    internal static class OverlayVerification
    {
        [StructLayout(LayoutKind.Explicit, Size = 40)] struct Input
        {
            [FieldOffset(0)] public uint Type;
            [FieldOffset(8)] public ushort Key;
            [FieldOffset(12)] public uint Flags;
            [FieldOffset(20)] public uint MouseFlags;
        }
        [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, Input[] inputs, int size);
        [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point point);
        static void Press(params ushort[] keys)
        {
            var input = keys.Select(key => new Input { Type = 1, Key = key }).Concat(keys.Reverse().Select(key => new Input { Type = 1, Key = key, Flags = 2 })).ToArray();
            if (SendInput((uint)input.Length, input, 40) != input.Length) throw new Exception("Windows did not accept the test keyboard input.");
        }
        static void Wait(Func<bool> condition, string failure)
        {
            var watch = Stopwatch.StartNew(); while (watch.Elapsed.TotalSeconds < 10) { Application.DoEvents(); if (condition()) return; Thread.Sleep(20); } throw new Exception(failure);
        }
        internal static int Run(string data)
        {
            var report = new Dictionary<string, object>(); OverlayForm overlay = null;
            try
            {
                Native.SetProcessDPIAware(); Application.EnableVisualStyles();
                var config = new Settings { SaveKey = 122, SaveModifiers = 7, OverlayKey = 123, OverlayModifiers = 7, ScreenshotKey = 118, ScreenshotModifiers = 7 };
                Settings submitted = null; bool paused = true; int saves = 0, screenshots = 0;
                using (var host = new Form { Text = "F10 Overlay Test Fullscreen", TopMost = true, FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, Bounds = Screen.PrimaryScreen.Bounds, BackColor = Color.FromArgb(28, 36, 46) })
                using (var hotkey = new HotkeyWindow())
                using (var form = new SettingsForm(config, data, delegate(Settings next, bool startup) { submitted = next; }, delegate(bool value) { paused = value; }, true))
                {
                    host.Controls.Add(new Label { Text = "Borderless fullscreen test application", ForeColor = Color.White, AutoSize = true, Font = UiTheme.Font(24), Location = new Point(48, 48) });
                    host.Show(); Native.ShowWindow(host.Handle, 9); Native.SetForegroundWindow(host.Handle);
                    Application.DoEvents();
                    if (Native.GetForegroundWindow() != host.Handle)
                    {
                        Point point = new Point(host.Left + 32, host.Top + host.Height / 2); Point oldCursor = Cursor.Position;
                        if (WindowFromPoint(point) != host.Handle) throw new Exception("Focus test click would not target the temporary test window.");
                        SetCursorPos(point.X, point.Y); SendInput(2, new[] { new Input { MouseFlags = 2 }, new Input { MouseFlags = 4 } }, 40); SetCursorPos(oldCursor.X, oldCursor.Y);
                    }
                    Wait(delegate { return Native.GetForegroundWindow() == host.Handle; }, "Test application could not receive focus.");
                    hotkey.Register(config);
                    hotkey.OverlayPressed += delegate
                    {
                        if (overlay != null) overlay.Dismiss();
                        else
                        {
                            overlay = new OverlayForm(form, config.OverlayHotkeyText, delegate { saves++; }, delegate { screenshots++; });
                            overlay.Dismissed += delegate { overlay = null; };
                            overlay.ShowForUser();
                        }
                    };
                    hotkey.Pressed += delegate { saves++; };
                    Press(0x11, 0x12, 0x10, 0x7B);
                    Wait(delegate { return overlay != null && overlay.Visible && form.SettingsContent.Visible; }, "Global shortcut did not open the overlay over the fullscreen application.");
                    if (!overlay.TopMost || overlay.ShowInTaskbar || overlay.FormBorderStyle != FormBorderStyle.None || !Screen.PrimaryScreen.Bounds.Contains(overlay.Bounds)) throw new Exception("Overlay window is not correctly positioned above the application.");
                    report["hotkeyOpensOverBorderlessFullscreen"] = true;
                    Log.Write("Overlay test: opened");
                    Wait(delegate { return form.CodecCheckFinished; }, "Embedded Settings did not finish its encoder probe.");
                    for (int i = 0; i < 4; i++) { form.SelectPage(i); Application.DoEvents(); }
                    form.SelectPage(0); Application.DoEvents();
                    using (var bitmap = new Bitmap(overlay.Width, overlay.Height)) { overlay.DrawToBitmap(bitmap, new Rectangle(0, 0, overlay.Width, overlay.Height)); bitmap.Save(Path.Combine(data, "overlay-preview.png")); }
                    Log.Write("Overlay test: sending save shortcut");
                    Press(0x11, 0x12, 0x10, 0x7A); Wait(delegate { return saves == 1; }, "Save shortcut stopped working while the overlay was open.");
                    ((Button)overlay.Controls.Find("OverlaySaveReplay", true).Single()).PerformClick(); if (saves != 2) throw new Exception("Overlay save button did not dispatch.");
                    report["saveShortcutAndButton"] = true;
                    ((Button)overlay.Controls.Find("OverlayScreenshot", true).Single()).PerformClick(); if (screenshots != 1) throw new Exception("Overlay screenshot button did not dispatch.");
                    report["screenshotButton"] = true;
                    Log.Write("Overlay test: sending dismiss shortcut");
                    Press(0x11, 0x12, 0x10, 0x7B); Wait(delegate { return overlay == null; }, "Overlay shortcut did not close it.");
                    Wait(delegate { return Native.GetForegroundWindow() == host.Handle; }, "Closing the overlay did not restore fullscreen application focus.");
                    if (!form.TopLevel || form.FormBorderStyle == FormBorderStyle.None) throw new Exception("Settings was not detached after dismissal.");
                    report["hotkeyToggleAndFocusRestore"] = true;
                    Log.Write("Overlay test: dismiss and restore passed");
                    Press(0x11, 0x12, 0x10, 0x7B); Wait(delegate { return overlay != null && overlay.Visible; }, "Overlay did not reopen.");
                    ((NumberStepper)overlay.Controls.Find("ReplayMinutes", true).Single()).Value = 4;
                    Press(0x1B); Wait(delegate { return overlay == null; }, "Escape did not close embedded Settings.");
                    Press(0x11, 0x12, 0x10, 0x7B); Wait(delegate { return overlay != null && overlay.Visible; }, "Overlay did not reopen after Escape.");
                    if (((NumberStepper)overlay.Controls.Find("ReplayMinutes", true).Single()).Value != 4) throw new Exception("Closing the overlay discarded unapplied settings.");
                    report["escapeAndUnappliedEditsPreserved"] = true;
                    if (!form.ApplySettings() || submitted == null || submitted.ReplayMinutes != 4 || submitted.OverlayKey != config.OverlayKey || !paused) throw new Exception("Applying overlay Settings failed or changed recording state.");
                    Wait(delegate { return overlay == null; }, "Apply did not dismiss the overlay.");
                    report["applyAndPausePreserved"] = true;

                    using (var occupied = new HotkeyWindow())
                    {
                        // A failed overlay registration must leave the save shortcut usable.
                        Native.UnregisterHotKey(hotkey.Handle, 2);
                        if (!Native.RegisterHotKey(occupied.Handle, 20, 7, 0x7B)) throw new Exception("Conflict fixture could not reserve its shortcut.");
                        bool rejected = false; try { hotkey.Register(config); } catch (InvalidOperationException) { rejected = true; }
                        Native.UnregisterHotKey(occupied.Handle, 20);
                        if (!rejected || !hotkey.SaveRegistered || hotkey.OverlayRegistered) throw new Exception("Overlay shortcut conflict was not correctly reported.");
                        report["shortcutConflictPreservesSave"] = true;
                    }
                    using (var existing = new SettingsForm(config, data, delegate { }))
                    {
                        existing.ShowForUser(); Application.DoEvents(); Native.SetForegroundWindow(host.Handle);
                        overlay = new OverlayForm(existing, config.OverlayHotkeyText, delegate { }); overlay.Dismissed += delegate { overlay = null; }; overlay.ShowForUser();
                        if (existing.Controls.Count != 0 || existing.SettingsContent.Parent == existing) throw new Exception("Existing Settings was not hosted by the overlay.");
                        overlay.Dismiss(); Application.DoEvents();
                        if (!Native.IsWindowVisible(existing.Handle) || existing.SettingsContent.Parent != existing) throw new Exception("Existing Settings did not return to its standalone window.");
                        Wait(delegate { return Native.GetForegroundWindow() == host.Handle; }, "Returning standalone Settings stole fullscreen application focus.");
                        report["existingSettingsRestoredWithoutLosingGameFocus"] = true;
                        overlay = new OverlayForm(existing, config.OverlayHotkeyText, delegate { }); overlay.Dismissed += delegate { overlay = null; }; overlay.ShowForUser();
                        Wait(delegate { return existing.CodecCheckFinished; }, "Existing Settings did not finish its encoder check.");
                        if (!existing.ApplySettings()) throw new Exception("Existing Settings could not apply from the overlay.");
                        Wait(delegate { return overlay == null && existing.IsDisposed; }, "Applying existing Settings left an overlay or window open.");
                        Wait(delegate { return Native.GetForegroundWindow() == host.Handle; }, "Applying existing Settings did not restore fullscreen focus.");
                        report["applyExistingSettingsClosesCleanly"] = true;
                    }
                }
                report["result"] = "passed";
            }
            catch (Exception ex) { report["result"] = "failed"; report["error"] = ex.ToString(); }
            finally { if (overlay != null) overlay.Dispose(); }
            File.WriteAllText(Path.Combine(data, "verification.json"), new JavaScriptSerializer().Serialize(report)); return report["result"].ToString() == "passed" ? 0 : 1;
        }
    }
}
