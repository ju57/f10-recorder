using System;
using System.Diagnostics;
namespace F10Recorder
{
    internal sealed class ForegroundApplicationTracker : IDisposable
    {
        readonly System.Windows.Forms.Timer poll = new System.Windows.Forms.Timer { Interval = 250 };
        readonly int excludedProcessId;
        IntPtr lastWindow;
        IntPtr lastExternalWindow;
        uint lastExternalProcess;
        internal string ApplicationName { get; private set; }
        internal event Action Changed;
        internal ForegroundApplicationTracker()
        {
            using (var process = Process.GetCurrentProcess()) excludedProcessId = process.Id;
            ApplicationName = "Desktop"; poll.Tick += delegate { Sample(); }; Sample(); poll.Start();
        }
        internal string Sample()
        {
            IntPtr window = Native.GetForegroundWindow();
            uint owner;
            if (lastExternalWindow != IntPtr.Zero && (!Native.IsWindow(lastExternalWindow) || Native.GetWindowThreadProcessId(lastExternalWindow, out owner) == 0 || owner != lastExternalProcess)) { lastExternalWindow = IntPtr.Zero; Update("Desktop"); }
            if (window == lastWindow && window != IntPtr.Zero) return ApplicationName;
            lastWindow = window;
            string name = ApplicationFolders.WindowApplication(window, excludedProcessId);
            if (name != null) { lastExternalWindow = window; Native.GetWindowThreadProcessId(window, out lastExternalProcess); Update(name); }
            return ApplicationName;
        }
        void Update(string name) { if (ApplicationName == name) return; ApplicationName = name; var callback = Changed; if (callback != null) callback(); }
        public void Dispose() { poll.Dispose(); }
    }
}
