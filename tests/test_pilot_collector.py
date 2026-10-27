"""Controlled preparation checks; no media tools, native recorder or GPU."""
import unittest
import copy
import contextlib
import io
import json
import tempfile
from pathlib import Path
from unittest.mock import patch

import pilot_collector as collector


class PilotPreparationChecks(unittest.TestCase):
    def setUp(self):
        self.plan = collector.preview()
        binding = {"source_manifest_sha256": "a" * 64, "cutoff": "302/1",
                   "destination": self.plan["root"], "volume": "controlled-volume", "allocation_unit": 4096}
        self.observation = {"provenance": "controlled-fixture", "plan_sha256": self.plan["plan_sha256"],
            "snapshot_binding": binding, "current_binding": copy.deepcopy(binding),
            "capacity_status": "known", "age_ms": 0, "used_bytes": 1_000_000_000,
            "next_unmaterialized_bytes": 2_000_000_000, "unwritten_closure_bytes": 128_000_000,
            "poll_reserve_bytes": 1_000_000_000, "caller_free_bytes": 43_128_000_000}

    def test_preview_is_inert_even_with_missing_root(self):
        with patch("os.stat", side_effect=AssertionError("filesystem stat")), \
                patch("builtins.open", side_effect=AssertionError("filesystem open")), \
                patch("pathlib.Path.mkdir", side_effect=AssertionError("mkdir")), \
                patch("subprocess.Popen", side_effect=AssertionError("child launch")) as launch:
            plan = collector.preview("Z:/missing/never-create")
            with contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(collector.main(["--plan", "--root", "Z:/missing/never-create"]), 0)
        self.assertFalse(plan["execution_enabled"])
        self.assertEqual(plan["actual_commands_launched"], [])
        self.assertEqual(plan["root"], "Z:/missing/never-create")
        launch.assert_not_called()

    def test_preview_freezes_one_generated_source_and_one_export(self):
        plan = collector.preview("artifacts/future pilot")
        source, export = plan["commands"]
        self.assertEqual(source["stage"], "source-304s")
        args = source["arguments"]
        for flag, value in {"-c:v": "h264_nvenc", "-b:v": "20M", "-g": "60",
                            "-c:a": "aac", "-b:a": "192k", "-ar": "48000", "-ac": "2"}.items():
            self.assertEqual(args[args.index(flag) + 1], value)
        self.assertIn("testsrc2=size=1920x1080:rate=60:duration=304", args)
        self.assertIn("-nostdin", args)
        self.assertIn("-n", args)
        self.assertEqual(export["stage"], "baseline-300s")
        args = export["arguments"]
        self.assertEqual(args[args.index("-t") + 1], "300")
        self.assertEqual(args[args.index("-c") + 1], "copy")
        self.assertEqual(args[args.index("-movflags") + 1], "+faststart")
        self.assertEqual(export["unresolved"], ["qualified CSV/IDR selection and relative offset"])
        self.assertEqual(plan["limits"]["hard_bytes"], 4_500_000_000)
        self.assertEqual(plan["limits"]["used_stop_bytes"], 3_350_000_000)
        self.assertEqual(plan["limits"]["max_children"], 12)
        self.assertEqual(plan["limits"]["cumulative_seconds"], 1800)
        self.assertTrue(plan["future_effects"]["gpu_nvenc"])
        self.assertFalse(any(plan["current_effects"].values()))
        self.assertIn("whole-child verified engine lease adapter", plan["execution_blockers"])

    def test_exact_floor_accepts_controlled_storage_snapshot_only(self):
        receipt = collector.assess(self.plan, self.observation)
        self.assertTrue(receipt["storage_preparation_eligible"])
        self.assertFalse(receipt["execution_enabled"])
        self.assertEqual(receipt["accounting"]["used_plus_future_bytes"], 4_128_000_000)
        self.assertEqual(receipt["accounting"]["future_bytes"], 3_128_000_000)
        self.assertEqual(receipt["accounting"]["required_caller_free_bytes"], 43_128_000_000)
        self.assertEqual(receipt["observation"], self.observation)
        self.assertEqual(receipt["actual_commands_launched"], [])
        self.assertEqual(receipt["facts_verified_natively"], False)

    def test_capacity_and_binding_sensitivity_rejects_uncertain_snapshot(self):
        changes = [({"caller_free_bytes": 43_127_999_999}, "caller-free"),
                   ({"caller_free_bytes": 0}, "caller-free"),
                   ({"capacity_status": "unknown", "caller_free_bytes": None}, "capacity unknown"),
                   ({"capacity_status": "denied", "caller_free_bytes": None}, "capacity denied"),
                   ({"age_ms": 2001}, "stale observation")]
        for change, reason in changes:
            with self.subTest(change=change):
                observation = copy.deepcopy(self.observation)
                observation.update(change)
                result = collector.assess(self.plan, observation)
                self.assertFalse(result["storage_preparation_eligible"])
                self.assertTrue(any(reason in text for text in result["reasons"]))
        for field, value in {"volume": "changed-volume", "allocation_unit": 8192,
                             "source_manifest_sha256": "b" * 64, "cutoff": "303/1",
                             "destination": "artifacts/other"}.items():
            with self.subTest(binding=field):
                observation = copy.deepcopy(self.observation)
                observation["current_binding"][field] = value
                result = collector.assess(self.plan, observation)
                self.assertFalse(result["storage_preparation_eligible"])
                self.assertIn("stale snapshot binding", result["reasons"])

    def test_resource_boundaries_and_malformed_values_fail_closed(self):
        observation = copy.deepcopy(self.observation)
        observation["used_bytes"] = 3_350_000_000
        observation["next_unmaterialized_bytes"] = 0
        self.assertIn("used-stop reached", collector.assess(self.plan, observation)["reasons"])
        observation["next_unmaterialized_bytes"] = 2_000_000_000
        observation["used_bytes"] = 1_372_000_000
        self.assertTrue(collector.assess(self.plan, observation)["storage_preparation_eligible"])
        observation["used_bytes"] += 1
        self.assertIn("hard budget insufficient for future obligations", collector.assess(self.plan, observation)["reasons"])
        for field in ("used_bytes", "next_unmaterialized_bytes", "unwritten_closure_bytes",
                      "poll_reserve_bytes", "age_ms", "caller_free_bytes"):
            for value in (True, -1, 1.0, "1", 2**63, None):
                with self.subTest(field=field, value=value):
                    bad = copy.deepcopy(self.observation)
                    bad[field] = value
                    with self.assertRaises(ValueError):
                        collector.assess(self.plan, bad)
        old_stop = copy.deepcopy(self.plan)
        old_stop["limits"]["used_stop_bytes"] = 3_400_000_000
        with self.assertRaisesRegex(ValueError, "combined closure and poll"):
            collector.assess(old_stop, self.observation)
        for field in ("plan_sha256", "provenance"):
            bad = copy.deepcopy(self.observation)
            del bad[field]
            with self.assertRaises(ValueError):
                collector.assess(self.plan, bad)

    def test_unknown_binding_and_reduced_reserves_cannot_become_eligible(self):
        for field, value in {"volume": "", "allocation_unit": None, "source_manifest_sha256": "",
                             "destination": "artifacts/unrelated", "cutoff": ""}.items():
            observation = copy.deepcopy(self.observation)
            observation["snapshot_binding"][field] = value
            observation["current_binding"][field] = value
            with self.subTest(field=field):
                self.assertFalse(collector.assess(self.plan, observation)["storage_preparation_eligible"])
        for field in ("poll_reserve_bytes", "unwritten_closure_bytes"):
            observation = copy.deepcopy(self.observation)
            observation[field] = 0
            with self.subTest(reserve=field):
                self.assertFalse(collector.assess(self.plan, observation)["storage_preparation_eligible"])
        for root in ("", "artifacts/../other", "artifacts\\..\\other", "bad\nroot"):
            with self.subTest(root=root), self.assertRaises(ValueError):
                collector.preview(root)
        modified = copy.deepcopy(self.plan)
        modified["commands"][0]["arguments"].append("-arbitrary")
        with self.assertRaises(ValueError):
            collector.assess(modified, self.observation)

    def test_closure_writes_remain_charged_when_cleanup_is_unconfirmed(self):
        self.observation.update(used_bytes=3_350_000_000, next_unmaterialized_bytes=0)
        before = collector.assess(self.plan, self.observation)
        for state, cleanup in (("present", "failed"), ("unknown", "unknown"), ("absent", "failed")):
            after = {"provenance": "controlled-fixture", "assessment_sha256": before["receipt_sha256"],
                "binding": self.observation["current_binding"], "closure_written_bytes": 32_000_000,
                "retained_used_bytes": 3_382_000_000, "owned_producer_state": state,
                "cleanup_outcome": cleanup, "child_exit_code": 0}
            with self.subTest(state=state, cleanup=cleanup):
                receipt = collector.settle(self.plan, before, after)
                self.assertFalse(receipt["closed_cleanly"])
                self.assertFalse(receipt["execution_enabled"])
                self.assertFalse(receipt["next_stage_enabled"])
                self.assertEqual(receipt["accounting"], {"retained_used_bytes": 3_382_000_000,
                    "unwritten_closure_bytes": 96_000_000, "poll_reserve_bytes": 1_000_000_000,
                    "retained_plus_reserves_bytes": 4_478_000_000})
                self.assertEqual(receipt["settlement_observation"], after)
                self.assertIn("owned producer absence and cleanup not both verified", receipt["reasons"])

    def test_settlement_requires_intact_prior_receipt_and_charged_closure(self):
        before = collector.assess(self.plan, self.observation)
        after = {"provenance": "caller-supplied-unverified", "assessment_sha256": before["receipt_sha256"],
            "binding": self.observation["current_binding"], "closure_written_bytes": 32_000_000,
            "retained_used_bytes": 1_032_000_000, "owned_producer_state": "absent", "cleanup_outcome": "verified"}
        positive = collector.settle(self.plan, before, after)
        self.assertTrue(positive["closed_cleanly"])
        self.assertEqual(positive["accounting"]["poll_reserve_bytes"], 0)
        self.assertFalse(positive["facts_verified_natively"])
        for field, value in {"closure_written_bytes": True, "retained_used_bytes": 1_000_000_000,
                             "assessment_sha256": "changed", "cleanup_outcome": None}.items():
            bad = copy.deepcopy(after)
            bad[field] = value
            with self.subTest(field=field), self.assertRaises(ValueError):
                collector.settle(self.plan, before, bad)
        bad_before = copy.deepcopy(before)
        bad_before["storage_preparation_eligible"] = False
        with self.assertRaises(ValueError):
            collector.settle(self.plan, bad_before, after)
        changed = copy.deepcopy(after)
        changed["binding"]["volume"] = "changed"
        self.assertFalse(collector.settle(self.plan, before, changed)["closed_cleanly"])

    def test_cli_preview_and_bounded_assessment_report_truthful_exits(self):
        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            self.assertEqual(collector.main(["--root", "Z:/does-not-exist"]), 0)
        self.assertFalse(json.loads(output.getvalue())["execution_enabled"])
        artifacts = Path(__file__).resolve().parents[1] / "artifacts" / "calibration-collector-writer"
        artifacts.mkdir(parents=True, exist_ok=True)
        # Retain each accepted/rejected input; do not delete failed attempts.
        root = Path(tempfile.mkdtemp(prefix="cli-fixtures-", dir=artifacts))
        source = root / "observation.json"
        for label, value, expected_exit in (("accepted", self.observation, 0),
                ("rejected", dict(self.observation, caller_free_bytes=43_127_999_999), 1)):
            source = root / (label + ".json")
            source.write_text(json.dumps(value), encoding="utf-8")
            output = io.StringIO()
            with contextlib.redirect_stdout(output):
                self.assertEqual(collector.main(["--assess", str(source)]), expected_exit)
            envelope = json.loads(output.getvalue())
            (root / (label + "-receipt.json")).write_text(output.getvalue(), encoding="utf-8")
            self.assertEqual(envelope["receipt"]["storage_preparation_eligible"], expected_exit == 0)
            self.assertFalse(envelope["execution_enabled"])
            self.assertFalse(envelope["receipt"]["facts_verified_natively"])
        source = root / "malformed.json"
        source.write_text("{", encoding="utf-8")
        with contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(collector.main(["--assess", str(source)]), 2)
        # Boundary fake file proves bounded read without writing a >1MiB fixture.
        with patch("builtins.open", return_value=io.BytesIO(b" " * 1_048_577)), \
                contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(collector.main(["--assess", "controlled-large-input"]), 2)
        with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit) as caught:
            collector.main(["--execute"])
        self.assertEqual(caught.exception.code, 2)

    def test_unknown_prior_binding_cannot_produce_clean_settlement(self):
        self.observation["current_binding"]["volume"] = ""
        self.observation["snapshot_binding"]["volume"] = ""
        before = collector.assess(self.plan, self.observation)
        after = {"provenance": "controlled-fixture", "assessment_sha256": before["receipt_sha256"],
            "binding": self.observation["current_binding"], "closure_written_bytes": 0,
            "retained_used_bytes": 1_000_000_000, "owned_producer_state": "absent", "cleanup_outcome": "verified"}
        result = collector.settle(self.plan, before, after)
        self.assertFalse(result["closed_cleanly"])
        self.assertEqual(result["accounting"]["poll_reserve_bytes"], 1_000_000_000)

    def test_json_type_changes_in_plan_or_receipt_invalidate_binding(self):
        plan = copy.deepcopy(self.plan)
        plan["version"] = True  # Python equality must not equate this JSON change to integer 1.
        with self.assertRaises(ValueError):
            collector.assess(plan, self.observation)
        before = collector.assess(self.plan, self.observation)
        after = {"provenance": "controlled-fixture", "assessment_sha256": before["receipt_sha256"],
            "binding": self.observation["current_binding"], "closure_written_bytes": 0,
            "retained_used_bytes": 1_000_000_000, "owned_producer_state": "absent", "cleanup_outcome": "verified"}
        before["execution_enabled"] = 0
        with self.assertRaises(ValueError):
            collector.settle(self.plan, before, after)

    def test_binding_allocation_unit_type_change_is_stale(self):
        self.observation["snapshot_binding"]["allocation_unit"] = 4096.0
        result = collector.assess(self.plan, self.observation)
        self.assertFalse(result["storage_preparation_eligible"])
        self.assertIn("stale snapshot binding", result["reasons"])


if __name__ == "__main__":
    unittest.main()
