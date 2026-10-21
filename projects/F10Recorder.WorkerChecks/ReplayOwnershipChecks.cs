using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace F10Recorder;

// These are file/ownership fixtures, not playable media. No CaptureSession,
// RecorderHost, encoder, window, shortcut or dependency provisioning is started.
internal static class ReplayOwnershipChecks
{
    static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    static async Task<ReplaySaveResult> Finish(SaveSubmission save)
    {
        Require(save.Accepted, save.Rejection);
        return await save.Completion.WaitAsync(Deadline);
    }

    sealed class Source : IReplaySessionSource
    {
        internal readonly long Epoch;
        readonly ReplaySaveClock clock;
        public ReplaySessionInfo ReplayInfo { get; }
        public bool Stopped { get; private set; }
        public string Diagnostics => "controlled source";
        internal bool Disposed;
        internal int ExportCount;
        internal Action ExportAction;
        internal readonly TaskCompletionSource ExportEntered = Signal();
        internal readonly TaskCompletionSource StopEntered = Signal();
        internal TaskCompletionSource ExportRelease;
        internal string[] Arguments, Concat;
        internal string Contents;
        internal int OutputBytes = 2048;
        internal bool MissingOutput;

        internal Source(string directory, string codec = "h264_nvenc", bool audio = false, double age = 200)
        {
            Directory.CreateDirectory(directory);
            string extension = codec == "av1_nvenc" ? ".mkv" : ".ts";
            ReplayInfo = new(directory, Path.Combine(directory, "segments.csv"), 60, 5, 60, codec, extension, audio, audio);
            Epoch = Stopwatch.GetTimestamp() - (long)(Stopwatch.Frequency * age);
            clock = new(new MediaClock(Epoch, Stopwatch.Frequency, 0, 0));
        }
        internal long Trigger(double seconds) => Epoch + (long)(seconds * Stopwatch.Frequency);
        internal string Chunk(int number) => Path.Combine(ReplayInfo.Directory, "chunk-" + number.ToString("D9") + ReplayInfo.Extension);
        internal void Segments(int first, int count, string[] labels = null, bool finalNewline = true)
        {
            var lines = new List<string>();
            for (int i = 0; i < count; i++)
            {
                int n = first + i;
                File.WriteAllText(Chunk(n), labels == null ? "picture-" + n : labels[i]);
                lines.Add(Path.GetFileName(Chunk(n)) + "," + n.ToString(CultureInfo.InvariantCulture) + "," + (n + 1).ToString(CultureInfo.InvariantCulture));
            }
            File.WriteAllText(ReplayInfo.Manifest, string.Join("\n", lines) + (finalNewline ? "\n" : ""));
        }
        public double SaveCutoffAt(long timestamp) => clock.PositionAt(timestamp);
        public void FreezeAt(long timestamp) => clock.StopAt(timestamp);
        public void StopCapture() { Stopped = true; StopEntered.TrySetResult(); }
        public void CheckCaptureHealth(double end) { Require(!Disposed, "Maintenance reached a disposed source."); }
        public void Export(IReadOnlyList<string> arguments, string snapshot)
        {
            Require(!Disposed, "Export reached a disposed source.");
            ExportCount++;
            Arguments = arguments.ToArray();
            Concat = File.ReadAllLines(Path.Combine(snapshot, "concat.txt"));
            Contents = string.Join("|", Concat.Where(s => s.StartsWith("file '")).Select(s => File.ReadAllText(Path.Combine(snapshot, s[6..^1]))));
            ExportEntered.TrySetResult();
            ExportRelease?.Task.WaitAsync(Deadline).GetAwaiter().GetResult();
            Require(!Disposed && Directory.Exists(snapshot), "Retirement removed export resources.");
            ExportAction?.Invoke();
            if (!MissingOutput) File.WriteAllBytes(arguments[^1], new byte[OutputBytes]);
        }
        public void Dispose() { Require(Stopped, "Resources released before acquisition stopped."); Disposed = true; }
    }

