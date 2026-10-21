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
        data = WorkerIdentity.DataPath(data);
        defaultData = WorkerIdentity.DataPath(defaultData);
        if (args.Contains("--ensure-media")) return MediaDependency.Install(data, !args.Contains("--no-download"));
        if (args.Contains("--recorder-worker"))
        {
            try { return RecorderWorker.Run(data, args.Contains("--paused")); }
            catch (Exception ex) { Log.Write(ex.ToString()); return 1; }
        }
        string suffix = !string.Equals(data, defaultData, StringComparison.OrdinalIgnoreCase) ? "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data.ToUpperInvariant())))[..16] : "";
        string controlTitle = HotkeyWindow.ControlTitle + suffix;
        if (args.Contains("--quit") || args.Contains("--pause") || args.Contains("--resume"))
        {
            if (args.Contains("--quit"))
            {
                var ui = Native.FindWindow(null, controlTitle + ".UI");
                if (ui != IntPtr.Zero) return Native.PostMessage(ui, HotkeyWindow.CommandMessage, new IntPtr(4), IntPtr.Zero) ? 0 : 1;
                if (Native.FindWindow(null, controlTitle) == IntPtr.Zero) return 0;
            }
            try
            {
                if (!args.Contains("--quit")) WorkerProtocol.EnsureWorker(data, args.Contains("--pause"), attachUi: false);
                using var deadline = new CancellationTokenSource(args.Contains("--quit") ? 40000 : 5000);
                var reply = WorkerProtocol.CallAsync(data, new WorkerRequest { Command = args.Contains("--quit") ? "quit" : "pause", Value = args.Contains("--pause") }, deadline.Token).GetAwaiter().GetResult();
                return reply.Error == null ? 0 : 1;
            }
            catch (OperationCanceledException) { return args.Contains("--quit") ? 0 : 1; }
            catch (IOException ex) { Log.Write(ex.ToString()); return 1; }
        }
        using var ownership = ProfileOwnership.TryAcquireUi(data, out string boundData);
        if (boundData != null) data = boundData;
        Log.DirectoryPath = data;
        suffix = !string.Equals(data, defaultData, StringComparison.OrdinalIgnoreCase) ? "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data.ToUpperInvariant())))[..16] : "";
        controlTitle = HotkeyWindow.ControlTitle + suffix;
        if (ownership == null)
        {
            int command = args.Contains("--quit") ? 4 : args.Contains("--pause") ? 2 : args.Contains("--resume") ? 3 : args.Contains("--overlay") ? 5 : args.Contains("--screenshot") ? 6 : 1;
            var window = Native.FindWindow(null, controlTitle + ".UI");
            return window != IntPtr.Zero && Native.PostMessage(window, HotkeyWindow.CommandMessage, new IntPtr(command), IntPtr.Zero) ? 0 : 1;
        }
        App.DataDirectory = data; App.ControlTitle = controlTitle; App.Arguments = args;
        try { return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        catch (Exception ex) { Log.Write(ex.ToString()); return 1; }
    }
    static string Argument(string[] args, string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
