using System.Runtime.InteropServices;

namespace F10Recorder;

internal static class WindowStyles
{
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] internal static extern nint Get(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern nint Set(nint window, int index, nint value);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SetWindowDisplayAffinity(nint window, uint affinity);
    public static bool ExcludeFromCapture(nint window) => SetCaptureExclusion(window, true);
    public static bool SetCaptureExclusion(nint window, bool exclude)
    {
        // Earlier Windows versions turn this value into an opaque capture mask.
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)) return false;
        if (SetWindowDisplayAffinity(window, exclude ? 0x11u : 0u)) return true;
        Log.Write("UI capture exclusion could not be applied; Win32 error " + Marshal.GetLastWin32Error());
        return false;
    }
    public static void Passive(nint window, bool interactive = false)
    {
        nint styles = Get(window, -20) | 0x08000000 | 0x00000080 | 0x00080000;
        Set(window, -20, interactive ? styles & ~0x00000020 : styles | 0x00000020);
        Native.SetWindowPos(window, new IntPtr(-1), 0, 0, 0, 0, 0x73);
    }
}
