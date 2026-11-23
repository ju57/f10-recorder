using System.Text.Json;
using System.Diagnostics;

namespace F10Recorder;

// Nonmedia fixtures: a shared write-only producer must permit native metadata
// queries without requiring payload-read access. No capture/worker/device.
internal static class ReplayMaintenanceInventoryChecks
{
    sealed class Source(string directory) : IReplaySessionSource
    {
        public ReplaySessionInfo ReplayInfo { get; } = new(directory, "", 1, 1, 60, "controlled", ".ts", false, false);
        public bool PreserveEvidence => true;
        public bool Stopped => true;
        public bool OutputProducerStopped => true;
        public string Diagnostics => "Nonmedia maintenance metadata fixture.";
        public List<Segment> ReadSegments() => [new() { Path = Path.Combine(directory, "chunk-000000.ts"), Start = 0, End = 1 }];
        public double SaveCutoffAt(long timestamp) => 1;
        public void FreezeAt(long timestamp) { }
        public void StopCapture() { }
        public void CheckCaptureHealth(double end) { }
        public void Export(IReadOnlyList<string> arguments, string snapshot) => throw new IOException("No export in metadata fixture.");
        public void Dispose() { }
    }
    sealed class CustomFiles(long length) : ReplayFiles
    {
        internal int Reads;
        internal override ReplayFileExtent Observe(string path) { Reads++; return base.Observe(path) with { Length = length }; }
    }
    sealed class CountingFiles : ReplayFiles
    {
        internal int Reads;
        internal bool Fail;
        internal override ReplayFileExtent ObserveInventory(string path, ReplayFileExtent enumerated)
        {
            Reads++;
            if (Fail) throw new IOException("Controlled inventory failure.");
            return base.ObserveInventory(path, enumerated);
        }
    }
    internal static int Run(string root)
    {
        if (!root.StartsWith(Path.GetFullPath("artifacts") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || Directory.Exists(root) || File.Exists(root)) throw new IOException("Fresh private metadata fixture required.");
        Directory.CreateDirectory(root); var checks = new List<(string Name, bool Passed, string Error)>();
        long limit = (long)(31 * (1_000_000L + 192000) / 8 * 1.5);
        void Check(string name, Action<string> test)
        {
            string cell = Path.Combine(root, name); Directory.CreateDirectory(cell);
            try { test(cell); checks.Add((name, true, null)); }
            catch (Exception error) { checks.Add((name, false, error.ToString())); }
        }
        Check("metadata-does-not-open-growing-chunk-for-data", cell => {
            string chunk = Path.Combine(cell, "chunk-000000.ts"); File.WriteAllBytes(chunk, new byte[1024]);
            using var writer = new FileStream(chunk, FileMode.Open, FileAccess.Write, FileShare.Write | FileShare.Delete);
            bool denied = false; try { using var data = File.OpenRead(chunk); } catch (IOException) { denied = true; }
            Need(denied, "Fixture must deny payload-read handles.");
            using var cache = new ReplayCache(cacheDirectory: cell); var id = cache.RegisterSession(new Source(cell)); cache.Maintain(id);
        });
        Check("growing-eof-is-refreshed-next-maintenance", cell => {
            string chunk = Path.Combine(cell, "chunk-000000.ts"); File.WriteAllBytes(chunk, new byte[1024]);
            using var writer = new FileStream(chunk, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            using var cache = new ReplayCache(cacheDirectory: cell); var id = cache.RegisterSession(new Source(cell));
            cache.Maintain(id); writer.SetLength(limit + 1); writer.Flush();
            bool rejected = false; try { cache.Maintain(id); } catch (ReplayCacheBudgetExceededException) { rejected = true; }
            Need(rejected, "A prior small EOF must not hide later growth.");
        });
        Check("rolling-byte-limit-excludes-nested-and-nonchunk-files", cell => {
            File.WriteAllBytes(Path.Combine(cell, "chunk-000000.ts"), new byte[1024]); Directory.CreateDirectory(Path.Combine(cell, "nested"));
            using (var nested = File.Create(Path.Combine(cell, "nested", "chunk-999999.ts"))) nested.SetLength(4_000_000);
            using (var other = File.Create(Path.Combine(cell, "other.bin"))) other.SetLength(4_000_000);
            using var cache = new ReplayCache(cacheDirectory: cell); var id = cache.RegisterSession(new Source(cell)); cache.Maintain(id);
        });
        Check("custom-observation-provider-keeps-its-own-semantics", cell => {
            File.WriteAllBytes(Path.Combine(cell, "chunk-000000.ts"), new byte[1024]); var files = new CustomFiles(limit + 1);
            using var cache = new ReplayCache(files, cacheDirectory: cell); var id = cache.RegisterSession(new Source(cell));
            bool rejected = false; try { cache.Maintain(id); } catch (ReplayCacheBudgetExceededException) { rejected = true; }
            Need(rejected && files.Reads > 0, "Custom EOF/fault providers must retain their own semantics.");
        });
        Check("session-outside-inventory-root-retains-length-limit", cell => {
            string source = Path.Combine(cell, "source"), inventory = Path.Combine(cell, "inventory");
            Directory.CreateDirectory(source); Directory.CreateDirectory(inventory);
            using (var chunk = File.Create(Path.Combine(source, "chunk-000000.ts"))) chunk.SetLength(limit + 1);
            using var cache = new ReplayCache(cacheDirectory: inventory); var id = cache.RegisterSession(new Source(source));
            bool rejected = false; try { cache.Maintain(id); } catch (ReplayCacheBudgetExceededException) { rejected = true; }
            Need(rejected, "A session outside the configured inventory roots must not be counted as zero.");
        });
        Check("diagnostic-projection-never-supplies-storage-credit", cell => {
            string chunk = Path.Combine(cell, "chunk-000000.ts"); File.WriteAllBytes(chunk, new byte[1024]);
            var files = new CountingFiles(); using var cache = new ReplayCache(files, cacheDirectory: cell);
            cache.RegisterSession(new Source(cell));
            Need(cache.LastKnownStatus.CacheBytes == null && cache.LastKnownStatus.CacheBytesObservedUtc == null && files.Reads == 0,
                "Unobserved bytes must be unknown without filesystem work.");
            cache.ValidateBudget(1_000_000); int reads = files.Reads;
            for (int i = 0; i < 20; i++)
                Need(cache.LastKnownStatus.CacheBytes > 0 && cache.LastKnownStatus.CacheBytesObservedUtc != null, "Successful inventory must supply dated diagnostics.");
            Need(files.Reads == reads, "Repeated diagnostic polling must not scan files.");
            using (var writer = new FileStream(chunk, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { writer.SetLength(8192); writer.Flush(); }
            bool rejected = false; try { cache.ValidateBudget(4096); } catch (ReplayCacheBudgetExceededException) { rejected = true; }
            Need(rejected && files.Reads > reads && cache.LastKnownStatus.CacheBytes > 4096, "Mandatory budget checks must refresh growth despite older diagnostic bytes.");
            files.Fail = true; var failed = cache.Status;
            Need(failed.CacheBytes == null && cache.LastKnownStatus.CacheBytes == null && cache.LastKnownStatus.CacheBytesObservedUtc == null
                && cache.LastKnownStatus.StorageError?.Contains("Controlled inventory failure.") == true, "Inventory failure must invalidate diagnostics.");
            files.Fail = false; cache.ValidateBudget(1_000_000);
            Need(cache.LastKnownStatus.CacheBytes > 0 && cache.LastKnownStatus.StorageError == null, "A later complete observation must recover diagnostics.");
        });
        var timings = new List<ReplayMaintenanceTiming>();
        var statusMilliseconds = new List<double>();
        var projectionMilliseconds = new List<double>();
        Check("finite-native-maintenance-cost", cell => {
            for (int i = 0; i < 300; i++) File.WriteAllBytes(Path.Combine(cell, $"chunk-{i:D6}.ts"), new byte[1024]);
            using var cache = new ReplayCache(cacheDirectory: cell); var id = cache.RegisterSession(new Source(cell));
            cache.Maintain(id); // Explicitly unmeasured setup; no cache eviction claim.
            cache.MaintenanceObservedForChecks = timings.Add;
            for (int i = 0; i < 20; i++) cache.Maintain(id);
            Need(timings.Count == 20 && timings.All(t => t.Error == null), "Twenty complete native observations required.");
            for (int i = 0; i < 20; i++)
            {
                long started = Stopwatch.GetTimestamp(); var status = cache.Status;
                statusMilliseconds.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                Need(status.CacheBytes > 0 && status.StorageError == null, "Fresh status must report the actual private files.");
            }
            for (int i = 0; i < 20; i++)
            {
                long started = Stopwatch.GetTimestamp(); var status = cache.LastKnownStatus;
                projectionMilliseconds.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                Need(status.CacheBytes > 0 && status.CacheBytesObservedUtc != null, "Projection must retain dated successful observation.");
            }
        });
        File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new {
            scope = "Seven finite nonmedia fixtures; <=32MiB generated bodies; no recording/model/qualification.",
            maintenanceMilliseconds = timings.Select(t => new { t.Total, t.Lengths, t.Budget }).ToArray(),
            statusMilliseconds,
            projectionMilliseconds,
            checks = checks.Select(c => new { name = c.Name, passed = c.Passed, error = c.Error }) }, new JsonSerializerOptions { WriteIndented = true }));
        return checks.Any(c => !c.Passed) ? 1 : 0;
    }
    static void Need(bool value, string message) { if (!value) throw new IOException(message); }
}
