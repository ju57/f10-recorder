"""R1 pilot preparation only. No engine, filesystem or process execution adapter."""
import hashlib
import json
import argparse


def digest(value):
    """Hash JSON values only; never read a file or executable."""
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(",", ":"),
                                     allow_nan=False).encode("utf-8")).hexdigest()


def preview(root="artifacts/r1-pilot-pending"):
    """Describe preparation without inspecting even the supplied root."""
    require(type(root) is str and root.strip() and not any(ord(c) < 32 for c in root)
            and ".." not in root.replace("\\", "/").split("/"), "Invalid lexical root")
    prefix = root.rstrip("/\\").replace("\\", "/")
    filters = ["drawbox=x=0:y=0:w=320:h=32:color=black:t=fill"]
    for bit in range(20):
        for row in range(2):
            enable = f"mod(floor(n/{1 << bit}),2)"
            if row:
                enable = f"not({enable})"
            filters.append(f"drawbox=x={bit * 16}:y={row * 16}:w=16:h=16:"
                           f"color=white:t=fill:enable='{enable}'")
    # The old prototype supplies the recipe, never its executable/bootstrap route.
    common = ["-hide_banner", "-loglevel", "warning", "-nostdin", "-n"]
    source = common + ["-f", "lavfi", "-i", "testsrc2=size=1920x1080:rate=60:duration=304",
        "-f", "lavfi", "-i", r"aevalsrc=if(lt(mod(t\,1)\,0.01)\,0.7*sin(2*PI*1000*t)\,0):s=48000:d=304",
        "-vf", ",".join(filters), "-map", "0:v:0", "-map", "1:a:0",
        "-c:v", "h264_nvenc", "-preset", "p3", "-tune", "ll", "-rc", "cbr",
        "-b:v", "20M", "-maxrate", "20M", "-bufsize", "40M", "-g", "60", "-bf", "0",
        "-forced-idr", "1", "-flags", "+cgop", "-pix_fmt", "yuv420p",
        "-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-ac", "2",
        "-force_key_frames", "expr:gte(t,n_forced*1)", "-f", "segment", "-segment_time", "1",
        "-segment_format", "mpegts", "-reset_timestamps", "1", "-segment_list",
        prefix + "/source/segments.csv", "-segment_list_type", "csv", prefix + "/source/chunk-%06d.ts"]
    export = common + ["-ss", "<qualified-relative-start>", "-f", "concat", "-safe", "0",
        "-i", prefix + "/snapshot/source.ffconcat", "-t", "300", "-map", "0:v:0", "-map", "0:a:0",
        "-c", "copy", "-movflags", "+faststart", "-f", "mp4", prefix + "/baseline.partial.mp4"]
    plan = {"kind": "r1-pilot-preparation", "version": 1, "root": root,
        "base": "a4d9a4119fbf90c0e1971a9f0e1c627e215c9839",
        "planning_v2_sha256": "35FE9FBC16B84BB8ADA76C1E9B124D803367A80191EA58A7E24738EB27E6347C",
        "execution_enabled": False, "actual_commands_launched": [],
        "current_effects": {"filesystem_inspection": False, "filesystem_writes": False,
                            "child_process": False, "engine_probe": False, "gpu_nvenc": False,
                            "capture": False, "ui": False, "settings_startup_vendor": False},
        "future_effects": {"gpu_nvenc": True, "generated_audio": True, "capture": False,
                           "ui": False, "settings_startup_vendor": False},
        "limits": {"hard_bytes": 4_500_000_000, "used_stop_bytes": 3_350_000_000,
                   "next_trial_bytes": 2_000_000_000, "closure_bytes": 128_000_000,
                   "poll_bytes": 1_000_000_000, "minimum_free_bytes": 40_000_000_000,
                   "max_children": 12, "cumulative_seconds": 1800, "maximum_gap_ms": 2000,
                   "maximum_short_controls": 2, "maximum_short_seconds": 2},
        "commands": [{"stage": "source-304s", "tool": "<verified-engine-pending>",
                      "arguments": source, "unresolved": ["current engine/host/NVENC qualification"]},
                     {"stage": "baseline-300s", "tool": "<verified-engine-pending>",
                      "arguments": export, "unresolved": ["qualified CSV/IDR selection and relative offset"]}],
        "selection": {"requested_start_seconds": 2, "requested_end_seconds": 302,
                      "qualified_manifest": "pending", "filename_is_clock": False},
        "execution_blockers": ["separate execution authorization", "current engine/host/NVENC qualification",
            "whole-child verified engine lease adapter", "qualified source/CSV/IDR/offset and stream schema",
            "fresh owned-root/alias/volume/caller-free observation adapter",
            "snapshot/measurement/observer/owned-child cleanup adapters"],
        "claims": {"source_system_audio_reference": False, "storage_margin": "unmeasured",
                   "physical_peak": "unmeasured", "release_gates_closed": False}}
    plan["plan_sha256"] = digest(plan)
    return plan


