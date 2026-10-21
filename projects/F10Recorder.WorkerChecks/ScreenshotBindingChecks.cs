using System.Diagnostics;
using System.Text.Json;
using System.Drawing;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;
namespace F10Recorder;
// Explicit opt-in: owned-window screenshots, up to four minutes of HWND churn,
// no audio/desktop capture, startup writes, input injection or excluded UI.
// The non-pumping schedule tests a continuity fence, not ordinary event latency.
internal static class ScreenshotBindingChecks
{
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetPropW", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern IntPtr GetProp(IntPtr hwnd, string name);
    static string Marker(WindowCaptureTarget tracker) => (string)typeof(WindowCaptureTarget).GetField("screenshotProperty", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(tracker);
    static void Wait(string path, int seconds = 270) { var t = Stopwatch.StartNew(); while (!File.Exists(path) && t.Elapsed.TotalSeconds < seconds) Thread.Sleep(10); if (!File.Exists(path)) throw new Exception("Timed out: " + path); }
    static T NoDispatch<T>(Func<T> action) { var task = Task.Run(action); while (!task.IsCompleted) Thread.Sleep(10); return task.GetAwaiter().GetResult(); }
    static object Snapshot(WindowTargetSnapshot s) => new { s.Handle, s.ProcessId, s.ProcessStartedUtcTicks, s.Generation, s.Available };
    static object Pixel(string path) { using var image = new Bitmap(path); var c = image.GetPixel(image.Width / 2, image.Height / 2); return new { c.R, c.G, c.B, image.Width, image.Height }; }
    internal static int Run(string data, string engine)
    {
        if (Directory.Exists(data) && Directory.EnumerateFileSystemEntries(data).Any()) throw new IOException("Use a new empty evidence directory.");
        TrustedStorage.CreatePrivateDirectory(data); RecorderHost.Initialize(data); MediaDependency.RootDirectory = engine;
        string fixture = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath), "F10ScreenshotFixture-" + Guid.NewGuid().ToString("N") + ".exe");
        File.Copy(Environment.ProcessPath, fixture);
        var report = new Dictionary<string, object>(); Process child = null; WindowCaptureTarget tracker = null;
        try
        {
            var info = new ProcessStartInfo(fixture) { UseShellExecute = false, CreateNoWindow = true }; info.ArgumentList.Add("--screenshot-binding-fixture"); info.ArgumentList.Add(data); child = Process.Start(info); Wait(Path.Combine(data, "ready.json"));
            using var ready = JsonDocument.Parse(File.ReadAllText(Path.Combine(data, "ready.json"))); long hwnd = ready.RootElement.GetProperty("handle").GetInt64();
            var choice = WindowCaptureTarget.Inspect(new IntPtr(hwnd)); if (choice == null) throw new Exception("Fixture inspection failed");
            tracker = new WindowCaptureTarget(new Settings { CaptureMode = "window", WindowExecutable = choice.Executable, WindowClass = choice.ClassName });
            var original = tracker.ReadForScreenshot(); report["original"] = Snapshot(original);
            string marker = Marker(tracker); report["initialMarker"] = GetProp(new IntPtr(hwnd), marker).ToInt64();
            var ordinaryInitial = tracker.Read(); report["ordinaryInitial"] = Snapshot(ordinaryInitial);
            using (var second = new WindowCaptureTarget(new Settings { CaptureMode = "window", WindowExecutable = choice.Executable, WindowClass = choice.ClassName }))
            {
                var secondSnapshot = second.ReadForScreenshot(); string secondMarker = Marker(second);
                if (secondMarker == marker || GetProp(new IntPtr(hwnd), marker) != new IntPtr(1)) throw new Exception("Trackers share identity property");
                second.Dispose(); report["independentTrackersAndDisposal"] = GetProp(new IntPtr(hwnd), secondMarker) == IntPtr.Zero && GetProp(new IntPtr(hwnd), marker) == new IntPtr(1);
            }
            Forms.Application.DoEvents();
            if (!original.Available) throw new Exception("Fixture unavailable");
            string source = CaptureSession.WindowVideoSource(original, 1).Replace(":capture_cursor=1", ":capture_cursor=0");
            string control = NoDispatch(() => ScreenshotCapture.CaptureSource(source, Path.Combine(data, "control"), true, () => ScreenshotCapture.SameTarget(original, tracker.ReadForScreenshot()))); report["control"] = new { path = control, pixel = Pixel(control) };
            File.WriteAllText(Path.Combine(data, "cycle"), "cycle"); Wait(Path.Combine(data, "cycled.json")); using var cycled = JsonDocument.Parse(File.ReadAllText(Path.Combine(data, "cycled.json"))); report["reuse"] = cycled.RootElement.Clone();
            if (!cycled.RootElement.GetProperty("exact").GetBoolean()) { report["result"] = "inconclusive-no-exact-reuse"; return 2; }
            // Intentionally withhold message pumping on the WinEvent owner thread:
            // controlled delayed-event scheduling, not a frequency claim.
            report["oldMarkerAfterActualReuse"] = GetProp(new IntPtr(hwnd), marker).ToInt64();
            var ordinaryBefore = NoDispatch(() => tracker.Read()); report["ordinaryBeforePump"] = Snapshot(ordinaryBefore);
            if (!ScreenshotCapture.SameTarget(ordinaryInitial, ordinaryBefore)) { report["result"] = "inconclusive-destroy-event-already-delivered"; return 2; }
            var before = NoDispatch(() => tracker.ReadForScreenshot()); report["beforeDelayedEventPump"] = Snapshot(before);
            report["sameTargetBeforePump"] = ScreenshotCapture.SameTarget(original, before);
            try { string replacement = NoDispatch(() => ScreenshotCapture.CaptureSource(source, Path.Combine(data, "replacement"), true, () => ScreenshotCapture.SameTarget(original, tracker.ReadForScreenshot()))); report["replacement"] = new { path = replacement, pixel = Pixel(replacement) }; report["publishedReplacement"] = true; }
            catch (Exception ex) { report["publishedReplacement"] = false; report["captureError"] = ex.ToString(); }
            report["replacementPublishedPngs"] = Directory.GetFiles(Path.Combine(data, "replacement"), "*.png").Length;
            Forms.Application.DoEvents(); var current = tracker.ReadForScreenshot(); report["afterEventPump"] = Snapshot(current);
            string nextSource = CaptureSession.WindowVideoSource(current, 1).Replace(":capture_cursor=1", ":capture_cursor=0");
            string later = NoDispatch(() => ScreenshotCapture.CaptureSource(nextSource, Path.Combine(data, "later-legitimate"), true, () => ScreenshotCapture.SameTarget(current, tracker.ReadForScreenshot())));
            report["laterLegitimate"] = new { path = later, pixel = Pixel(later) };
            string finalMarker = Marker(tracker); tracker.Dispose(); report["finalMarkerCleaned"] = GetProp(new IntPtr(hwnd), finalMarker) == IntPtr.Zero;
            if ((bool)report["publishedReplacement"] || (int)report["replacementPublishedPngs"] != 0
             || (bool)report["sameTargetBeforePump"] || (long)report["oldMarkerAfterActualReuse"] != 0
             || !(bool)report["independentTrackersAndDisposal"] || !(bool)report["finalMarkerCleaned"])
                throw new Exception("Screenshot lifetime regression.");
            using (var white = new Bitmap(control)) using (var black = new Bitmap(later))
            {
                if (white.GetPixel(white.Width / 2, white.Height / 2).R < 240 || black.GetPixel(black.Width / 2, black.Height / 2).R > 15) throw new Exception("Fixture pixel identities were not established.");
            }
            report["scope"] = "Owned-window screenshots only, deliberately delayed WinEvent delivery; no ordinary-timing, performance, hostile-owner or desktop/audio claim.";
            report["result"] = "passed"; return 0;
        }
        catch (Exception ex) { report["result"] = "failed"; report["error"] = ex.ToString(); return 1; }
        finally { tracker?.Dispose(); File.WriteAllText(Path.Combine(data, "stop"), "stop"); if (child != null) { if (!child.WaitForExit(5000)) { child.Kill(); child.WaitForExit(); } child.Dispose(); } File.Delete(fixture); File.WriteAllText(Path.Combine(data, "verification.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true })); }
    }
}

internal static class ScreenshotBindingFixture
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowExW(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool PatBlt(IntPtr dc, int x, int y, int w, int h, uint op);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern bool TextOutW(IntPtr dc, int x, int y, string text, int length);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int show);
    [DllImport("user32.dll")] static extern bool UpdateWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool PeekMessage(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG msg);
    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public UIntPtr wParam; public IntPtr lParam; public uint time; public int x, y; public uint extra; }
    static void Pump() { while (PeekMessage(out var m, IntPtr.Zero, 0, 0, 1)) { TranslateMessage(ref m); DispatchMessage(ref m); } }
    static IntPtr New(bool white) => CreateWindowExW(0x08000000, "STATIC", "F10 owned binding fixture", (white ? 0x00CF0000u : 0x80000000u) | (white ? 6u : 4u), 60, 60, 320, 240, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
    internal static void Run(string data)
    {
        Directory.CreateDirectory(data); IntPtr current = New(true), original = current;
        if (current == IntPtr.Zero) throw new Exception("CreateWindow failed");
        ShowWindow(current, 4); UpdateWindow(current);
        File.WriteAllText(Path.Combine(data, "ready.json"), JsonSerializer.Serialize(new { handle = current.ToInt64(), pid = Environment.ProcessId }));
        var clock = Stopwatch.StartNew(); bool cycled = false;
        try
        {
            while (clock.Elapsed.TotalSeconds < 360)
            {
                Pump();
                var dc = GetDC(current); if (dc != IntPtr.Zero) { PatBlt(dc, 0, 0, 320, 240, cycled ? 0x00000042u : 0x00FF0062u); string text = clock.ElapsedMilliseconds.ToString(); TextOutW(dc, 10, 10, text, text.Length); ReleaseDC(current, dc); }
                if (!cycled && File.Exists(Path.Combine(data, "cycle")))
                {
                    // Let capture teardown settle while the original still owns
                    // its handle slot. Allocation by other Windows components can
                    // otherwise take that slot; such a run is inconclusive.
                    Thread.Sleep(1000);
                    DestroyWindow(current); current = IntPtr.Zero; var cycle = Stopwatch.StartNew(); int count = 0, slotMatches = 0; bool exact = false;
                    for (; count < 80000 && cycle.Elapsed.TotalSeconds < 240; count++)
                    {
                        current = New(false); if (current == IntPtr.Zero) throw new Exception("Recreate failed");
                        if ((current.ToInt64() & 65535) == (original.ToInt64() & 65535)) slotMatches++;
                        else break;
                        if (current == original) { exact = true; break; }
                        DestroyWindow(current); current = IntPtr.Zero;
                    }
                    if (current == IntPtr.Zero) current = New(false);
                    SetWindowLongPtr(current, -16, new IntPtr(0x00CF0004)); SetWindowPos(current, IntPtr.Zero, 60, 60, 320, 240, 0x34); ShowWindow(current, 4); UpdateWindow(current); Pump(); cycled = true;
                    File.WriteAllText(Path.Combine(data, "cycled.json"), JsonSerializer.Serialize(new { exact, original = original.ToInt64(), replacement = current.ToInt64(), iterations = count + 1, slotMatches, milliseconds = cycle.Elapsed.TotalMilliseconds, pid = Environment.ProcessId }));
                }
                if (File.Exists(Path.Combine(data, "stop"))) break;
                Thread.Sleep(16);
            }
            return;
        }
        finally { if (current != IntPtr.Zero) DestroyWindow(current); }
    }
}
