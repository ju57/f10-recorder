using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace F10Recorder
{
    internal static class AudioPipe
    {
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint processId);
        [StructLayout(LayoutKind.Sequential)] struct SecurityAttributes { internal int Length; internal IntPtr Descriptor; internal int Inherit; }
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string text, uint revision, out IntPtr descriptor, out uint size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode, uint instances, uint outputBytes, uint inputBytes, uint timeout, ref SecurityAttributes attributes);
        [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);
        internal static NamedPipeServerStream Create(string name)
        {
            using (var identity = WindowsIdentity.GetCurrent())
            {
                IntPtr descriptor; uint size;
                if (!ConvertStringSecurityDescriptorToSecurityDescriptor("O:" + identity.User.Value + "D:P(A;;GA;;;" + identity.User.Value + ")", 1, out descriptor, out size)) throw new Win32Exception();
                try
                {
                    var attributes = new SecurityAttributes { Length = Marshal.SizeOf(typeof(SecurityAttributes)), Descriptor = descriptor };
                    // Outbound, overlapped, first instance; byte mode and reject
                    // remote clients. Use the same explicit ACL on Framework/.NET.
                    var handle = CreateNamedPipe(@"\\.\pipe\" + name, 0x40080002, 8, 1, 65536, 65536, 0, ref attributes);
                    if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(); }
                    try { return new NamedPipeServerStream(PipeDirection.Out, true, false, handle); }
                    catch { handle.Dispose(); throw; }
                }
                finally { LocalFree(descriptor); }
            }
        }
        internal static void Accept(NamedPipeServerStream pipe, Process encoder, Func<bool> cancelled, int timeoutMilliseconds)
        {
            var connected = pipe.BeginWaitForConnection(null, null);
            try
            {
                var deadline = Stopwatch.StartNew();
                while (!connected.AsyncWaitHandle.WaitOne(50))
                {
                    if (cancelled != null && cancelled()) throw new OperationCanceledException();
                    if (encoder.HasExited || deadline.ElapsedMilliseconds >= timeoutMilliseconds) throw new IOException("The recording engine did not connect to system audio.");
                }
                pipe.EndWaitForConnection(connected);
                uint pid;
                if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out pid)) throw new Win32Exception();
                if (encoder.HasExited || pid != (uint)encoder.Id) throw new IOException("An unexpected process connected to system audio. No audio was delivered.");
                if (cancelled != null && cancelled()) throw new OperationCanceledException();
            }
            catch { pipe.Dispose(); throw; }
            finally { connected.AsyncWaitHandle.Close(); }
        }
    }
}
