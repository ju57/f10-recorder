using System.Text.Json;

namespace F10Recorder;

// Literal independent examples: no media, native storage query or producer starts.
internal static class ReplayCalibrationModelChecks
{
    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    static ReplayReferenceModel Model(long residual = 0, long extra = 0) =>
        new("independent-reference-r1", "reference-scope", "unqueried-volume", 4096,
            residual, extra, ["training-one", "training-two", "training-three"]);

    static void Overflow(Action action)
    {
        try { action(); }
        catch (OverflowException) { return; }
        throw new InvalidOperationException("An unrepresentable reservation must reject overflow.");
    }

    internal static int Run(string root)
    {
        root = Path.GetFullPath(root);
        Directory.CreateDirectory(root);
        var results = new List<object>();
        int failed = 0;
        void Check(string name, Action action)
        {
            try { action(); results.Add(new { name, result = "passed" }); }
            catch (Exception ex) { failed++; results.Add(new { name, result = "failed", error = ex.ToString() }); }
        }

        Check("guard-transition-and-positive-residual", () =>
        {
            Require(Model().FinalBytes(335_544_320) == 352_321_536, "Guard transition must include exactly 16 MiB.");
            Require(Model().FinalBytes(335_544_321) == 352_321_538, "The first byte above the transition must round the guard upward.");
            Require(Model(1234).FinalBytes(1000) == 16_779_450, "Training residual must remain separate from the prospective guard.");
        });
        Check("rounded-output-and-finalization-peak", () =>
        {
            Require(Model(1234, 8192).PeakBytes(1000) == 201_342_976,
                "Peak must include rounded final prediction, sampled excess, finalization guard and all three scratch claims.");
        });
        Check("independent-final-and-peak-coverage", () =>
        {
            var model = Model(1234, 8192);
            Require(model.CoversObservations(1000, 16_779_450, 16_781_312, 201_342_976), "Exact final and peak boundaries must pass.");
            Require(!model.CoversObservations(1000, 16_779_451, 16_781_312, 201_342_976), "Final EOF exceedance must fail.");
            // 16,785,408 is one allocation unit above the final prediction but below the peak allowance.
            Require(!model.CoversObservations(1000, 16_779_450, 16_785_408, 201_342_976),
                "Final allocation exceedance must not consume finalization headroom.");
            Require(!model.CoversObservations(1000, 16_779_450, 16_781_312, 201_347_072), "Sampled peak exceedance must fail.");
        });
        Check("arithmetic-overflow-rejected", () =>
        {
            Overflow(() => Model().FinalBytes(long.MaxValue));
            Overflow(() => Model(long.MaxValue).FinalBytes(1000));
            Overflow(() => Model(0, long.MaxValue).PeakBytes(1000));
            Overflow(() => ReplayReferenceModel.Round(long.MaxValue, 4096));
        });
        Check("duration-cells-use-distinct-hard-eligibility-and-open-tail-cover", () =>
        {
            Require(ReplayReferenceModel.InputLimit(1) == 200_000_000L && ReplayReferenceModel.InputLimit(5) == 1_000_000_000L
                && ReplayReferenceModel.InputLimit(30) == 5_000_000_000L, "Cell limits must retain 64-bit thirty-minute input sizes.");
            Require(ReplayReferenceModel.InputCountLimit(1) == 65 && ReplayReferenceModel.InputCountLimit(30) == 1805,
                "Each cell must cover its completed input count and preceding head.");
            Require(ReplayReferenceModel.SnapshotEnvelope(4096, 30) == 5_008_445_440L,
                "Thirty-minute open snapshot must charge rounded 5GB payload, 1805 allocation units and 1MiB concat.");
            Require(ReplayReferenceModel.ConcatLimit(30) == 1048576 && !ReplayReferenceModel.SupportedMinutes(2),
                "Unsupported profiles must not inherit a nearby cell.");
            foreach (int minutes in new[] { 1, 5, 30 })
            {
                var model = Model() with { ReplayMinutes = minutes };
                Require(model.PeakBytes(model.InputByteLimit) > model.InputByteLimit * 2,
                    "Open-tail reservations must include output plus coexisting full assembly scratch.");
            }
        });

        // Null paths deliberately make a native/path query fail if an eligibility guard regresses.
        var selection = new ReplayOutputSelection("independent-selection", ReplayReferenceModel.ExporterIdentity,
            null, 300, true, Array.Empty<ReplayOutputInput>());
        foreach (var item in new[]
        {
            (Name: "wrong-scope", Selection: selection, Scope: "different-scope"),
            (Name: "wrong-exporter", Selection: selection with { ExporterIdentity = "different-exporter" }, Scope: "reference-scope"),
            (Name: "open-selection", Selection: selection with { Closed = false }, Scope: "reference-scope"),
            (Name: "insufficient-history", Selection: selection with { End = 2 }, Scope: "reference-scope")
        })
            Check(item.Name + "-unmeasured-before-storage", () =>
                Require(Model().Estimate(item.Selection, item.Scope, null).Kind == ReplayOutputEvidenceKind.Unmeasured,
                    "Out-of-envelope selection must remain unmeasured without observing the destination."));

        string report = Path.Combine(root, "calibration-model-checks.json");
        using (var output = new FileStream(report, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            JsonSerializer.Serialize(output, new { result = failed == 0 ? "passed" : "failed", failed, cases = results },
                new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine("Calibration model checks: " + (failed == 0 ? "passed" : "failed") + " " + report);
        return failed == 0 ? 0 : 1;
    }
}