def require(condition, message):
    if not condition:
        raise ValueError(message)


def integer(value, name):
    require(type(value) is int and 0 <= value <= 2**63 - 1, "Invalid integer: " + name)
    return value


def validate_plan(plan):
    require(type(plan) is dict and type(plan.get("root")) is str, "Invalid preparation plan")
    limits = plan.get("limits", {})
    require(type(limits) is dict, "Plan limits object required")
    for name in ("hard_bytes", "used_stop_bytes", "closure_bytes", "poll_bytes"):
        integer(limits.get(name), name)
    require(limits["used_stop_bytes"] + limits["closure_bytes"] + limits["poll_bytes"]
            <= limits["hard_bytes"], "Stop gap must cover combined closure and poll reserves")
    require(digest(plan) == digest(preview(plan["root"])), "Changed preparation plan or plan digest")


def assess(plan, observation):
    """Evaluate supplied storage assumptions, never grant permission to execute."""
    validate_plan(plan)
    require(type(observation) is dict, "Observation object required")
    require(observation.get("provenance") in ("controlled-fixture", "caller-supplied-unverified"),
            "Supplied observation provenance required")
    require(observation.get("plan_sha256") == plan["plan_sha256"], "Stale or missing plan digest")
    for name in ("used_bytes", "next_unmaterialized_bytes", "unwritten_closure_bytes",
                 "poll_reserve_bytes", "age_ms"):
        integer(observation.get(name), name)
    status = observation.get("capacity_status", "unknown")
    require(status in ("known", "unknown", "denied"), "Invalid capacity status")
    if status == "known":
        integer(observation.get("caller_free_bytes"), "caller_free_bytes")
    limits = plan["limits"]
    future = (observation["next_unmaterialized_bytes"] + observation["unwritten_closure_bytes"]
              + observation["poll_reserve_bytes"])
    used_plus_future = observation["used_bytes"] + future
    required_free = limits["minimum_free_bytes"] + future
    integer(used_plus_future, "used plus future")
    integer(required_free, "floor plus future")
    reasons = []
    binding = observation.get("current_binding")
    previous = observation.get("snapshot_binding")
    bindings_match = digest(previous) == digest(binding)
    if not bindings_match:
        reasons.append("stale snapshot binding")
    valid_binding = type(binding) is dict and set(binding) == {
        "source_manifest_sha256", "cutoff", "destination", "volume", "allocation_unit"}
    if valid_binding:
        unit = binding["allocation_unit"]
        sha = binding["source_manifest_sha256"]
        valid_binding = (type(unit) is int and 0 < unit <= 1_048_576 and unit & (unit - 1) == 0
            and type(sha) is str and len(sha) == 64 and all(c in "0123456789abcdefABCDEF" for c in sha)
            and type(binding["volume"]) is str and bool(binding["volume"].strip())
            and type(binding["cutoff"]) is str and bool(binding["cutoff"].strip())
            and binding["destination"] == plan["root"])
    if not valid_binding:
        reasons.append("unknown or invalid source/destination/volume/allocation binding")
    if observation["unwritten_closure_bytes"] != limits["closure_bytes"]:
        reasons.append("initial assessment requires full unwritten closure reserve")
    if observation["poll_reserve_bytes"] != limits["poll_bytes"]:
        reasons.append("initial assessment requires full poll reserve")
    if observation.get("age_ms", 2001) > limits["maximum_gap_ms"]:
        reasons.append("stale observation")
    if status != "known":
        reasons.append("capacity " + status)
    if observation["used_bytes"] >= limits["used_stop_bytes"]:
        reasons.append("used-stop reached")
    if used_plus_future > limits["hard_bytes"]:
        reasons.append("hard budget insufficient for future obligations")
    if status == "known" and observation["caller_free_bytes"] < required_free:
        reasons.append("caller-free below floor plus future obligations")
    receipt = {"kind": "r1-storage-preparation-assessment", "plan_sha256": plan["plan_sha256"],
        "observation": json.loads(json.dumps(observation, allow_nan=False)),
        "facts_verified_natively": False, "execution_enabled": False,
        "binding_complete": valid_binding and bindings_match,
        "actual_commands_launched": [], "execution_blockers": list(plan["execution_blockers"]),
        "storage_preparation_eligible": not reasons, "reasons": reasons,
        "accounting": {"future_bytes": future, "used_plus_future_bytes": used_plus_future,
                       "required_caller_free_bytes": required_free}}
    receipt["receipt_sha256"] = digest(receipt)
    return receipt


