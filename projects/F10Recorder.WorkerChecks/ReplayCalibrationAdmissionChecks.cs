using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;

namespace F10Recorder;

// Call the real command dispatch and Run entry for every check. Positive paths
// exit at the admission-only boundary; negative Capture mode paths must reject
// before any current-device, source, audio or engine initialization.
internal static class ReplayCalibrationAdmissionChecks
{
    static void Require(bool value, string message) { if (!value) throw new IOException(message); }
    static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    internal static int Run(string root, string templatePath)
    {
        Require(root.StartsWith(Path.GetFullPath("artifacts") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !Directory.Exists(root) && !File.Exists(root) && new FileInfo(templatePath).Length <= 65536, "Fresh bounded gate fixture required.");
        Directory.CreateDirectory(root);
        var template = JsonNode.Parse(File.ReadAllBytes(templatePath)); var rows = new List<object>(); int failed = 0;
        string NewCase(string name, Action<JsonNode> mutate = null, Func<string, string> rewrite = null)
        {
            string directory = Path.Combine(root, name); Directory.CreateDirectory(directory);
            var input = template.DeepClone(); input["plan"]["Root"] = Path.Combine(directory, "source-never-created");
            input["stopFile"] = Path.Combine(directory, "STOP.REQUEST"); input["watchStatusFile"] = Path.Combine(directory, "NEXT-HANDOFF.json");
            // Bind the actual per-case resource document as well as the plan.
            string original = input["plan"]["EvidenceRoles"]["resources"].GetValue<string>();
            var resources = JsonNode.Parse(File.ReadAllBytes(original)); resources["experimentRoot"] = input["plan"]["Root"].GetValue<string>();
            string resourcePath = Path.Combine(directory, "resources.json"); File.WriteAllText(resourcePath, resources.ToJsonString(Json));
            input["plan"]["Files"].AsObject().Remove(original); input["plan"]["Files"][resourcePath] = Hash(resourcePath);
            input["plan"]["EvidenceRoles"]["resources"] = resourcePath;
            mutate?.Invoke(input);
            string text = input.ToJsonString(Json); if (rewrite != null) text = rewrite(text);
            string path = Path.Combine(directory, "plan.json"); File.WriteAllText(path, text); return path;
        }
        void Check(string name, Action check)
        {
            try { check(); rows.Add(new { name, passed = true }); }
            catch (Exception error) { failed++; rows.Add(new { name, passed = false, error = error.ToString() }); }
        }
        int Call(string flag, string path, string expected = null) => WorkerChecks.Main([flag, path, expected ?? Hash(path)]);
        void NoSource(string path)
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path)); string source = document.RootElement.GetProperty("plan").GetProperty("Root").GetString();
            Require(!Directory.Exists(source) && !File.Exists(source), "A validation path entered/created its source root.");
        }
        void Rejected(string path, string flag = "--replay-calibration-preflight", string expected = null, string cause = null)
        {
            Require(Call(flag, path, expected) == 1, "Invalid admission entry did not fail."); NoSource(path);
            string[] reports = Directory.GetFiles(Path.GetDirectoryName(path), "STARTUP-FAILURE-*.json");
            Require(reports.Length > 0, "Startup rejection did not write its structured cause.");
            using var json = JsonDocument.Parse(File.ReadAllBytes(reports[^1])); var row = json.RootElement;
            Require(row.GetProperty("phase").GetString() == "final-plan-admission" && !row.GetProperty("captureStarted").GetBoolean()
                && !row.GetProperty("audioStarted").GetBoolean() && !row.GetProperty("engineStarted").GetBoolean(), "Startup phase/effects reporting is false.");
            if (cause != null) Require(reports.Any(file =>
            {
                using var report = JsonDocument.Parse(File.ReadAllBytes(file));
                return report.RootElement.GetProperty("cause").GetString().Contains(cause, StringComparison.Ordinal);
            }), "Admission was rejected for the wrong cause: " + cause);
        }
        Check("former-current-plus-historical-basename-package-passes-real-preflight-and-launch-check", () =>
        {
            string path = NewCase("positive"); var input = JsonNode.Parse(File.ReadAllBytes(path));
            Require(input["plan"]["Files"].AsObject().Count(p => Path.GetFileName(p.Key) == "outside-inventory.json") == 2
                && input["plan"]["Files"].AsObject().Count(p => Path.GetFileName(p.Key) == "outside-inventory-plan.json") == 2,
                "The exact old failure pattern is absent from the fixture.");
            Require(Call("--replay-calibration-preflight", path) == 0 && Call("--replay-calibration-check-launch", path) == 0,
                "The real owning routes disagree on final valid admission."); NoSource(path);
            using var result = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(path), "PREFLIGHT.json")));
            Require(result.RootElement.GetProperty("planSha256").GetString() == Hash(path)
                && !result.RootElement.GetProperty("sourceEntered").GetBoolean(), "Receipt is not bound to final plan/source boundary.");
        });
        Check("valid-actual-capture-entry-rejects-missing-validation-receipt", () => Rejected(NewCase("no-receipt"), "--replay-calibration-run", cause: "Successful final-plan preflight receipt required"));
        Check("validated-actual-capture-entry-still-requires-fresh-recording-authorization", () =>
        {
            string path = NewCase("no-authorization"); Require(Call("--replay-calibration-preflight", path) == 0, "Initial final admission did not pass.");
            Require(Call("--replay-calibration-run", path) == 1, "Missing recording authorization was accepted."); NoSource(path);
            Require(Directory.GetFiles(Path.GetDirectoryName(path), "STARTUP-FAILURE-*.json").Any(file =>
            { using var report = JsonDocument.Parse(File.ReadAllBytes(file)); return report.RootElement.GetProperty("phase").GetString() == "recording-authorization"; }), "Recording rejection phase was lost.");
        });
        Check("missing-explicit-role-rejected-in-preflight-and-actual-capture-entry", () =>
        {
            string path = NewCase("missing-role", n => n["plan"]["EvidenceRoles"].AsObject().Remove("outside-inventory"));
            Rejected(path, cause: "Missing explicit evidence role: outside-inventory"); Rejected(path, "--replay-calibration-run", cause: "Missing explicit evidence role: outside-inventory");
        });
        Check("duplicated-role-json-key-rejected", () =>
        {
            string path = NewCase("duplicate-role", rewrite: text =>
            {
                string marker = "\"EvidenceRoles\": {"; int at = text.IndexOf(marker, StringComparison.Ordinal);
                Require(at >= 0, "Role JSON injection marker absent.");
                string value = template["plan"]["EvidenceRoles"]["outside-inventory"].ToJsonString();
                return text.Insert(at + marker.Length, "\"outside-inventory\":" + value + ",");
            }); Rejected(path, cause: "Duplicate JSON member");
        });
        Check("canonical-role-path-alias-rejected", () =>
        {
            string path = NewCase("alias-role", n =>
            {
                string target = n["plan"]["EvidenceRoles"]["outside-inventory"].GetValue<string>();
                string alias = Path.Combine(Path.GetDirectoryName(target), ".", Path.GetFileName(target));
                n["plan"]["Files"][alias] = Hash(target); n["plan"]["EvidenceRoles"]["outside-inventory-plan"] = alias;
            }); Rejected(path, cause: "Duplicate, aliased or unbound explicit evidence roles");
        });
        Check("duplicate-files-and-top-level-members-rejected", () =>
        {
            string path = NewCase("duplicate-top", rewrite: text => text.Insert(text.IndexOf('{') + 1, "\"createdUtc\":\"2000-01-01T00:00:00Z\",")); Rejected(path, cause: "Duplicate JSON member");
            path = NewCase("duplicate-files", rewrite: text =>
            {
                string marker = "\"Files\": {"; int at = text.IndexOf(marker, StringComparison.Ordinal);
                var first = JsonNode.Parse(text)["plan"]["Files"].AsObject().First();
                return text.Insert(at + marker.Length, JsonSerializer.Serialize(first.Key) + ":" + first.Value.ToJsonString() + ",");
            }); Rejected(path, cause: "Duplicate JSON member");
        });
        Check("post-validation-final-binding-change-invalidates-receipt", () =>
        {
            string path = NewCase("changed-binding"); Require(Call("--replay-calibration-preflight", path) == 0, "Initial final admission did not pass.");
            var input = JsonNode.Parse(File.ReadAllBytes(path)); string evidence = Path.Combine(Path.GetDirectoryName(path), "late-provenance.txt"); File.WriteAllText(evidence, "late binding");
            input["plan"]["Files"][evidence] = Hash(evidence); File.WriteAllText(path, input.ToJsonString(Json));
            Rejected(path, "--replay-calibration-check-launch", cause: "Preflight receipt does not validate"); Rejected(path, "--replay-calibration-run", cause: "Preflight receipt does not validate");
        });
        Check("fixed-purpose-and-document-expiry-rejected", () =>
        {
            Rejected(NewCase("expired-purpose", n => n["originalPurposeDeadlineUtc"] = DateTime.UtcNow.AddMinutes(-1)), cause: "Fixed purpose window");
            Rejected(NewCase("short-purpose", n => n["originalPurposeDeadlineUtc"] = DateTime.UtcNow.AddMinutes(30)), cause: "Fixed purpose window");
            Rejected(NewCase("expired-document", n => n["expiresUtc"] = DateTime.UtcNow.AddMinutes(-1)), cause: "Final admission expired");
        });
        Check("changed-content-binding-and-wrong-approved-plan-hash-rejected", () =>
        {
            Rejected(NewCase("bad-binding", n => n["plan"]["Files"][n["plan"]["EvidenceRoles"]["resources"].GetValue<string>()] = new string('0', 64)));
            Rejected(NewCase("wrong-plan-hash"), expected: new string('0', 64), cause: "Reviewed final plan identity mismatch");
        });
        File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new { result = failed == 0 ? "passed" : "failed", failed, rows,
            entry = "WorkerChecks.Main -> ReplayCalibrationRun.Run for all modes; positive paths stop before current devices/source",
            captureStarted = false, audioStarted = false, engineStarted = false, policyMilliseconds = 500,
            claim = "Owning file/host/capacity admission and receipt linkage only. Actual device drift, recording, process-access cause and Saved acceptance remain unproved." }, Json));
        Console.WriteLine($"Final admission checks: {rows.Count} cases, {failed} failures;{root}"); return failed == 0 ? 0 : 1;
    }
}
