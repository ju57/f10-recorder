using Microsoft.Win32.SafeHandles;

namespace F10Recorder;

// Only the isolated nonmedia follow-up can opt into these finite measurements
// or the held-native-handle injection. Ordinary qualification cannot disable
// diagnostics or install this hook through a plan file.
internal sealed class CalibrationObserverProbe
{
    internal bool DiagnosticsEnabled = true;
    internal Action<SafeFileHandle, string> Opened;
    internal readonly List<(double Milliseconds, long AllocatedBytes)> Scans = new();
    internal void Record(double milliseconds, long allocatedBytes)
    {
        if (Scans.Count >= 64) throw new IOException("Observer fixture exceeded64 scans.");
        Scans.Add((milliseconds, allocatedBytes));
    }
}
