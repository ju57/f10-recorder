using System.Diagnostics;
using System.Text.Json;

namespace F10Recorder;

// Real input/owned-process lifetime control, deliberately without media.
// Does not simulate an unkillable decoder or certify Run's unknown-kill branch.
internal static class ReplayDecodeLeaseChecks
{
    sealed class CloseSignal(ManualResetEventSlim closed) : IDisposable
    {
        public void Dispose() => closed.Set();
    }
    static void Require(bool value, string reason) { if (!value) throw new IOException(reason); }

    internal static int Child(string eventName)
    {
        Require(eventName.StartsWith("Local\\F10-issue83-lease-", StringComparison.Ordinal), "Private fixture event required.");
        using var stop = EventWaitHandle.OpenExisting(eventName);
        return stop.WaitOne(TimeSpan.FromSeconds(10)) ? 0 : 3;
    }

    internal static int Run(string root)
    {
        root = Path.GetFullPath(root);
        Require(!Directory.Exists(root) && !File.Exists(root), "Lifetime control needs a new root.");
        Directory.CreateDirectory(root);
        var clock = Stopwatch.StartNew();
        using var parent = Process.GetCurrentProcess();
        long peakRam = 0, peakChildRam = 0; int peakHandles = 0, peakChildHandles = 0;
        Process child = null, observer = null;
        void Guard()
        {
            parent.Refresh(); peakRam = Math.Max(peakRam, parent.WorkingSet64); peakHandles = Math.Max(peakHandles, parent.HandleCount);
            Require(clock.Elapsed.TotalSeconds < 25 && peakRam < 512L * 1024 * 1024 && peakHandles <= 1024,
                "Parent lifetime control resource bound exceeded.");
            Require(new DriveInfo(Path.GetPathRoot(root)).AvailableFreeSpace >= 60_000_000_000,
                "Lifetime control free-space floor exceeded.");
            Require(Directory.EnumerateFiles(root).Sum(f => new FileInfo(f).Length) <= 2_000_000, "Lifetime control output cap exceeded.");
            if (observer != null && !observer.WaitForExit(0))
            {
                observer.Refresh();
                peakChildRam = Math.Max(peakChildRam, observer.WorkingSet64);
                peakChildHandles = Math.Max(peakChildHandles, observer.HandleCount);
                Require(peakChildRam < 256L * 1024 * 1024 && peakChildHandles <= 256, "Benign child resource bound exceeded.");
            }
        }
        string eventName = "Local\\F10-issue83-lease-" + Guid.NewGuid().ToString("N");
        using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
        using var closed = new ManualResetEventSlim();
        using var job = new Native.ProcessJob();
        ReplayDecodeLease input = null;
        bool registryOwns = false, exited = false, released = false, blocked = false;
        int pid = 0; DateTime? started = null; Exception failure = null;
        try
        {
            Guard(); string path = Path.Combine(root, "protected.fixture"); File.WriteAllBytes(path, new byte[32]);
            input = new ReplayDecodeLease(path);
            var start = new ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = false, CreateNoWindow = true };
            if (Path.GetFileNameWithoutExtension(Environment.ProcessPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(typeof(ReplayDecodeLeaseChecks).Assembly.Location);
            start.ArgumentList.Add("--replay-decode-lease-child"); start.ArgumentList.Add(eventName);
            child = Process.Start(start) ?? throw new IOException("Benign lifetime child did not start.");
            pid = child.Id; started = child.StartTime.ToUniversalTime();
            observer = Process.GetProcessById(pid); _ = observer.SafeHandle;
            job.Add(child);
            ReplayExportProcess.RetainUntilExit(child, input, new CloseSignal(closed));
            registryOwns = true; input = null; // Only the registry retains this file owner.
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Guard(); Require(!observer.WaitForExit(0), "Benign fixture exited before retention assertions.");
            int denied = 0;
            try { using var writer = File.OpenWrite(path); } catch (IOException) { denied++; }
            try { File.Move(path, path + ".moved"); } catch (IOException) { denied++; }
            try { File.Delete(path); } catch (IOException) { denied++; }
            blocked = denied == 3 && !closed.IsSet;
            Require(blocked && File.Exists(path), "GC/unknown-stop retention released protected bytes or pathname.");
            stop.Set();
            while (!(exited = observer.WaitForExit(0)) || !closed.IsSet) { Guard(); Thread.Sleep(10); }
            Require(observer.ExitCode == 0, "Benign lifetime child failed.");
            using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Require(exclusive.Length == 32, "Exact-child exit changed or failed to release its protected input.");
            released = true;
        }
        catch (Exception error) { failure = error; }
        finally
        {
            stop.Set();
            // Launch ownership precedes job/observer setup. Even a failed Add
            // must stop and join the exact child rather than dispose it alive.
            var ownedStop = observer ?? (registryOwns ? null : child);
            if (ownedStop != null)
            {
                try
                {
                    if (!ownedStop.WaitForExit(1000)) { ownedStop.Kill(); exited = ownedStop.WaitForExit(5000); }
                    else exited = true;
                    Require(exited, "Benign owned child absence is unconfirmed.");
                }
                catch (Exception error) { failure ??= error; }
            }
            observer?.Dispose();
            if (!registryOwns) { input?.Dispose(); child?.Dispose(); }
            File.WriteAllText(Path.Combine(root, "verification.json"), JsonSerializer.Serialize(new {
                status = failure == null ? "passed" : "failed", error = failure?.ToString(), parentPid = Environment.ProcessId,
                childPid = pid, childStartedUtc = started, exited, blockedThroughGc = blocked, bothLeasesReleasedAfterExactChildExit = released,
                peakRam, peakHandles, peakChildRam, peakChildHandles, seconds = clock.Elapsed.TotalSeconds,
                media = false, capture = false, catalog = false, installedStateChanged = false,
                limit = "Exercises real exact-child retention registry; does not force an unkillable child or the Run unknown-kill branch."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        if (failure != null) { Console.Error.WriteLine(failure); return 1; }
        Console.WriteLine("Real decode-input retention survived GC and released both leases only after exact-child exit.");
        return 0;
    }
}
