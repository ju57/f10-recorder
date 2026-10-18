using Avalonia.Controls;

namespace F10Recorder;

internal static class CaptureVisibility
{
    internal static void Exclude(Window window)
    {
        void Apply()
        {
            var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (handle != IntPtr.Zero) WindowStyles.ExcludeFromCapture(handle);
        }
        // Apply before Show when the platform handle exists, and again after opening
        // for platforms that defer creation. This changes capture, not input or focus.
        Apply(); window.Opened += (_, _) => Apply();
    }
}
