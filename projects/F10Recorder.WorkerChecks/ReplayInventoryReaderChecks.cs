using System.Diagnostics;
using System.Text.Json;

namespace F10Recorder;

internal static class ReplayInventoryReaderChecks
{
    static void Require(bool value, string reason) { if (!value) throw new IOException(reason); }
    internal static int Run(string root)
    {
        using var owner = PilotStorageObservation.Create(root);
        var results = new List<object>(); int failed = 0;
        void Check(string name, Action<string> action)
        {
            string directory = Path.Combine(root, name); Directory.CreateDirectory(directory);
            try { action(directory); results.Add(new { name, passed = true }); }
            catch (Exception ex) { failed++; results.Add(new { name, passed = false, error = ex.ToString() }); }
        }
        static Dictionary<string, ReplayFileExtent> Scan(string directory, ReplayInventoryReader reader)
        {
            reader.BeginScan(); var inventory = ReplayStorage.Inventory([directory], reader, 1100, 4); reader.EndScan(); return inventory;
        }
        Check("growth-is-refreshed-without-reopening", directory =>
        {
            string file = Path.Combine(directory, "grow.bin"); File.WriteAllBytes(file, new byte[4096]);
            using var reader = new ReplayInventoryReader([directory]);
            var before = Scan(directory, reader)[file]; Require(reader.OpenCount == 1, "Initial native handle was not opened.");
            using (var output = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            { output.Write(new byte[1_048_576]); output.Flush(); }
            var after = Scan(directory, reader)[file];
            Require(reader.OpenCount == 0 && reader.HandleCount == 1 && after.Length == 1_052_672
                && after.AllocatedBytes >= before.AllocatedBytes && after == ReplayStorage.Observe(file), "A reused handle returned stale growth/allocation.");
        });
        Check("same-name-replacement-is-reopened", directory =>
        {
            string file = Path.Combine(directory, "replace.bin"), old = Path.Combine(directory, "old.bin"); File.WriteAllBytes(file, new byte[4096]);
            using var reader = new ReplayInventoryReader([directory]); var before = Scan(directory, reader)[file];
            File.Move(file, old); File.WriteAllBytes(file, new byte[8192]); var after = Scan(directory, reader);
            Require(after[file].Identity != before.Identity && after[file].Length == 8192 && after[old].Identity == before.Identity
                && reader.OpenCount == 2 && reader.HandleCount == 2, "Replacement reused a different object's metadata handle.");
        });
        Check("absent-names-release-handles", directory =>
        {
            string file = Path.Combine(directory, "remove.bin"); File.WriteAllBytes(file, new byte[4096]);
            using var reader = new ReplayInventoryReader([directory]); Scan(directory, reader);
            File.Delete(file); var after = Scan(directory, reader);
            Require(!after.ContainsKey(file) && reader.HandleCount == 0, "Absent names retained their native handles.");
            Directory.Delete(directory, false); Directory.CreateDirectory(directory);
        });
        Check("volatile-manifests-are-never-held", directory =>
        {
            string file = Path.Combine(directory, "segments.csv"); File.WriteAllText(file, "first");
            using var reader = new ReplayInventoryReader([directory]); Scan(directory, reader);
            string temporary = file + ".tmp"; File.WriteAllText(temporary, "second-list"); File.Move(temporary, file, true);
            var after = Scan(directory, reader);
            Require(reader.OpenCount == 0 && reader.HandleCount == 0 && after[file].Length == 11, "Volatile list acquired a long-lived handle.");
        });
        Check("wrong-identity-is-rejected", directory =>
        {
            string file = Path.Combine(directory, "identity.bin"); File.WriteAllBytes(file, new byte[4096]);
            using var reader = new ReplayInventoryReader([directory]); var before = Scan(directory, reader)[file];
            bool rejected = false; reader.BeginScan();
            try { reader.ObserveInventory(file, before with { Identity = "wrong-native-identity" }); } catch (IOException) { rejected = true; }
            Require(rejected && reader.HandleCount == 0, "A stale/mismatched native identity was accepted.");
        });
        Check("current-mutable-root-keeps-short-handles", directory =>
        {
            File.WriteAllBytes(Path.Combine(directory, "current.bin"), new byte[4096]);
            using var reader = new ReplayInventoryReader([]); Scan(directory, reader); Scan(directory, reader);
            Require(reader.OpenCount == 1 && reader.HandleCount == 0, "Unselected current output retained a native handle.");
        });
        Check("dispose-releases-all-native-handles", directory =>
        {
            File.WriteAllBytes(Path.Combine(directory, "closed.bin"), new byte[4096]);
            var reader = new ReplayInventoryReader([directory]); Scan(directory, reader); reader.Dispose();
            Require(reader.HandleCount == 0, "Reader disposal retained native handles.");
        });
        object timing = null, lastClose = null;
        Check("last-close-allocation-observation", directory =>
        {
            string file = Path.Combine(directory, "append.bin");
            using var reader = new ReplayInventoryReader([directory]);
            using (var writer = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 4096))
            {
                var block = new byte[65536];
                for (int n = 0; n < 40; n++) { writer.Write(block); writer.Flush(); if (n == 0) Scan(directory, reader); }
            }
            var held = ReplayStorage.Observe(file); reader.ReleaseHandles(); var released = ReplayStorage.Observe(file);
            Require(held.Length == released.Length && released.Length == 2_621_440, "Read-only observation changed file bytes/EOF.");
            lastClose = new { held.AllocatedBytes, afterReleaseAllocatedBytes = released.AllocatedBytes,
                effectReproduced = held.AllocatedBytes > released.AllocatedBytes, limits = "Filesystem-specific observation; no forced equality assumption." };
        });
        Check("sampler-rejects-undeclared-or-current-roots-before-write", directory =>
        {
            string current = Path.Combine(directory, "current");
            foreach (int variant in new[] { 0, 1, 2 })
            {
                string retained = variant == 2 ? Path.Combine(current, "nested-retained") : directory;
                var plan = new CalibrationPlan(current, "unused", "native-control", new(), [], variant != 0 ? [retained] : [], 0, 0,
                    RetainedOfflineSeconds: 0, MetadataHandleRoots: [retained]);
                bool rejected = false;
                try { using var sampler = new ReplayCalibrationRun.Sampler(plan, active: false); }
                catch (IOException ex) when (ex.Message.Contains("metadata handles")) { rejected = true; }
                Require(rejected && !Directory.Exists(current), "Unsafe metadata root was not rejected before observer writes.");
            }
        });
        Check("bounded-native-metadata-comparison", directory =>
        {
            for (int n = 0; n < 1000; n++) File.WriteAllBytes(Path.Combine(directory, $"metadata-{n:D4}.bin"), []);
            using var repeated = new ReplayInventoryReader([]); using var reused = new ReplayInventoryReader([directory]);
            Scan(directory, reused);
            var ordinary = new List<double>(); var cached = new List<double>();
            for (int n = 0; n < 10; n++)
            {
                var watch = Stopwatch.StartNew(); var first = Scan(directory, repeated); ordinary.Add(watch.Elapsed.TotalMilliseconds);
                watch.Restart(); var second = Scan(directory, reused); cached.Add(watch.Elapsed.TotalMilliseconds);
                Require(first.Count == 1000 && second.Count == 1000 && first.All(e => second[e.Key] == e.Value)
                    && repeated.OpenCount == 1000 && reused.OpenCount == 0 && reused.HandleCount == 1000,
                    "Metadata comparison omitted native extents or reused allocation values.");
            }
            timing = new { paths = 1000, comparisons = 10, ordinaryMilliseconds = ordinary, reusedMilliseconds = cached,
                limits = "Tiny closed metadata fixtures only; no full-size workflow or500ms reliability claim." };
        });
        File.WriteAllText(Path.Combine(root, "inventory-reader-checks.json"), JsonSerializer.Serialize(new { failed, results, timing, lastClose,
            effects = "Under4MiB ordinary dummy payload plus1000 empty files; native metadata handles only. No engine, children, recording, audio, GPU or UI."
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Native metadata reader controls: {results.Count - failed}/{results.Count} passed"); return failed == 0 ? 0 : 1;
    }
}
