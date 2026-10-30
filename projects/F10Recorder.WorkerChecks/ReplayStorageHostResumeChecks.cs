using System.Text.Json;

namespace F10Recorder;

// Exercises the real host's status projection/command state with supplied
// supervisor observations. A paused host never constructs a capture engine.
internal static class ReplayStorageHostResumeChecks
{
    internal static int Run(string root)
    {
        if (Directory.Exists(root)) throw new IOException("Fresh host Resume evidence root required.");
        Directory.CreateDirectory(root);
        var checks = new List<string>(); Exception failure = null; int startupWrites = 0;
        try
        {
            var settings = new Settings { OutputDirectory = Path.Combine(root, "output"), ScreenshotDirectory = Path.Combine(root, "pictures"),
                SaveKey = 118, OverlayKey = 119, ScreenshotKey = 120, SaveModifiers = 7, OverlayModifiers = 7, ScreenshotModifiers = 7 };
            settings.Save(Path.Combine(root, "settings.json"));
            using var host = new RecorderHost(root, action => action(), true, "F10Recorder.HostResume." + Guid.NewGuid().ToString("N"),
                syntheticSource: true, setStartup: _ => startupWrites++);
            var held = new RecorderMetrics { ReplayStorageSafetyHeld = true, ReplayStorageSafetyReason = "Previous producer's durable guard awaits replacement." };
            host.PresentCaptureStatus(new(CapturePhase.Starting, false, "Explicit Resume pending"), held, true, false);
            Require(!host.Paused && !host.Ready, "A prior durable guard overwrote the explicitly resumed capture intent.");
            checks.Add("pending-explicit-resume-keeps-running-intent-without-readiness");
            host.PresentCaptureStatus(new(CapturePhase.Buffering, true, "Fresh supervisor evidence"), new(), false, false);
            Require(!host.Paused && host.Ready, "Safely resumed acquisition is still presented as paused.");
            checks.Add("fresh-resumed-buffering-is-not-paused");
            int changes = 0; host.Changed += () => changes++;
            host.Pause(true);
            Require(host.Paused && changes > 0, "Pause after Resume was incorrectly treated as an already-satisfied command.");
            checks.Add("pause-after-resume-is-an-effective-owner-command");
            host.PresentCaptureStatus(new(CapturePhase.Paused, false, "New storage stop"), held, true, true);
            Require(host.Paused && !host.Ready && !host.CanSave, "A new real storage stop lost its paused state.");
            checks.Add("new-storage-stop-remains-paused-and-not-ready");
            Require(host.CaptureProcessId == 0 && startupWrites == 0, "Nonmedia host checks started capture or wrote startup state.");
        }
        catch (Exception error) { failure = error; }
        File.WriteAllText(Path.Combine(root, "verification.json"), JsonSerializer.Serialize(new { result = failure == null ? "passed" : "failed",
            error = failure?.ToString(), checks, startupWrites,
            effects = "Supplied supervisor states through a real isolated paused host; owned hidden shortcut window only. No capture engine, child, media, audio, model, visible UI or startup write." }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Host storage Resume checks: " + (failure == null ? "passed" : "failed")); return failure == null ? 0 : 1;
    }
    static void Require(bool condition, string reason) { if (!condition) throw new IOException(reason); }
}
