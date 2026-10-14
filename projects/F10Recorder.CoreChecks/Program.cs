using F10Recorder;
using System.Text.Json;

string data = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Path.GetTempPath(), "F10Recorder-CoreChecks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(data); Log.DirectoryPath = data;
void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
var segments = Enumerable.Range(0, 12).Select(i => new Segment { Path = i + ".ts", Start = i, End = i + 1 }).ToList();
var selected = ReplaySelection.Select(segments, 9.4, 5);
Require(selected.Select(s => s.Start).SequenceEqual(new double[] { 5, 6, 7, 8, 9 }), "Replay selection must start on a full keyframe interval and exclude future footage.");
string manifest = Path.Combine(data, "segments.csv"); File.WriteAllText(manifest, "\"chunk,quoted.ts\",0,1\nchunk-2.ts,1,2");
Require(ReplaySelection.Read(manifest, data).Count == 1, "An unfinished manifest line must not be saved.");
File.WriteAllText(manifest, "../outside.ts,0,1\n"); bool traversalRejected = false;
try { ReplaySelection.Read(manifest, data); } catch (IOException) { traversalRejected = true; }
Require(traversalRejected, "A manifest must not escape its session directory.");
string settingsFile = Path.Combine(data, "settings.json");
string legacy = JsonSerializer.Serialize(new { ReplayMinutes = 5, FramesPerSecond = 60, BitrateMbps = 20, VideoCodec = "hevc_nvenc", SystemAudio = false, DisplayIndex = -1, OutputDirectory = Path.Combine(data, "clips"), ScreenshotDirectory = Path.Combine(data, "screenshots"), SaveKey = 121, SaveModifiers = 6, OverlayKey = 120, OverlayModifiers = 6, ScreenshotKey = 119, ScreenshotModifiers = 6, FfmpegPath = "obsolete-configured-engine", HotkeyText = "Ctrl+Shift+F10" });
File.WriteAllText(settingsFile, legacy); var settings = Settings.Load(settingsFile);
Require(settings.VideoCodec == "hevc_nvenc" && !settings.SystemAudio && settings.ReplayMinutes == 5 && settings.OverlayHotkeyText == "Ctrl+Shift+F9", "Existing settings were not preserved by the serializer migration.");
settings.Save(settingsFile); Require(!File.ReadAllText(settingsFile).Contains("FfmpegPath"), "Engine paths must stay out of recording preferences.");
var previousInvalidFiles = Directory.GetFiles(data, "settings.json.invalid-*").ToHashSet();
string invalidLegacy = legacy.Replace("\"FramesPerSecond\":60", "\"FramesPerSecond\":999");
File.WriteAllText(settingsFile, invalidLegacy);
settings = Settings.Load(settingsFile);
var newInvalidFiles = Directory.GetFiles(data, "settings.json.invalid-*").Where(p => !previousInvalidFiles.Contains(p)).ToArray();
Require(settings.FramesPerSecond == 60 && newInvalidFiles.Length == 1 && File.ReadAllText(newInvalidFiles[0]) == invalidLegacy, "Invalid settings must be preserved before falling back to defaults.");
Console.WriteLine("Passed: replay boundaries, incomplete manifests, path containment, legacy configuration migration, and invalid-file preservation.");
