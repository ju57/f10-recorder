"""Admission and sampled-health checks for expensive game measurements. No capture."""
import argparse
import csv
import hashlib
import json
import math
from pathlib import Path
import shutil

COLLECTION_ROLES = {"desktop_apphost", "desktop_assembly", "windows_assembly", "core_assembly",
                    "capture_engine", "presentmon", "profile"}


def file_sha256(path):
    checksum = hashlib.sha256()
    with Path(path).open("rb") as file:
        for block in iter(lambda: file.read(1024 * 1024), b""):
            checksum.update(block)
    return checksum.hexdigest()


def read_json(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def write_new(path, value):
    with Path(path).open("x", encoding="utf-8") as file:
        json.dump(value, file, indent=2, allow_nan=False)


def digest(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, allow_nan=False).encode()).hexdigest()


def require(condition, message):
    if not condition:
        raise ValueError(message)


def finite(value, name, minimum=0):
    require(type(value) in (int, float) and math.isfinite(value) and value >= minimum,
            f"Invalid {name}")
    return value


def load_plan(path):
    plan = read_json(path)
    require(plan["version"] == 1, "Unsupported experiment plan version")
    for field in ("experiment_id", "source_qualification", "environment"):
        require(isinstance(plan[field], str) and plan[field].strip(), f"Missing {field}")
    require(isinstance(plan["acceptance_criteria"], dict) and plan["acceptance_criteria"], "Acceptance criteria required")
    start, end = plan["window_seconds"]
    interval = finite(plan["sampling_interval_seconds"], "sampling interval", .01)
    gap = finite(plan["maximum_sample_gap_seconds"], "maximum sample gap", interval)
    finite(start, "window start", interval)
    finite(end, "window end", start + interval)
    duration = finite(plan["trial_seconds"], "trial duration", end + gap)
    require(duration <= 3600 and float(duration).is_integer(), "Trial duration must be whole seconds <=3600")
    storage = plan["storage"]
    for field in ("expected_bytes", "budget_bytes", "trial_reserve_bytes",
                  "finalization_reserve_bytes", "poll_growth_reserve_bytes", "minimum_free_bytes"):
        finite(storage[field], field)
        require(type(storage[field]) is int, f"{field} must be integer bytes")
    require(0 < storage["expected_bytes"] <= storage["budget_bytes"], "Expected storage exceeds budget")
    require(storage["trial_reserve_bytes"] > 0 and storage["finalization_reserve_bytes"] > 0
            and storage["poll_growth_reserve_bytes"] > 0, "Storage reserves must be positive")
    require(sum(storage[k] for k in ("trial_reserve_bytes", "finalization_reserve_bytes", "poll_growth_reserve_bytes"))
            <= storage["budget_bytes"], "Trial and stop reserves exceed budget")
    require(isinstance(plan["identity_files"], dict) and plan["identity_files"], "Build/configuration identity files required")
    for name, sha in plan["identity_files"].items():
        require(name and len(sha) == 64 and all(c in "0123456789abcdef" for c in sha), "Invalid identity hash")
    inputs = plan["collection_inputs"]
    require(isinstance(inputs, dict) and set(inputs) == COLLECTION_ROLES, "Required collection input roles are missing")
    require(len(set(inputs.values())) == len(COLLECTION_ROLES), "Collection input roles require distinct files")
    require(all(name in plan["identity_files"] for name in inputs.values()), "Collection input is missing its identity hash")
    trials = plan["trials"]
    require(isinstance(trials, list) and trials, "Frozen trial sequence required")
    labels = []
    for trial in [plan["qualification"]] + trials:
        label = trial["label"]
        require(label and all(c.isascii() and (c.isalnum() or c in "-_") for c in label), "Invalid trial label")
        require(trial["mode"] in ("On", "Off"), "Invalid trial mode")
        labels.append(label)
    require(len(set(labels)) == len(labels), "Trial labels must be unique")
    require(plan["qualification"]["mode"] == "On", "Qualification must exercise recording")
    require(plan["retention"], "Retention records required")
    for item in plan["retention"]:
        p = Path(item["path"])
        require(not p.is_absolute() and ".." not in p.parts, "Retention path must stay in experiment root")
        require(item["class"] in ("report", "recording", "build", "worktree"), "Unknown retention class")
        require(item["needed_for"].strip(), "Retention purpose required")
    return plan


def verify_identity(plan, repository):
    for name, expected in plan["identity_files"].items():
        path = (repository / name).resolve()
        require(path.is_relative_to(repository) or name == plan["collection_inputs"]["capture_engine"],
                "Only the managed capture engine may be outside the repository")
        require(path.is_file() and file_sha256(path) == expected,
                f"Build/configuration identity changed: {name}")


def storage_usage(root):
    total = 0
    for path in root.rglob("*"):
        require(not path.is_symlink() and not (hasattr(path, "is_junction") and path.is_junction()),
                "Linked experiment storage cannot be accounted safely")
        if path.is_file():
            total += path.stat().st_size
    return total


