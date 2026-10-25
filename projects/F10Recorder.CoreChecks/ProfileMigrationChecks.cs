using System.Text;
using System.Text.Json;
using F10Recorder;

internal static class ProfileMigrationChecks
{
    internal static void Run(string data, bool sensitivityControl = false)
    {
        Directory.CreateDirectory(data);
        string path = Path.Combine(data, "settings.json");
        var input = new Dictionary<string, object> {
            ["FramesPerSecond"] = 999, ["VideoCodec"] = "hevc_nvenc", ["SystemAudio"] = false,
            ["DisplayIndex"] = 0, ["SaveKey"] = 118, ["SaveModifiers"] = 0,
            ["OutputDirectory"] = Path.Combine(data, "Clips ação"),
            ["ScreenshotDirectory"] = Path.Combine(data, "Pictures"),
            ["CaptureMode"] = "window", ["WindowExecutable"] = Path.Combine(data, "absent.exe"), ["WindowClass"] = "AbsentClass"
        };
        if (sensitivityControl) input["VideoCodec"] = "h264_nvenc"; // Fixture mutation: HEVC preservation oracle must disagree.
        byte[] original = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(input, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }).Replace(",", ",\r\n"))).ToArray();
        File.WriteAllBytes(path, original);
        var loaded = Settings.Load(path);
        Require(loaded.FramesPerSecond == 60 && loaded.VideoCodec == "hevc_nvenc" && !loaded.SystemAudio && loaded.DisplayIndex == 0 && loaded.SaveKey == 118 && loaded.SaveModifiers == 0 && loaded.OutputDirectory == (string)input["OutputDirectory"] && loaded.WindowExecutable == (string)input["WindowExecutable"] && loaded.WindowClass == "AbsentClass",
            "A single invalid FPS must preserve valid codec/audio/zero/bindings/folder/source siblings.");
        Require(File.ReadAllBytes(path).SequenceEqual(original), "Loading must preserve exact original bytes.");
        string absent = Path.Combine(data, "absent.json"); File.WriteAllText(absent, "{}");
        var defaults = Settings.Load(absent);
        Require(defaults.SaveKey == 121 && defaults.SaveModifiers == 0 && defaults.OverlayKey == 90 && defaults.OverlayModifiers == 1 && defaults.ScreenshotKey == 112 && defaults.ScreenshotModifiers == 1,
            "Absent bindings must receive accepted F10, Alt+Z and Alt+F1 defaults.");
        string future = Path.Combine(data, "future.json");
        byte[] futureBytes = Encoding.UTF8.GetBytes("{\"SchemaVersion\":999,\"SystemAudio\":false}"); File.WriteAllBytes(future, futureBytes);
        bool refused = false; try { new Settings().Save(future); } catch (IOException) { refused = true; }
        Require(refused && File.ReadAllBytes(future).SequenceEqual(futureBytes), "Saving must refuse and preserve a newer profile instead of silently downgrading it.");
        string incomplete = Path.Combine(data, "missing-activation"); Directory.CreateDirectory(incomplete);
        new Settings().Save(Path.Combine(incomplete, "settings.json"));
        File.WriteAllText(Path.Combine(incomplete, "worker-state.json"), "{\"SchemaVersion\":1,\"Paused\":false,\"UserStopped\":false}");
        Require(RecordingProfile.Load(incomplete).Status.ActivationRequired, "Absent activation intent must never implicitly enable capture.");
        var partial = RecordingProfile.Load(data);
        Require(partial.Status.ExistingProfile && partial.Status.Outcome == "correction-required" && partial.Status.Issues.Any(i => i.StartsWith("FramesPerSecond:")), "Partial correction must identify the invalid field and existing-user outcome.");
        partial.Commit(partial.Settings, false, true, true);
        Require(Directory.GetFiles(data, "settings.json.original-*").Any(p => File.ReadAllBytes(p).SequenceEqual(original)), "A committed correction must retain exact original BOM/CRLF/non-ASCII bytes.");
        var corrected = RecordingProfile.Load(data);
        Require(corrected.State.UserStopped && !corrected.State.Paused && corrected.Status.ActivationRequired && corrected.Settings.VideoCodec == "hevc_nvenc", "A correction must not clear stopped intent or activate capture.");
        corrected.Commit(corrected.Settings, true, false, false);
        var current = RecordingProfile.Load(data);
        Require(current.Status.Outcome == "current" && !current.Status.ActivationRequired && current.State.Paused, "A current explicitly committed profile must remain idempotent and paused.");
        foreach (var entry in new[] { ("null", "null"), ("truncated", "{\"CaptureMode\":\"window\","), ("future", "{\"SchemaVersion\":999}"), ("duplicate", "{\"SchemaVersion\":999,\"SchemaVersion\":1}") })
        {
            foreach (string affected in new[] { "settings.json", "worker-state.json" })
            {
                string folder = Path.Combine(data, entry.Item1 + "-" + affected); Directory.CreateDirectory(folder);
                new Settings { VideoCodec = "hevc_nvenc" }.Save(Path.Combine(folder, "settings.json"));
                File.WriteAllText(Path.Combine(folder, "worker-state.json"), "{\"SchemaVersion\":1,\"Paused\":true,\"UserStopped\":false,\"ActivationRequired\":false}");
                File.WriteAllText(Path.Combine(folder, affected), entry.Item2);
                byte[] before = File.ReadAllBytes(Path.Combine(folder, affected));
                var incompatible = RecordingProfile.Load(folder);
                Require(!incompatible.Status.CanWrite && incompatible.Status.RequiresCorrection && incompatible.Status.ActivationRequired, "Malformed/future/ambiguous " + affected + " must give a blocking compatibility result.");
                bool blocked = false; try { incompatible.Commit(new Settings(), false, false, false); } catch (IOException) { blocked = true; }
                Require(blocked && File.ReadAllBytes(Path.Combine(folder, affected)).SequenceEqual(before), "An incompatible document must remain byte-identical after an attempted commit.");
                if (affected == "worker-state.json") Require(incompatible.Settings.VideoCodec == "hevc_nvenc", "Bad state must not discard valid settings.");
            }
        }
        foreach (string invalid in new[] { "{\"ReplayMinutes\":0,\"SystemAudio\":false,\"DisplayIndex\":0,\"SaveModifiers\":0}", "{\"ReplayMinutes\":null,\"SystemAudio\":false}", "{\"SystemAudio\":null}", "{\"CaptureMode\":null}", "{\"CaptureMode\":\"unknown\"}" })
        {
            string folder = Path.Combine(data, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder, "settings.json"), invalid);
            var invalidProfile = RecordingProfile.Load(folder);
            Require(invalidProfile.Status.RequiresCorrection && invalidProfile.Status.ActivationRequired, "Invalid zero/null/mode must be observable rather than treated as absence.");
            if (invalid.Contains("CaptureMode")) Require(invalidProfile.Settings.CaptureMode == "window", "Invalid source mode must not broaden capture.");
            if (invalid.Contains("\"SystemAudio\":false")) Require(!invalidProfile.Settings.SystemAudio, "A bad sibling must not enable intentionally disabled audio.");
        }
        string opaque = Path.Combine(data, "opaque"); Directory.CreateDirectory(opaque);
        File.WriteAllText(Path.Combine(opaque, "settings.json"), "{\"SystemAudio\":false,\"SelectedDeviceIdentity\":\"unavailable-device\",\"MicrophoneLevel\":0,\"MicrophoneMuted\":true}");
        File.WriteAllText(Path.Combine(opaque, "worker-state.json"), "{\"Paused\":true,\"UserStopped\":false,\"UnimplementedState\":0}");
        var identity = RecordingProfile.Load(opaque); identity.Commit(identity.Settings, true, false, false);
        var identityAgain = RecordingProfile.Load(opaque);
        Require(identityAgain.Settings.AdditionalPreferences["SelectedDeviceIdentity"].GetString() == "unavailable-device" && identityAgain.Settings.AdditionalPreferences["MicrophoneLevel"].GetInt32() == 0 && identityAgain.Settings.AdditionalPreferences["MicrophoneMuted"].GetBoolean() && identityAgain.State.AdditionalState["UnimplementedState"].GetInt32() == 0, "Opaque device/zero/mute/state data must survive without inferred application.");
        File.WriteAllText(Path.Combine(opaque, "profile-migration.json"), "{\"SchemaVersion\":1,\"phase\":\"settings-written\"}");
        var interrupted = RecordingProfile.Load(opaque);
        Require(interrupted.Status.Outcome == "interrupted" && !interrupted.Status.CanWrite && interrupted.Settings.AdditionalPreferences["SelectedDeviceIdentity"].GetString() == "unavailable-device", "Interrupted paired state must be preserved, blocked and diagnosable.");
        string failure = Path.Combine(data, "write-failure"); Directory.CreateDirectory(failure);
        var beforeFailure = RecordingProfile.Load(failure); beforeFailure.Commit(new Settings(), true, false, false);
        byte[] previous = File.ReadAllBytes(Path.Combine(failure, "settings.json"));
        Directory.CreateDirectory(Path.Combine(failure, "worker-state.json.tmp"));
        Require(RecordingProfile.Load(failure).Status.Outcome == "interrupted", "An obstructed pending-write path must block before a worker can activate.");
        bool failed = false; try { beforeFailure.Commit(new Settings { VideoCodec = "hevc_nvenc" }, true, false, false); } catch (IOException) { failed = true; } catch (UnauthorizedAccessException) { failed = true; }
        Require(failed && RecordingProfile.Load(failure).Status.Outcome == "interrupted", "A failed replacement cannot claim migration success or permit activation.");
        Require(File.ReadAllBytes(Path.Combine(failure, "settings.json")).SequenceEqual(previous), "A staging obstruction must leave originals unchanged.");
        string backupFailure = Path.Combine(data, "backup-failure"); Directory.CreateDirectory(backupFailure);
        File.WriteAllText(Path.Combine(backupFailure, "settings.json"), "{\"VideoCodec\":\"hevc_nvenc\"}");
        byte[] backupOriginal = File.ReadAllBytes(Path.Combine(backupFailure, "settings.json"));
        string blockedBackup = Path.Combine(backupFailure, "settings.json.original-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(backupOriginal)));
        Directory.CreateDirectory(blockedBackup); bool backupRejected = false;
        var blockedProfile = RecordingProfile.Load(backupFailure);
        try { blockedProfile.Commit(blockedProfile.Settings, true, false, false); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { backupRejected = true; }
        Require(backupRejected && File.ReadAllBytes(Path.Combine(backupFailure, "settings.json")).SequenceEqual(backupOriginal) && !File.Exists(Path.Combine(backupFailure, "worker-state.json")), "Backup failure must be observable before either original is replaced.");
        if (OperatingSystem.IsWindows())
        {
            string replaceFailure = Path.Combine(data, "replace-failure"); Directory.CreateDirectory(replaceFailure);
            var replaceProfile = RecordingProfile.Load(replaceFailure); replaceProfile.Commit(new Settings(), true, false, false);
            byte[] replaceOriginal = File.ReadAllBytes(Path.Combine(replaceFailure, "settings.json"));
            bool replacementRejected = false;
            using (var held = new FileStream(Path.Combine(replaceFailure, "settings.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            { try { replaceProfile.Commit(new Settings { VideoCodec = "hevc_nvenc" }, true, false, false); } catch (IOException) { replacementRejected = true; } }
            Require(replacementRejected && File.ReadAllBytes(Path.Combine(replaceFailure, "settings.json")).SequenceEqual(replaceOriginal) && RecordingProfile.Load(replaceFailure).Status.Outcome == "interrupted", "Replacement failure must preserve original bytes and leave an actionable interrupted result.");
        }
        string expanding = Path.Combine(data, "unicode-size-bound"); Directory.CreateDirectory(expanding);
        string expandingPath = Path.Combine(expanding, "settings.json");
        File.WriteAllText(expandingPath, "{\"OpaquePreference\":\"" + new string('é', 180000) + "\"}");
        byte[] expandingOriginal = File.ReadAllBytes(expandingPath);
        var expandingProfile = RecordingProfile.Load(expanding); bool rejectedExpansion = false;
        try { expandingProfile.Commit(expandingProfile.Settings, true, false, false); } catch (IOException) { rejectedExpansion = true; }
        Require(rejectedExpansion ? File.ReadAllBytes(expandingPath).SequenceEqual(expandingOriginal) && !File.Exists(Path.Combine(expanding, "worker-state.json")) : RecordingProfile.Load(expanding).Status.CanWrite,
            "A valid UTF-8 opaque preference must remain reloadable or be rejected before either original is replaced.");
        Console.WriteLine("Passed: partial profile correction preserves valid recording intent and exact original bytes.");
    }
    static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