    sealed class Files : ReplayFiles
    {
        internal Action<string, string> BeforeCopy;
        internal bool RealHardLinks, FailPublish, FailCleanup;
        internal int Copies, LinkAttempts, LinkError;
        internal readonly ConcurrentQueue<string> Cleaned = new();
        internal override bool TryHardLink(string copy, string original)
        {
            LinkAttempts++;
            if (!RealHardLinks) return false;
            bool linked = base.TryHardLink(copy, original);
            if (!linked) LinkError = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            return linked;
        }
        internal override void Copy(string original, string copy) { Copies++; BeforeCopy?.Invoke(original, copy); base.Copy(original, copy); }
        internal override void Publish(string partial, string final)
        {
            if (FailPublish) throw new IOException("controlled publication failure");
            base.Publish(partial, final);
        }
        internal override void DeleteFile(string path)
        {
            Cleaned.Enqueue(path);
            if (FailCleanup) throw new IOException("controlled cleanup failure");
            base.DeleteFile(path);
        }
        internal override void DeleteDirectory(string path)
        {
            Cleaned.Enqueue(path);
            if (FailCleanup) throw new UnauthorizedAccessException("controlled directory cleanup failure");
            base.DeleteDirectory(path);
        }
    }

    sealed class ManualTime : TimeProvider
    {
        long ticks = 1;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => ticks;
        internal void Advance() => ticks += 1000;
    }

    sealed class Fixture : IDisposable
    {
        internal readonly string Root, Destination, Existing;
        internal readonly Files Files = new();
        internal readonly ConcurrentQueue<string> Diagnostics = new();
        internal readonly Source Source;
        internal readonly ReplayCache Cache;
        internal readonly ReplaySessionId Id;
        internal Fixture(string root, string codec = "h264_nvenc", bool audio = false,
            TimeProvider time = null, Func<Task> wait = null)
        {
            Root = root; Destination = Path.Combine(root, "clips"); Directory.CreateDirectory(Destination);
            Existing = Path.Combine(Destination, "existing.mp4"); File.WriteAllText(Existing, "preserve this saved video");
            Source = new(Path.Combine(root, "session-fixture"), codec, audio);
            Source.Segments(0, 3);
            Cache = new(Files, time, wait, Diagnostics.Enqueue); Id = Cache.RegisterSession(Source);
        }
        internal SaveSubmission Save(double end = 3) => Cache.TrySave(Id, Source.Trigger(end), Destination);
        public void Dispose()
        {
            Source.ExportRelease?.TrySetResult();
            Cache.Dispose();
            Require(File.ReadAllText(Existing) == "preserve this saved video", "Existing saved media changed.");
        }
    }

    internal static int Run(string root) => RunAsync(root).GetAwaiter().GetResult();
    static async Task<int> RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        Log.DirectoryPath = root;
        var results = new List<object>();
        int failed = 0;
        async Task Check(string name, Func<string, Task> check)
        {
            try { await check(Path.Combine(root, name)); results.Add(new { name, result = "passed" }); }
            catch (Exception ex) { failed++; results.Add(new { name, result = "failed", error = ex.ToString() }); }
        }

