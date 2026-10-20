using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Forms = System.Windows.Forms;

namespace F10Recorder;

// Opt-in live trial: records only its owned fixture and default-system audio.
// No startup changes, game launch or vendor controls. Decode after process cleanup.
internal static class WindowCaptureChecks
{
    internal static void Fixture(string data)
    {
        Forms.Application.EnableVisualStyles();
        using var form = new SourceWindow { Text = "F10 selected-window acceptance", Width = 1024, Height = 700, StartPosition = Forms.FormStartPosition.Manual, Left = 500, Top = 100 };
        Forms.Form duplicate = null;
        string command = Path.Combine(data, "fixture-command.txt");
        using var timer = new Forms.Timer { Interval = 50 };
        timer.Tick += (_, _) =>
        {
            if (!File.Exists(command)) return;
            string action;
            try { action = File.ReadAllText(command); File.Delete(command); } catch (IOException) { return; }
            switch (action)
            {
                case "minimize": form.WindowState = Forms.FormWindowState.Minimized; break;
                case "restore": form.WindowState = Forms.FormWindowState.Normal; break;
                case "resize": form.Size = new System.Drawing.Size(820, 540); break;
                case "duplicate": duplicate = new SourceWindow { Text = "F10 ambiguous second source", Width = 600, Height = 400 }; duplicate.Show(); break;
                case "removeDuplicate": duplicate?.Close(); duplicate = null; break;
                case "close": form.Close(); break;
            }
        };
        timer.Start(); Forms.Application.Run(form); duplicate?.Dispose();
    }
    sealed class SourceWindow : Forms.Form
    {
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly Forms.Timer timer = new() { Interval = 16 };
        internal SourceWindow() { DoubleBuffered = true; BackColor = System.Drawing.Color.FromArgb(32, 72, 52); timer.Tick += (_, _) => Invalidate(); timer.Start(); }
        protected override void OnPaint(Forms.PaintEventArgs e)
        {
            base.OnPaint(e);
            int x = (int)(clock.Elapsed.TotalSeconds * 200) % Math.Max(1, ClientSize.Width - 60);
            e.Graphics.FillRectangle(System.Drawing.Brushes.Cyan, x, 200, 50, 50);
            using var font = new System.Drawing.Font("Segoe UI", 20);
            e.Graphics.DrawString("Selected window · " + clock.Elapsed.TotalMilliseconds.ToString("0"), font, System.Drawing.Brushes.White, 30, 40);
        }
        protected override void Dispose(bool disposing) { if (disposing) timer.Dispose(); base.Dispose(disposing); }
    }
    internal static int Run(string data, string desktop)
    {
        Directory.CreateDirectory(data);
        var report = new Dictionary<string, object>();
        var samples = new List<WorkerStatus>(); var clips = new List<string>();
        Process fixture = null, worker = null, ui = null;
        Process Launch(string path, params string[] args)
        {
            var info = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in args) info.ArgumentList.Add(arg);
            return Process.Start(info);
        }
        void Require(bool condition, string reason) { if (!condition) throw new Exception(reason); }
        async Task<WorkerReply> Call(string command = "status", bool value = false, long after = -1) => await WorkerProtocol.CallAsync(data, new WorkerRequest { Command = command, Value = value, AfterEvent = after });
        async Task<WorkerStatus> Until(Func<WorkerStatus, bool> condition, string reason, int seconds = 30)
        {
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < seconds)
            {
                var response = await Call(); Require(response.Error == null, response.Error); samples.Add(response.Status);
                if (condition(response.Status)) return response.Status;
                await Task.Delay(100);
            }
            throw new TimeoutException(reason);
        }
        async Task<string> SaveClip()
        {
            long sequence = (await Call()).Status.Sequence;
            Require((await Call("save")).Error == null, "Save was rejected.");
            for (int i = 0; i < 200; i++)
            {
                var response = await Call(after: sequence); samples.Add(response.Status);
                var saved = response.Status.Events.FirstOrDefault(e => e.Kind == "saved");
                if (saved != null) { Require(saved.Success && File.Exists(saved.Message), "Replay save failed: " + saved.Message); clips.Add(saved.Message); return saved.Message; }
                await Task.Delay(100);
            }
            throw new TimeoutException("No save completion.");
        }
        void Command(string action) => File.WriteAllText(Path.Combine(data, "fixture-command.txt"), action);
        async Task Check()
        {
            fixture = Launch(Environment.ProcessPath, "--window-fixture", data);
            WindowCaptureChoice selected = null;
            for (int i = 0; i < 100 && selected == null; i++) { fixture.Refresh(); selected = WindowCaptureTarget.Inspect(fixture.MainWindowHandle); if (selected == null) await Task.Delay(100); }
            Require(selected != null, "Owned fixture did not appear."); report["target"] = new { selected.Executable, selected.ClassName, selected.Handle, selected.ProcessId, selected.ProcessStartedUtcTicks };
            var settings = new Settings { CaptureMode = "window", WindowExecutable = selected.Executable, WindowClass = selected.ClassName, ReplayMinutes = 1, FramesPerSecond = 30, BitrateMbps = 5,
                OutputDirectory = Path.Combine(data, "clips"), ScreenshotDirectory = Path.Combine(data, "screenshots"), SaveKey = 114, SaveModifiers = 3, OverlayKey = 115, OverlayModifiers = 3, ScreenshotKey = 116, ScreenshotModifiers = 3 };
            settings.Save(Path.Combine(data, "settings.json"));
            worker = Launch(desktop, "--recorder-worker", "--data-dir", data);
            var initial = await Until(s => s.Ready && s.CanSave && s.Metrics.BufferedSeconds >= 4, "Selected-window worker did not become ready.");
            Require(initial.WorkerProcessId == worker.Id && initial.Phase == CapturePhase.Buffering, "Wrong worker/state.");
            report["initial"] = initial;
            ui = Launch(desktop, "--data-dir", data, "--overlay");
            long overlay = 0;
            for (int i = 0; i < 100 && overlay == 0; i++)
            {
                try { using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(data, "status.json"))); if (json.RootElement.GetProperty("OverlayVisible").GetBoolean()) overlay = json.RootElement.GetProperty("OverlayWindow").GetInt64(); } catch (Exception ex) when (ex is IOException or JsonException) { }
                await Task.Delay(100);
            }
            uint affinity = uint.MaxValue;
            Require(overlay != 0 && GetWindowDisplayAffinity(new IntPtr(overlay), out affinity) && affinity == 0, "Window-mode sidebar was not an ordinary nonexcluded window.");
            report["overlayAffinity"] = affinity;
            await Task.Delay(1500); await SaveClip();
            // UI detaches without stopping the worker; save no longer depends on UI.
            ui.Kill(); ui.WaitForExit(10000); ui.Dispose(); ui = null;
            Command("minimize");
            var minimized = await Until(s => s.Phase == CapturePhase.TargetMinimized && !s.Ready && s.Metrics.CaptureProcessId == 0 && s.CanSave, "Minimize did not clear readiness and preserve eligible footage.");
            report["minimized"] = minimized; await SaveClip();
            Command("restore");
            var restored = await Until(s => s.Ready && s.CanSave && s.Metrics.CaptureProcessId != initial.Metrics.CaptureProcessId && s.Metrics.BufferedSeconds >= 3, "Restore did not rebuild the selected-window buffer.");
            report["restored"] = restored;
            Command("resize"); await Task.Delay(2000); await SaveClip();
            Command("duplicate");
            report["ambiguous"] = await Until(s => s.Phase == CapturePhase.WaitingForTarget && !s.Ready && s.Metrics.CaptureProcessId == 0 && s.CanSave, "Ambiguous windows did not fail closed with retained footage.");
            Command("removeDuplicate");
            await Until(s => s.Ready && s.Metrics.BufferedSeconds >= 3, "Resolving ambiguity did not resume capture.");
            Command("close");
            report["closed"] = await Until(s => s.Phase == CapturePhase.TargetClosed && !s.Ready && s.Metrics.CaptureProcessId == 0 && s.CanSave, "Source close lost eligible footage or advertised live capture.");
            await SaveClip();
            await Call("screenshot");
            var failedShot = await Until(s => !s.ScreenshotInProgress, "Missing-target screenshot did not finish.");
            Require(string.IsNullOrEmpty(failedShot.LastScreenshot), "Missing target silently produced a screenshot.");
            await Call("pause", true);
            await Until(s => s.Paused && !s.Ready && !s.CanSave, "Deliberate pause was not retained.");
            await Call("quit"); worker.WaitForExit(15000);
            worker.Dispose(); worker = null;
            worker = Launch(desktop, "--recorder-worker", "--data-dir", data);
            report["pausedRestart"] = await Until(s => s.Paused && !s.Ready && !s.CanSave && s.Metrics.CaptureProcessId == 0, "Worker restart lost deliberate pause.");
            report["result"] = "passed";
        }
        try { Check().GetAwaiter().GetResult(); }
        catch (Exception ex) { report["result"] = "failed"; report["error"] = ex.ToString(); }
        finally
        {
            if (worker != null) { try { Call("quit").GetAwaiter().GetResult(); worker.WaitForExit(15000); } catch { } if (!worker.HasExited) worker.Kill(true); worker.Dispose(); }
            if (ui != null) { if (!ui.HasExited) { ui.Kill(); ui.WaitForExit(10000); } ui.Dispose(); }
            if (fixture != null) { if (!fixture.HasExited) { Command("close"); if (!fixture.WaitForExit(5000)) fixture.Kill(true); } fixture.Dispose(); }
            report["clips"] = clips; report["samples"] = samples;
            report["scope"] = "Real owned window + WASAPI + production worker/sidebar. No game, reboot, coded A/V markers, long-session drift or cadence/performance acceptance.";
            File.WriteAllText(Path.Combine(data, "verification.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions(WorkerProtocol.Json) { WriteIndented = true }));
        }
        return (string)report["result"] == "passed" ? 0 : 1;
    }
    [DllImport("user32.dll")] static extern bool GetWindowDisplayAffinity(IntPtr window, out uint affinity);
}
