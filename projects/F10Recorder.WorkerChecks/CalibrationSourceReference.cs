using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace F10Recorder;

// Only the explicitly selected v10 fixture uses this render client. It is not a
// recorder/audio-clock replacement. No native call occurs in Signal/CheckBounds.
internal sealed class CalibrationSourceReference : IDisposable
{
    internal const int Rate = 48000, Block = 96000, Symbol = 2400;
    internal const string Policy = ReplayCalibrationCatalog.SameCaptureOracle;
    readonly string root;
    readonly long qualificationArmQpc;
    readonly Thread thread;
    readonly ManualResetEventSlim ready = new();
    readonly Stopwatch lifetime=Stopwatch.StartNew();
    readonly double initialCpu=ReadProcessCpu();
    static double ReadProcessCpu(){using var process=Process.GetCurrentProcess();return process.TotalProcessorTime.TotalMilliseconds;}
    volatile bool stopping;
    Exception error;
    internal string Endpoint { get; private set; }
    internal static float Signal(long sample)
    {
        int n = (int)(sample % Block), id = checked((int)(sample / Block));
        if (id is < 0 or > 511) throw new IOException("Reference marker lifetime exceeded.");
        // 150ms unique sync chirp followed by 16 Manchester-coded ID bits.
        // Each 50ms symbol has an independent phase; no one-second alias exists.
        double frequency;
        if (n < 7200) { double t = n / (double)Rate; return (float)(.045 * Math.Sin(2 * Math.PI * (600 * t + 4300 * t * t))); }
        int symbol = (n - 7200) / Symbol;
        if (symbol >= 32) return 0;
        int bit = (id >> (symbol / 2)) & 1;
        if ((symbol & 1) != 0) bit ^= 1;
        frequency = bit == 0 ? 900 : 1500;
        double within = (n - 7200) % Symbol / (double)Rate;
        double envelope = Math.Min(1, Math.Min(within * 1000, (.05 - within) * 1000));
        return (float)(.045 * envelope * Math.Sin(2 * Math.PI * frequency * within));
    }
    internal CalibrationSourceReference(string root,long qualificationArmQpc=0)
    {
        this.root = root;this.qualificationArmQpc=qualificationArmQpc;
        if(qualificationArmQpc<0)throw new IOException("Negative qualification arm");
        thread = new Thread(Render) { IsBackground = true, Name = "C00-source-render-reference" };
        thread.SetApartmentState(ApartmentState.MTA); thread.Start();
        try { if (!ready.Wait(5000)) throw new IOException("Reference render startup5s deadline."); Check(); }
        catch { stopping = true; if (!thread.Join(5000)) throw new IOException("Failed reference constructor has unconfirmed render closure."); ready.Dispose(); throw; }
    }
    internal void Check() { if (error != null) throw new IOException("Independent source reference failed.", error); }
    void Render()
    {
        IntPtr enumerator = IntPtr.Zero, device = IntPtr.Zero, client = IntPtr.Zero, render = IntPtr.Zero, clock = IntPtr.Zero, format = IntPtr.Zero;
        bool started = false, initialized = false, clockRunning=false; long submitted = 0; int rows = 0;
        var watch = Stopwatch.StartNew();
        try
        {
            int apartment = CoInitializeEx(IntPtr.Zero, 0); HR(apartment); initialized = true;
            Guid cls = new("BCDE0395-E52F-467C-8E3D-C4579291692E"), iid = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
            Marshal.ThrowExceptionForHR(CoCreateInstance(in cls, IntPtr.Zero, 23, in iid, out enumerator));
            HR(Method<DefaultEndpoint>(enumerator, 4)(enumerator, 0, 0, out device));
            HR(Method<DeviceId>(device, 5)(device, out var name));
            try { Endpoint = Marshal.PtrToStringUni(name); } finally { Marshal.FreeCoTaskMem(name); }
            iid = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
            HR(Method<Activate>(device, 3)(device, in iid, 23, IntPtr.Zero, out client));
            HR(Method<GetFormat>(client, 8)(client, out format));
            if (format == IntPtr.Zero) throw new IOException("Reference mix format is absent.");
            int tag = (ushort)Marshal.ReadInt16(format), channels = (ushort)Marshal.ReadInt16(format, 2), rate = Marshal.ReadInt32(format, 4), bits = (ushort)Marshal.ReadInt16(format, 14);
            if (tag == 0xFFFE && (ushort)Marshal.ReadInt16(format,16) >= 22 && Marshal.PtrToStructure<Guid>(format + 24) == new Guid("00000003-0000-0010-8000-00aa00389b71")) tag = 3;
            if (tag != 3 || channels != 2 || rate != Rate || bits != 32 || Marshal.ReadInt16(format,12) != 8 || Marshal.ReadInt32(format,8) != Rate * 8)
                throw new IOException("Reference requires unchanged stereo48k float mix; no device reconfiguration.");
            Guid session = Guid.NewGuid();
            HR(Method<Initialize>(client, 3)(client, 0, 0, 1_000_000, 0, format, in session));
            HR(Method<GetUInt>(client, 4)(client, out uint capacity));
            if (capacity is < 48 or > 9600) throw new IOException("Reference buffer exceeds200ms.");
            iid = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"); HR(Method<Service>(client, 14)(client, in iid, out render));
            iid = new("CD63314F-3FBA-4A1B-812C-EF96358728E7"); HR(Method<Service>(client, 14)(client, in iid, out clock));
            HR(Method<Frequency>(clock, 3)(clock, out ulong frequency));
            if (frequency == 0) throw new IOException("Missing independent render clock frequency.");
            using var log = new StreamWriter(new FileStream(Path.Combine(root, "source-render-clock.csv"), FileMode.CreateNew, FileAccess.Write, FileShare.Read));
            log.WriteLine("submitted_samples,position,frequency,qpc_100ns,before_qpc,after_qpc,hresult");
            void Fill(uint count)
            {
                if (count == 0) return;
                HR(Method<GetBuffer>(render, 3)(render, count, out var data));
                try { var values = new float[checked((int)count * 2)]; for (int n = 0; n < count; n++) values[2*n] = values[2*n+1] = Signal(submitted + n); Marshal.Copy(values, 0, data, values.Length); }
                finally { HR(Method<ReleaseBuffer>(render, 4)(render, count, 0)); }
                submitted += count;
            }
            Fill(capacity); HR(Method<Simple>(client, 10)(client)); started = true; watch.Restart();
            File.WriteAllText(Path.Combine(root, "source-reference-policy.json"), JsonSerializer.Serialize(new { Policy, Endpoint, Rate, channels,
                blockSamples = Block, symbolSamples = Symbol, syncSamples = 7200, maximumSeconds = 700, qpcFrequency = Stopwatch.Frequency,
                qualificationPhase=qualificationArmQpc>0?CalibrationReferencePhase.Identity:"legacy-at-Shown",qualificationArmQpc,
                meaning = "Independent render stream position/QPC plus compositor publication brackets; software source, not physical DAC/scanout qualification." }));
            while (!stopping)
            {
                if (watch.Elapsed.TotalSeconds > 700 || ++rows > 140000) throw new IOException("Finite reference lifetime/row bound.");
                long before = Stopwatch.GetTimestamp(); int result = Method<Position>(clock, 4)(clock, out ulong position, out ulong qpc); long after = Stopwatch.GetTimestamp();
                // S_FALSE is degraded data, never an acceptable reference sample.
                if(result<0)HR(result);
                if(position>0 && (result != 0 || qpc == 0 || Stopwatch.GetElapsedTime(before, after).TotalMilliseconds > 2
                    || qpc < before*(1e7/Stopwatch.Frequency)-1 || qpc > after*(1e7/Stopwatch.Frequency)+1)
                    )throw new IOException("Degraded/stale/uncertain render clock.");
                log.WriteLine(FormattableString.Invariant($"{submitted},{position},{frequency},{qpc},{before},{after},{result}"));
                if(position>0){clockRunning=true;ready.Set();}
                else if(clockRunning||watch.Elapsed.TotalSeconds>.2)throw new IOException("Render clock reset/leading zero-position prefix exceeds200ms; no reset/retry.");
                HR(Method<GetUInt>(client, 6)(client, out uint padding));
                if (padding == 0) throw new IOException("Reference render underrun; stop without reset/retry.");
                if (padding > capacity) throw new IOException("Reference padding changed.");
                Fill(capacity - padding);
                if (rows % 200 == 0) log.Flush();
                Thread.Sleep(5);
            }
            log.Flush();
        }
        catch (Exception ex) { error = ex; ready.Set(); }
        finally
        {
            if (started) { try { HR(Method<Simple>(client, 11)(client)); } catch (Exception ex) { error ??= ex; } }
            if (format != IntPtr.Zero) Marshal.FreeCoTaskMem(format);
            foreach (var value in new[] { clock, render, client, device, enumerator }) if (value != IntPtr.Zero) Marshal.Release(value);
            if (initialized) CoUninitialize();
        }
    }
    public void Dispose()
    {
        stopping = true;
        if (!thread.Join(5000)) throw new IOException("Reference render closure unconfirmed.");
        using(var process=Process.GetCurrentProcess())
            File.WriteAllText(Path.Combine(root,"source-reference-costs.json"),JsonSerializer.Serialize(new {
                renderThreadAbsent=true,seconds=lifetime.Elapsed.TotalSeconds,processCpuMilliseconds=process.TotalProcessorTime.TotalMilliseconds-initialCpu,
                processPeakWorkingSetBytes=process.PeakWorkingSet64,clockCsvBytes=new FileInfo(Path.Combine(root,"source-render-clock.csv")).Length,
                error=error?.ToString(),meaning=qualificationArmQpc>0?"Fixture process CPU delta since qualified reference construction;whole-process peak includes pre-arm. Full fixture costs/phase coverage in source-pre-admission.json;GPU unmeasured,not zero.":"Actual entire fixture-process CPU delta/peak includes GDI and reference renderer. No isolated-cost subtraction or recorder performance claim."}));
        ready.Dispose(); Check();
    }
    static void HR(int value) => Marshal.ThrowExceptionForHR(value);
    static T Method<T>(IntPtr self, int slot) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(self), slot * IntPtr.Size));
    [DllImport("ole32.dll")] static extern int CoCreateInstance(in Guid cls, IntPtr outer, uint context, in Guid iid, out IntPtr value);
    [DllImport("ole32.dll")] static extern int CoInitializeEx(IntPtr reserved, uint apartment);
    [DllImport("ole32.dll")] static extern void CoUninitialize();
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int DefaultEndpoint(IntPtr self, int flow, int role, out IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int DeviceId(IntPtr self, out IntPtr id);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int Activate(IntPtr self, in Guid iid, uint context, IntPtr activation, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int Initialize(IntPtr self, int mode, uint flags, long duration, long periodicity, IntPtr format, in Guid session);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetFormat(IntPtr self, out IntPtr format);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int Service(IntPtr self, in Guid iid, out IntPtr service);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetUInt(IntPtr self, out uint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int Frequency(IntPtr self, out ulong value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int Position(IntPtr self, out ulong position, out ulong qpc);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int Simple(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetBuffer(IntPtr self, uint frames, out IntPtr data);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int ReleaseBuffer(IntPtr self, uint frames, uint flags);
}