def settle(plan, before, after):
    """Account for supplied closure observations; this does not stop any process."""
    validate_plan(plan)
    require(type(before) is dict and "observation" in before, "Prior assessment required")
    require(digest(before) == digest(assess(plan, before["observation"])), "Changed prior assessment")
    require(type(after) is dict and after.get("provenance") in
            ("controlled-fixture", "caller-supplied-unverified"), "Settlement provenance required")
    require(after.get("assessment_sha256") == before["receipt_sha256"], "Stale assessment binding")
    written = integer(after.get("closure_written_bytes"), "closure_written_bytes")
    used = integer(after.get("retained_used_bytes"), "retained_used_bytes")
    require(used >= before["observation"]["used_bytes"] + written, "Materialized closure is uncharged")
    state, cleanup = after.get("owned_producer_state"), after.get("cleanup_outcome")
    require(state in ("absent", "present", "unknown"), "Owned producer state required")
    require(cleanup in ("verified", "failed", "unknown"), "Cleanup outcome required")
    reasons = []
    if not before["binding_complete"]:
        reasons.append("prior binding incomplete or stale")
    if digest(after.get("binding")) != digest(before["observation"]["current_binding"]):
        reasons.append("stale settlement binding")
    if state != "absent" or cleanup != "verified":
        reasons.append("owned producer absence and cleanup not both verified")
    remaining = before["observation"]["unwritten_closure_bytes"] - written
    if remaining < 0:
        reasons.append("closure reserve exceeded")
    remaining = max(0, remaining)
    poll = before["observation"]["poll_reserve_bytes"] if reasons else 0
    total = integer(used + remaining + poll, "retained plus reserves")
    if total > plan["limits"]["hard_bytes"]:
        reasons.append("hard budget exceeded")
    receipt = {"kind": "r1-controlled-settlement", "plan_sha256": plan["plan_sha256"],
        "assessment_sha256": before["receipt_sha256"], "settlement_observation": json.loads(json.dumps(after, allow_nan=False)),
        "facts_verified_natively": False, "execution_enabled": False, "actual_commands_launched": [],
        "next_stage_enabled": False, "closed_cleanly": not reasons, "reasons": reasons,
        "accounting": {"retained_used_bytes": used, "unwritten_closure_bytes": remaining,
                       "poll_reserve_bytes": poll, "retained_plus_reserves_bytes": total},
        "execution_blockers": list(plan["execution_blockers"])}
    receipt["receipt_sha256"] = digest(receipt)
    return receipt


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    modes = parser.add_mutually_exclusive_group()
    modes.add_argument("--plan", action="store_true", help="inert preview (the default)")
    modes.add_argument("--assess", metavar="OBSERVATION_JSON", help="read at most 1MiB of supplied assumptions")
    parser.add_argument("--root", default="artifacts/r1-pilot-pending", help="unverified future task root")
    args = parser.parse_args(argv)
    try:
        plan = preview(args.root)
        if not args.assess:
            result, code = plan, 0
        else:
            with open(args.assess, "rb") as source:
                data = source.read(1_048_577)
            require(len(data) <= 1_048_576, "Observation JSON exceeds 1MiB")
            observation = json.loads(data.decode("utf-8-sig"))
            receipt = assess(plan, observation)
            result = {"kind": "r1-preparation-cli", "execution_enabled": False,
                "actual_commands_launched": [], "receipt": receipt,
                "input": {"sha256": hashlib.sha256(data).hexdigest(), "bytes": len(data),
                          "provenance": "caller-supplied-unverified", "filesystem_write": False}}
            code = 0 if receipt["storage_preparation_eligible"] else 1
    except (ValueError, OSError) as error:
        result = {"kind": "r1-preparation-input-rejected", "error": str(error),
                  "execution_enabled": False, "actual_commands_launched": []}
        code = 2
    print(json.dumps(result, indent=2, allow_nan=False))
    return code


if __name__ == "__main__":
    raise SystemExit(main())
