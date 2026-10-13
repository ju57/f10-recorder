using System.Runtime.InteropServices;

namespace F10Recorder;

internal static class WindowStyles
{
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] internal static extern nint Get(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern nint Set(nint window, int index, nint value);
    public static void Passive(nint window)
    {
        Set(window, -20, Get(window, -20) | 0x08000000 | 0x00000080 | 0x00000020 | 0x00080000);
        Native.SetWindowPos(window, new IntPtr(-1), 0, 0, 0, 0, 0x73);
    }
}
