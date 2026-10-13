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
    internal static string DataPath(string data) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(data));
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
    public int Version = 1;
    public string Command = "status";
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
    public long Revision;
    public Settings Config;
    public bool Paused, Ready, Applying, ScreenshotInProgress, SaveRegistered, OverlayRegistered, ScreenshotRegistered, Stopping;
    public string State, LastScreenshot, ApplicationName;
    public int WorkerProcessId;
    public long Sequence;
    public RecorderMetrics Metrics;
    public WorkerEvent[] Events = [];
}
internal sealed class WorkerPersistentState { public bool Paused, UserStopped; }
internal sealed class WorkerReply
{
    public int Version = 1;
    public string Error;
    public WorkerStatus Status;
}

internal static class WorkerProtocol
{
    internal static bool WasStopped(string data)
    {
        try { return JsonSerializer.Deserialize<WorkerPersistentState>(File.ReadAllText(Path.Combine(data, "worker-state.json")), Json)?.UserStopped == true; }
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
        deadline.CancelAfter(request.Command is "apply" or "quit" ? TimeSpan.FromSeconds(40) : TimeSpan.FromSeconds(5));
        using var pipe = new NamedPipeClientStream(".", WorkerIdentity.PipeName(data), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
        await WriteAsync(pipe, request, deadline.Token).ConfigureAwait(false);
        var reply = await ReadAsync<WorkerReply>(pipe, deadline.Token).ConfigureAwait(false);
        if (reply.Version != 1) throw new InvalidDataException("Recorder protocol version mismatch.");
        return reply;
    }
    internal static WorkerReply EnsureWorker(string data, bool paused, string executable = null, bool attachUi = true)
    {
        using var launch = new Mutex(false, @"Local\F10Recorder-Launch-" + WorkerIdentity.Scope(data));
        bool owns = false;
        try
        {
            try { owns = launch.WaitOne(TimeSpan.FromSeconds(10)); } catch (AbandonedMutexException) { owns = true; }
            if (!owns) throw new TimeoutException("Recorder startup is already in progress.");
            try
            {
                using var check = new CancellationTokenSource(500);
                return CallAsync(data, new WorkerRequest { AttachUi = attachUi }, check.Token).GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException) { }
            var info = new ProcessStartInfo(executable ?? Environment.ProcessPath) { UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add("--recorder-worker"); info.ArgumentList.Add("--data-dir"); info.ArgumentList.Add(data);
            if (paused) info.ArgumentList.Add("--paused");
            using var child = Process.Start(info) ?? throw new IOException("Recorder worker did not start.");
            using var startup = new CancellationTokenSource(10000);
            return CallAsync(data, new WorkerRequest { AttachUi = attachUi }, startup.Token).GetAwaiter().GetResult();
        }
        finally { if (owns) launch.ReleaseMutex(); }
    }
}
