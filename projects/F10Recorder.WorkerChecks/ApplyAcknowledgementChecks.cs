using System.Text.Json;

namespace F10Recorder;

// Exercises the existing production client boundary without a native worker.
internal static class ApplyAcknowledgementChecks
{
    internal static int Run(string root, bool sensitivityControl = false) => RunAsync(root, sensitivityControl).GetAwaiter().GetResult();
    static WorkerStatus Status() => new()
    {
        Instance = "correction-worker", Revision = 8, Config = new Settings(),
        Paused = true, Phase = CapturePhase.Paused,
        ProfileStatus = new ProfileStatus { ExistingProfile = true, ActivationRequired = true }
    };
    static async Task<int> RunAsync(string root, bool sensitivityControl)
    {
        Directory.CreateDirectory(root); var checks = new List<object>(); int failed = 0;
        async Task Check(string name, Func<Task> body)
        {
            try { await body(); checks.Add(new { name, result = "passed" }); }
            catch (Exception ex) { failed++; checks.Add(new { name, result = "failed", error = ex.ToString() }); }
        }
        await Check("unmatched-operation-cannot-report-correction-success", async () =>
        {
            using var recorder = new WorkerRecorder(Status(), request =>
            {
                // Sensitivity changes only this reply fixture into a valid completion.
                var reply = Status(); reply.Revision = 9; reply.CompletedApply = sensitivityControl ? request.OperationId : "another-operation";
                return Task.FromResult(new WorkerReply { Status = reply });
            });
            bool rejected = false;
            try { await recorder.ApplyAsync(new Settings { FramesPerSecond = 30 }, false); }
            catch (IOException) { rejected = true; }
            if (!rejected) throw new InvalidOperationException("An unrelated completion was reported as a successful correction.");
        });
        await Check("fresh-matching-correction-preserves-explicit-pause-and-hold", async () =>
        {
            using var recorder = new WorkerRecorder(Status(), request =>
            {
                if (request.Command != "apply" || request.Settings.FramesPerSecond != 30 || request.Value)
                    throw new InvalidOperationException("The requested correction or explicit startup false changed.");
                var reply = Status(); reply.Revision = 9; reply.CompletedApply = request.OperationId; reply.Config = request.Settings;
                return Task.FromResult(new WorkerReply { Status = reply });
            });
            await recorder.ApplyAsync(new Settings { FramesPerSecond = 30, SystemAudio = false, DisplayIndex = 0 }, false);
            if (recorder.Config.FramesPerSecond != 30 || recorder.Config.SystemAudio || recorder.Config.DisplayIndex != 0 ||
                !recorder.Paused || !recorder.ProfileStatus.ActivationRequired || !recorder.CaptureModeKnown)
                throw new InvalidOperationException("Acknowledged correction changed deliberate intent or remained unknown.");
        });
        await Check("matching-completion-allows-legitimate-engine-context-change", async () =>
        {
            using var recorder = new WorkerRecorder(Status(), request =>
            {
                var reply = Status(); reply.Revision = 9; reply.CompletedApply = request.OperationId;
                reply.EngineCapabilityContext = "changed-by-the-settings-operation";
                return Task.FromResult(new WorkerReply { Status = reply });
            });
            await recorder.ApplyAsync(new Settings(), false);
            if (!recorder.EngineCapabilityContext.EndsWith(":changed-by-the-settings-operation") || !recorder.CaptureModeKnown)
                throw new InvalidOperationException("A valid operation must not depend on a stable engine-probe context.");
        });
        foreach (string name in new[] { "missing-status", "still-applying", "same-revision", "stale-revision", "replacement-worker" })
            await Check(name + "-cannot-report-correction-success", async () =>
            {
                using var recorder = new WorkerRecorder(Status(), request =>
                {
                    var reply = Status(); reply.Revision = name == "same-revision" ? 8 : name == "stale-revision" ? 7 : 9;
                    reply.CompletedApply = request.OperationId; reply.Applying = name == "still-applying";
                    if (name == "replacement-worker") reply.Instance = "replacement-worker";
                    return Task.FromResult(new WorkerReply { Status = name == "missing-status" ? null : reply });
                });
                await RequireUnconfirmed(recorder.ApplyAsync(new Settings { FramesPerSecond = 30 }, false));
                if ((name == "same-revision" || name == "stale-revision") && recorder.CaptureModeKnown)
                    throw new InvalidOperationException("A stale completion cleared capture-mode uncertainty despite rejection.");
            });
        await Check("error-with-matching-completion-remains-a-rejection", async () =>
        {
            using var recorder = new WorkerRecorder(Status(), request =>
            {
                var reply = Status(); reply.Revision = 9; reply.CompletedApply = request.OperationId;
                return Task.FromResult(new WorkerReply { Status = reply, Error = "Controlled correction write failed" });
            });
            try { await recorder.ApplyAsync(new Settings { FramesPerSecond = 30 }, false); }
            catch (InvalidOperationException ex) when (ex.Message == "Controlled correction write failed")
            {
                if (recorder.Config.FramesPerSecond != 60 || !recorder.Paused || !recorder.ProfileStatus.ActivationRequired || !recorder.CaptureModeKnown)
                    throw new InvalidOperationException("Rejected correction lost old intent or matching failure acknowledgement.");
                return;
            }
            throw new InvalidOperationException("A matching operation marker hid a failed correction.");
        });
        await Check("replacement-poll-retires-old-success-reply", async () =>
        {
            var response = new TaskCompletionSource<WorkerReply>(); WorkerRequest sent = null;
            using var recorder = new WorkerRecorder(Status(), request => { sent = request; return response.Task; });
            var operation = recorder.ApplyAsync(new Settings { FramesPerSecond = 30 }, false);
            var replacement = Status(); replacement.Instance = "replacement-worker"; replacement.Config.BitrateMbps = 35;
            recorder.Update(replacement);
            var old = Status(); old.Revision = 10; old.CompletedApply = sent.OperationId; old.Config = sent.Settings;
            response.SetResult(new WorkerReply { Status = old });
            await RequireUnconfirmed(operation);
            if (recorder.Config.BitrateMbps != 35 || !recorder.EngineCapabilityContext.StartsWith("replacement-worker:"))
                throw new InvalidOperationException("A retired worker's reply replaced the accepted worker state.");
        });
        await Check("newer-matching-poll-accepts-older-matching-completion-reply", async () =>
        {
            var response = new TaskCompletionSource<WorkerReply>(); WorkerRequest sent = null;
            using var recorder = new WorkerRecorder(Status(), request => { sent = request; return response.Task; });
            var operation = recorder.ApplyAsync(new Settings { FramesPerSecond = 30 }, false);
            var poll = Status(); poll.Revision = 10; poll.CompletedApply = sent.OperationId; poll.Config = sent.Settings;
            recorder.Update(poll);
            var reply = Status(); reply.Revision = 9; reply.CompletedApply = sent.OperationId; reply.Config = sent.Settings;
            response.SetResult(new WorkerReply { Status = reply }); await operation;
            if (recorder.Config.FramesPerSecond != 30 || !recorder.Paused || !recorder.ProfileStatus.ActivationRequired || !recorder.CaptureModeKnown)
                throw new InvalidOperationException("A valid completed correction was rejected after a newer matching poll.");
        });
        await Check("same-worker-newer-unrelated-poll-rejects-stale-completion", async () =>
        {
            var response = new TaskCompletionSource<WorkerReply>(); WorkerRequest sent = null;
            using var recorder = new WorkerRecorder(Status(), request => { sent = request; return response.Task; });
            var operation = recorder.ApplyAsync(new Settings { FramesPerSecond = 30 }, false);
            var poll = Status(); poll.Revision = 10; poll.CompletedApply = "later-operation"; poll.Config.BitrateMbps = 35;
            recorder.Update(poll);
            var reply = Status(); reply.Revision = 9; reply.CompletedApply = sent.OperationId; reply.Config = sent.Settings;
            response.SetResult(new WorkerReply { Status = reply }); await RequireUnconfirmed(operation);
            if (recorder.Config.BitrateMbps != 35 || recorder.CaptureModeKnown)
                throw new InvalidOperationException("A stale completion replaced newer state or cleared outstanding uncertainty.");
        });
        await Check("repair-uses-the-same-matching-completion-contract", async () =>
        {
            using var recorder = new WorkerRecorder(Status(), request =>
            {
                if (request.Command != "repairMedia") throw new InvalidOperationException("Unexpected repair command.");
                var reply = Status(); reply.Revision = 9; reply.CompletedApply = request.OperationId;
                return Task.FromResult(new WorkerReply { Status = reply });
            });
            await recorder.RepairMediaAsync();
            if (!recorder.CaptureModeKnown || !recorder.Paused) throw new InvalidOperationException("Acknowledged repair changed pause or remained unknown.");
        });
        File.WriteAllText(Path.Combine(root, "verification.json"), JsonSerializer.Serialize(new
        {
            result = failed == 0 ? "passed" : "failed", sensitivityControl, checks,
            effects = "Headless worker client and local reports only; no native windows, worker, pipe, engine, GPU, capture or startup writes."
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Apply acknowledgements: {checks.Count - failed}/{checks.Count} passed. {root}");
        return failed == 0 ? 0 : 1;
    }
    static async Task RequireUnconfirmed(Task operation)
    {
        try { await operation; }
        catch (IOException ex) when (ex.Message.Contains("did not confirm", StringComparison.Ordinal)) { return; }
        throw new InvalidOperationException("An unconfirmed correction was reported as successful.");
    }
}