        await Check("pins-prune-retire", async path =>
        {
            using var f = new Fixture(path);
            var copying = Signal(); var resume = Signal();
            f.Source.ExportRelease = Signal();
            f.Files.BeforeCopy = (_, _) => { if (f.Files.Copies == 1) { copying.TrySetResult(); resume.Task.WaitAsync(Deadline).GetAwaiter().GetResult(); } };
            var save = f.Save();
            try
            {
                await copying.Task.WaitAsync(Deadline);
                f.Source.Segments(100, 3); f.Cache.Maintain(f.Id);
                Require(Enumerable.Range(0, 3).All(i => File.Exists(f.Source.Chunk(i))), "Pruning removed pinned originals during copy.");
                resume.SetResult(); await f.Source.ExportEntered.Task.WaitAsync(Deadline);
                f.Cache.Maintain(f.Id);
                Require(Enumerable.Range(0, 3).All(i => !File.Exists(f.Source.Chunk(i))), "Original pins leaked after snapshot creation.");
                var retirement = f.Cache.RetireSessionAsync(f.Id);
                await f.Source.StopEntered.Task.WaitAsync(Deadline);
                Require(!retirement.IsCompleted && !f.Source.Disposed && Directory.Exists(f.Source.ReplayInfo.Directory), "Retirement did not wait for export.");
                Require(!f.Save().Accepted, "Retiring session accepted another save.");
                f.Source.ExportRelease.SetResult(); var result = await Finish(save); await retirement.WaitAsync(Deadline);
                Require(result.Succeeded && File.Exists(result.File), "Protected export failed.");
                Require(f.Source.Contents == "picture-0|picture-1|picture-2", "Export lost its original snapshot.");
                Require(f.Source.Disposed && !Directory.Exists(f.Source.ReplayInfo.Directory), "Retirement did not reclaim the session.");
            }
            finally { resume.TrySetResult(); }
        });

        await Check("copy-failure-recovery", async path =>
        {
            using var f = new Fixture(path);
            f.Files.BeforeCopy = (_, _) => { if (f.Files.Copies == 2) throw new IOException("controlled second copy failure"); };
            var result = await Finish(f.Save());
            Require(!result.Succeeded && f.Source.ExportCount == 0 && !f.Cache.Status.Saving, "Copy failure left a save active or started export.");
            Require(!Directory.GetDirectories(f.Source.ReplayInfo.Directory, "save-*").Any(), "Failed snapshot was not cleaned.");
            f.Source.Segments(100, 3); f.Cache.Maintain(f.Id);
            Require(Enumerable.Range(0, 3).All(i => !File.Exists(f.Source.Chunk(i))), "Copy failure leaked pins.");
            f.Files.BeforeCopy = null;
            Require((await Finish(f.Save(103))).Succeeded, "A later save could not recover.");
        });

        foreach (string failure in new[] { "start", "timeout", "exit", "missing", "short", "publish" })
            await Check("export-failure-" + failure, async path =>
            {
                using var f = new Fixture(path);
                f.Source.ExportAction = () =>
                {
                    File.WriteAllBytes(f.Source.Arguments[^1], new byte[2048]);
                    if (failure == "start") throw new IOException("controlled start failure");
                    if (failure == "timeout") throw new TimeoutException("controlled export timeout");
                    if (failure == "exit") throw new IOException("controlled nonzero exit");
                    if (failure == "missing") File.Delete(f.Source.Arguments[^1]);
                };
                f.Source.MissingOutput = failure == "missing"; f.Source.OutputBytes = failure == "short" ? 10 : 2048;
                f.Files.FailPublish = failure == "publish";
                var result = await Finish(f.Save());
                Require(!result.Succeeded && !f.Cache.Status.Saving && f.Source.ExportCount == 1, "Failure was successful, busy, or automatically retried.");
                Require(Directory.GetFiles(f.Destination).SequenceEqual(new[] { f.Existing }), "Failure published a final file or left a partial output.");
                Require(!Directory.GetDirectories(f.Source.ReplayInfo.Directory, "save-*").Any(), "Export failure left a snapshot.");
                f.Source.ExportAction = null; f.Source.MissingOutput = false; f.Source.OutputBytes = 2048; f.Files.FailPublish = false;
                Require((await Finish(f.Save())).Succeeded, "Failure prevented a new save.");
            });

        await Check("cleanup-separate-from-result", async path =>
        {
            using var f = new Fixture(path);
            f.Files.FailCleanup = true;
            var result = await Finish(f.Save());
            Require(result.Succeeded && File.Exists(result.File), "Cleanup reversed publication success.");
            Require(f.Diagnostics.Any(s => s.Contains("cleanup failed")), "Cleanup failure was not diagnosed.");
            f.Source.ExportAction = () => throw new IOException("primary export error");
            var failure = await Finish(f.Save());
            Require(!failure.Succeeded && failure.Error == "primary export error", "Cleanup hid the primary failure.");
            f.Files.FailCleanup = false;
        });

