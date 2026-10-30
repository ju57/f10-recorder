using System.Diagnostics;
using System.Text.Json;

namespace F10Recorder;

internal static class ReplayExecutableLeaseChecks
{
    internal static int Run(string root, string engineRoot)
    {
        using var owner = PilotStorageObservation.Create(root);
        MediaDependency.RootDirectory = Path.GetFullPath(engineRoot);
        string executable = MediaDependency.ExecutablePath;
        var checks = new List<string>();
        using var lease = MediaDependency.OpenVerifiedExecutable(executable);
        if (!string.Equals(lease.PathName, Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase)) throw new IOException("Lease path changed.");
        checks.Add("lease-is-bound-to-verified-canonical-engine");
        bool writeDenied = false;
        try { using var writer = new FileStream(executable, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); }
        catch (IOException) { writeDenied = true; }
        if (!writeDenied) throw new IOException("Verified read lease permitted a competing write handle.");
        checks.Add("live-lease-denies-competing-write-handle-without-writing-any-byte");
        bool wrongRejected = false;
        try { lease.Start(new ProcessStartInfo(Path.Combine(root, "different-not-executable.exe"))); }
        catch (IOException) { wrongRejected = true; }
        if (!wrongRejected) throw new IOException("Lease permitted an unrelated executable.");
        checks.Add("different-executable-rejected-before-process-creation");
        lease.Dispose();
        bool disposedRejected = false;
        try { lease.Start(new ProcessStartInfo(executable) { Arguments = "-version", UseShellExecute = false, CreateNoWindow = true }); }
        catch (ObjectDisposedException) { disposedRejected = true; }
        if (!disposedRejected) throw new IOException("Disposed lease started a process.");
        checks.Add("disposed-lease-cannot-start-engine");
        File.WriteAllText(Path.Combine(root, "verification.json"), JsonSerializer.Serialize(new { result = "passed", checks,
            effects = "Read-only approved engine verification, denied write-handle attempt with zero bytes written and rejected launch attempts. No process, engine, capture/audio/GPU or user-state change." }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Verified executable lease:4/4 passed;0children"); return 0;
    }
}
