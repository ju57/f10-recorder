using System.Diagnostics;
using System.Media;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Text;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace F10Recorder;

internal static class CalibrationPattern
{
    // The disarmed operator wait precedes the acquisition clock. The fixture's
    // own watchdog must cover both, including bounded publication/startup work.
    internal const int ManualLifetimeSeconds = CalibrationForeground.UserHandoffSeconds + 330 + 15;
    internal const int TimerIntervalMilliseconds = 16, SourceIdRange = 25_000;
    internal const int ReferenceSourceIdRange = 40_000;
    internal static bool LifetimeExpired(double seconds, int lifetimeSeconds) => seconds > lifetimeSeconds;
    internal static int MaximumAnimatedFrame(int lifetimeSeconds)
    {
        if (lifetimeSeconds is <= 0 or > 4800) throw new IOException("Fixture lifetime exceeds its finite source contract.");
        return checked((lifetimeSeconds * 1000 + TimerIntervalMilliseconds - 1) / TimerIntervalMilliseconds);
    }
    internal static int BarcodeFrameDivisor(int lifetimeSeconds)
        => checked((MaximumAnimatedFrame(lifetimeSeconds) + SourceIdRange) / SourceIdRange);
    internal static int BarcodeOffset(int frame, int lifetimeSeconds)
    {
        if (frame < 0 || frame > MaximumAnimatedFrame(lifetimeSeconds)) throw new IOException("Fixture frame exceeds its source contract.");
        return frame / BarcodeFrameDivisor(lifetimeSeconds);
    }
    internal static bool ValidLabel(string label) => label is "T01" or "T02" or "T03" or "H02" or "H01" or "H03";
    internal static int Run(string root, string label, bool manualHandoff = false, int? lifetimeSeconds = null, bool sourceReference = false,PresentationPlanBinding presentation=null)
    {
        if (!ValidLabel(label)) return 2;
        Forms.Application.SetUnhandledExceptionMode(Forms.UnhandledExceptionMode.ThrowException);
        CalibrationWakeLease wake = null;
        using var display = new CalibrationDesktop();
        try
        {
            CalibrationDesktop.RequireLocalDesktop();
            File.WriteAllText(Path.Combine(root, "fixture-desktop-before.json"), JsonSerializer.Serialize(display.Snapshot()));
            wake = new CalibrationWakeLease();
            display.AwaitDisplayOn();
            File.WriteAllText(Path.Combine(root, "fixture-desktop-admitted.json"), JsonSerializer.Serialize(display.Snapshot()));
            using var form = new Pattern(root, label, lifetimeSeconds ?? (manualHandoff ? ManualLifetimeSeconds : 350), activate: !manualHandoff, sourceReference,presentation);
            Forms.Application.Run(form);
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(root, "fixture-failure.json"), JsonSerializer.Serialize(new { error = ex.ToString(), desktop = display.Snapshot() }));
            return 1;
        }
        finally
        {
            wake?.Dispose();
            File.WriteAllText(Path.Combine(root, "fixture-display-exit.json"), JsonSerializer.Serialize(new
            { desktop = display.Snapshot(), previousExecutionState = wake?.Previous, restored = wake?.Restored }));
        }
    }

    sealed class Pattern : Forms.Form
    {
        readonly Forms.Timer timer = new() { Interval = TimerIntervalMilliseconds };
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly Drawing.Bitmap texture;
        readonly string root, label;
        readonly int seed, lifetimeSeconds, maximumAnimatedFrame;
        readonly SoundPlayer sound;
        readonly MemoryStream wave;
        readonly bool sourceReference;
        CalibrationSourceReference reference;
        CalibrationPresentationReference presentation;
        StreamWriter paints;
        StreamWriter prePaints;
        CalibrationReferencePhase.TextBudget preBudget;
        readonly bool nativeReference;
        long armQpc,preRows;double preMaximumSeconds;double? armCpu;
        int frame;
        internal Pattern(string root, string label, int lifetimeSeconds, bool activate, bool sourceReference,PresentationPlanBinding presentationBinding)
        {
            if(presentationBinding!=null)PresentationMethod.ValidateFixture(label,sourceReference,lifetimeSeconds);
            this.root = root; this.label = label;
            this.sourceReference = sourceReference;
            nativeReference=sourceReference&&presentationBinding!=null;
            this.lifetimeSeconds = lifetimeSeconds;
            maximumAnimatedFrame = MaximumAnimatedFrame(lifetimeSeconds);
            FormBorderStyle = Forms.FormBorderStyle.None; StartPosition = Forms.FormStartPosition.Manual;
            Bounds = Forms.Screen.PrimaryScreen.Bounds; TopMost = true; DoubleBuffered = true;
            Text = "F10 bounded storage calibration " + label;
            seed = label.StartsWith('T') ? 13579 : 24680;
            // The opaque v6 H03 texture painted faster in this format while
            // producing identical pixels through the existing scaled draws.
            texture = presentationBinding?.Spec.AllowsAdjacentTransition == true
                ? new Drawing.Bitmap(320, 180, Drawing.Imaging.PixelFormat.Format32bppPArgb)
                : new Drawing.Bitmap(320, 180);
            var random = new Random(seed);
            for (int y = 0; y < texture.Height; y++) for (int x = 0; x < texture.Width; x++)
                texture.SetPixel(x, y, Drawing.Color.FromArgb(random.Next(256), random.Next(256), random.Next(256)));
            wave = new MemoryStream();
            using (var writer = new BinaryWriter(wave, System.Text.Encoding.UTF8, true))
            {
                writer.Write("RIFF"u8); writer.Write(36 + 48000 * 4); writer.Write("WAVEfmt "u8);
                writer.Write(16); writer.Write((short)1); writer.Write((short)2); writer.Write(48000); writer.Write(192000);
                writer.Write((short)4); writer.Write((short)16); writer.Write("data"u8); writer.Write(48000 * 4);
                for (int n = 0; n < 48000; n++)
                {
                    // Quiet one-second repeating chirp through the real output endpoint.
                    double t = n / 48000.0, envelope = Math.Min(1, Math.Min(t * 100, (1 - t) * 100));
                    short value = (short)(1200 * envelope * Math.Sin(2 * Math.PI * (220 * t + 110 * t * t)));
                    writer.Write(value); writer.Write(value);
                }
            }
            wave.Position = 0; sound = new SoundPlayer(wave);
            timer.Tick += (_, _) => {
                if (LifetimeExpired(clock.Elapsed.TotalSeconds, lifetimeSeconds) || frame >= maximumAnimatedFrame) Close();
                else if (sourceReference)
                {
                    if(presentationBinding!=null)
                    {
                        presentation??=CalibrationPresentationIntegration.TryStart(root,presentationBinding,Environment.ProcessId,Handle,r=>{
                            armQpc=Stopwatch.GetTimestamp();if(armQpc<r.RequestedQpc)throw new IOException("Future qualification request");
                            using(var process=Process.GetCurrentProcess()){double measured=process.TotalProcessorTime.TotalMilliseconds;if(!double.IsFinite(measured)||measured<0)throw new IOException("Unknown pre-arm fixture CPU");armCpu=measured;}
                            reference=new CalibrationSourceReference(root,armQpc);
                            paints=new StreamWriter(new FileStream(Path.Combine(root,"source-paints.csv"),FileMode.CreateNew,FileAccess.Write,FileShare.Read));
                            paints.WriteLine("id,before_qpc,after_qpc");prePaints.Flush();
                        });
                        presentation?.Check();
                    }
                    reference?.Check(); frame++;
                    long before = Stopwatch.GetTimestamp(); Refresh();
                    Marshal.ThrowExceptionForHR(DwmFlush()); long after = Stopwatch.GetTimestamp();
                    bool qualified=!nativeReference||reference!=null;
                    var output=qualified?paints:prePaints;
                    CalibrationReferencePhase.WritePaint(output,seed*256+frame,before,after,Stopwatch.Frequency,qualified,qualified?null:preBudget);
                    if(!qualified){preRows++;preMaximumSeconds=Math.Max(preMaximumSeconds,(after-before)/(double)Stopwatch.Frequency);
                        if(preRows>40000)throw new IOException("Pre-admission diagnostic row bound");}
                    if (frame % 60 == 0) output.Flush();
                }
                else { frame++; Invalidate(); }
            };
            Shown += (_, _) =>
            {
                if (activate) Activate();
                if (sourceReference)
                {
                    if(nativeReference){prePaints=new StreamWriter(new FileStream(Path.Combine(root,"source-pre-admission.csv"),FileMode.CreateNew,FileAccess.Write,FileShare.Read),new UTF8Encoding(false));
                        preBudget=new(CalibrationReferencePhase.PreAdmissionMaximumBytes);preBudget.Write(prePaints,"id,before_qpc,after_qpc"+prePaints.NewLine);}
                    else {paints = new StreamWriter(new FileStream(Path.Combine(root, "source-paints.csv"), FileMode.CreateNew, FileAccess.Write, FileShare.Read));
                        paints.WriteLine("id,before_qpc,after_qpc"); reference = new CalibrationSourceReference(root);}
                }
                else sound.PlayLooping();
                timer.Start();
                CalibrationFixturePublication.Publish(root, new
                { pid = Environment.ProcessId, hwnd = Handle.ToInt64(), width = Bounds.Width, height = Bounds.Height, label, seed, lifetimeSeconds,
                    maximumAnimatedFrame, barcodeFrameDivisor = sourceReference ? 1 : BarcodeFrameDivisor(lifetimeSeconds),
                    sourceIdRange = sourceReference ? ReferenceSourceIdRange : SourceIdRange, timerIntervalMilliseconds = TimerIntervalMilliseconds,
                    recipe = sourceReference ? CalibrationSourceReference.Policy : "GDI texture320x180-nearest-scale; static/translation/cut-every15frames; barcode=frame/divisor-bounded25000; chirp48k-stereo16bit-1200peak", startedUtc = DateTime.UtcNow });
            };
        }
        protected override void OnPaint(Forms.PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.InterpolationMode = Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            int mode = label[^1] - '0';
            int shift = mode == 1 ? 0 : mode == 2 ? frame * 7 % Width : (frame / 15 * 173) % Width;
            e.Graphics.DrawImage(texture, new Drawing.Rectangle(shift - Width, 0, Width, Height));
            e.Graphics.DrawImage(texture, new Drawing.Rectangle(shift, 0, Width, Height));
            e.Graphics.FillRectangle(Drawing.Brushes.Black, 0, 0, 850, 96);
            int id = seed * 256 + (mode == 1 ? 0 : sourceReference ? frame : BarcodeOffset(frame, lifetimeSeconds));
            for (int bit = 0; bit < 24; bit++)
            {
                bool white = (id & (1 << bit)) != 0;
                e.Graphics.FillRectangle(white ? Drawing.Brushes.White : Drawing.Brushes.Black, 16 + bit * 24, 8, 24, 24);
                e.Graphics.FillRectangle(white ? Drawing.Brushes.Black : Drawing.Brushes.White, 16 + bit * 24, 32, 24, 24);
            }
            e.Graphics.DrawString("F10 calibration " + label + "  frame " + id, Forms.SystemInformation.MenuFont, Drawing.Brushes.White, 16, 64);
        }
        protected override void Dispose(bool disposing)
        {
            try
            {
                if (disposing)
                {
                    timer.Stop();
                    try { try {presentation?.Dispose();}finally{reference?.Dispose();} }
                    finally {
                        paints?.Dispose();prePaints?.Dispose();sound.Stop();sound.Dispose();wave.Dispose();texture.Dispose();timer.Dispose();
                        if(nativeReference)
                        {
                            using var process=Process.GetCurrentProcess();double cpu=process.TotalProcessorTime.TotalMilliseconds;
                            if(!double.IsFinite(cpu)||cpu<0||armCpu.HasValue&&cpu<armCpu.Value||process.PeakWorkingSet64<=0)throw new IOException("Unknown whole-fixture CPU coverage");
                            // Diagnostic only, read after fixture closure: no readiness
                            // signal or rename that a sampler-held leaf could block.
                            File.WriteAllBytes(Path.Combine(root,"source-pre-admission.json"),CalibrationReferencePhase.DiagnosticSummaryBytes(new{qualified=false,preRows,preMaximumSeconds,armQpc,
                                preAdmissionCsvBytes=preBudget?.Bytes,qualificationArmAttempted=armQpc>0,phaseCpuBoundaryMeasured=armCpu.HasValue,audioReferenceReady=reference!=null,nativeObserverCreated=presentation!=null,
                                processCpuTotalMilliseconds=cpu,preArmProcessCpuMilliseconds=armCpu??cpu,armedAttemptProcessCpuMilliseconds=armCpu.HasValue?cpu-armCpu.Value:(double?)null,
                                processPeakWorkingSetBytes=process.PeakWorkingSet64,seconds=clock.Elapsed.TotalSeconds,
                                coverage="CPU from process birth through final Form.Dispose observation,including pre-arm/GDI/audio/native/cleanup;post-observation exit tail unmeasured,not zero. Peak whole process as observed;GPU unmeasured. No isolated-cost subtraction."}));
                        }
                    }
                }
            }
            finally { base.Dispose(disposing); }
        }
    }
    [DllImport("dwmapi.dll")] static extern int DwmFlush();
}
