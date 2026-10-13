using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Web.Script.Serialization;

namespace F10Recorder
{
    internal static class Verification
    {
        internal static int SettingsWindow(string data)
        {
            var report = new Dictionary<string, object>();
            try
            {
                Native.SetProcessDPIAware(); System.Windows.Forms.Application.EnableVisualStyles();
                Settings submitted = null; var pauseRequests = new List<bool>();
                using (var form = new SettingsForm(new Settings(), data, delegate(Settings next, bool startup) { submitted = next; }, delegate(bool paused) { pauseRequests.Add(paused); }, true))
                {
                    form.Show(); System.Windows.Forms.Application.DoEvents();
                    report["visibleBeforeExplicitRestore"] = Native.IsWindowVisible(form.Handle);
                    form.ShowForUser(); System.Windows.Forms.Application.DoEvents();
                    if (!Native.IsWindowVisible(form.Handle)) throw new Exception("Settings stayed hidden after startup.");
                    report["visibleAfterHiddenLaunch"] = true;
                    form.Hide(); form.ShowForUser(); System.Windows.Forms.Application.DoEvents();
                    if (!Native.IsWindowVisible(form.Handle)) throw new Exception("Existing hidden settings could not be reopened.");
                    report["reopenHiddenWindow"] = true;
                    form.WindowState = System.Windows.Forms.FormWindowState.Minimized; form.ShowForUser(); System.Windows.Forms.Application.DoEvents();
                    if (form.WindowState != System.Windows.Forms.FormWindowState.Normal || !Native.IsWindowVisible(form.Handle)) throw new Exception("Minimized settings were not restored.");
                    report["restoreMinimizedWindow"] = true;
                    var wait = Stopwatch.StartNew();
                    while (!form.CodecCheckFinished && wait.Elapsed.TotalSeconds < 15) { System.Windows.Forms.Application.DoEvents(); Thread.Sleep(20); }
                    if (!form.CodecCheckFinished || form.AvailableCodecs.Length == 0) throw new Exception("Hardware codec check did not complete.");
                    report["availableCodecs"] = form.AvailableCodecs;
                    string[] pageNames = { "Capture", "Audio", "Controls", "Storage" };
                    for (int i = 0; i < pageNames.Length; i++)
                    {
                        form.SelectPage(i); System.Windows.Forms.Application.DoEvents();
                        var page = form.Controls.Find("Page" + pageNames[i], true).Single(); if (!page.Visible) throw new Exception(pageNames[i] + " page did not open.");
                        using (var bitmap = new System.Drawing.Bitmap(form.ClientSize.Width, form.ClientSize.Height)) { form.Controls[0].DrawToBitmap(bitmap, new System.Drawing.Rectangle(System.Drawing.Point.Empty, form.ClientSize)); bitmap.Save(Path.Combine(data, "settings-" + pageNames[i].ToLowerInvariant() + ".png")); if (i == 0) bitmap.Save(Path.Combine(data, "settings-preview.png")); }
                    }
                    report["allPagesVisible"] = true;
                    if (form.Controls.Find("FfmpegPath", true).Length != 0) throw new Exception("An editable recording-engine path is still visible.");
                    report["noEditableEnginePath"] = true;
                    form.Size = new System.Drawing.Size(700, 740); form.SelectPage(0); System.Windows.Forms.Application.DoEvents();
                    var applyButton = form.Controls.Find("ApplySettings", true).Single(); var buttonOrigin = form.PointToClient(applyButton.PointToScreen(System.Drawing.Point.Empty));
                    if (buttonOrigin.Y < 0 || buttonOrigin.Y + applyButton.Height > form.ClientSize.Height) throw new Exception("Apply button became unreachable at the minimum window size.");
                    report["minimumWindowSize"] = "passed";
                    var toggle = (System.Windows.Forms.CheckBox)form.Controls.Find("RecordingEnabled", true).Single(); toggle.Checked = true; toggle.Checked = false; form.SetRecordingState(true, "Paused", false);
                    if (pauseRequests.Count != 2 || pauseRequests[0] || !pauseRequests[1]) throw new Exception("Replay toggle did not dispatch the correct pause/resume requests.");
                    report["replayToggle"] = "passed";
                    ((NumberStepper)form.Controls.Find("ReplayMinutes", true).Single()).Value = 3;
                    ((NumberStepper)form.Controls.Find("BitrateMbps", true).Single()).Value = 42;
                    ((System.Windows.Forms.ComboBox)form.Controls.Find("FramesPerSecond", true).Single()).SelectedItem = 120;
                    ((System.Windows.Forms.CheckBox)form.Controls.Find("SystemAudio", true).Single()).Checked = false;
                    ((System.Windows.Forms.ComboBox)form.Controls.Find("OverlayKey", true).Single()).SelectedItem = "Z";
                    ((System.Windows.Forms.CheckBox)form.Controls.Find("OverlayCtrl", true).Single()).Checked = false;
                    ((System.Windows.Forms.CheckBox)form.Controls.Find("OverlayAlt", true).Single()).Checked = true;
                    ((System.Windows.Forms.CheckBox)form.Controls.Find("OverlayShift", true).Single()).Checked = false;
                    ((System.Windows.Forms.ComboBox)form.Controls.Find("ScreenshotKey", true).Single()).SelectedItem = "F7";
                    ((System.Windows.Forms.CheckBox)form.Controls.Find("ScreenshotAlt", true).Single()).Checked = true;
                    ((System.Windows.Forms.TextBox)form.Controls.Find("ScreenshotDirectory", true).Single()).Text = Path.Combine(data, "custom screenshots");
                    var selected = (System.Windows.Forms.ComboBox)form.Controls.Find("VideoCodec", true).Single(); if (form.AvailableCodecs.Contains("av1_nvenc")) selected.SelectedItem = selected.Items.Cast<EncoderOption>().First(option => option.Codec == "av1_nvenc");
                    if (!form.ApplySettings() || submitted == null || submitted.ReplayMinutes != 3 || submitted.BitrateMbps != 42 || submitted.FramesPerSecond != 120 || submitted.SystemAudio) throw new Exception("Settings edits did not reach Apply correctly.");
                    report["applyEditedValues"] = "passed";
                    if (submitted.OverlayKey != 90 || submitted.OverlayModifiers != 1 || submitted.OverlayHotkeyText != "Alt+Z") throw new Exception("Overlay shortcut edits were not applied.");
                    report["overlayShortcutEditing"] = "passed";
                    if (submitted.ScreenshotKey != 118 || submitted.ScreenshotModifiers != 7 || submitted.ScreenshotDirectory != Path.Combine(data, "custom screenshots")) throw new Exception("Screenshot shortcut or folder edits were not applied.");
                    report["screenshotSettingsEditing"] = "passed";
                }
                var config = new Settings { OutputDirectory = Path.Combine(data, "clips") };
                using (var engine = new ReplayEngine(config, data, true))
                { Thread.Sleep(300); if (engine.CaptureProcessId != 0) throw new Exception("A paused engine unexpectedly started capture."); }
                report["pausedEngineDoesNotCapture"] = true;
                report["result"] = "passed";
                File.WriteAllText(Path.Combine(data, "verification.json"), new JavaScriptSerializer().Serialize(report)); return 0;
            }
            catch (Exception ex) { report["result"] = "failed"; report["error"] = ex.ToString(); File.WriteAllText(Path.Combine(data, "verification.json"), new JavaScriptSerializer().Serialize(report)); return 1; }
        }
        internal static int CodecMedia(string data, string selectedCodec = null)
        {
            var report = new Dictionary<string, object>();
            try
            {
                var clips = new List<object>();
                var choices = EncoderCapabilities.Probe(new Settings().FfmpegPath);
                foreach (var choice in choices.Where(choice => selectedCodec == null || choice.Codec == selectedCodec))
                {
                    int[] rates = choice.Codec == "h264_nvenc" ? new[] { 24, 120 } : new[] { 60 };
                    foreach (int rate in rates)
                    {
                        var settings = new Settings { VideoCodec = choice.Codec, FramesPerSecond = rate, ReplayMinutes = 1, BitrateMbps = 5, OutputDirectory = Path.Combine(data, "clips") }; settings.Validate();
                        string root = Path.Combine(data, choice.Codec + "-" + rate); Directory.CreateDirectory(root);
                        using (var session = new CaptureSession(settings, root, true))
                        {
                            for (int i = 0; i < 8; i++) { Thread.Sleep(500); session.Maintain(); if (session.HasExited) throw new Exception(session.ErrorTail); }
                            string clip = session.Save(session.Position, settings.OutputDirectory);
                            double before = session.Position;
                            for (int i = 0; i < 3; i++) { Thread.Sleep(500); session.Maintain(); }
                            if (session.Position <= before) throw new Exception("Capture stopped after " + choice.Name + " save.");
                            clips.Add(new { Encoder = choice.Codec, FramesPerSecond = rate, Clip = clip });
                        }
                    }
                }
                if (clips.Count == 0) throw new Exception("No hardware codecs available.");
                report["clips"] = clips; report["result"] = "passed"; File.WriteAllText(Path.Combine(data, "verification.json"), new JavaScriptSerializer().Serialize(report)); return 0;
            }
            catch (Exception ex) { report["result"] = "failed"; report["error"] = ex.ToString(); File.WriteAllText(Path.Combine(data, "verification.json"), new JavaScriptSerializer().Serialize(report)); return 1; }
        }
        internal static int Run(string data, bool synthetic)
        {
            var report = new Dictionary<string, object>();
            try
            {
                var config = new Settings { ReplayMinutes = 1, FramesPerSecond = 30, BitrateMbps = 5, OutputDirectory = Path.Combine(data, "clips") };
                config.Validate();
                string root = Path.Combine(data, "test-buffer"); Directory.CreateDirectory(root);
                // Boundary selection must exclude the oldest partial GOP and future frames.
                var segments = Enumerable.Range(0, 10).Select(i => new Segment { Start = i, End = i + 1, Path = i.ToString() }).ToList();
                var selected = ReplaySelection.Select(segments, 9.4, 5);
                if (selected.First().Start != 5 || selected.Last().Start != 9) throw new Exception("Replay boundary selection failed.");
                report["selection"] = "passed";
                if (CaptureSession.Quote(@"C:\some folder\") != "\"C:\\some folder\\\\\"") throw new Exception("Argument quoting failed.");
                report["quoting"] = "passed";
                string csv = Path.Combine(data, "test.csv");
                File.WriteAllText(csv, "\"chunk,quoted.ts\",0.000000,1.000000\nchunk-1.ts,1.000000,2.000000");
                if (ReplaySelection.Read(csv, data).Count != 1) throw new Exception("CSV partial-line or quoted-comma parsing failed.");
                report["csv"] = "passed";
                using (var form = new SettingsForm(config, data, delegate { }))
                using (var bitmap = new System.Drawing.Bitmap(form.Width, form.Height))
                { form.StartPosition = System.Windows.Forms.FormStartPosition.Manual; form.Location = new System.Drawing.Point(-10000, -10000); form.ShowInTaskbar = false; form.Show(); System.Windows.Forms.Application.DoEvents(); form.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, form.Width, form.Height)); bitmap.Save(Path.Combine(data, "settings-preview.png")); form.Hide(); }
                using (var session = new CaptureSession(config, root, synthetic))
                {
                    for (int i = 0; i < 30; i++) { Thread.Sleep(500); session.Maintain(); if (session.HasExited) throw new Exception(session.ErrorTail); }
                    double end = session.Position;
                    string clip = session.Save(end, config.OutputDirectory);
                    report["clip"] = clip; report["requestedEndSeconds"] = end; report["bufferedSeconds"] = session.BufferedSeconds;
                    double before = session.Position;
                    for (int i = 0; i < 6; i++) { Thread.Sleep(500); session.Maintain(); }
                    if (session.Position <= before + 1) throw new Exception("Capture did not continue after save.");
                    report["continuousCaptureAfterSave"] = true;
                    report["audio"] = synthetic ? "synthetic sine" : "WASAPI default render endpoint";
                    report["source"] = synthetic ? "synthetic moving pattern" : "primary monitor";
                }
                report["result"] = "passed";
                File.WriteAllText(Path.Combine(data, "verification.json"), new JavaScriptSerializer().Serialize(report));
                return 0;
            }
            catch (Exception ex) { Log.Write(ex.ToString()); report["result"] = "failed"; report["error"] = ex.ToString(); File.WriteAllText(Path.Combine(data, "verification.json"), new JavaScriptSerializer().Serialize(report)); return 1; }
        }
        internal static int Resilience(string data)
        {
            var report = new Dictionary<string, object>();
            try
            {
                var settings = new Settings { ReplayMinutes = 1, FramesPerSecond = 30, BitrateMbps = 5, OutputDirectory = Path.Combine(data, "clips") };
                using (var ready = new ManualResetEvent(false))
                using (var saved = new ManualResetEvent(false))
                using (var engine = new ReplayEngine(settings, data))
                {
                    string failure = null, clip = null;
                    engine.StateChanged += delegate(string status, bool isReady) { if (isReady) ready.Set(); };
                    engine.SaveFinished += delegate(string message, bool success) { if (success) clip = message; else failure = message; saved.Set(); };
                    if (!ready.WaitOne(20000)) throw new Exception("Initial capture did not become ready.");
                    int firstPid = engine.CaptureProcessId; ready.Reset();
                    using (var process = Process.GetProcessById(firstPid)) process.Kill();
                    if (!ready.WaitOne(25000)) throw new Exception("Capture did not recover after encoder kill.");
                    int secondPid = engine.CaptureProcessId;
                    if (secondPid == 0 || secondPid == firstPid) throw new Exception("The failed encoder was not replaced.");
                    report["encoderCrashRecovery"] = true;
                    string tone = Path.Combine(data, "tone.wav"); WriteTone(tone);
                    using (var player = new System.Media.SoundPlayer(tone))
                    {
                        // Exercise silence -> sound -> silence -> sound on the actual WASAPI endpoint.
                        for (int i = 0; i < 76; i++) { if (i == 10 || i == 55) player.Play(); Thread.Sleep(1000); }
                    }
                    int chunks = Directory.GetFiles(Path.Combine(data, "buffer"), "chunk-*.*", SearchOption.AllDirectories).Length;
                    if (chunks > 74 || chunks < 55) throw new Exception("The one-minute rolling buffer did not prune correctly: " + chunks);
                    report["retainedChunks"] = chunks;
                    engine.Save();
                    if (!saved.WaitOne(20000) || failure != null) throw new Exception(failure ?? "Replay save did not complete.");
                    report["clip"] = clip;
                    Thread.Sleep(1500);
                    if (engine.CaptureProcessId != secondPid) throw new Exception("Encoder changed unexpectedly after save.");
                    report["continuousCaptureAfterSave"] = true;
                }
                report["result"] = "passed";
                File.WriteAllText(Path.Combine(data, "verification.json"), new JavaScriptSerializer().Serialize(report)); return 0;
            }
            catch (Exception ex) { Log.Write(ex.ToString()); report["result"] = "failed"; report["error"] = ex.ToString(); File.WriteAllText(Path.Combine(data, "verification.json"), new JavaScriptSerializer().Serialize(report)); return 1; }
        }
        static void WriteTone(string path)
        {
            const int rate = 48000, samples = 24000;
            using (var writer = new BinaryWriter(File.Create(path)))
            {
                writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + samples * 2); writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
                writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(rate); writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(samples * 2);
                for (int i = 0; i < samples; i++) writer.Write((short)(Math.Sin(2 * Math.PI * 440 * i / rate) * 1600));
            }
        }
    }
}
