using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace F10Recorder;

internal sealed record PilotStorageBinding(string PlanSha256, string SourceManifestSha256, string Cutoff);

internal sealed record PilotStorageSnapshot(bool Complete, string Origin, string Root, string RootIdentity,
    ReplayVolumeCapacity Capacity, IReadOnlyDictionary<string, ReplayFileExtent> Files, long? UsedBytes,
    string Error, DateTime StartedUtc, DateTime CompletedUtc, long ElapsedMilliseconds,
    long CompletedTimestamp, PilotStorageBinding SuppliedBinding)
{
    internal object ToPreparationObservation(PilotStorageBinding supplied)
    {
        if (supplied != SuppliedBinding) throw new IOException("Snapshot supplied binding changed during preparation conversion.");
        var binding = new { source_manifest_sha256 = supplied.SourceManifestSha256, cutoff = supplied.Cutoff,
            destination = Root, volume = Capacity?.Volume, allocation_unit = Capacity?.AllocationUnit };
        return new { provenance = "caller-supplied-unverified", plan_sha256 = supplied.PlanSha256,
            snapshot_binding = binding, current_binding = binding, capacity_status = Complete ? "known" : "unknown",
            age_ms = Math.Max(0, (long)Stopwatch.GetElapsedTime(CompletedTimestamp).TotalMilliseconds), used_bytes = UsedBytes,
            next_unmaterialized_bytes = 2_000_000_000L, unwritten_closure_bytes = 128_000_000L,
            poll_reserve_bytes = 1_000_000_000L, caller_free_bytes = Complete ? Capacity?.AvailableBytes : null,
            native_storage = this, execution_enabled = false, facts_verified_natively = false };
    }
}

