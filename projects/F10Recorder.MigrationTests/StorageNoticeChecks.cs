using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace F10Recorder;

internal static class StorageNoticeChecks
{
    internal static int Run(string root)
    {
        if (Directory.Exists(root)) throw new IOException("A fresh notice evidence directory is required.");
        Directory.CreateDirectory(root);
        bool passed = false; string failure = null; StorageNoticeStack stack = null;
        AppBuilder.Configure<Application>().UsePlatformDetect()
            .With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software] }).SetupWithoutStarting();
        Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
        Application.Current.Styles.Add(new FluentTheme()); Ui.AddStyles(Application.Current.Styles);
        using var done = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                stack = new StorageNoticeStack(); var notice = stack.Problem;
                const string reason = "Storage is unavailable. Check storage, then resume recording explicitly.";
                stack.Observe(true, false, null, reason, "first-hold");
                Require(notice.IsVisible, "Attaching to an already-held worker must reconstruct its persistent problem from current state.");
                Require(AutomationProperties.GetName(notice).Contains(reason), "Reconstructed notice lost the current hold reason.");
                await Task.Delay(6500);
                Require(notice.IsVisible, "Storage-stop problem auto-expired.");
                var button = notice.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Dismiss notification");
                Require(button.IsVisible && button.IsEnabled, "Persistent problem lacks its accessible individual dismiss control.");
                var icon = button.Content as Control;
                Require(icon != null, "Dismiss control must render a vector icon rather than text content.");
                var center = icon.TranslatePoint(new Point(icon.Bounds.Width / 2, icon.Bounds.Height / 2), button);
                Require(center.HasValue && Math.Abs(center.Value.X - button.Bounds.Width / 2) < 0.5
                    && Math.Abs(center.Value.Y - button.Bounds.Height / 2) < 0.5, "Dismiss icon is not centered in its actual button bounds.");
                long styles = WindowStyles.Get(notice.TryGetPlatformHandle().Handle, -20).ToInt64();
                Require((styles & 0x08000000) != 0 && (styles & 0x20) == 0, "Persistent problem must accept dismissal without automatic window activation.");
                stack.Present("Replay saved", "An already accepted save finished while storage remains held.", NotificationKind.Success, 150, null);
                Require(notice.IsVisible && stack.Transient.IsVisible && notice.Heading == "Replay recording paused for storage"
                    && stack.Transient.Position.Y > notice.Position.Y, "Accepted result replaced or covered the blocking problem.");
                await Task.Delay(450);
                Require(notice.IsVisible && !stack.Transient.IsVisible, "Transient expiry dismissed the independent storage problem.");
                stack.HideForScreenshot(); stack.Observe(true, true, null);
                stack.Present("Replay recording paused for storage", reason, NotificationKind.Warning, 0, null);
                Require(!notice.IsVisible, "Screenshot preparation left the problem visible.");
                stack.Observe(true, false, null);
                Require(notice.IsVisible && notice.Heading == "Replay recording paused for storage", "Screenshot preparation permanently dismissed the storage problem.");
                using (var image = new RenderTargetBitmap(new PixelSize((int)notice.Bounds.Width, (int)notice.Bounds.Height), new Vector(96, 96)))
                { image.Render(notice); image.Save(Path.Combine(root, "storage-problem.png"), PngBitmapEncoderOptions.Default); }
                stack.Present("Replay saved", "Saved-success notice keeps its finite lifetime.", NotificationKind.Success, 150, null);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(!notice.IsVisible && stack.Transient.IsVisible, "Individual problem dismissal changed an unrelated accepted result.");
                stack.Observe(true, false, null, "Updated reason for the same hold.", "first-hold");
                stack.Present("Replay recording paused for storage", reason, NotificationKind.Warning, 0, null);
                Require(!notice.IsVisible, "Polling or a duplicate event resurrected a dismissed hold.");
                await Task.Delay(450);
                Require(!stack.Transient.IsVisible, "The dismissed persistent problem made a later saved result permanent.");
                stack.Observe(true, false, null, reason, "second-hold");
                Require(notice.IsVisible, "A different hold was suppressed by the previous hold's dismissal.");
                stack.HideForScreenshot(); stack.Observe(false, true, null);
                stack.Observe(false, false, null);
                Require(!notice.IsVisible, "Screenshot restoration resurrected a resolved hold.");
                stack.Observe(true, true, null, reason, "third-hold");
                Require(!notice.IsVisible, "Attaching during screenshot preparation exposed the problem window.");
                stack.Observe(true, false, null, reason, "third-hold");
                Require(notice.IsVisible, "Current hold did not appear after screenshot preparation.");
                passed = true;
            }
            catch (Exception error) { failure = error.ToString(); }
            finally { stack?.Dispose(); done.Cancel(); }
        });
        Dispatcher.UIThread.MainLoop(done.Token);
        if (!passed && failure == null) failure = "Bounded native notice test deadline elapsed.";
        File.WriteAllText(Path.Combine(root, "verification.json"), JsonSerializer.Serialize(new { result = passed ? "passed" : "failed", error = failure,
            effects = "Only a task-owned non-activating notification window, software rendering and its own PNG/report. No recorder/worker, engine, desktop/audio capture, model, settings/startup write or installation." }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Storage notice native checks: " + (passed ? "passed" : "failed"));
        return passed ? 0 : 1;
    }
    static void Require(bool value, string reason) { if (!value) throw new IOException(reason); }
}
