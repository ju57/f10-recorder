using System;
using System.Drawing;
using System.Windows.Forms;

namespace F10Recorder
{


    internal sealed class NotificationForm : Form
    {
        readonly Timer dismiss = new Timer();
        readonly Font brandFont = UiTheme.Font(8, true), titleFont = UiTheme.Font(12, true), detailFont = UiTheme.Font(9), symbolFont = UiTheme.Font(15, true);
        string title = "", detail = "";
        NotificationKind kind;
        internal string NotificationTitle { get { return title; } }
        internal string NotificationDetail { get { return detail; } }
        internal bool IsPresented { get { return !IsDisposed && IsHandleCreated && Native.IsWindowVisible(Handle); } }
        internal event Action Changed;

        internal NotificationForm()
        {
            Text = "F10 Recorder Notification"; FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual; BackColor = UiTheme.Surface; DoubleBuffered = true; AutoScaleMode = AutoScaleMode.None;
            Opacity = 0.98; Size = new Size(440, 132);
            dismiss.Tick += delegate { Dismiss(); };
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { var parameters = base.CreateParams; parameters.Style &= ~0x10000000; parameters.ExStyle |= 0x08000000 | 0x00000080 | 0x00000020 | 8; return parameters; } // Create hidden; NOACTIVATE, TOOLWINDOW, TRANSPARENT, TOPMOST; Opacity adds LAYERED.
        }
        internal void Present(string heading, string message, NotificationKind appearance, int milliseconds, Screen screen, Rectangle? overlayBounds = null)
        {
            dismiss.Stop(); title = heading; detail = message; kind = appearance;
            AccessibleName = heading; AccessibleDescription = message;
            Reposition(screen, overlayBounds);
            // WinForms' TopMost setter activates during handle creation. Set the native
            // style directly, and show exclusively through SWP_NOACTIVATE.
            if (!IsHandleCreated) { var window = Handle; Native.ShowWindow(window, 0); }
            Native.SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x53); // Topmost, no activation/move/resize.
            Invalidate();
            if (milliseconds > 0) { dismiss.Interval = milliseconds; dismiss.Start(); }
            var callback = Changed; if (callback != null) callback();
        }
        internal void Reposition(Screen screen, Rectangle? overlayBounds = null)
        {
            Rectangle area = screen.Bounds; Width = Math.Min(440, Math.Max(180, area.Width - 48));
            int left = area.Right - Width - 24;
            if (overlayBounds.HasValue) left = Math.Max(area.Left + 24, overlayBounds.Value.Left - Width - 16);
            Location = new Point(left, area.Top + 24);
        }
        internal void Dismiss() { dismiss.Stop(); if (IsHandleCreated) Native.ShowWindow(Handle, 0); var callback = Changed; if (callback != null) callback(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); Color accent = kind == NotificationKind.Warning ? UiTheme.Warning : UiTheme.Accent;
            using (var pen = new Pen(UiTheme.Border)) e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            using (var brush = new SolidBrush(accent)) { e.Graphics.FillRectangle(brush, 0, 0, 3, Height); e.Graphics.FillEllipse(brush, 18, 44, 32, 32); }
            TextRenderer.DrawText(e.Graphics, "F10 RECORDER", brandFont, new Rectangle(18, 14, Width - 36, 18), UiTheme.Muted, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(e.Graphics, kind == NotificationKind.Warning ? "!" : kind == NotificationKind.Success ? "✓" : "•", symbolFont, new Rectangle(18, 43, 32, 34), UiTheme.Background, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(e.Graphics, title, titleFont, new Rectangle(64, 41, Width - 84, 24), UiTheme.Text, TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(e.Graphics, detail, detailFont, new Rectangle(64, 73, Width - 84, 44), UiTheme.Muted, TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }
        protected override void Dispose(bool disposing) { if (disposing) { dismiss.Dispose(); brandFont.Dispose(); titleFont.Dispose(); detailFont.Dispose(); symbolFont.Dispose(); } base.Dispose(disposing); }
    }

}
