"""Generated metadata checks; two bounded Windows owner previews and an opt-in prebuilt helper-parser roundtrip, with no native/media execution."""
import importlib.util
import json
import os
from pathlib import Path
import shutil
import tempfile
import subprocess
import re
import unittest

SPEC = importlib.util.spec_from_file_location("confirmation_package", Path(__file__).with_name("replay-confirmation-package.py"))
PACKAGE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(PACKAGE)


def write_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(PACKAGE.encoded(value))


def make_fixture(directory):
    """Return (recipe, fresh output) for generated controller-compatible metadata."""
    inputs = directory / "inputs"
    inputs.mkdir()
    names = ("dotnet.exe", "F10Recorder.WorkerChecks.dll", "pwsh.exe", "runtime.dll", "ffmpeg-9.0.2/bin/ffmpeg.exe")
    pins = {}
    for name in names:
        path = inputs / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(("generated nonexecutable metadata pin " + name).encode())
        pins[str(path)] = PACKAGE.hash_file(path)
    if os.name == "nt":
        console = Path(os.environ["SystemRoot"]) / "system32/conhost.exe"
        pins[str(console)] = PACKAGE.hash_file(console)
    runtime = inputs / "runtime.json"
    write_json(runtime, {str(inputs / "runtime.dll"): pins[str(inputs / "runtime.dll")]})
    templates = {}
    for operation in PACKAGE.OPERATIONS:
        retained = operation != "C00"
        p = {"FreshCandidateAdmission": "issue83-retained-same-C00-final-minute-v10" if retained else "issue83-C00-same-capture-reference-v10", "ReplayMinutes": 1 if retained else 5, "DeadlineSeconds": 900 if retained else 1800, "OfflineDeadlineSeconds": 600 if retained else 900, "MaximumChildren": 9 if retained else 16, "RetainedChildren": 0, "RetainedElapsedSeconds": 0, "RetainedOfflineSeconds": 0, "Order": ["H03"], "FloorBytes": 60000000000, "ReplayExporterRecipe": "replay-avc-aac-owned-completed-history-direct-avcc-with-reserved-staging-exact-fallback-v5", "EngineRoot": {"$inputPath": str(inputs)}, "CompositeQualification": not retained, "SelectedRetainedFiles": [], "Files": {}, "EvidenceRoles": {}, "Environment": "", "RetainedRoots": []}
        if not retained:
            p.update(CompositeExplicitForegroundRelease=True, CompositeManualForegroundHandoff=True)
        template = {"operation": operation, "controllerKind": "issue83-two-confirmation-owner-v2", "Resources" if retained else "plan": p, "parentHandleBudget": 49152, "maximumSupportChildren": 1, "maximumOuterRoles": 1, "maximumManagedRoles": 9 if retained else 16, "controlCreatorEvidence": "prefixture-held-instance-control-receipt-v1", "creatorProofPolicy": "qualification-child-creator-v1", "creatorProofMilliseconds": 130000, "rawChildByteLimit": 4 * ((900 if retained else 1800) * 2 + 2) * 4096, "prospectiveSameCaptureRules": "generated fixture: genuine C00 final minute only"}
        if retained:
            template["Inputs"] = []
            template["MappingReviewHash"] = "A" * 64
            template["MappingReviewer"] = "generated-nonempirical-fixture-review"
        source = inputs / (PACKAGE.SLOTS[operation] + "-source.json")
        write_json(source, template)
        templates[operation] = str(source)
    documents = {
        "outside-inventory-plan": {"kind": "owned-history-fixed-outside-inputs-v1", "files": [], "writerRoots": [{"$packagePath": "admissions/C00"}, {"$packagePath": "admissions/retained-1m"}]},
        "outside-outstanding-writes": {"kind": "owned-history-outside-future-v1", "maximumAdditionalBytes": 1000},
        "outside-forward-envelope": {"kind": "owned-history-outside-forward-v1", "forecastSha256": "stale", "baselinePlanSha256": "stale", "maximumAdditionalBytes": 1000, "reportReserveBytes": 100},
    }
    bindings = {operation: [] for operation in PACKAGE.OPERATIONS}
    for role, content in documents.items():
        source = inputs / (role + ".json")
        write_json(source, content)
        bindings["C00"].append({"role": role, "path": "admissions/C00/" + role + ".json", "source": str(source)})
    for role in ("source-manifest", "host-facts", "preservation-before", "retained-attempt", "resources", "rehearsal"):
        source = inputs / (role + ".json")
        write_json(source, {"fixture": role, "empiricalAcceptance": False})
        bindings["C00"].append({"role": role, "path": "admissions/C00/" + role + ".json", "source": str(source)})
    instructions = inputs / "RUN.md"
    instructions.write_text("Generated fixture instructions; no execution authority.\n")
    bindings["C00"].append({"role": "run-instructions", "path": "admissions/C00/RUN.md", "source": str(instructions)})
    for role in ("outside-storage-baseline", "outside-inventory", "fresh-candidate-qualification"):
        bindings["C00"].append({"role": role, "path": "admissions/C00/" + role + ".json"})
    bindings["C00"].append({"role": "coverage-freeze", "path": "admissions/coverage-freeze.json"})
    retained_paths = {"coverage-freeze": "admissions/coverage-freeze.json", "C00-confirmation": "live/C00/context-confirmation.json", "C00-manifest": "live/C00/C00-input-manifest.json", "C00-acceptance": "live/C00/independent-acceptance.json", "C00-controller": "admissions/C00/controller.json", "C00-plan": "admissions/C00/plan.json", "C00-selection": "live/C00/H03/selection.jsonl", "C00-oracle-data": "live/C00/H03/reference-oracle/source-data.json", "C00-source-evidence": "live/C00/H03/reference-oracle/source/evidence.json", "C00-reference-binding": "live/C00/H03/source-reference-binding.json", "C00-source-clock": "live/C00/H03/source-clock.json"}
    bindings["retained-H03-1m"] = [{"role": role, "path": path} for role, path in retained_paths.items()]
    recipe = inputs / "recipe.json"
    write_json(recipe, {"kind": "confirmation-package-recipe-v1", "profile": PACKAGE.PROFILE, "revision": "generated-test-v1", "templates": templates, "bindings": bindings, "pins": pins, "runtimeManifests": {"qualification": str(runtime), "controller": str(runtime)}, "controllerRuntime": str(inputs / "pwsh.exe")})
    return recipe, directory / "package"