        await Check("stop-during-segment-wait", async path =>
        {
            var waiting = Signal(); var resume = Signal();
            using var f = new Fixture(path, wait: () => { waiting.TrySetResult(); return resume.Task; });
            f.Source.Segments(0, 2);
            var save = f.Save(3.5);
            try
            {
                await waiting.Task.WaitAsync(Deadline);
                await f.Cache.StopAcquisitionAsync(f.Id).WaitAsync(Deadline);
                resume.SetResult(); var result = await Finish(save);
                Require(result.Succeeded && !f.Source.Disposed, "Source loss discarded retained footage.");
                int trim = Array.IndexOf(f.Source.Arguments, "-t");
                Require(f.Source.Arguments[trim + 1] == "2.000000", "Save exceeded completed footage after stop.");
                Require((await Finish(f.Save(20))).Succeeded, "Stopped completed footage was no longer savable.");
            }
            finally { resume.TrySetResult(); }
        });

        await Check("retire-during-wait", async path =>
        {
            var waiting = Signal(); var resume = Signal();
            using var f = new Fixture(path, wait: () => { waiting.TrySetResult(); return resume.Task; });
            f.Source.Segments(0, 2); var save = f.Save(4);
            try
            {
                await waiting.Task.WaitAsync(Deadline);
                var retirement = f.Cache.RetireSessionAsync(f.Id);
                await f.Source.StopEntered.Task.WaitAsync(Deadline);
                Require(!f.Source.Disposed && !retirement.IsCompleted, "Retirement removed waiting save inputs.");
                resume.SetResult(); Require((await Finish(save)).Succeeded, "Accepted waiting save did not finish.");
                await retirement.WaitAsync(Deadline);
            }
            finally { resume.TrySetResult(); }
        });

        await Check("admission-reserves-before-segment-wait", async path =>
        {
            var waiting = Signal(); var resume = Signal();
            using var f = new Fixture(path, wait: () => { waiting.TrySetResult(); return resume.Task; });
            var save = f.Save(4);
            try
            {
                await waiting.Task.WaitAsync(Deadline);
                // The current GOP closes while the save task remains suspended.
                f.Source.Segments(0, 4); f.Cache.Maintain(f.Id);
                // Manifest rotation must not erase metadata for the reserved inputs.
                f.Source.Segments(100, 3); f.Cache.Maintain(f.Id);
                Require(Enumerable.Range(0, 4).All(i => File.Exists(f.Source.Chunk(i))), "Accepted waiting save lost inputs to pruning.");
                resume.SetResult(); Require((await Finish(save)).Succeeded, "Reserved input metadata was lost after manifest rotation.");
                Require(f.Source.Contents == "picture-0|picture-1|picture-2|picture-3", "Waiting save changed its requested footage.");
            }
            finally { resume.TrySetResult(); }
        });

        await Check("retire-during-copy", async path =>
        {
            using var f = new Fixture(path);
            var copying = Signal(); var resume = Signal();
            f.Files.BeforeCopy = (_, _) => { copying.TrySetResult(); resume.Task.WaitAsync(Deadline).GetAwaiter().GetResult(); };
            var save = f.Save();
            try
            {
                await copying.Task.WaitAsync(Deadline); var retirement = f.Cache.RetireSessionAsync(f.Id);
                await f.Source.StopEntered.Task.WaitAsync(Deadline);
                Require(!retirement.IsCompleted && !f.Source.Disposed, "Retirement removed copy inputs.");
                resume.SetResult(); Require((await Finish(save)).Succeeded, "Accepted copying save did not finish.");
                await retirement.WaitAsync(Deadline);
            }
            finally { resume.TrySetResult(); }
        });