def check_storage(plan, root, admission=False, free_bytes=None):
    storage = plan["storage"]
    used = storage_usage(root)
    reserve = storage["finalization_reserve_bytes"] + storage["poll_growth_reserve_bytes"]
    if admission:
        reserve += storage["trial_reserve_bytes"]
    free = shutil.disk_usage(root).free if free_bytes is None else free_bytes
    require(used + reserve <= storage["budget_bytes"], "Experiment storage budget would be exceeded")
    require(free >= reserve + storage["minimum_free_bytes"], "Experiment free-space reserve would be violated")
    return {"used_bytes": used, "reserved_bytes": reserve, "free_bytes": free}


def validate_samples(plan, samples, mode):
    require(mode in ("On", "Off") and samples, "Missing worker sample coverage")
    times = [finite(s["ElapsedSeconds"], "sample timestamp") for s in samples]
    require(all(b > a for a, b in zip(times, times[1:])), "Sample timestamps are not strictly increasing")
    start, end = plan["window_seconds"]
    before = [i for i, t in enumerate(times) if t <= start]
    after = [i for i, t in enumerate(times) if t >= end]
    require(before and after, "Samples do not bracket the measurement window")
    lo, hi = before[-1], after[0]
    selected = samples[lo:hi + 1]
    require(all(b - a <= plan["maximum_sample_gap_seconds"] for a, b in zip(times[lo:hi], times[lo+1:hi+1])),
            "Excessive worker sample gap")
    states = [s["Status"] for s in selected]
    metrics = [s["Metrics"] for s in states]
    workers = {s["WorkerProcessId"] for s in states}
    require(len(workers) == 1 and next(iter(workers)) > 0, "Worker replaced during measurement")
    require(all(s["ApplicationName"] == "cs2" for s in states), "Game source changed")
    require(len({m["Recoveries"] for m in metrics}) == 1, "Recovery during measurement")
    if mode == "On":
        require(all(s["Ready"] is True and s["Paused"] is False for s in states), "Recording not ready throughout guard")
        pids = {m["CaptureProcessId"] for m in metrics}
        require(len(pids) == 1 and next(iter(pids)) > 0, "Capture process replaced during measurement")
        progress = [finite(m["CaptureProgressSeconds"], "capture progress") for m in metrics]
        require(all(b >= a for a, b in zip(progress, progress[1:])) and progress[-1] > progress[0],
                "Capture progress reset or stalled")
    else:
        require(all(s["Paused"] is True and m["CaptureProcessId"] == 0 for s, m in zip(states, metrics)),
                "Recorder-off control was recording")
    return {"result": "valid", "window_seconds": [start, end], "samples": len(selected),
            "coverage": "Sampled worker health; no proof of uninterrupted health between samples or source-picture preservation."}


def validate_presentation_window(plan, path):
    with path.open(encoding="utf-8-sig", newline="") as file:
        times = [finite(float(row["TimeInMs"]), "presentation timestamp") / 1000 for row in csv.DictReader(file)]
    start, end = plan["window_seconds"]
    require(times and min(times) <= start and max(times) >= end
            and any(start <= t < end for t in times), "Incomplete presentation window")


def context(plan_path, root, verify_build=True):
    repository = Path(__file__).resolve().parent.parent
    requested_root = Path(root).absolute()
    root = requested_root.resolve()
    require(requested_root == root, "Linked evidence root is unsupported")
    require(root.is_relative_to(repository / "artifacts") and root != repository / "artifacts",
            "Use an isolated experiment directory beneath artifacts")
    root.mkdir(parents=True, exist_ok=True)
    # Reject linked roots as well as linked descendants before writing state.
    current = root
    while current != repository:
        require(not current.is_symlink() and not (hasattr(current, "is_junction") and current.is_junction()),
                "Linked evidence root is unsupported")
        current = current.parent
    plan = load_plan(plan_path)
    if verify_build:
        verify_identity(plan, repository)
    state = root / "experiment-state"
    state.mkdir(exist_ok=True)
    return plan, root, state


def trial_input(plan, label, mode, seconds, qualification):
    candidates = [plan["qualification"]] if qualification else plan["trials"]
    require(any(t == {"label": label, "mode": mode} for t in candidates), "Trial is not in the declared plan")
    require(seconds == plan["trial_seconds"], "Trial duration differs from plan")


