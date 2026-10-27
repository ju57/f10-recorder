using System.Text.Json;

namespace F10Recorder;

internal static class PilotStorageObservationChecks
{
    sealed class SecondFileFailure : ReplayFiles
    {
        internal bool Denied;
        internal Func<ReplayVolumeCapacity, ReplayVolumeCapacity> Capacity;
        internal Func<string, ReplayFileExtent, ReplayFileExtent> Extent;
        internal override ReplayVolumeCapacity ObserveCapacity(string directory)
        {
            var value = base.ObserveCapacity(directory);
            return Capacity == null ? value : Capacity(value);
        }
        internal override ReplayFileExtent Observe(string path)
        {
            if (Denied && Path.GetFileName(path) == "second.txt") throw new IOException("Controlled second-file denial.");
            var value = base.Observe(path);
            return Extent == null ? value : Extent(Path.GetFileName(path), value);
        }
    }
    static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    static (int Code, string Json) Command(params string[] args)
    {
        var output = new StringWriter();
        TextWriter previous = Console.Out;
        try { Console.SetOut(output); return (PilotStorageObservation.Dispatch(args), output.ToString()); }
        finally { Console.SetOut(previous); }
    }
    internal static int Run(string root)
    {
        using var scope = PilotStorageObservation.Create(root);
        var checks = new List<object>();
        int failed = 0;
        void Check(string name, Action body)
        {
            try { body(); checks.Add(new { name, result = "passed" }); }
            catch (Exception error) { failed++; checks.Add(new { name, result = "failed", error = error.ToString() }); }
        }
        var binding = new PilotStorageBinding(new string('a', 64), new string('b', 64), "302/1");
        Check("native-root-file-and-marker-observed-without-byte-changes", () =>
        {
            using var session = PilotStorageObservation.Create(Path.Combine(root, "positive"));
            string path = Path.Combine(session.Root, "sample.txt");
            File.WriteAllText(path, "pilot-sentinel\n");
            string marker = File.ReadAllText(Path.Combine(session.Root, PilotStorageObservation.Marker));
            var result = session.Observe(binding, new[] { "sample.txt" });
            Require(result.Complete, result.Error);
            Require(result.Origin == "native-storage", "Native positive was supplied data.");
            Require(result.Capacity.Volume.Length > 0 && result.Capacity.AllocationUnit > 0 && result.Capacity.AvailableBytes >= 0, "Unknown native capacity.");
            Require(result.Files["sample.txt"].Length == 15, "Native EOF differs from literal sentinel length.");
            Require(result.Files.ContainsKey(PilotStorageObservation.Marker) && result.Files.Count == 2, "Marker disappeared from allocation accounting.");
            Require(result.UsedBytes >= 0 && result.RootIdentity.Length > 0, "Unknown native retained total or root identity.");
            Require(File.ReadAllText(path) == "pilot-sentinel\n" && File.ReadAllText(Path.Combine(session.Root, PilotStorageObservation.Marker)) == marker, "Observation changed bytes.");
            File.WriteAllText(Path.Combine(root, "native-observation.json"), JsonSerializer.Serialize(result.ToPreparationObservation(binding), PilotStorageObservation.Json));
        });
        Check("second-file-failure-retains-partial-evidence-without-cheap-complete-total", () =>
        {
            var files = new SecondFileFailure();
            using var session = PilotStorageObservation.Create(Path.Combine(root, "partial"), files);
            File.WriteAllText(Path.Combine(session.Root, "first.txt"), "first\n");
            File.WriteAllText(Path.Combine(session.Root, "second.txt"), "second\n");
            var positive = session.Observe(binding, new[] { "first.txt", "second.txt" });
            Require(positive.Complete && positive.UsedBytes >= 0 && positive.Files.Count == 3, "Complete two-entry control failed.");
            files.Denied = true;
            var denied = session.Observe(binding, new[] { "first.txt", "second.txt" });
            Require(!denied.Complete && denied.UsedBytes == null, "Second-file denial manufactured a cheaper complete total.");
            Require(denied.Files.ContainsKey("first.txt") && denied.Error.Contains("second-file denial"), "Partial observations or reason disappeared.");
            File.WriteAllText(Path.Combine(root, "partial-observation.json"), JsonSerializer.Serialize(denied.ToPreparationObservation(binding), PilotStorageObservation.Json));
        });
        Check("session-rejects-source-cutoff-plan-and-capacity-rebinding", () =>
        {
            var files = new SecondFileFailure();
            using var session = PilotStorageObservation.Create(Path.Combine(root, "bindings"), files);
            var original = session.Observe(binding, Array.Empty<string>());
            Require(original.Complete, "Initial binding control failed.");
            foreach (var changed in new[] { binding with { PlanSha256 = new string('c', 64) }, binding with { SourceManifestSha256 = new string('d', 64) }, binding with { Cutoff = "303/1" } })
            {
                Require(!session.Observe(changed, Array.Empty<string>()).Complete, "A changed supplied binding silently rebound the owned session.");
                bool rejected = false;
                try { original.ToPreparationObservation(changed); } catch (IOException) { rejected = true; }
                Require(rejected, "A prior native snapshot was rebound during preparation serialization.");
            }
            files.Capacity = value => value with { Volume = "changed-volume" };
            files.Extent = (_, value) => value with { Volume = "changed-volume" };
            Require(!session.Observe(binding, Array.Empty<string>()).Complete, "Destination volume silently rebound.");
            files.Capacity = value => value with { AllocationUnit = checked(value.AllocationUnit * 2) };
            files.Extent = (_, value) => value with { AllocationUnit = checked(value.AllocationUnit * 2) };
            Require(!session.Observe(binding, Array.Empty<string>()).Complete, "Destination allocation unit silently rebound.");
        });
        Check("shared-extents-charge-once-and-contradictory-aliases-are-unknown", () =>
        {
            var files = new SecondFileFailure();
            files.Capacity = _ => new("controlled-volume", 4096, 43_128_000_000);
            files.Extent = (name, value) => new("controlled-volume", name == PilotStorageObservation.Marker ? "marker" : "shared", name == PilotStorageObservation.Marker ? 4096 : 8192, name == PilotStorageObservation.Marker ? value.Length : 17, 4096);
            using var session = PilotStorageObservation.Create(Path.Combine(root, "aliases"), files);
            File.WriteAllText(Path.Combine(session.Root, "first.txt"), "first\n");
            File.WriteAllText(Path.Combine(session.Root, "second.txt"), "second\n");
            var positive = session.Observe(binding, new[] { "first.txt", "second.txt" });
            Require(positive.Complete && positive.UsedBytes == 12288 && positive.Origin == "controlled-system-boundary", "Shared allocation was charged twice or declared native.");
            files.Extent = (name, value) => new("controlled-volume", name == PilotStorageObservation.Marker ? "marker" : "shared", name == PilotStorageObservation.Marker ? 4096 : name == "first.txt" ? 8192 : 16384, name == PilotStorageObservation.Marker ? value.Length : 17, 4096);
            Require(!session.Observe(binding, new[] { "first.txt", "second.txt" }).Complete, "Contradictory alias allocation became complete.");
            files.Extent = (name, value) => new("controlled-volume", name == PilotStorageObservation.Marker ? "marker" : "shared", name == PilotStorageObservation.Marker ? 4096 : 8192, name == PilotStorageObservation.Marker ? value.Length : name == "first.txt" ? 17 : 18, 4096);
            Require(!session.Observe(binding, new[] { "first.txt", "second.txt" }).Complete, "Contradictory alias EOF became complete.");
        });
        Check("known-zero-is-not-unavailable-and-changed-second-extent-stays-unknown", () =>
        {
            var files = new SecondFileFailure();
            files.Capacity = _ => new("controlled-volume", 4096, 0);
            files.Extent = (name, value) => value with { Volume = "controlled-volume", Identity = name, AllocationUnit = 4096, AllocatedBytes = 0 };
            using var session = PilotStorageObservation.Create(Path.Combine(root, "unknowns"), files);
            File.WriteAllText(Path.Combine(session.Root, "first.txt"), "first\n");
            File.WriteAllText(Path.Combine(session.Root, "second.txt"), "second\n");
            var zero = session.Observe(binding, new[] { "first.txt", "second.txt" });
            Require(zero.Complete && zero.UsedBytes == 0 && zero.Capacity.AvailableBytes == 0, "Known zero was confused with Unknown.");
            files.Capacity = _ => null;
            var unknown = session.Observe(binding, new[] { "first.txt", "second.txt" });
            Require(!unknown.Complete && unknown.UsedBytes == null && unknown.Capacity == null, "Unavailable capacity became known zero.");
            files.Capacity = _ => new("controlled-volume", 4096, 0);
            int reads = 0;
            // Change only the second extent on its reread.
            files.Extent = (name, value) => value with { Volume = "controlled-volume", Identity = name == "second.txt" && ++reads > 1 ? "changed" : name, AllocationUnit = 4096, AllocatedBytes = 0 };
            var changed = session.Observe(binding, new[] { "first.txt", "second.txt" });
            Require(!changed.Complete && changed.UsedBytes == null && changed.Error.Contains("changed during"), "Changed second extent produced a complete sum.");
        });
        Check("malformed-unit-or-oversized-extent-does-not-become-complete", () =>
        {
            var files = new SecondFileFailure();
            files.Capacity = _ => new("controlled-volume", 3, 0);
            files.Extent = (_, value) => value with { Volume = "controlled-volume", AllocationUnit = 3, AllocatedBytes = 0 };
            using var session = PilotStorageObservation.Create(Path.Combine(root, "malformed"), files);
            Require(!session.Observe(binding, Array.Empty<string>()).Complete, "Malformed allocation unit became a complete observation.");
            files.Capacity = _ => new("controlled-volume", 4096, 0);
            files.Extent = (_, value) => value with { Volume = "controlled-volume", AllocationUnit = 4096, Length = 1_048_577, AllocatedBytes = 0 };
            var oversized = session.Observe(binding, Array.Empty<string>());
            Require(!oversized.Complete && oversized.UsedBytes == null && oversized.Error.Contains("1MiB"), "Oversized synthetic extent was not rejected as a tiny-file boundary.");
        });
        Check("existing-roots-and-ancestors-remain-untouched", () =>
        {
            string collision = Path.Combine(root, "collision");
            Directory.CreateDirectory(collision);
            File.WriteAllText(Path.Combine(collision, "sentinel.txt"), "unrelated-sentinel");
            string before = Native.DirectoryIdentity(collision);
            bool rejected = false;
            try { using var unexpected = PilotStorageObservation.Create(collision); } catch (System.ComponentModel.Win32Exception) { rejected = true; }
            Require(rejected && Native.DirectoryIdentity(collision) == before && File.ReadAllText(Path.Combine(collision, "sentinel.txt")) == "unrelated-sentinel", "Existing root was reused or changed.");
            string regular = Path.Combine(root, "regular.txt");
            File.WriteAllText(regular, "regular-file");
            foreach (string path in new[] { Path.Combine(regular, "child"), Path.Combine(root, "missing-parent", "child"), Path.Combine(root, "..", "escape"), Path.Combine(root, "alias.") })
            {
                rejected = false;
                try { using var unexpected = PilotStorageObservation.Create(path); } catch (IOException) { rejected = true; }
                Require(rejected, "Invalid ancestor/lexical root was admitted.");
            }
            Require(File.ReadAllText(regular) == "regular-file", "Regular-file ancestor changed.");
        });
        Check("finite-declared-inventory-rejects-missing-duplicate-and-unlisted-files", () =>
        {
            using var session = PilotStorageObservation.Create(Path.Combine(root, "inventory"));
            Require(!session.Observe(binding, new[] { "missing.txt" }).Complete, "Missing file became an empty complete inventory.");
            foreach (var names in new[] { new[] { "same.txt", "same.txt" }, new[] { "../escape.txt" }, Enumerable.Range(0, 32).Select(i => "file" + i).ToArray() })
            {
                bool rejected = false;
                try { session.Observe(binding, names); } catch (IOException) { rejected = true; }
                Require(rejected, "Invalid declared inventory was admitted.");
            }
            File.WriteAllText(Path.Combine(session.Root, "extra.txt"), "retained-extra");
            var unlisted = session.Observe(binding, Array.Empty<string>());
            Require(!unlisted.Complete && unlisted.UsedBytes == null && File.ReadAllText(Path.Combine(session.Root, "extra.txt")) == "retained-extra", "Unlisted file was ignored, changed or deleted.");
        });
        Check("atomic-root-creation-collision-has-exactly-one-owner", () =>
        {
            string path = Path.Combine(root, "creation-race");
            PilotStorageObservation Attempt()
            {
                try { return PilotStorageObservation.Create(path); }
                catch (System.ComponentModel.Win32Exception) { return null; }
            }
            var tasks = new[] { Task.Run(Attempt), Task.Run(Attempt) };
            Task.WaitAll(tasks);
            var owners = tasks.Select(task => task.Result).Where(owner => owner != null).ToArray();
            try { Require(owners.Length == 1 && owners[0].Observe(binding, Array.Empty<string>()).Complete, "Creation collision did not retain exactly one real owner."); }
            finally { foreach (var owner in owners) owner.Dispose(); }
        });
        Check("malformed-storage-dispatch-is-rejected-without-initialization", () =>
        {
            foreach (var arguments in new[] { new[] { "--pilot-storage-checks" }, new[] { "--pilot-storage-unknown", "unused" }, new[] { "--worker", "--pilot-storage-checks", "unused" }, new[] { "--pilot-storage-checks", "unused", "--engine-root", "unused" } })
            {
                var output = Command(arguments);
                using var result = JsonDocument.Parse(output.Json);
                Require(output.Code == 2 && result.RootElement.GetProperty("kind").GetString() == "storage-input-rejected", "Malformed command left the storage-only rejection route.");
            }
        });
        Check("native-command-keeps-storage-evidence-separate-from-supplied-binding", () =>
        {
            string target = Path.Combine(root, "command-root"), request = Path.Combine(root, "command-request.json");
            File.WriteAllText(request, JsonSerializer.Serialize(new { root = target, plan_sha256 = binding.PlanSha256, source_manifest_sha256 = binding.SourceManifestSha256, cutoff = binding.Cutoff }));
            var output = Command("--pilot-storage-observe", request);
            using var document = JsonDocument.Parse(output.Json);
            var value = document.RootElement;
            Require(output.Code == 0 && value.GetProperty("native_storage").GetProperty("complete").GetBoolean(), "Native observation command failed.");
            Require(value.GetProperty("provenance").GetString() == "caller-supplied-unverified" && !value.GetProperty("facts_verified_natively").GetBoolean() && !value.GetProperty("execution_enabled").GetBoolean(), "Serialization upgraded supplied/native scope or execution permission.");
            Require(value.GetProperty("native_storage").GetProperty("files").GetProperty("first.txt").GetProperty("length").GetInt64() == 6, "Command did not observe the literal fixture EOF.");
            File.WriteAllText(Path.Combine(root, "native-command.json"), output.Json);
        });
        Check("forged-native-request-label-does-not-initialize-or-authorize-observation", () =>
        {
            string target = Path.Combine(root, "forged-target"), request = Path.Combine(root, "forged-request.json");
            File.WriteAllText(request, JsonSerializer.Serialize(new { root = target, plan_sha256 = binding.PlanSha256, source_manifest_sha256 = binding.SourceManifestSha256, cutoff = binding.Cutoff, native_verified = true }));
            var output = Command("--pilot-storage-observe", request);
            using var value = JsonDocument.Parse(output.Json);
            Require(output.Code == 2 && !Directory.Exists(target) && !value.RootElement.GetProperty("native_storage_complete").GetBoolean(), "Forged native label initialized a root or was accepted.");
        });
        var report = new { result = failed == 0 ? "passed" : "failed", checks, engine = false, gpu = false, nativeUi = false };
        string json = PilotStorageObservation.SerializeBounded(report);
        File.WriteAllText(Path.Combine(root, "verification.json"), json);
        Console.WriteLine(json);
        return failed == 0 ? 0 : 1;
    }
}