        await Check("busy-new-session-and-close", async path =>
        {
            using var f = new Fixture(path); f.Source.ExportRelease = Signal();
            var save = f.Save(); await f.Source.ExportEntered.Task.WaitAsync(Deadline);
            var replacement = new Source(Path.Combine(path, "session-replacement"), age: 0); replacement.Segments(0, 3);
            var next = f.Cache.RegisterSession(replacement);
            Require(f.Cache.TrySave(next, replacement.Trigger(3), f.Destination).Rejection.Contains("already"), "Busy exclusion did not span sessions.");
            var close = f.Cache.CloseAsync();
            await f.Source.StopEntered.Task.WaitAsync(Deadline);
            Require(!close.IsCompleted && !f.Source.Disposed, "Close abandoned an accepted save.");
            Require(!f.Save().Accepted, "Close accepted another save.");
            f.Source.ExportRelease.SetResult(); Require((await Finish(save)).Succeeded, "Close interrupted save.");
            await close.WaitAsync(Deadline); Require(replacement.Disposed && f.Source.Disposed, "Close leaked source resources.");
        });

        await Check("admission-versus-retirement", async path =>
        {
            for (int i = 0; i < 20; i++)
            {
                using var f = new Fixture(Path.Combine(path, i.ToString()));
                var go = Signal();
                var saving = Task.Run(async () => { await go.Task; return f.Save(); });
                var retiring = Task.Run(async () => { await go.Task; await f.Cache.RetireSessionAsync(f.Id); });
                go.SetResult(); var submission = await saving.WaitAsync(Deadline);
                if (submission.Accepted) Require((await Finish(submission)).Succeeded, "Accepted save lost the admission race.");
                await retiring.WaitAsync(Deadline);
                Require(f.Source.Disposed && !f.Cache.Status.Saving, "Admission race leaked ownership.");
            }
        });

        await Check("stale-trigger-and-first-stop", async path =>
        {
            using var f = new Fixture(path);
            Require(!f.Cache.TrySave(f.Id, f.Source.Epoch - 1, f.Destination).Accepted && !f.Cache.Status.Saving, "Stale trigger borrowed session footage.");
            f.Source.FreezeAt(f.Source.Trigger(1.5));
            await f.Cache.StopAcquisitionAsync(f.Id);
            f.Source.FreezeAt(f.Source.Trigger(20));
            Require(f.Source.SaveCutoffAt(f.Source.Trigger(50)) == 1.5, "A later stop moved the first cutoff.");
            Require(f.Source.SaveCutoffAt(f.Source.Trigger(1)) == 1, "Stop changed an earlier trigger.");
            Require((await Finish(f.Save(20))).Succeeded, "First-stop save failed.");
            int trim = Array.IndexOf(f.Source.Arguments, "-t");
            Require(f.Source.Arguments[trim + 1] == "1.500000", "First-stop cutoff did not reach export.");
        });

        await Check("monitor-application-sequence", async path =>
        {
            using var f = new Fixture(path);
            f.Source.Segments(0, 3, ["fullscreen-game", "browser", "files"]);
            f.Source.ExportRelease = Signal(); var save = f.Save(); await f.Source.ExportEntered.Task.WaitAsync(Deadline);
            f.Source.Segments(0, 4, ["fullscreen-game", "browser", "files", "after-trigger"]);
            f.Source.ExportRelease.SetResult(); var result = await Finish(save);
            Require(result.Succeeded && Path.GetDirectoryName(result.File) == f.Destination, "Save destination changed.");
            Require(f.Source.Contents == "fullscreen-game|browser|files", "Application change filtered footage or extended the accepted request.");
        });

