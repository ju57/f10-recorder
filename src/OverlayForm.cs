using System;
using System.Drawing;
using System.Windows.Forms;

namespace F10Recorder
{
    // A compositor overlay for desktop and borderless fullscreen applications.
    // The recorder remains independent of the foreground application's process.
    internal sealed class OverlayForm : Form
    {
        sealed class Backdrop : Form { protected override bool ShowWithoutActivation { get { return true; } } }
        readonly Form backdrop = new Backdrop();
        readonly SettingsForm settings;
        readonly Control settingsContent;
        readonly IntPtr previousWindow;
        readonly Rectangle previousBounds;
        readonly Size previousMinimum;
        readonly bool previouslyVisible;
        bool released, settingsClosed;
        internal event Action Dismissed;

        internal OverlayForm(SettingsForm form, string shortcut, Action saveReplay, Action screenshot = null)
        {
            previousWindow = Native.GetForegroundWindow(); settings = form; settingsContent = form.SettingsContent;
            previousBounds = form.Bounds; previousMinimum = form.MinimumSize; previouslyVisible = form.Visible;
            var screen = Screen.FromHandle(previousWindow); Rectangle area = screen.Bounds;
            Text = "F10 Recorder Overlay"; FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
            StartPosition = FormStartPosition.Manual; BackColor = UiTheme.Background; KeyPreview = true; AutoScaleMode = AutoScaleMode.None;
            int width = Math.Min(780, area.Width - 32), height = Math.Min(948, area.Height - 32);
            Bounds = new Rectangle(area.Right - width - 16, area.Top + (area.Height - height) / 2, width, height);
            backdrop.Text = "F10 Recorder Overlay Backdrop"; backdrop.FormBorderStyle = FormBorderStyle.None; backdrop.ShowInTaskbar = false; backdrop.TopMost = true;
            backdrop.StartPosition = FormStartPosition.Manual; backdrop.Bounds = area; backdrop.BackColor = Color.Black; backdrop.Opacity = 0.48;
            backdrop.MouseDown += delegate { Dismiss(); };

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0), Padding = new Padding(0) };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); Controls.Add(root);
            var bar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, BackColor = UiTheme.Surface, Padding = new Padding(16, 6, 8, 6), Margin = new Padding(0) };
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120)); bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 126)); bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));
            var hint = UiTheme.Label(shortcut + " / Esc to return", 9, false, true); hint.Anchor = AnchorStyles.Left; bar.Controls.Add(hint, 0, 0);
            var shot = UiTheme.Button("Screenshot"); shot.Name = "OverlayScreenshot"; shot.Enabled = screenshot != null; shot.Dock = DockStyle.Fill; shot.Margin = new Padding(0, 0, 8, 0); shot.Click += delegate { if (screenshot != null) screenshot(); }; bar.Controls.Add(shot, 1, 0);
            var save = UiTheme.Button("Save replay", true); save.Name = "OverlaySaveReplay"; save.Dock = DockStyle.Fill; save.Margin = new Padding(0, 0, 8, 0); save.Click += delegate { saveReplay(); }; bar.Controls.Add(save, 2, 0);
            var close = UiTheme.Button("×"); close.Name = "OverlayClose"; close.AccessibleName = "Close overlay and return to application"; close.Dock = DockStyle.Fill; close.FlatAppearance.BorderSize = 0; close.Click += delegate { Dismiss(); }; bar.Controls.Add(close, 3, 0); root.Controls.Add(bar, 0, 0);

            settings.Hide(); settings.FormClosed += SettingsClosed; root.Controls.Add(settingsContent, 0, 1); settings.PrepareOverlay();
        }
        internal void ShowForUser()
        {
            if (!Visible) { backdrop.Show(); Show(backdrop); } Native.ShowWindow(Handle, 9); BringToFront(); Activate(); Native.SetForegroundWindow(Handle);
        }
        void SettingsClosed(object sender, FormClosedEventArgs e) { settingsClosed = true; Dismiss(); }
        internal void Dismiss() { if (!released) { Log.Write("Overlay closing"); Close(); } }
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData) { if (keyData == Keys.Escape) { Dismiss(); return true; } return base.ProcessCmdKey(ref msg, keyData); }
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            ReleaseSettings(); base.OnFormClosed(e); var callback = Dismissed; if (callback != null) callback();
        }
        void ReleaseSettings()
        {
            if (released) return; released = true;
            Log.Write("Overlay detaching Settings");
            IntPtr foreground = Native.GetForegroundWindow();
            bool returnFocus = foreground == Handle || foreground == backdrop.Handle || Native.IsChild(Handle, foreground);
            settings.FormClosed -= SettingsClosed;
            if (!settings.IsDisposed && !settingsClosed)
            {
                if (!settingsContent.IsDisposed) settings.Controls.Add(settingsContent); settings.MinimumSize = previousMinimum; settings.Bounds = previousBounds;
            }
            // Detach before closing the owner, which otherwise recursively closes this window.
            Owner = null; backdrop.Close();
            if (!settings.IsDisposed && !settingsClosed && previouslyVisible) settings.ShowForUser();
            if (returnFocus && Native.IsWindow(previousWindow)) Native.SetForegroundWindow(previousWindow);
        }
        protected override void Dispose(bool disposing) { if (disposing) { ReleaseSettings(); backdrop.Dispose(); } base.Dispose(disposing); }
    }
}
