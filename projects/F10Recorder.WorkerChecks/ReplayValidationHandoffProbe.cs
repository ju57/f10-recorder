using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace F10Recorder;

internal static class ReplayValidationHandoffProbe
{
    static void Need(bool value, string text) { if (!value) throw new IOException(text); }
    static readonly JsonSerializerOptions Json = new() { IncludeFields = true, WriteIndented = true };
    static byte[] Read(string path, int cap)
    {
        using var file = File.OpenRead(path); Need(file.Length is > 0 && file.Length <= cap, "Bounded probe metadata required.");
        var bytes = new byte[(int)file.Length]; file.ReadExactly(bytes); return bytes;
    }
    internal static int Run(string planFile, string expectedSha)
    {
        var raw = Read(planFile, 65536);
        Need(Convert.ToHexString(SHA256.HashData(raw)) == expectedSha, "Exact handoff probe plan required.");
        using var plan = JsonDocument.Parse(raw); var p = plan.RootElement;
        Need(p.GetProperty("kind").GetString() == "issue83-validation-handoff-file-probe-v1"
            && p.GetProperty("seconds").GetInt32() == 45 && p.GetProperty("priorDelayMilliseconds").GetInt32() == 150
            && p.GetProperty("contentionMilliseconds").GetInt32() == 400
            && DateTime.UtcNow.AddSeconds(60) < p.GetProperty("expiresUtc").GetDateTime(), "Fresh finite handoff scope required.");
        foreach (var item in p.GetProperty("files").EnumerateObject())
            Need(Convert.ToHexString(SHA256.HashData(Read(item.Name, 16777216))) == item.Value.GetString(), "Changed handoff binding: " + item.Name);
        foreach (string file in new[] { Environment.ProcessPath, typeof(ReplayValidationHandoffProbe).Assembly.Location,
            typeof(CaptureSession).Assembly.Location, typeof(Settings).Assembly.Location })
            Need(p.GetProperty("files").TryGetProperty(file, out _), "Actual handoff binary unbound.");
        string root = Path.GetFullPath(p.GetProperty("root").GetString());
        string inputsRoot = Path.GetFullPath(p.GetProperty("inputsRoot").GetString());
        string prefix = Path.GetFullPath("artifacts") + Path.DirectorySeparatorChar;
        Need(root.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && inputsRoot.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && !Directory.Exists(root) && !File.Exists(root) && !root.StartsWith(inputsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Fresh disjoint roots required.");
        using var closure = JsonDocument.Parse(Read(p.GetProperty("closure").GetString(), 65536));
        Need(closure.RootElement.GetProperty("absent").GetBoolean(), "Old collector closure required.");
        var inputs = p.GetProperty("inputs").EnumerateArray().ToArray(); Need(inputs.Length == 3, "Exactly three retained inputs.");
        foreach (var input in inputs)
        {
            string path = Path.GetFullPath(input.GetProperty("path").GetString());
            Need(path.StartsWith(inputsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Only declared retained task outputs.");
            for (string ancestor = path; ancestor != null; ancestor = Path.GetDirectoryName(ancestor))
                Need((File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) == 0, "No reparse ancestry.");
        }
        Directory.CreateDirectory(root);
        using var process = Process.GetCurrentProcess(); var clock = Stopwatch.StartNew();
        var rows = new List<object>(); Exception failure = null; long peakRam = 0; int peakHandles = 0;
        var observationField = typeof(ReplayStorageObservationGate).GetField("observedAt", BindingFlags.Instance | BindingFlags.NonPublic);
        Need(observationField?.FieldType == typeof(long), "Exact product gate timestamp layout required for read-only attribution.");
        void Bound()
        {
            process.Refresh(); peakRam = Math.Max(peakRam, process.WorkingSet64); peakHandles = Math.Max(peakHandles, process.HandleCount);
            Need(clock.Elapsed.TotalSeconds < 45 && peakRam <= 268435456 && peakHandles <= 1024, "Handoff time/RAM/handle bound.");
            Need(ReplayStorage.ObserveCapacity(root).AvailableBytes >= 60000000000, "Free-space floor.");
        }
        try
        {
            foreach (var input in inputs)
                foreach (string mode in new[] { "natural-inline", "natural-cooperative", "injected-inline", "injected-cooperative" })
                {
                    Bound(); string path = Path.GetFullPath(input.GetProperty("path").GetString()); object observerGate = new();
                    var stages = new List<object>(); var checks = new ReplayStorageCheckTiming();
                    var productGate = new ReplayStorageObservationGate(() => {
                        _ = ReplayStorage.Observe(path); _ = ReplayStorage.ObserveCapacity(Path.GetDirectoryName(path)); return true;
                    }, 100, 500, checks);
                    void Mark(string name) {
                        long at = Stopwatch.GetTimestamp(), last = (long)observationField.GetValue(productGate);
                        stages.Add(new { name, at, successfulObservationQpc = last, ageMilliseconds = (at - last) * 1000d / Stopwatch.Frequency });
                    }
                    void Guard() { Mark("guard-before"); try { productGate.Check(); } finally { Mark("guard-after"); } }
                    productGate.CheckNow(); Mark("after-forced-observation");
                    var open = new ReplayPublicationLease.OpenTiming();
                    using var output = new ReplayPublicationLease(path, open);
                    Need(output.CheckedIdentity.Length == input.GetProperty("bytes").GetInt64(), "Input EOF differs from admission.");
                    using var decoder = new ReplayDecodeLease(path, output); Mark("decoder-pin-complete");
                    CalibrationOutputObservationTiming nativeTiming = null; CalibrationValidationWorkTiming workTiming = null;
                    bool injected = mode.StartsWith("injected"), cooperative = mode.EndsWith("cooperative");
                    Exception caseError = null; bool merged = false; Task contention = null;
                    using var held = new ManualResetEventSlim();
                    try
                    {
                        if (injected)
                        {
                            Thread.Sleep(150); Mark("injected-prior-work-complete");
                            contention = Task.Run(() => { lock (observerGate) { held.Set(); Thread.Sleep(400); } });
                            Need(held.Wait(2000), "Contention task did not start."); Mark("injected-sampler-gate-held");
                        }
                        Action work = () => CalibrationOutputObservation.Run(path, observerGate,
                            (_, extent, _) => { Need(extent == output.CheckedIdentity, "Observer identity/extent changed."); merged = true; },
                            output.CheckedIdentity, timing => nativeTiming = timing);
                        if (cooperative) CalibrationValidationWork.Run(work, Guard, timing => workTiming = timing);
                        else work();
                        Mark("observer-complete"); Guard(); decoder.VerifyUnchanged(); Mark("next-stage-ready-no-decoder-launched");
                    }
                    catch (Exception error) { caseError = error; }
                    finally { contention?.GetAwaiter().GetResult(); }
                    bool expectedRejection = mode == "injected-inline";
                    rows.Add(new { path, mode, injected, open, nativeTiming, workTiming, stages, checks, merged,
                        identity = output.CheckedIdentity, error = caseError?.ToString(), expectedRejection });
                    if (expectedRejection) Need(caseError is IOException && caseError.Message.Contains("exceeded 500 ms") && merged,
                        "Injected old-path control must reproduce the exact prelaunch freshness failure.");
                    else Need(caseError == null && merged && (!cooperative || workTiming is { Joined: true, Error: null }),
                        "Natural/cooperative handoff failed; preserve all cases.");
                }
        }
        catch (Exception error) { failure = error; Console.Error.WriteLine(error.Message); }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { result = failure == null ? "handoff-mechanism-controls-passed" : "failed", error = failure?.ToString(),
            rows, seconds = clock.Elapsed.TotalSeconds, peakRam, peakHandles, capture = false, mediaChildren = 0, payloadBytesRead = 0,
            scope = "Actual unchanged product lease/pin/gate plus shared qualification output observer; natural retained files and explicitly injected150ms priorwork/400ms observergate contention. No codec/capture/catalog/model/Cache admission/publication/Saved. Controlled mechanism reproduction does not establish original filesystem cause or real-worker performance." }, Json);
        Need(bytes.Length <= 262144, "Handoff report256KiB bound."); Bound();
        File.WriteAllBytes(Path.Combine(root, "result.json"), bytes); Bound();
        return failure == null ? 0 : 1;
    }
}
