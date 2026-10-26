using System.Text.Json;
using F10Recorder;

internal static class StorageBudgetChecks
{
    internal static void Run(string root)
    {
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "settings.json");
        File.WriteAllText(path, "{}");
        byte[] original = File.ReadAllBytes(path);
        var settings = Settings.Load(path);
        if (!File.ReadAllBytes(path).SequenceEqual(original)) throw new Exception("P01: loading a budget default rewrote preferences.");
        string saved = Path.Combine(root, "default.json");
        settings.Save(saved);
        using var document = JsonDocument.Parse(File.ReadAllBytes(saved));
        if (!document.RootElement.TryGetProperty("ReplayCacheBudgetBytes", out var budget) || budget.GetInt64() != 10_000_000_000)
            throw new Exception("P01: a missing replay-cache budget must default to decimal 10 GB.");
        Console.WriteLine("Passed: decimal replay-cache budget default without load-time writes.");
        foreach (string invalid in new[] { "0", "-1", "9223372036854775808", "\"10 GB\"", "null" })
        {
            string folder = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            string invalidPath = Path.Combine(folder, "settings.json");
            File.WriteAllText(invalidPath, "{\"ReplayCacheBudgetBytes\":" + invalid + ",\"SystemAudio\":false,\"DisplayIndex\":0,\"DeviceMuted\":true}");
            byte[] before = File.ReadAllBytes(invalidPath);
            var profile = RecordingProfile.Load(folder);
            if (!profile.Status.RequiresCorrection || !profile.Status.ActivationRequired || profile.Settings.SystemAudio || profile.Settings.DisplayIndex != 0
                || !profile.Settings.AdditionalPreferences["DeviceMuted"].GetBoolean() || !before.SequenceEqual(File.ReadAllBytes(invalidPath)))
                throw new Exception("P03: invalid explicit budget must require correction without rewriting originals or valid siblings: " + invalid);
        }
        bool zeroRejected = false;
        try { new Settings { ReplayCacheBudgetBytes = 0 }.Validate(); } catch (ArgumentException) { zeroRejected = true; }
        if (!zeroRejected) throw new Exception("P03: an explicit zero budget must fail direct validation.");
        var large = new Settings { ReplayMinutes = 30, BitrateMbps = 100, ReplayCacheBudgetBytes = 10_000_000_000 };
        bool tooSmall = false;
        try { large.Validate(); } catch (ArgumentException ex) { tooSmall = ex.Message.Contains("budget", StringComparison.OrdinalIgnoreCase); }
        if (!tooSmall || large.ReplayMinutes != 30 || large.BitrateMbps != 100 || large.ReplayCacheBudgetBytes != 10_000_000_000)
            throw new Exception("P04: decimal 10 GB cannot hold the 22,500,000,000-byte video floor; request an explicit change without changing quality/history.");
        large.ReplayCacheBudgetBytes = 22_500_000_000;
        large.Validate(); // Only the nominal video floor fits, not a calibrated sufficient reserve.
        string custom = Path.Combine(root, "custom"); Directory.CreateDirectory(custom);
        string customPath = Path.Combine(custom, "settings.json");
        File.WriteAllText(customPath, "{\"ReplayCacheBudgetBytes\":30000000000,\"SystemAudio\":false,\"DisplayIndex\":0,\"MicrophoneLevel\":0,\"DeviceIdentity\":\"retained\"}");
        File.WriteAllText(Path.Combine(custom, "worker-state.json"), "{\"Paused\":true,\"UserStopped\":true,\"OpaqueState\":0}");
        byte[] customOriginal = File.ReadAllBytes(customPath);
        var retained = RecordingProfile.Load(custom);
        retained.Settings.FramesPerSecond = 30;
        retained.Commit(retained.Settings, retained.State.Paused, retained.State.UserStopped, true);
        var again = RecordingProfile.Load(custom);
        if (again.Settings.ReplayCacheBudgetBytes != 30_000_000_000 || again.Settings.SystemAudio || again.Settings.DisplayIndex != 0
            || again.Settings.AdditionalPreferences["MicrophoneLevel"].GetInt32() != 0 || again.Settings.AdditionalPreferences["DeviceIdentity"].GetString() != "retained"
            || !again.State.Paused || !again.State.UserStopped || !again.Status.ActivationRequired || again.State.AdditionalState["OpaqueState"].GetInt32() != 0
            || !Directory.GetFiles(custom, "settings.json.original-*").Any(p => File.ReadAllBytes(p).SequenceEqual(customOriginal)))
            throw new Exception("P02: unrelated preference edits must preserve custom budget, exact originals, opaque siblings and paused/stopped intent.");
        new Settings { ReplayCacheBudgetBytes = long.MaxValue }.Validate();
        Console.WriteLine("Passed: explicit budget correction, high-bitrate video floor, custom-budget and original-intent preservation (P01-P04).");
    }
}
