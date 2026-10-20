using System.Diagnostics;
using System.Text.Json;
using Forms = System.Windows.Forms;

namespace F10Recorder;

// Verification-only window; no fixture or test capture switches ship in the app.
internal static class DesktopFixture
{
    internal static void Run(string data)
    {
        Forms.Application.EnableVisualStyles();
        var screen = Forms.Screen.PrimaryScreen.Bounds;
        using var form = new FixtureWindow { Text = "F10 worker controlled desktop acceptance", FormBorderStyle = Forms.FormBorderStyle.None, StartPosition = Forms.FormStartPosition.Manual, Bounds = screen, TopMost = true };
        form.Shown += (_, _) => File.WriteAllText(Path.Combine(data, "fixture-ready.json"), JsonSerializer.Serialize(new { width = screen.Width, height = screen.Height, window = form.Handle.ToInt64(), pid = Environment.ProcessId, requestedTimerIntervalMilliseconds = 16 }));
        Forms.Application.Run(form);
    }
    sealed class FixtureWindow : Forms.Form
    {
        readonly Stopwatch elapsed = Stopwatch.StartNew();
        readonly Forms.Timer timer = new() { Interval = 16 };
        internal FixtureWindow()
        {
            DoubleBuffered = true; BackColor = System.Drawing.Color.FromArgb(23, 27, 25);
            timer.Tick += (_, _) => Invalidate(); timer.Start();
        }
        protected override void OnPaint(Forms.PaintEventArgs e)
        {
            base.OnPaint(e);
            using var font = new System.Drawing.Font("Segoe UI", 28);
            using var brush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(83, 197, 139));
            e.Graphics.DrawString("F10 Recorder · controlled acceptance", font, brush, 60, 80);
            e.Graphics.DrawString(elapsed.Elapsed.ToString(@"hh\:mm\:ss\.fff"), font, brush, 60, 140);
            int span = Math.Max(1, ClientSize.Width - 160);
            int x = 40 + (int)((elapsed.Elapsed.TotalSeconds * 160) % span);
            e.Graphics.FillRectangle(brush, x, ClientSize.Height / 2, 100, 100);
        }
        protected override void Dispose(bool disposing) { if (disposing) timer.Dispose(); base.Dispose(disposing); }
    }
}
