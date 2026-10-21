using System.Diagnostics;

namespace F10Recorder;

internal static class ProfileOwnership
{
    internal static string RootDirectory = TrustedStorage.DefaultRoot;
    internal static IDisposable Acquire(string data) => TrustedStorage.AcquireLock(RootDirectory, "profile", WorkerIdentity.DataPath(data));
    internal static IDisposable Acquire(string data, out string bound) => TrustedStorage.AcquireLock(RootDirectory, "profile", data, out bound);
    internal static IDisposable TryAcquireUi(string data, out string bound) => TrustedStorage.TryAcquireLock(RootDirectory, "ui-" + Process.GetCurrentProcess().SessionId, data, out bound);
    internal static IDisposable AcquireLaunch(string data, out string bound)
    {
        Directory.CreateDirectory(data);
        data = WorkerIdentity.DataPath(data);
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            var lease = TrustedStorage.TryAcquireLock(RootDirectory, "launch-" + Process.GetCurrentProcess().SessionId, data, out bound);
            if (lease != null) return lease;
            if (deadline.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Recorder startup is already in progress.");
            Thread.Sleep(50);
        }
    }
}
