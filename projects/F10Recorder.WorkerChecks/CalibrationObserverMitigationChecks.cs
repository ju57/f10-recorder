using System.Diagnostics;
using System.Text.Json;

namespace F10Recorder;

// Finite, nonmedia verification. Synthetic inventory cost is bookkeeping only;
// the tiny native leaf checks diagnostic wiring, not capture performance.
internal static class CalibrationObserverMitigationChecks
{
    static readonly string[] Labels = ["T01", "T02", "T03", "H02", "H01", "H03"];
    static void Require(bool condition, string message) { if (!condition) throw new IOException(message); }
    static void Reject(Action action)
    { try { action(); } catch (IOException) { return; } catch (System.ComponentModel.Win32Exception) { return; } throw new Exception("Expected storage rejection."); }
    // Original sampler's repeated whole-inventory filters, retained as the
    // equivalence oracle and cost baseline. No native calls in either branch.
    static Dictionary<string, long> Reference(Dictionary<string, ReplayFileExtent> inventory, string root, string[] retained)
    {
        var result = new Dictionary<string, long>();
        string rootPrefix = root + Path.DirectorySeparatorChar;
        result["current"] = ReplayStorage.Bytes(inventory.Where(i => i.Key.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)).ToDictionary());
        result["retained"] = ReplayStorage.Bytes(inventory.Where(i => retained.Any(p => i.Key.StartsWith(p, StringComparison.OrdinalIgnoreCase))).ToDictionary());
        foreach (string label in Labels)
        {
            string prefix = Path.Combine(root, label) + Path.DirectorySeparatorChar;
            var members = inventory.Where(i => i.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            result[label + ":source"] = ReplayStorage.Bytes(members.Where(i => Path.GetFileName(i.Key).StartsWith("chunk-")).ToDictionary());
            result[label + ":output"] = ReplayStorage.Bytes(members.Where(i => i.Key.EndsWith(".mp4")
                || i.Key.Contains(".partial.mp4.work" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToDictionary());
            string buffer = Path.Combine(root, label, "buffer") + Path.DirectorySeparatorChar;
            foreach (var item in inventory.Where(i => i.Key.StartsWith(buffer, StringComparison.OrdinalIgnoreCase)
                && (Path.GetFileName(i.Key).StartsWith("chunk-", StringComparison.Ordinal) || Path.GetFileName(i.Key) == "segments.csv")))
                result["capture:" + item.Key] = item.Value.AllocatedBytes;
        }
        foreach (var group in inventory.Where(i => i.Key.Contains(".partial.mp4.work" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .GroupBy(i => Path.GetDirectoryName(i.Key), StringComparer.OrdinalIgnoreCase))
            result["workspace:" + group.Key[..^5]] = ReplayStorage.Bytes(group.ToDictionary());
        foreach (var item in inventory.Where(i => i.Key.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))) result["output:" + item.Key] = item.Value.AllocatedBytes;
        return result;
    }
    static Dictionary<string, long> Candidate(Dictionary<string, ReplayFileExtent> inventory, string root, string[] retained)
    {
        var accounting = new CalibrationInventoryAccounting(inventory, root, Labels, retained, true);
        var result = new Dictionary<string, long> { ["current"] = ReplayStorage.Bytes(accounting.Current), ["retained"] = ReplayStorage.Bytes(accounting.Retained) };
        foreach (var cell in accounting.Cells)
        {
            result[cell.Key + ":source"] = ReplayStorage.Bytes(cell.Value.Sources);
            result[cell.Key + ":output"] = ReplayStorage.Bytes(cell.Value.OutputsAndWork);
            foreach (var item in cell.Value.Capture) result["capture:" + item.Key] = item.Value.AllocatedBytes;
        }
        foreach (var group in accounting.Workspaces) result["workspace:" + group.Key] = ReplayStorage.Bytes(group.Value);
        foreach (var item in accounting.Outputs) result["output:" + item.Key] = item.Value.AllocatedBytes;
        return result;
    }
    sealed record Cost(double Milliseconds, long AllocatedBytes);
    static Cost Measure(Func<Dictionary<string, long>> run)
    {
        long allocated = GC.GetAllocatedBytesForCurrentThread(), at = Stopwatch.GetTimestamp();
        var result = run(); double ms = Stopwatch.GetElapsedTime(at).TotalMilliseconds;
        GC.KeepAlive(result); return new(ms, GC.GetAllocatedBytesForCurrentThread() - allocated);
    }
    static object Describe(List<Cost> rows)
    {
        var times = rows.Select(r => r.Milliseconds).Order().ToArray();
        return new { iterations = rows.Count, medianMs = (times[15] + times[16]) / 2, p95Ms = times[30], maximumMs = times[^1],
            meanAllocatedBytes = rows.Average(r => (double)r.AllocatedBytes) };
    }
    internal static int Run(string root)
    {
        Require(root.StartsWith(Path.GetFullPath("artifacts") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !Directory.Exists(root) && !File.Exists(root), "Fresh artifacts root required.");
        Directory.CreateDirectory(root);
        var whole = Stopwatch.StartNew(); var rows = new List<object>(); int failed = 0;
        void Check(string name, Action action)
        {
            try { action(); rows.Add(new { name, passed = true }); }
            catch (Exception ex) { failed++; rows.Add(new { name, passed = false, error = ex.ToString() }); }
        }
        Check("observed574ms-pattern-still-rejects-before-publication", () =>
        {
            long now = 0; bool published = false;
            var gate = new ReplayStorageObservationGate(() => { now += 4838204; return true; }, 100, 500,
                timestamp: () => now, frequency: 10000000);
            now = 904183; Reject(() => { gate.CheckNow(); published = true; });
            Require(!published && Math.Abs(now / 10000d - 574.2387) < .0001, "Historical work/wait accounting changed.");
            Reject(() => ReplayCalibrationRun.SamplingGap(0, 574.2387));
        });
        Check("scheduling-pauses-and-incomplete-observations-never-refresh", () =>
        {
            long now = 0; int called = 0;
            var gate = new ReplayStorageObservationGate(() => { called++; return false; }, 100, 500, timestamp: () => now, frequency: 1000);
            now = 100; gate.Check(); now = 499; gate.Check(); now = 501; Reject(gate.Check);
            Require(called == 2, "Deferred work renewed freshness.");
            now = 0; gate = new(() => { now = 5001; return true; }, 100, 500, timestamp: () => now, frequency: 1000); Reject(gate.CheckNow);
        });
        Check("low-capacity-unknown-growth-and-volume-change-stop", () =>
        {
            var allowance = new ReplayCaptureHeadroom("fixture", "volume", 4096, 8192, 16384);
            Require(ReplayStorageSafety.Assess(new("volume", 4096, 2_000_000_000L + 8192 + 16384 - 1), allowance).ShouldStop, "Growth allowance not charged.");
            Require(ReplayStorageSafety.Assess(new("volume", 4096, 100_000_000_000), null).ShouldStop, "Unknown growth treated as zero.");
            Require(ReplayStorageSafety.Assess(new("other", 4096, 100_000_000_000), allowance).ShouldStop, "Volume change accepted.");
        });
        var inventory = new Dictionary<string, ReplayFileExtent>(StringComparer.OrdinalIgnoreCase);
        var retained = new[] { Path.Combine(root, "history") + Path.DirectorySeparatorChar };
        foreach (string label in Labels)
        {
            for (int i = 0; i < 300; i++) inventory.Add(Path.Combine(root, label, "buffer", $"chunk-{i:D9}.ts"), new("vol", label + i, 4096L * (i + 1), i, 4096));
            inventory.Add(Path.Combine(root, label, "buffer", "segments.csv"), new("vol", label + "manifest", 4096, 100, 4096));
            string output = Path.Combine(root, label, "save.partial.mp4"); inventory.Add(output, new("vol", label + "output", 32768, 30000, 4096));
            inventory.Add(Path.Combine(output + ".work", "assembly.mp4"), new("vol", label + "assembly", 8192, 8000, 4096));
            inventory.Add(Path.Combine(output + ".work", "alias.mp4"), new("vol", label + "assembly", 8192, 8000, 4096));
        }
        for (int i = 0; i < 128; i++) inventory.Add(Path.Combine(root, "history", $"leaf-{i}"), new("vol", "old" + i, 4096, 0, 4096));
        inventory.Add(Path.Combine(root, "T010", "chunk-not-T01.ts"), new("vol", "prefix-neighbor", 4096, 0, 4096));
        Check("partition-preserves-reference-membership-and-hardlink-accounting", () =>
        {
            var expected = Reference(inventory, root, retained); var actual = Candidate(inventory, root, retained);
            Require(expected.Count == actual.Count && expected.All(i => actual.TryGetValue(i.Key, out long value) && value == i.Value), "Accounting/growth membership changed.");
            var conflict = new Dictionary<string, ReplayFileExtent>(inventory);
            string alias = Path.Combine(root, "T01", "save.partial.mp4.work", "alias.mp4"); conflict[alias] = conflict[alias] with { AllocatedBytes = 16384 };
            Reject(() => Reference(conflict, root, retained)); Reject(() => Candidate(conflict, root, retained));
            var nested = new Dictionary<string, ReplayFileExtent>(inventory)
            { [Path.Combine(root, "T01", "save.partial.mp4.work", "nested", "leaf")] = new("vol", "nested", 4096, 0, 4096) };
            Reject(() => Candidate(nested, root, retained));
        });
        var before = new List<Cost>(); var after = new List<Cost>();
        Check("finite32-paired-bookkeeping-measurements", () =>
        {
            _ = Reference(inventory, root, retained); _ = Candidate(inventory, root, retained);
            for (int i = 0; i < 32; i++)
            {
                Require(whole.Elapsed.TotalSeconds < 20, "Finite verification deadline exceeded.");
                if ((i & 1) == 0) { before.Add(Measure(() => Reference(inventory, root, retained))); after.Add(Measure(() => Candidate(inventory, root, retained))); }
                else { after.Add(Measure(() => Candidate(inventory, root, retained))); before.Add(Measure(() => Reference(inventory, root, retained))); }
            }
        });
        var metadata = new CalibrationFileOperationSummary();
        Check("native-TS-open-observe-close-identity-failure-and-closure", () =>
        {
            string leaf = Path.Combine(root, "tiny.ts"); File.WriteAllBytes(leaf, new byte[4096]); var extent = ReplayStorage.Observe(leaf);
            using var reader = new ReplayInventoryReader([], _ => false) { OperationObserved = metadata.Record };
            reader.BeginScan(); var observed = reader.ObserveInventory(leaf, extent); reader.EndScan();
            Require(observed.Identity == extent.Identity && reader.HandleCount == 0, "Short-lived ownership changed.");
            Reject(() => reader.ObserveInventory(leaf, extent with { Identity = "wrong" }));
            File.Delete(leaf); Reject(() => reader.ObserveInventory(leaf, extent));
            var snapshot = metadata.Snapshot();
            Require(snapshot.Count == 3 && snapshot["ts:open"].Count == 3 && snapshot["ts:observe"].Count == 2
                && snapshot["ts:close"].Count == 2 && snapshot["ts:open"].Failures == 1 && snapshot["ts:observe"].Failures == 1, "TS telemetry missing.");
            Require(snapshot.Values.All(e => e.Slowest.ThreadBefore == e.Slowest.ThreadAfter && e.Slowest.ThreadBefore != 0), "Thread context missing.");
        });
        var nativeMetadata = metadata.Snapshot();
        Check("diagnostic-failure-preserves-native-error-and-handle-closure", () =>
        {
            string leaf = Path.Combine(root, "diagnostic.ts"); File.WriteAllBytes(leaf, new byte[4096]); var extent = ReplayStorage.Observe(leaf);
            using var reader = new ReplayInventoryReader([], _ => false) { OperationObserved = _ => throw new InvalidOperationException("Injected diagnostic failure") };
            reader.ObserveInventory(leaf, extent);
            Require(reader.TimingFailure == nameof(InvalidOperationException) && reader.HandleCount == 0, "Diagnostic failure was not retained.");
            bool primary = false;
            try { reader.ObserveInventory(leaf, extent with { Identity = "wrong" }); }
            catch (IOException) { primary = true; }
            Require(primary, "Diagnostic failure replaced the native identity error.");
            using (new FileStream(leaf, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            File.Delete(leaf);
        });
        Check("metadata-summary-is-bounded-independent-of-file-count", () =>
        {
            for (int i = 0; i < 10000; i++) metadata.Record(new(new string('x', 1024) + ".ts", "open", i, true, 1, 1, null, 0));
            Require(metadata.Snapshot().Count == 3 && metadata.Snapshot().Values.All(e => e.Slowest.Path.Length <= 512), "Metadata grew without bound.");
        });
        File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new { result = failed == 0 ? "passed" : "failed", failed,
            seconds = whole.Elapsed.TotalSeconds, rows, syntheticInventoryFiles = inventory.Count,
            reference = before.Count == 32 ? Describe(before) : null, candidate = after.Count == 32 ? Describe(after) : null, nativeMetadata,
            limits = "32 paired in-memory iterations;20s measurement guard;two sequential4KiB native fixtures;no capture/engine/audio/UI/catalog;500ms unchanged",
            interpretation = "Accounting microbenchmark only, not full observer/capture performance. Original slow native event remains unexplained. Logical clock jumps do not reproduce a blocked native call or production shutdown." }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Observer mitigation: {rows.Count} checks, {failed} failures; {root}"); return failed == 0 ? 0 : 1;
    }
}
