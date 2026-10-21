"""Offline behavior tests: no recorder, game, GPU, UI, network or captured media."""
import copy
import hashlib
import json
from pathlib import Path
import tempfile
import unittest
import shutil
import subprocess
import importlib.util
import sys
from unittest.mock import patch

import experiment_contract as contract


class ExperimentChecks(unittest.TestCase):
    def setUp(self):
        self.repository = Path(__file__).resolve().parent.parent
        artifacts = self.repository / "artifacts"
        artifacts.mkdir(exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(prefix="contract-checks-", dir=artifacts)
        self.root = Path(self.temporary.name).resolve()
        self.identity = self.root / "build.txt"
        self.identity.write_text("known-build")
        self.plan = {
            "version": 1, "experiment_id": "offline-fixture", "environment": "controlled fixture",
            "source_qualification": "fake source for offline checks only",
            "acceptance_criteria": {"scope": "Sampled fixture validity only; no performance claim"},
            "identity_files": {self.identity.relative_to(self.repository).as_posix(): hashlib.sha256(self.identity.read_bytes()).hexdigest()},
            "window_seconds": [2, 5], "sampling_interval_seconds": 1, "maximum_sample_gap_seconds": 1.5,
            "trial_seconds": 8, "qualification": {"label": "qualification", "mode": "On"},
            "trials": [{"label": "off-1", "mode": "Off"}, {"label": "on-1", "mode": "On"}],
            "storage": {"expected_bytes": 100000, "budget_bytes": 1000000, "trial_reserve_bytes": 10000,
                        "finalization_reserve_bytes": 10000, "poll_growth_reserve_bytes": 10000, "minimum_free_bytes": 0},
            "retention": [{"path": "qualification.json", "class": "report", "needed_for": "setup proof"}]}
        self.plan["collection_inputs"] = {}
        for role in sorted(contract.COLLECTION_ROLES):
            path = self.identity if role == "desktop_assembly" else self.root / (role + ".fixture")
            if path != self.identity:
                path.write_text("known-" + role)
            name = path.relative_to(self.repository).as_posix()
            self.plan["collection_inputs"][role] = name
            self.plan["identity_files"][name] = contract.file_sha256(path)
        self.path = self.root / "plan.json"
        self.save_plan()

    def tearDown(self):
        assert self.root.is_relative_to(self.repository / "artifacts")
        self.temporary.cleanup()

    def save_plan(self):
        self.path.write_text(json.dumps(self.plan))

    def samples(self, mode="On"):
        return [{"ElapsedSeconds": t, "Status": {"Ready": mode == "On", "Paused": mode == "Off",
                 "WorkerProcessId": 10, "ApplicationName": "cs2", "Config": {"ReplayMinutes": 1, "BitrateMbps": 5},
                 "Metrics": {"CaptureProcessId": 20 if mode == "On" else 0,
                 "Recoveries": 0, "CaptureProgressSeconds": t + 5, "CaptureCpuPercent": 0, "WorkerCpuPercent": 0, "CacheBytes": 0}}} for t in range(8)]

    def trace(self, label, mode="On", samples=None):
        path = self.root / (label + "-worker.json")
        csv = self.root / (label + "-presentmon.csv")
        csv.write_text("TimeInMs,MsBetweenPresents,PresentMode,AllowsTearing,MsUntilDisplayed\n" +
                       "\n".join(f"{t*1000},1000,fixture,0,0" for t in range(8)))
        path.write_text(json.dumps({"Label": label, "Mode": mode, "TraceExitCode": 0, "PresentMonCSV": str(csv),
                        "ExperimentPlan": self.plan, "ExperimentPlanSha256": contract.digest(self.plan),
                        "Samples": samples if samples is not None else self.samples(mode)}))
        return path

    def qualify(self):
        contract.admit(self.path, self.root, "qualification", "On", 8, True)
        contract.finish(self.path, self.root, "qualification", self.trace("qualification"))

    def test_plan_requires_explicit_limits_and_retention(self):
        contract.load_plan(self.path)
        for field in ["window_seconds", "sampling_interval_seconds", "maximum_sample_gap_seconds", "storage", "retention", "identity_files", "collection_inputs", "acceptance_criteria"]:
            with self.subTest(field=field):
                value = copy.deepcopy(self.plan)
                del value[field]
                self.path.write_text(json.dumps(value))
                with self.assertRaises((KeyError, ValueError)):
                    contract.load_plan(self.path)

    def test_invalid_plans(self):
        for change in [lambda p: p.update(window_seconds=[5, 2]),
                       lambda p: p.update(maximum_sample_gap_seconds=.5),
                       lambda p: p.update(sampling_interval_seconds=float("nan")),
                       lambda p: p["storage"].update(expected_bytes=1000001),
                       lambda p: p["storage"].update(poll_growth_reserve_bytes=0),
                       lambda p: p["trials"].append(p["trials"][0]),
                       lambda p: p["retention"][0].update(path="../escape")]:
            value = copy.deepcopy(self.plan)
            change(value)
            self.path.write_text(json.dumps(value))
            with self.assertRaises(ValueError):
                contract.load_plan(self.path)

    def test_sampled_on_and_off_health(self):
        for mode in ("On", "Off"):
            self.assertEqual(contract.validate_samples(self.plan, self.samples(mode), mode)["result"], "valid")

    def test_invalid_status_traces(self):
        mutations = [lambda s: s.__delitem__(slice(0, 3)),
                     lambda s: s.__delitem__(slice(5, None)),
                     lambda s: s.__delitem__(slice(3, 5)),
                     lambda s: s[3]["Status"].update(Ready=False),
                     lambda s: s[3]["Status"].update(WorkerProcessId=11),
                     lambda s: s[3]["Status"]["Metrics"].update(CaptureProcessId=21),
                     lambda s: s[3]["Status"]["Metrics"].update(Recoveries=1),
                     lambda s: s[3]["Status"]["Metrics"].update(CaptureProgressSeconds=0),
                     lambda s: s[3]["Status"].update(ApplicationName="other"),
                     lambda s: s[3].update(ElapsedSeconds=s[2]["ElapsedSeconds"])]
        for mutation in mutations:
            samples = self.samples()
            mutation(samples)
            with self.assertRaises(ValueError):
                contract.validate_samples(self.plan, samples, "On")

    def test_storage_admission_and_free_space(self):
        contract.check_storage(self.plan, self.root, admission=True, free_bytes=1000000)
        with self.assertRaisesRegex(ValueError, "free-space"):
            contract.check_storage(self.plan, self.root, admission=True, free_bytes=1)
        self.plan["storage"]["budget_bytes"] = 20001
        with self.assertRaisesRegex(ValueError, "budget"):
            contract.check_storage(self.plan, self.root, admission=True, free_bytes=1000000)

    def test_qualification_required(self):
        with self.assertRaises(OSError):
            contract.admit(self.path, self.root, "off-1", "Off", 8)

    def test_actual_preview_paths_start_no_collection(self):
        shell = shutil.which("pwsh")
        if shell is None:
            self.skipTest("PowerShell is unavailable")
        self.plan["sampling_interval_seconds"] = 1.0
        self.save_plan()
        for script in ("game-benchmark.ps1", "run-game-benchmark-batch.ps1"):
            command = [shell, "-NoProfile", "-File", str(self.repository / "tests" / script),
                       "-ExperimentPlan", str(self.path), "-ValidatePlan"]
            if script.startswith("run-"):
                command += ["-DataDirectory", str(self.root / "worker")]
            result = subprocess.run(command, capture_output=True, text=True, encoding="utf-8", timeout=15)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(json.loads(result.stdout)["experiment_id"], self.plan["experiment_id"])
            self.assertEqual(contract.digest(json.loads(result.stdout)), contract.digest(self.plan))
            self.assertFalse((self.root / "experiment-state").exists())
            self.assertFalse((self.root / "worker").exists())

    def test_frozen_plan_and_sequence(self):
        self.qualify()
        with self.assertRaises(OSError):
            contract.admit(self.path, self.root, "on-1", "On", 8)
        contract.admit(self.path, self.root, "off-1", "Off", 8)
        contract.finish(self.path, self.root, "off-1", self.trace("off-1", "Off"))
        self.plan["storage"]["budget_bytes"] += 1
        self.save_plan()
        with self.assertRaisesRegex(ValueError, "Frozen"):
            contract.admit(self.path, self.root, "on-1", "On", 8)

    def test_invalid_trial_blocks_next(self):
        self.qualify()
        contract.admit(self.path, self.root, "off-1", "Off", 8)
        with self.assertRaisesRegex(ValueError, "deliberate failure"):
            contract.finish(self.path, self.root, "off-1", error="deliberate failure")
        with self.assertRaisesRegex(ValueError, "stopped"):
            contract.admit(self.path, self.root, "on-1", "On", 8)
        self.assertTrue((self.root / "experiment-state/off-1.result.json").exists())

    def test_interrupted_trial_blocks_next(self):
        self.qualify()
        contract.admit(self.path, self.root, "off-1", "Off", 8)
        with self.assertRaisesRegex(ValueError, "incomplete"):
            contract.admit(self.path, self.root, "on-1", "On", 8)

    def test_changed_build_rejected(self):
        self.identity.write_text("different-build")
        with self.assertRaisesRegex(ValueError, "identity changed"):
            contract.admit(self.path, self.root, "qualification", "On", 8, True)

    def test_required_collection_identities_cannot_be_omitted(self):
        original = copy.deepcopy(self.plan)
        for role in contract.COLLECTION_ROLES:
            with self.subTest(role=role):
                self.plan = copy.deepcopy(original)
                del self.plan["identity_files"][self.plan["collection_inputs"][role]]
                self.save_plan()
                with self.assertRaisesRegex(ValueError, "identity hash"):
                    contract.load_plan(self.path)

    def test_each_measured_input_is_rechecked_after_qualification(self):
        self.qualify()
        for role, name in self.plan["collection_inputs"].items():
            with self.subTest(role=role):
                path = self.repository / name
                original = path.read_bytes()
                path.write_text("changed input")
                with self.assertRaisesRegex(ValueError, "identity changed"):
                    contract.admit(self.path, self.root, "off-1", "Off", 8)
                path.write_bytes(original)

    def test_incomplete_presentation_trace_rejected(self):
        contract.admit(self.path, self.root, "qualification", "On", 8, True)
        trace = self.trace("qualification")
        (self.root / "qualification-presentmon.csv").write_text("TimeInMs\n0\n1000\n")
        with self.assertRaisesRegex(ValueError, "presentation window"):
            contract.finish(self.path, self.root, "qualification", trace)

    def test_valid_inconclusive_result_does_not_extend_batch(self):
        self.qualify()
        for label, mode in [("off-1", "Off"), ("on-1", "On")]:
            contract.admit(self.path, self.root, label, mode, 8)
            trace = self.trace(label, mode)
            data = contract.read_json(trace)
            data["statistics"] = "inconclusive"
            trace.write_text(json.dumps(data))
            self.assertEqual(contract.finish(self.path, self.root, label, trace)["result"], "valid")
        with self.assertRaises(ValueError):
            contract.admit(self.path, self.root, "extra-run", "On", 8)
        with self.assertRaisesRegex(ValueError, "reused"):
            contract.admit(self.path, self.root, "on-1", "On", 8)

    def test_prospective_window_selected_after_qualification(self):
        self.qualify()
        self.plan["window_seconds"] = [3, 6]
        self.save_plan()
        contract.admit(self.path, self.root, "off-1", "Off", 8)
        self.assertEqual(contract.read_json(self.root / "experiment-state/batch-plan.json")["window_seconds"], [3, 6])

    def test_analyzer_uses_frozen_labels_window_and_hashes(self):
        if importlib.util.find_spec("numpy") is None:
            self.skipTest("Existing analyzer requires NumPy")
        self.qualify()
        for label, mode in [("off-1", "Off"), ("on-1", "On")]:
            contract.admit(self.path, self.root, label, mode, 8)
            contract.finish(self.path, self.root, label, self.trace(label, mode))
        command = [sys.executable, str(self.repository / "tests/analyze-game-benchmark.py"), str(self.root)]
        result = subprocess.run(command, capture_output=True, text=True, timeout=15)
        self.assertEqual(result.returncode, 0, result.stderr)
        report = json.loads(result.stdout)
        self.assertEqual([t["label"] for t in report["traces"]], ["off-1", "on-1"])
        self.assertEqual(report["traces"][0]["window_seconds"], [2, 5])
        result = subprocess.run(command + ["--end", "6"], capture_output=True, text=True, timeout=15)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("frozen batch plan", result.stderr)
        with (self.root / "on-1-presentmon.csv").open("a") as file:
            file.write("\n")
        result = subprocess.run(command, capture_output=True, text=True, timeout=15)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("evidence changed", result.stderr)

    def test_closure_retains_files_and_records_missing_inputs(self):
        result = contract.close(self.path, self.root)
        self.assertEqual(result["retention"][0]["availability"], "missing")
        self.assertTrue(self.identity.is_file())
        with self.assertRaisesRegex(ValueError, "closed"):
            contract.admit(self.path, self.root, "qualification", "On", 8, True)

    def test_closure_preserves_original_identity_after_a_later_build(self):
        original = copy.deepcopy(self.plan["identity_files"])
        self.identity.write_text("later-build")
        result = contract.close(self.path, self.root)
        self.assertEqual(result["plan"]["identity_files"], original)
        self.assertTrue(self.identity.exists())

    def test_closure_streams_file_hashes(self):
        media = self.root / "recording.mp4"
        with media.open("wb") as file:
            for _ in range(4):
                file.write(b"x" * 1024 * 1024)
        with patch.object(Path, "read_bytes", side_effect=AssertionError("Whole-file reads forbidden")):
            result = contract.close(self.path, self.root)
        record = next(item for item in result["files"] if item["path"] == "recording.mp4")
        self.assertEqual(record["bytes"], 4 * 1024 * 1024)
        self.assertEqual(record["sha256"], contract.file_sha256(media))


if __name__ == "__main__":
    unittest.main()
