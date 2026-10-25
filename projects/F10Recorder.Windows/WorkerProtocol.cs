using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace F10Recorder;

internal static class WorkerIdentity
{
    internal static string DataPath(string data)
    {
        string full = Path.GetFullPath(data);
        return Path.TrimEndingDirectorySeparator(Directory.Exists(full) ? Native.FinalDirectoryPath(full) : full);
    }
    internal static string Scope(string data)
    {
        using var identity = WindowsIdentity.GetCurrent();
        using var process = Process.GetCurrentProcess();
        string user = identity.User?.Value ?? throw new InvalidOperationException("Current Windows user unavailable.");
        string key = user + ":" + process.SessionId + ":" + DataPath(data).ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24];
    }
    internal static string PipeName(string data) => "F10Recorder.v1." + Scope(data);
    internal static string ControlTitle(string data)
    {
        string standard = DataPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "F10Recorder"));
        string suffix = string.Equals(DataPath(data), standard, StringComparison.OrdinalIgnoreCase) ? "" : "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(DataPath(data).ToUpperInvariant())))[..16];
        return HotkeyWindow.ControlTitle + suffix;
    }
}

internal sealed class WorkerRequest
{
    public int Version = WorkerProtocol.Version;
    public string Command = "status";
    public string OperationId;
    public Settings Settings;
    public bool Value;
    public bool AttachUi;
    public long AfterEvent = -1;
}
internal sealed class WorkerEvent
{
    public long Sequence;
    public string Kind, Title, Message;
    public int NotificationKind, Duration;
    public bool Success;
}
internal sealed class WorkerStatus
{
    public string Instance;
    public string CompletedApply;
    public string EngineCapabilityContext;
    public long Revision;
    public Settings Config;
    public int RetainedPreferenceCount;
    public ProfileStatus ProfileStatus = new();
    public bool Paused, Ready, Applying, Saving, ScreenshotInProgress, SaveRegistered, OverlayRegistered, ScreenshotRegistered, Stopping;
    public CapturePhase Phase;
    public bool CanSave;
    public string State, LastScreenshot, ApplicationName;
    public int WorkerProcessId;
    public long Sequence;
    public RecorderMetrics Metrics;
    public WorkerEvent[] Events = [];

    // The desktop edits represented settings. Opaque future preferences remain
    // worker-owned: their JSON escaping and size must not consume reply space.
    internal WorkerStatus ForClient()
    {
        var result = (WorkerStatus)MemberwiseClone();
        if (Config != null)
        {
            result.Config = new Settings();
            foreach (var field in Settings.StoredFields) field.SetValue(result.Config, field.GetValue(Config));
            result.RetainedPreferenceCount = Math.Max(RetainedPreferenceCount, Config.AdditionalPreferences?.Count ?? 0);
        }
        return result;
    }
}
internal sealed class WorkerReply
{
    public int Version = WorkerProtocol.Version;
    public string Error;
    public WorkerStatus Status;
    public RecordingEngineCapabilities Capabilities;
}
internal sealed class WorkerProtocolMismatchException(string message) : IOException(message);

internal static class WorkerProtocol
{
    internal const int Version = 5;
    internal static bool WasStopped(string data)
    {
        try { var profile = RecordingProfile.Load(data); return profile.State.UserStopped || !profile.Status.CanWrite; }
        catch (Exception ex) when (ex is IOException or JsonException) { return false; }
    }
    internal const int MaxBytes = 1024 * 1024;
    internal static readonly JsonSerializerOptions Json = new() { IncludeFields = true };
    internal static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellation)
    {
        byte[] header = new byte[4]; await stream.ReadExactlyAsync(header, cancellation).ConfigureAwait(false);
        int bytes = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (bytes <= 0 || bytes > MaxBytes) throw new InvalidDataException("Recorder message length is invalid.");
        byte[] body = new byte[bytes]; await stream.ReadExactlyAsync(body, cancellation).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(body, Json) ?? throw new InvalidDataException("Empty recorder message.");
    }
    internal static async Task WriteAsync<T>(Stream stream, T message, CancellationToken cancellation)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        if (body.Length > MaxBytes) throw new InvalidDataException("Recorder message is too large.");
        byte[] header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await stream.WriteAsync(header, cancellation).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellation).ConfigureAwait(false);
        await stream.FlushAsync(cancellation).ConfigureAwait(false);
    }
    internal static async Task<WorkerReply> CallAsync(string data, WorkerRequest request, CancellationToken cancellation = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(request.Command == "repairMedia" ? TimeSpan.FromSeconds(180) : request.Command is "apply" or "quit" or "probeMedia" ? TimeSpan.FromSeconds(40) : TimeSpan.FromSeconds(5));
        using var pipe = new NamedPipeClientStream(".", WorkerIdentity.PipeName(data), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
        await WriteAsync(pipe, request, deadline.Token).ConfigureAwait(false);
        var reply = await ReadAsync<WorkerReply>(pipe, deadline.Token).ConfigureAwait(false);
        if (reply.Version != Version) throw new WorkerProtocolMismatchException("Recorder protocol version mismatch. Quit the older recorder before opening this build.");
        return reply;
    }
    internal static WorkerReply EnsureWorker(string data, bool paused, string executable = null, bool attachUi = true)
    {
        Directory.CreateDirectory(data); data = WorkerIdentity.DataPath(data);
        using var launch = ProfileOwnership.AcquireLaunch(data, out data);
        try
        {
            using var check = new CancellationTokenSource(500);
            return CallAsync(data, new WorkerRequest { AttachUi = attachUi }, check.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is not WorkerProtocolMismatchException && (ex is IOException or OperationCanceledException or TimeoutException)) { }
        var info = new ProcessStartInfo(executable ?? Environment.ProcessPath) { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("--recorder-worker"); info.ArgumentList.Add("--data-dir"); info.ArgumentList.Add(data);
        if (paused) info.ArgumentList.Add("--paused");
        using var child = Process.Start(info) ?? throw new IOException("Recorder worker did not start.");
        using var startup = new CancellationTokenSource(10000);
        return CallAsync(data, new WorkerRequest { AttachUi = attachUi }, startup.Token).GetAwaiter().GetResult();
    }
}
