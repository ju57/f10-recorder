using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using Microsoft.Win32.SafeHandles;

// Compiled in-memory by Add-Type. Only read-only process accounting and bounded
// pipe copying; no CreateProcess, acquisition, UI or persistent configuration.
public static class F10ReplayAcceptanceNative
{
    [StructLayout(LayoutKind.Sequential)] public struct Io
    {
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
    }
    [StructLayout(LayoutKind.Sequential)] public struct Memory
    {
        public uint Size, PageFaults;
        public UIntPtr PeakWorkingSet, WorkingSet, PeakPagedPool, PagedPool, PeakNonPagedPool, NonPagedPool, Pagefile, PeakPagefile;
    }
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetProcessIoCounters(SafeProcessHandle process, out Io counters);
    [DllImport("psapi.dll", SetLastError = true)] static extern bool GetProcessMemoryInfo(SafeProcessHandle process, out Memory counters, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref uint characters);
    public static string ReadImage(Process process)
    {
        var path = new StringBuilder(32768); uint characters = 32768;
        if (!QueryFullProcessImageName(process.SafeHandle, 0, path, ref characters)) throw new Win32Exception();
        if (characters == 0 || characters >= 32768 || string.IsNullOrWhiteSpace(path.ToString())) throw new IOException("No verified held-process image.");
        return Path.GetFullPath(path.ToString());
    }
    public static Io ReadIo(Process process)
    {
        if (!GetProcessIoCounters(process.SafeHandle, out var value)) throw new Win32Exception();
        return value;
    }
    public static Memory ReadMemory(Process process)
    {
        if (!GetProcessMemoryInfo(process.SafeHandle, out var value, (uint)Marshal.SizeOf<Memory>())) throw new Win32Exception();
        return value;
    }
    public sealed class PrefixCopy : IDisposable
    {
        readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        int overflow;
        long kept, read;
        public Task Completion { get; }
        public bool Overflow => Volatile.Read(ref overflow) != 0;
        public long Kept => Interlocked.Read(ref kept);
        public long Read => Interlocked.Read(ref read);
        public PrefixCopy(Stream input, Stream output, int maximum)
        {
            if (maximum <= 0) throw new ArgumentOutOfRangeException(nameof(maximum));
            Completion = Copy(input, output, maximum);
        }
        async Task Copy(Stream input, Stream output, int maximum)
        {
            byte[] buffer = new byte[4096];
            while (true)
            {
                int count = await input.ReadAsync(buffer, 0, buffer.Length, cancellation.Token).ConfigureAwait(false);
                if (count == 0) break;
                Interlocked.Add(ref read, count);
                int retain = (int)Math.Min(count, maximum - Kept);
                if (retain > 0)
                {
                    await output.WriteAsync(buffer, 0, retain, cancellation.Token).ConfigureAwait(false);
                    Interlocked.Add(ref kept, retain);
                }
                // Keep draining/discarding after overflow so owned shutdown
                // cannot deadlock on a full pipe. Only the fixed prefix is saved.
                if (retain != count) Volatile.Write(ref overflow, 1);
            }
            await output.FlushAsync(cancellation.Token).ConfigureAwait(false);
        }
        public void Cancel() => cancellation.Cancel();
        public void Dispose()
        {
            if (!Completion.IsCompleted) throw new InvalidOperationException("Cannot dispose an unjoined log reader.");
            cancellation.Dispose();
        }
    }
}