        foreach (string codec in new[] { "h264_nvenc", "hevc_nvenc", "av1_nvenc" })
            await Check("manifest-and-export-" + codec, async path =>
            {
                using var f = new Fixture(path, codec, audio: true);
                f.Source.Segments(0, 3, finalNewline: false);
                File.WriteAllText(f.Source.Chunk(999999999), "new chunk absent from partial manifest");
                f.Cache.Maintain(f.Id);
                Require(f.Cache.GetStatus(f.Id).BufferedSeconds == 1, "Readiness included pre-roll or unfinished manifest entry.");
                var result = await Finish(f.Save(2)); Require(result.Succeeded, result.Error);
                Require(f.Source.Contents == "picture-1" && f.Source.Concat[1] == "duration 1.000000", "Pre-roll/incomplete manifest exclusion or authoritative duration changed.");
                Require(File.Exists(f.Source.Chunk(999999999)), "Pruning removed a newer unlisted chunk.");
                Require(f.Source.Arguments.Contains("0:a:0") && f.Source.Arguments.Contains("+faststart"), "Audio map or MP4 finalization missing.");
                Require(f.Source.Arguments.Contains("hvc1") == (codec == "hevc_nvenc"), "HEVC tag changed.");
                Require(f.Source.Arguments.Any(s => s.Contains("setts=time_base=1/48000")) == (codec == "av1_nvenc"), "AV1 clock restoration changed.");
                Require(!Directory.GetFiles(f.Destination, "*.partial.mp4").Any(), "Successful save left a partial output.");
            });

        await Check("hard-links-and-fallback", async path =>
        {
            using var f = new Fixture(path); f.Files.RealHardLinks = true;
            var result = await Finish(f.Save());
            Require(result.Succeeded && f.Files.Copies == 0 && f.Files.LinkAttempts == 3,
                $"Local hard-link snapshot failed: success={result.Succeeded}; copies={f.Files.Copies}; attempts={f.Files.LinkAttempts}; Win32={f.Files.LinkError}; {result.Error}");
            f.Files.RealHardLinks = false;
            Require((await Finish(f.Save())).Succeeded && f.Files.Copies == 3, "Copy fallback failed.");
        });

        await Check("bounded-segment-wait", async path =>
        {
            var time = new ManualTime();
            using var f = new Fixture(path, time: time, wait: () => { time.Advance(); return Task.CompletedTask; });
            f.Source.Segments(0, 1);
            var result = await Finish(f.Save(10));
            Require(!result.Succeeded && result.Error.Contains("finish the current segment") && !f.Cache.Status.Saving, "Segment timeout did not release admission.");
        });

        await Check("retention-byte-limit", path =>
        {
            using var f = new Fixture(path);
            long limit = (long)((60 + 30) * (5 * 1000000L + 192000) / 8 * 1.5);
            using (var chunk = File.OpenWrite(f.Source.Chunk(2))) chunk.SetLength(limit + 1);
            bool rejected = false;
            try { f.Cache.Maintain(f.Id); } catch (IOException ex) { rejected = ex.Message.Contains("byte limit"); }
            Require(rejected, "Retention stopped enforcing its byte limit.");
            return Task.CompletedTask;
        });

        await Check("fractional-manifest-durations", async path =>
        {
            using var f = new Fixture(path);
            File.WriteAllText(f.Source.ReplayInfo.Manifest,
                Path.GetFileName(f.Source.Chunk(0)) + ",0.25,1.5\n" + Path.GetFileName(f.Source.Chunk(1)) + ",1.5,2.75\n");
            Require((await Finish(f.Save(2.5))).Succeeded, "Fractional-boundary export failed.");
            Require(f.Source.Concat[1] == "duration 1.250000" && f.Source.Concat[3] == "duration 1.250000", "Concat used container probes instead of manifest boundaries.");
            int trim = Array.IndexOf(f.Source.Arguments, "-t");
            Require(f.Source.Arguments[trim + 1] == "2.250000", "Trigger trim ignored the selected keyframe start.");
        });

        await Check("keyframe-start-and-window", async path =>
        {
            using var f = new Fixture(path); f.Source.Segments(0, 64);
            Require((await Finish(f.Save(62.5))).Succeeded, "Keyframe selection failed.");
            Require(f.Source.Contents.StartsWith("picture-3|") && f.Source.Contents.EndsWith("|picture-62"), "Replay did not start on the first keyframe after the window start.");
            int trim = Array.IndexOf(f.Source.Arguments, "-t");
            Require(f.Source.Arguments[trim + 1] == "59.500000", "Replay length or frozen fractional end changed.");
        });

