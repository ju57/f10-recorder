using System.Runtime.InteropServices;

namespace F10Recorder;

// Test-only, GPU-rendered flip-model source. No capture or product code uses this.
internal sealed class GpuPacingSource : IDisposable
{
    IntPtr swap, swap2, device, context, context1, view, wait;
    readonly ClearTarget clear;
    readonly ClearView clearRect;
    readonly Present present;
    readonly Statistics statistics;
    readonly StreamWriter trace;
    readonly int width, height;
    static readonly float[][] Colors = [[.03f, .08f, .25f, 1], [.25f, .03f, .08f, 1], [.03f, .25f, .08f, 1], [.25f, .16f, .03f, 1]];
    static readonly float[] White = [1, 1, 1, 1], Black = [0, 0, 0, 1], Gray = [.35f, .35f, .35f, 1];
    internal int RefreshRate { get; }
    internal uint SyncInterval { get; }
    internal bool HardwarePaced { get; }

    internal GpuPacingSource(IntPtr window, int width, int height, int rate, string directory)
    {
        this.width = width; this.height = height;
        IntPtr mode = Marshal.AllocHGlobal(220);
        try
        {
            for (int i = 0; i < 220; i++) Marshal.WriteByte(mode, i, 0);
            Marshal.WriteInt16(mode, 68, 220);
            if (!EnumDisplaySettings(null, -1, mode)) throw new System.ComponentModel.Win32Exception();
            RefreshRate = Marshal.ReadInt32(mode, 184);
        }
        finally { Marshal.FreeHGlobal(mode); }
        uint interval = (uint)Math.Clamp((int)Math.Round(RefreshRate / (double)rate), 1, 4);
        HardwarePaced = Math.Abs(RefreshRate - rate * interval) <= 2;
        SyncInterval = HardwarePaced ? interval : 1;
        trace = new StreamWriter(Path.Combine(directory, "dxgi-present.csv"));
        trace.WriteLine("id,present_return_qpc,statistics_hr,present_count,present_refresh_count,sync_refresh_count,sync_qpc");
        try
        {
            var desc = new SwapDescription { Width = (uint)width, Height = (uint)height, RateDenominator = 1, Format = 28, SampleCount = 1, Usage = 0x20, BufferCount = 2, Window = window, Windowed = 1, Effect = 4, Flags = 64 };
            Marshal.ThrowExceptionForHR(CreateDeviceAndSwapChain(IntPtr.Zero, 1, IntPtr.Zero, 0x20, IntPtr.Zero, 0, 7, ref desc, out swap, out device, out _, out context));
            Guid iid = new("a8be2ac4-199f-4946-b331-79599fb98de7");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(swap, in iid, out swap2));
            Marshal.ThrowExceptionForHR(Method<SetLatency>(swap2, 31)(swap2, 1));
            wait = Method<GetWait>(swap2, 33)(swap2);
            if (wait == IntPtr.Zero) throw new IOException("Swap-chain frame-latency wait handle is unavailable.");
            iid = new("bb2c6faa-b5fb-4082-8e6b-388b8cfa90e1");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(context, in iid, out context1));
            iid = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
            Marshal.ThrowExceptionForHR(Method<GetBuffer>(swap, 9)(swap, 0, ref iid, out var texture));
            try { Marshal.ThrowExceptionForHR(Method<CreateView>(device, 9)(device, texture, IntPtr.Zero, out view)); }
            finally { Marshal.Release(texture); }
            clear = Method<ClearTarget>(context, 50); clearRect = Method<ClearView>(context1, 132);
            present = Method<Present>(swap, 8); statistics = Method<Statistics>(swap, 16);
        }
        catch { Dispose(); throw; }
    }
    internal void Draw(long frame)
    {
        if (WaitForSingleObject(wait, 2000) != 0) throw new IOException("GPU source presentation did not advance.");
        clear(context, view, Colors[(frame / 7) % Colors.Length]);
        var stripe = new Rectangle { Left = (int)(frame * 11 % (width - 96)), Top = 0, Right = (int)(frame * 11 % (width - 96)) + 96, Bottom = height };
        clearRect(context1, view, Gray, ref stripe, 1);
        uint id = (uint)frame & 0xFFFFFF;
        uint checksum = ((id & 255) ^ ((id >> 8) & 255) ^ ((id >> 16) & 255) ^ 0xA7) & 255;
        uint word = id | checksum << 24;
        for (int bit = 0; bit < 32; bit++)
        {
            bool on = (word & 1u << bit) != 0;
            var cell = new Rectangle { Left = 32 + bit * 24, Top = 32, Right = 32 + (bit + 1) * 24, Bottom = 56 };
            clearRect(context1, view, on ? White : Black, ref cell, 1);
            cell.Top = 64; cell.Bottom = 88;
            clearRect(context1, view, on ? Black : White, ref cell, 1);
        }
        Marshal.ThrowExceptionForHR(present(swap, SyncInterval, 0));
        long returned = System.Diagnostics.Stopwatch.GetTimestamp();
        int hr = statistics(swap, out var stats);
        trace.WriteLine($"{frame},{returned},{hr},{stats.PresentCount},{stats.PresentRefreshCount},{stats.SyncRefreshCount},{stats.SyncQpc}");
        if (frame % 120 == 0) trace.Flush();
    }
    public void Dispose()
    {
        trace?.Dispose();
        if (wait != IntPtr.Zero) { CloseHandle(wait); wait = IntPtr.Zero; }
        foreach (var value in new[] { view, context1, context, device, swap2, swap }) if (value != IntPtr.Zero) Marshal.Release(value);
        view = context1 = context = device = swap2 = swap = IntPtr.Zero;
    }
    static T Method<T>(IntPtr self, int slot) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(self), slot * IntPtr.Size));
    [StructLayout(LayoutKind.Sequential)] struct SwapDescription
    {
        public uint Width, Height, RateNumerator, RateDenominator, Format, Scanline, Scaling, SampleCount, SampleQuality, Usage, BufferCount;
        public IntPtr Window; public uint Windowed, Effect, Flags;
    }
    [StructLayout(LayoutKind.Sequential)] struct Rectangle { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct FrameStatistics { public uint PresentCount, PresentRefreshCount, SyncRefreshCount; public long SyncQpc, SyncGpu; }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetBuffer(IntPtr self, uint index, ref Guid iid, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateView(IntPtr self, IntPtr texture, IntPtr desc, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void ClearTarget(IntPtr self, IntPtr view, float[] color);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void ClearView(IntPtr self, IntPtr view, float[] color, ref Rectangle rectangle, uint count);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int Present(IntPtr self, uint interval, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int Statistics(IntPtr self, out FrameStatistics value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SetLatency(IntPtr self, uint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate IntPtr GetWait(IntPtr self);
    [DllImport("d3d11.dll", EntryPoint = "D3D11CreateDeviceAndSwapChain")] static extern int CreateDeviceAndSwapChain(IntPtr adapter, uint driver, IntPtr software, uint flags, IntPtr levels, uint count, uint sdk, ref SwapDescription desc, out IntPtr swap, out IntPtr device, out uint level, out IntPtr context);
    [DllImport("user32.dll", EntryPoint = "EnumDisplaySettingsW", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool EnumDisplaySettings(string name, int mode, IntPtr value);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
}
