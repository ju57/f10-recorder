using System.Text.Json;

namespace F10Recorder;

// Hidden shortcut windows only: no UI surfaces, capture, GPU probing or startup writes.
internal static class SettingsValidationChecks
{
    static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    internal static int Run(string data)
    {
        Directory.CreateDirectory(data); RecorderHost.Initialize(data);
        var report = new Dictionary<string, object>();
        string originalRoot = MediaDependency.RootDirectory;
        try
        {
            MediaDependency.RootDirectory = Path.Combine(data, "managed");
            var current = new Settings { ReplayMinutes = 1, SaveKey = 112, OverlayKey = 113, ScreenshotKey = 114,
                SaveModifiers = 7, OverlayModifiers = 7, ScreenshotModifiers = 7,
                OutputDirectory = Path.Combine(data, "clips"), ScreenshotDirectory = Path.Combine(data, "screenshots") };
            string path = Path.Combine(data, "settings.json"); current.Save(path);
            string before = File.ReadAllText(path);
            int startupWrites = 0, changes = 0;
            using var host = new RecorderHost(data, _ => { }, true, "F10Recorder.Validation." + Guid.NewGuid().ToString("N"),
                syntheticSource: true, setStartup: _ => startupWrites++);
            Require(host.SaveRegistered && host.OverlayRegistered && host.ScreenshotRegistered, "Validation fixture shortcuts could not register. Use an ordinary desktop token with Ctrl+Alt+Shift+F1/F2/F3 available; a restricted sandbox can also deny registration.");
            host.Changed += () => changes++;
            Settings active = host.Config;
            // Occupied proposed shortcuts distinguish engine preflight from shortcut admission.
            var next = new Settings { ReplayMinutes = 2, SaveKey = 115, OverlayKey = 116, ScreenshotKey = 117,
                SaveModifiers = 7, OverlayModifiers = 7, ScreenshotModifiers = 7,
                OutputDirectory = current.OutputDirectory, ScreenshotDirectory = current.ScreenshotDirectory };
            using var occupied = new HotkeyWindow("F10Recorder.Validation.Blocker." + Guid.NewGuid().ToString("N"));
            occupied.Register(next);
            void RejectedBeforeMutation(string name)
            {
                bool rejected = false;
                try { host.ApplyAsync(next, true).WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult(); }
                catch (IOException ex) { rejected = ex.Message.Contains("approved recording engine"); }
                Require(rejected, "Engine preflight must reject " + name + " before trying the occupied proposed shortcuts.");
                Require(ReferenceEquals(host.Config, active) && File.ReadAllText(path) == before, "Failed engine preflight changed live or persisted preferences.");
                Require(changes == 0 && startupWrites == 0 && !host.Applying && host.Paused && !host.Ready && host.CaptureProcessId == 0,
                    "Failed engine preflight entered the settings transaction or started capture.");
                Require(host.SaveRegistered && host.OverlayRegistered && host.ScreenshotRegistered, "Failed engine preflight changed shortcut registration.");
                report[name] = true;
            }
            RejectedBeforeMutation("missingEngineRejectedBeforeMutation");
            TrustedStorage.CreatePrivateDirectory(MediaDependency.RootDirectory);
            string package = Path.GetDirectoryName(Path.GetDirectoryName(MediaDependency.ExecutablePath));
            TrustedStorage.CreatePrivateDirectory(package);
            TrustedStorage.CreatePrivateDirectory(Path.GetDirectoryName(MediaDependency.ExecutablePath));
            File.WriteAllText(MediaDependency.ExecutablePath, "Unapproved executable; must never run.");
            RejectedBeforeMutation("untrustedEngineRejectedBeforeMutation");
            report["result"] = "passed";
            return 0;
        }
        catch (Exception ex) { report["result"] = "failed"; report["error"] = ex.ToString(); return 1; }
        finally
        {
            MediaDependency.RootDirectory = originalRoot;
            File.WriteAllText(Path.Combine(data, "verification.json"), JsonSerializer.Serialize(report, WorkerProtocol.Json));
        }
    }
}
