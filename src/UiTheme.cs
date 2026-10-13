using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace F10Recorder
{
    internal static class UiTheme
    {
        internal static readonly Color Background = Color.FromArgb(23, 27, 25);
        internal static readonly Color Surface = Color.FromArgb(32, 38, 34);
        internal static readonly Color Input = Color.FromArgb(43, 50, 45);
        internal static readonly Color Border = Color.FromArgb(57, 66, 60);
        internal static readonly Color Text = Color.FromArgb(237, 244, 239);
        internal static readonly Color Muted = Color.FromArgb(157, 174, 163);
        internal static readonly Color Accent = Color.FromArgb(83, 197, 139);
        internal static readonly Color Selected = Color.FromArgb(40, 73, 54);
        internal static readonly Color Warning = Color.FromArgb(224, 177, 99);
        internal static readonly Color Error = Color.FromArgb(240, 145, 145);

        internal static Font Font(float size, bool bold = false) { return new Font(bold ? "Segoe UI Semibold" : "Segoe UI", size, FontStyle.Regular); }
        internal static Button Button(string text, bool primary = false)
        {
            var button = new Button { Text = text, FlatStyle = FlatStyle.Flat, BackColor = primary ? Accent : Input, ForeColor = primary ? Background : Text, Font = Font(10, true), Height = 40, Cursor = Cursors.Hand, Margin = new Padding(0), UseVisualStyleBackColor = false, AutoEllipsis = true };
            button.FlatAppearance.BorderSize = primary ? 0 : 1; button.FlatAppearance.BorderColor = Border;
            button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(107, 211, 157) : Color.FromArgb(55, 65, 59);
            button.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(64, 173, 118) : Selected;
            return button;
        }
        internal static Label Label(string text, float size = 10, bool bold = false, bool muted = false)
        {
            return new Label { Text = text, Font = Font(size, bold), ForeColor = muted ? Muted : Text, AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0) };
        }
        internal static TextBox TextBox()
        {
            return new TextBox { BackColor = Input, ForeColor = Text, BorderStyle = BorderStyle.None, Font = Font(10), Dock = DockStyle.Fill, Margin = new Padding(0) };
        }
        internal static Panel TextField(TextBox box)
        {
            var panel = new Panel { BackColor = Input, Height = 40, Padding = new Padding(12, 10, 12, 8), Margin = new Padding(0) };
            panel.Controls.Add(box); panel.Paint += delegate(object sender, PaintEventArgs e) { using (var pen = new Pen(box.Focused ? Accent : Border)) e.Graphics.DrawRectangle(pen, 0, 0, panel.Width - 1, panel.Height - 1); };
            box.Enter += delegate { panel.Invalidate(); }; box.Leave += delegate { panel.Invalidate(); };
            return panel;
        }
        internal static GraphicsPath Rounded(RectangleF rectangle, float radius)
        {
            var path = new GraphicsPath(); float diameter = radius * 2;
            path.AddArc(rectangle.X, rectangle.Y, diameter, diameter, 180, 90); path.AddArc(rectangle.Right - diameter, rectangle.Y, diameter, diameter, 270, 90); path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90); path.AddArc(rectangle.X, rectangle.Bottom - diameter, diameter, diameter, 90, 90); path.CloseFigure(); return path;
        }
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
        internal static void DarkTitleBar(IntPtr window)
        {
            try { int enabled = 1; DwmSetWindowAttribute(window, 20, ref enabled, 4); int color = Surface.R | (Surface.G << 8) | (Surface.B << 16); DwmSetWindowAttribute(window, 35, ref color, 4); color = Text.R | (Text.G << 8) | (Text.B << 16); DwmSetWindowAttribute(window, 36, ref color, 4); } catch (DllNotFoundException) { } catch (EntryPointNotFoundException) { }
        }
        internal static string CodecName(string codec) { return codec == "hevc_nvenc" ? "HEVC (H.265)" : codec == "av1_nvenc" ? "AV1" : "H.264"; }
    }
    internal sealed class DarkComboBox : ComboBox
    {
        internal DarkComboBox()
        {
            DropDownStyle = ComboBoxStyle.DropDownList; FlatStyle = FlatStyle.Flat; DrawMode = DrawMode.OwnerDrawFixed;
            BackColor = UiTheme.Input; ForeColor = UiTheme.Text; Font = UiTheme.Font(10); ItemHeight = 28; IntegralHeight = true; DropDownHeight = 220; Margin = new Padding(0);
        }
        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            Color background = (e.State & DrawItemState.Selected) != 0 ? UiTheme.Selected : UiTheme.Input;
            using (var brush = new SolidBrush(background)) e.Graphics.FillRectangle(brush, e.Bounds);
            if (e.Index >= 0)
            {
                object item = Items[e.Index]; var option = item as EncoderOption;
                string text = option == null ? item.ToString() : UiTheme.CodecName(option.Codec);
                TextRenderer.DrawText(e.Graphics, text, Font, Rectangle.Inflate(e.Bounds, -10, 0), Enabled ? UiTheme.Text : UiTheme.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
            if ((e.State & DrawItemState.Focus) != 0) using (var pen = new Pen(UiTheme.Accent)) e.Graphics.DrawRectangle(pen, e.Bounds.X, e.Bounds.Y, e.Bounds.Width - 1, e.Bounds.Height - 1);
        }
        void PaintChrome(Graphics graphics)
        {
            using (var brush = new SolidBrush(UiTheme.Input)) graphics.FillRectangle(brush, Width - 25, 0, 25, Height);
            using (var pen = new Pen(Focused ? UiTheme.Accent : UiTheme.Border)) graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            using (var pen = new Pen(Enabled ? UiTheme.Muted : UiTheme.Border, 1.5f)) { float x = Width - 14, y = Height / 2f; graphics.DrawLines(pen, new[] { new PointF(x - 4, y - 2), new PointF(x, y + 2), new PointF(x + 4, y - 2) }); }
        }
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == 0xF && IsHandleCreated) using (var graphics = Graphics.FromHwnd(Handle)) PaintChrome(graphics);
            if ((m.Msg == 0x317 || m.Msg == 0x318) && m.WParam != IntPtr.Zero) using (var graphics = Graphics.FromHdc(m.WParam)) PaintChrome(graphics);
        }
    }
    internal sealed class NavigationButton : Button
    {
        readonly int glyph; bool hover;
        internal bool Selected;
        internal NavigationButton(string text, int symbol) { Text = text; glyph = symbol; FlatStyle = FlatStyle.Flat; Font = UiTheme.Font(10, true); Cursor = Cursors.Hand; SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true); }
        protected override void OnMouseEnter(EventArgs e) { hover = true; base.OnMouseEnter(e); Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; base.OnMouseLeave(e); Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Selected ? UiTheme.Selected : hover ? UiTheme.Surface : UiTheme.Background); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Color ink = Selected ? UiTheme.Accent : UiTheme.Muted; float x = 18, y = Height / 2f - 7;
            using (var pen = new Pen(ink, 1.5f))
            {
                if (glyph == 0) { e.Graphics.DrawRectangle(pen, x, y + 2, 12, 10); e.Graphics.DrawPolygon(pen, new[] { new PointF(x + 12, y + 5), new PointF(x + 18, y + 2), new PointF(x + 18, y + 12), new PointF(x + 12, y + 9) }); }
                else if (glyph == 1) { e.Graphics.DrawPolygon(pen, new[] { new PointF(x, y + 5), new PointF(x + 4, y + 5), new PointF(x + 9, y + 1), new PointF(x + 9, y + 13), new PointF(x + 4, y + 9), new PointF(x, y + 9) }); e.Graphics.DrawArc(pen, x + 9, y + 1, 9, 12, -60, 120); }
                else if (glyph == 2) { e.Graphics.DrawRectangle(pen, x, y + 1, 18, 12); for (int i = 0; i < 4; i++) e.Graphics.DrawLine(pen, x + 3 + i * 4, y + 5, x + 3 + i * 4, y + 7); e.Graphics.DrawLine(pen, x + 4, y + 10, x + 14, y + 10); }
                else { e.Graphics.DrawPolygon(pen, new[] { new PointF(x, y), new PointF(x + 7, y), new PointF(x + 9, y + 3), new PointF(x + 18, y + 3), new PointF(x + 18, y + 13), new PointF(x, y + 13) }); }
            }
            TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(46, 0, Width - 50, Height), ink, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if (Focused) using (var pen = new Pen(UiTheme.Accent)) e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }
    internal sealed class DarkToggle : CheckBox
    {
        internal DarkToggle()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            AutoSize = false; Size = new Size(48, 30); Cursor = Cursors.Hand; BackColor = UiTheme.Background; AccessibleRole = AccessibleRole.CheckButton;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = UiTheme.Rounded(new RectangleF(3, 4, Width - 6, Height - 8), (Height - 8) / 2f))
            using (var brush = new SolidBrush(Checked ? UiTheme.Accent : UiTheme.Border)) e.Graphics.FillPath(brush, path);
            float diameter = Height - 14;
            using (var brush = new SolidBrush(Checked ? UiTheme.Background : UiTheme.Muted)) e.Graphics.FillEllipse(brush, Checked ? Width - diameter - 6 : 6, 7, diameter, diameter);
            if (Focused) using (var pen = new Pen(UiTheme.Accent)) e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
        protected override void OnCheckedChanged(EventArgs e) { base.OnCheckedChanged(e); Invalidate(); }
        protected override void OnEnter(EventArgs e) { base.OnEnter(e); Invalidate(); }
        protected override void OnLeave(EventArgs e) { base.OnLeave(e); Invalidate(); }
    }
    internal sealed class NumberStepper : UserControl
    {
        readonly TextBox input = UiTheme.TextBox();
        decimal value;
        public decimal Minimum = 0, Maximum = 100;
        public event EventHandler ValueChanged;
        public decimal Value
        {
            get { Commit(); return value; }
            set { if (value < Minimum || value > Maximum) throw new ArgumentOutOfRangeException("value"); if (this.value == value && input.Text.Length > 0) return; this.value = value; input.Text = value.ToString("0"); var callback = ValueChanged; if (callback != null) callback(this, EventArgs.Empty); }
        }
        internal NumberStepper()
        {
            BackColor = UiTheme.Input; Size = new Size(180, 40); MinimumSize = new Size(130, 40); MaximumSize = new Size(10000, 40); Margin = new Padding(0); AccessibleRole = AccessibleRole.SpinButton;
            var minus = new StepperButton(false); var plus = new StepperButton(true); minus.Width = plus.Width = 38; minus.Dock = DockStyle.Left; plus.Dock = DockStyle.Right;
            minus.AccessibleName = "Decrease value"; plus.AccessibleName = "Increase value";
            var center = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4, 10, 4, 6) }; input.TextAlign = HorizontalAlignment.Center; input.Font = new Font("Consolas", 11); center.Controls.Add(input); Controls.Add(center); Controls.Add(minus); Controls.Add(plus);
            minus.Click += delegate { Value = Math.Max(Minimum, Value - 1); }; plus.Click += delegate { Value = Math.Min(Maximum, Value + 1); };
            input.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down) { Value = Math.Max(Minimum, Math.Min(Maximum, Value + (e.KeyCode == Keys.Up ? 1 : -1))); e.Handled = true; e.SuppressKeyPress = true; } };
            input.Leave += delegate { Commit(); }; input.Enter += delegate { input.SelectAll(); Invalidate(); };
            Paint += delegate(object sender, PaintEventArgs e) { using (var pen = new Pen(ContainsFocus ? UiTheme.Accent : UiTheme.Border)) e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1); };
        }
        void Commit()
        {
            decimal parsed;
            if (decimal.TryParse(input.Text, out parsed)) { parsed = Math.Max(Minimum, Math.Min(Maximum, decimal.Truncate(parsed))); if (parsed != value) { value = parsed; var callback = ValueChanged; if (callback != null) callback(this, EventArgs.Empty); } }
            string normalized = value.ToString("0"); if (input.Text != normalized) input.Text = normalized;
        }
        sealed class StepperButton : Button
        {
            readonly bool plus; bool hover;
            internal StepperButton(bool increment) { plus = increment; FlatStyle = FlatStyle.Flat; BackColor = UiTheme.Input; Cursor = Cursors.Hand; SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true); }
            protected override void OnMouseEnter(EventArgs e) { hover = true; base.OnMouseEnter(e); Invalidate(); }
            protected override void OnMouseLeave(EventArgs e) { hover = false; base.OnMouseLeave(e); Invalidate(); }
            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.Clear(hover ? UiTheme.Selected : UiTheme.Input);
                using (var border = new Pen(Focused ? UiTheme.Accent : UiTheme.Border)) e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
                using (var pen = new Pen(UiTheme.Text, 1.4f)) { float x = Width / 2f, y = Height / 2f; e.Graphics.DrawLine(pen, x - 4, y, x + 4, y); if (plus) e.Graphics.DrawLine(pen, x, y - 4, x, y + 4); }
            }
        }
    }
    internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        internal DarkMenuRenderer() : base(new DarkColors()) { RoundedEdges = false; }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) { e.TextColor = e.Item.Enabled ? UiTheme.Text : UiTheme.Muted; base.OnRenderItemText(e); }
        sealed class DarkColors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground { get { return UiTheme.Surface; } }
            public override Color MenuItemSelected { get { return UiTheme.Selected; } }
            public override Color MenuItemBorder { get { return UiTheme.Border; } }
            public override Color MenuBorder { get { return UiTheme.Border; } }
            public override Color ImageMarginGradientBegin { get { return UiTheme.Surface; } }
            public override Color ImageMarginGradientMiddle { get { return UiTheme.Surface; } }
            public override Color ImageMarginGradientEnd { get { return UiTheme.Surface; } }
            public override Color SeparatorDark { get { return UiTheme.Border; } }
            public override Color SeparatorLight { get { return UiTheme.Border; } }
        }
    }
}
