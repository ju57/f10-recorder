using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace F10Recorder;

public sealed class ProfileStatus
{
    public string Outcome = "current";
    public bool ExistingProfile, CanWrite = true, RequiresCorrection, ActivationRequired;
    public string[] Issues = [];
    public string Message => Issues.Length > 0 ? string.Join(" ", Issues) : ActivationRequired ? "Preferences preserved. Resume replay explicitly when ready." : "Recording preferences loaded.";
}

public sealed class RecordingProfileState
{
    public int SchemaVersion = 1;
    public bool Paused, UserStopped;
    public bool ActivationRequired = true;
    [JsonExtensionData] public Dictionary<string, JsonElement> AdditionalState = new();
}

// Loading never writes a profile. The owning worker commits an explicit change;
// a journal left by a failed paired commit blocks automatic recovery/activation.
public sealed class RecordingProfile
{
    readonly string directory;
    public Settings Settings { get; private set; }
    public RecordingProfileState State { get; private set; }
    public ProfileStatus Status { get; private set; }
    RecordingProfile(string directory) { this.directory = Path.GetFullPath(directory); }
    public static RecordingProfile Load(string directory)
    {
        var profile = new RecordingProfile(directory);
        var settings = ProfileDocuments.ReadSettings(Path.Combine(directory, "settings.json"));
        var state = ProfileDocuments.ReadState(Path.Combine(directory, "worker-state.json"));
        profile.Settings = settings.Value; profile.State = state.Value;
        string[] issues = settings.Issues.Concat(state.Issues).ToArray();
        bool interrupted = new[] { "profile-migration.json", "settings.json.tmp", "worker-state.json.tmp" }.Any(name => Path.Exists(Path.Combine(directory, name)));
        if (interrupted) issues = issues.Append("An interrupted profile write is preserved. Stop F10 and restore settings/state from the original backups before removing the journal or temporary files.").ToArray();
        bool migration = settings.Legacy || state.Legacy;
        profile.Status = new ProfileStatus {
            ExistingProfile = settings.Exists, CanWrite = settings.CanWrite && state.CanWrite && !interrupted,
            RequiresCorrection = issues.Length > 0, Issues = issues,
            ActivationRequired = migration || !settings.Exists || !state.Exists || state.Value.ActivationRequired || issues.Length > 0,
            Outcome = interrupted ? "interrupted" : !settings.CanWrite || !state.CanWrite ? "incompatible" : issues.Length > 0 ? "correction-required" : !settings.Exists ? "new-profile" : migration ? "migration-required" : "current"
        };
        return profile;
    }
    public void Commit(Settings settings, bool paused, bool userStopped, bool activationRequired)
    {
        var current = Load(directory);
        if (!current.Status.CanWrite) throw new IOException(current.Status.Message);
        settings.Validate();
        string settingsPath = Path.Combine(directory, "settings.json"), statePath = Path.Combine(directory, "worker-state.json"), journal = Path.Combine(directory, "profile-migration.json");
        var state = new RecordingProfileState { Paused = paused, UserStopped = userStopped, ActivationRequired = activationRequired, AdditionalState = current.State.AdditionalState };
        // JSON escaping can expand valid UTF-8 input beyond the reader's bound.
        // Admit both encoded documents before replacing either original.
        ProfileDocuments.Encode(settings); ProfileDocuments.Encode(state);
        ProfileDocuments.PreserveOriginal(settingsPath); ProfileDocuments.PreserveOriginal(statePath);
        using (var stream = new FileStream(journal, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, new { SchemaVersion = 1, SettingsOriginal = ProfileDocuments.Hash(settingsPath), StateOriginal = ProfileDocuments.Hash(statePath) });
            stream.Flush(true);
        }
        ProfileDocuments.WriteSettings(settingsPath, settings, true);
        ProfileDocuments.WriteState(statePath, state, true);
        File.Delete(journal);
        var committed = Load(directory); Settings = committed.Settings; State = committed.State; Status = committed.Status;
    }
    public void PersistState(bool paused, bool userStopped)
    {
        var current = Load(directory);
        if (!current.Status.CanWrite || current.Status.ActivationRequired) throw new IOException(current.Status.Message);
        current.State.Paused = paused; current.State.UserStopped = userStopped;
        ProfileDocuments.WriteState(Path.Combine(directory, "worker-state.json"), current.State);
        State = current.State;
    }
}

internal sealed class ProfileDocument<T>(T value)
{
    internal T Value = value;
    internal bool Exists, Legacy, CanWrite = true;
    internal readonly List<string> Issues = [];
}

