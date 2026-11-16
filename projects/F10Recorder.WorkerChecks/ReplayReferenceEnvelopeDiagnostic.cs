using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace F10Recorder;

// Read-only reconstruction, not calibration, a replay save or a numeric claim.
internal static class ReplayReferenceEnvelopeDiagnostic
{
    sealed record Plan(string Kind, string Root, string SourceDirectory, string Manifest, string Model,
        string EngineRoot, double RequestedEnd, DateTime ExpiresUtc, Dictionary<string, string> Files);
    sealed class ObservedFiles(Dictionary<string, ReplayFileExtent> observed) : ReplayFiles
    {
        internal override ReplayFileExtent Observe(string path) => observed[Path.GetFullPath(path)];
    }
    static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
    static void Require(bool value, string reason) { if (!value) throw new IOException(reason); }
    internal static int Run(string path, string expectedHash)
    {
        var clock = Stopwatch.StartNew();
        Require(new FileInfo(path).Length is > 0 and <= 65536 && Hash(path) == expectedHash, "Exact diagnostic plan required.");
        var p = JsonSerializer.Deserialize<Plan>(File.ReadAllText(path));
        Require(p.Kind == "issue83-reference-envelope-diagnostic-v1" && DateTime.UtcNow.AddSeconds(30) < p.ExpiresUtc
            && !Directory.Exists(p.Root) && !File.Exists(p.Root)
            && Path.GetFullPath(p.Root).StartsWith(Path.GetFullPath("artifacts") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "Fresh bounded diagnostic scope required.");
        foreach (var file in p.Files) Require(Hash(file.Key) == file.Value, "Changed diagnostic binding: " + file.Key);
        Require(new FileInfo(p.Manifest).Length <= 65536 && new FileInfo(p.Model).Length <= 65536, "Small original metadata only.");
        Directory.CreateDirectory(p.Root); Log.DirectoryPath = p.Root;
        MediaDependency.RootDirectory = p.EngineRoot; // Verified reuse, no repair or probe.
        var model = JsonSerializer.Deserialize<ReplayReferenceModel>(File.ReadAllText(p.Model));
        var segments = ReplaySelection.Read(p.Manifest, p.SourceDirectory);
        Require(segments.Count == 302 && double.IsFinite(p.RequestedEnd), "Exact preserved H03 manifest required.");
        var before = ReplayStorage.Inventory([p.SourceDirectory], new ReplayFiles(), 512, 4);
        var files = new ObservedFiles(before);
        var info = new ReplaySessionInfo(p.SourceDirectory, p.Manifest, 300, 20, 60, "h264_nvenc", ".ts", false, true);
        var rows = new List<object>(); int failed = 0;
        using var process = Process.GetCurrentProcess();
        void Bound()
        {
            process.Refresh();
            Require(clock.Elapsed.TotalSeconds < 20 && process.WorkingSet64 <= 268435456 && process.HandleCount <= 4096,
                "Diagnostic time/process bound.");
        }
        ReplayOutputSelection Selection(int last, double end)
        {
            var available = segments.Take(last + 1).ToList();
            var interval = ReplayInterval.AtRequest(available, end, 300);
            var selected = ReplaySelection.Select(available, end, interval.Seconds + ReplaySaveExport.Preroll(info));
            return ReplayOutputSelection.Create(info, ReplayReferenceModel.ExporterIdentity, p.Root, end,
                available[^1].End >= end, selected, files);
        }
        void Case(string name, ReplayOutputSelection selection, bool numeric, bool synthetic = false, string scope = null)
        {
            Bound();
            double duration = selection.End - selection.Start;
            double span = selection.Inputs.Count == 0 ? 0 : selection.End - selection.Inputs[0].Start;
            double? lastEnd = selection.Inputs.Count == 0 ? null : selection.Inputs[^1].End;
            double? tailGap = lastEnd.HasValue ? selection.End - lastEnd.Value : null;
            var demand = model.Estimate(selection, scope ?? model.Scope, p.SourceDirectory);
            bool actual = demand.Kind == ReplayOutputEvidenceKind.CalibratedEstimate;
            if (actual != numeric) failed++;
            rows.Add(new { name, syntheticGuardControl = synthetic, reconstructedPrefix = !synthetic,
                exactPressTimePrefixKnown = false, expectedNumeric = numeric, actualNumeric = actual, demand.Kind, demand.Reason,
                selection.Start, selection.End, selection.Closed, inputCount = selection.Inputs.Count,
                firstStart = selection.Inputs.Count == 0 ? (double?)null : selection.Inputs[0].Start, lastEnd, duration, span, tailGap,
                predicates = new {
                    scopeMatches = (scope ?? model.Scope) == model.Scope, exporterMatches = selection.ExporterIdentity == ReplayReferenceModel.ExporterIdentity,
                    finiteDuration = double.IsFinite(duration), minimumDuration = duration >= 1 - .000000001,
                    maximumDuration = duration <= 300.000001, minimumHeadSpan = span >= duration - .000001,
                    maximumHeadSpan = span <= duration + 1.1, minimumInputs = selection.Inputs.Count >= 1,
                    maximumInputs = selection.Inputs.Count <= 305,
                    openTailPositive = selection.Closed || tailGap > 0, openTailWithinLimit = selection.Closed || tailGap <= 1.25 },
                diagnosticOnly = "No output/capacity reservation, export, catalog, publication or Saved is performed." });
        }
        foreach (int last in new[] { 298, 299, 300, 301 })
            Case("preserved-prefix-" + last, Selection(last, p.RequestedEnd), last >= 300);
        double edge = segments[299].End + 1.25;
        Case("tail-below-1.25", Selection(299, edge - .000001), true);
        Case("tail-exact-1.25", Selection(299, edge), true);
        Case("tail-above-1.25", Selection(299, edge + .000001), false);
        var complete = Selection(301, p.RequestedEnd);
        ReplayOutputSelection Count(int count) => complete with {
            Inputs = Enumerable.Range(0, count).Select(n => complete.Inputs[Math.Min(n, complete.Inputs.Count - 1)]).ToArray() };
        Case("synthetic-count-305", Count(305), true, true);
        Case("synthetic-count-306", Count(306), false, true);
        Case("synthetic-duration-too-long", complete with { Start = complete.Start - .000002 }, false, true);
        var head = complete.Inputs.ToArray(); head[0] = head[0] with { Start = complete.Start - 1.100002 };
        Case("synthetic-head-span-too-long", complete with { Inputs = head }, false, true);
        Case("synthetic-wrong-scope", complete, false, true, "not-the-preserved-scope");
        var after = ReplayStorage.Inventory([p.SourceDirectory], new ReplayFiles(), 512, 4);
        Require(before.Count == after.Count && before.All(pair => after.TryGetValue(pair.Key, out var value) && pair.Value == value),
            "Original source native namespace/identity/EOF/allocation changed during reconstruction.");
        Bound();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { result = failed == 0 ? "passed-reconstruction-controls" : "failed",
            failed, modelQualified = false, exactLivePrefixKnown = false, requestedEndFromLog = p.RequestedEnd,
            productWindows = ReplayReferenceModel.AssemblyHash, productCore = ReplayReferenceModel.CoreHash,
            model.Revision, observedOriginalFiles = before.Count, sourceIdentityEofAllocationUnchanged = true,
            requiresSuccessfulController = true, rows,
            interpretation = "Hypothetical availability prefixes reconstructed from the drained original manifest. They do not establish the actual press-time prefix, qualify the model, retry H03, relax 1.25 seconds or move the requested endpoint.",
            seconds = clock.Elapsed.TotalSeconds }, new JsonSerializerOptions { WriteIndented = true });
        Require(bytes.Length <= 1048576, "Diagnostic report bound.");
        File.WriteAllBytes(Path.Combine(p.Root, "result.json"), bytes); Bound();
        Console.WriteLine($"Reference envelope diagnostic: {rows.Count} cases, {failed} failures; no media or publication.");
        return failed == 0 ? 0 : 1;
    }
}
