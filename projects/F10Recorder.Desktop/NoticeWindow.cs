using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace F10Recorder;

internal sealed class NoticeWindow : Window
{
    readonly TextBlock title = Ui.Label("", 16, null, FontWeight.SemiBold), detail = Ui.Label("", 13, Ui.Muted);
    readonly Border frame;
    readonly DispatcherTimer dismiss = new();
    public string Heading => title.Text;
    public NoticeWindow()
    {
        Title = "F10 Recorder Notification"; Width = 420; Height = 140; CanResize = false; ShowInTaskbar = false; ShowActivated = false; Topmost = true;
        WindowDecorations = WindowDecorations.None; Background = Ui.Surface; Opacity = 0.98;
        var text = new StackPanel { Spacing = 8, Margin = new Thickness(20, 16) }; text.Children.Add(Ui.Label("F10 RECORDER", 10, Ui.Muted)); text.Children.Add(title); text.Children.Add(detail);
        frame = new Border { BorderBrush = Ui.Accent, BorderThickness = new Thickness(3, 0, 0, 0), Child = text }; Content = frame;
        Opened += (_, _) => { var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero; if (handle != IntPtr.Zero) WindowStyles.Passive(handle); };
        dismiss.Tick += (_, _) => Dismiss();
    }
    public void Present(string heading, string message, NotificationKind kind, int duration, SettingsWindow settings)
    {
        dismiss.Stop(); title.Text = heading; detail.Text = message; frame.BorderBrush = kind == NotificationKind.Warning ? Ui.Warning : Ui.Accent;
        Reposition(settings);
        if (!IsVisible) Show();
        var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero; if (handle != IntPtr.Zero) WindowStyles.Passive(handle);
        if (duration > 0) { dismiss.Interval = TimeSpan.FromMilliseconds(duration); dismiss.Start(); }
    }
    public void Reposition(SettingsWindow settings)
    {
        var area = Native.DisplayBounds(Native.GetForegroundWindow());
        double scale = Screens.ScreenFromPoint(new PixelPoint(area.Left, area.Top))?.Scaling ?? 1;
        Width = Math.Min(420, (area.Width - 48) / scale);
        int left = area.Right - (int)(Width * scale) - 24;
        if (settings.OverlayVisible) left = Math.Max(area.Left + 24, settings.Position.X - (int)(Width * scale) - 16);
        Position = new PixelPoint(left, area.Top + 24);
    }
    public void Dismiss() { dismiss.Stop(); Hide(); }
}
