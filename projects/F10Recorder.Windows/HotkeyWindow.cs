using System.Runtime.InteropServices;
using System.ComponentModel;

namespace F10Recorder;

internal sealed class HotkeyWindow : IDisposable
{
    internal const string ControlTitle = "F10Recorder.ControlWindow";
    internal const uint CommandMessage = 0x8001;
    public event Action Pressed, OverlayPressed, ScreenshotPressed;
    public event Action EnvironmentChanged;
    public event Action<int> CommandRequested;
    public bool SaveRegistered { get; private set; }
    public bool OverlayRegistered { get; private set; }
    public bool ScreenshotRegistered { get; private set; }
    readonly WindowProcedure procedure;
    readonly string className = "F10Recorder.Shortcuts." + Guid.NewGuid().ToString("N");
    readonly IntPtr module;
    IntPtr window;
    public HotkeyWindow(string title = ControlTitle)
    {
        module = GetModuleHandle(null); procedure = Dispatch;
        var definition = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Procedure = Marshal.GetFunctionPointerForDelegate(procedure), Instance = module, ClassName = className };
        if (RegisterClass(ref definition) == 0) throw new Win32Exception();
        window = CreateWindow(0, className, title, 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, module, IntPtr.Zero);
        if (window == IntPtr.Zero) { UnregisterClass(className, module); throw new Win32Exception(); }
        if (!RegisterSessionNotifications(window, 0)) Log.Write("Session-change notifications unavailable: " + Marshal.GetLastWin32Error());
    }
    IntPtr Dispatch(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam)
    {
        // Exceptions must never unwind across a native window procedure.
        try
        {
            if (message == 0x0312) { switch (wParam.ToInt32()) { case 1: Pressed?.Invoke(); break; case 2: OverlayPressed?.Invoke(); break; case 3: ScreenshotPressed?.Invoke(); break; } return IntPtr.Zero; }
            if (message == CommandMessage) { CommandRequested?.Invoke(wParam.ToInt32()); return IntPtr.Zero; }
            if (message == 0x007E || message == 0x02B1 && (wParam.ToInt32() == 7 || wParam.ToInt32() == 8) || message == 0x0218 && (wParam.ToInt32() == 4 || wParam.ToInt32() == 7 || wParam.ToInt32() == 18)) { EnvironmentChanged?.Invoke(); return new IntPtr(1); }
        }
        catch (Exception ex) { Log.Write(ex.ToString()); }
        return DefaultProcedure(handle, message, wParam, lParam);
    }
    public void Register(Settings settings)
    {
        Native.UnregisterHotKey(window, 1); Native.UnregisterHotKey(window, 2); Native.UnregisterHotKey(window, 3);
        SaveRegistered = Native.RegisterHotKey(window, 1, (uint)settings.SaveModifiers | 0x4000, (uint)settings.SaveKey);
        OverlayRegistered = Native.RegisterHotKey(window, 2, (uint)settings.OverlayModifiers | 0x4000, (uint)settings.OverlayKey);
        ScreenshotRegistered = Native.RegisterHotKey(window, 3, (uint)settings.ScreenshotModifiers | 0x4000, (uint)settings.ScreenshotKey);
        var failures = new List<string>(); if (!SaveRegistered) failures.Add(settings.HotkeyText + " (save replay)"); if (!OverlayRegistered) failures.Add(settings.OverlayHotkeyText + " (overlay)"); if (!ScreenshotRegistered) failures.Add(settings.ScreenshotHotkeyText + " (screenshot)");
        if (failures.Count > 0) throw new InvalidOperationException("These shortcuts are already in use: " + string.Join(", ", failures) + ". Choose different combinations in Controls.");
    }
    public void Dispose()
    {
        if (window == IntPtr.Zero) return;
        Native.UnregisterHotKey(window, 1); Native.UnregisterHotKey(window, 2); Native.UnregisterHotKey(window, 3);
        UnregisterSessionNotifications(window);
        DestroyWindow(window); window = IntPtr.Zero; UnregisterClass(className, module); GC.KeepAlive(procedure);
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct WindowClass
    {
        public uint Size, Style; public IntPtr Procedure; public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background; public string MenuName, ClassName; public IntPtr SmallIcon;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)] static extern ushort RegisterClass(ref WindowClass definition);
    [DllImport("user32.dll", EntryPoint = "UnregisterClassW", CharSet = CharSet.Unicode)] static extern bool UnregisterClass(string name, IntPtr instance);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateWindow(uint extended, string className, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")] static extern IntPtr DefaultProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr window);
    [DllImport("wtsapi32.dll", EntryPoint = "WTSRegisterSessionNotification", SetLastError = true)] static extern bool RegisterSessionNotifications(IntPtr window, uint flags);
    [DllImport("wtsapi32.dll", EntryPoint = "WTSUnRegisterSessionNotification")] static extern bool UnregisterSessionNotifications(IntPtr window);
}
