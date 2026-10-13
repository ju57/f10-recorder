using System;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace F10Recorder
{
    public sealed class Settings
    {
        public int ReplayMinutes = 5;
        public int FramesPerSecond = 60;
        public int BitrateMbps = 20;
        public string VideoCodec = "h264_nvenc";
        public bool SystemAudio = true;
        public int DisplayIndex = -1;
        public string OutputDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "F10 Recorder");
        internal string FfmpegPath { get { return RecorderRuntime.ResolveExecutable(); } }
        public int SaveKey = 121; // VK_F10
        public int SaveModifiers = 6; // Ctrl + Shift
        public int OverlayKey = 120; // VK_F9
        public int OverlayModifiers = 6; // Ctrl + Shift
        public int ScreenshotKey = 119; // VK_F8
        public int ScreenshotModifiers = 6; // Ctrl + Shift
        public string ScreenshotDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "F10 Recorder");

        public void Validate()
        {
            ValidateValues();
            if (!File.Exists(FfmpegPath)) throw new FileNotFoundException("Run the installer to repair F10 Recorder's media engine.");
        }
        void ValidateValues()
        {
            if (ReplayMinutes < 1 || ReplayMinutes > 30) throw new ArgumentException("Replay length must be 1–30 minutes.");
            if (!SupportedFrameRates.Contains(FramesPerSecond)) throw new ArgumentException("Choose 24, 30, 60, or 120 FPS.");
            if (BitrateMbps < 5 || BitrateMbps > 100) throw new ArgumentException("Bitrate must be 5–100 Mbps.");
            if (VideoCodec != "h264_nvenc" && VideoCodec != "hevc_nvenc" && VideoCodec != "av1_nvenc") throw new ArgumentException("Choose H.264, HEVC, or AV1.");
            if (DisplayIndex < -1 || DisplayIndex > 31) throw new ArgumentException("Invalid display index.");
            if (SaveKey < 112 || SaveKey > 123 || SaveModifiers < 0 || SaveModifiers > 7) throw new ArgumentException("Choose an F1–F12 hotkey with optional modifiers.");
            if ((OverlayKey < 65 || OverlayKey > 90) && (OverlayKey < 112 || OverlayKey > 123) || OverlayModifiers < 1 || OverlayModifiers > 7) throw new ArgumentException("Choose an overlay shortcut with a letter or F1–F12 and at least one modifier.");
            if (SaveKey == OverlayKey && SaveModifiers == OverlayModifiers) throw new ArgumentException("Use different shortcuts for saving and opening the overlay.");
            if ((ScreenshotKey < 65 || ScreenshotKey > 90) && (ScreenshotKey < 112 || ScreenshotKey > 123) || ScreenshotModifiers < 1 || ScreenshotModifiers > 7) throw new ArgumentException("Choose a screenshot shortcut with a letter or F1–F12 and at least one modifier.");
            if (ScreenshotKey == SaveKey && ScreenshotModifiers == SaveModifiers || ScreenshotKey == OverlayKey && ScreenshotModifiers == OverlayModifiers) throw new ArgumentException("Use different shortcuts for screenshots, replays, and the overlay.");
            OutputDirectory = Path.GetFullPath(OutputDirectory);
            if (string.IsNullOrWhiteSpace(ScreenshotDirectory)) throw new ArgumentException("Choose a folder for screenshots.");
            ScreenshotDirectory = Path.GetFullPath(ScreenshotDirectory);
        }
        public static Settings Load(string path)
        {
            if (!File.Exists(path)) return new Settings();
            try { var result = new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(path)); if (result == null) throw new FormatException("Empty settings."); result.ValidateValues(); return result; }
            catch (Exception ex) { File.Copy(path, path + ".invalid-" + DateTime.UtcNow.Ticks); Log.Write("Settings could not be loaded; original preserved. " + ex.Message); return new Settings(); }
        }
        public void Save(string path)
        {
            string temp = path + ".tmp";
            File.WriteAllText(temp, new JavaScriptSerializer().Serialize(this));
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        }
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
        public static void Write(string message)
        {
            lock (Gate) { try { string path = Path.Combine(DirectoryPath, "recorder.log"); if (File.Exists(path) && new FileInfo(path).Length > 2000000) File.Move(path, path + "." + DateTime.UtcNow.Ticks + ".old"); foreach (string old in Directory.GetFiles(DirectoryPath, "recorder.log.*.old")) if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddDays(-2)) File.Delete(old); File.AppendAllText(path, DateTime.Now.ToString("O") + " " + message + Environment.NewLine); } catch { } }
        }
    }
}
