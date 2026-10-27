using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace F10Recorder;

internal static class StorageControlsChecks
{
    internal static int Run(string data, string workerExecutable = null, string engineRoot = null)
    {
        Directory.CreateDirectory(data);
        string profilePath = Path.Combine(data, "profile");
        if (Directory.Exists(profilePath) || File.Exists(Path.Combine(data, "verification.json"))) throw new IOException("Storage checks require a fresh evidence directory.");
        Directory.CreateDirectory(profilePath);
        string settingsPath = Path.Combine(profilePath, "settings.json"), statePath = Path.Combine(profilePath, "worker-state.json");
        var original = new Settings { SystemAudio = false, DisplayIndex = 0, ReplayCacheBudgetBytes = 12_345_678_901,
            SaveKey = 115, SaveModifiers = 7, OverlayKey = 116, OverlayModifiers = 7, ScreenshotKey = 117, ScreenshotModifiers = 7 };
        original.AdditionalPreferences["PlaybackIdentity"] = JsonSerializer.SerializeToElement("unavailable-device");
        original.AdditionalPreferences["MicrophoneMuted"] = JsonSerializer.SerializeToElement(false);
        original.AdditionalPreferences["MicrophoneLevel"] = JsonSerializer.SerializeToElement(0);
        original.Save(settingsPath);
        File.WriteAllText(statePath, "{\"SchemaVersion\":1,\"Paused\":true,\"UserStopped\":true,\"ActivationRequired\":true,\"UnrelatedState\":0}");
        string oldRoot = Path.Combine(profilePath, "buffer"); Directory.CreateDirectory(oldRoot);
        string retained = Path.Combine(oldRoot, "retained.bin"), saved = Path.Combine(data, "existing.mp4"), picture = Path.Combine(data, "existing.png");
        foreach (string file in new[] { retained, saved, picture }) File.WriteAllText(file, "Preserved sentinel; not playable media: " + Path.GetFileName(file));
        File.WriteAllBytes(retained, new byte[8192]);
        var sentinelHashes = new[] { retained, saved, picture }.ToDictionary(p => p, Hash);
        string originalSettings = Hash(settingsPath), originalState = Hash(statePath);
        var report = new Dictionary<string, object>(); var checks = new List<string>();
        Process worker = null; long revision = 1; int commands = 0; bool reject = false;
        WorkerStatus Snapshot()
        {
            var p = RecordingProfile.Load(profilePath);
            return new WorkerStatus { Instance = "storage-controlled", Revision = ++revision, Config = p.Settings, ProfileStatus = p.Status,
                Paused = true, Phase = CapturePhase.Paused, Metrics = new RecorderMetrics { CacheBytes = -1 } }.ForClient();
        }
        WorkerReply Call(WorkerRequest request) => WorkerProtocol.CallAsync(profilePath, request).GetAwaiter().GetResult();
        try
        {
            if (workerExecutable != null)
            {
                var launch = new ProcessStartInfo(workerExecutable) { UseShellExecute = false, CreateNoWindow = true };
                foreach (string arg in new[] { "--profile-correction-worker", profilePath, "--ownership-root", Path.Combine(data, "ownership"), "--engine-root", engineRoot }) launch.ArgumentList.Add(arg);
                worker = Process.Start(launch) ?? throw new IOException("Paused test worker did not start.");
                report["workerPid"] = worker.Id;
            }
            WorkerStatus Current() => worker == null ? Snapshot() : Call(new WorkerRequest()).Status;
            var initial = Current();
            Require(initial.Paused && initial.ProfileStatus.ActivationRequired && initial.Metrics.CaptureProcessId == 0, "Worker must remain paused without capture.");
            Task<WorkerReply> Send(WorkerRequest request)
            {
                commands++; Require(request.Command == "apply", "Storage editing attempted a non-Apply command.");
                // Opaque cache history belongs to the worker, not a client draft.
                request.Settings.AdditionalPreferences["F10.ReplayCacheRoots.v1"] = JsonSerializer.SerializeToElement(123);
                if (reject) return Task.FromResult(new WorkerReply { Status = Current(), Error = "Controlled storage rejection; retained footage exceeds the budget." });
                if (worker != null) return Task.FromResult(Call(request));
                string error = null;
                try
                {
                    var p = RecordingProfile.Load(profilePath);
                    using var selection = ReplayCacheLocations.PinSelection(request.Settings);
                    request.Settings.AdditionalPreferences = new(p.Settings.AdditionalPreferences);
                    ReplayCacheLocations.PreservePrevious(request.Settings, p.Settings, profilePath);
                    p.Commit(request.Settings, p.State.Paused, p.State.UserStopped, true);
                }
                catch (Exception ex) { error = ex.Message; }
                var status = Snapshot(); status.CompletedApply = request.OperationId;
                return Task.FromResult(new WorkerReply { Status = status, Error = error });
            }
            AppBuilder.Configure<Application>().UsePlatformDetect().With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software] }).SetupWithoutStarting();
            Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
            Application.Current.Styles.Add(new FluentTheme()); Ui.AddStyles(Application.Current.Styles);
            using var done = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    using var client = new WorkerRecorder(initial, Send, profilePath);
                    string selected = null, lastTitle = null, lastCurrent = null;
                    var panel = new SettingsPanel(client, (title, current) => { lastTitle = title; lastCurrent = current; return Task.FromResult(selected); });
                    client.Changed += panel.UpdateState;
                    int closes = 0; panel.CloseRequested = () => closes++;
                    panel.SelectPage(3);
                    var defaults = new Settings();
                    Require(defaults.OutputDirectory == Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "F10 Recorder")
                        && defaults.ScreenshotDirectory == Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "F10 Recorder")
                        && defaults.ReplayCacheBudgetBytes == 10_000_000_000, "Accepted defaults changed.");
                    Require(Find<TextBox>(panel, "ReplayCacheBudgetGB").Text == GB(12_345_678_901), "Custom byte budget was rounded on entry.");
                    Require(Find<TextBox>(panel, "ReplayCacheDirectory").Text == oldRoot, "Historical cache location changed.");
                    checks.Add("defaults-and-exact-custom-budget");
                    foreach (string field in new[] { "OutputDirectory", "ScreenshotDirectory", "ReplayCacheDirectory" })
                    {
                        var text = Find<TextBox>(panel, field); var button = Find<Button>(panel, field + "Picker");
                        Require(text.Padding.Right >= 40 && AutomationProperties.GetName(button)?.StartsWith("Choose ") == true, "Folder command is not inset/accessible.");
                        string before = text.Text;
                        selected = null; Click(button);
                        Require(text.Text == before && lastCurrent == before, "Picker cancellation changed the draft or selected the wrong field.");
                        selected = Path.Combine(data, field); Directory.CreateDirectory(selected); Click(button);
                        Require(text.Text == selected && lastTitle.Contains(field == "OutputDirectory" ? "recordings" : field == "ScreenshotDirectory" ? "screenshots" : "replay cache"), "Picker routed to a different field.");
                        selected = "relative-invalid"; Click(button);
                        Require(text.Text != selected && !string.IsNullOrWhiteSpace(Find<TextBlock>(panel, field + "Error").Text), "Invalid picker result erased a valid draft.");
                    }
                    Require(commands == 0 && Hash(settingsPath) == originalSettings, "Folder selection wrote settings before Apply.");
                    checks.Add("three-picker-routing-cancel-invalid-and-no-write");
                    Find<TextBox>(panel, "ReplayCacheBudgetGB").Text = GB(10_000_000_001); Click(Find<Button>(panel, "ResetStorageEdits"));
                    Require(Find<TextBox>(panel, "ReplayCacheBudgetGB").Text == GB(12_345_678_901) && Find<TextBox>(panel, "OutputDirectory").Text == original.OutputDirectory
                        && Find<TextBox>(panel, "ReplayCacheDirectory").Text == oldRoot && commands == 0, "Reset changed disk or lost saved storage values.");
                    checks.Add("reset-restores-applied-values");
                    panel.SelectPage(0); var combo = Find<ComboBox>(panel, "VideoCodec");
                    var codec = new EncoderOption { Codec = original.VideoCodec, Name = "Unchanged saved codec; controlled UI selection" };
                    combo.ItemsSource = new[] { codec }; combo.SelectedItem = codec;
                    panel.SelectPage(3);
                    foreach (string invalid in new[] { "", "0", "-1", "NaN", GB(1) + "1", "9223372037" })
                    {
                        Find<TextBox>(panel, "ReplayCacheBudgetGB").Text = invalid;
                        Require(!await panel.ApplyAsync() && commands == 0 && closes == 0 && Hash(settingsPath) == originalSettings
                            && !string.IsNullOrWhiteSpace(Find<TextBlock>(panel, "ReplayCacheBudgetError").Text), "Invalid budget reached persistence: " + invalid);
                    }
                    Find<TextBox>(panel, "ReplayCacheBudgetGB").Text = GB(1_000_000);
                    Require(!await panel.ApplyAsync() && commands == 0 && Hash(settingsPath) == originalSettings, "Impossible budget was accepted.");
                    checks.Add("invalid-and-impossible-budget-rejection");
                    Find<TextBox>(panel, "ReplayCacheBudgetGB").Text = GB(10_000_000_001);
                    Find<TextBox>(panel, "OutputDirectory").Text = "";
                    Require(!await panel.ApplyAsync() && commands == 0 && !string.IsNullOrWhiteSpace(Find<TextBlock>(panel, "OutputDirectoryError").Text), "Empty folder lacked inline feedback.");
                    string newClips = Path.Combine(data, "future-clips"), newPictures = Path.Combine(data, "future-pictures"), newCache = Path.Combine(data, "selected-cache");
                    Directory.CreateDirectory(newCache);
                    Find<TextBox>(panel, "OutputDirectory").Text = newClips; Find<TextBox>(panel, "ScreenshotDirectory").Text = newPictures; Find<TextBox>(panel, "ReplayCacheDirectory").Text = newCache;
                    reject = true;
                    Require(!await panel.ApplyAsync() && Hash(settingsPath) == originalSettings && closes == 0 && Find<TextBox>(panel, "ReplayCacheBudgetGB").Text == GB(10_000_000_001)
                        && Find<TextBox>(panel, "OutputDirectory").Text == newClips, "Rejected worker reply discarded the draft or rewrote settings.");
                    reject = false; checks.Add("rejected-worker-reply-preserves-draft-and-disk");
                    Find<TextBox>(panel, "ReplayCacheDirectory").Text = Path.Combine(data, "missing-cache-folder");
                    Require(!await panel.ApplyAsync() && Hash(settingsPath) == originalSettings && closes == 0 && !string.IsNullOrWhiteSpace(Find<TextBlock>(panel, "ReplayCacheDirectoryError").Text), "Unavailable folder was saved or lost its inline error.");
                    Find<TextBox>(panel, "ReplayCacheDirectory").Text = newCache;
                    checks.Add("worker-rejects-unavailable-cache-folder-before-write");
                    Require(await panel.ApplyAsync() && closes == 1, "Valid Apply failed: " + panel.Error);
                    var persisted = RecordingProfile.Load(profilePath);
                    Require(persisted.Settings.OutputDirectory == newClips && persisted.Settings.ScreenshotDirectory == newPictures && persisted.Settings.ReplayCacheDirectory == newCache
                        && persisted.Settings.ReplayCacheBudgetBytes == 10_000_000_001, "Storage values did not persist exactly.");
                    Require(persisted.State.Paused && persisted.State.UserStopped && persisted.State.ActivationRequired && !persisted.Settings.SystemAudio && persisted.Settings.DisplayIndex == 0
                        && !persisted.Settings.AdditionalPreferences["MicrophoneMuted"].GetBoolean() && persisted.Settings.AdditionalPreferences["MicrophoneLevel"].GetInt32() == 0, "Apply lost explicit false/zero, opaque preferences or pause.");
                    checks.Add("Apply-persists-storage-and-preserves-intent");
                    Require(persisted.Settings.AdditionalPreferences["F10.ReplayCacheRoots.v1"].ValueKind == JsonValueKind.Array, "Client-supplied opaque cache history replaced the worker's history.");
                    checks.Add("worker-owns-cache-history-despite-client-opaque-input");
                    var roots = ReplayCacheLocations.Roots(persisted.Settings, profilePath);
                    Require(roots.Contains(oldRoot) && roots.Any(p => p.StartsWith(newCache + Path.DirectorySeparatorChar)), "Cache move forgot retained locations.");
                    string otherProfile = Path.Combine(data, "other-profile"); Directory.CreateDirectory(otherProfile);
                    Require(ReplayCacheLocations.Current(persisted.Settings, otherProfile) != ReplayCacheLocations.Current(persisted.Settings, profilePath), "Different profiles share a cache directory.");
                    using (var retainedCache = new ReplayCache(retainedDirectories: roots))
                    {
                        Require(retainedCache.Status.CacheBytes > 0, "Retained bytes vanished after a move.");
                        bool refused = false; try { retainedCache.ValidateBudget(1); } catch (IOException) { refused = true; }
                        Require(refused, "Location change bypassed retained-cache budget admission.");
                    }
                    checks.Add("cache-profile-isolation-and-retained-accounting");
                    // Older versions could preserve the custom path as opaque
                    // while still writing the historical default cache.
                    using (var legacyCache = new ReplayCache(retainedDirectories: ReplayCacheLocations.Roots(new Settings { ReplayCacheDirectory = newCache }, profilePath)))
                    {
                        Require(legacyCache.Status.CacheBytes > 0, "Custom cache without history omitted retained default bytes.");
                        bool refused = false; try { legacyCache.ValidateBudget(1); } catch (IOException) { refused = true; }
                        Require(refused, "A legacy custom cache bypassed default-root debt.");
                    }
                    checks.Add("legacy-custom-path-without-history-keeps-default-debt");
                    string pipelineProfile = Path.Combine(data, "paused-pipeline"); Directory.CreateDirectory(pipelineProfile);
                    var beforeDirectories = Directory.GetDirectories(newCache).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    using (var pausedPipeline = new ReplayEngine(new Settings { ReplayCacheDirectory = newCache, SystemAudio = false }, pipelineProfile, startPaused: true))
                    {
                        Require(pausedPipeline.CaptureProcessId == 0 && Directory.GetDirectories(newCache).Except(beforeDirectories, StringComparer.OrdinalIgnoreCase).Count() == 1,
                            "Paused production pipeline ignored the selected cache folder or launched capture.");
                    }
                    checks.Add("production-paused-pipeline-uses-selected-folder-without-capture");
                    using var reopened = new WorkerRecorder(Current(), Send, profilePath);
                    var reopenedPanel = new SettingsPanel(reopened); reopenedPanel.SelectPage(3);
                    Require(Find<TextBox>(reopenedPanel, "ReplayCacheBudgetGB").Text == GB(10_000_000_001) && Find<TextBox>(reopenedPanel, "ReplayCacheDirectory").Text == newCache
                        && Find<TextBox>(reopenedPanel, "OutputDirectory").Text == newClips, "Reopened editor lost applied fields.");
                    checks.Add("reconnect-and-reopen");
                    Require(new[] { retained, saved, picture }.All(file => Hash(file) == sentinelHashes[file]), "Storage edits changed existing media/cache.");
                    var state = RecordingProfile.Load(profilePath).State;
                    Require(state.AdditionalState["UnrelatedState"].GetInt32() == 0 && (worker == null || Hash(statePath) == originalState), "Unrelated state was lost.");
                    Require(!panel.GetLogicalDescendants().OfType<TextBlock>().Any(b => b.Text?.Contains("Drive space") == true), "Removed dashboard returned.");
                    checks.Add("media-and-state-preservation-no-dashboard");
                    var fixtureWindow = new Window { Content = panel, Width = 980, Height = 980, ShowInTaskbar = false };
                    var storage = (Control)Find<ContentControl>(panel, "ProfileEditor").Content;
                    void Prepare(Control control)
                    {
                        control.ApplyStyling(); control.ApplyTemplate();
                        if (control is ContentPresenter presenter) presenter.UpdateChild();
                        foreach (var child in control.GetVisualChildren().OfType<Control>()) Prepare(child);
                    }
                    Prepare(fixtureWindow); Prepare(storage); storage.Measure(new Size(760, 880)); storage.Arrange(new Rect(0, 0, 760, 880));
                    Require(storage.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Replay cache budget" && t.Bounds.Height > 0), "Offscreen Storage controls were not rendered.");
                    foreach (string field in new[] { "OutputDirectory", "ScreenshotDirectory", "ReplayCacheDirectory" }) Require(Find<Button>(panel, field + "Picker").Bounds.Width == 36, "Inset picker was not laid out.");
                    using var bitmap = new RenderTargetBitmap(new PixelSize(760, 880)); bitmap.Render(storage); bitmap.Save(Path.Combine(data, "storage.png"), PngBitmapEncoderOptions.Default);
                    Require(!fixtureWindow.IsVisible, "Render fixture must never be shown."); fixtureWindow.Content = null; fixtureWindow.Close();
                    report["result"] = "passed";
                }
                catch (Exception ex) { report["result"] = "failed"; report["error"] = ex.ToString(); }
                finally { done.Cancel(); }
            });
            Dispatcher.UIThread.MainLoop(done.Token);
            if (worker != null)
            {
                Require(Current().Paused && Current().Metrics.CaptureProcessId == 0, "Integration worker started capture.");
                Call(new WorkerRequest { Command = "quit" }); Require(worker.WaitForExit(10000), "Owned worker did not exit."); report["workerExit"] = worker.ExitCode;
            }
        }
        catch (Exception ex) { report["result"] = "failed"; report["error"] = ex.ToString(); }
        finally
        {
            if (worker != null) { if (!worker.HasExited) { worker.Kill(); worker.WaitForExit(5000); report["forcedOwnedCleanup"] = true; } report["cleanupVerified"] = worker.HasExited; worker.Dispose(); }
            report["cases"] = checks; report["commands"] = commands; report["mode"] = workerExecutable == null ? "controlled transport and real profile persistence" : "actual paused named-pipe worker";
            File.WriteAllText(Path.Combine(data, "verification.json"), JsonSerializer.Serialize(report));
        }
        Console.WriteLine(JsonSerializer.Serialize(report));
        return report.TryGetValue("result", out var value) && (string)value == "passed" ? 0 : 1;
    }
    static string GB(long bytes) => (bytes / 1_000_000_000m).ToString("0.#########", CultureInfo.CurrentCulture);
    static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    static T Find<T>(Control panel, string name) where T : Control => panel.GetLogicalDescendants().OfType<T>().Single(c => c.Name == name);
    static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
