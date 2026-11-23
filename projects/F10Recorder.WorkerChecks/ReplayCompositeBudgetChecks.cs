using System.Text.Json;

namespace F10Recorder;

internal static partial class ReplayCalibrationRun
{
    // Tiny admission-state checks only: no capture, engine, catalog, media or
    // qualification. The reference plan supplies the existing frozen recipe.
    internal static int CompositeBudgetChecks(string root, string reference)
    {
        Require(!Directory.Exists(root) && new FileInfo(reference).Length <= 65536, "Fresh small budget fixture required.");
        using var document = JsonDocument.Parse(File.ReadAllText(reference));
        var original = document.RootElement.GetProperty("plan").Deserialize<CalibrationPlan>() with {
            ReplayExporterRecipe = ReplaySaveExport.PolicyIdentity, CopyFallbackReserveBytes = 1_000_000_000,
            AdditionalHistoricalReserveBytes = 0, RetainedBudgetBytes = 30_000_000_000 };
        Directory.CreateDirectory(root);
        string prior = Path.Combine(root, "prior"); Directory.CreateDirectory(prior);
        var continuation = original with { Root = Path.Combine(root, "future"), CompositeTrainingRoot = prior,
            Order = Order.Skip(2).ToArray(), RetainedRoots = [prior], MetadataHandleRoots = [prior],
            RetainedChildren = 21, RetainedElapsedSeconds = 1147, RetainedOfflineSeconds = 70,
            MaximumChildren = 58, ValidationChildren = 12 };
        var rows = new List<object>(); int failed = 0;
        void Check(string name, CalibrationPlan plan, bool expected)
        {
            bool accepted = false; string error = null;
            try { ValidateCompositeResources(plan); accepted = true; } catch (IOException ex) { error = ex.Message; }
            bool passed = accepted == expected; if (!passed) failed++;
            rows.Add(new { name, passed, expected, accepted, error });
        }
        Check("actual-1147-seconds-and-conservative-58-roles", continuation, true);
        var fresh = original with { Root = Path.Combine(root, "fresh"), CompositeTrainingRoot = null, Order = Order,
            RetainedRoots = [prior], MetadataHandleRoots = [prior], RetainedChildren = 0, RetainedElapsedSeconds = 0,
            RetainedOfflineSeconds = 0, MaximumChildren = 61, ValidationChildren = 18 };
        Check("fresh30-with-explicit-copy-reserve", fresh, true);
        Check("fresh31-with-charged-historical-reserve", fresh with { RetainedBudgetBytes = 31_000_000_000, AdditionalHistoricalReserveBytes = 700_000_000 }, true);
        Check("fresh31-cannot-drop-copy-reserve", fresh with { RetainedBudgetBytes = 31_000_000_000, CopyFallbackReserveBytes = 0 }, false);
        Check("negative-historical-reserve-rejected", fresh with { RetainedBudgetBytes = 31_000_000_000, AdditionalHistoricalReserveBytes = -1 }, false);
        Check("historical-reserve-cannot-hide-in-old30-route", fresh with { AdditionalHistoricalReserveBytes = 1 }, false);
        Check("continuation-cannot-inherit-fresh31-cap", continuation with { RetainedBudgetBytes = 31_000_000_000 }, false);
        Check("new-history-cap-cannot-rise-to32", fresh with { RetainedBudgetBytes = 32_000_000_000 }, false);
        Check("whole-envelope-early-ceiling-rejected", fresh with { RetainedBudgetBytes = 31_000_000_000, AdditionalHistoricalReserveBytes = 856_891_136 }, false);
        Check("whole-envelope-overflow-rejected", fresh with { RetainedBudgetBytes = 31_000_000_000, AdditionalHistoricalReserveBytes = long.MaxValue }, false);
        Check("null-resource-proposal-rejected", null, false);
        Check("obsolete-54-role-ceiling-rejected", continuation with { MaximumChildren = 54 }, false);
        Check("nan-retained-time-rejected", continuation with { RetainedElapsedSeconds = double.NaN }, false);
        Check("negative-retained-time-rejected", continuation with { RetainedElapsedSeconds = -1 }, false);
        Check("exhausted-shared-5400-seconds-rejected", continuation with { RetainedElapsedSeconds = 5400 }, false);
        Check("wrong-spent-role-count-rejected", continuation with { RetainedChildren = 20 }, false);
        Check("fresh-original-61-role-route-preserved", original with { RetainedRoots = [prior], MetadataHandleRoots = [prior] }, true);
        void Evidence(string name, bool passed)
        { if (!passed) failed++; rows.Add(new { name, passed }); }
        Evidence("actual-bound-controller-ceiling-matches", CompositeElapsedMatches(1147, 1146.5852218));
        Evidence("old1106-undercharge-does-not-match", !CompositeElapsedMatches(1106, 1146.5852218));
        Evidence("fractional-undercharge-does-not-match", !CompositeElapsedMatches(1146.5852218, 1146.5852218));
        Evidence("nan-actual-controller-does-not-match", !CompositeElapsedMatches(1147, double.NaN));
        Evidence("negative-actual-controller-does-not-match", !CompositeElapsedMatches(1147, -1));
        Evidence("exhausted-actual-controller-does-not-match", !CompositeElapsedMatches(5400, 5400));
        Evidence("historical-parent-handle-default-preserved", CompositeParentHandleBudget((int?)null) == 32768);
        Evidence("explicit-historical-parent-handle-budget-preserved", CompositeParentHandleBudget(32768) == 32768);
        Evidence("fresh-parent-handle-budget-bounded", CompositeParentHandleBudget(49152) == 49152);
        foreach (int limit in new[] { -1, 0, 32769, 49153, int.MaxValue })
        {
            bool rejected = false; try { CompositeParentHandleBudget(limit); } catch (IOException) { rejected = true; }
            Evidence("unknown-parent-handle-budget-rejected-" + limit, rejected);
        }
        string resource = Path.Combine(root, "resources.json");
        void Resource(string name, string json, bool expected, int value = 0)
        {
            File.WriteAllText(resource, json);
            var proposal = original with { Files = new() { [resource] = HashFile(resource) } };
            bool accepted = false;
            try { accepted = CompositeParentHandleBudget(proposal) == value; }
            catch (Exception error) when (error is IOException or FormatException or InvalidOperationException) { }
            Evidence(name, accepted == expected);
        }
        Resource("bound-resource-default-remains-historical", "{}", true, 32768);
        Resource("bound-fresh-resource-limit", "{\"parentHandles\":49152}", true, 49152);
        Resource("bound-unknown-resource-limit-rejected", "{\"parentHandles\":32769}", false);
        Resource("fractional-resource-limit-rejected", "{\"parentHandles\":32768.1}", false);
        Resource("string-resource-limit-rejected", "{\"parentHandles\":\"49152\"}", false);
        var staleResource = original with { Files = new() { [resource] = HashFile(resource) } };
        File.WriteAllText(resource, "{\"parentHandles\":49152}"); bool staleRejected = false;
        try { CompositeParentHandleBudget(staleResource); } catch (IOException) { staleRejected = true; }
        Evidence("changed-bound-resource-rejected", staleRejected);
        string session = Path.Combine(prior, "session"), current = Path.Combine(prior, "recorder.log"), rotated = current + ".639294002869558050.old";
        string Line(long qpc) => $"Replay save trigger: QPC={qpc}; session={session}; cutoff=1.25; timeline=media-clock; epoch=1; frequency=100\n";
        File.WriteAllText(current, Line(300)); File.WriteAllText(rotated, Line(100) + Line(200));
        var bound = new Dictionary<string, string> { [current] = HashFile(current), [rotated] = HashFile(rotated) };
        var clocks = ReadCompositeClockCalls(prior, session, bound);
        Evidence("rotated-and-current-clocks-retained-in-qpc-order", clocks.Select(call => call.qpc).SequenceEqual(new long[] { 100, 200, 300 }));
        bool omittedRejected = false;
        try { ReadCompositeClockCalls(prior, session, new Dictionary<string, string> { [current] = bound[current] }); }
        catch (IOException) { omittedRejected = true; }
        Evidence("omitted-original-rotation-rejected", omittedRejected);
        File.AppendAllText(rotated, Line(250)); bool changedRejected = false;
        try { ReadCompositeClockCalls(prior, session, bound); } catch (IOException) { changedRejected = true; }
        Evidence("changed-bound-rotation-rejected", changedRejected);
        File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new { failed, rows,
            claim = "NONMEDIA resource-shape controls only. Actual bound prior-receipt equality, clock evidence, source content and continuation admission require separate verification." },
            new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Composite budget controls: {rows.Count - failed}/{rows.Count} passed");
        return failed == 0 ? 0 : 1;
    }
}
