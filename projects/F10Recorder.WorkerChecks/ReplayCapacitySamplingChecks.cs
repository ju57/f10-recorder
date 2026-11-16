using System.Text.Json;

namespace F10Recorder;

// Tiny native ledger/identity fixtures with controlled caller-visible capacity.
// No capture, audio, media process, worker or normal profile is started.
internal static class ReplayCapacitySamplingChecks
{
    sealed class Files : ReplayFiles
    {
        internal bool RejectMetadata;
        internal int CapacityCalls, MetadataCalls;
        internal Action BeforeCapacity;
        internal long Free = 10000;
        internal readonly Dictionary<string, string> Volumes = new(StringComparer.OrdinalIgnoreCase);
        string Volume(string path) => Volumes.FirstOrDefault(p => path.StartsWith(p.Key, StringComparison.OrdinalIgnoreCase)).Value ?? "a";
        internal override ReplayFileExtent Observe(string path)
        {
            MetadataCalls++;
            if (RejectMetadata) throw new IOException("Metadata unavailable during capacity-only sampling.");
            return base.Observe(path) with { Volume = Volume(path), AllocationUnit = 1, AllocatedBytes = 2000 };
        }
        internal override ReplayVolumeCapacity ObserveCapacity(string path)
        { CapacityCalls++; BeforeCapacity?.Invoke(); return new(Volume(path), 1, Free); }
    }
    internal static int Run(string root)
    {
        if (Directory.Exists(root) || File.Exists(root)) throw new IOException("Capacity checks need a fresh private root.");
        Directory.CreateDirectory(root);
        var results = new List<object>(); int failures = 0;
        void Check(string name, Action<string> action)
        {
            string directory = Path.Combine(root, name); Directory.CreateDirectory(directory);
            try { action(directory); results.Add(new { name, passed = true }); }
            catch (Exception error) { failures++; results.Add(new { name, passed = false, error = error.ToString() }); }
        }
        static void Require(bool value, string reason) { if (!value) throw new IOException(reason); }
        Check("no-file-metadata-or-allocation-credit", directory =>
        {
            var files = new Files(); string security = Path.Combine(directory, "security");
            var store = new ReplayVolumeReservations(security);
            string owner = Guid.NewGuid().ToString("N"), path = Path.Combine(directory, "held");
            File.WriteAllBytes(path, [1]);
            store.Update(owner, [new(path, directory, "a", 1, 6000)], files);
            string ledger = Path.Combine(security, "replay-volume-reservations.json");
            byte[] before = File.ReadAllBytes(ledger);
            files.RejectMetadata = true; files.Free = 8000; files.CapacityCalls = files.MetadataCalls = 0;
            var value = store.ObserveUnreserved(directory, files);
            Require(value.Bytes == 2000, "Capacity-only sampling must debit the full peak, without assuming allocated prefixes remain present.");
            Require(files.MetadataCalls == 0 && files.CapacityCalls == 1, "Periodic sampling must query just the actual target volume, without holder metadata.");
            Require(before.SequenceEqual(File.ReadAllBytes(ledger)), "Sampling must never recover or mutate a holder.");
            Require(store.ObserveUnreserved(directory, files, owner).Bytes == 8000, "Own capture allowance must be charged once by its separate stop policy.");
            bool rejected = false;
            try { store.Update(Guid.NewGuid().ToString("N"), [new(Path.Combine(directory, "new"), directory, "a", 1, 1)], files); }
            catch (IOException) { rejected = true; }
            Require(rejected, "Full admission must still reject unavailable holder identity evidence.");
        });
        Check("shared-and-separate-volumes", directory =>
        {
            var files = new Files(); var store = new ReplayVolumeReservations(Path.Combine(directory, "security"));
            string other = Path.Combine(directory, "other"); Directory.CreateDirectory(other); files.Volumes[other] = "b";
            store.Update(Guid.NewGuid().ToString("N"), [new(Path.Combine(directory, "one"), directory, "a", 1, 4000)], files);
            store.Update(Guid.NewGuid().ToString("N"), [new(Path.Combine(directory, "two"), directory, "a", 1, 3000),
                new(Path.Combine(other, "three"), other, "b", 1, 9000)], files);
            files.RejectMetadata = true;
            Require(store.ObserveUnreserved(directory, files).Bytes == 3000, "Same-volume commitments must add.");
            Require(store.ObserveUnreserved(other, files).Bytes == 1000, "Other volumes cannot subsidize this volume.");
            files.Free = 500;
            Require(store.ObserveUnreserved(other, files).Bytes == -8500, "Sudden capacity loss must remain negative, not clamp away the failure.");
        });
        Check("changed-ledger-roundtrip-invalidates-sample", directory =>
        {
            var files = new Files(); var writerFiles = new Files();
            string security = Path.Combine(directory, "security"), owner = Guid.NewGuid().ToString("N");
            var store = new ReplayVolumeReservations(security); var peer = new ReplayVolumeReservations(security);
            ReplayVolumeClaim claim = new(Path.Combine(directory, "held"), directory, "a", 1, 6000);
            store.Update(owner, [claim], files);
            files.BeforeCapacity = () =>
            {
                // This would deadlock/fail if the capacity query retained the
                // ledger lock. Identical final claims still need a new epoch.
                peer.Release(owner); peer.Update(owner, [claim], writerFiles);
            };
            bool rejected = false;
            try { store.ObserveUnreserved(directory, files); } catch (ReplayVolumeReservationBusyException) { rejected = true; }
            Require(rejected, "An intervening release/reacquisition must invalidate the earlier free-space observation.");
            files.BeforeCapacity = null;
            Require(store.ObserveUnreserved(directory, files).Bytes == 4000, "The next genuine stable sample must charge the retained full commitment.");
        });
        File.WriteAllText(Path.Combine(root, "verification.json"), JsonSerializer.Serialize(new { failures, checks = results }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Capacity sampling: {results.Count - failures}/{results.Count} passed.");
        return failures == 0 ? 0 : 1;
    }
}
