using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace F10Recorder;

internal sealed class SettingsWindow : Window
{
    internal SettingsPanel Panel { get; }
    internal OverlayPanel Overlay { get; }
    internal bool OverlayVisible => overlay?.IsVisible == true;
    internal Window OverlaySurface => overlay;
    internal Window OverlayBackdrop => backdrop;
    internal event Action VisibilityChanged;
    Window backdrop, overlay;
    bool wasVisible, exiting, dismissingOverlay;
    IntPtr previousWindow;
    public SettingsWindow(WindowsRecorder recorder)
    {
        Title = "F10 Recorder"; Width = 900; Height = 820; MinWidth = 780; MinHeight = 620;
        Background = Ui.Background; Icon = Ui.TrayIcon(true); WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Panel = new SettingsPanel(recorder); Panel.CloseRequested = Dismiss; Content = Panel;
        Overlay = new OverlayPanel(recorder, Dismiss, () => { Dismiss(); ShowSettings(); });
        CaptureVisibility.Exclude(this);
        Closing += (_, e) => { if (!exiting) { e.Cancel = true; Dismiss(); } };
    }
    internal void ShowSettings()
    {
        if (OverlayVisible) Dismiss();
        Content = Panel;
        if (!IsVisible) Show(); BringForward(this); UpdateState(); VisibilityChanged?.Invoke();
    }
    internal void UpdateState() { Panel.UpdateState(); Overlay.UpdateState(); }
    static void BringForward(Window window)
    {
        window.WindowState = WindowState.Normal;
        var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle != IntPtr.Zero) { Native.ShowWindow(handle, 9); Native.SetForegroundWindow(handle); } window.Activate();
    }
    internal void ToggleOverlay()
    {
        if (OverlayVisible) { Dismiss(); return; }
        previousWindow = Native.GetForegroundWindow(); wasVisible = IsVisible;
        var area = Native.DisplayBounds(previousWindow);
        double scale = Screens.ScreenFromPoint(new PixelPoint(area.Left, area.Top))?.Scaling ?? 1;
        Hide();
        overlay = new Window { Title = "F10 Recorder Replay Controls", WindowDecorations = WindowDecorations.None, ShowInTaskbar = false, Topmost = true, CanResize = false, Background = Ui.Background, WindowStartupLocation = WindowStartupLocation.Manual, Width = Math.Min(440, area.Width / scale), Height = area.Height / scale, Position = new PixelPoint(area.Left, area.Top), Content = Overlay };
        overlay.KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Dismiss(); } };
        overlay.Closing += (_, e) => { if (!dismissingOverlay) { e.Cancel = true; Dismiss(); } };
        UpdateState();
        backdrop = new Window { Title = "F10 Recorder Overlay Backdrop", WindowDecorations = WindowDecorations.None, ShowInTaskbar = false, Topmost = true, ShowActivated = false, Background = Brushes.Black, Opacity = 0.16, Width = area.Width / scale, Height = area.Height / scale, Position = new PixelPoint(area.Left, area.Top), CanResize = false };
        backdrop.PointerPressed += (_, e) => { if (e.GetCurrentPoint(backdrop).Properties.IsLeftButtonPressed) Dismiss(); };
        CaptureVisibility.Exclude(backdrop); CaptureVisibility.Exclude(overlay);
        backdrop.Show(); overlay.Show(); BringForward(overlay); Overlay.FocusInitial(); VisibilityChanged?.Invoke();
    }
    internal void Dismiss()
    {
        if (!OverlayVisible) { Hide(); VisibilityChanged?.Invoke(); return; }
        var current = Native.GetForegroundWindow(); var handle = overlay.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        bool restoreFocus = current == handle || current == backdrop?.TryGetPlatformHandle()?.Handle;
        var closing = overlay; overlay = null; closing.Content = null;
        dismissingOverlay = true;
        try { closing.Close(); } finally { dismissingOverlay = false; }
        backdrop?.Close(); backdrop = null;
        if (wasVisible)
        {
            // Restoring the editor must not activate it after the user switches away.
            // The previous foreground window is restored explicitly below when owned.
            bool activate = ShowActivated; ShowActivated = false;
            try { Show(); } finally { ShowActivated = activate; }
        }
        if (restoreFocus && Native.IsWindow(previousWindow)) Native.SetForegroundWindow(previousWindow);
        VisibilityChanged?.Invoke();
    }
    internal void DismissOverlayForScreenshot()
    {
        if (OverlayVisible) Dismiss();
        // A previously visible Settings window is also excluded from the screenshot.
        if (IsVisible) Hide(); VisibilityChanged?.Invoke();
    }
    internal void PrepareExit() { exiting = true; wasVisible = false; Dismiss(); }
}
