using System.Text.Json;
using System.Text.Json.Nodes;

namespace F10Recorder;

internal static partial class ReplayCalibrationRun
{
    // These deliberately synthetic JSON receipts exercise the real consumer.
    // They do not measure native allocation or qualify any recording/model.
    internal static int OutsideBaselineMetadataChecks(string root)
    {
        Require(!Directory.Exists(root) && !File.Exists(root), "Fresh metadata-control root required.");
        Directory.CreateDirectory(root);
        var checks = new List<object>();
        void Case(string name, bool reject, Action<JsonObject> changeProof = null,
            Action<JsonObject> changeForecast = null, Action<JsonArray> changeExclusions = null, bool badBinding = false, bool legacy = false)
        {
            string cell = Path.Combine(root, name); Directory.CreateDirectory(cell);
            string retained = Path.Combine(cell, "retained-not-read");
            var excluded = new JsonArray(retained); changeExclusions?.Invoke(excluded);
            string input = Path.Combine(cell, "outside-inventory-plan.json");
            File.WriteAllText(input, new JsonObject { ["excludedRoots"] = excluded }.ToJsonString());
            long observed = legacy ? 8_703_487_400 : 9_691_414_560, future = legacy ? 1_000_000_000 : 308_585_440;
            string native = Path.Combine(cell, "outside-inventory.json"), baseline = Path.Combine(cell, "outside-storage-baseline.json"),
                proof = Path.Combine(cell, "outside-forward-envelope.json"), forecastPath = Path.Combine(cell, "outside-outstanding-writes.json");
            File.WriteAllText(native, JsonSerializer.Serialize(new { result = "complete-native-outside-inventory", error = (string)null,
                planSha256 = HashFile(input), nativeAllocatedBytes = observed }));
            File.WriteAllText(baseline, JsonSerializer.Serialize(new { baselineCapBytes = legacy ? 9_000_000_000 : observed,
                futureGrowthAllowanceBytes = future, observedNativeBytes = observed }));
            var forecast = new JsonObject { ["kind"] = "outside-outstanding-writes-v1",
                ["charges"] = new JsonArray(new JsonObject { ["name"] = "archives", ["bytes"] = 180_000_000 },
                    new JsonObject { ["name"] = "other-bounded-writes", ["bytes"] = 82_000_000 }) };
            changeForecast?.Invoke(forecast); File.WriteAllText(forecastPath, forecast.ToJsonString());
            var e = new JsonObject { ["revision"] = "outside-partition-v2", ["result"] = "complete-forward-native-changed-set",
                ["withinAllowance"] = true, ["baselineResultSha256"] = HashFile(native), ["baselinePlanSha256"] = HashFile(input),
                ["forecastSha256"] = HashFile(forecastPath), ["allowanceBytes"] = future, ["newOrTouchedNativeBytes"] = 4096,
                ["archiveReserveBytes"] = 262_000_000, ["reportAndLogReserveBytes"] = 8_388_608, ["chargedFutureBytes"] = 270_392_704,
                ["namespaceRetainedRoots"] = new JsonArray(retained), ["rows"] = new JsonArray(
                    new JsonObject { ["path"] = Path.Combine(cell, "first.bin"), ["identity"] = "controlled-native-id", ["nativeAllocatedBytes"] = 4096 },
                    new JsonObject { ["path"] = Path.Combine(cell, "alias.bin"), ["identity"] = "controlled-native-id", ["nativeAllocatedBytes"] = 4096 }) };
            changeProof?.Invoke(e); File.WriteAllText(proof, e.ToJsonString());
            var files = new[] { native, baseline, input }.Concat(legacy ? [] : new[] { proof, forecastPath })
                .ToDictionary(p => p, p => HashFile(p), StringComparer.OrdinalIgnoreCase);
            if (badBinding) files[proof] = new string('0', 64);
            var plan = new CalibrationPlan(cell, "unused", "synthetic-metadata-control-not-native-proof", files, [], [retained], 0, 0,
                OutsideBuildAllowanceBytes: 10_000_000_000, CompositeQualification: true);
            bool rejected = false;
            try { ValidateCompositeOutsideBaseline(plan); }
            catch (Exception error) when (error is IOException or InvalidOperationException or KeyNotFoundException or JsonException or OverflowException)
            { rejected = true; }
            Require(rejected == reject, "Outside metadata control failed: " + name);
            checks.Add(new { name, passed = true, rejected });
        }
        Case("v2-valid-native-alias-deduplicated", false);
        Case("legacy-nine-plus-one", false, legacy: true);
        Case("failed-proof", true, e => e["result"] = "failed-incomplete");
        Case("over-budget-proof-flag", true, e => e["withinAllowance"] = false);
        Case("proof-hash-mismatch", true, badBinding: true);
        Case("baseline-origin-mismatch", true, e => e["baselinePlanSha256"] = new string('0', 64));
        Case("forecast-hash-mismatch", true, e => e["forecastSha256"] = new string('0', 64));
        Case("forecast-sum-mismatch", true, changeForecast: f => f["charges"][0]["bytes"] = 180_000_001);
        Case("forecast-negative", true, changeForecast: f => f["charges"][0]["bytes"] = -1);
        Case("forecast-duplicate-name", true, changeForecast: f => f["charges"][1]["name"] = "archives");
        Case("extra-excluded-root", true, changeExclusions: e => e.Add(Path.Combine(root, "uncharged-extra")));
        Case("missing-excluded-root", true, changeExclusions: e => e.Clear());
        Case("proof-retained-namespace-mismatch", true, e => e["namespaceRetainedRoots"] = new JsonArray());
        Case("alias-allocation-inconsistent", true, e => e["rows"][1]["nativeAllocatedBytes"] = 8192);
        Case("negative-native-allocation", true, e => e["rows"][0]["nativeAllocatedBytes"] = -1);
        Case("duplicate-native-path", true, e => e["rows"][1]["path"] = e["rows"][0]["path"].GetValue<string>());
        Case("accrued-sum-mismatch", true, e => e["newOrTouchedNativeBytes"] = 4097);
        Case("charged-sum-mismatch", true, e => e["chargedFutureBytes"] = 1);
        Case("report-reserve-too-small", true, e => e["reportAndLogReserveBytes"] = 0);
        Write(Path.Combine(root, "verification.json"), new { checks, claim = "Synthetic metadata consumer controls only; no native allocation, media, capture, model, catalog or user-state action." });
        Console.WriteLine(checks.Count + " metadata outside-partition controls passed; no native/media/state action."); return 0;
    }
}
