using System;
using System.Windows.Forms;
namespace F10Recorder
{
    internal sealed class HotkeyWindow : NativeWindow, IDisposable
    {
        internal const string ControlTitle = "F10Recorder.ControlWindow";
        internal const uint CommandMessage = 0x8001;
        public event Action Pressed;
        public event Action OverlayPressed;
        public event Action ScreenshotPressed;
        public bool SaveRegistered { get; private set; }
        public bool OverlayRegistered { get; private set; }
        public bool ScreenshotRegistered { get; private set; }
        public event Action<int> CommandRequested;
        public HotkeyWindow(string title = ControlTitle) { CreateHandle(new CreateParams { Caption = title }); }
        public void Register(Settings settings)
        {
            Log.Write("Registering hotkey on window " + Handle.ToInt64());
            Native.UnregisterHotKey(Handle, 1);
            Native.UnregisterHotKey(Handle, 2); Native.UnregisterHotKey(Handle, 3);
            SaveRegistered = Native.RegisterHotKey(Handle, 1, (uint)settings.SaveModifiers | 0x4000, (uint)settings.SaveKey);
            OverlayRegistered = Native.RegisterHotKey(Handle, 2, (uint)settings.OverlayModifiers | 0x4000, (uint)settings.OverlayKey);
            ScreenshotRegistered = Native.RegisterHotKey(Handle, 3, (uint)settings.ScreenshotModifiers | 0x4000, (uint)settings.ScreenshotKey);
            var failures = new System.Collections.Generic.List<string>();
            if (!SaveRegistered) failures.Add(settings.HotkeyText + " (save replay)");
            if (!OverlayRegistered) failures.Add(settings.OverlayHotkeyText + " (overlay)");
            if (!ScreenshotRegistered) failures.Add(settings.ScreenshotHotkeyText + " (screenshot)");
            if (failures.Count > 0) throw new InvalidOperationException("These shortcuts are already in use: " + string.Join(", ", failures) + ". Choose different combinations in Controls.");
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0312 && m.WParam.ToInt32() == 1) { var callback = Pressed; if (callback != null) callback(); }
            if (m.Msg == 0x0312 && m.WParam.ToInt32() == 2) { var callback = OverlayPressed; if (callback != null) callback(); }
            if (m.Msg == 0x0312 && m.WParam.ToInt32() == 3) { var callback = ScreenshotPressed; if (callback != null) callback(); }
            if (m.Msg == CommandMessage) { var callback = CommandRequested; if (callback != null) callback(m.WParam.ToInt32()); }
            base.WndProc(ref m);
        }
        public void Dispose() { Native.UnregisterHotKey(Handle, 1); Native.UnregisterHotKey(Handle, 2); Native.UnregisterHotKey(Handle, 3); DestroyHandle(); }
    }
}
