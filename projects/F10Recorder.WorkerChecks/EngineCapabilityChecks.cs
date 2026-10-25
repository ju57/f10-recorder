using System.Text.Json;

namespace F10Recorder;

// The production worker-client transport seam only. No native recorder or pipe
// is constructed; these fixtures do not establish engine or GPU qualification.
internal static class EngineCapabilityChecks
{
    internal static int Run(string root, bool sensitivityControl = false) => RunAsync(root, sensitivityControl).GetAwaiter().GetResult();

    static WorkerStatus Status(bool applying = false) => new()
    {
        Instance = "capability-fixture-worker", EngineCapabilityContext = "capability-fixture-context", Revision = 7,
        Config = new Settings(), Paused = true, Phase = CapturePhase.Paused, State = "Paused", Applying = applying
    };
    static RecordingEngineCapabilities Receipt() => new()
    {
        CheckedUtc = "2000-01-01T00:00:00.0000000Z", PackageVersion = "fixture",
        EngineSha256 = "fixture-engine", PackageSha256 = "fixture-package", Encoders = []
    };
    static void Require(bool condition, string error) { if (!condition) throw new InvalidOperationException(error); }
    static async Task RequireRejected(Task<RecordingEngineCapabilities> operation, string message)
    {
        try { await operation; }
        catch (IOException ex) when (ex.Message.Contains(message, StringComparison.OrdinalIgnoreCase)) { return; }
        throw new InvalidOperationException("Expected capability rejection was not observed: " + message);
    }
    static async Task<int> RunAsync(string root, bool sensitivityControl)
    {
        Directory.CreateDirectory(root);
        var checks = new List<object>();
        int failed = 0;
        async Task Check(string name, Func<Task> body)
        {
            try { await body(); checks.Add(new { name, result = "passed" }); }
            catch (Exception ex) { failed++; checks.Add(new { name, result = "failed", error = ex.ToString() }); }
        }
        await Check("empty-codec-receipt-does-not-invent-hardware-support", async () =>
        {
            using var recorder = new WorkerRecorder(Status(), _ => Task.FromResult(new WorkerReply { Status = Status(), Capabilities = Receipt() }));
            var receipt = await recorder.ProbeMediaAsync();
            Require(receipt.Encoders.Length == 0, "A codec was invented despite the worker's empty initialization result.");
            Require(recorder.EngineCapabilityContext == "capability-fixture-worker:capability-fixture-context", "A successful check changed worker identity.");
        });
        await Check("missing-capability-response-must-reject", async () =>
        {
            // The optional control changes this response fixture, not production
            // code: a complete receipt must make this rejection assertion fail.
            using var recorder = new WorkerRecorder(Status(), _ => Task.FromResult(new WorkerReply
            {
                Status = Status(), Capabilities = sensitivityControl ? Receipt() : null
            }));
            await RequireRejected(recorder.ProbeMediaAsync(), "did not return recording engine capabilities");
        });
        await Check("apply-in-progress-rejects-delayed-receipt-without-context-change", async () =>
        {
            var delayed = new TaskCompletionSource<WorkerReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var recorder = new WorkerRecorder(Status(), _ => delayed.Task);
            var pending = recorder.ProbeMediaAsync();
            delayed.SetResult(new WorkerReply { Status = Status(applying: true), Capabilities = Receipt() });
            await RequireRejected(pending, "became unavailable");
            Require(recorder.Applying, "The worker's active Apply state was lost.");
        });
        await Check("error-bearing-response-cannot-masquerade-as-success", async () =>
        {
            using var recorder = new WorkerRecorder(Status(), _ => Task.FromResult(new WorkerReply
            {
                Status = Status(), Error = "Controlled rejected capability request", Capabilities = Receipt()
            }));
            try { await recorder.ProbeMediaAsync(); }
            catch (InvalidOperationException ex) when (ex.Message == "Controlled rejected capability request") { return; }
            throw new InvalidOperationException("An error-bearing worker response was accepted.");
        });
        File.WriteAllText(Path.Combine(root, "verification.json"), JsonSerializer.Serialize(new
        {
            result = failed == 0 ? "passed" : "failed", sensitivityControl, checks,
            effects = new { ui = false, capture = false, gpu = false, engineLaunch = false, namedPipe = false, startupWrites = false }
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Engine capability responses: {checks.Count - failed}/{checks.Count} passed. {root}");
        return failed == 0 ? 0 : 1;
    }
}
