using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace F10Recorder;

// File-open attribution only. No catalog, codec, capture, payload read, output
// mutation, publication or successful-save assertion belongs to this route.
internal static class ReplayDecodeHandoffProbe
{
    static readonly JsonSerializerOptions Json = new() { IncludeFields = true, WriteIndented = true };
    static void Need(bool condition, string message) { if (!condition) throw new IOException(message); }
    static byte[] Read(string path, int maximum)
    {
        using var input = File.OpenRead(path);
        Need(input.Length is > 0 && input.Length <= maximum, "Bounded probe metadata required.");
        var bytes = new byte[(int)input.Length]; input.ReadExactly(bytes); return bytes;
    }
    static string Hash(string path) => Convert.ToHexString(SHA256.HashData(Read(path, 16 * 1024 * 1024)));

    internal static int Run(string planFile, string expectedHash)
    {
        byte[] raw = Read(planFile, 65536);
        Need(Convert.ToHexString(SHA256.HashData(raw)) == expectedHash, "Exact probe admission required.");
        using var plan = JsonDocument.Parse(raw); var p = plan.RootElement;
        Need(p.GetProperty("kind").GetString() == "issue83-decode-handoff-probe-v1"
            && p.GetProperty("iterations").GetInt32() == 10
            && p.GetProperty("maximumSeconds").GetInt32() == 45
            && DateTime.UtcNow.AddSeconds(60) < p.GetProperty("expiresUtc").GetDateTime(), "Fresh finite probe scope required.");
        foreach (var binding in p.GetProperty("files").EnumerateObject())
            Need(Hash(binding.Name) == binding.Value.GetString(), "Probe binding changed: " + binding.Name);
        foreach (string role in new[] { Environment.ProcessPath, typeof(ReplayDecodeHandoffProbe).Assembly.Location,
            typeof(CaptureSession).Assembly.Location, typeof(Settings).Assembly.Location })
            Need(p.GetProperty("files").TryGetProperty(role, out _), "Actual probe binary unbound.");
        string root = Path.GetFullPath(p.GetProperty("root").GetString());
        string inputsRoot = Path.GetFullPath(p.GetProperty("inputsRoot").GetString());
        string prefix = Path.GetFullPath("artifacts") + Path.DirectorySeparatorChar;
        Need(root.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && inputsRoot.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && !Directory.Exists(root) && !File.Exists(root) && !root.StartsWith(inputsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "Fresh disjoint isolated probe root required.");
        using var closure = JsonDocument.Parse(Read(p.GetProperty("closure").GetString(), 65536));
        Need(closure.RootElement.GetProperty("absent").GetBoolean(), "Previous collector must be joined.");
        var inputs = p.GetProperty("inputs").EnumerateArray().ToArray();
        Need(inputs.Length == 3, "Exactly three retained outputs required.");
        foreach (var input in inputs)
        {
            string path = Path.GetFullPath(input.GetProperty("path").GetString());
            Need(path.StartsWith(inputsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase), "Only retained task outputs may be opened.");
            for (string ancestor = path; ancestor != null; ancestor = Path.GetDirectoryName(ancestor))
                Need((File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) == 0, "Probe rejects reparse ancestry.");
        }
        Directory.CreateDirectory(root);
        var clock = Stopwatch.StartNew(); using var process = Process.GetCurrentProcess();
        var rows = new List<object>(); var identities = new Dictionary<string, ReplayFileExtent>(StringComparer.OrdinalIgnoreCase);
        Exception failure = null; long peakRam = 0; int peakHandles = 0;
        void Bound()
        {
            process.Refresh(); peakRam = Math.Max(peakRam, process.WorkingSet64); peakHandles = Math.Max(peakHandles, process.HandleCount);
            Need(clock.Elapsed.TotalSeconds < 45 && peakRam <= 268435456 && peakHandles <= 1024, "Probe process/time bound.");
            Need(ReplayStorage.ObserveCapacity(root).AvailableBytes >= 60000000000, "Probe free-space floor.");
        }
        try
        {
            // First pass is retained-file-first within this process, not an OS
            // cold-cache or immediate-producer-close trial. Every later pass is kept.
            for (int iteration = 0; iteration < 10; iteration++)
                foreach (var input in inputs)
                {
                    Bound(); string path = Path.GetFullPath(input.GetProperty("path").GetString());
                    var timings = new ReplayPublicationLease.OpenTiming();
                    ReplayPublicationLease publication = null; ReplayDecodeLease decoder = null;
                    ReplayFileExtent identity = null, after = null; Exception error = null;
                    double? publicationMs = null, pinMs = null, verifyMs = null, closeMs = null, afterMs = null;
                    long at = Stopwatch.GetTimestamp(), started = at;
                    try
                    {
                        publication = new(path, timings); publicationMs = Stopwatch.GetElapsedTime(at).TotalMilliseconds;
                        identity = publication.CheckedIdentity;
                        Need(identity.Length == input.GetProperty("bytes").GetInt64(), "Retained output EOF differs from admission.");
                        if (identities.TryGetValue(path, out var first)) Need(identity == first, "Retained output identity/extent changed between iterations.");
                        else identities.Add(path, identity);
                        at = Stopwatch.GetTimestamp(); decoder = new(path, publication); publication = null;
                        pinMs = Stopwatch.GetElapsedTime(at).TotalMilliseconds;
                        at = Stopwatch.GetTimestamp(); decoder.VerifyUnchanged(); verifyMs = Stopwatch.GetElapsedTime(at).TotalMilliseconds;
                    }
                    catch (Exception e) { error = e; }
                    finally
                    {
                        at = Stopwatch.GetTimestamp();
                        try { decoder?.Dispose(); publication?.Dispose(); }
                        catch (Exception e) { error ??= e; }
                        closeMs = Stopwatch.GetElapsedTime(at).TotalMilliseconds;
                    }
                    at = Stopwatch.GetTimestamp();
                    try { after = ReplayStorage.Observe(path); Need(identity != null && after == identity, "Post-close identity/extent changed."); }
                    catch (Exception e) { error ??= e; }
                    afterMs = Stopwatch.GetElapsedTime(at).TotalMilliseconds;
                    rows.Add(new { iteration, path, started, finished = Stopwatch.GetTimestamp(),
                        publicationMs, timings, pinMs, verifyMs, closeMs, afterMs, identity, after, error = error?.ToString() });
                    if (error != null) throw new IOException("Decode handoff probe failed; retain diagnostics.", error);
                    Bound();
                }
        }
        catch (Exception error) { failure = error; Console.Error.WriteLine(error.Message); }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { result = failure == null ? "file-open-attribution-collected" : "failed",
            error = failure?.ToString(), rows, seconds = clock.Elapsed.TotalSeconds, peakRam, peakHandles,
            capture = false, mediaChildren = 0, payloadBytesRead = 0, catalog = false, publication = false,
            scope = "Actual unchanged publication/decode lease primitives on three retained outputs, ten ordered passes; IDs/EOF/allocations checked within and between passes. No media decoding or validation, no Cache admission/model, no ordinary-worker/Saved latency or first-close/OS-cold claim. Fast retained-file repeats cannot rule out original producer-close stalls." }, Json);
        Need(bytes.Length <= 262144, "Probe report256KiB bound.");
        Bound();
        File.WriteAllBytes(Path.Combine(root, "result.json"), bytes);
        Bound(); // Serialization/report close are inside the same finite run.
        return failure == null ? 0 : 1;
    }
}
