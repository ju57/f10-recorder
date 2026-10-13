using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace F10Recorder
{
    internal sealed class SettingsForm : Form
    {
        readonly NumberStepper minutes = new NumberStepper { Minimum = 1, Maximum = 30, Name = "ReplayMinutes" };
        readonly NumberStepper bitrate = new NumberStepper { Minimum = 5, Maximum = 100, Name = "BitrateMbps" };
        readonly NumberStepper display = new NumberStepper { Minimum = -1, Maximum = 31, Name = "DisplayIndex" };
        readonly DarkComboBox fps = new DarkComboBox { Name = "FramesPerSecond" };
        readonly DarkComboBox codec = new DarkComboBox { Name = "VideoCodec" };
        readonly DarkComboBox key = new DarkComboBox { Name = "SaveKey" };
        readonly DarkComboBox overlayKey = new DarkComboBox { Name = "OverlayKey" };
        readonly DarkComboBox screenshotKey = new DarkComboBox { Name = "ScreenshotKey" };
        readonly DarkToggle audio = new DarkToggle { Name = "SystemAudio" };
        readonly DarkToggle startup = new DarkToggle { Name = "Startup" };
        readonly DarkToggle recording = new DarkToggle { Name = "RecordingEnabled" };
        readonly CheckBox ctrl = Modifier("Ctrl"), alt = Modifier("Alt"), shift = Modifier("Shift");
        readonly CheckBox overlayCtrl = Modifier("Ctrl"), overlayAlt = Modifier("Alt"), overlayShift = Modifier("Shift");
        readonly CheckBox screenshotCtrl = Modifier("Ctrl"), screenshotAlt = Modifier("Alt"), screenshotShift = Modifier("Shift");
        internal Control SettingsContent { get; private set; }
        readonly TextBox output = UiTheme.TextBox();
        readonly TextBox screenshotOutput = UiTheme.TextBox();
        readonly Label codecStatus = UiTheme.Label("Checking GPU encoders…", 9, false, true);
        readonly Label estimate = UiTheme.Label("", 9, false, true);
        readonly Label error = UiTheme.Label("", 9);
        readonly Label stateTitle = UiTheme.Label("Replay buffer", 12, true);
        readonly Label stateDescription = UiTheme.Label("", 9, false, true);
        readonly Label stateBadge = UiTheme.Label("Paused", 9, true);
        readonly Label codecDescription = UiTheme.Label("", 9, false, true);
        readonly Button save = UiTheme.Button("Apply changes", true);
        readonly List<Panel> pages = new List<Panel>();
        readonly List<Button> navigation = new List<Button>();
        readonly Action<bool> pauseChanged;
        readonly Action<Settings, bool> apply;
        readonly string data;
        readonly int activeMinutes;
        readonly Icon brandIcon;
        int probeVersion;
        bool syncingState, probing, dirty;
        string selectedCodec;

        internal SettingsForm(Settings config, string dataDirectory, Action<Settings, bool> applySettings, Action<bool> setPaused = null, bool isPaused = true)
        {
            data = dataDirectory; apply = applySettings; pauseChanged = setPaused; activeMinutes = config.ReplayMinutes;
            Text = "F10 Recorder"; BackColor = UiTheme.Background; ForeColor = UiTheme.Text; Font = UiTheme.Font(10); brandIcon = TrayApplication.MakeIcon(UiTheme.Accent); Icon = brandIcon;
            AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
            Size = new Size(780, 900); MinimumSize = new Size(700, 740); StartPosition = FormStartPosition.CenterScreen; MaximizeBox = false; DoubleBuffered = true;
            minutes.Value = config.ReplayMinutes; bitrate.Value = config.BitrateMbps; display.Value = config.DisplayIndex;
            fps.Items.AddRange(Settings.SupportedFrameRates.Cast<object>().ToArray()); fps.SelectedItem = config.FramesPerSecond;
            selectedCodec = config.VideoCodec; codec.Enabled = false; save.Enabled = false;
            for (int i = 1; i <= 12; i++) key.Items.Add("F" + i); key.SelectedIndex = config.SaveKey - 112;
            ctrl.Checked = (config.SaveModifiers & 2) != 0; alt.Checked = (config.SaveModifiers & 1) != 0; shift.Checked = (config.SaveModifiers & 4) != 0;
            for (int i = 1; i <= 12; i++) overlayKey.Items.Add("F" + i); for (char letter = 'A'; letter <= 'Z'; letter++) overlayKey.Items.Add(letter.ToString());
            overlayKey.SelectedIndex = config.OverlayKey >= 112 ? config.OverlayKey - 112 : config.OverlayKey - 65 + 12;
            overlayCtrl.Checked = (config.OverlayModifiers & 2) != 0; overlayAlt.Checked = (config.OverlayModifiers & 1) != 0; overlayShift.Checked = (config.OverlayModifiers & 4) != 0;
            overlayCtrl.Name = "OverlayCtrl"; overlayAlt.Name = "OverlayAlt"; overlayShift.Name = "OverlayShift";
            for (int i = 1; i <= 12; i++) screenshotKey.Items.Add("F" + i); for (char letter = 'A'; letter <= 'Z'; letter++) screenshotKey.Items.Add(letter.ToString());
            screenshotKey.SelectedIndex = config.ScreenshotKey >= 112 ? config.ScreenshotKey - 112 : config.ScreenshotKey - 65 + 12;
            screenshotCtrl.Checked = (config.ScreenshotModifiers & 2) != 0; screenshotAlt.Checked = (config.ScreenshotModifiers & 1) != 0; screenshotShift.Checked = (config.ScreenshotModifiers & 4) != 0;
            screenshotCtrl.Name = "ScreenshotCtrl"; screenshotAlt.Name = "ScreenshotAlt"; screenshotShift.Name = "ScreenshotShift";
            screenshotOutput.Name = "ScreenshotDirectory"; screenshotOutput.Text = config.ScreenshotDirectory;
            audio.Checked = config.SystemAudio; startup.Checked = Startup.Enabled; output.Text = config.OutputDirectory;
            output.Name = "OutputDirectory";

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(28, 14, 28, 18), BackColor = UiTheme.Background };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 78)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 82)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 96)); Controls.Add(root);
            SettingsContent = root;
            root.Controls.Add(Header(config), 0, 0); root.Controls.Add(ReplayState(), 0, 1);
            var tabs = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = new Padding(0, 14, 0, 8) };
            for (int i = 0; i < 4; i++) tabs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25)); root.Controls.Add(tabs, 0, 2);
            var content = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) }; root.Controls.Add(content, 0, 3);
            string[] names = { "Capture", "Audio", "Controls", "Storage" };
            for (int i = 0; i < names.Length; i++)
            {
                int pageIndex = i; var button = new NavigationButton(names[i], i); button.Name = "Nav" + names[i]; button.Dock = DockStyle.Fill; button.Margin = new Padding(i == 0 ? 0 : 4, 0, i == 3 ? 0 : 4, 0); button.FlatAppearance.BorderSize = 0;
                button.Click += delegate { SelectPage(pageIndex); }; navigation.Add(button); tabs.Controls.Add(button, i, 0);
                var page = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = UiTheme.Background, Visible = false, Name = "Page" + names[i] }; pages.Add(page); content.Controls.Add(page);
            }
            BuildCapture(); BuildAudio(); BuildControls(); BuildStorage(); root.Controls.Add(Footer(), 0, 4);
            recording.CheckedChanged += delegate { if (!syncingState && pauseChanged != null) pauseChanged(!recording.Checked); };
            SetRecordingState(isPaused, isPaused ? "Paused" : "Starting replay buffer…", false);
            foreach (var control in new Control[] { minutes, bitrate, display, fps, codec, key, ctrl, alt, shift, overlayKey, overlayCtrl, overlayAlt, overlayShift, screenshotKey, screenshotCtrl, screenshotAlt, screenshotShift, audio, startup, output, screenshotOutput }) BindChanges(control);
            minutes.ValueChanged += delegate { UpdateEstimate(); }; bitrate.ValueChanged += delegate { UpdateEstimate(); }; audio.CheckedChanged += delegate { UpdateEstimate(); };
            codec.SelectedIndexChanged += delegate { UpdateCodecDescription(); };
            Shown += delegate { CheckCodecs(); }; SelectPage(0); UpdateEstimate();
        }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); UiTheme.DarkTitleBar(Handle); }
        Control Header(Settings config)
        {
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = new Padding(0) };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var title = UiTheme.Label("F10 Recorder", 25, true); panel.Controls.Add(title, 0, 0);
            var subtitle = UiTheme.Label("Your recent moments, ready to save.", 10, false, true); panel.Controls.Add(subtitle, 0, 1);
            var shortcut = UiTheme.Label(config.HotkeyText.Replace("+", " + "), 10, true); shortcut.Font = new Font("Consolas", 10); shortcut.Padding = new Padding(12, 12, 12, 12); shortcut.BackColor = UiTheme.Surface; shortcut.Anchor = AnchorStyles.Right; shortcut.AccessibleName = "Current save shortcut"; panel.Controls.Add(shortcut, 1, 0); panel.SetRowSpan(shortcut, 2);
            return panel;
        }
        Control ReplayState()
        {
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, Padding = new Padding(18, 12, 18, 12), BackColor = UiTheme.Surface, Margin = new Padding(0) };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
            panel.Controls.Add(stateTitle, 0, 0); panel.Controls.Add(stateDescription, 0, 1);
            stateBadge.Anchor = AnchorStyles.Right; stateBadge.Margin = new Padding(10, 0, 12, 0); panel.Controls.Add(stateBadge, 1, 0); panel.SetRowSpan(stateBadge, 2);
            recording.BackColor = UiTheme.Surface; recording.Anchor = AnchorStyles.Right; recording.AccessibleName = "Enable replay recording"; recording.Enabled = pauseChanged != null; panel.Controls.Add(recording, 2, 0); panel.SetRowSpan(recording, 2);
            return panel;
        }
        internal void SetRecordingState(bool paused, string message, bool ready)
        {
            syncingState = true; recording.Checked = !paused; syncingState = false;
            stateBadge.Text = paused ? "Paused" : ready ? "Buffering" : "Starting"; stateBadge.ForeColor = paused ? UiTheme.Warning : ready ? UiTheme.Accent : UiTheme.Muted;
            if (!paused && message.StartsWith("Recovering:")) { stateBadge.Text = "Recovering"; stateBadge.ForeColor = UiTheme.Warning; }
            stateDescription.Text = paused ? "Resume when you want to capture again." : ready ? "Saving keeps the replay buffer running." : "Preparing your replay buffer…";
            if (!paused && ready)
            {
                var match = System.Text.RegularExpressions.Regex.Match(message, @"·\s*(\d+)s");
                if (match.Success) stateDescription.Text = match.Groups[1].Value + " seconds captured · up to " + activeMinutes + " minutes.";
            }
            if (!paused && message.StartsWith("Recovering:")) stateDescription.Text = "Capture is recovering automatically.";
            if (!paused && message.Contains("hotkey unavailable")) { stateBadge.Text = "Shortcut unavailable"; stateBadge.ForeColor = UiTheme.Warning; stateDescription.Text = "Choose an available save shortcut in Controls."; }
        }
        TableLayoutPanel PageBody(int index, string title, string description)
        {
            var body = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 0, Padding = new Padding(0, 8, 6, 6), Margin = new Padding(0) }; body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); pages[index].Controls.Add(body);
            var heading = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) }; var name = UiTheme.Label(title, 18, true); name.Location = new Point(0, 0); heading.Controls.Add(name); var text = UiTheme.Label(description, 9, false, true); text.Location = new Point(0, 33); heading.Controls.Add(text); AddBlock(body, heading, 60); return body;
        }
        void BuildCapture()
        {
            var body = PageBody(0, "Video capture", "Choose the quality and length of your saved replays.");
            AddRow(body, "Replay length", "Save up to this many minutes.", WithUnit(minutes, "min"));
            AddRow(body, "Video codec", "Encoded on your NVIDIA GPU.", codec);
            codecDescription.Margin = new Padding(0, 4, 0, 0); AddBlock(body, codecDescription, 24);
            AddRow(body, "Frame rate", "24, 30, 60, or 120 FPS.", fps);
            AddRow(body, "Video bitrate", "5–100 Mbps. More detail uses more disk.", WithUnit(bitrate, "Mbps"));
            AddRow(body, "Display", "−1 follows your primary monitor.", display);
            var check = UiTheme.Button("Refresh available codecs"); check.Width = 205; check.Height = 32; check.FlatAppearance.BorderSize = 0; check.BackColor = UiTheme.Background; check.ForeColor = UiTheme.Accent; check.Click += delegate { CheckCodecs(); };
            codecStatus.Margin = new Padding(0, 8, 0, 0); AddBlock(body, codecStatus, 28); AddBlock(body, check, 32);
        }
        void BuildAudio()
        {
            var body = PageBody(1, "System audio", "Keep the sound playing through your default Windows output.");
            AddRow(body, "Capture system sound", "Include game audio, music, and other apps.", audio);
            AddRow(body, "Playback device", "Follows your Windows default output.", ReadOnlyValue("Default Windows output"));
            Section(body, "Saved audio");
            AddRow(body, "Format", "Compatible with your MP4 replay.", ReadOnlyValue("AAC"));
            AddRow(body, "Channels", "Left and right audio.", ReadOnlyValue("Stereo"));
            AddRow(body, "Quality", "48 kHz sample rate.", ReadOnlyValue("192 kbps"));
        }
        void BuildControls()
        {
            var body = PageBody(2, "Shortcuts & startup", "Keep replay saving close at hand.");
            var keys = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Fill, Margin = new Padding(0) }; key.Width = 78; key.Height = 34; key.Margin = new Padding(0, 3, 6, 0); keys.Controls.AddRange(new Control[] { key, ctrl, alt, shift });
            AddRow(body, "Save replay", "Available while other apps are open.", keys);
            var overlayKeys = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Fill, Margin = new Padding(0) }; overlayKey.Width = 78; overlayKey.Height = 34; overlayKey.Margin = new Padding(0, 3, 6, 0); overlayKeys.Controls.AddRange(new Control[] { overlayKey, overlayCtrl, overlayAlt, overlayShift });
            AddRow(body, "Open / close overlay", "Escape also closes the overlay.", overlayKeys);
            var screenshotKeys = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Fill, Margin = new Padding(0) }; screenshotKey.Width = 78; screenshotKey.Height = 34; screenshotKey.Margin = new Padding(0, 3, 6, 0); screenshotKeys.Controls.AddRange(new Control[] { screenshotKey, screenshotCtrl, screenshotAlt, screenshotShift });
            AddRow(body, "Take screenshot", "Save the selected display as a PNG.", screenshotKeys);
            AddRow(body, "Start with Windows", "Start buffering when you sign in.", startup);
            var hint = UiTheme.Label("Shortcut changes take effect when you apply them.\nIf another app owns the shortcut, choose a different combination.", 9, false, true); hint.Margin = new Padding(0, 22, 0, 0); AddBlock(body, hint, 64);
        }
        void BuildStorage()
        {
            var body = PageBody(3, "Files & storage", "Your replays stay on this computer.");
            Section(body, "Saved clips");
            AddBlock(body, Browse(output), 42);
            var folders = UiTheme.Label("Replays are grouped into a folder for the active application.", 9, false, true); folders.Margin = new Padding(0, 8, 0, 0); AddBlock(body, folders, 32);
            var open = UiTheme.Button("Open saved clips"); open.Width = 160; open.Margin = new Padding(0, 12, 0, 16); open.Click += delegate { try { Directory.CreateDirectory(output.Text); Process.Start("explorer.exe", CaptureSession.Quote(output.Text)); } catch (Exception ex) { ShowError(ex.Message); } }; AddBlock(body, open, 68);
            var hint = UiTheme.Label("The rolling buffer uses temporary disk space.\nSaved clips are kept until you delete them.", 9, false, true); hint.Margin = new Padding(0, 0, 0, 14); AddBlock(body, hint, 50);
            Section(body, "Screenshots"); AddBlock(body, Browse(screenshotOutput), 42);
            var openScreenshots = UiTheme.Button("Open screenshots"); openScreenshots.Width = 160; openScreenshots.Margin = new Padding(0, 12, 0, 8); openScreenshots.Click += delegate { try { Directory.CreateDirectory(screenshotOutput.Text); Process.Start("explorer.exe", CaptureSession.Quote(screenshotOutput.Text)); } catch (Exception ex) { ShowError(ex.Message); } }; AddBlock(body, openScreenshots, 60);
            Section(body, "Media engine");
            var engineHint = UiTheme.Label("FFmpeg is managed automatically by F10 Recorder.\nSetup reuses a compatible installed copy or installs one for you.", 9, false, true); engineHint.Margin = new Padding(0, 0, 0, 12); AddBlock(body, engineHint, 48);
        }
        Control Footer()
        {
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 3, Margin = new Padding(0), Padding = new Padding(0, 10, 0, 0) };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88)); panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 154));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 1)); panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 29)); panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var line = new Panel { BackColor = UiTheme.Border, Dock = DockStyle.Fill, Margin = new Padding(0) }; panel.Controls.Add(line, 0, 0); panel.SetColumnSpan(line, 3);
            error.ForeColor = UiTheme.Error; error.AutoEllipsis = true; error.AutoSize = false; error.Dock = DockStyle.Fill; error.Visible = false; panel.Controls.Add(error, 0, 1); panel.SetColumnSpan(error, 3);
            var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0) }; left.Controls.Add(estimate, 0, 0);
            var logs = new LinkLabel { Text = "Diagnostic logs", AutoSize = true, Font = UiTheme.Font(9), LinkColor = UiTheme.Muted, ActiveLinkColor = UiTheme.Accent, VisitedLinkColor = UiTheme.Muted, BackColor = UiTheme.Background, Margin = new Padding(0, 4, 0, 0), LinkBehavior = LinkBehavior.HoverUnderline }; logs.LinkClicked += delegate { Process.Start("explorer.exe", CaptureSession.Quote(data)); }; left.Controls.Add(logs, 0, 1); panel.Controls.Add(left, 0, 2);
            var close = UiTheme.Button("Close"); close.Dock = DockStyle.Fill; close.Margin = new Padding(0, 0, 8, 0); close.Click += delegate { Close(); }; panel.Controls.Add(close, 1, 2); save.Dock = DockStyle.Fill; save.Name = "ApplySettings"; save.Click += delegate { ApplySettings(); }; panel.Controls.Add(save, 2, 2); return panel;
        }
        static void AddBlock(TableLayoutPanel body, Control control, int height) { int row = body.RowCount++; body.RowStyles.Add(new RowStyle(SizeType.Absolute, height)); body.Controls.Add(control, 0, row); }
        static void Section(TableLayoutPanel body, string title) { var label = UiTheme.Label(title, 11, true); label.Margin = new Padding(0, 12, 0, 12); AddBlock(body, label, 46); }
        static void AddRow(TableLayoutPanel body, string title, string description, Control control)
        {
            var row = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, Dock = DockStyle.Fill, Margin = new Padding(0) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 51)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 49));
            var labels = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) }; var name = UiTheme.Label(title, 10, true); name.Location = new Point(0, 11); var hint = UiTheme.Label(description, 9, false, true); hint.Location = new Point(0, 36); labels.Controls.Add(name); labels.Controls.Add(hint); row.Controls.Add(labels, 0, 0);
            var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 14, 0, 14), Margin = new Padding(0) }; control.AccessibleName = title; control.Margin = new Padding(0); control.Anchor = AnchorStyles.Right;
            if (control is DarkToggle) { control.Location = new Point(0, 0); host.Resize += delegate { control.Location = new Point(host.Width - control.Width, (host.Height - control.Height) / 2); }; } else control.Dock = DockStyle.Fill;
            host.Controls.Add(control); row.Controls.Add(host, 1, 0); row.Paint += delegate(object sender, PaintEventArgs e) { using (var pen = new Pen(UiTheme.Border)) e.Graphics.DrawLine(pen, 0, row.Height - 1, row.Width, row.Height - 1); }; AddBlock(body, row, 68);
        }
        static Control WithUnit(NumberStepper stepper, string unit)
        {
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) }; panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 56)); stepper.Dock = DockStyle.Fill; panel.Controls.Add(stepper, 0, 0); var label = UiTheme.Label(unit, 9, false, true); label.Anchor = AnchorStyles.Right; panel.Controls.Add(label, 1, 0); return panel;
        }
        static Control ReadOnlyValue(string text) { var label = UiTheme.Label(text, 10); label.TextAlign = ContentAlignment.MiddleRight; label.AutoSize = false; label.Height = 40; return label; }
        static CheckBox Modifier(string text)
        {
            var box = new CheckBox { Text = text, Appearance = Appearance.Button, FlatStyle = FlatStyle.Flat, AutoSize = false, Size = new Size(62, 38), TextAlign = ContentAlignment.MiddleCenter, BackColor = UiTheme.Input, ForeColor = UiTheme.Text, Font = UiTheme.Font(9, true), Cursor = Cursors.Hand, Margin = new Padding(0, 0, 5, 0), AccessibleName = text + " modifier" };
            box.FlatAppearance.BorderColor = UiTheme.Border; box.FlatAppearance.CheckedBackColor = UiTheme.Selected; box.FlatAppearance.MouseOverBackColor = UiTheme.Selected; box.CheckedChanged += delegate { box.BackColor = box.Checked ? UiTheme.Selected : UiTheme.Input; box.ForeColor = box.Checked ? UiTheme.Accent : UiTheme.Text; box.FlatAppearance.BorderColor = box.Checked ? UiTheme.Accent : UiTheme.Border; }; return box;
        }
        Control Browse(TextBox box)
        {
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Height = 42, Margin = new Padding(0) }; panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104)); var field = UiTheme.TextField(box); field.Dock = DockStyle.Fill; panel.Controls.Add(field, 0, 0);
            var button = UiTheme.Button("Browse…"); button.Dock = DockStyle.Fill; button.Margin = new Padding(10, 0, 0, 0); panel.Controls.Add(button, 1, 0);
            button.Click += delegate { using (var picker = new FolderBrowserDialog { SelectedPath = box.Text }) if (picker.ShowDialog(this) == DialogResult.OK) box.Text = picker.SelectedPath; }; return panel;
        }
        void BindChanges(Control control)
        {
            Action changed = delegate { dirty = true; error.Visible = false; };
            var number = control as NumberStepper; if (number != null) number.ValueChanged += delegate { changed(); };
            var combo = control as ComboBox; if (combo != null) combo.SelectedIndexChanged += delegate { changed(); };
            var check = control as CheckBox; if (check != null) check.CheckedChanged += delegate { changed(); };
            var text = control as TextBox; if (text != null) text.TextChanged += delegate { changed(); };
        }
        void UpdateEstimate()
        {
            decimal megabytes = minutes.Value * 60 * (bitrate.Value + (audio.Checked ? 0.192m : 0)) / 8;
            estimate.Text = "Estimated buffer · " + (megabytes >= 1000 ? (megabytes / 1000).ToString("0.0") + " GB" : megabytes.ToString("0") + " MB");
        }
        internal void SelectPage(int index)
        {
            for (int i = 0; i < pages.Count; i++) { pages[i].Visible = i == index; ((NavigationButton)navigation[i]).Selected = i == index; navigation[i].Invalidate(); navigation[i].AccessibleDescription = i == index ? "Selected page" : "Open " + navigation[i].Text + " settings"; }
            if (pages.Count > index) pages[index].BringToFront();
        }
        internal void ShowForUser()
        {
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            var work = Screen.FromControl(this).WorkingArea; MinimumSize = new Size(Math.Min(700, work.Width - 24), Math.Min(740, work.Height - 24));
            if (Width > work.Width - 24 || Height > work.Height - 24) Size = new Size(Math.Min(Width, work.Width - 24), Math.Min(Height, work.Height - 24));
            if (!Screen.AllScreens.Any(screen => screen.WorkingArea.Contains(Bounds))) Location = new Point(work.Left + (work.Width - Width) / 2, work.Top + (work.Height - Height) / 2);
            if (!Visible) Show(); Native.ShowWindow(Handle, 9); BringToFront(); Activate(); Native.SetForegroundWindow(Handle);
        }
        void UpdateCodecDescription()
        {
            var option = codec.SelectedItem as EncoderOption; codecDescription.Text = option == null ? "" : option.Codec == "h264_nvenc" ? "H.264 works with the widest range of video players." : option.Codec == "hevc_nvenc" ? "HEVC keeps more detail at lower bitrates. Use a compatible player." : "AV1 is efficient at lower bitrates. Use a modern video player.";
        }
        internal bool CodecCheckFinished { get { return probeVersion > 0 && !probing; } }
        internal void PrepareOverlay() { var handle = Handle; if (probeVersion == 0) CheckCodecs(); }
        internal string[] AvailableCodecs { get { return codec.Items.Cast<EncoderOption>().Select(option => option.Codec).ToArray(); } }
        void CheckCodecs()
        {
            if (codec.SelectedItem != null) selectedCodec = ((EncoderOption)codec.SelectedItem).Codec;
            int version = ++probeVersion; probing = true; codec.Enabled = false; save.Enabled = false; codecStatus.Text = "Checking GPU encoders…";
            Task.Run(delegate
            {
                List<EncoderOption> options = null; string failure = null;
                try { options = EncoderCapabilities.Probe(MediaDependency.ResolveExecutable()); } catch (Exception ex) { failure = ex.Message; }
                if (IsDisposed || Disposing) return;
                try { BeginInvoke(new Action(delegate
                {
                    if (version != probeVersion || IsDisposed) return;
                    bool hadChanges = dirty; codec.Items.Clear(); if (options != null) foreach (var option in options) codec.Items.Add(option);
                    codec.SelectedItem = codec.Items.Cast<EncoderOption>().FirstOrDefault(option => option.Codec == selectedCodec); if (codec.SelectedItem == null && codec.Items.Count > 0) codec.SelectedIndex = 0;
                    codec.Enabled = codec.Items.Count > 0; save.Enabled = codec.Enabled; probing = false; dirty = hadChanges;
                    codecStatus.Text = failure ?? (codec.Enabled ? "GPU encoders available · " + string.Join(" / ", AvailableCodecs.Select(UiTheme.CodecName)) : "No supported encoders found. Check your NVIDIA driver or repair the installation.");
                    Log.Write("Settings codec check: " + string.Join(", ", AvailableCodecs));
                })); } catch (InvalidOperationException) { }
            });
        }
        void ShowError(string message) { error.Text = message; error.Visible = true; Log.Write("Settings: " + message); }
        internal bool ApplySettings()
        {
            try
            {
                if (codec.SelectedItem == null || probing) throw new InvalidOperationException("Check the available codecs before applying changes.");
                var next = new Settings { ReplayMinutes = (int)minutes.Value, FramesPerSecond = (int)fps.SelectedItem, BitrateMbps = (int)bitrate.Value, VideoCodec = ((EncoderOption)codec.SelectedItem).Codec, DisplayIndex = (int)display.Value, SaveKey = 112 + key.SelectedIndex, SaveModifiers = (ctrl.Checked ? 2 : 0) | (alt.Checked ? 1 : 0) | (shift.Checked ? 4 : 0), OverlayKey = overlayKey.SelectedIndex < 12 ? 112 + overlayKey.SelectedIndex : 65 + overlayKey.SelectedIndex - 12, OverlayModifiers = (overlayCtrl.Checked ? 2 : 0) | (overlayAlt.Checked ? 1 : 0) | (overlayShift.Checked ? 4 : 0), SystemAudio = audio.Checked, OutputDirectory = output.Text };
                next.ScreenshotKey = screenshotKey.SelectedIndex < 12 ? 112 + screenshotKey.SelectedIndex : 65 + screenshotKey.SelectedIndex - 12;
                next.ScreenshotModifiers = (screenshotCtrl.Checked ? 2 : 0) | (screenshotAlt.Checked ? 1 : 0) | (screenshotShift.Checked ? 4 : 0); next.ScreenshotDirectory = screenshotOutput.Text;
                next.Validate(); apply(next, startup.Checked); dirty = false; Close(); return true;
            }
            catch (Exception ex) { ShowError(ex.Message); return false; }
        }
        protected override void Dispose(bool disposing) { if (disposing && brandIcon != null) brandIcon.Dispose(); base.Dispose(disposing); }
    }
}