def admit(plan_path, root, label, mode, seconds, qualification=False):
    plan, root, state = context(plan_path, root)
    trial_input(plan, label, mode, seconds, qualification)
    require(not (state / "closed.json").exists(), "Experiment is closed")
    require(not (state / "blocked.json").exists(), "Batch stopped on invalidity; retain it and use a new experiment")
    require(not list(state.glob("*.pending.json")), "An earlier trial is incomplete; collection is blocked")
    require(not (state / (label + ".result.json")).exists(), "Trial labels cannot be reused")
    if qualification:
        require(not list(state.glob("*.result.json")), "Qualification already attempted; use a fresh experiment")
    else:
        qualified = read_json(state / (plan["qualification"]["label"] + ".result.json"))
        require(qualified["result"] == "valid", "Successful qualification required")
        prior = qualified["plan"]
        for field in ("identity_files", "collection_inputs", "environment", "source_qualification", "sampling_interval_seconds"):
            require(plan[field] == prior[field], f"Qualification does not match current {field}")
        trace_path = root / qualified["trace"]
        require(file_sha256(trace_path) == qualified["trace_sha256"], "Qualification trace changed")
        trace = read_json(trace_path)
        presentation = Path(trace["PresentMonCSV"]).resolve()
        require(presentation.is_relative_to(root), "Qualification presentation trace outside experiment")
        require(file_sha256(presentation) == qualified["presentation_sha256"], "Qualification presentation trace changed")
        validate_samples(plan, trace["Samples"], "On")
        validate_presentation_window(plan, presentation)
        frozen = state / "batch-plan.json"
        if frozen.exists():
            require(digest(read_json(frozen)) == digest(plan), "Frozen batch plan changed")
        for trial in plan["trials"]:
            if trial["label"] == label:
                break
            result = read_json(state / (trial["label"] + ".result.json"))
            require(result["result"] == "valid", "Previous trial was invalid; batch must stop")
        else:
            raise ValueError("Trial missing from frozen sequence")
    usage = check_storage(plan, root, admission=True)
    if not qualification and not (state / "batch-plan.json").exists():
        write_new(state / "batch-plan.json", plan)
    write_new(state / (label + ".pending.json"), {"plan": plan, "mode": mode, "qualification": qualification})
    return {"plan_sha256": digest(plan), "storage": usage}


def finish(plan_path, root, label, trace=None, error=None):
    plan, root, state = context(plan_path, root)
    pending_path = state / (label + ".pending.json")
    pending = read_json(pending_path)
    result = {"result": "invalid", "plan": pending["plan"], "label": label}
    try:
        require(digest(plan) == digest(pending["plan"]), "Plan changed during trial")
        require(not error, error or "Trial failed")
        path = Path(trace).resolve()
        require(path.is_relative_to(root), "Trace outside experiment root")
        data = read_json(path)
        require(data["TraceExitCode"] == 0, "Presentation trace failed")
        health = validate_samples(plan, data["Samples"], pending["mode"])
        presentation = Path(data["PresentMonCSV"]).resolve()
        require(presentation.is_relative_to(root), "Presentation trace outside experiment root")
        validate_presentation_window(plan, presentation)
        check_storage(plan, root)
        result["trace"] = path.relative_to(root).as_posix()
        result["trace_sha256"] = file_sha256(path)
        result["presentation_sha256"] = file_sha256(presentation)
        result.update(health)
    except (ValueError, KeyError, TypeError, OSError) as failure:
        result["error"] = str(failure)
    write_new(state / (label + ".result.json"), result)
    if result["result"] != "valid" and not (state / "blocked.json").exists():
        write_new(state / "blocked.json", {"label": label, "error": result["error"]})
    pending_path.unlink()
    require(result["result"] == "valid", result.get("error", "Invalid trial"))
    return result


def close(plan_path, root):
    plan, root, state = context(plan_path, root, verify_build=False)
    if (state / "batch-plan.json").exists():
        require(digest(plan) == digest(read_json(state / "batch-plan.json")), "Closure must use the frozen plan")
    used = storage_usage(root)
    records = []
    for item in plan["retention"]:
        path = (root / item["path"]).resolve()
        require(path.is_relative_to(root), "Retention path escapes experiment")
        records.append({**item, "availability": "present" if path.exists() else "missing"})
    files = []
    for path in root.rglob("*"):
        if path.is_file():
            files.append({"path": path.relative_to(root).as_posix(), "bytes": path.stat().st_size,
                          "sha256": file_sha256(path)})
    result = {"plan": plan, "retention": records, "files": files, "used_bytes": used,
              "pending_trials": [p.name for p in state.glob("*.pending.json")],
              "scope": "Inventory only. No automatic deletion; retirement changes availability and must retain a receipt."}
    write_new(state / "closed.json", result)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=["validate-plan", "admit", "finish", "close"])
    parser.add_argument("plan")
    parser.add_argument("--root")
    parser.add_argument("--label")
    parser.add_argument("--mode", choices=["On", "Off"])
    parser.add_argument("--seconds", type=int)
    parser.add_argument("--qualification", action="store_true")
    parser.add_argument("--trace")
    parser.add_argument("--error")
    args = parser.parse_args()
    try:
        if args.command == "validate-plan":
            result = load_plan(args.plan)
        elif args.command == "admit":
            result = admit(args.plan, args.root, args.label, args.mode, args.seconds, args.qualification)
        elif args.command == "finish":
            result = finish(args.plan, args.root, args.label, args.trace, args.error)
        else:
            result = close(args.plan, args.root)
        print(json.dumps(result, allow_nan=False))
    except (ValueError, KeyError, TypeError, OSError) as error:
        parser.exit(1, f"Experiment rejected: {error}\n")


if __name__ == "__main__":
    main()
