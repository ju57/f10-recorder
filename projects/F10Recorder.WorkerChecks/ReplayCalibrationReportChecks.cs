using System.Text.Json;

namespace F10Recorder;

internal static partial class ReplayCalibrationRun
{
    internal static int ReportChecks(string root, string reference)
    {
        Require(!Directory.Exists(root) && new FileInfo(reference).Length <= 65536, "Fresh report fixture and small reference required.");
        using var admission = JsonDocument.Parse(File.ReadAllText(reference));
        var plan = admission.RootElement.GetProperty("plan").Deserialize<CalibrationPlan>();
        var samples = new List<JsonElement>();
        foreach (string label in Order)
        {
            string source = label is "T01" or "T02" ? plan.CompositeTrainingRoot ?? plan.Root : plan.Root;
            string file = Path.Combine(source, label, "sample.json");
            Require(new FileInfo(file).Length <= 131072, "Per-case sample read bound.");
            using var sample = JsonDocument.Parse(File.ReadAllText(file));
            samples.Add(sample.RootElement.Clone());
        }
        var report = new { result = "report-serialization-fixture-only", samples };
        int prettyBytes = JsonSerializer.SerializeToUtf8Bytes(report, Json).Length;
        Directory.CreateDirectory(root);
        var checks = new List<object>();
        void Check(string name, Action run)
        {
            try { run(); checks.Add(new { name, passed = true, error = (string)null }); }
            catch (Exception e) { checks.Add(new { name, passed = false, error = e.Message }); }
        }
        Check("complete-six-case-result-fits-unchanged-cap", () => {
            Require(prettyBytes > 131072, "Actual six-case corpus must reproduce the oversized indented report.");
            string file = Path.Combine(root, "result.json"); Write(file, report);
            Require(new FileInfo(file).Length <= 131072, "Terminal cap unchanged.");
            using var saved = JsonDocument.Parse(File.ReadAllText(file));
            Require(JsonElement.DeepEquals(saved.RootElement, JsonSerializer.SerializeToElement(report, Json)), "Compact report lost semantic fields.");
        });
        Check("complete-six-case-failure-fits-unchanged-cap", () => Write(Path.Combine(root, "failure.json"), report));
        Check("genuinely-oversized-terminal-report-rejected", () => {
            string file = Path.Combine(root, "oversized", "result.json"); Directory.CreateDirectory(Path.GetDirectoryName(file));
            bool rejected = false; try { Write(file, new { payload = new string('x', 131073) }); } catch (IOException) { rejected = true; }
            Require(rejected && !File.Exists(file), "Oversized terminal report must fail before file creation.");
        });
        Check("nonterminal-json-retains-existing-format", () => {
            string file = Path.Combine(root, "detail.json"); Write(file, new { test = 1 });
            Require(File.ReadAllText(file).Contains('\n'), "Nonterminal evidence formatting changed.");
        });
        Check("oversized-failure-preserves-original-cause", () => {
            string failureRoot = Path.Combine(root, "failure-fallback"); Directory.CreateDirectory(failureRoot);
            WriteFailure(failureRoot, new IOException("original-failure-sentinel"), new { payload = new string('x', 131073) });
            using var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(failureRoot, "failure-summary.json")));
            Require(saved.RootElement.GetProperty("error").GetString().Contains("original-failure-sentinel")
                && saved.RootElement.GetProperty("fullReportUnavailable").GetBoolean(), "Original failure/incomplete-report status was lost.");
        });
        int failed = checks.Count(c => !JsonSerializer.SerializeToElement(c).GetProperty("passed").GetBoolean());
        Write(Path.Combine(root, "checks.json"), new { prettyBytes, failed, checks, claim = "File-only report controls; no capture, decoder, model fitting or qualification." });
        Console.WriteLine($"Report controls: {checks.Count - failed}/{checks.Count}; indented corpus {prettyBytes} bytes.");
        return failed == 0 ? 0 : 1;
    }
}
