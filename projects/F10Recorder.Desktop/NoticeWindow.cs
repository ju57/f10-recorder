using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Automation;
using Avalonia.Layout;

namespace F10Recorder;

internal sealed class NoticeWindow : Window
{
    readonly TextBlock title = Ui.Label("", 14, null, FontWeight.SemiBold), detail = Ui.Label("", 12, Ui.Muted);
    readonly TextBlock symbol = Ui.Label("", 24, Ui.Accent, FontWeight.SemiBold);
    readonly Border frame;
    readonly DispatcherTimer dismiss = new();
    int presentation;
    public string Heading => title.Text;
    public NoticeWindow()
    {
        Title = "F10 Recorder Notification"; Width = 360; Height = 112; CanResize = false; ShowInTaskbar = false; ShowActivated = false; Topmost = true;
        WindowDecorations = WindowDecorations.None; Background = Ui.Background;
        Transitions = new Transitions { new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(160), Easing = new CubicEaseOut() } };
        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("44,*"), ColumnSpacing = 14, Margin = new Thickness(16, 15, 20, 15) };
        symbol.VerticalAlignment = VerticalAlignment.Center; symbol.HorizontalAlignment = HorizontalAlignment.Center; content.Children.Add(symbol);
        var text = new StackPanel { Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        title.MaxLines = 2; detail.MaxLines = 2; title.TextTrimming = TextTrimming.CharacterEllipsis; detail.TextTrimming = TextTrimming.CharacterEllipsis;
        text.Children.Add(title); text.Children.Add(detail); Grid.SetColumn(text, 1); content.Children.Add(text);
        frame = new Border { BorderBrush = Ui.Accent, BorderThickness = new Thickness(5, 0, 0, 0), Child = content }; Content = frame;
        CaptureVisibility.Exclude(this);
        Opened += (_, _) => { var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero; if (handle != IntPtr.Zero) WindowStyles.Passive(handle); };
        dismiss.Tick += (_, _) => Expire();
    }
    public void Present(string heading, string message, NotificationKind kind, int duration, SettingsWindow settings)
    {
        int current = ++presentation;
        dismiss.Stop(); title.Text = heading; detail.Text = message; frame.BorderBrush = kind == NotificationKind.Warning ? Ui.Warning : Ui.Accent;
        bool expanded = message.Length > 90 || heading.Length > 45;
        Height = expanded ? 144 : 112; detail.MaxLines = expanded ? 4 : 2;
        symbol.Text = kind == NotificationKind.Warning ? "!" : kind == NotificationKind.Success ? "✓" : "↻";
        symbol.Foreground = frame.BorderBrush;
        AutomationProperties.SetName(this, heading + ". " + message);
        Reposition(settings);
        if (!IsVisible) { Opacity = 0; Show(); Dispatcher.UIThread.Post(() => { if (presentation == current && IsVisible) Opacity = 1; }, DispatcherPriority.Render); }
        else Opacity = 1;
        var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero; if (handle != IntPtr.Zero) WindowStyles.Passive(handle);
        if (duration > 0) { dismiss.Interval = TimeSpan.FromMilliseconds(duration); dismiss.Start(); }
    }
    public void Reposition(SettingsWindow settings)
    {
        var area = Native.DisplayBounds(Native.GetForegroundWindow());
        double scale = Screens.ScreenFromPoint(new PixelPoint(area.Left, area.Top))?.Scaling ?? 1;
        Width = Math.Max(160, Math.Min(360, area.Width / scale));
        Position = new PixelPoint(area.Right - (int)(Width * scale), area.Top + Math.Min((int)(105 * scale), Math.Max(0, area.Height - (int)(Height * scale))));
    }
    void Expire()
    {
        dismiss.Stop(); int current = ++presentation; Opacity = 0;
        DispatcherTimer.RunOnce(() => { if (presentation == current) Hide(); }, TimeSpan.FromMilliseconds(160));
    }
    // Screenshot preparation must hide synchronously, without waiting for a fade.
    public void Dismiss() { ++presentation; dismiss.Stop(); Hide(); }
}
