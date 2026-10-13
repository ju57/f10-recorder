using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace F10Recorder
{
    internal static class Native
    {
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] internal static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] internal static extern bool IsChild(IntPtr parent, IntPtr child);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(IntPtr window, System.Text.StringBuilder name, int maximum);
        [DllImport("user32.dll")] internal static extern IntPtr GetShellWindow();
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool QueryFullProcessImageName(IntPtr process, uint flags, System.Text.StringBuilder path, ref uint size);
        internal static string ProcessImagePath(uint processId)
        {
            IntPtr process = OpenProcess(0x1000, false, processId); if (process == IntPtr.Zero) return null;
            try { var path = new System.Text.StringBuilder(32768); uint length = (uint)path.Capacity; return QueryFullProcessImageName(process, 0, path, ref length) ? path.ToString() : null; }
            finally { CloseHandle(process); }
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr FindWindow(string className, string title);
        [DllImport("user32.dll")] internal static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool CreateHardLink(string link, string existing, IntPtr reserved);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint size);
        [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        [DllImport("dxgi.dll")] static extern int CreateDXGIFactory1(ref Guid iid, out IntPtr factory);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int Enumerate(IntPtr self, uint index, out IntPtr result);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetDescription(IntPtr self, out OutputDescription description);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct OutputDescription
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            public int Left, Top, Right, Bottom;
            [MarshalAs(UnmanagedType.Bool)] public bool Attached;
            public int Rotation;
            public IntPtr Monitor;
        }
        static T Method<T>(IntPtr self, int slot) where T : class
        {
            return Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(self), slot * IntPtr.Size), typeof(T)) as T;
        }
        // FFmpeg ddagrab uses adapter 0. Match its outputs to Windows' actual primary display.
        internal static int PrimaryOutputIndex()
        {
            IntPtr factory = IntPtr.Zero, adapter = IntPtr.Zero;
            try
            {
                Guid iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
                Marshal.ThrowExceptionForHR(CreateDXGIFactory1(ref iid, out factory));
                Marshal.ThrowExceptionForHR(Method<Enumerate>(factory, 12)(factory, 0, out adapter));
                string primary = PrimaryDisplayName();
                for (uint i = 0; i < 32; i++)
                {
                    IntPtr output;
                    if (Method<Enumerate>(adapter, 7)(adapter, i, out output) < 0) break;
                    try { OutputDescription d; Marshal.ThrowExceptionForHR(Method<GetDescription>(output, 7)(output, out d)); if (d.Attached && d.DeviceName == primary) return (int)i; }
                    finally { Marshal.Release(output); }
                }
                throw new InvalidOperationException("Primary display is not attached to adapter 0. Select a display index in Settings.");
            }
            finally { if (adapter != IntPtr.Zero) Marshal.Release(adapter); if (factory != IntPtr.Zero) Marshal.Release(factory); }
        }
        [StructLayout(LayoutKind.Sequential)] internal struct DesktopPoint { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] internal struct DesktopRectangle { public int Left, Top, Right, Bottom; public int Width { get { return Right - Left; } } public int Height { get { return Bottom - Top; } } }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct MonitorInformation
        {
            public int Size; public DesktopRectangle Bounds, WorkArea; public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        }
        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(DesktopPoint point, uint flags);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)] static extern bool GetMonitorInformation(IntPtr monitor, ref MonitorInformation info);
        static MonitorInformation Monitor(IntPtr handle)
        {
            var info = new MonitorInformation(); info.Size = Marshal.SizeOf(typeof(MonitorInformation));
            if (!GetMonitorInformation(handle, ref info)) throw new System.ComponentModel.Win32Exception(); return info;
        }
        static string PrimaryDisplayName() { return Monitor(MonitorFromPoint(new DesktopPoint(), 1)).DeviceName; }
        internal static DesktopRectangle DisplayBounds(IntPtr window) { return Monitor(MonitorFromWindow(window, 2)).Bounds; }
        [StructLayout(LayoutKind.Sequential)] struct BasicLimit
        {
            public long PerProcess, PerJob;
            public uint Flags;
            public UIntPtr Min, Max;
            public uint Active;
            public UIntPtr Affinity;
            public uint Priority, Scheduling;
        }
        [StructLayout(LayoutKind.Sequential)] struct IoCounters { public ulong R, W, O, RB, WB, OB; }
        [StructLayout(LayoutKind.Sequential)] struct ExtendedLimit
        {
            public BasicLimit Basic; public IoCounters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcess, PeakJob;
        }
        internal sealed class ProcessJob : IDisposable
        {
            IntPtr handle;
            public ProcessJob()
            {
                handle = CreateJobObject(IntPtr.Zero, null);
                if (handle == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
                var info = new ExtendedLimit(); info.Basic.Flags = 0x2000;
                IntPtr p = Marshal.AllocHGlobal(Marshal.SizeOf(info));
                try { Marshal.StructureToPtr(info, p, false); if (!SetInformationJobObject(handle, 9, p, (uint)Marshal.SizeOf(info))) throw new System.ComponentModel.Win32Exception(); }
                finally { Marshal.FreeHGlobal(p); }
            }
            public void Add(Process process) { if (!AssignProcessToJobObject(handle, process.Handle)) throw new System.ComponentModel.Win32Exception(); }
            public void Dispose() { if (handle != IntPtr.Zero) { CloseHandle(handle); handle = IntPtr.Zero; } }
        }
    }
}
