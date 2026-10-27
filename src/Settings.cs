using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Generic;

namespace F10Recorder
{
    public sealed class Settings
    {
        public int SchemaVersion = 1;
        [JsonExtensionData] public Dictionary<string, JsonElement> AdditionalPreferences = new Dictionary<string, JsonElement>();
        public int ReplayMinutes = 5;
        public long ReplayCacheBudgetBytes = 10_000_000_000;
        // Empty preserves the profile's historical buffer directory.
        public string ReplayCacheDirectory = "";
        public int FramesPerSecond = 60;
        public int BitrateMbps = 20;
        public string VideoCodec = "h264_nvenc";
        public bool SystemAudio = true;
        public int DisplayIndex = -1;
        // Missing in older profiles means monitor capture. Never persist an HWND.
        public string CaptureMode = "monitor";
        public string WindowExecutable = "", WindowClass = "";
        public string OutputDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "F10 Recorder");
        public int SaveKey = 121; // VK_F10
        public int SaveModifiers = 0;
        public int OverlayKey = 90; // Z
        public int OverlayModifiers = 1; // Alt
        public int ScreenshotKey = 112; // VK_F1
        public int ScreenshotModifiers = 1; // Alt
        public string ScreenshotDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "F10 Recorder");

        public void Validate()
        {
            foreach (var field in StoredFields) { string issue = FieldIssue(field.Name); if (issue != null) throw new ArgumentException(issue); }
            // A necessary video-only lower bound, not calibrated admission headroom.
            long videoBytes = ReplayMinutes * 60L * BitrateMbps * 1_000_000 / 8;
            if (videoBytes > ReplayCacheBudgetBytes) throw new ArgumentException("Replay-cache budget cannot hold even the nominal video history. Increase the budget or explicitly change replay length/bitrate. Audio, container and burst margins remain uncalibrated.");
            if (SaveKey == OverlayKey && SaveModifiers == OverlayModifiers) throw new ArgumentException("Use different shortcuts for saving and opening the overlay.");
            if (ScreenshotKey == SaveKey && ScreenshotModifiers == SaveModifiers || ScreenshotKey == OverlayKey && ScreenshotModifiers == OverlayModifiers) throw new ArgumentException("Use different shortcuts for screenshots, replays, and the overlay.");
            OutputDirectory = Path.GetFullPath(OutputDirectory);
            ScreenshotDirectory = Path.GetFullPath(ScreenshotDirectory);
            if (ReplayCacheDirectory.Length > 0) ReplayCacheDirectory = Path.GetFullPath(ReplayCacheDirectory);
        }
        internal static readonly FieldInfo[] StoredFields = typeof(Settings).GetFields(BindingFlags.Instance | BindingFlags.Public).Where(f => f.Name != nameof(AdditionalPreferences)).ToArray();
        internal string FieldIssue(string field)
        {
            switch (field)
            {
                case nameof(SchemaVersion): return SchemaVersion == 1 ? null : "Unsupported settings schema version.";
                case nameof(ReplayMinutes): return ReplayMinutes >= 1 && ReplayMinutes <= 30 ? null : "Replay length must be 1–30 minutes.";
                case nameof(ReplayCacheBudgetBytes): return ReplayCacheBudgetBytes > 0 ? null : "Replay-cache budget must be a positive number of decimal bytes.";
                case nameof(ReplayCacheDirectory):
                    try { if (ReplayCacheDirectory != null && (ReplayCacheDirectory.Length == 0 || !string.IsNullOrWhiteSpace(ReplayCacheDirectory) && Path.IsPathFullyQualified(ReplayCacheDirectory))) { if (ReplayCacheDirectory.Length > 0) Path.GetFullPath(ReplayCacheDirectory); return null; } }
                    catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { }
                    return "Choose an absolute replay-cache folder.";
                case nameof(FramesPerSecond): return SupportedFrameRates.Contains(FramesPerSecond) ? null : "Choose 24, 30, 60, or 120 FPS.";
                case nameof(BitrateMbps): return BitrateMbps >= 5 && BitrateMbps <= 100 ? null : "Bitrate must be 5–100 Mbps.";
                case nameof(VideoCodec): return VideoCodec == "h264_nvenc" || VideoCodec == "hevc_nvenc" || VideoCodec == "av1_nvenc" ? null : "Choose H.264, HEVC, or AV1.";
                case nameof(DisplayIndex): return DisplayIndex >= -1 && DisplayIndex <= 31 ? null : "Invalid display index.";
                case nameof(CaptureMode): return CaptureMode == "monitor" || CaptureMode == "window" ? null : "Choose monitor or window capture.";
                case nameof(WindowExecutable):
                    try { return WindowExecutable != null && WindowExecutable.Length <= 32768 && (WindowExecutable.Length == 0 || Path.IsPathRooted(WindowExecutable) && Path.GetFullPath(WindowExecutable) == WindowExecutable) ? null : "Choose an application window with an absolute executable path."; }
                    catch (ArgumentException) { return "Invalid window executable path."; }
                case nameof(WindowClass): return WindowClass != null && WindowClass.Length <= 256 ? null : "Invalid window class.";
                case nameof(SaveKey): return SaveKey >= 112 && SaveKey <= 123 ? null : "Choose an F1–F12 replay shortcut.";
                case nameof(OverlayKey): case nameof(ScreenshotKey):
                    int key = field == nameof(OverlayKey) ? OverlayKey : ScreenshotKey;
                    return key >= 65 && key <= 90 || key >= 112 && key <= 123 ? null : "Choose a letter or F1–F12 shortcut.";
                case nameof(SaveModifiers): return SaveModifiers >= 0 && SaveModifiers <= 7 ? null : "Invalid replay shortcut modifiers.";
                case nameof(OverlayModifiers): case nameof(ScreenshotModifiers):
                    int modifiers = field == nameof(OverlayModifiers) ? OverlayModifiers : ScreenshotModifiers;
                    return modifiers >= 1 && modifiers <= 7 ? null : "Choose at least one shortcut modifier.";
                case nameof(OutputDirectory): case nameof(ScreenshotDirectory):
                    string path = field == nameof(OutputDirectory) ? OutputDirectory : ScreenshotDirectory;
                    try { if (!string.IsNullOrWhiteSpace(path)) { Path.GetFullPath(path); return null; } }
                    catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { }
                    return "Choose a valid save folder.";
                default: return null;
            }
        }
        public static Settings Load(string path) => ProfileDocuments.ReadSettings(path).Value;
        public void Save(string path) { Validate(); ProfileDocuments.WriteSettings(path, this); }
        public string HotkeyText
        {
            get { return ((SaveModifiers & 2) != 0 ? "Ctrl+" : "") + ((SaveModifiers & 1) != 0 ? "Alt+" : "") + ((SaveModifiers & 4) != 0 ? "Shift+" : "") + "F" + (SaveKey - 111); }
        }
        public string OverlayHotkeyText { get { return ((OverlayModifiers & 2) != 0 ? "Ctrl+" : "") + ((OverlayModifiers & 1) != 0 ? "Alt+" : "") + ((OverlayModifiers & 4) != 0 ? "Shift+" : "") + (OverlayKey >= 112 ? "F" + (OverlayKey - 111) : ((char)OverlayKey).ToString()); } }
        public string ScreenshotHotkeyText { get { return ((ScreenshotModifiers & 2) != 0 ? "Ctrl+" : "") + ((ScreenshotModifiers & 1) != 0 ? "Alt+" : "") + ((ScreenshotModifiers & 4) != 0 ? "Shift+" : "") + (ScreenshotKey >= 112 ? "F" + (ScreenshotKey - 111) : ((char)ScreenshotKey).ToString()); } }
        public static readonly int[] SupportedFrameRates = { 24, 30, 60, 120 };
        public string SegmentFormat { get { return VideoCodec == "av1_nvenc" ? "matroska" : "mpegts"; } }
        public string SegmentExtension { get { return VideoCodec == "av1_nvenc" ? ".mkv" : ".ts"; } }
    }
    public static class Log
    {
        static readonly object Gate = new object();
        public static string DirectoryPath;
        public static string FileName = "recorder.log";
        public static void Write(string message)
        {
            lock (Gate) { try { string path = Path.Combine(DirectoryPath, FileName); if (File.Exists(path) && new FileInfo(path).Length > 2000000) File.Move(path, path + "." + DateTime.UtcNow.Ticks + ".old"); foreach (string old in Directory.GetFiles(DirectoryPath, FileName + ".*.old")) if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddDays(-2)) File.Delete(old); File.AppendAllText(path, DateTime.Now.ToString("O") + " " + message + Environment.NewLine); } catch { } }
        }
    }
}
