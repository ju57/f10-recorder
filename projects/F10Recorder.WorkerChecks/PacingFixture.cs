using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace F10Recorder;

internal static class PacingFixture
{
    internal static void Run(string data, int fps, string mode)
    {
        Forms.Application.EnableVisualStyles();
        using var window = new Pattern(data, fps, mode);
        if (mode == "paced") window.RunPaced(); else Forms.Application.Run(window);
    }
    sealed class Pattern : Forms.Form
    {
        readonly string data;
        readonly int fps;
        readonly bool paced;
        readonly Forms.Timer timer;
        readonly StreamWriter trace;
        readonly Drawing.Font font = new("Segoe UI", 24);
        readonly Drawing.Brush[] colors = [Drawing.Brushes.DarkBlue, Drawing.Brushes.DarkRed, Drawing.Brushes.DarkGreen, Drawing.Brushes.DarkGoldenrod];
        long frame, next;
        bool periodSet;
        internal Pattern(string directory, int rate, string mode)
        {
            data = directory; fps = rate; paced = mode == "paced";
            Directory.CreateDirectory(data);
            trace = new StreamWriter(Path.Combine(data, "render.csv")); trace.WriteLine("id,paint_begin_qpc,compositor_flush_qpc");
            Text = "F10 frame pacing controlled source"; FormBorderStyle = Forms.FormBorderStyle.None;
            StartPosition = Forms.FormStartPosition.Manual; Bounds = Forms.Screen.PrimaryScreen.Bounds; TopMost = true; DoubleBuffered = true;
            KeyPreview = true; KeyDown += (_, e) => { if (e.KeyCode == Forms.Keys.Escape) Close(); };
            if (paced) { periodSet = TimeBeginPeriod(1) == 0; if (!periodSet) throw new IOException("Fixture timer-resolution request failed."); }
            timer = new Forms.Timer { Interval = 16 };
            timer.Tick += (_, _) => Pulse(Stopwatch.GetTimestamp());
            Shown += (_, _) =>
            {
                next = Stopwatch.GetTimestamp(); if (!paced) timer.Start();
                File.WriteAllText(Path.Combine(data, "pacing-ready.json"), JsonSerializer.Serialize(new { width = Bounds.Width, height = Bounds.Height, pid = Environment.ProcessId, fps, mode, qpc_frequency = Stopwatch.Frequency, timer_resolution_requested = periodSet, scheduler = paced ? "high-resolution waitable timer and QPC deadlines" : "WinForms 16ms timer" }));
            };
        }
        void Pulse(long begin)
        {
            frame++; Refresh();
            int result = DwmFlush(); if (result != 0) throw new IOException("Compositor flush failed: " + result);
            trace.WriteLine($"{frame},{begin},{Stopwatch.GetTimestamp()}");
            if (frame % fps == 0) trace.Flush();
        }
        internal void RunPaced()
        {
            IntPtr wait = CreateWaitableTimer(IntPtr.Zero, null, 2, 0x1F0003);
            if (wait == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
            try
            {
                Show(); Activate();
                long interval = Stopwatch.Frequency / fps;
                while (!IsDisposed && Visible)
                {
                    Forms.Application.DoEvents(); if (IsDisposed || !Visible) break;
                    long remaining = next - Stopwatch.GetTimestamp();
                    if (remaining > 0)
                    {
                        long due = -Math.Max(1, remaining * 10000000 / Stopwatch.Frequency);
                        if (!SetWaitableTimer(wait, ref due, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0) || WaitForSingleObject(wait, 1000) != 0) throw new System.ComponentModel.Win32Exception();
                    }
                    Pulse(Stopwatch.GetTimestamp()); next += interval;
                    if (next < Stopwatch.GetTimestamp() - interval) next = Stopwatch.GetTimestamp() + interval;
                }
            }
            finally { CloseHandle(wait); }
        }
        protected override void OnPaint(Forms.PaintEventArgs e)
        {
            base.OnPaint(e);
            // Full-screen spatial motion plus a loss-resistant 24-bit frame ID,
            // checksum byte and inverted second barcode row.
            for (int y = 0; y < ClientSize.Height; y += 64)
                for (int x = -64; x < ClientSize.Width; x += 64)
                    e.Graphics.FillRectangle(colors[((x + 64) / 64 + y / 64 + frame / 7) % colors.Length], x + (int)(frame * 11 % 64), y, 64, 64);
            uint id = (uint)frame & 0xFFFFFF;
            uint checksum = ((id & 255) ^ ((id >> 8) & 255) ^ ((id >> 16) & 255) ^ 0xA7) & 255;
            uint word = id | (checksum << 24);
            for (int bit = 0; bit < 32; bit++)
            {
                bool on = (word & (1u << bit)) != 0;
                e.Graphics.FillRectangle(on ? Drawing.Brushes.White : Drawing.Brushes.Black, 32 + bit * 24, 32, 24, 24);
                e.Graphics.FillRectangle(on ? Drawing.Brushes.Black : Drawing.Brushes.White, 32 + bit * 24, 64, 24, 24);
            }
            e.Graphics.FillRectangle(Drawing.Brushes.Black, 24, 112, 1000, 96);
            e.Graphics.DrawString($"Controlled frame source · {fps} FPS · frame {frame}", font, Drawing.Brushes.White, 32, 128);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                timer?.Dispose(); trace?.Dispose(); font?.Dispose();
                if (periodSet) { TimeEndPeriod(1); periodSet = false; }
            }
            base.Dispose(disposing);
        }
    }
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")] static extern uint TimeBeginPeriod(uint period);
    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")] static extern uint TimeEndPeriod(uint period);
    [DllImport("dwmapi.dll")] static extern int DwmFlush();
    [DllImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", SetLastError = true)] static extern IntPtr CreateWaitableTimer(IntPtr attributes, string name, uint flags, uint access);
    [DllImport("kernel32.dll", EntryPoint = "SetWaitableTimerEx", SetLastError = true)] static extern bool SetWaitableTimer(IntPtr timer, ref long due, int period, IntPtr callback, IntPtr argument, IntPtr wake, uint tolerance);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
}
