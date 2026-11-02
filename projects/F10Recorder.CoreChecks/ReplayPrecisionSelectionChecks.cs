using F10Recorder;

internal static class ReplayPrecisionSelectionChecks
{
    static void Require(bool value, string reason) { if (!value) throw new IOException(reason); }
    internal static void Run()
    {
        var source = Enumerable.Range(0, 1810).Select(i => new Segment { Path = i + ".ts", Start = i, End = i + 1 }).ToList();
        foreach (int seconds in new[] { 60, 300, 1800 })
        foreach (int fps in new[] { 24, 30, 60, 120 })
        foreach (double phase in new[] { 0, 1.0 / fps, 0.5 / fps, (fps - 1.0) / fps })
        {
            double end = seconds + 3 + phase;
            var interval = ReplayInterval.AtRequest(source, end, seconds);
            var selected = ReplaySelection.Select(source, end, seconds);
            interval.RequireCoverage(selected);
            Require(Math.Abs(interval.Start - (3 + phase)) < 1e-8 && interval.End == end && !interval.LimitedHistory,
                "Full replay must freeze the entire requested interval.");
            Require(selected.First().Start <= interval.Start && selected.First().End > interval.Start && selected.Last().Start < end,
                "A fractional request must retain both boundary GOPs without future segments.");
        }
        var warm = ReplayInterval.AtRequest(source, 1.25, 300);
        Require(warm.Start == 0 && warm.Seconds == 1.25 && warm.LimitedHistory, "Warm-up must disclose actual history without padding.");
        var shortened = ReplayInterval.AtRequest(source.Skip(100).ToList(), 120, 300);
        Require(shortened.Start == 100 && shortened.Seconds == 20 && shortened.LimitedHistory, "Unavailable history must remain disclosed.");
        foreach (double end in new[] { 0.999, 0.9999995 })
        {
            bool subsecond = false;
            try { ReplayInterval.AtRequest(source, end, 60); } catch (IOException) { subsecond = true; }
            Require(subsecond, "Subsecond requests must reject.");
        }
        foreach (var bad in new[] {
            new List<Segment> { new() { Start = 0, End = 1 }, new() { Start = 2, End = 3 } },
            new List<Segment> { new() { Start = 0, End = 1.1 }, new() { Start = 1, End = 3 } },
            new List<Segment> { new() { Start = 1, End = 3 } },
            new List<Segment> { new() { Start = 0, End = 2 } }
        })
        {
            bool rejected = false;
            try { new ReplayInterval(0, 3, 3).RequireCoverage(bad); } catch (IOException) { rejected = true; }
            Require(rejected, "Gaps, overlaps or missing frozen boundaries must not become apparent continuous history.");
        }
        Console.WriteLine("Passed: exact full/warm-up intervals at1/5/30minutes and24/30/60/120FPS, minimum duration and unavailable/overlapping history.");
    }
}
