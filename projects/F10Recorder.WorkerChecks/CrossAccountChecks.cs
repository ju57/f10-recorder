using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;

namespace F10Recorder;

internal static class CrossAccountChecks
{
    sealed class Challenge { public string Pipe, OwnerSid, LockPath; }
    sealed class ClientResult { public string ClientSid; public bool PipeDenied, OwnershipDenied; public string Error; }
    // Coordinated manually: start Server as the recorder user, then Client as a
    // genuinely different Windows account. This code never requests credentials,
    // creates accounts, launches runas, or changes account policy.
    internal static int Server(string data)
    {
        if (Directory.Exists(data) && Directory.EnumerateFileSystemEntries(data).Any()) throw new IOException("Use a new empty directory for each cross-account trial.");
        Directory.CreateDirectory(data); string root = Path.Combine(data, "private");
        TrustedStorage.CreatePrivateDirectory(root); ProfileOwnership.RootDirectory = root;
        string profile = Path.Combine(data, "profile"); Directory.CreateDirectory(profile);
        using var owner = ProfileOwnership.Acquire(profile);
        string name = "F10Recorder-cross-account-" + Guid.NewGuid().ToString("N");
        using var pipe = AudioPipe.Create(name);
        using var identity = WindowsIdentity.GetCurrent();
        var challenge = new Challenge { Pipe = name, OwnerSid = identity.User.Value, LockPath = Directory.GetFiles(Path.Combine(root, "profiles"), "*.lock").Single() };
        string challengePath = Path.Combine(data, "challenge.json"), responsePath = Path.Combine(data, "client-result.json");
        File.WriteAllText(challengePath, JsonSerializer.Serialize(challenge, WorkerProtocol.Json));
        Console.WriteLine("Run --security-account-client \"" + challengePath + "\" as a different Windows account within three minutes.");
        var connection = pipe.BeginWaitForConnection(null, null); var clock = Stopwatch.StartNew();
        try
        {
            while (clock.Elapsed < TimeSpan.FromMinutes(3) && !File.Exists(responsePath) && !connection.IsCompleted) Thread.Sleep(100);
            if (connection.IsCompleted) throw new IOException("A foreign account reached the protected audio pipe.");
            if (!File.Exists(responsePath)) throw new TimeoutException("No different-account result received; cross-account verification remains outstanding.");
            var result = JsonSerializer.Deserialize<ClientResult>(File.ReadAllText(responsePath), WorkerProtocol.Json);
            bool passed = result != null && result.ClientSid != challenge.OwnerSid && result.PipeDenied && result.OwnershipDenied && result.Error == null;
            File.WriteAllText(Path.Combine(data, "verification.json"), JsonSerializer.Serialize(new { result = passed ? "passed" : "failed", ownerSid = challenge.OwnerSid, client = result }, WorkerProtocol.Json));
            return passed ? 0 : 1;
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(data, "verification.json"), JsonSerializer.Serialize(new { result = "inconclusive", error = ex.ToString() })); return 1; }
        finally { pipe.Dispose(); connection.AsyncWaitHandle.Close(); }
    }
    internal static int Client(string challengePath)
    {
        var result = new ClientResult();
        try
        {
            var challenge = JsonSerializer.Deserialize<Challenge>(File.ReadAllText(challengePath), WorkerProtocol.Json) ?? throw new IOException("No challenge.");
            using var identity = WindowsIdentity.GetCurrent(); result.ClientSid = identity.User.Value;
            if (result.ClientSid == challenge.OwnerSid) throw new IOException("Run this client under a genuinely different account.");
            try { using var pipe = new NamedPipeClientStream(".", challenge.Pipe, PipeDirection.In); pipe.Connect(3000); }
            catch (UnauthorizedAccessException) { result.PipeDenied = true; }
            try { using var file = new FileStream(challenge.LockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite); }
            catch (UnauthorizedAccessException) { result.OwnershipDenied = true; }
        }
        catch (Exception ex) { result.Error = ex.ToString(); }
        string response = Path.Combine(Path.GetDirectoryName(challengePath), "client-result.json"), temporary = response + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(result, WorkerProtocol.Json)); File.Move(temporary, response);
        return result.Error == null && result.PipeDenied && result.OwnershipDenied ? 0 : 1;
    }
}
