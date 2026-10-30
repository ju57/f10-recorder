using System.Diagnostics;
using System.Media;
using System.Text.Json;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace F10Recorder;

internal static class CalibrationPattern
{
    internal static int Run(string root, string label)
    {
        if (!new[] { "T01", "T02", "T03", "H02", "H01", "H03" }.Contains(label)) return 2;
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
            using var form = new Pattern(root, label);
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
        readonly Forms.Timer timer = new() { Interval = 16 };
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly Drawing.Bitmap texture;
        readonly string root, label;
        readonly int seed;
        readonly SoundPlayer sound;
        readonly MemoryStream wave;
        int frame;
        internal Pattern(string root, string label)
        {
            this.root = root; this.label = label;
            FormBorderStyle = Forms.FormBorderStyle.None; StartPosition = Forms.FormStartPosition.Manual;
            Bounds = Forms.Screen.PrimaryScreen.Bounds; TopMost = true; DoubleBuffered = true;
            Text = "F10 bounded storage calibration " + label;
            seed = label.StartsWith('T') ? 13579 : 24680;
            texture = new Drawing.Bitmap(320, 180);
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
            timer.Tick += (_, _) => { if (clock.Elapsed.TotalSeconds > 350) Close(); else { frame++; Invalidate(); } };
            Shown += (_, _) =>
            {
                Activate(); sound.PlayLooping(); timer.Start();
                CalibrationFixturePublication.Publish(root, new
                { pid = Environment.ProcessId, hwnd = Handle.ToInt64(), width = Bounds.Width, height = Bounds.Height, label, seed,
                    recipe = "GDI texture320x180-nearest-scale; static/translation/cut-every15frames; chirp48k-stereo16bit-1200peak", startedUtc = DateTime.UtcNow });
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
            int id = seed * 256 + (mode == 1 ? 0 : frame);
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
            if (disposing) { timer.Stop(); sound.Stop(); sound.Dispose(); wave.Dispose(); texture.Dispose(); timer.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