class PackageChecks(unittest.TestCase):
    def setUp(self):
        artifacts = PACKAGE.REPO / "artifacts"
        artifacts.mkdir(exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(prefix="confirmation-package-checks-", dir=artifacts)
        self.root = Path(self.temp.name)
        self.assertTrue(self.root.is_relative_to(artifacts))
        self.addCleanup(self.temp.cleanup)
        self.recipe, self.output = make_fixture(self.root)

    def recipe_value(self):
        return json.loads(self.recipe.read_text())

    def change_recipe(self, action):
        value = self.recipe_value()
        action(value)
        write_json(self.recipe, value)

    def build(self):
        return PACKAGE.build(self.recipe, self.output)

    def rejected_before_publication(self, pattern):
        with self.assertRaisesRegex(PACKAGE.PackageError, pattern):
            self.build()
        self.assertFalse(self.output.exists())

    def verify(self, sha=None):
        manifest = self.output / "invariants.json"
        return PACKAGE.verify(manifest, sha or PACKAGE.hash_file(manifest))

    def reseal(self):
        """Recompute hashes after generated mutations to test semantic consistency."""
        manifest_path = self.output / "invariants.json"
        manifest = json.loads(manifest_path.read_text())
        for slot in manifest["operations"].values():
            path = Path(slot["template"])
            template = json.loads(path.read_text())
            p = template.get("plan", template.get("Resources"))
            for name, value in p["Files"].items():
                if value not in ("conditional-derived", "conditional-approved-manifest"):
                    p["Files"][name] = PACKAGE.hash_file(Path(name))
            write_json(path, template)
        manifest["files"] = {path: PACKAGE.hash_file(Path(path)) for path in manifest["files"]}
        write_json(manifest_path, manifest)

    def test_build_verifies_and_creates_only_prospective_metadata(self):
        result = self.build()
        self.assertFalse(result["executionAuthority"])
        self.assertEqual(self.verify(), result)
        for name in ("live", "admissions/C00", "admissions/retained-1m"):
            self.assertTrue((self.output / name).is_dir())
        for path in self.output.rglob("*"):
            self.assertNotIn(path.name.casefold(), PACKAGE.FUTURE_NAMES)
        template = json.loads((self.output / "templates/C00-template.json").read_text())
        self.assertEqual(template["createdUtc"], "conditional-T0")
        self.assertFalse((self.output / "live/C00").exists())

    def test_deterministic_preparation(self):
        first = PACKAGE.prepare(self.recipe, self.output)
        self.assertEqual(first, PACKAGE.prepare(self.recipe, self.output))
        self.assertFalse(self.output.exists())

    def test_stale_input_envelope_is_recomputed_after_structural_path_resolution(self):
        self.build()
        path = self.output / "admissions/C00/outside-forward-envelope.json"
        envelope = json.loads(path.read_text())
        self.assertEqual(envelope["baselinePlanSha256"], PACKAGE.hash_file(path.with_name("outside-inventory-plan.json")))
        self.assertEqual(envelope["forecastSha256"], PACKAGE.hash_file(path.with_name("outside-outstanding-writes.json")))
        plan = json.loads(path.with_name("outside-inventory-plan.json").read_text())
        self.assertEqual(plan["writerRoots"], [str(self.output / "admissions/C00"), str(self.output / "admissions/retained-1m")])

    def test_changed_forecast_resealed_without_envelope_repair_rejects(self):
        self.build()
        path = self.output / "admissions/C00/outside-outstanding-writes.json"
        write_json(path, {"maximumAdditionalBytes": 2000})
        self.reseal()
        with self.assertRaisesRegex(PACKAGE.PackageError, "Stale forward-envelope"):
            self.verify()

    def test_changed_static_pin_rejects(self):
        self.build()
        (self.root / "inputs/runtime.dll").write_bytes(b"changed")
        with self.assertRaisesRegex(PACKAGE.PackageError, "Static invariant changed"):
            self.verify()

    def test_changed_expected_input_pin_prevents_publication(self):
        (self.root / "inputs/runtime.dll").write_bytes(b"changed")
        self.rejected_before_publication("binding drift")

    def test_missing_metadata_directory_rejects(self):
        self.build()
        (self.output / "admissions/retained-1m").rmdir()
        with self.assertRaisesRegex(PACKAGE.PackageError, "Missing ordinary preparation"):
            self.verify()

    def test_missing_live_parent_rejects(self):
        self.build()
        (self.output / "live").rmdir()
        with self.assertRaisesRegex(PACKAGE.PackageError, "Missing ordinary preparation"):
            self.verify()

    def test_future_execution_fact_prevents_static_verification(self):
        self.build()
        for name in ("worker-ready.json", "human-authorization.json", "PRE-READY.json", "independent-acceptance.json"):
            with self.subTest(name=name):
                path = self.output / name
                path.write_bytes(b"{}")
                with self.assertRaisesRegex(PACKAGE.PackageError, "Existing execution state"):
                    self.verify()
                self.assertEqual(path.read_bytes(), b"{}")
                path.unlink()

    def test_existing_output_is_preserved(self):
        self.output.mkdir()
        marker = self.output / "preserve.txt"
        marker.write_text("original")
        with self.assertRaisesRegex(PACKAGE.PackageError, "Preserve existing"):
            self.build()
        self.assertEqual(marker.read_text(), "original")

    def test_v4_profile_does_not_relabel_maintained_reader(self):
        self.change_recipe(lambda r: r.update(profile="artifact-v4-worker-ready"))
        self.rejected_before_publication("Unsupported profile")

    def test_artifact_extra_role_is_rejected(self):
        self.change_recipe(lambda r: r["bindings"]["C00"].append({"role": "worker-ready", "path": "worker-ready.json"}))
        self.rejected_before_publication("Role is not allowed")

    def test_retained_schema_matches_actual_strict_reader(self):
        reader = PACKAGE.REPO / "projects/F10Recorder.WorkerChecks/ReplayCoverageRetained.cs"
        if not reader.is_file():
            self.skipTest("Matching issue-83 retained C# reader is absent; standalone static-profile checks still run")
        source = reader.read_text(encoding="utf-8-sig")
        declaration = re.search(r"string\[\] names=\[(.*?)\];", source, re.DOTALL)
        self.assertIsNotNone(declaration)
        self.assertEqual(PACKAGE.RETAINED_FIELDS, frozenset(re.findall(r'"([^"\n]+)"', declaration.group(1))))

    def test_missing_retained_mapping_review_rejects(self):
        path = Path(self.recipe_value()["templates"]["retained-H03-1m"])
        value = json.loads(path.read_text())
        del value["MappingReviewHash"]
        write_json(path, value)
        self.rejected_before_publication("Caller-supplied mapping review")

    def test_unknown_retained_outer_field_rejects(self):
        path = Path(self.recipe_value()["templates"]["retained-H03-1m"])
        value = json.loads(path.read_text())
        value["padding"] = "unexpected"
        write_json(path, value)
        self.rejected_before_publication("Exact retained outer schema")

    def test_presentation_method_is_static_only(self):
        method = self.root / "inputs/method.json"
        write_json(method, {"Method": "generated method metadata only"})
        self.change_recipe(lambda r: r["bindings"]["C00"].append({"role": "source-presentation-method", "path": "method.json", "source": str(method)}))
        self.build()
        self.verify()

    def test_missing_common_admission_role_rejects(self):
        self.change_recipe(lambda r: r["bindings"].update(C00=[binding for binding in r["bindings"]["C00"] if binding["role"] != "run-instructions"]))
        self.rejected_before_publication("Missing mandatory C00 admission role")

    def test_static_run_instructions_cannot_be_late(self):
        self.change_recipe(lambda r: next(binding for binding in r["bindings"]["C00"] if binding["role"] == "run-instructions").pop("source"))
        self.rejected_before_publication("Role cannot be late-bound")

    def test_missing_required_executable_pin_rejects(self):
        self.change_recipe(lambda r: r["pins"].pop(str(self.root / "inputs/dotnet.exe")))
        self.rejected_before_publication("Exact owner executable role")

    def test_duplicate_role_rejects(self):
        self.change_recipe(lambda r: r["bindings"]["C00"].append(r["bindings"]["C00"][0]))
        self.rejected_before_publication("Duplicate operation role")

    def test_escaping_destination_rejects(self):
        self.change_recipe(lambda r: r["bindings"]["C00"][0].update(path="../escape.json"))
        self.rejected_before_publication("Ordinary relative")

    def test_media_pin_is_not_read(self):
        media = self.root / "inputs/generated.mp4"
        media.write_bytes(b"dummy generated fixture, never read by package tool")
        self.change_recipe(lambda r: r["pins"].update({str(media): "A" * 64}))
        self.rejected_before_publication("Media payload")

    def test_duplicate_json_member_rejects(self):
        self.recipe.write_text('{"kind":"x","KIND":"y"}')
        self.rejected_before_publication("Duplicate JSON member")

    def test_unknown_recipe_field_rejects(self):
        self.change_recipe(lambda r: r.update(automaticRelease=True))
        self.rejected_before_publication("Unknown fields")

    def test_finite_shape_change_rejects(self):
        path = Path(self.recipe_value()["templates"]["C00"])
        value = json.loads(path.read_text())
        value["plan"]["MaximumChildren"] += 1
        write_json(path, value)
        self.rejected_before_publication("finite shape")

    def test_foreign_string_substitution_rejects(self):
        path = Path(self.recipe_value()["templates"]["C00"])
        value = json.loads(path.read_text())
        value["description"] = "@package/unsafe-substitution"
        write_json(path, value)
        self.rejected_before_publication("typed path")

    def test_template_consumer_64k_boundary(self):
        initial = PACKAGE.prepare(self.recipe, self.output)
        target = self.output / "templates/C00-template.json"
        size = len(initial[target])
        path = Path(self.recipe_value()["templates"]["C00"])
        value = json.loads(path.read_text())
        value["plan"]["Environment"] = "x" * (PACKAGE.TEMPLATE_LIMIT - size)
        write_json(path, value)
        exact = PACKAGE.prepare(self.recipe, self.output)
        self.assertEqual(len(exact[target]), PACKAGE.TEMPLATE_LIMIT)
        value["plan"]["Environment"] += "x"
        write_json(path, value)
        self.rejected_before_publication("Generated metadata exceeds reader byte limit")

    def test_manifest_512k_boundary_checked_before_publication(self):
        initial = PACKAGE.prepare(self.recipe, self.output)
        size = len(initial[self.output / "invariants.json"])
        value = self.recipe_value()
        value["revision"] += "x" * (PACKAGE.MANIFEST_LIMIT - size)
        write_json(self.recipe, value)
        exact = PACKAGE.prepare(self.recipe, self.output)
        self.assertEqual(len(exact[self.output / "invariants.json"]), PACKAGE.MANIFEST_LIMIT)
        value["revision"] += "x"
        write_json(self.recipe, value)
        self.rejected_before_publication("Generated metadata exceeds reader byte limit")

    def test_qualification_runtime_reader_256k_boundary(self):
        runtime = self.root / "inputs/runtime.json"
        original = runtime.read_bytes()
        runtime.write_bytes(original + b" " * (262145 - len(original)))
        self.rejected_before_publication("Bounded metadata read exceeded")

    @unittest.skipUnless(os.name == "nt" and shutil.which("pwsh"), "Actual Windows owner Plan requires PowerShell 7")
    def test_emitted_templates_pass_actual_maintained_owner_plan(self):
        self.build()
        owner = PACKAGE.REPO / "tests/replay-confirmation-owner.ps1"
        for slot in PACKAGE.SLOTS.values():
            with self.subTest(operation=slot):
                template = self.output / "templates" / (slot + "-template.json")
                result = subprocess.run([shutil.which("pwsh"), "-NoLogo", "-NoProfile", "-NonInteractive", "-File", str(owner), "-Template", "-Plan", "-PlanFile", str(template), "-ExpectedPlanSha256", PACKAGE.hash_file(template)], capture_output=True, text=True, timeout=20, cwd=PACKAGE.REPO)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                preview = json.loads(result.stdout)
                self.assertTrue(preview["prospectiveTemplate"])
                self.assertEqual(preview["captures"], 1 if slot == "C00" else 0)
                self.assertFalse((self.output / "live" / slot).exists())
                self.assertFalse((self.output / "worker-ready.json").exists())

    def test_missing_required_resource_constructor_field_rejects(self):
        path = Path(self.recipe_value()["templates"]["retained-H03-1m"])
        value = json.loads(path.read_text())
        del value["Resources"]["RetainedRoots"]
        write_json(path, value)
        self.rejected_before_publication("Required CalibrationPlan constructor fields missing")

    def test_emitted_retained_template_passes_actual_helper_parser(self):
        sdk = os.environ.get("CONFIRMATION_CHECKS_SDK")
        if not sdk:
            self.skipTest("Set CONFIRMATION_CHECKS_SDK to run the prebuilt owning helper parser")
        helper = PACKAGE.REPO / "projects/F10Recorder.WorkerChecks/bin/Release/net10.0-windows/F10Recorder.WorkerChecks.dll"
        self.assertTrue(helper.is_file(), "Build WorkerChecks before the helper compatibility check")
        self.build()
        report = self.root / "owning-reader.json"
        template = self.output / "templates/retained-1m-template.json"
        result = subprocess.run([sdk, str(helper), "--replay-same-C00-envelope-checks", str(template), str(report)],
                                capture_output=True, text=True, timeout=35, cwd=PACKAGE.REPO)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        receipt = json.loads(report.read_text())
        self.assertEqual(receipt["count"], 7)
        self.assertTrue(receipt["generatedMetadataOnly"])
        self.assertFalse(receipt["empiricalAcceptance"])
        self.assertFalse(receipt["mediaRead"])

    def test_expected_manifest_identity_required(self):
        self.build()
        with self.assertRaisesRegex(PACKAGE.PackageError, "SHA256 mismatch"):
            self.verify("0" * 64)


if __name__ == "__main__":
    unittest.main()
