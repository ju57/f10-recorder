using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace F10Recorder;

internal static partial class ReplayCalibrationRun
{
    internal static int OutputMetadataChecks(string planFile, string expectedHash)
    {
        Require(new FileInfo(planFile).Length < 65536 && HashFile(planFile) == expectedHash, "Output-metadata plan changed.");
        using var document = JsonDocument.Parse(File.ReadAllText(planFile)); var p = document.RootElement;
        Require(p.GetProperty("kind").GetString() == "issue14-output-metadata-nonmedia-v1", "Unknown metadata probe.");
        string root = Path.GetFullPath(p.GetProperty("root").GetString()), family = Path.GetDirectoryName(root);
        string source = Path.GetFullPath(p.GetProperty("dummySource").GetString());
        Require(!Directory.Exists(root) && p.GetProperty("deadlineSeconds").GetInt32() == 180
            && p.GetProperty("budgetBytes").GetInt64() == 20_000_000_000 && p.GetProperty("floorBytes").GetInt64() == 60_000_000_000,
            "Fresh, bounded nonmedia probe required.");
        foreach (var binding in p.GetProperty("files").EnumerateObject()) Require(HashFile(binding.Name) == binding.Value.GetString(), "Probe input changed.");
        foreach (string required in new[] { Environment.ProcessPath, typeof(ReplayCalibrationRun).Assembly.Location,
            Path.Combine(Path.GetDirectoryName(typeof(ReplayCalibrationRun).Assembly.Location), "F10Recorder.Windows.dll"),
            Path.Combine(Path.GetDirectoryName(typeof(ReplayCalibrationRun).Assembly.Location), "F10Recorder.Core.dll") })
            Require(p.GetProperty("files").TryGetProperty(Path.GetFullPath(required), out _), "Probe must bind its actual host/consumers.");
        Require(SourceFingerprint() == p.GetProperty("sourceFingerprint").GetString(), "Retained dummy bytes changed.");
        using var owned = PilotStorageObservation.Create(root);
        Log.DirectoryPath = root; Log.FileName = "metadata-probe.log";
        var clock = Stopwatch.StartNew();
        string controls = Path.Combine(root, "controls");
        Require(ReplayInventoryReaderChecks.Run(controls) == 0, "Existing native-reader controls failed.");
        var results = new List<object>(); bool proposedPassed = false;
        foreach (bool retained in new[] { false, true })
        {
            Deadline(); string mode = retained ? "retained" : "ordinary", current = Path.Combine(root, mode);
            Directory.CreateDirectory(current);
            string[] histories = Directory.GetDirectories(family).Where(d => !Path.GetFullPath(d).Equals(root, StringComparison.OrdinalIgnoreCase))
                .Concat(retained ? new[] { controls, Path.Combine(root, "ordinary") } : new[] { controls }).ToArray();
            var plan = new CalibrationPlan(current, "unused", "nonmedia-output-metadata", new(), Order, histories, clock.Elapsed.TotalSeconds, 0,
                BudgetBytes: 20_000_000_000, StopBytes: 18_000_000_000, FloorBytes: 60_000_000_000, DeadlineSeconds: 175,
                RetainedOfflineSeconds: 0, MetadataHandleRoots: histories, ReuseCurrentMetadataHandles: true,
                DirectoryLimit: 2048, FileLimit: 16384, OutputComponentBytes: 1_100_000_000, RetainedBudgetBytes: 14_000_000_000,
                OutsideBuildAllowanceBytes: 268_435_456, FreezeRetainedAllocations: true, RetainActiveOutputHandles: retained);
            using var sampler = new Sampler(plan, active: false);
            string cell = Path.Combine(current, "T01"), session = Path.Combine(cell, "buffer", "session-dummy"); Directory.CreateDirectory(session);
            for (int n = 0; n < 310; n++)
            { Deadline(); string name = $"chunk-{n:D9}.ts"; Require(Native.CreateHardLink(Path.Combine(session, name), Path.Combine(source, name), IntPtr.Zero), "Dummy source link failed."); }
            File.Copy(Path.Combine(source, "segments.csv"), Path.Combine(session, "segments.csv"));
            string partial = null, final = null, failure = null; int freshChecks = 0;
            double writerCloseMilliseconds = 0; ReplayFileExtent beforeRelease = null, afterRelease = null;
            bool renamedWithoutReopen = false; ReplayCache cache = null;
            bool replacementObserved = false;
            object beforeReleaseOperations = null;
            Sampler.OutputRelease release = null;
            byte[] block = new byte[1_048_576]; RandomNumberGenerator.Fill(block);
            try
            {
                sampler.BeginActive();
                cache = new ReplayCache(new RetainFiles(sampler, true), cacheDirectory: Path.Combine(cell, "buffer"));
                var dummy = new Issue14Dummy(session, (args, check) =>
                {
                    partial = args[^1];
                    var writer = new FileStream(partial, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write,
                        Share = FileShare.ReadWrite | FileShare.Delete, BufferSize = block.Length, PreallocationSize = 850_000_000 });
                    try
                    {
                        long left = 800_000_000, written = 0;
                        while (left > 0)
                        {
                            Deadline(); sampler.Check(); int take = (int)Math.Min(left, block.Length); writer.Write(block, 0, take); left -= take; written += take;
                            if (written >= 32_000_000 || left == 0)
                            { writer.Flush(); check(); Fresh(writer.Length); written = 0; }
                        }
                        writer.Position = 0; for (int n = 0; n < 16; n++) { writer.Write(block); sampler.Check(); check(); }
                        writer.Flush(); Fresh(800_000_000);
                    }
                    finally { var close = Stopwatch.StartNew(); writer.Dispose(); writerCloseMilliseconds = close.Elapsed.TotalMilliseconds; }
                    sampler.Check(); check(); Fresh(800_000_000);

                    void Fresh(long length)
                    {
                        var wait = Stopwatch.StartNew();
                        while (sampler.OutputExtent(partial)?.Length != length)
                        { Deadline(); sampler.Check(); check(); Require(wait.ElapsedMilliseconds < 450, "Fresh output EOF was not observed within the500ms contract."); Thread.Sleep(5); }
                        var expected = ReplayStorage.Observe(partial); var observed = sampler.OutputExtent(partial);
                        Require(observed == expected && expected.Length == length, "Native retained allocation/EOF was stale or changed identity."); freshChecks++;
                    }
                });
                var id = cache.RegisterSession(dummy); var save = cache.TrySave(id, 0, cell);
                Require(save.Accepted, save.Rejection);
                while (!save.Completion.IsCompleted) { Deadline(); sampler.Check(); Thread.Sleep(10); }
                var result = save.Completion.GetAwaiter().GetResult(); Require(result.Succeeded, result.Error); final = result.File;
                cache.CloseAsync().GetAwaiter().GetResult();
                sampler.EndActive();
                Require(sampler.MaximumActiveGap <= 500, "Proposed observer exceeded500ms.");
                beforeReleaseOperations = sampler.OutputOperationTimings;
                var operationCounts = JsonSerializer.SerializeToElement(beforeReleaseOperations);
                renamedWithoutReopen = !retained || sampler.RetainsMetadata(final)
                    && operationCounts.GetProperty("open").GetProperty("Count").GetInt32() == 1;
                release = sampler.ReleaseClosedOutputHandles(final); beforeRelease = release.Before; afterRelease = release.After;
                Require(beforeRelease.Length == afterRelease.Length && afterRelease.Length == 800_000_000
                    && beforeRelease.Identity == afterRelease.Identity && afterRelease.AllocatedBytes <= beforeRelease.AllocatedBytes,
                    "Releasing metadata changed bytes/identity or increased allocation.");
                Require(renamedWithoutReopen && freshChecks >= 25, "Output growth/rename controls were not exercised.");
                string oldOutput = final + ".old.mp4";
                File.Move(final, oldOutput); File.WriteAllBytes(final, new byte[4096]);
                var replacement = ReplayStorage.Observe(final); var replacementWait = Stopwatch.StartNew();
                while (sampler.OutputExtent(final)?.Identity != replacement.Identity || sampler.OutputExtent(oldOutput)?.Identity != afterRelease.Identity)
                { Deadline(); sampler.Check(); Require(replacementWait.ElapsedMilliseconds < 450, "Output replacement identity was not refreshed."); Thread.Sleep(5); }
                replacementObserved = replacement.Identity != afterRelease.Identity && sampler.OutputExtent(final) == replacement;
                Require(replacementObserved, "Same-name output replacement reused old identity/allocation.");
            }
            catch (Exception error) { failure = error.ToString(); }
            finally
            {
                try { cache?.CloseAsync().GetAwaiter().GetResult(); }
                catch (Exception error) { failure = (failure == null ? "" : failure + "\nCleanup: ") + error; }
                // Producer is synchronous and has drained before this boundary.
                // Failed coverage remains failed; stopping is not a repair.
                try { sampler.StopAndCheck(); }
                catch (Exception error) { failure = (failure == null ? "" : failure + "\nCleanup: ") + error; }
            }
            if (retained && failure == null) proposedPassed = true;
            var resultEvidence = new { mode, result = failure == null ? "passed" : "failed", failure, freshChecks,
                writerCloseMilliseconds, beforeRelease, afterRelease, release, renamedWithoutReopen, replacementObserved,
                sampler.MaximumActiveGap, sampler.PeakUsed, beforeReleaseOperations, operations = sampler.OutputOperationTimings };
            Write(Path.Combine(current, "verification.json"), resultEvidence); results.Add(resultEvidence);
        }
        Require(SourceFingerprint() == p.GetProperty("sourceFingerprint").GetString(), "Probe changed retained input bytes.");
        Write(Path.Combine(root, "verification.json"), new { result = proposedPassed ? "passed" : "failed", results, elapsedSeconds = clock.Elapsed.TotalSeconds,
            effects = "One bounded helper, two800MB ordinary dummy outputs, read-only source hardlinks and actual replay storage callbacks; no engine, devices, media children, window, capture, startup/preferences or model activation.",
            limits = "Collector metadata-policy evidence, not playable media, fresh native qualification or a universal500ms guarantee." });
        Console.WriteLine("Output metadata probe: " + (proposedPassed ? "passed" : "failed")); return proposedPassed ? 0 : 1;

        void Deadline() => Require(clock.Elapsed.TotalSeconds < 175, "Nonmedia metadata probe deadline; cleanup reserve remains.");
        string SourceFingerprint()
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (string name in Enumerable.Range(0, 310).Select(n => $"chunk-{n:D9}.ts").Append("segments.csv"))
            { string file = Path.Combine(source, name); Require(File.Exists(file), "Missing retained dummy source.");
                hash.AppendData(System.Text.Encoding.UTF8.GetBytes(name + ":" + HashFile(file) + "\n")); }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
    }
}
