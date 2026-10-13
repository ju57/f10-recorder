using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;


namespace F10Recorder
{
    internal static class ApplicationFolders
    {
        internal static string FolderName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Desktop";
            var invalid = Path.GetInvalidFileNameChars();
            string safe = new string(name.Select(character => char.IsControl(character) || invalid.Contains(character) ? '_' : character).ToArray()).Trim().TrimEnd('.', ' ');
            if (safe.Length > 80) safe = safe.Substring(0, 80).TrimEnd('.', ' ');
            if (safe.Length > 0 && char.IsHighSurrogate(safe[safe.Length - 1])) safe = safe.Substring(0, safe.Length - 1);
            if (safe.Length == 0) return "Desktop";
            string stem = safe.Split('.')[0].TrimEnd(' ');
            if (Regex.IsMatch(stem, @"^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) safe = "_" + safe;
            return safe;
        }
        internal static string Destination(string root, string applicationName)
        {
            root = Path.GetFullPath(root); string directory = Path.GetFullPath(Path.Combine(root, FolderName(applicationName)));
            if (!string.Equals(Path.GetDirectoryName(directory).TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) throw new IOException("Application folder is outside the selected clips location.");
            return directory;
        }
        internal static string DisplayName(string executable, string processName)
        {
            try
            {
                if (!string.IsNullOrEmpty(executable))
                {
                    var version = FileVersionInfo.GetVersionInfo(executable);
                    if (UsableMetadata(version.FileDescription)) return version.FileDescription.Trim();
                    if (UsableMetadata(version.ProductName)) return version.ProductName.Trim();
                }
            }
            catch (Exception ex) { Log.Write("Application metadata unavailable: " + ex.Message); }
            if (!string.IsNullOrWhiteSpace(processName)) return processName;
            if (!string.IsNullOrEmpty(executable)) return Path.GetFileNameWithoutExtension(executable);
            return "Unknown application";
        }
        static bool UsableMetadata(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.IndexOf("operating system", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            return !new[] { "Unreal Engine", "UnrealEngine", "Unity", "Unity Player", "BootstrapPackagedGame" }.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase);
        }
        internal static string WindowApplication(IntPtr window, int excludedProcessId)
        {
            if (window == IntPtr.Zero || !Native.IsWindow(window)) return null;
            uint pid; Native.GetWindowThreadProcessId(window, out pid);
            if (pid == excludedProcessId) return null;
            var className = new StringBuilder(256); Native.GetClassName(window, className, className.Capacity);
            if (window == Native.GetShellWindow() || new[] { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd" }.Contains(className.ToString(), StringComparer.Ordinal)) return "Desktop";
            string executable = Native.ProcessImagePath(pid), processName = null;
            try { using (var process = Process.GetProcessById((int)pid)) processName = process.ProcessName; } catch (Exception) { }
            return DisplayName(executable, processName);
        }
    }
}
