using Avalonia;
using System.Security.Cryptography;
using System.Text;

namespace F10Recorder;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        string defaultData = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "F10Recorder")));
        string data = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Argument(args, "--data-dir") ?? defaultData));
        WindowsRecorder.Initialize(data);
        if (args.Contains("--ensure-media")) return MediaDependency.Install(data, !args.Contains("--no-download"));
        string suffix = !string.Equals(data, defaultData, StringComparison.OrdinalIgnoreCase) ? "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data.ToUpperInvariant())))[..16] : "";
        string controlTitle = HotkeyWindow.ControlTitle + suffix;
        using var mutex = new Mutex(true, @"Local\F10Recorder-Interactive-" + Environment.UserName + suffix, out bool first);
        if (!first)
        {
            int command = args.Contains("--quit") ? 4 : args.Contains("--pause") ? 2 : args.Contains("--resume") ? 3 : args.Contains("--overlay") ? 5 : args.Contains("--screenshot") ? 6 : 1;
            var window = Native.FindWindow(null, controlTitle);
            return window != IntPtr.Zero && Native.PostMessage(window, HotkeyWindow.CommandMessage, new IntPtr(command), IntPtr.Zero) ? 0 : 1;
        }
        App.DataDirectory = data; App.ControlTitle = controlTitle; App.Arguments = args;
        try { return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        catch (Exception ex) { Log.Write(ex.ToString()); return 1; }
    }
    static string Argument(string[] args, string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