// Verification-only fresh-root owner. Native measurements reuse production storage calls.
internal sealed class PilotStorageObservation : IDisposable
{
    internal const string Marker = ".pilot-owned.json";
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true };
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateDirectory(string path, IntPtr security);
    readonly List<SafeFileHandle> anchors = [];
    readonly ReplayFiles files;
    string identity;
    PilotStorageBinding suppliedBinding;
    ReplayVolumeCapacity destination;
    bool disposed;
    internal string Root { get; }

    PilotStorageObservation(string root, ReplayFiles files) { Root = root; this.files = files; }

    static void Require(bool condition, string message) { if (!condition) throw new IOException(message); }
    static void ValidateBinding(PilotStorageBinding binding)
    {
        bool Hash(string value) => value?.Length == 64 && value.All(Uri.IsHexDigit);
        Require(binding != null && Hash(binding.PlanSha256) && Hash(binding.SourceManifestSha256)
            && !string.IsNullOrWhiteSpace(binding.Cutoff) && binding.Cutoff.Length <= 256, "Supplied plan/source/cutoff binding required.");
    }
    static void PinParents(string parent, List<SafeFileHandle> retained)
    {
        for (; parent != null; parent = Path.GetDirectoryName(parent))
        {
            var attributes = File.GetAttributes(parent);
            Require((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) == FileAttributes.Directory, "Root ancestry must be ordinary existing directories.");
            var anchor = Native.AnchorDirectory(parent);
            retained.Add(anchor);
            Require(string.Equals(Native.FinalDirectoryPath(anchor).TrimEnd('\\'), parent.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) == 0, "Unverified directory alias or changed ancestry.");
        }
    }
    internal static string Artifacts => Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "artifacts"));
    internal static PilotStorageObservation Create(string root, ReplayFiles files = null)
    {
        Require(!string.IsNullOrWhiteSpace(root) && !root.Replace('\\', '/').Split('/').Contains(".."), "Lexical root escape is not supported.");
        Require(root.Length <= 4096 && !root.StartsWith(@"\\") && !root.Any(char.IsControl)
            && !root.Replace('\\', '/').Split('/').Any(part => part.EndsWith('.') || part.EndsWith(' ')), "Unverified lexical root alias is unsupported.");
        string full = Path.GetFullPath(root), relative = Path.GetRelativePath(Artifacts, full);
        Require(relative != "." && !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar), "Root must be a fresh child of the working repository artifacts.");
        var session = new PilotStorageObservation(full, files ?? new ReplayFiles());
        try
        {
            PinParents(Path.GetDirectoryName(full), session.anchors);
            // Atomic failure on preexisting directory/file, including creation races.
            if (!CreateDirectory(full, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Fresh root creation failed; prior material is retained.");
            session.anchors.Insert(0, Native.AnchorDirectory(full));
            Require(string.Equals(Native.FinalDirectoryPath(session.anchors[0]), full, StringComparison.OrdinalIgnoreCase), "Created root is an unsupported alias.");
            session.identity = Native.DirectoryIdentity(session.anchors[0]);
            using (var marker = new FileStream(Path.Combine(full, Marker), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                JsonSerializer.Serialize(marker, new { kind = "task-owned-storage-fixture", root_identity = session.identity, nonce = Guid.NewGuid() });
            return session;
        }
        catch { session.Dispose(); throw; }
    }

    internal PilotStorageSnapshot Observe(PilotStorageBinding binding, IReadOnlyList<string> declared)
    {
        Require(!disposed, "Session is disposed.");
        ValidateBinding(binding);
        Require(declared != null && declared.Count <= 31, "At most31declared files plus owned marker.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Marker };
        foreach (string name in declared)
            Require(!string.IsNullOrWhiteSpace(name) && name.Length <= 128 && name != "." && name != ".."
                && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !name.Contains('/') && !name.Contains('\\') && names.Add(name), "Invalid or duplicate retained leaf path.");
        void ValidateInventory()
        {
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in Directory.EnumerateFileSystemEntries(Root))
            {
                Require(found.Count < 32, "Owned inventory exceeds32files.");
                Require((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0, "Nested directory or reparse entry is unsupported.");
                Require(names.Contains(Path.GetFileName(path)) && found.Add(Path.GetFileName(path)), "Unlisted or duplicate retained entry.");
            }
            Require(found.SetEquals(names), "Declared retained inventory is missing an entry.");
        }
        DateTime start = DateTime.UtcNow;
        var timer = Stopwatch.StartNew();
        var pinned = new List<FileStream>();
        var inventory = new Dictionary<string, ReplayFileExtent>(StringComparer.OrdinalIgnoreCase);
        ReplayVolumeCapacity capacity = null;
        string origin = files.GetType() == typeof(ReplayFiles) ? "native-storage" : "controlled-system-boundary";
        try
        {
            suppliedBinding ??= binding;
            Require(suppliedBinding == binding, "Supplied plan/source/cutoff binding changed.");
            Require(Native.DirectoryIdentity(Root) == identity, "Owned root identity changed.");
            ValidateInventory();
            capacity = ReplayStorage.ValidateCapacity(files.ObserveCapacity(Root));
            Require(capacity.AllocationUnit <= 1_048_576 && (capacity.AllocationUnit & (capacity.AllocationUnit - 1)) == 0, "Unsupported allocation unit for the preparation contract.");
            destination ??= capacity;
            Require(capacity.Volume.Equals(destination.Volume, StringComparison.OrdinalIgnoreCase) && capacity.AllocationUnit == destination.AllocationUnit, "Destination volume/unit binding changed.");
            foreach (string name in new[] { Marker }.Concat(declared))
            {
                string path = Path.Combine(Root, name);
                pinned.Add(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));
                var extent = ReplayStorage.Validate(files.Observe(path));
                Require(pinned[^1].Length <= 1_048_576 && extent.Length <= 1_048_576, "Retained extent exceeds the current1MiB tiny-file scope.");
                Require(extent.Volume.Equals(capacity.Volume, StringComparison.OrdinalIgnoreCase) && extent.AllocationUnit == capacity.AllocationUnit, "Retained extent volume/unit differs from destination.");
                inventory.Add(name, extent);
            }
            foreach (var entry in inventory)
                Require(files.Observe(Path.Combine(Root, entry.Key)) == entry.Value, "Retained extent changed during observation.");
            ValidateInventory();
            var final = ReplayStorage.ValidateCapacity(files.ObserveCapacity(Root));
            Require(final.Volume.Equals(capacity.Volume, StringComparison.OrdinalIgnoreCase) && final.AllocationUnit == capacity.AllocationUnit && Native.DirectoryIdentity(Root) == identity, "Destination identity/volume/unit changed.");
            Require(timer.ElapsedMilliseconds <= 2000, "Storage observation exceeded2seconds.");
            foreach (var aliases in inventory.Values.GroupBy(value => (value.Volume.ToUpperInvariant(), value.Identity.ToUpperInvariant())))
                Require(aliases.All(value => value.Length == aliases.First().Length && value.AllocationUnit == aliases.First().AllocationUnit), "Shared extent EOF/unit observations contradict each other.");
            return new(true, origin, Root, identity,
                final, inventory, ReplayStorage.Bytes(inventory), null, start, DateTime.UtcNow, timer.ElapsedMilliseconds,
                Stopwatch.GetTimestamp(), suppliedBinding);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or OverflowException)
        {
            // Never convert a missing second member to a smaller complete sum.
            return new(false, origin, Root, identity, capacity, inventory, null, error.Message,
                start, DateTime.UtcNow, timer.ElapsedMilliseconds, Stopwatch.GetTimestamp(), suppliedBinding);
        }
        finally { foreach (var file in pinned) file.Dispose(); }
    }

    sealed record Request(string Root, string PlanSha256, string SourceManifestSha256, string Cutoff);
    internal static string SerializeBounded(object value)
    {
        string json = JsonSerializer.Serialize(value, Json);
        Require(System.Text.Encoding.UTF8.GetByteCount(json) <= 131072, "Storage JSON exceeds128KiB.");
        return json;
    }
    internal static int Run(string request)
    {
        string path = Path.GetFullPath(request), relative = Path.GetRelativePath(Artifacts, path);
        Require(!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar), "Request must be an owned artifact JSON path.");
        var parents = new List<SafeFileHandle>();
        try
        {
            PinParents(Path.GetDirectoryName(path), parents);
            Require((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0, "Request is not an ordinary file.");
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Require(input.Length <= 1_048_576 && string.Equals(Native.FinalDirectoryPath(input.SafeFileHandle), path, StringComparison.OrdinalIgnoreCase)
                && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0, "Request is oversized or an unverified alias.");
            var options = new JsonSerializerOptions(Json) { MaxDepth = 8, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
            var supplied = JsonSerializer.Deserialize<Request>(input, options) ?? throw new IOException("Request object required.");
            var binding = new PilotStorageBinding(supplied.PlanSha256, supplied.SourceManifestSha256, supplied.Cutoff);
            ValidateBinding(binding);
            Require(supplied.Root != null && Path.IsPathFullyQualified(supplied.Root), "Request root must be the exact absolute preview destination.");
            using var session = Create(supplied.Root);
            foreach (var fixture in new[] { ("first.txt", "first\n"), ("second.txt", "second\n") })
                using (var file = new StreamWriter(new FileStream(Path.Combine(session.Root, fixture.Item1), FileMode.CreateNew, FileAccess.Write, FileShare.Read))) file.Write(fixture.Item2);
            var result = session.Observe(binding, new[] { "first.txt", "second.txt" });
            Console.WriteLine(SerializeBounded(result.ToPreparationObservation(binding)));
            return result.Complete ? 0 : 1;
        }
        finally { foreach (var parent in parents) parent.Dispose(); }
    }
    internal static int Dispatch(string[] args)
    {
        try
        {
            Require(args.Length == 2 && args[1].Length <= 4096, "Storage-only command requires exactly one bounded path argument.");
            Require(!args[1].Replace('\\', '/').Split('/').Contains(".."), "Lexical input escape is unsupported.");
            if (args[0] == "--pilot-storage-checks") return PilotStorageObservationChecks.Run(Path.GetFullPath(args[1]));
            if (args[0] == "--pilot-storage-observe") return Run(args[1]);
            throw new IOException("Unknown storage-only entry point.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException or NotSupportedException or JsonException or OverflowException)
        {
            Console.WriteLine(SerializeBounded(new { kind = "storage-input-rejected", error = error.Message, execution_enabled = false, native_storage_complete = false }));
            return 2;
        }
    }
    public void Dispose()
    {
        disposed = true;
        foreach (var anchor in anchors) anchor.Dispose();
        anchors.Clear();
    }
}
