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
// A late audio client starts at a nonzero position on the session timeline.
// GetBuffer's QPC is in100ns units even when Stopwatch has another frequency.
var mediaClock = new MediaClock(50_000, 1_000, 1_800_000_000_000_000, 0);
Require(mediaClock.SamplesAtQpc(50_400, 48_000) == 19_200, "Audio startup must preserve the session epoch instead of resetting to zero.");
Require(mediaClock.SamplesAtReferenceTime(501_250_000, 48_000) == 6_000, "WASAPI100ns timestamps must map to the same QPC epoch across frequencies.");
Require(mediaClock.SamplesAtReferenceTime(499_900_000, 48_000) == -480, "The initial packet can precede the session epoch and must be clipped by the timeline writer.");
Require(mediaClock.SamplesAtQpc(350_000, 44_100) == 13_230_000, "A five-minute timeline must retain sample-rate precision.");
Require(mediaClock.VideoTimestampFilter.Contains("PREV_OUTPTS-PREV_INPTS"), "Video must retain its acquisition intervals after its one-time epoch bridge.");
File.WriteAllText(Path.Combine(data, "clock-filter.txt"), mediaClock.VideoTimestampFilter);
foreach (string format in new[] { "u8", "s16le", "s24le", "s32le", "f32le" })
{
    int bytesPerSample = format == "u8" ? 1 : format == "s16le" ? 2 : format == "s24le" ? 3 : 4;
    var pcm = new byte[480 * 2 * bytesPerSample];
    for (int i = 0; i < pcm.Length; i++) pcm[i] = format == "f32le" ? BitConverter.GetBytes(.25f)[i % 4] : (byte)(format == "u8" ? 128 : 1);
    File.WriteAllBytes(Path.Combine(data, format + "-expected.pcm"), pcm.Concat(pcm).ToArray());
    foreach (bool delayed in new[] { false, true })
    {
        using var stream = File.Create(Path.Combine(data, format + (delayed ? "-delayed" : "-normal") + ".mka"));
        var transport = new TimestampedAudio(stream, 48_000, 2, format);
        if (delayed) Thread.Sleep(100);
        transport.Write(pcm, 0, pcm.Length, 24_000);
        transport.Write(pcm, 0, pcm.Length, 24_480);
    }
    Require(File.ReadAllBytes(Path.Combine(data, format + "-normal.mka")).SequenceEqual(File.ReadAllBytes(Path.Combine(data, format + "-delayed.mka"))), "Delivery delay must not change audio timestamps or samples.");
}
Console.WriteLine("Passed: replay boundaries, incomplete manifests, path containment, configuration migration, invalid-file preservation, and common audio/video epoch mapping.");
