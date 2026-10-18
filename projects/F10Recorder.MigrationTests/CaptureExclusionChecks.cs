using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia.Controls;
using Forms = System.Windows.Forms;

namespace F10Recorder;

internal static class CaptureExclusionChecks
{
    [DllImport("user32.dll", SetLastError = true)] static extern bool SetWindowDisplayAffinity(nint window, uint affinity);
    [DllImport("user32.dll", SetLastError = true)] static extern bool GetWindowDisplayAffinity(nint window, out uint affinity);
    static nint Handle(Window window) => window.TryGetPlatformHandle()?.Handle ?? 0;
    static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    static void Affinity(Window window, uint value)
    {
        Require(SetWindowDisplayAffinity(Handle(window), value), "Could not set controlled display affinity: " + Marshal.GetLastWin32Error());
        CheckAffinity(window, value);
    }
    static void CheckAffinity(Window window, uint value) => Require(GetWindowDisplayAffinity(Handle(window), out uint actual) && actual == value, "Unexpected capture affinity for " + window.Title);
    static void CheckPixels(string path, (int x, int y)[] points, bool excluded, int tolerance)
    {
        using var image = new Bitmap(path);
        foreach (var (x, y) in points)
        {
            int matching = 0;
            for (int dx = -2; dx <= 2; dx++) for (int dy = -2; dy <= 2; dy++)
            {
                var c = image.GetPixel(x + dx, y + dy);
                if (Math.Abs(c.R - 32) <= tolerance && Math.Abs(c.G - 72) <= tolerance && Math.Abs(c.B - 52) <= tolerance) matching++;
            }
            Require(excluded ? matching == 25 : matching < 5, (excluded ? "UI/black/dim pixels remain" : "Positive control did not capture UI") + " at " + x + "," + y + " in " + path);
        }
    }
    static async Task<string> Still(string root) => await Task.Run(() => ScreenshotCapture.Capture(-1, root));
    static async Task<string> Clip(string root)
    {
        return await Task.Run(() =>
        {
            var config = new Settings { ReplayMinutes = 1, FramesPerSecond = 30, BitrateMbps = 5, SystemAudio = false };
            using var session = new CaptureSession(config, Path.Combine(root, "cache"), false);
            for (int i = 0; i < 12; i++) { System.Threading.Thread.Sleep(500); session.Maintain(); Require(!session.HasExited, session.ErrorTail); }
            return session.Save(session.Position, Path.Combine(root, "clips"));
        });
    }
    static async Task<string> Frame(string clip, string output)
    {
        await Task.Run(() =>
        {
            var start = new ProcessStartInfo(MediaDependency.ResolveExecutable()) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (string arg in new[] { "-hide_banner", "-v", "error", "-nostdin", "-n", "-ss", "2", "-i", clip, "-frames:v", "1", output }) start.ArgumentList.Add(arg);
            using var job = new Native.ProcessJob(); using var process = Process.Start(start); job.Add(process);
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(15000)) { process.Kill(); throw new TimeoutException("Frame extraction timed out."); }
            Require(process.ExitCode == 0 && error.Result.Length == 0, "Frame extraction failed: " + error.Result);
        });
        return output;
    }
    internal static async Task<object> Run(App app, string data, Action activateFixture)
    {
        var root = Path.Combine(data, "capture-exclusion"); Directory.CreateDirectory(root);
        var bounds = Forms.Screen.PrimaryScreen.Bounds;
        Require(bounds.Width >= 1280 && bounds.Height >= 720, "Capture exclusion check needs a 1280x720 primary display.");
        var evidence = new Dictionary<string, object>();
        try
        {
            app.OpenSettings(); await Task.Delay(250); CheckAffinity(app.SettingsWindow, 0x11);
            var editorPoint = new[] { (app.SettingsWindow.Position.X - bounds.Left + 40, app.SettingsWindow.Position.Y - bounds.Top + 340) };
            Affinity(app.SettingsWindow, 0); await Task.Delay(250);
            string editorControl = await Still(Path.Combine(root, "settings-control")); CheckPixels(editorControl, editorPoint, false, 0);
            Affinity(app.SettingsWindow, 0x11); await Task.Delay(250);
            string editorExcluded = await Still(Path.Combine(root, "settings-excluded")); CheckPixels(editorExcluded, editorPoint, true, 0);
            evidence["settingsControl"] = editorControl; evidence["settingsExcluded"] = editorExcluded;
            app.SettingsWindow.Dismiss(); activateFixture(); app.ToggleOverlay();
            app.Notice.Present("Capture exclusion check", "Visible controls must leave the underlying picture intact.", NotificationKind.Success, 0, app.SettingsWindow);
            await Task.Delay(350);
            var surfaces = new[] { app.SettingsWindow.OverlaySurface, app.SettingsWindow.OverlayBackdrop, app.Notice };
            foreach (var surface in surfaces) { Require(surface.IsVisible, "Expected visible surface: " + surface.Title); CheckAffinity(surface, 0x11); Affinity(surface, 0); }
            var points = new[] { (40, 350), (bounds.Width / 2, bounds.Height - 100), (bounds.Width - 150, 160) };
            await Task.Delay(250);
            string control = await Still(Path.Combine(root, "overlay-control")); CheckPixels(control, points, false, 0);
            string controlClip = await Clip(Path.Combine(root, "overlay-control"));
            foreach (var surface in surfaces) Affinity(surface, 0x11);
            await Task.Delay(350);
            string excluded = await Still(Path.Combine(root, "overlay-excluded")); CheckPixels(excluded, points, true, 0);
            string excludedClip = await Clip(Path.Combine(root, "overlay-excluded"));
            // Decode only after both controlled capture windows have ended.
            CheckPixels(await Frame(controlClip, Path.Combine(root, "control-video.png")), points, false, 4);
            CheckPixels(await Frame(excludedClip, Path.Combine(root, "excluded-video.png")), points, true, 4);
            evidence["overlayControl"] = control; evidence["overlayExcluded"] = excluded; evidence["controlClip"] = controlClip; evidence["excludedClip"] = excludedClip;
            evidence["result"] = "passed"; evidence["scope"] = "Visible Settings/sidebar/backdrop/notice, real Desktop Duplication PNG and H264 replay, controlled fixture; no game/performance claim.";
            return evidence;
        }
        finally
        {
            app.Notice.Dismiss(); app.SettingsWindow.Dismiss();
            Affinity(app.SettingsWindow, 0x11); activateFixture();
        }
    }
}
