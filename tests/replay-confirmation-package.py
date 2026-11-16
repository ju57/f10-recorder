#!/usr/bin/env python3
"""Build and verify static v10 confirmation metadata. Never author execution facts."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import sys
import stat

PROFILE = "maintained-v10-approval-clock-v1"
KIND = "issue83-two-confirmation-invariants-v1"
OPERATIONS = ("C00", "retained-H03-1m")
SLOTS = {"C00": "C00", "retained-H03-1m": "retained-1m"}
COMMON_ROLES = frozenset(("coverage-freeze", "source-manifest", "host-facts", "preservation-before", "retained-attempt", "resources", "rehearsal", "outside-storage-baseline", "outside-inventory", "outside-inventory-plan", "outside-forward-envelope", "outside-outstanding-writes", "fresh-candidate-qualification"))
STATIC_ROLES = COMMON_ROLES | {"run-instructions", "source-presentation-method"}
REQUIRED_C00_ROLES = COMMON_ROLES - {"coverage-freeze"}
REQUIRED_C00_ROLES = REQUIRED_C00_ROLES | {"run-instructions", "coverage-freeze"}
RETAINED_ROLES = frozenset(("C00-controller", "C00-confirmation", "C00-manifest", "C00-acceptance", "C00-selection", "C00-plan", "C00-oracle-data", "C00-source-evidence", "C00-reference-binding", "C00-source-clock"))
FUTURE_NAMES = frozenset(("human-authorization.json", "current-authorization.json", "approval-release.json", "worker-ready.json", "pre-ready.json", "attempt-failure.json", "stop.request", "next-handoff.json", "computer-use-ready.json", "foreground-handoff-release.json", "foreground-handoff-complete.json", "capture-arguments.json", "independent-acceptance.json", "coverage-freeze.json", "freeze-request.json", "plan.json", "preflight.json", "controller.json"))
MEDIA_SUFFIXES = frozenset((".ts", ".mp4", ".mkv", ".mov", ".avi", ".wav", ".mp3", ".pcm", ".f32", ".rgb", ".png", ".jpg", ".jpeg", ".webm", ".flac", ".aac"))
HASH = re.compile(r"^[0-9A-F]{64}$")
MANIFEST_LIMIT = 524288
TEMPLATE_LIMIT = 65536  # The release reader is stricter than owner -Template -Plan.
RUNTIME_LIMITS = {"qualification": 262144, "controller": 524288}
REPO = Path(__file__).resolve().parents[1]


class PackageError(ValueError):
    pass


def require(condition, message):
    if not condition:
        raise PackageError(message)


def encoded(value):
    return (json.dumps(value, ensure_ascii=True, indent=2, allow_nan=False) + "\n").encode("utf-8")


def digest(raw):
    return hashlib.sha256(raw).hexdigest().upper()


def no_duplicates(pairs):
    result = {}
    seen = set()
    for key, value in pairs:
        require(key.casefold() not in seen, "Duplicate JSON member: " + key)
        seen.add(key.casefold())
        result[key] = value
    return result


def decode(raw, limit, label):
    require(0 < len(raw) <= limit, f"{label} exceeds its {limit}-byte bound or is empty")
    try:
        return json.loads(raw.decode("utf-8-sig"), object_pairs_hook=no_duplicates,
                          parse_constant=lambda value: (_ for _ in ()).throw(PackageError("Invalid JSON number: " + value)))
    except (UnicodeError, json.JSONDecodeError) as error:
        raise PackageError(f"Invalid {label}: {error}") from error


def metadata_path(path):
    require(path.suffix.lower() not in MEDIA_SUFFIXES, "Media payload is outside static package scope: " + str(path))
    return path


def read(path, limit):
    metadata_path(path)
    with path.open("rb") as stream:
        raw = stream.read(limit + 1)
    require(len(raw) <= limit, "Bounded metadata read exceeded: " + str(path))
    return raw


def hash_file(path):
    metadata_path(path)
    h = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            h.update(block)
    return h.hexdigest().upper()


def absolute(value, base):
    require(isinstance(value, str) and value.strip(), "Nonempty input path required")
    path = Path(value)
    return Path(os.path.abspath(path if path.is_absolute() else base / path))


def target(root, relative):
    require(isinstance(relative, str) and relative and "\\" not in relative, "Package paths use relative forward slashes")
    path = Path(relative)
    require(not path.is_absolute() and all(part not in ("", ".", "..") for part in relative.split("/")), "Ordinary relative package path required")
    result = root / path
    require(result.is_relative_to(root), "Package path escapes its root")
    return result


def alias(path):
    try:
        info = path.lstat()
    except FileNotFoundError:
        return False
    return stat.S_ISLNK(info.st_mode) or bool(getattr(info, "st_file_attributes", 0) & 0x400)


def ordinary_ancestors(path):
    for current in (path, *path.parents):
        if current.exists() or current.is_symlink():
            require(not alias(current), "Ordinary package ancestors required: " + str(current))
            require(current.is_dir(), "Package ancestor conflicts with a file: " + str(current))


def output_root(path):
    path = Path(os.path.abspath(path))
    artifacts = REPO / "artifacts"
    require(path.is_relative_to(artifacts) and path != artifacts, "Package output must be a fresh child of this checkout's artifacts directory")
    ordinary_ancestors(path)
    return path


def resolve_values(value, root, base):
    if isinstance(value, dict):
        if "$packagePath" in value or "$inputPath" in value:
            require(len(value) == 1, "A typed path reference cannot contain other fields")
            if "$packagePath" in value:
                return str(target(root, value["$packagePath"]))
            return str(absolute(value["$inputPath"], base))
        return {key: resolve_values(item, root, base) for key, item in value.items()}
    if isinstance(value, list):
        return [resolve_values(item, root, base) for item in value]
    require(not isinstance(value, str) or not value.startswith(("${", "@package/", "@input/")), "Use typed path references, not string substitutions")
    return value


def fields(value, allowed, label):
    require(isinstance(value, dict) and set(value) <= set(allowed), "Unknown fields in " + label)


RETAINED_FIELDS = frozenset(("operation", "controllerKind", "prospectivePlanPath", "Resources", "createdUtc", "expiresUtc", "parentHandleBudget", "maximumSupportChildren", "maximumManagedRoles", "maximumOuterRoles", "creatorProofPolicy", "creatorProofMilliseconds", "controlCreatorEvidence", "entryPath", "stopFile", "watchStatusFile", "runtimeManifest", "controllerRuntimeManifest", "controllerRuntime", "rawChildByteLimit", "prospectiveSameCaptureRules", "FreezePath", "FreezeSha256", "OriginalSelectionPath", "OriginalSelectionSha256", "Inputs", "MappingReviewHash", "MappingReviewer", "SameCapture"))


def shape(template, operation, *, complete=False):
    retained = operation != "C00"
    key = "Resources" if retained else "plan"
    require(key in template and isinstance(template[key], dict), "Complete owner template with " + key + " required")
    p = template[key]
    required_resources = {"Root", "EngineRoot", "Environment", "Files", "Order", "RetainedRoots", "RetainedElapsedSeconds", "RetainedChildren"}
    if not complete:
        required_resources.remove("Root")  # The builder derives the fresh operation output.
    require(required_resources <= set(p), "Required CalibrationPlan constructor fields missing: " + ", ".join(sorted(required_resources - set(p))))
    require(isinstance(p["Environment"], str) and isinstance(p["RetainedRoots"], list)
            and all(isinstance(path, str) for path in p["RetainedRoots"]), "Invalid CalibrationPlan environment/retained roots")
    expected = {"FreshCandidateAdmission": "issue83-retained-same-C00-final-minute-v10" if retained else "issue83-C00-same-capture-reference-v10", "ReplayMinutes": 1 if retained else 5, "DeadlineSeconds": 900 if retained else 1800, "OfflineDeadlineSeconds": 600 if retained else 900, "MaximumChildren": 9 if retained else 16, "RetainedChildren": 0, "RetainedElapsedSeconds": 0, "RetainedOfflineSeconds": 0, "Order": ["H03"], "FloorBytes": 60000000000, "ReplayExporterRecipe": "replay-avc-aac-owned-completed-history-direct-avcc-with-reserved-staging-exact-fallback-v5"}
    for name, value in expected.items():
        require(p.get(name) == value, "Template changes maintained finite shape: " + name)
    require(template.get("operation") == operation and template.get("controllerKind") == "issue83-two-confirmation-owner-v2", "Exact maintained v10 owner required")
    require(not {name.casefold() for name in template} & {"fault", "lifecyclemode"}, "Injected production owner modes are forbidden")
    require(bool(template.get("prospectiveSameCaptureRules")), "Explicit prospective same-capture rules required")
    require(template.get("parentHandleBudget") == 49152 and template.get("maximumSupportChildren") == 1 and template.get("maximumOuterRoles") == 1 and template.get("creatorProofPolicy") == "qualification-child-creator-v1" and template.get("creatorProofMilliseconds") == 130000, "Maintained owner limits changed")
    require(template.get("maximumManagedRoles") == (9 if retained else 16) and template.get("controlCreatorEvidence") == "prefixture-held-instance-control-receipt-v1", "Maintained managed-role/creator contract changed")
    require(template.get("rawChildByteLimit") == 4 * ((900 if retained else 1800) * 2 + 2) * 4096, "Maintained raw observation reserve changed")
    if retained:
        require(set(template) <= RETAINED_FIELDS and (not complete or set(template) == RETAINED_FIELDS), "Exact retained outer schema required")
        require(isinstance(template.get("MappingReviewHash"), str) and HASH.fullmatch(template["MappingReviewHash"]) and isinstance(template.get("MappingReviewer"), str) and template["MappingReviewer"].strip(), "Caller-supplied mapping review hash and reviewer required")
        require(template.get("Inputs") == [] and p.get("SelectedRetainedFiles") == [] and not p.get("CompositeQualification"), "Retained template cannot invent future selected inputs")
    else:
        require(all(p.get(name) is True for name in ("CompositeQualification", "CompositeExplicitForegroundRelease", "CompositeManualForegroundHandoff")), "C00 requires explicit owned foreground release")
    return p


def prepare(recipe_path, root):
    """Return fully verified metadata bytes keyed by final path; write nothing."""
    recipe_path = Path(recipe_path).resolve()
    recipe_raw = read(recipe_path, 1048576)
    recipe = decode(recipe_raw, 1048576, "recipe")
    fields(recipe, ("kind", "profile", "revision", "templates", "bindings", "pins", "runtimeManifests", "owner", "controllerRuntime"), "recipe")
    require(recipe.get("kind") == "confirmation-package-recipe-v1" and recipe.get("profile") == PROFILE, "Unsupported profile; artifact v4/worker-ready amendments are not the maintained approval-clock reader")
    require(isinstance(recipe.get("revision"), str) and recipe["revision"], "Package revision required")
    require(set(recipe.get("templates", {})) == set(OPERATIONS) and set(recipe.get("bindings", {})) == set(OPERATIONS), "Exactly C00 and retained-H03-1m required")
    require(set(recipe.get("runtimeManifests", {})) == set(RUNTIME_LIMITS), "Both explicitly pinned runtime manifests required")
    root = output_root(root)
    require(not root.exists(), "Preserve existing package output")
    base = recipe_path.parent
    output = {}
    pins = {}
    template_pins = {}

    def pin(path, expected=None, *, owner_input=False):
        path = Path(path)
        actual = hash_file(path)
        require(expected is None or actual == expected, "Static input binding drift: " + str(path))
        require(str(path).casefold() not in {p.casefold() for p in pins} or pins[str(path)] == actual, "Conflicting static path")
        pins[str(path)] = actual
        if owner_input:
            template_pins[str(path)] = actual
        return actual

    def put(path, value, limit=1048576):
        require(path not in output and path.name.casefold() not in FUTURE_NAMES, "Output would create a future fact or overwrite metadata: " + str(path))
        raw = encoded(value)
        require(0 < len(raw) <= limit, "Generated metadata exceeds reader byte limit: " + str(path))
        output[path] = raw

    pin(recipe_path, digest(recipe_raw))
    require(isinstance(recipe.get("pins"), dict), "Explicit input path-to-SHA256 pins required")
    for name, expected in recipe["pins"].items():
        require(isinstance(expected, str) and HASH.fullmatch(expected), "Uppercase SHA256 required for static input")
        pin(absolute(name, base), expected, owner_input=True)
    owner = absolute(recipe.get("owner", str(REPO / "tests/replay-confirmation-owner.ps1")), base)
    require(owner == REPO / "tests/replay-confirmation-owner.ps1", "Only this checkout's maintained owner profile is supported")
    pin(owner, owner_input=True)
    for dependency in sorted((owner.parent / "confirmation-controller").iterdir()):
        if dependency.is_file():
            pin(dependency, owner_input=True)
    controller = absolute(recipe.get("controllerRuntime"), base)
    require(str(controller) in pins, "Controller runtime must be explicitly pinned")
    runtime_paths = {}
    for role, limit in RUNTIME_LIMITS.items():
        source = absolute(recipe["runtimeManifests"][role], base)
        raw = read(source, limit)
        pin(source, digest(raw))
        manifest = decode(raw, limit, role + " runtime manifest")
        require(isinstance(manifest, dict) and 1 <= len(manifest) <= 1024, "Runtime closure requires 1..1024 members")
        for name, expected in manifest.items():
            require(Path(name).is_absolute() and isinstance(expected, str) and HASH.fullmatch(expected), "Runtime closure needs absolute paths and uppercase digests")
            pin(Path(name), expected)
        runtime_paths[role] = root / "runtime" / (role + "-runtime.json")
        put(runtime_paths[role], manifest, limit)

    templates = {}
    roles = {}
    slots = {}
    for operation in OPERATIONS:
        source = absolute(recipe["templates"][operation], base)
        raw = read(source, TEMPLATE_LIMIT)
        pin(source, digest(raw))
        template = resolve_values(decode(raw, TEMPLATE_LIMIT, "prospective template"), root, base)
        p = shape(template, operation)
        require(not p.get("Files") and not p.get("EvidenceRoles"), "Files and EvidenceRoles must come from the one recipe binding map")
        slot = SLOTS[operation]
        final = root / "admissions" / slot / "plan.json"
        template.update(prospectivePlanPath=str(final), createdUtc="conditional-T0", expiresUtc="conditional-expiry", entryPath=str(owner), stopFile=str(final.parent / "STOP.REQUEST"), watchStatusFile=str(final.parent / "NEXT-HANDOFF.json"), runtimeManifest=str(runtime_paths["qualification"]), controllerRuntimeManifest=str(runtime_paths["controller"]), controllerRuntime=str(controller))
        if operation == "C00":
            template["originalPurposeDeadlineUtc"] = "conditional-purpose"
        else:
            require("originalPurposeDeadlineUtc" not in template, "Retained template has no C00 purpose clock")
        p["Root"] = str(root / "live" / slot)
        declared = recipe["bindings"][operation]
        require(isinstance(declared, list), "Operation bindings must be a list")
        current = {}
        late = []
        for binding in declared:
            fields(binding, ("role", "path", "source"), "operation binding")
            role = binding.get("role")
            require(role in STATIC_ROLES | (RETAINED_ROLES if operation != "C00" else frozenset()), "Role is not allowed by maintained profile: " + str(role))
            require(role not in current, "Duplicate operation role: " + role)
            path = target(root, binding.get("path"))
            require(str(path).casefold() not in {str(value).casefold() for value in current.values()}, "Duplicate role path")
            current[role] = path
            if "source" in binding:
                require(path.name.casefold() not in FUTURE_NAMES and not path.is_relative_to(root / "live"), "Future evidence cannot be copied as static metadata")
                source = absolute(binding["source"], base)
                raw = read(source, 1048576)
                pin(source, digest(raw))
                if role == "run-instructions":
                    require(path.suffix.lower() == ".md", "Static run instructions must be Markdown")
                    value_raw = raw
                else:
                    value_raw = encoded(resolve_values(decode(raw, 1048576, "static role metadata"), root, base))
                if path in output:
                    require(output[path] == value_raw, "Shared role bytes differ")
                else:
                    require(path.name.casefold() not in FUTURE_NAMES, "Future fact cannot be a static role")
                    output[path] = value_raw
            else:
                require(role in COMMON_ROLES | (RETAINED_ROLES if operation != "C00" else frozenset()), "Role cannot be late-bound: " + role)
                require(not path.exists(), "Future evidence already exists")
                late.append({"role": role, "path": str(path)})
        require(len(late) <= 24, "Conditional binding ceiling is 24")
        roles[operation] = current
        slots[operation] = {"path": str(final), "template": str(root / "templates" / (slot + "-template.json")), "lateBindings": late}
        templates[operation] = template

    retained = templates["retained-H03-1m"]
    retained_roles = roles["retained-H03-1m"]
    required = {"FreezePath": "coverage-freeze", "OriginalSelectionPath": "C00-selection"}
    for field, role in required.items():
        require(role in retained_roles, "Required retained dependency role missing: " + role)
        retained[field] = str(retained_roles[role])
    retained["FreezeSha256"] = "conditional-derived"
    retained["OriginalSelectionSha256"] = "conditional-derived"
    same_capture_roles = {"ConfirmationPath": "C00-confirmation", "ManifestPath": "C00-manifest", "AcceptancePath": "C00-acceptance", "ControllerPath": "C00-controller", "C00PlanPath": "C00-plan"}
    require(all(role in retained_roles for role in same_capture_roles.values()), "Complete same-capture dependency roles required")
    retained["SameCapture"] = {field: str(retained_roles[role]) for field, role in same_capture_roles.items()}
    c00 = roles["C00"]
    require(REQUIRED_C00_ROLES <= set(c00), "Missing mandatory C00 admission role: " + ",".join(sorted(REQUIRED_C00_ROLES - set(c00))))
    envelope_roles = ("outside-inventory-plan", "outside-outstanding-writes", "outside-forward-envelope")
    require(all(name in c00 and c00[name] in output for name in envelope_roles), "Static outside plan, forecast and forward envelope are required")
    envelope_path = c00["outside-forward-envelope"]
    envelope = decode(output[envelope_path], 1048576, "outside forward envelope")
    require(envelope.get("kind") == "owned-history-outside-forward-v1", "Exact outside forward envelope kind required")
    envelope["baselinePlanSha256"] = digest(output[c00["outside-inventory-plan"]])
    envelope["forecastSha256"] = digest(output[c00["outside-outstanding-writes"]])
    output[envelope_path] = encoded(envelope)
    all_static = dict(template_pins)
    all_static.update({str(path): digest(raw) for path, raw in output.items()})
    manifest_path = root / "invariants.json"
    for operation, template in templates.items():
        p = template["plan" if operation == "C00" else "Resources"]
        p["Files"] = dict(sorted(all_static.items()))
        p["Files"][str(manifest_path)] = "conditional-approved-manifest"
        for binding in slots[operation]["lateBindings"]:
            require(binding["path"] not in all_static, "Future role conflicts with a static file")
            p["Files"][binding["path"]] = "conditional-derived"
        p["EvidenceRoles"] = {role: str(path) for role, path in roles[operation].items()}
        put(Path(slots[operation]["template"]), template, TEMPLATE_LIMIT)
    manifest = {"kind": KIND, "revision": recipe["revision"], "packageProfile": PROFILE, "status": "HOLD-static-only-no-execution-authority", "files": dict(sorted({**pins, **{str(path): digest(raw) for path, raw in output.items()}}.items())), "operations": slots, "outsideEnvelope": {name: str(c00[name]) for name in envelope_roles}}
    put(manifest_path, manifest, MANIFEST_LIMIT)
    verify(manifest_path, digest(output[manifest_path]), generated=output)
    return output


def verify(manifest_path, expected_sha256, *, generated=None):
    manifest_path = Path(manifest_path)
    root = output_root(manifest_path.parent)
    require(manifest_path == root / "invariants.json", "Exact package manifest path required")
    generated = generated or {}

    def raw(path, limit):
        path = Path(path)
        value = generated[path] if path in generated else read(path, limit)
        require(0 < len(value) <= limit, "Reader byte limit exceeded: " + str(path))
        return value

    def hashed(path):
        path = Path(path)
        return digest(generated[path]) if path in generated else hash_file(path)

    content = raw(manifest_path, MANIFEST_LIMIT)
    require(isinstance(expected_sha256, str) and HASH.fullmatch(expected_sha256) and digest(content) == expected_sha256, "Exact manifest SHA256 mismatch")
    manifest = decode(content, MANIFEST_LIMIT, "invariant manifest")
    require(manifest.get("kind") == KIND and manifest.get("packageProfile") == PROFILE, "Unsupported invariant profile")
    require(set(manifest.get("operations", {})) == set(OPERATIONS), "Exactly two fixed operations required")
    files = manifest.get("files")
    require(isinstance(files, dict) and files, "Static invariant bindings required")
    require(len({str(Path(path)).casefold() for path in files}) == len(files), "Case-insensitive static path collision")
    for path, expected in files.items():
        require(Path(path).is_absolute() and HASH.fullmatch(expected or ""), "Static bindings need absolute paths and uppercase SHA256")
        require(Path(path).name.casefold() not in FUTURE_NAMES, "Static seal includes an operational fact")
        require(hashed(path) == expected, "Static invariant changed: " + path)
    for operation, slot in manifest["operations"].items():
        require(slot.get("path") == str(root / "admissions" / SLOTS[operation] / "plan.json") and slot.get("template") == str(root / "templates" / (SLOTS[operation] + "-template.json")), "Operation paths differ from fixed package layout")
        require(slot["template"] in files, "Unpinned operation template")
        template = decode(raw(slot["template"], TEMPLATE_LIMIT), TEMPLATE_LIMIT, "prospective template")
        p = shape(template, operation, complete=True)
        require(template.get("entryPath") == str(REPO / "tests/replay-confirmation-owner.ps1"), "Maintained owner entry path changed")
        require(template.get("stopFile") == str(Path(slot["path"]).parent / "STOP.REQUEST") and template.get("watchStatusFile") == str(Path(slot["path"]).parent / "NEXT-HANDOFF.json"), "Owner stop/watch paths changed")
        require(template.get("prospectivePlanPath") == slot["path"] and p.get("Root") == str(root / "live" / SLOTS[operation]), "Prospective operation paths changed")
        require(template.get("createdUtc") == "conditional-T0" and template.get("expiresUtc") == "conditional-expiry" and (operation != "C00" or template.get("originalPurposeDeadlineUtc") == "conditional-purpose"), "Static package cannot invent execution times")
        require(p["Files"].get(str(manifest_path)) == "conditional-approved-manifest", "Exactly bound self-manifest sentinel required")
        late = slot.get("lateBindings")
        require(isinstance(late, list) and len(late) <= 24, "Conditional binding ceiling is 24")
        require(len({binding["role"] for binding in late}) == len(late) and len({binding["path"].casefold() for binding in late}) == len(late), "Duplicate late binding")
        late_paths = set()
        for binding in late:
            role, path = binding["role"], binding["path"]
            require(role in COMMON_ROLES | (RETAINED_ROLES if operation != "C00" else frozenset()), "Unsupported late role")
            require(Path(path).is_relative_to(root), "Future path escapes package")
            require(p["Files"].get(path) == "conditional-derived" and p["EvidenceRoles"].get(role) == path, "Unbound named future role")
            require(not Path(path).exists(), "Future execution fact already exists: " + path)
            late_paths.add(path)
        for path, value in p["Files"].items():
            if path == str(manifest_path):
                require(value == "conditional-approved-manifest", "Invalid manifest sentinel")
            elif path in late_paths:
                require(value == "conditional-derived", "Invalid future sentinel")
            else:
                require(HASH.fullmatch(value or "") and files.get(path) == value and hashed(path) == value, "Unpinned static template member: " + path)
        require(len({path.casefold() for path in p["EvidenceRoles"].values()}) == len(p["EvidenceRoles"]), "Aliased evidence roles")
        if operation == "C00":
            require(REQUIRED_C00_ROLES <= set(p["EvidenceRoles"]), "Missing mandatory C00 admission role")
        for role, path in p["EvidenceRoles"].items():
            require(role in STATIC_ROLES | (RETAINED_ROLES if operation != "C00" else frozenset()) and path in p["Files"], "Unbound or unknown evidence role: " + role)
        require(template.get("controllerRuntime") in p["Files"], "Unbound controller runtime")
        for required in (str(REPO / "tests/replay-confirmation-owner.ps1"), *[str(path) for path in (REPO / "tests/confirmation-controller").iterdir() if path.is_file()]):
            require(required in p["Files"], "Unbound owner dependency: " + required)
        images = {}
        for image in ("dotnet.exe", "F10Recorder.WorkerChecks.dll", "ffmpeg.exe"):
            selected = [path for path in p["Files"] if Path(path).name == image]
            require(len(selected) == 1, "Exact owner executable role required: " + image)
            images[image] = selected[0]
        require(str(Path(p.get("EngineRoot", "")) / "ffmpeg-9.0.2/bin/ffmpeg.exe") == images["ffmpeg.exe"], "Managed engine root differs from pinned engine")
        if os.name == "nt":
            console = str(Path(os.environ["SystemRoot"]) / "System32/conhost.exe")
            require(console.casefold() in {path.casefold() for path in p["Files"]}, "Actual console image is unbound")
        if operation != "C00":
            expected_roles = {"FreezePath": "coverage-freeze", "OriginalSelectionPath": "C00-selection"}
            for field, role in expected_roles.items():
                require(template.get(field) == p["EvidenceRoles"].get(role), "Retained dependency path differs from named role")
            require(template.get("FreezeSha256") == "conditional-derived" and template.get("OriginalSelectionSha256") == "conditional-derived", "Future retained digest must remain conditional")
            for field, role in {"ConfirmationPath": "C00-confirmation", "ManifestPath": "C00-manifest", "AcceptancePath": "C00-acceptance", "ControllerPath": "C00-controller", "C00PlanPath": "C00-plan"}.items():
                require(template.get("SameCapture", {}).get(field) == p["EvidenceRoles"].get(role), "Same-capture dependency differs from named role")
        for name, limit in RUNTIME_LIMITS.items():
            runtime_path = template["runtimeManifest" if name == "qualification" else "controllerRuntimeManifest"]
            require(runtime_path in files, "Unpinned runtime manifest")
            members = decode(raw(runtime_path, limit), limit, "runtime closure")
            require(isinstance(members, dict) and 1 <= len(members) <= 1024, "Runtime member count")
            for path, expected in members.items():
                require(files.get(path) == expected and hashed(path) == expected, "Runtime member drift")
    envelope_map = manifest["outsideEnvelope"]
    require(set(envelope_map) == {"outside-inventory-plan", "outside-outstanding-writes", "outside-forward-envelope"}, "Exact forward-envelope role map required")
    require(all(path in files for path in envelope_map.values()), "Unpinned envelope dependency")
    envelope = decode(raw(envelope_map["outside-forward-envelope"], 1048576), 1048576, "forward envelope")
    require(envelope.get("kind") == "owned-history-outside-forward-v1" and envelope.get("baselinePlanSha256") == hashed(envelope_map["outside-inventory-plan"]) and envelope.get("forecastSha256") == hashed(envelope_map["outside-outstanding-writes"]), "Stale forward-envelope dependency digest")
    for directory in (root / "live", root / "admissions/C00", root / "admissions/retained-1m"):
        if not generated:
            require(directory.is_dir(), "Missing ordinary preparation directory: " + str(directory))
        ordinary_ancestors(directory)
    if root.exists():
        for path in root.rglob("*"):
            require(not alias(path), "Package namespace contains an alias")
            require(path.name.casefold() not in FUTURE_NAMES and path not in (root / "live/C00", root / "live/retained-1m"), "Existing execution state prevents static verification: " + str(path))
    return {"kind": "static-confirmation-package-verification-v1", "manifestSha256": expected_sha256, "profile": PROFILE, "pinnedFiles": len(files), "operations": list(OPERATIONS), "executionAuthority": False, "readinessCreated": False}


def build(recipe_path, root):
    output = prepare(recipe_path, root)
    root = output_root(root)
    root.mkdir(parents=True)  # All bytes and constraints were checked before publishing anything.
    for directory in (root / "live", root / "admissions/C00", root / "admissions/retained-1m"):
        directory.mkdir(parents=True, exist_ok=True)
    manifest = root / "invariants.json"
    for path, content in sorted(output.items(), key=lambda item: item[0] == manifest):
        path.parent.mkdir(parents=True, exist_ok=True)
        with path.open("xb") as stream:
            stream.write(content)
    return verify(manifest, digest(output[manifest]))


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("plan", help="Describe effects without reading recipe inputs or creating files")
    create = sub.add_parser("build")
    create.add_argument("--recipe", required=True)
    create.add_argument("--output", required=True)
    check = sub.add_parser("verify")
    check.add_argument("--manifest", required=True)
    check.add_argument("--expected-sha256", required=True)
    args = parser.parse_args(argv)
    if args.command == "plan":
        print("Static metadata only: explicit nonmedia recipe/template/runtime/pin reads; fresh checkout artifacts output; two prospective templates, static invariant manifest, ordinary metadata directories/live parent. No child process, native/device/UI/audio/media, human release, Ready clock, actual admission or acceptance. Failed validation publishes no package.")
        return 0
    try:
        result = build(args.recipe, args.output) if args.command == "build" else verify(args.manifest, args.expected_sha256)
        print(json.dumps(result, indent=2))
        return 0
    except (PackageError, OSError, KeyError, TypeError) as error:
        print("Static package rejected: " + str(error), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
