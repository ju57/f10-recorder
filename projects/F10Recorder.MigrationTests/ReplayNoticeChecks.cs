using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace F10Recorder;

internal static class ReplayNoticeChecks
{
    internal static int Run(string root)
    {
        if (Directory.Exists(root)) throw new IOException("A fresh replay notice root is required.");
        Directory.CreateDirectory(root);
        AppBuilder.Configure<Application>().UsePlatformDetect()
            .With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software] }).SetupWithoutStarting();
        Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
        Application.Current.Styles.Add(new FluentTheme()); Ui.AddStyles(Application.Current.Styles);
        // This short UI-only check never requests exclusion, which could disturb
        // another recorder even without capturing. State is local to this process.
        CaptureVisibility.Configure(new Settings { CaptureMode = "window" });
        bool passed = false; string error = null;
        using var done = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Dispatcher.UIThread.Post(async () =>
        {
            NoticeWindow window = null;
            try
            {
                window = new NoticeWindow();
                var notices = new RecorderNotifications((title, message, kind, duration) => window.Present(title, message, kind, duration, null));
                notices.SaveFinished("Replay-2026-10-31-00-00-00-000-fixture.mp4", true,
                    new RecorderMetrics { LastSaveLimitedHistory = true, LastSaveAvailableSeconds = 1.25, LastSaveRequestedSeconds = 1800 });
                await Task.Delay(250);
                string name = AutomationProperties.GetName(window);
                if (!name.Contains("1.25 s of 1800 s") || !name.Contains("Earlier history unavailable"))
                    throw new IOException("Warm-up feedback omitted available/requested duration or unavailable history.");
                var body = window.GetVisualDescendants().OfType<TextBlock>().Single(c => c.Text != null && c.Text.StartsWith("Saved 1.25"));
                if (body.TextLayout.TextLines.Count > body.MaxLines || body.Bounds.Height < body.TextLayout.Height - 1)
                    throw new IOException("Warm-up disclosure is clipped in the actual notification.");
                using (var image = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height), new Vector(96, 96)))
                { image.Render(window); image.Save(Path.Combine(root, "warm-up.png"), PngBitmapEncoderOptions.Default); }
                passed = true;
            }
            catch (Exception failure) { error = failure.ToString(); }
            finally { window?.Close(); done.Cancel(); }
        });
        Dispatcher.UIThread.MainLoop(done.Token);
        File.WriteAllText(Path.Combine(root, "verification.json"), JsonSerializer.Serialize(new { result = passed ? "passed" : "failed", error,
            effects = "One task-owned passive notice, software render of that notice, no capture exclusion request, desktop/device/audio capture, worker, engine, preferences/startup/vendor changes." }));
        return passed ? 0 : 1;
    }
}