        await Check("throwing-notification-observers", async path =>
        {
            using var f = new Fixture(path);
            int started = 0, finished = 0;
            Action start = () => throw new IOException("controlled start observer failure"); start += () => started++;
            Action<string, bool> finish = (_, _) => throw new IOException("controlled finish observer failure");
            finish += (_, success) => { Require(success, "Observer saw an incorrect result."); finished++; };
            var save = f.Save();
            var observed = new ReplaySaveNotifications().Observe(save.Completion, start, finish);
            var result = await Finish(save); await observed.WaitAsync(Deadline);
            Require(result.Succeeded && started == 1 && finished == 1 && !f.Cache.Status.Saving, "Observer error altered save completion.");
            Require((await Finish(f.Save())).Succeeded, "Observer failure stranded admission.");
        });

        await Check("missing-cache-status", path =>
        {
            using var f = new Fixture(path);
            Directory.Delete(f.Source.ReplayInfo.Directory, true);
            var status = f.Cache.GetStatus(f.Id);
            Require(status.BufferedSeconds == 0 && status.CacheBytes == 0, "Missing cache reported available footage.");
            return Task.CompletedTask;
        });

        await Check("consecutive-save-notification-order", async path =>
        {
            using var f = new Fixture(path);
            var notifications = new ReplaySaveNotifications();
            var events = new ConcurrentQueue<string>();
            var firstDelivery = Signal(); var releaseFirst = Signal(); var secondStarted = Signal();
            var notices = new RecorderNotifications((title, _, _, _) => events.Enqueue(title));
            var first = f.Save();
            // Hold A's result delivery after its export has released admission.
            var delayedFirst = new TaskCompletionSource<ReplaySaveResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstObserved = notifications.Observe(delayedFirst.Task, notices.SaveStarted, (message, success) =>
            {
                firstDelivery.SetResult(); releaseFirst.Task.WaitAsync(Deadline).GetAwaiter().GetResult();
                notices.SaveFinished(message, success);
            });
            var firstResult = await Finish(first);
            try
            {
                delayedFirst.SetResult(firstResult);
                await firstDelivery.Task.WaitAsync(Deadline);
                f.Source.ExportRelease = Signal();
                var second = f.Save(); Require(second.Accepted, second.Rejection);
                var secondObserved = notifications.Observe(second.Completion,
                    () => { notices.SaveStarted(); secondStarted.SetResult(); }, notices.SaveFinished);
                Require(f.Cache.Status.Saving, "A notification handler blocked save admission or changed busy state.");
                releaseFirst.SetResult(); await firstObserved.WaitAsync(Deadline); await secondStarted.Task.WaitAsync(Deadline);
                Require(events.Last() == "Saving replay…", "A delayed completion replaced the newer save's active notice.");
                f.Source.ExportRelease.SetResult(); Require((await Finish(second)).Succeeded, "Second save failed.");
                await secondObserved.WaitAsync(Deadline);
                Require(events.SequenceEqual(new[] { "Saving replay…", "Replay saved", "Saving replay…", "Replay saved" }), "Save notices were reordered or duplicated.");
            }
            finally { releaseFirst.TrySetResult(); }
        });

        File.WriteAllText(Path.Combine(root, "verification.json"), JsonSerializer.Serialize(new
        {
            result = failed == 0 ? "passed" : "failed", checks = results,
            effects = new { ui = false, capture = false, gpu = false, engineLaunch = false, startupWrites = false },
            limits = "Controlled manifests, files and export outcomes; no playable media, live capture, A/V or hardware acceptance."
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Replay ownership: {results.Count - failed}/{results.Count} passed. {root}");
        return failed == 0 ? 0 : 1;
    }
}
