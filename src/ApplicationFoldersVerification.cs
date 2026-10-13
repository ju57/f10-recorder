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
    internal static class ApplicationFoldersVerification
    {
        [StructLayout(LayoutKind.Sequential)] struct WindowBounds { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Explicit, Size = 40)] struct MouseInput { [FieldOffset(20)] public uint Flags; }
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window, out WindowBounds bounds);
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point point);
        [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] static extern uint SendInput(uint count, MouseInput[] inputs, int size);
        static void ActivateFixture(IntPtr window, int expectedProcess)
        {
            uint owner; Native.GetWindowThreadProcessId(window, out owner); if (owner != expectedProcess) throw new Exception("Focus fixture no longer owns its window.");
            Native.SetForegroundWindow(window);
            if (Native.GetForegroundWindow() != window)
            {
                Native.SetWindowPos(window, new IntPtr(-1), 0, 0, 0, 0, 0x13);
                WindowBounds bounds; if (!GetWindowRect(window, out bounds)) throw new Exception("Focus fixture bounds are unavailable.");
                Point target = new Point(bounds.Left + 32, bounds.Bottom - 32), previous = Cursor.Position;
                if (WindowFromPoint(target) != window) throw new Exception("Focus test click would target another application.");
                SetCursorPos(target.X, target.Y); SendInput(2, new[] { new MouseInput { Flags = 2 }, new MouseInput { Flags = 4 } }, 40); SetCursorPos(previous.X, previous.Y);
            }
            Wait(delegate { return Native.GetForegroundWindow() == window; }, "Fixture did not receive foreground input focus.");
        }
        internal static int Fixture()
        {
            Native.SetProcessDPIAware(); Application.EnableVisualStyles();
            string name = Path.GetFileNameWithoutExtension(Application.ExecutablePath);
            using (var form = new Form { Text = "F10 Application Fixture: " + name, TopMost = true, BackColor = UiTheme.Background, Size = new Size(640, 360), StartPosition = FormStartPosition.CenterScreen })
            {
                form.Controls.Add(new Label { Text = "Application identity test: " + name, ForeColor = UiTheme.Text, Font = UiTheme.Font(18), AutoSize = true, Location = new Point(24, 50) });
                form.Show(); Native.ShowWindow(form.Handle, 9); Native.SetForegroundWindow(form.Handle); Application.Run(form);
            }
            return 0;
        }
        static void Wait(Func<bool> condition, string failure)
        {
            var clock = Stopwatch.StartNew(); while (clock.Elapsed.TotalSeconds < 20) { Application.DoEvents(); if (condition()) return; Thread.Sleep(20); } throw new Exception(failure);
        }
        static Process StartFixture(string data, string name, out IntPtr window)
        {
            string directory = Path.Combine(data, "fixture-" + name); Directory.CreateDirectory(directory); string exe = Path.Combine(directory, name + ".exe");
            File.Copy(Application.ExecutablePath, exe);
            var process = Process.Start(new ProcessStartInfo { FileName = exe, Arguments = "--application-fixture --data-dir " + CaptureSession.Quote(directory), UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
            IntPtr found = IntPtr.Zero;
            Wait(delegate { found = Native.FindWindow(null, "F10 Application Fixture: " + name); return found != IntPtr.Zero; }, "Test application did not create its window.");
            window = found; uint pid; Native.GetWindowThreadProcessId(window, out pid); if (pid != process.Id) throw new Exception("Test window does not belong to its fixture process.");
            ActivateFixture(window, process.Id); return process;
        }
        internal static int Run(string data)
        {
            var report = new Dictionary<string, object>(); var fixtures = new List<Process>(); var windows = new List<IntPtr>();
            try
            {
                Native.SetProcessDPIAware(); Application.EnableVisualStyles();
                string clipsRoot = Path.Combine(data, "configured clips"); Directory.CreateDirectory(clipsRoot);
                string original = Path.Combine(clipsRoot, "existing.mp4"); File.WriteAllText(original, "existing clip remains untouched"); string originalHash = MediaDependency.Hash(original);
                foreach (string name in new[] { "..", "../outside", "Game: Name?", "NUL.txt", "CON", "COM¹", "LPT9.log", "Game. ", " ", "A\0B", new string('A', 200), "Jogo português" })
                {
                    string destination = ApplicationFolders.Destination(clipsRoot, name);
                    if (Path.GetDirectoryName(destination) != clipsRoot) throw new Exception("Unsafe application name escaped the clips root.");
                    Directory.CreateDirectory(destination);
                }
                if (ApplicationFolders.FolderName("NUL.txt") != "_NUL.txt" || ApplicationFolders.FolderName("COM¹") != "_COM¹" || ApplicationFolders.FolderName("..").Equals("..")) throw new Exception("Reserved/dot names were not normalized.");
                report["safeWindowsFolderNamesAndBoundaries"] = true;
                if (ApplicationFolders.DisplayName(null, "FallbackGame") != "FallbackGame" || ApplicationFolders.DisplayName(null, null) != "Unknown application" || ApplicationFolders.FolderName(null) != "Desktop") throw new Exception("Application identity fallbacks failed.");
                report["fallbackNames"] = true;
                using (var tracker = new ForegroundApplicationTracker())
                using (var dispatcher = new Control())
                {
                    dispatcher.CreateControl(); IntPtr firstWindow, secondWindow;
                    fixtures.Add(StartFixture(data, "Fixture Game", out firstWindow)); windows.Add(firstWindow);
                    string firstName = tracker.Sample();
                    if (firstName != "Fixture Game" || ApplicationFolders.WindowApplication(firstWindow, 0) != firstName) throw new Exception("Foreground executable was not identified: " + firstName);
                    string path = Native.ProcessImagePath((uint)fixtures[0].Id); if (path == null || Path.GetFileNameWithoutExtension(path) != "Fixture Game") throw new Exception("Limited-information image lookup did not identify the executable.");
                    report["foregroundProcessIdentity"] = true;
                    var config = new Settings { ReplayMinutes = 1, FramesPerSecond = 30, BitrateMbps = 5, OutputDirectory = clipsRoot };
                    var clips = new List<string>(); string failure = null; bool ready = false;
                    using (var engine = new ReplayEngine(config, data, false, true))
                    {
                        engine.StateChanged += delegate(string message, bool active) { if (active) ready = true; };
                        engine.SaveFinished += delegate(string message, bool success) { dispatcher.BeginInvoke(new Action(delegate { if (success) clips.Add(message); else failure = message; })); };
                        Wait(delegate { return ready; }, "Synthetic capture did not become ready."); int capturePid = engine.CaptureProcessId;
                        ActivateFixture(firstWindow, fixtures[0].Id);
                        engine.Save(tracker.Sample());
                        // Switch the foreground before async save completion; its destination must already be fixed.
                        fixtures.Add(StartFixture(data, "Second App", out secondWindow)); windows.Add(secondWindow);
                        Wait(delegate { return clips.Count == 1 || failure != null; }, "First grouped replay did not save.");
                        if (failure != null) throw new Exception(failure);
                        if (Path.GetDirectoryName(clips[0]) != Path.Combine(clipsRoot, "Fixture Game")) throw new Exception("Async save followed a later application switch.");
                        ActivateFixture(secondWindow, fixtures[1].Id);
                        string secondName = tracker.Sample(); if (secondName != "Second App") throw new Exception("Application switch was not detected.");
                        engine.Save(secondName); Wait(delegate { return clips.Count == 2 || failure != null; }, "Second grouped replay did not save.");
                        if (failure != null) throw new Exception(failure);
                        if (Path.GetDirectoryName(clips[1]) != Path.Combine(clipsRoot, "Second App")) throw new Exception("The second application did not get its own directory.");
                        report["applicationFoldersAndSaveTimeSnapshot"] = true;
                        using (var settings = new SettingsForm(config, data, delegate { }))
                        using (var overlay = new OverlayForm(settings, config.OverlayHotkeyText, delegate { engine.Save(tracker.Sample()); }))
                        {
                            overlay.ShowForUser(); Application.DoEvents();
                            if (tracker.Sample() != "Second App") throw new Exception("F10 overlay replaced the game identity.");
                            ((Button)overlay.Controls.Find("OverlaySaveReplay", true).Single()).PerformClick();
                            Wait(delegate { return clips.Count == 3 || failure != null; }, "Overlay grouped replay did not save.");
                            if (failure != null) throw new Exception(failure);
                            if (Path.GetDirectoryName(clips[2]) != Path.Combine(clipsRoot, "Second App")) throw new Exception("Overlay save used F10 Recorder's name.");
                            overlay.Dismiss();
                        }
                        if (engine.CaptureProcessId != capturePid || clips.Any(clip => !File.Exists(clip))) throw new Exception("Recording stopped or grouped clips are missing.");
                        report["overlayRetainsForegroundApplication"] = true; report["captureContinuesAfterGroupedSaves"] = true;
                    }
                    if (ApplicationFolders.WindowApplication(Native.GetShellWindow(), 0) != "Desktop") throw new Exception("Desktop shell was not recognized.");
                    report["desktopFallback"] = true; report["clips"] = clips;
                }
                if (MediaDependency.Hash(original) != originalHash) throw new Exception("An existing clip was changed.");
                report["existingClipsPreserved"] = true; report["result"] = "passed";
            }
            catch (Exception ex) { report["result"] = "failed"; report["error"] = ex.ToString(); }
            finally
            {
                for (int i = 0; i < fixtures.Count; i++)
                {
                    var process = fixtures[i];
                    if (!process.HasExited)
                    {
                        uint owner; Native.GetWindowThreadProcessId(windows[i], out owner);
                        if (owner == process.Id) Native.PostMessage(windows[i], 0x10, IntPtr.Zero, IntPtr.Zero);
                        if (!process.WaitForExit(5000)) process.Kill();
                    }
                    process.Dispose();
                }
            }
            File.WriteAllText(Path.Combine(data, "verification.json"), new JavaScriptSerializer().Serialize(report)); return report["result"].ToString() == "passed" ? 0 : 1;
        }
        internal static int Installed(string data)
        {
            var report = new Dictionary<string, object>(); Process fixture = null; IntPtr window = IntPtr.Zero, control = Native.FindWindow(null, HotkeyWindow.ControlTitle); bool opened = false;
            try
            {
                string status = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "F10Recorder", "status.json");
                var serializer = new JavaScriptSerializer(); var before = serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(status));
                fixture = StartFixture(data, "Fixture Game", out window);
                Dictionary<string, object> current = null;
                Func<bool> matches = delegate
                {
                    try { current = serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(status)); } catch (IOException) { return false; } catch (ArgumentException) { return false; }
                    return current != null && current.ContainsKey("CurrentApplication") && current["CurrentApplication"].ToString() == "Fixture Game" && current["ApplicationFolder"].ToString() == "Fixture Game";
                };
                Wait(matches, "Installed recorder did not detect the external application.");
                if (!(bool)before["OverlayVisible"]) { if (!Native.PostMessage(control, HotkeyWindow.CommandMessage, new IntPtr(5), IntPtr.Zero)) throw new Exception("Could not open installed overlay."); opened = true; }
                else Native.PostMessage(control, HotkeyWindow.CommandMessage, new IntPtr(1), IntPtr.Zero);
                Wait(delegate { return matches() && (bool)current["OverlayVisible"]; }, "Installed overlay replaced or lost the application identity.");
                if ((bool)current["Paused"] != (bool)before["Paused"]) throw new Exception("Application detection changed recording state.");
                report["installedDetectsExternalApplication"] = true; report["installedOverlayPreservesApplicationName"] = true; report["recordingStatePreserved"] = true; report["result"] = "passed";
            }
            catch (Exception ex) { report["result"] = "failed"; report["error"] = ex.ToString(); }
            finally
            {
                if (opened) Native.PostMessage(control, HotkeyWindow.CommandMessage, new IntPtr(5), IntPtr.Zero);
                if (fixture != null) { if (!fixture.HasExited) { uint owner; Native.GetWindowThreadProcessId(window, out owner); if (owner == fixture.Id) Native.PostMessage(window, 0x10, IntPtr.Zero, IntPtr.Zero); if (!fixture.WaitForExit(5000)) fixture.Kill(); } fixture.Dispose(); }
            }
            File.WriteAllText(Path.Combine(data, "verification.json"), new JavaScriptSerializer().Serialize(report)); return report["result"].ToString() == "passed" ? 0 : 1;
        }
    }
}
