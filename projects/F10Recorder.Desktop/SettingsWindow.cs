using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace F10Recorder;

internal sealed class SettingsWindow : Window
{
    internal SettingsPanel Panel { get; }
    internal bool OverlayVisible { get; private set; }
    internal event Action VisibilityChanged;
    Window backdrop;
    PixelPoint oldPosition;
    double oldWidth, oldHeight;
    bool wasVisible, exiting;
    IntPtr previousWindow;
    public SettingsWindow(WindowsRecorder recorder)
    {
        Title = "F10 Recorder"; Width = 900; Height = 820; MinWidth = 780; MinHeight = 620;
        Background = Ui.Background; Icon = Ui.TrayIcon(true); WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Panel = new SettingsPanel(recorder); Panel.CloseRequested = Dismiss; Content = Panel;
        Closing += (_, e) => { if (!exiting) { e.Cancel = true; Dismiss(); } };
        KeyDown += (_, e) => { if (OverlayVisible && e.Key == Key.Escape) { e.Handled = true; Dismiss(); } };
    }
    internal void ShowSettings()
    {
        if (!IsVisible) Show(); BringForward(); Panel.UpdateState(); VisibilityChanged?.Invoke();
    }
    void BringForward()
    {
        WindowState = WindowState.Normal;
        var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle != IntPtr.Zero) { Native.ShowWindow(handle, 9); Native.SetForegroundWindow(handle); } Activate();
    }
    internal void ToggleOverlay()
    {
        if (OverlayVisible) { Dismiss(); return; }
        previousWindow = Native.GetForegroundWindow(); wasVisible = IsVisible; oldPosition = Position; oldWidth = Width; oldHeight = Height;
        var area = Native.DisplayBounds(previousWindow);
        double scale = Screens.ScreenFromPoint(new PixelPoint(area.Left, area.Top))?.Scaling ?? 1;
        Hide(); OverlayVisible = true; WindowStartupLocation = WindowStartupLocation.Manual; WindowDecorations = WindowDecorations.None; ShowInTaskbar = false; Topmost = true;
        Width = Math.Min(900, (area.Width - 32) / scale); Height = Math.Min(948, (area.Height - 32) / scale);
        MinWidth = Math.Min(780, Width); MinHeight = Math.Min(620, Height);
        Position = new PixelPoint(area.Right - (int)(Width * scale) - 16, area.Top + (area.Height - (int)(Height * scale)) / 2);
        backdrop = new Window { Title = "F10 Recorder Overlay Backdrop", WindowDecorations = WindowDecorations.None, ShowInTaskbar = false, Topmost = true, ShowActivated = false, Background = Brushes.Black, Opacity = 0.48, Width = area.Width / scale, Height = area.Height / scale, Position = new PixelPoint(area.Left, area.Top), CanResize = false };
        backdrop.PointerPressed += (_, _) => Dismiss(); backdrop.Show(); Show(); BringForward(); VisibilityChanged?.Invoke();
    }
    internal void Dismiss()
    {
        if (!OverlayVisible) { Hide(); VisibilityChanged?.Invoke(); return; }
        var current = Native.GetForegroundWindow(); var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        bool restoreFocus = current == handle || current == backdrop?.TryGetPlatformHandle()?.Handle;
        Hide(); backdrop?.Close(); backdrop = null; OverlayVisible = false;
        Topmost = false; WindowDecorations = WindowDecorations.Full; ShowInTaskbar = true; MinWidth = 780; MinHeight = 620;
        Width = oldWidth; Height = oldHeight; Position = oldPosition;
        if (wasVisible) Show();
        if (restoreFocus && Native.IsWindow(previousWindow)) Native.SetForegroundWindow(previousWindow);
        VisibilityChanged?.Invoke();
    }
    internal void DismissOverlayForScreenshot()
    {
        if (OverlayVisible) Dismiss();
        // A previously visible Settings window is also excluded from the screenshot.
        if (IsVisible) Hide(); VisibilityChanged?.Invoke();
    }
    internal void PrepareExit() { exiting = true; backdrop?.Close(); backdrop = null; }
}
