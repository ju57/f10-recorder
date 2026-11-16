using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace F10Recorder;

// Metadata-only attribution. A partially trained model is an input to revision
// arithmetic here, never permission for recording, export or publication.
internal static class ReplayCalibrationCheckCosts
{
    static void Require(bool condition, string message)
    { if (!condition) throw new IOException(message); }
    static string Hash(string file)
    { using var input = File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(input)); }
    internal static int Run(string planFile, string expectedHash)
    {
        Require(new FileInfo(planFile).Length <= 65536 && Hash(planFile) == expectedHash, "Reviewed metadata plan changed.");
        using var document = JsonDocument.Parse(File.ReadAllBytes(planFile));
        var plan = document.RootElement;
        Require(plan.GetProperty("kind").GetString() == "issue83-calibration-check-cost-v1"
            && plan.GetProperty("seconds").GetInt32() == 15 && plan.GetProperty("groups").GetInt32() == 4,
            "Wrong metadata-only scope.");
        Require(DateTime.UtcNow.AddSeconds(30) < plan.GetProperty("expiresUtc").GetDateTime(), "Complete metadata window no longer fits.");
        foreach (var binding in plan.GetProperty("files").EnumerateObject())
            Require(Hash(binding.Name) == binding.Value.GetString(), "Bound metadata input changed: " + binding.Name);
        string modelFile = plan.GetProperty("modelFile").GetString(), destination = plan.GetProperty("destination").GetString();
        Require(new FileInfo(modelFile).Length is > 0 and <= 131072 && Directory.Exists(destination), "Bound model/destination absent.");
        var model = JsonSerializer.Deserialize<ReplayReferenceModel>(File.ReadAllBytes(modelFile));
        string root = Path.GetFullPath(plan.GetProperty("output").GetString());
        string artifacts = Path.GetFullPath("artifacts") + Path.DirectorySeparatorChar;
        Require(root.StartsWith(artifacts, StringComparison.OrdinalIgnoreCase), "Isolated metadata output required.");
        using var owned = PilotStorageObservation.Create(root);
        var clock = Stopwatch.StartNew();
        var rows = new List<object>();
        try
        {
            long at = Stopwatch.GetTimestamp();
            string host = ReplayCalibrationCatalog.HostIdentity;
            double snapshotMilliseconds = Stopwatch.GetElapsedTime(at).TotalMilliseconds;
            Require(model.Revision == ReplayCalibrationCatalog.ModelRevision(model.Scope, model.Volume, model.AllocationUnit,
                model.FinalResidual, model.SampledFinalizationExtra, model.TrainingReceipts, host, model.CaptureHeadroom),
                "Provisional revision does not match these exact candidate bytes/current host.");
            // ABBA, repeated four times; only the supplied host argument differs.
            // Native destination and model checks remain real in both variants.
            for (int group = 0; group < 4; group++)
                foreach (bool supplied in new[] { false, true, true, false })
                {
                    Require(clock.Elapsed.TotalSeconds < 15, "Metadata attribution deadline.");
                    at = Stopwatch.GetTimestamp();
                    bool applicable = ReplayCalibrationCatalog.IsApplicableToDestination(model, destination, supplied ? host : null);
                    double milliseconds = Stopwatch.GetElapsedTime(at).TotalMilliseconds;
                    rows.Add(new { group, suppliedFreshHostSnapshot = supplied, applicable, milliseconds });
                    Require(applicable && clock.Elapsed.TotalSeconds < 15, "Applicability failed or exceeded metadata deadline.");
                }
            at = Stopwatch.GetTimestamp();
            Require(ReplayCalibrationCatalog.HostIdentity == host, "Host changed during comparison.");
            double finalHostMilliseconds = Stopwatch.GetElapsedTime(at).TotalMilliseconds;
            Require(clock.Elapsed.TotalSeconds < 15, "Final host check exceeded metadata deadline.");
            Write(root, "result.json", new { result = "measured-component-costs", expectedHash, snapshotMilliseconds,
                finalHostMilliseconds, rows, seconds = clock.Elapsed.TotalSeconds,
                interpretation = "Actual model/destination applicability ABBA: fresh host each default call versus one explicitly supplied current snapshot. No production caching selected; no full-save latency, semantic decode, qualified-model, recording or publication claim." });
            return 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Write(root, "failure.json", new { result = "failed-no-retry", expectedHash, rows, seconds = clock.Elapsed.TotalSeconds, error = error.ToString() });
            Console.Error.WriteLine(error.Message); return 1;
        }
    }
    static void Write(string root, string name, object value)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions { WriteIndented = true });
        Require(bytes.Length <= 1048576, "Metadata report exceeds1MiB.");
        using var output = new FileStream(Path.Combine(root, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        output.Write(bytes);
    }
}
