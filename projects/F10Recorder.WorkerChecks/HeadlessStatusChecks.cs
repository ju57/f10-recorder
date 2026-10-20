using System.Diagnostics;
using System.Text.Json;

namespace F10Recorder;

// Monitor-policy compatibility without displaying capture-excluded UI surfaces.
// Synthetic media only; the production worker host/control/engine are exercised.
internal static class HeadlessStatusChecks
{
    internal static int Run(string data, string desktop)
    {
        Directory.CreateDirectory(data);
        var report = new Dictionary<string, object>(); var clips = new List<string>();
        Process worker = null;
        void Require(bool value, string reason) { if (!value) throw new Exception(reason); }
        Task<WorkerReply> Call(string command = "status", bool value = false, long after = -1) => WorkerProtocol.CallAsync(data, new WorkerRequest { Command = command, Value = value, AfterEvent = after });
        Process Launch()
        {
            var info = new ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "--worker", data, desktop }) info.ArgumentList.Add(arg);
            return Process.Start(info);
        }
        async Task<WorkerStatus> Until(Func<WorkerStatus, bool> condition, string reason)
        {
            for (int i = 0; i < 200; i++)
            {
                try { var reply = await Call(); Require(reply.Error == null, reply.Error); if (condition(reply.Status)) return reply.Status; }
                catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
                await Task.Delay(100);
            }
            throw new TimeoutException(reason);
        }
        async Task Save()
        {
            long sequence = (await Call()).Status.Sequence;
            Require((await Call("save")).Error == null, "Save command failed.");
            for (int i = 0; i < 100; i++)
            {
                var reply = await Call(after: sequence);
                var saved = reply.Status.Events.FirstOrDefault(e => e.Kind == "saved");
                if (saved != null) { Require(saved.Success && File.Exists(saved.Message), "Save failed: " + saved.Message); clips.Add(saved.Message); return; }
                await Task.Delay(100);
            }
            throw new TimeoutException("Save completion missing.");
        }
        async Task Check()
        {
            new Settings { ReplayMinutes = 1, FramesPerSecond = 30, BitrateMbps = 5, OutputDirectory = Path.Combine(data, "clips"), ScreenshotDirectory = Path.Combine(data, "screenshots"),
                SaveKey = 114, SaveModifiers = 3, OverlayKey = 115, OverlayModifiers = 3, ScreenshotKey = 116, ScreenshotModifiers = 3 }.Save(Path.Combine(data, "settings.json"));
            worker = Launch();
            var first = await Until(s => s.Ready && s.CanSave && s.Phase == CapturePhase.Buffering && s.Metrics.CaptureProgressSeconds >= 4, "Synthetic monitor path did not reach readiness.");
            Require(first.Config.CaptureMode == "monitor" && first.WorkerProcessId == worker.Id, "Default source/worker ownership changed.");
            var wrong = await WorkerProtocol.CallAsync(data, new WorkerRequest { Version = 1 });
            Require(wrong.Error != null && (await Call()).Status.Ready, "An old protocol request was not rejected safely.");
            await Save();
            var restarted = await Call("restart"); Require(!restarted.Status.Ready, "Restart advertised stale readiness.");
            await Until(s => s.Ready && s.CanSave && s.Metrics.CaptureProcessId != first.Metrics.CaptureProcessId && s.Metrics.CaptureProgressSeconds >= 4, "Restart did not produce fresh capture readiness.");
            await Save();
            await Call("pause", true);
            await Until(s => s.Paused && !s.Ready && !s.CanSave && s.Metrics.CaptureProcessId == 0, "Pause left stale readiness or save access.");
            await Call("quit"); Require(worker.WaitForExit(15000), "Worker did not quit."); worker.Dispose(); worker = Launch();
            await Until(s => s.Paused && !s.Ready && !s.CanSave && s.Metrics.CaptureProcessId == 0, "Intentional pause was lost across worker restart.");
            report["defaultMonitorSyntheticWorkerCompatibility"] = true; report["oldProtocolRejected"] = true; report["restartAndPersistedPause"] = true; report["result"] = "passed";
        }
        try { Check().GetAwaiter().GetResult(); }
        catch (Exception ex) { report["result"] = "failed"; report["error"] = ex.ToString(); }
        finally
        {
            if (worker != null) { try { Call("quit").GetAwaiter().GetResult(); worker.WaitForExit(15000); } catch { } if (!worker.HasExited) worker.Kill(true); worker.Dispose(); }
            report["clips"] = clips; report["desktopCapture"] = false;
            File.WriteAllText(Path.Combine(data, "verification.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        return (string)report["result"] == "passed" ? 0 : 1;
    }
}
