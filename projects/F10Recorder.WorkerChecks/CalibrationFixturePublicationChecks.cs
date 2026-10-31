using System.Diagnostics;
using System.Text.Json;

namespace F10Recorder;

internal static class CalibrationFixturePublicationChecks
{
    internal static int Run(string root)
    {
        using var owner = PilotStorageObservation.Create(root);
        var checks = new List<string>();
        void Require(bool value, string reason) { if (!value) throw new IOException(reason); }
        string Cell(string name) { string cell = Path.Combine(root, name); Directory.CreateDirectory(cell); return cell; }
        string legacy = Cell("legacy"); bool sharingFailure = false;
        using (var writer = new FileStream(Path.Combine(legacy, CalibrationFixturePublication.ReadyName), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            writer.Write("{\"pid\":"u8); writer.Flush(true);
            Require(File.Exists(Path.Combine(legacy, CalibrationFixturePublication.ReadyName)), "Legacy visibility schedule was not reached.");
            try { File.ReadAllText(Path.Combine(legacy, CalibrationFixturePublication.ReadyName)); } catch (IOException) { sharingFailure = true; }
        }
        Require(sharingFailure, "Native sharing control did not reproduce file-exists-before-close failure.");
        checks.Add("legacy-name-visible-before-writer-close-is-not-ready");
        var plan = new CalibrationPlan(root, "unused", "nonmedia-fixture-publication", new(), [], [], 0, 0,
            BudgetBytes: 3_000_000_000, StopBytes: 2_500_000_000, FloorBytes: 0, DeadlineSeconds: 30,
            RetainedOfflineSeconds: 0, ReuseCurrentMetadataHandles: true, DirectoryLimit: 64);
        using var sampler = new ReplayCalibrationRun.Sampler(plan);
        var timer = Stopwatch.StartNew();
        int publications = 0;
        for (int n = 0; n < 40; n++)
        {
            sampler.Check(); string cell = Cell("publish-" + n);
            CalibrationFixturePublication.Publish(cell, new { pid = 100 + n, hwnd = 200 + n, width = 1920, height = 1080, label = "T02" }, pending =>
            {
                Require(!File.Exists(Path.Combine(cell, CalibrationFixturePublication.ReadyName)), "Partial readiness became visible to the controller.");
                Require(!sampler.RetainsMetadata(pending), "Observer retained an atomic readiness leaf.");
                Thread.Sleep(30); // Actual native observation overlaps the open pending writer.
            });
            using var ready = CalibrationFixturePublication.Read(cell);
            Require(ready.RootElement.GetProperty("pid").GetInt32() == 100 + n
                && ready.RootElement.GetProperty("width").GetInt32() == 1920, "Published complete readiness lost its source identity.");
            Require(!File.Exists(Path.Combine(cell, CalibrationFixturePublication.PendingName)), "Published pending name remains.");
            publications++;
        }
        sampler.StopAndCheck();
        Require(sampler.Observations >= 3 && sampler.MaximumActiveGap <= 500, "Concurrent sampler coverage was incomplete.");
        checks.Add("forty-complete-atomic-publications-under-real-native-sampler");
        checks.Add("no-final-name-before-flush-close-and-rename");
        string malformed = Cell("malformed"); File.WriteAllText(Path.Combine(malformed, CalibrationFixturePublication.ReadyName), "{\"pid\":");
        bool malformedRejected = false;
        try { using var ignored = CalibrationFixturePublication.Read(malformed); } catch (JsonException) { malformedRejected = true; }
        Require(malformedRejected, "Malformed completed readiness was silently admitted.");
        checks.Add("malformed-readiness-is-a-failure-not-a-retry");
        string oversized = Cell("oversized"); bool oversizedRejected = false;
        try { CalibrationFixturePublication.Publish(oversized, new { value = new string('x', 8192) }); } catch (IOException) { oversizedRejected = true; }
        Require(oversizedRejected && !File.Exists(Path.Combine(oversized, CalibrationFixturePublication.ReadyName)), "Oversized readiness was published.");
        checks.Add("oversized-payload-never-publishes");
        File.WriteAllText(Path.Combine(root, "verification.json"), JsonSerializer.Serialize(new { result = "passed", checks, publications,
            sampler.MaximumActiveGap, elapsedSeconds = timer.Elapsed.TotalSeconds,
            effects = "Tiny JSON/native-sharing/publication fixtures plus actual native sampler; no child, engine, window, wake request, capture/audio or GPU." }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Fixture readiness publication: 5/5 passed;40native-observed publications"); return 0;
    }
}
