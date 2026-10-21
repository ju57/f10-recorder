using F10Recorder;
using System.Text.Json;

string data = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Path.GetTempPath(), "F10Recorder-CoreChecks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(data); Log.DirectoryPath = data;
void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
var preferences = new Settings { OutputDirectory = Path.Combine(data, "clips", "..", "clips"), ScreenshotDirectory = Path.Combine(data, "screenshots") };
preferences.Validate();
Require(preferences.OutputDirectory == Path.GetFullPath(Path.Combine(data, "clips")), "Preference validation must normalize paths without an engine or platform initialization.");
foreach (var invalid in new[] {
    new Settings { FramesPerSecond = 999 },
    new Settings { SaveKey = 120, SaveModifiers = 6 },
    new Settings { CaptureMode = "unsupported" },
    new Settings { CaptureMode = "window", WindowExecutable = "relative.exe" },
    new Settings { ScreenshotDirectory = "" }
})
{
    bool rejected = false;
    try { invalid.Validate(); } catch (ArgumentException) { rejected = true; }
    Require(rejected, "Invalid recording preferences must be rejected through the ordinary validation interface without an engine.");
}
Console.WriteLine("Passed: direct portable preference validation and path normalization without a recording engine.");
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
Require(settings.CaptureMode == "monitor" && settings.WindowExecutable == "" && settings.WindowClass == "", "Old profiles must retain monitor mode without persisting transient target identity.");
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
AudioPacketChecks.Run(data);
// Save cutoffs use the existing media epoch, not copyts-relative display progress.
var saveEpoch = new MediaClock(7_000_000_000, 10_000_000, 1_800_000_000_000_000, 0);
var saveClock = new ReplaySaveClock(saveEpoch);
long triggerQpc = saveEpoch.QpcEpoch + 328_500_000;
double cutoff = saveClock.PositionAt(triggerQpc);
var mediaSegments = Enumerable.Range(0, 34).Select(i => new Segment { Path = i + ".ts", Start = 2.05 + i, End = 3.05 + i }).ToList();
var atTrigger = ReplaySelection.Select(mediaSegments, cutoff, 300);
Require(Math.Abs(cutoff - 32.85) < 1e-9 && atTrigger.Last().Start == 32.05, "A nonzero media epoch must select through the trigger's fractional media position.");
foreach (double displayOrigin in new[] { .3, 1.335, 2.6 })
{
    var fromDisplayProgress = ReplaySelection.Select(mediaSegments, cutoff - displayOrigin, 300);
    Require(cutoff - displayOrigin - fromDisplayProgress.First().Start < cutoff - atTrigger.First().Start, "A shifted display origin would trim an earlier endpoint even when it selects the same last segment.");
    Require(saveClock.PositionAt(triggerQpc) == cutoff, "Display-report startup offsets must not alter the frozen trigger cutoff.");
}
var utcStep = new ReplaySaveClock(new MediaClock(saveEpoch.QpcEpoch, saveEpoch.QpcFrequency, saveEpoch.UnixMicroseconds + 999_000_000, 0));
Require(utcStep.PositionAt(triggerQpc) == cutoff, "A later UTC clock step must not move a monotonic save trigger.");
var fullSegments = Enumerable.Range(0, 352).Select(i => new Segment { Path = i + ".ts", Start = i, End = i + 1 }).ToList();
double fullEnd = saveClock.PositionAt(saveEpoch.QpcEpoch + 3_503_000_000);
var fullSelection = ReplaySelection.Select(fullSegments, fullEnd, 300);
Require(fullSelection.First().Start == 51 && fullSelection.Last().Start == 350 && fullEnd - fullSelection.First().Start > 299 && fullEnd - fullSelection.First().Start <= 300, "A five-minute cutoff must preserve the one-keyframe start policy and the requested media endpoint.");
saveClock.StopAt(triggerQpc);
Parallel.For(1, 20, i => saveClock.StopAt(triggerQpc + i * 10_000_000));
Require(saveClock.PositionAt(triggerQpc + 9_000_000_000) == cutoff, "Retained stopped footage must freeze at the first stop request.");
Require(Math.Abs(saveClock.PositionAt(triggerQpc - 5_000_000) - 32.35) < 1e-9, "A pre-stop trigger must not move forward to stop time.");
var replacement = new ReplaySaveClock(new MediaClock(triggerQpc + 10_000_000, saveEpoch.QpcFrequency, saveEpoch.UnixMicroseconds, 0));
Require(replacement.PositionAt(triggerQpc) == 0 && replacement.PositionAt(triggerQpc + 20_000_000) == 1, "A stale trigger must not select a replacement session; its new epoch must start independently.");
Require(ReplaySelection.Select(mediaSegments, new ReplaySaveClock(saveEpoch).PositionAt(saveEpoch.QpcEpoch + 500_000), 300).Count == 0, "A trigger before the first eligible segment must remain warming up.");
File.WriteAllText(Path.Combine(data, "save-timeline.json"), JsonSerializer.Serialize(new { result = "passed", cutoff, fullEnd, first = fullSelection.First().Start, last = fullSelection.Last().Start, stopped = saveClock.PositionAt(triggerQpc + 9_000_000_000), staleTrigger = replacement.PositionAt(triggerQpc) }));

