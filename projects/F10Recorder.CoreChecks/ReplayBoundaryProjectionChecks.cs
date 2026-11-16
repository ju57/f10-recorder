using F10Recorder;

internal static class ReplayBoundaryProjectionChecks
{
    static void Need(bool ok, string message) { if (!ok) throw new Exception(message); }
    static void Reject<T>(Action action, string message) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception(message); }

    internal static void Run()
    {
        int cases = 0;
        foreach (int fps in new[] { 24, 60, 120 })
            foreach (int seconds in new[] { 1, 300, 1800 })
            {
                var inputs = Enumerable.Range(0, seconds + 3).Select(n => ReplayHybridPlanChecks.Input((long)n * fps, fps, fps)).ToArray();
                foreach (double start in new[] { 1d, 1.25, (fps + 2d) / fps, Math.BitDecrement((fps + 2d) / fps), Math.BitIncrement((fps + 2d) / fps) })
                    Check(inputs, new(start, start + seconds, seconds), fps, true, ref cases);
            }

        // Exercise zero/one/two partial starts using nonuniform actual GOPs.
        var uneven = new[] { ReplayHybridPlanChecks.Input(0, 90, 60), ReplayHybridPlanChecks.Input(90, 90, 60),
            ReplayHybridPlanChecks.Input(180, 30, 60), ReplayHybridPlanChecks.Input(210, 90, 60) };
        foreach (var interval in new[] { new ReplayInterval(0, 5, 5), new(.25, 4, 3.75), new(1.25, 3.75, 2.5) })
            Check(uneven, interval, 60, false, ref cases);
        Check(uneven, new(0, 2, 300), 60, false, ref cases); // Honest warm-up.

        long nearWrap = 95442 * 60L;
        var wrapped = new[] { ReplayHybridPlanChecks.Input(nearWrap - 60, 180, 60), ReplayHybridPlanChecks.Input(nearWrap + 120, 30, 60),
            ReplayHybridPlanChecks.Input(nearWrap + 150, 180, 60), ReplayHybridPlanChecks.Input(nearWrap + 330, 30, 60) };
        Check(wrapped, new(95442.25, 95447.25, 5), 60, true, ref cases);

        // The first protected source may be AAC preroll with no video output.
        var regular = Enumerable.Range(0, 8).Select(n => ReplayHybridPlanChecks.Input(n * 60L, 60, 60)).ToArray();
        var request = new ReplayInterval(2.01, 5.01, 3);
        Need(ReplayBoundaryProjection.Create(0, regular[1], regular[0].Index, request, 60) == null,
            "Audio-preroll-only source incorrectly became a boundary.");
        var local = ReplayBoundaryProjection.Create(2, regular[2], regular[0].Index, request, 60);
        var complete = ReplayHybridPlan.Create(regular, request, 60, true);
        Need(local is { First: 1, Count: 59 } && local.Matches(complete, regular[2], request, 60), "Exact physical head projection failed.");
        Need(!local.Matches(ReplayHybridPlan.Create(regular, new(2.01, 6.01, 4), 60, true), regular[2], new(2.01, 6.01, 4), 60),
            "An unchanged head authorized a different final requested interval.");
        foreach (var changed in new[] { request with { Start = Math.BitIncrement(request.Start) }, request with { End = Math.BitIncrement(request.End) },
            request with { End = 5.0101 }, request with { RequestedSeconds = 300 } })
            Need(!local.Matches(ReplayHybridPlan.Create(regular, changed, 60, true), regular[2], changed, 60), "Same-frame changed frozen request matched.");
        Need(!local.Matches(complete, regular[2], request, 120), "Changed requested FPS matched.");
        Need(!local.Matches(complete with { ClockShift = complete.ClockShift + 1 }, regular[2], request, 60), "Changed common-clock translation matched.");
        Need(!local.Matches(complete, regular[2] with { Index = regular[2].Index with { Sha256 = "different-source" } }, request, 60), "Changed coded source hash matched.");
        Need(!local.Matches(complete, regular[2] with { Index = regular[2].Index with { Bytes = regular[2].Index.Bytes + 188 } }, request, 60), "Changed indexed source byte length matched.");
        Need(!local.Matches(complete, regular[2] with { End = regular[2].End + .001 }, request, 60), "Changed source interval matched.");
        Need(!local.Matches(complete, regular[2] with { Index = regular[2].Index with { Sps = new byte[] { 9 } } }, request, 60), "Changed SPS matched.");
        Need(!local.Matches(complete, regular[2] with { Index = regular[2].Index with { Pps = new byte[] { 9 } } }, request, 60), "Changed PPS matched.");
        Need(!local.Matches(complete with { Parts = [.. complete.Parts, complete.Parts.Single(p => p.Input == 2)] }, regular[2], request, 60),
            "Duplicate final source membership matched.");

        var bad = regular[2] with { End = regular[2].End + .0001 };
        Reject<IOException>(() => ReplayBoundaryProjection.Create(2, bad, regular[0].Index, request, 60), "Corrupt source clock projected.");
        var pictures = regular[2].Index.Pictures.ToArray(); pictures[30] = pictures[30] with { Pts = pictures[30].Pts + 1 };
        bad = regular[2] with { Index = regular[2].Index with { Pictures = pictures } };
        Reject<IOException>(() => ReplayBoundaryProjection.Create(2, bad, regular[0].Index, request, 60), "Cumulative picture drift projected.");
        bad = regular[2] with { Index = regular[2].Index with { Pps = new byte[] { 9 } } };
        Reject<NotSupportedException>(() => ReplayBoundaryProjection.Create(2, bad, regular[0].Index, request, 60), "Unsupported configuration projected.");
        Reject<IOException>(() => ReplayBoundaryProjection.Create(-1, regular[2], regular[0].Index, request, 60), "Negative ordinal projected.");

        // Local video proof never certifies later source continuity or AAC.
        var laterBad = regular.ToArray(); laterBad[4] = laterBad[4] with { End = laterBad[4].End + .0001 };
        Need(local.Matches(complete, regular[2], request, 60), "Local source control unexpectedly changed.");
        Reject<IOException>(() => ReplayHybridPlan.Create(laterBad, request, 60, true), "Later corrupt source escaped full planning.");
        laterBad = regular.ToArray(); var audio = laterBad[4].Index.Audio.ToArray(); audio[0] = audio[0] with { Pts = audio[0].Pts + 90 };
        laterBad[4] = laterBad[4] with { Index = laterBad[4].Index with { Audio = audio } };
        Reject<IOException>(() => ReplayHybridPlan.Create(laterBad, request, 60, true), "Later AAC gap escaped full planning.");
        Console.WriteLine($"Passed: {cases} independently enumerated local/full physical boundary cases and changed-request/source/configuration/later-corruption controls. No files, native calls, processes or media decoding.");
    }

    static void Check(ReplayTransportInput[] inputs, ReplayInterval interval, int fps, bool audio, ref int cases)
    {
        var plan = ReplayHybridPlan.Create(inputs, interval, fps, audio);
        for (int n = 0; n < inputs.Length; n++)
        {
            var source = inputs[n]; long sourceFirst = (long)Math.Round(source.Start * fps);
            // Enumerate the fixture's actual physical picture times. This oracle
            // does not call FrameAtOrAfter or reproduce its rounding algorithm.
            var expected = Enumerable.Range(0, source.Index.Pictures.Count)
                .Where(p => (sourceFirst + p) / (double)fps >= interval.Start && (sourceFirst + p) / (double)fps < interval.End).ToArray();
            var projection = ReplayBoundaryProjection.Create(n, source, inputs[0].Index, interval, fps);
            if (expected.Length == 0 || expected[0] == 0) Need(projection == null, "Unnecessary boundary projection.");
            else Need(projection != null && projection.First == expected[0] && projection.Count == expected.Length
                && projection.FirstFrame == sourceFirst + expected[0] && projection.Matches(plan, source, interval, fps), "Local projection differs from actual physical times/full plan.");
        }
        cases++;
    }
}
