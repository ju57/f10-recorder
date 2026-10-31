using System.Security.Cryptography;
using System.Text.Json;

namespace F10Recorder;

internal sealed record CalibrationCaptureIdentity(int Pid, DateTime StartedUtc, string SessionDirectory);
internal static class CalibrationCaptureEvidence
{
    internal static object Closure(CalibrationCaptureIdentity identity, bool exited, bool producersAbsent, int? exit, string diagnostics)
    {
        if (identity == null || identity.Pid <= 0 || identity.StartedUtc.Kind != DateTimeKind.Utc
            || identity.StartedUtc <= DateTime.UnixEpoch || !Path.IsPathFullyQualified(identity.SessionDirectory))
            throw new IOException("Capture evidence requires the actual owned PID, UTC start time and private session path.");
        return new { pid = identity.Pid, startedUtc = identity.StartedUtc, sessionDirectory = identity.SessionDirectory,
            exited, producersAbsent, exit, diagnostics };
    }
    internal static int Checks(string root)
    {
        if (Directory.Exists(root)) throw new IOException("Fresh capture evidence control root required.");
        Directory.CreateDirectory(root);
        string Hash(object value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
        var legacy = new { exited = true, producersAbsent = true, exit = 0, diagnostics = "" };
        if (Enumerable.Range(0, 6).Select(_ => Hash(legacy)).Distinct().Count() != 1) throw new IOException("Legacy duplicate schedule not reproduced.");
        var start = new DateTime(2026, 10, 30, 0, 0, 0, DateTimeKind.Utc);
        var receipts = Enumerable.Range(1, 6).Select(n => Closure(new(100 + n, start.AddSeconds(n), Path.Combine(root, "session-" + n)), true, true, 0, "")).ToArray();
        if (receipts.Select(Hash).Distinct().Count() != 6) throw new IOException("Independent capture identities did not produce independent evidence.");
        foreach (var invalid in new[] { new CalibrationCaptureIdentity(0, start, root), new(1, DateTime.SpecifyKind(start, DateTimeKind.Unspecified), root), new(1, start, "relative") })
        {
            bool rejected = false; try { Closure(invalid, true, true, 0, ""); } catch (IOException) { rejected = true; }
            if (!rejected) throw new IOException("Invalid process identity accepted.");
        }
        File.WriteAllText(Path.Combine(root, "verification.json"), JsonSerializer.Serialize(new { result = "passed", checks = 5,
            legacyIdenticalExitHashes = 1, independentCaptureHashes = 6,
            effects = "Controlled process identity values/JSON only; no process, engine, capture/audio/GPU, window, model or user-state action." }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Capture closure evidence controls:5/5passed"); return 0;
    }
}