// Readiness is independent of enabled intent, saved footage and an in-flight save.
var minimized = ReplayPresentation.Describe(CapturePhase.TargetMinimized, "Restore the window.", false, false, true, false, false, true, 12);
Require(minimized.CanSave && !minimized.Healthy && minimized.Heading.Contains("minimized") && minimized.Detail.Contains("Previously buffered"), "A minimized target must retain save access without advertising live recording.");
foreach (var phase in new[] { CapturePhase.WaitingForTarget, CapturePhase.TargetClosed, CapturePhase.Recovering })
{
    var status = ReplayPresentation.Describe(phase, "Source unavailable.", false, false, false, false, false, true, 0);
    Require(!status.Healthy && !status.CanSave && !status.Detail.Contains("buffer is filling"), "An unavailable target must not be shown as filling or ready.");
}
var reconnecting = ReplayPresentation.Describe(CapturePhase.Reconnecting, "", true, true, true, false, false, true, 50);
Require(!reconnecting.CanSave && !reconnecting.Healthy && reconnecting.Detail.Contains("cannot be confirmed"), "A lost connection must override stale pause/readiness observations.");
var active = ReplayPresentation.Describe(CapturePhase.Buffering, "", false, true, true, false, false, false, 12.5);
Require(active.Healthy && active.CanSave && active.Detail.Contains("12 seconds") && active.Detail.Contains("shortcut unavailable"), "An unavailable shortcut must not hide valid button-based saving or exaggerate the buffer length.");
Require(!ReplayPresentation.Describe(CapturePhase.Buffering, "", false, true, true, true, false, true, 12).Healthy, "Applying settings must not claim live readiness.");
Require(!ReplayPresentation.Describe(CapturePhase.Buffering, "", false, true, true, false, true, true, 12).CanSave, "A save already in progress must disable a second save action.");
var notices = new List<string>();
var notifications = new RecorderNotifications((title, message, kind, duration) => notices.Add(title));
for (int i = 0; i < 10; i++) notifications.CaptureChanged(CapturePhase.WaitingForTarget, "No matching window.", false, false, settings, true);
Require(notices.Count == 1, "Polling a missing window must not repeatedly interrupt the user.");
notifications.CaptureChanged(CapturePhase.Buffering, "", true, false, settings, true);
notifications.CaptureChanged(CapturePhase.Buffering, "", true, false, settings, true);
Require(notices.Count == 2, "Readiness must be announced once after the target becomes available.");
notifications.CaptureChanged(CapturePhase.WaitingForTarget, "No matching window.", false, false, settings, true);
notifications.PauseChanged(true); notifications.PauseChanged(false);
notifications.CaptureChanged(CapturePhase.WaitingForTarget, "No matching window.", false, false, settings, true);
Require(notices.Last() == "Waiting for a window", "Resuming without a target must replace the preparing-buffer notice with the actual waiting state.");
string windowSettings = Path.Combine(data, "window-settings.json");
File.WriteAllText(windowSettings, legacy.TrimEnd('}') + ",\"CaptureMode\":\"window\",\"WindowExecutable\":\"\",\"WindowClass\":\"\"}");
var waiting = Settings.Load(windowSettings);
Require(waiting.CaptureMode == "window" && waiting.VideoCodec == "hevc_nvenc", "Missing window selection must wait while preserving the profile's other preferences.");
waiting.Save(windowSettings);
Require(!File.ReadAllText(windowSettings).Contains("Handle") && !File.ReadAllText(windowSettings).Contains("ProcessId"), "Transient HWND/process identities must not become reboot intent.");
File.WriteAllText(windowSettings, File.ReadAllText(windowSettings).Replace("\"CaptureMode\":\"window\"", "\"CaptureMode\":\"unsupported\""));
Require(Settings.Load(windowSettings).CaptureMode == "window", "An invalid mode must fail closed instead of capturing the desktop.");
File.WriteAllText(windowSettings, "{\"CaptureMode\":\"window\",\"WindowExecutable\":");
Require(Settings.Load(windowSettings).CaptureMode == "window", "A truncated window profile must not broaden capture to the desktop.");
Console.WriteLine("Passed: truthful replay status, retained-footage actions, notification suppression, and fail-closed window-intent migration.");
