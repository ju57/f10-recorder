using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace F10Recorder
{
    internal static class ScreenshotVerification
    {
        [StructLayout(LayoutKind.Explicit, Size = 40)] struct Input
        {
            [FieldOffset(0)] public uint Type;
            [FieldOffset(8)] public ushort Key;
            [FieldOffset(12)] public uint Flags;
        }
        [DllImport("user32.dll")] static extern uint SendInput(uint count, Input[] inputs, int size);
        static void Wait(Func<bool> condition, string failure)
        {
            var watch = Stopwatch.StartNew(); while (watch.ElapsedMilliseconds < 20000) { Application.DoEvents(); if (condition()) return; Thread.Sleep(20); } throw new Exception(failure);
        }
        internal static int Run(string data)
        {
            var report = new Dictionary<string, object>();
            try
            {
                Native.SetProcessDPIAware(); Application.EnableVisualStyles();
                Color expected = Color.FromArgb(25, 80, 140);
                var config = new Settings { SaveKey = 122, SaveModifiers = 7, OverlayKey = 123, OverlayModifiers = 7, ScreenshotKey = 118, ScreenshotModifiers = 7, ScreenshotDirectory = Path.Combine(data, "custom screenshots") };
                string preferences = Path.Combine(data, "settings.json"); config.Save(preferences); config = Settings.Load(preferences);
                if (config.ScreenshotDirectory != Path.Combine(data, "custom screenshots") || config.ScreenshotHotkeyText != "Ctrl+Alt+Shift+F7") throw new Exception("Screenshot preferences did not round trip.");
                report["configuredFolderAndShortcutPersist"] = true;
                string saved = null, failure = null; int requests = 0;
                using (var host = new Form { Text = "F10 Screenshot Test Fullscreen", TopMost = true, FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, Bounds = Screen.PrimaryScreen.Bounds, BackColor = expected })
                using (var hotkey = new HotkeyWindow())
                using (var dispatcher = new Control())
                {
                    dispatcher.CreateControl(); host.Show(); Native.ShowWindow(host.Handle, 9); Native.SetForegroundWindow(host.Handle); Application.DoEvents(); host.Refresh();
                    hotkey.Register(config);
                    hotkey.ScreenshotPressed += delegate
                    {
                        requests++;
                        Task.Run(delegate
                        {
                            try { string path = ScreenshotCapture.Capture(-1, config.ScreenshotDirectory); dispatcher.BeginInvoke(new Action(delegate { saved = path; })); }
                            catch (Exception ex) { dispatcher.BeginInvoke(new Action(delegate { failure = ex.ToString(); })); }
                        });
                    };
                    Input[] input = { new Input { Type = 1, Key = 17 }, new Input { Type = 1, Key = 18 }, new Input { Type = 1, Key = 16 }, new Input { Type = 1, Key = 118 }, new Input { Type = 1, Key = 118, Flags = 2 }, new Input { Type = 1, Key = 16, Flags = 2 }, new Input { Type = 1, Key = 18, Flags = 2 }, new Input { Type = 1, Key = 17, Flags = 2 } };
                    if (SendInput(8, input, 40) != 8) throw new Exception("Windows refused the screenshot test shortcut.");
                    Wait(delegate { return saved != null || failure != null; }, "Screenshot hotkey did not finish capture.");
                    if (failure != null) throw new Exception(failure);
                    if (requests != 1 || !saved.StartsWith(config.ScreenshotDirectory + Path.DirectorySeparatorChar)) throw new Exception("Hotkey did not save in the configured folder.");
                    using (var bitmap = new Bitmap(saved))
                    {
                        if (bitmap.RawFormat.Guid != ImageFormat.Png.Guid || bitmap.Size != host.Bounds.Size) throw new Exception("Screenshot is not a native-resolution PNG.");
                        Color pixel = bitmap.GetPixel(32, bitmap.Height / 2);
                        if (pixel.ToArgb() != expected.ToArgb()) throw new Exception("Screenshot pixels do not match the selected display's test content: " + pixel);
                        report["dimensions"] = new { Width = bitmap.Width, Height = bitmap.Height };
                    }
                    report["globalHotkeyCapturesSelectedDisplay"] = true; report["nativeResolutionPngAndPixels"] = true; report["screenshot"] = saved;
                    string originalHash = MediaDependency.Hash(saved);
                    string second = ScreenshotCapture.Capture(Native.PrimaryOutputIndex(), config.ScreenshotDirectory);
                    if (second == saved || MediaDependency.Hash(saved) != originalHash) throw new Exception("Repeated screenshots overwrite an existing image.");
                    report["uniqueFilenamesAndExplicitDisplay"] = true;
                    bool rejected = false;
                    try { ScreenshotCapture.Capture(31, config.ScreenshotDirectory); } catch (IOException) { rejected = true; }
                    if (!rejected || Directory.GetFiles(config.ScreenshotDirectory, "*.partial.png").Length > 0) throw new Exception("Capture failure left an incomplete screenshot.");
                    report["invalidDisplayCleansIncompleteFiles"] = true;
                    string blocked = Path.Combine(data, "blocked-folder"); File.WriteAllText(blocked, "existing file"); rejected = false;
                    try { ScreenshotCapture.CaptureSource("color=blue:s=64x64", blocked, false); } catch (IOException) { rejected = true; }
                    if (!rejected || File.ReadAllText(blocked) != "existing file") throw new Exception("Invalid output path was not rejected safely.");
                    report["invalidDestinationPreservesFiles"] = true;
                    Native.UnregisterHotKey(hotkey.Handle, 3);
                    using (var occupied = new HotkeyWindow())
                    {
                        if (!Native.RegisterHotKey(occupied.Handle, 20, 7, 118)) throw new Exception("Could not create shortcut conflict fixture.");
                        rejected = false; try { hotkey.Register(config); } catch (InvalidOperationException) { rejected = true; }
                        Native.UnregisterHotKey(occupied.Handle, 20);
                        if (!rejected || hotkey.ScreenshotRegistered || !hotkey.SaveRegistered || !hotkey.OverlayRegistered) throw new Exception("Screenshot conflict disabled unrelated shortcuts.");
                        report["screenshotConflictPreservesOtherShortcuts"] = true;
                    }
                }
                var duplicate = new Settings { ScreenshotKey = 121, ScreenshotModifiers = 6 }; bool invalid = false;
                try { duplicate.Validate(); } catch (ArgumentException) { invalid = true; }
                if (!invalid) throw new Exception("Duplicate shortcut was allowed.");
                report["duplicateShortcutRejected"] = true; report["result"] = "passed";
            }
            catch (Exception ex) { report["result"] = "failed"; report["error"] = ex.ToString(); }
            File.WriteAllText(Path.Combine(data, "verification.json"), new JavaScriptSerializer().Serialize(report)); return report["result"].ToString() == "passed" ? 0 : 1;
        }
        internal static int Installed(string data)
        {
            var report = new Dictionary<string, object>();
            try
            {
                Native.SetProcessDPIAware(); Application.EnableVisualStyles();
                string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "F10Recorder");
                string statusPath = Path.Combine(root, "status.json"); var serializer = new JavaScriptSerializer();
                var before = serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(statusPath));
                var config = Settings.Load(Path.Combine(root, "settings.json"));
                if (config.DisplayIndex != -1) throw new Exception("Installed screenshot fixture expects the current primary-display configuration.");
                if (!(bool)before["ScreenshotShortcutRegistered"]) throw new Exception("The installed screenshot shortcut is unavailable.");
                Color pattern = Color.FromArgb(60, 110, 75); string saved = null;
                using (var host = new Form { Text = "F10 Installed Screenshot Test", TopMost = true, FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, Bounds = Screen.PrimaryScreen.Bounds, BackColor = pattern })
                {
                    host.Show(); Native.ShowWindow(host.Handle, 9); Native.SetForegroundWindow(host.Handle); Application.DoEvents(); host.Refresh();
                    Wait(delegate { return Native.GetForegroundWindow() == host.Handle; }, "Installed screenshot fixture could not receive focus.");
                    var keys = new List<ushort>(); if ((config.ScreenshotModifiers & 2) != 0) keys.Add(17); if ((config.ScreenshotModifiers & 1) != 0) keys.Add(18); if ((config.ScreenshotModifiers & 4) != 0) keys.Add(16); keys.Add((ushort)config.ScreenshotKey);
                    var input = keys.Select(key => new Input { Type = 1, Key = key }).Concat(keys.AsEnumerable().Reverse().Select(key => new Input { Type = 1, Key = key, Flags = 2 })).ToArray();
                    if (SendInput((uint)input.Length, input, 40) != input.Length) throw new Exception("Installed shortcut input was refused.");
                    Dictionary<string, object> current = null;
                    Wait(delegate
                    {
                        try { current = serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(statusPath)); } catch (IOException) { return false; } catch (ArgumentException) { return false; }
                        if (current == null) return false;
                        object path; if (current.TryGetValue("LastScreenshot", out path) && path != null && !object.Equals(path, before["LastScreenshot"])) saved = path.ToString();
                        return saved != null && !(bool)current["ScreenshotInProgress"];
                    }, "Installed hotkey did not save a screenshot.");
                    if ((bool)current["Paused"] != (bool)before["Paused"] || Native.GetForegroundWindow() != host.Handle || current["NotificationTitle"].ToString() != "Screenshot saved" || !(bool)current["NotificationVisible"]) throw new Exception("Installed screenshot changed recording/focus or did not show confirmation.");
                    using (var bitmap = new Bitmap(saved)) if (bitmap.Size != host.Bounds.Size || bitmap.GetPixel(32, bitmap.Height / 2).ToArgb() != pattern.ToArgb()) throw new Exception("Installed screenshot pixels do not match the controlled display.");
                    if (Path.GetDirectoryName(saved) != config.ScreenshotDirectory) throw new Exception("Installed screenshot ignored the configured directory.");
                    report["installedHotkeySavedPngInConfiguredFolder"] = true; report["controlledFullscreenPixelsVerified"] = true; report["recordingStateAndForegroundPreserved"] = true; report["confirmationNotification"] = true; report["screenshot"] = saved;
                }
                report["result"] = "passed";
            }
            catch (Exception ex) { report["result"] = "failed"; report["error"] = ex.ToString(); }
            File.WriteAllText(Path.Combine(data, "verification.json"), new JavaScriptSerializer().Serialize(report)); return report["result"].ToString() == "passed" ? 0 : 1;
        }
    }
}
