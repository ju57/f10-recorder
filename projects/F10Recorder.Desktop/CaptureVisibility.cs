using Avalonia.Controls;

namespace F10Recorder;

internal static class CaptureVisibility
{
    static readonly List<WeakReference<Window>> windows = [];
    static bool exclude = true;
    internal static void Configure(Settings settings)
    {
        bool next = settings.CaptureMode != "window";
        if (exclude == next) return;
        exclude = next;
        windows.RemoveAll(reference => !reference.TryGetTarget(out _));
        foreach (var reference in windows) if (reference.TryGetTarget(out var window)) Apply(window);
    }
    static void Apply(Window window)
    {
        var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle != IntPtr.Zero) WindowStyles.SetCaptureExclusion(handle, exclude);
    }
    internal static void Exclude(Window window)
    {
        windows.Add(new WeakReference<Window>(window));
        // Apply before Show when the platform handle exists, and again after opening
        // for platforms that defer creation. This changes capture, not input or focus.
        Apply(window); window.Opened += (_, _) => Apply(window);
    }
}
