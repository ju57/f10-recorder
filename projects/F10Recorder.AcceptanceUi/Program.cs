using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace F10Recorder;

// Hosts the unchanged frozen Avalonia App and its real WorkerRecorder in a
// separate process. Only authority/owned lifecycle are supplied by the test.
internal static class AcceptanceUi
{
    static void Need(bool value, string reason) { if (!value) throw new IOException(reason); }
    static void Write(string root, string name, object value)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions(WorkerProtocol.Json) { WriteIndented = true });
        Need(bytes.Length <= 65536, "UI receipt bound.");
        string target = Path.Combine(root, name), pending = target + ".pending";
        using (var output = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { output.Write(bytes); output.Flush(true); }
        File.Move(pending, target); // Readers only see a complete, closed receipt.
    }
    // Assigned before touching App. It cannot create a fallback SDK worker or
    // encoder child, including one too short-lived for the outer CIM sampler.
    // No kill-on-close flag: this job contains only this task-owned UI process.
    sealed class SingleProcessJob : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)] struct BasicLimit
        {
            public long ProcessTime, JobTime; public uint Flags;
            public UIntPtr Minimum, Maximum; public uint Active; public UIntPtr Affinity; public uint Priority, Scheduling;
        }
        [StructLayout(LayoutKind.Sequential)] struct Accounting
        {
            public long UserTime, KernelTime, PeriodUserTime, PeriodKernelTime;
            public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetInformationJobObject(IntPtr job, int kind, ref BasicLimit limit, uint size);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool QueryInformationJobObject(IntPtr job, int kind, out Accounting information, uint size, out uint returned);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        IntPtr handle;
        internal SingleProcessJob()
        {
            Need(IntPtr.Size == 8 && Marshal.SizeOf<BasicLimit>() == 64 && Marshal.SizeOf<Accounting>() == 48, "Expected x64 job ABI.");
            handle = CreateJobObject(IntPtr.Zero, null); if (handle == IntPtr.Zero) throw new Win32Exception();
            try
            {
                var limit = new BasicLimit { Flags = 0x8, Active = 1 };
                if (!SetInformationJobObject(handle, 2, ref limit, (uint)Marshal.SizeOf<BasicLimit>())) throw new Win32Exception();
                using var self = Process.GetCurrentProcess();
                if (!AssignProcessToJobObject(handle, self.Handle)) throw new Win32Exception();
                Check();
            }
            catch { Dispose(); throw; }
        }
        internal object Check()
        {
            if (!QueryInformationJobObject(handle, 1, out var information, 48, out uint returned)) throw new Win32Exception();
            Need(returned == 48 && information.TotalProcesses == 1 && information.ActiveProcesses == 1 && information.TerminatedProcesses == 0,
                "UI must remain the only process ever admitted to its job; fallback attempts fail this check.");
            return new { totalProcesses = information.TotalProcesses, activeProcesses = information.ActiveProcesses,
                terminatedProcesses = information.TerminatedProcesses, activeProcessLimit = 1, breakawayAllowed = false };
        }
        public void Dispose() { if (handle != IntPtr.Zero) { CloseHandle(handle); handle = IntPtr.Zero; } }
    }
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length != 6) return 2;
        using var job = new SingleProcessJob();
        string data = Path.GetFullPath(args[0]), authority = Path.GetFullPath(args[1]), evidence = Path.GetFullPath(args[2]);
        string artifacts = Path.GetFullPath("artifacts") + Path.DirectorySeparatorChar;
        Need(new[] { data, authority, evidence }.All(p => p.StartsWith(artifacts, StringComparison.OrdinalIgnoreCase))
            && Directory.Exists(data) && Directory.Exists(authority) && Directory.Exists(evidence), "Existing private artifact directories required.");
        int workerPid = int.Parse(args[3]); long workerBirthTicks = long.Parse(args[4]); string instance = args[5];
        Need(workerPid > 0 && workerBirthTicks > 0 && !string.IsNullOrWhiteSpace(instance), "Exact existing worker identity required.");
        using var worker = Process.GetProcessById(workerPid); _ = worker.SafeHandle;
        Need(worker.StartTime.ToUniversalTime().Ticks == workerBirthTicks && !worker.HasExited, "Existing worker PID/birth mismatch.");
        ProfileOwnership.RootDirectory = authority;
        MediaDependency.RootDirectory = Path.Combine(data, "unavailable-media");
        Need(!File.Exists(MediaDependency.ExecutablePath), "No media executable allowed in the private disabled root.");
        RecorderHost.Initialize(data);
        using var lookup = new CancellationTokenSource(2000);
        var reply = WorkerProtocol.CallAsync(data, new WorkerRequest(), lookup.Token).GetAwaiter().GetResult();
        Need(reply.Error == null && reply.Status?.Instance == instance && reply.Status.Metrics.WorkerProcessId == workerPid
            && reply.Status.Paused && reply.Status.SaveRegistered && reply.Status.OverlayRegistered && reply.Status.ScreenshotRegistered,
            "The actual private current-user worker channel must already match.");
        using var ownership = ProfileOwnership.TryAcquireUi(data, out string bound);
        Need(ownership != null && WorkerIdentity.DataPath(bound) == WorkerIdentity.DataPath(data), "Private UI ownership required.");
        App.DataDirectory = data; App.ControlTitle = WorkerIdentity.ControlTitle(data); App.Arguments = ["--paused"];
        App.RecorderFactory = null; // Actual production WorkerRecorder, never a direct/synthetic host.
        Exception failure = null; int uiExitCode = -1; bool requestedStop = false, started = false; var clock = Stopwatch.StartNew(); DispatcherTimer timer = null;
        FieldInfo entered = typeof(SettingsPanel).GetField("loaded", BindingFlags.Instance | BindingFlags.NonPublic);
        Need(entered?.DeclaringType == typeof(SettingsPanel) && entered.FieldType == typeof(bool), "Exact sticky SettingsPanel.loaded Boolean field required.");
        App active = null; int enterChecks = 0;
        void CheckHiddenPanel(App app)
        {
            enterChecks++;
            Need(!(bool)entered.GetValue(app.SettingsWindow.Panel) && !app.SettingsWindow.IsVisible && !app.SettingsWindow.OverlayVisible,
                "Hidden UI entered settings/probe path or became visible; this run must fail.");
        }
        App.Started = app =>
        {
            active = app; CheckHiddenPanel(app);
            Need(app.Recorder is WorkerRecorder && app.Recorder.Metrics.WorkerProcessId == workerPid && app.Recorder.Paused
                && app.Recorder.SaveRegistered && app.Recorder.OverlayRegistered && app.Recorder.ScreenshotRegistered, "Actual registered paused worker attachment required.");
            started = true;
            using var self = Process.GetCurrentProcess();
            Write(evidence, "ui-ready.json", new { pid = Environment.ProcessId, birthUtc = self.StartTime.ToUniversalTime(), workerPid, workerBirthTicks,
                instance, actualWorkerRecorder = true, job = job.Check(), mediaRoot = MediaDependency.RootDirectory,
                settingsEnterObserved = (bool)entered.GetValue(app.SettingsWindow.Panel), enterChecks,
                stickyField = "F10Recorder.SettingsPanel.loaded:System.Boolean",
                settingsVisible = app.SettingsWindow.IsVisible, overlayVisible = app.SettingsWindow.OverlayVisible,
                note = "Unchanged frozen App/WorkerRecorder. No save, pause, screenshot or provisioning command sent by this launcher." });
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            timer.Tick += (_, _) =>
            {
                if (requestedStop) return;
                try { job.Check(); CheckHiddenPanel(app); }
                catch (Exception error) { failure = error; requestedStop = true; app.Quit(); return; }
                if (clock.Elapsed.TotalSeconds >= 880 || worker.HasExited)
                {
                    failure = new IOException(worker.HasExited ? "Expected worker exited before owned UI stop." : "UI deadline.");
                    requestedStop = true; ((IClassicDesktopStyleApplicationLifetime)app.ApplicationLifetime).Shutdown(1); return;
                }
                if (!File.Exists(Path.Combine(evidence, "STOP.UI"))) return;
                requestedStop = true;
                // The controller first pauses/joins capture. Normal App.Quit
                // then owns its actual worker StopAsync and UI shutdown path.
                app.Quit();
            };
            timer.Start();
        };
        try { uiExitCode = Program.BuildAvaloniaApp().StartWithClassicDesktopLifetime([]); }
        catch (Exception error) { failure = error; }
        finally
        {
            timer?.Stop(); App.Started = null;
            bool? settingsEnterObserved = active == null ? null : (bool)entered.GetValue(active.SettingsWindow.Panel);
            if (active != null) try { CheckHiddenPanel(active); } catch (Exception error) { failure ??= error; }
            try { using var self = Process.GetCurrentProcess(); Write(evidence, "ui-closure.json", new { pid = Environment.ProcessId, birthUtc = self.StartTime.ToUniversalTime(),
                workerPid, workerBirthTicks, instance, error = failure?.ToString(), started, requestedStop, uiExitCode, seconds = clock.Elapsed.TotalSeconds,
                settingsEnterObserved, enterChecks, stickyField = "F10Recorder.SettingsPanel.loaded:System.Boolean",
                workerHostProcessAbsent = worker.HasExited, job = job.Check(), note = "Owned real UI shutdown. In the test, worker runs on its own thread in the collector host; caller verifies that thread joined, capture absent, processes and production preservation." }); }
            catch (Exception error) { failure ??= error; }
        }
        return failure == null && started && requestedStop && uiExitCode == 0 ? 0 : 1;
    }
}
