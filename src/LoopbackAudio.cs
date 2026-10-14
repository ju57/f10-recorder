using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace F10Recorder
{
    // Raw interleaved PCM in the default rendering endpoint's shared mix format.
    // Start transfers ownership of the output stream; Dispose closes it to unblock writes.
    public sealed class LoopbackAudio : IDisposable
    {
        private const uint Loopback = 0x00020000;
        private const uint Silent = 0x00000002;
        private const uint TimestampError = 0x00000004;
        private readonly object gate = new object();
        private readonly int blockAlign;
        private volatile bool stopping;
        private volatile Exception failure;
        private Stream destination;
        private Thread worker;
        private bool disposed;

        public int SampleRate { get; private set; }
        public int Channels { get; private set; }
        public string FfmpegFormat { get; private set; }
        public string EndpointId { get; private set; }
        public Exception Failure { get { return failure; } }

        public LoopbackAudio()
        {
            IMMDeviceEnumerator enumerator = null;
            IMMDevice device = null;
            IAudioClient client = null;
            IntPtr format = IntPtr.Zero;
            try
            {
                enumerator = CreateEnumerator();
                Check(enumerator.GetDefaultAudioEndpoint(0, 0, out device));
                string id;
                Check(device.GetId(out id));
                EndpointId = id;
                client = Activate(device);
                Check(client.GetMixFormat(out format));
                MixFormat mix = ReadFormat(format);
                SampleRate = mix.Rate;
                Channels = mix.Channels;
                FfmpegFormat = mix.Format;
                blockAlign = mix.BlockAlign;
            }
            finally
            {
                if (format != IntPtr.Zero) Marshal.FreeCoTaskMem(format);
                Release(client);
                Release(device);
                Release(enumerator);
            }
        }

        public void Start(Stream output)
        {
            if (output == null) throw new ArgumentNullException("output");
            if (!output.CanWrite) throw new ArgumentException("Audio output must be writable.", "output");
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException("LoopbackAudio");
                if (worker != null) throw new InvalidOperationException("Loopback audio has already started.");
                destination = output;
                worker = new Thread(Run);
                worker.IsBackground = true;
                worker.Name = "F10 WASAPI loopback";
                worker.SetApartmentState(ApartmentState.MTA);
                worker.Start();
            }
        }

        // Returns true when capture must be rebuilt, including when no default exists.
        public bool DefaultEndpointChanged()
        {
            IMMDeviceEnumerator enumerator = null;
            IMMDevice device = null;
            try
            {
                enumerator = CreateEnumerator();
                Check(enumerator.GetDefaultAudioEndpoint(0, 0, out device));
                string id;
                Check(device.GetId(out id));
                bool changed = !String.Equals(id, EndpointId, StringComparison.Ordinal);
                if (changed) Log.Write("Default audio endpoint changed from " + EndpointId + " to " + id);
                return changed;
            }
            catch (COMException ex) { Log.Write("Default audio endpoint query failed (0x" + ex.ErrorCode.ToString("X8") + "): " + ex.Message); return true; }
            finally { Release(device); Release(enumerator); }
        }

        public void Dispose()
        {
            Thread join;
            Stream close;
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                stopping = true;
                join = worker;
                close = destination;
            }
            // NamedPipeServerStream.Dispose interrupts a synchronous blocked write.
            if (close != null) { try { close.Dispose(); } catch { } }
            if (join != null && join != Thread.CurrentThread) join.Join(1500);
        }

        private void Run()
        {
            IMMDeviceEnumerator enumerator = null;
            IMMDevice device = null;
            IAudioClient client = null;
            IAudioCaptureClient capture = null;
            IntPtr format = IntPtr.Zero;
            bool started = false;
            try
            {
                enumerator = CreateEnumerator();
                Check(enumerator.GetDefaultAudioEndpoint(0, 0, out device));
                string id;
                Check(device.GetId(out id));
                if (!String.Equals(id, EndpointId, StringComparison.Ordinal))
                    throw new IOException("The default audio output changed while capture was starting.");
                client = Activate(device);
                Check(client.GetMixFormat(out format));
                MixFormat mix = ReadFormat(format);
                if (mix.Rate != SampleRate || mix.Channels != Channels ||
                    mix.BlockAlign != blockAlign || mix.Format != FfmpegFormat)
                    throw new IOException("The audio output format changed; capture must restart.");
                Guid session = Guid.Empty;
                Check(client.Initialize(0, Loopback, 1000000, 0, format, ref session));
                Guid captureId = typeof(IAudioCaptureClient).GUID;
                object service;
                Check(client.GetService(ref captureId, out service));
                capture = (IAudioCaptureClient)service;

                long frequency;
                if (!QueryPerformanceFrequency(out frequency) || frequency <= 0)
                    throw new InvalidOperationException("Windows performance clock is unavailable.");
                long ticks;
                QueryPerformanceCounter(out ticks);
                double epoch = ticks * (10000000.0 / frequency);
                Check(client.Start());
                started = true;
                Queue<Packet> packets = new Queue<Packet>();
                byte[] silence = new byte[Math.Max(blockAlign, (SampleRate / 100) * blockAlign)];
                FillSilence(silence);
                long written = 0;
                long nextDeviceCheck = SampleRate;
                // Hold output 100 ms behind capture so packet jitter does not erase real sound.
                long cushion = SampleRate / 10;
                while (!stopping)
                {
                    uint frames;
                    Check(capture.GetNextPacketSize(out frames));
                    while (frames > 0 && !stopping)
                    {
                        IntPtr data;
                        uint flags;
                        ulong devicePosition, qpc;
                        Check(capture.GetBuffer(out data, out frames, out flags, out devicePosition, out qpc));
                        try
                        {
                            if (frames > SampleRate * 2)
                                throw new IOException("Unexpectedly large audio packet.");
                            int bytes = checked((int)frames * blockAlign);
                            byte[] copy = new byte[bytes];
                            if ((flags & Silent) != 0) FillSilence(copy);
                            if ((flags & Silent) == 0 && bytes > 0)
                            {
                                if (data == IntPtr.Zero) throw new IOException("Audio packet has no sample data.");
                                Marshal.Copy(data, copy, 0, bytes);
                            }
                            double packetClock;
                            if ((flags & TimestampError) == 0 && qpc != 0)
                                packetClock = qpc;
                            else
                            {
                                QueryPerformanceCounter(out ticks);
                                packetClock = ticks * (10000000.0 / frequency) - frames * (10000000.0 / SampleRate);
                            }
                            long start = (long)Math.Round((packetClock - epoch) * (SampleRate / 10000000.0));
                            // A negative start can occur for the first engine packet.
                            if (start + frames > written)
                                packets.Enqueue(new Packet { Start = start, Frames = (int)frames, Data = copy });
                            if (packets.Count > 1000)
                                throw new IOException("Audio output cannot keep up with capture.");
                        }
                        finally { Check(capture.ReleaseBuffer(frames)); }
                        Check(capture.GetNextPacketSize(out frames));
                    }

                    QueryPerformanceCounter(out ticks);
                    long elapsed = (long)((ticks * (10000000.0 / frequency) - epoch) * (SampleRate / 10000000.0));
                    long target = Math.Max(0, elapsed - cushion);
                    // Do not synthesize a huge burst after suspension or a blocked destination.
                    if (target - written > SampleRate * 2L)
                        throw new IOException("Audio capture fell behind the system clock; capture must restart.");
                    while (written < target && !stopping)
                    {
                        while (packets.Count > 0 && packets.Peek().Start + packets.Peek().Frames <= written)
                            packets.Dequeue();
                        Packet packet = packets.Count > 0 ? packets.Peek() : null;
                        if (packet == null || packet.Start > written)
                        {
                            long end = packet == null ? target : Math.Min(target, packet.Start);
                            int count = (int)Math.Min(end - written, silence.Length / blockAlign);
                            destination.Write(silence, 0, count * blockAlign);
                            written += count;
                        }
                        else
                        {
                            int offset = (int)(written - packet.Start);
                            int count = (int)Math.Min(target - written, packet.Frames - offset);
                            destination.Write(packet.Data, offset * blockAlign, count * blockAlign);
                            written += count;
                        }
                    }
                    if (elapsed >= nextDeviceCheck)
                    {
                        if (DefaultEndpointChanged())
                            throw new IOException("The default audio output changed; capture must restart.");
                        nextDeviceCheck = elapsed + SampleRate;
                    }
                    Thread.Sleep(5);
                }
            }
            catch (Exception error) { if (!stopping) failure = error; }
            finally
            {
                if (started && client != null) { try { client.Stop(); } catch { } }
                if (format != IntPtr.Zero) Marshal.FreeCoTaskMem(format);
                Release(capture);
                Release(client);
                Release(device);
                Release(enumerator);
            }
        }

        private sealed class Packet { public long Start; public int Frames; public byte[] Data; }
        private sealed class MixFormat { public int Rate, Channels, BlockAlign; public string Format; }

        private void FillSilence(byte[] samples)
        {
            // Unsigned 8-bit PCM uses 128 for zero amplitude; every other supported format uses zero.
            if (FfmpegFormat == "u8")
                for (int i = 0; i < samples.Length; i++) samples[i] = 128;
        }

        private static MixFormat ReadFormat(IntPtr pointer)
        {
            if (pointer == IntPtr.Zero) throw new IOException("Audio output returned no mix format.");
            int tag = (ushort)Marshal.ReadInt16(pointer, 0);
            int channels = (ushort)Marshal.ReadInt16(pointer, 2);
            int rate = Marshal.ReadInt32(pointer, 4);
            int align = (ushort)Marshal.ReadInt16(pointer, 12);
            int bits = (ushort)Marshal.ReadInt16(pointer, 14);
            if (tag == 0xFFFE)
            {
                if ((ushort)Marshal.ReadInt16(pointer, 16) < 22)
                    throw new IOException("Invalid extensible audio mix format.");
                Guid subformat = (Guid)Marshal.PtrToStructure(IntPtr.Add(pointer, 24), typeof(Guid));
                if (subformat == new Guid("00000001-0000-0010-8000-00aa00389b71")) tag = 1;
                else if (subformat == new Guid("00000003-0000-0010-8000-00aa00389b71")) tag = 3;
                else throw new NotSupportedException("The audio mix format is not PCM or IEEE float.");
            }
            string raw = null;
            if (tag == 3 && bits == 32) raw = "f32le";
            if (tag == 3 && bits == 64) raw = "f64le";
            if (tag == 1 && bits == 8) raw = "u8";
            if (tag == 1 && bits == 16) raw = "s16le";
            if (tag == 1 && bits == 24) raw = "s24le";
            if (tag == 1 && bits == 32) raw = "s32le";
            if (raw == null || channels < 1 || channels > 32 || rate < 8000 || rate > 384000 || align != channels * (bits / 8))
                throw new NotSupportedException("Unsupported audio output mix format.");
            return new MixFormat { Rate = rate, Channels = channels, BlockAlign = align, Format = raw };
        }

        private static IMMDeviceEnumerator CreateEnumerator()
        {
            return (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")));
        }
        private static IAudioClient Activate(IMMDevice device)
        {
            Guid id = typeof(IAudioClient).GUID;
            object client;
            Check(device.Activate(ref id, 23, IntPtr.Zero, out client));
            return (IAudioClient)client;
        }
        private static void Check(int result) { if (result < 0) Marshal.ThrowExceptionForHR(result); }
        private static void Release(object instance)
        {
            if (instance != null && Marshal.IsComObject(instance))
            { try { Marshal.ReleaseComObject(instance); } catch { } }
        }

        [DllImport("kernel32.dll")]
        private static extern bool QueryPerformanceCounter(out long value);
        [DllImport("kernel32.dll")]
        private static extern bool QueryPerformanceFrequency(out long value);

        // Method order and PreserveSig match the native COM vtables exactly.
        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int flow, uint mask, out IntPtr devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice endpoint);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
            [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr callback);
            [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr callback);
        }
        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid id, uint context, IntPtr activation, [MarshalAs(UnmanagedType.IUnknown)] out object result);
            [PreserveSig] int OpenPropertyStore(uint access, out IntPtr properties);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetState(out uint state);
        }
        [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioClient
        {
            [PreserveSig] int Initialize(int shareMode, uint flags, long duration, long periodicity, IntPtr format, ref Guid session);
            [PreserveSig] int GetBufferSize(out uint frames);
            [PreserveSig] int GetStreamLatency(out long latency);
            [PreserveSig] int GetCurrentPadding(out uint frames);
            [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
            [PreserveSig] int GetMixFormat(out IntPtr format);
            [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
            [PreserveSig] int Start();
            [PreserveSig] int Stop();
            [PreserveSig] int Reset();
            [PreserveSig] int SetEventHandle(IntPtr handle);
            [PreserveSig] int GetService(ref Guid id, [MarshalAs(UnmanagedType.IUnknown)] out object service);
        }
        [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioCaptureClient
        {
            [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
            [PreserveSig] int ReleaseBuffer(uint frames);
            [PreserveSig] int GetNextPacketSize(out uint frames);
        }
    }
}