internal static class ProfileDocuments
{
    const int MaxDocumentBytes = 1024 * 1024;
    internal static readonly JsonSerializerOptions Json = new() { IncludeFields = true };
    static JsonDocument Read<T>(string path, ProfileDocument<T> result)
    {
        result.Exists = File.Exists(path);
        if (!result.Exists) return null;
        try
        {
            if (new FileInfo(path).Length > MaxDocumentBytes) throw new JsonException("Profile document exceeds 1 MiB.");
            var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object) { document.Dispose(); throw new JsonException("Expected a JSON object."); }
            if (document.RootElement.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() != 1)) { document.Dispose(); throw new JsonException("Duplicate profile fields are ambiguous."); }
            result.Legacy = !document.RootElement.TryGetProperty("SchemaVersion", out var version);
            if (!result.Legacy && (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int number) || number != 1))
            {
                result.CanWrite = false;
                result.Issues.Add(Path.GetFileName(path) + ": unsupported schema. Use a compatible F10 version or restore the original document while F10 is stopped.");
            }
            return document;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            result.CanWrite = false;
            result.Issues.Add(Path.GetFileName(path) + ": cannot read this document; original preserved. Stop F10 and restore a valid original. " + ex.Message);
            return null;
        }
    }
    internal static ProfileDocument<Settings> ReadSettings(string path)
    {
        var result = new ProfileDocument<Settings>(new Settings());
        using var document = Read(path, result);
        if (document == null) { if (result.Exists) result.Value.CaptureMode = "window"; return result; }
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Name == "SchemaVersion") continue;
            var field = Settings.StoredFields.FirstOrDefault(f => f.Name == property.Name);
            if (field == null)
            {
                // Retain future preferences/identity without reviving profile engine discovery.
                if (property.Name != "FfmpegPath" && typeof(Settings).GetProperty(property.Name) == null) result.Value.AdditionalPreferences[property.Name] = property.Value.Clone();
                continue;
            }
            object fallback = field.GetValue(result.Value);
            try
            {
                if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("Null is invalid; it is not an absent field.");
                field.SetValue(result.Value, property.Value.Deserialize(field.FieldType));
                string issue = result.Value.FieldIssue(field.Name);
                if (issue != null) throw new JsonException(issue);
            }
            catch (JsonException ex)
            {
                result.Issues.Add(field.Name + ": " + ex.Message);
                field.SetValue(result.Value, field.Name == nameof(Settings.CaptureMode) ? "window" : fallback);
            }
        }
        try { result.Value.Validate(); }
        catch (ArgumentException ex) { result.Issues.Add(ex.Message); }
        return result;
    }
    internal static ProfileDocument<RecordingProfileState> ReadState(string path)
    {
        var result = new ProfileDocument<RecordingProfileState>(new RecordingProfileState());
        using var document = Read(path, result);
        if (document == null) return result;
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Name == "SchemaVersion") continue;
            if (property.Name is nameof(RecordingProfileState.Paused) or nameof(RecordingProfileState.UserStopped) or nameof(RecordingProfileState.ActivationRequired))
            {
                if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    result.CanWrite = false; result.Issues.Add("worker-state.json: " + property.Name + " must be true or false. Restore the original state while F10 is stopped.");
                    continue;
                }
                typeof(RecordingProfileState).GetField(property.Name).SetValue(result.Value, property.Value.GetBoolean());
            }
            else result.Value.AdditionalState[property.Name] = property.Value.Clone();
        }
        return result;
    }
    internal static string Hash(string path) => File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : "absent";
    internal static void PreserveOriginal(string path)
    {
        if (!File.Exists(path)) return;
        byte[] bytes = File.ReadAllBytes(path);
        string backup = path + ".original-" + Convert.ToHexString(SHA256.HashData(bytes));
        if (File.Exists(backup)) { if (!File.ReadAllBytes(backup).SequenceEqual(bytes)) throw new IOException("Original profile backup does not match: " + backup); return; }
        using var stream = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes); stream.Flush(true);
    }
    internal static void WriteSettings(string path, Settings settings, bool migrating = false)
    {
        if (!ReadSettings(path).CanWrite) throw new IOException("Settings are incompatible or unreadable. Original preserved; use a compatible F10 version or restore the original while stopped.");
        Write(path, settings, migrating);
    }
    internal static void WriteState(string path, RecordingProfileState state, bool migrating = false)
    {
        if (!ReadState(path).CanWrite) throw new IOException("Worker state is incompatible or unreadable. Original preserved; restore it while F10 is stopped.");
        Write(path, state, migrating);
    }
    internal static byte[] Encode<T>(T value)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length > MaxDocumentBytes) throw new IOException("Encoded profile exceeds 1 MiB. Original preserved; reduce unsupported preference data with a compatible F10 version before retrying.");
        return bytes;
    }
    static void Write<T>(string path, T value, bool migrating)
    {
        if (!migrating && Path.Exists(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)), "profile-migration.json"))) throw new IOException("Interrupted profile migration must be reviewed before saving.");
        if (Path.Exists(path + ".tmp")) throw new IOException("Interrupted profile write must be reviewed before saving: " + path + ".tmp");
        byte[] bytes = Encode(value);
        PreserveOriginal(path);
        using (var stream = new FileStream(path + ".tmp", FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
        if (File.Exists(path)) File.Replace(path + ".tmp", path, null); else File.Move(path + ".tmp", path);
    }
}
