using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Forms = System.Windows.Forms;

namespace F10Recorder;

// These helpers belong only to the controlled calibration fixture, never the
// installed worker. No power scheme, display mode, preference or input is changed.
internal sealed class CalibrationWakeLease : IDisposable
{
    internal const uint Required = 0x80000003;
    readonly Func<uint, uint> set;
    readonly int thread = Environment.CurrentManagedThreadId;
    internal uint Previous { get; }
    internal bool Restored { get; private set; }
    internal CalibrationWakeLease(Func<uint, uint> set = null)
    {
        this.set = set ?? SetThreadExecutionState;
        Previous = this.set(Required);
        if (Previous == 0) throw new IOException("Calibration display/system wake request failed; no source admitted.");
    }
    public void Dispose()
    {
        if (Restored) return;
        if (thread != Environment.CurrentManagedThreadId) throw new IOException("Wake lease must be restored on its owning thread.");
        if (set(Previous) == 0) throw new IOException("Calibration wake request restoration failed.");
        Restored = true;
    }
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint SetThreadExecutionState(uint flags);
}

internal sealed class CalibrationDesktop : Forms.NativeWindow, IDisposable
{
    static readonly Guid ConsoleDisplayState = new("6FE69556-704A-47A0-8F24-C28D936FDA47");
    readonly List<object> events = [];
    readonly IntPtr notification;
    internal int? DisplayState { get; private set; }
    int omitted;
    internal CalibrationDesktop()
    {
        CreateHandle(new Forms.CreateParams { Caption = "F10 calibration display observation" });
        Guid setting = ConsoleDisplayState;
        notification = RegisterPowerSettingNotification(Handle, ref setting, 0);
        if (notification == IntPtr.Zero) { DestroyHandle(); throw new IOException("Display power observation registration failed."); }
    }
    protected override void WndProc(ref Forms.Message message)
    {
        if (message.Msg == 0x218 && message.WParam.ToInt64() == 0x8013 && message.LParam != IntPtr.Zero
            && Marshal.PtrToStructure<Guid>(message.LParam) == ConsoleDisplayState && Marshal.ReadInt32(message.LParam, 16) == 4)
        {
            int state = Marshal.ReadInt32(message.LParam, 20);
            DisplayState = state;
            if (events.Count < 64) events.Add(new { utc = DateTime.UtcNow, state }); else omitted++;
        }
        base.WndProc(ref message);
    }
    internal object Snapshot() => new { utc = DateTime.UtcNow, desktop = ReadDesktop(), displayState = DisplayState, events, omitted };
    internal void AwaitDisplayOn()
    {
        RequireLocalDesktop();
        var timer = Stopwatch.StartNew();
        while (DisplayState != 1 && timer.Elapsed.TotalSeconds < 3) { Forms.Application.DoEvents(); Thread.Sleep(10); }
        if (DisplayState != 1) throw new IOException("Display-on state was not observed within3seconds; no capture admitted.");
    }
    internal static void RequireLocalDesktop()
    {
        if (!LocalDesktop(ReadDesktop()))
            throw new IOException("Calibration requires the local unlocked Default input desktop; no capture admitted.");
    }
    internal static bool LocalDesktop(DesktopState state) => state.SessionId == state.ConsoleSessionId && !state.RemoteSession
        && state.ThreadDesktop == "Default" && state.InputDesktop == "Default" && state.ReceivingInput;
    internal sealed record DesktopState(int SessionId, uint ConsoleSessionId, bool RemoteSession,
        string ThreadDesktop, string InputDesktop, bool ReceivingInput, double? IdleSeconds);
    internal static DesktopState ReadDesktop()
    {
        string Name(IntPtr desktop)
        {
            var text = new StringBuilder(256);
            return desktop != IntPtr.Zero && GetUserObjectInformation(desktop, 2, text, text.Capacity * 2, out _) ? text.ToString() : null;
        }
        IntPtr input = OpenInputDesktop(0, false, 1);
        try
        {
            bool receiving = input != IntPtr.Zero && GetUserObjectInformationBool(input, 6, out int value, 4, out _) && value != 0;
            var idle = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() };
            double? seconds = GetLastInputInfo(ref idle) ? unchecked((uint)Environment.TickCount - idle.Tick) / 1000.0 : null;
            using var process = Process.GetCurrentProcess();
            return new(process.SessionId, WTSGetActiveConsoleSessionId(),
                Forms.SystemInformation.TerminalServerSession, Name(GetThreadDesktop(GetCurrentThreadId())), Name(input), receiving, seconds);
        }
        finally { if (input != IntPtr.Zero) CloseDesktop(input); }
    }
    internal static int Observe(string root)
    {
        using var owner = PilotStorageObservation.Create(root);
        using var display = new CalibrationDesktop();
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed.TotalSeconds < 1) { Forms.Application.DoEvents(); Thread.Sleep(10); }
        File.WriteAllText(Path.Combine(root, "desktop.json"), JsonSerializer.Serialize(new { state = display.Snapshot(),
            effects = "Read-only desktop/session/display notifications through one hidden message window; no input, wake, preference change, audio, GPU, capture or child." },
            new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
    public void Dispose()
    {
        try { if (notification != IntPtr.Zero && !UnregisterPowerSettingNotification(notification)) throw new IOException("Display notification cleanup failed."); }
        finally { DestroyHandle(); }
    }
    [StructLayout(LayoutKind.Sequential)] struct LastInput { internal uint Size, Tick; }
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr RegisterPowerSettingNotification(IntPtr window, ref Guid setting, uint flags);
    [DllImport("user32.dll", SetLastError = true)] static extern bool UnregisterPowerSettingNotification(IntPtr notification);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll")] static extern IntPtr GetThreadDesktop(uint thread);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool GetUserObjectInformation(IntPtr obj, int index, StringBuilder text, int length, out int required);
    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", SetLastError = true)] static extern bool GetUserObjectInformationBool(IntPtr obj, int index, out int value, int length, out int required);
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LastInput input);
}
