"""Summarize game presentation traces and saved media; image repeats are candidates, not proven loss."""
import argparse
import csv
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime
import json
from pathlib import Path
import subprocess

import numpy as np


def summarize(values):
    data = np.asarray(values, dtype=float)
    data = data[np.isfinite(data)]
    if not len(data):
        return None
    return {"count": len(data), "mean": float(data.mean()),
            "p50": float(np.quantile(data, .5)), "p95": float(np.quantile(data, .95)),
            "p99": float(np.quantile(data, .99)), "max": float(data.max())}


def number(value):
    try:
        return float(value)
    except (ValueError, TypeError):
        return float("nan")


def analyze_trace(path, start, end):
    with path.open(newline="", encoding="utf-8-sig") as file:
        all_rows = list(csv.DictReader(file))
    coverage_end = max((number(row["TimeInMs"]) for row in all_rows), default=0) / 1000
    if start < 0 or end <= start or coverage_end < end:
        raise ValueError(f"Incomplete requested window {start:.3f}..{end:.3f}s; trace ends at {coverage_end:.3f}s: {path}")
    rows = [row for row in all_rows if start * 1000 <= number(row["TimeInMs"]) < end * 1000]
    if not rows:
        raise ValueError(f"No frames in requested analysis window: {path}")
    intervals = [number(row["MsBetweenPresents"]) for row in rows]
    frame_times = summarize(intervals)
    result = {"path": str(path), "all_frames": len(all_rows), "window_seconds": [start, end],
              "present_fps": 1000 / frame_times["mean"], "present_interval_ms": frame_times,
              "present_gaps_over_16_67ms": sum(value > 1000 / 60 for value in intervals),
              "present_gaps_over_50ms": sum(value > 50 for value in intervals),
              "modes": sorted({row["PresentMode"] for row in rows}),
              "allows_tearing": sorted({row["AllowsTearing"] for row in rows})}
    for name in ("MsBetweenDisplayChange", "MsUntilDisplayed", "MsGPUBusy", "MsGPUTime", "MsCPUBusy", "MsVideoBusy"):
        result[name] = summarize([number(row.get(name)) for row in rows])
    # An ideal sampler of fully displayed presentations is a model, not a mapping to encoded pictures.
    visible = sorted({number(row["TimeInMs"]) + number(row["MsUntilDisplayed"]) for row in all_rows
                      if number(row.get("MsUntilDisplayed")) > 0})
    if len(visible) > 1:
        expected_repeats = []
        for phase in np.linspace(0, 1000 / 120, 100, endpoint=False):
            ticks = np.arange(start * 1000 + phase, end * 1000, 1000 / 120)
            identities = np.searchsorted(visible, ticks, side="right") - 1
            identities = identities[identities >= 0]
            if len(identities) > 1:
                expected_repeats.append(float(np.mean(np.diff(identities) == 0) * 100))
        result["ideal_120fps_sampler_repeat_model_percent"] = {
            "min": min(expected_repeats), "max": max(expected_repeats),
            "limitations": "Clock phase sweep; presentation completion is not exact Desktop Duplication availability; tearing may apply."}
    return result


def analyze_trial(path, start, end):
    label = path.name.removesuffix("-presentmon.csv")
    resume_path = path.with_name(label + "-resume.json")
    ready_path = path.with_name(label + "-ready.json")
    offset = 0
    if resume_path.exists() and ready_path.exists():
        resume = json.loads(resume_path.read_text(encoding="utf-8-sig"))
        ready = json.loads(ready_path.read_text(encoding="utf-8-sig"))
        parse = lambda value: datetime.fromisoformat(value.replace("Z", "+00:00"))
        before, after = parse(resume["beforeUtc"]), parse(resume["afterUtc"])
        offset = ((before + (after - before) / 2) - parse(ready["TraceStartUtc"])).total_seconds()
    try:
        result = analyze_trace(path, start + offset, end + offset)
        result.update(label=label, qualification="complete requested window", resume_offset_seconds=offset)
        return result
    except ValueError as error:
        return {"label": label, "qualification": "inconclusive", "error": str(error)}


def analyze_worker(path):
    trial = json.loads(path.read_text(encoding="utf-8-sig"))
    samples = trial["Samples"]
    if not samples:
        raise ValueError(f"No worker sample coverage: {path}")
    states = [sample["Status"] for sample in samples]
    metrics = [state["Metrics"] for state in states]
    active = trial["Mode"] == "On"
    config = states[0]["Config"]
    result = {"label": trial["Label"], "mode": trial["Mode"], "samples": len(samples),
              "last_sample_seconds": samples[-1]["ElapsedSeconds"],
              "trace_exit_code": trial["TraceExitCode"],
              "foreground_apps": sorted({state["ApplicationName"] for state in states}),
              "capture_process_ids": sorted({metric["CaptureProcessId"] for metric in metrics}),
              "max_recoveries": max(metric["Recoveries"] for metric in metrics),
              "max_cache_bytes": max(metric["CacheBytes"] for metric in metrics),
              "cache_limit_bytes": int((config["ReplayMinutes"] * 60 + 30) *
                                       (config["BitrateMbps"] * 1_000_000 + 192_000) / 8 * 1.5),
              "capture_cpu_percent": summarize([metric["CaptureCpuPercent"] for metric in metrics]),
              "worker_cpu_percent": summarize([metric["WorkerCpuPercent"] for metric in metrics]),
              "cpu_limitations": "Percent normalized across logical processors; replay remux child and isolated GPU cost not measured."}
    result["health_passed"] = (trial["TraceExitCode"] == 0 and
        all(state["ApplicationName"] == "cs2" for state in states) and
        (all(state["Ready"] and not state["Paused"] for state in states) if active else
         all(state["Paused"] and state["Metrics"]["CaptureProcessId"] == 0 for state in states)) and
        (not active or len(result["capture_process_ids"]) == 1 and result["capture_process_ids"][0] != 0) and
        result["max_recoveries"] == 0 and result["max_cache_bytes"] <= result["cache_limit_bytes"])
    return result


def run(command, timeout=180):
    completed = subprocess.run(command, capture_output=True, timeout=timeout)
    if completed.returncode:
        raise RuntimeError(completed.stderr.decode(errors="replace")[-6000:])
    return completed.stdout


def analyze_clip(path, ffmpeg, output):
    ffprobe = str(Path(ffmpeg).with_name("ffprobe.exe"))
    media = json.loads(run([ffprobe, "-v", "error", "-show_streams", "-show_format", "-of", "json", str(path)]))
    timestamps = json.loads(run([ffprobe, "-v", "error", "-select_streams", "v:0", "-show_frames",
                                "-show_entries", "frame=best_effort_timestamp_time", "-of", "json", str(path)]))
    pts = np.array([float(frame["best_effort_timestamp_time"]) for frame in timestamps["frames"]])
    out = output / path.stem
    out.mkdir(parents=True, exist_ok=True)
    (out / "streams.json").write_text(json.dumps(media, indent=2))
    (out / "timestamps.json").write_text(json.dumps(timestamps))
    # World-region crop excludes most HUD and weapon animation. Sensitivity is reported, never used as a loss gate.
    gray = run([ffmpeg, "-hide_banner", "-v", "error", "-xerror", "-i", str(path), "-map", "0:v:0", "-vf",
                "crop=iw*0.7:ih*0.5:iw*0.15:ih*0.25,scale=96:64:flags=area,format=gray",
                "-fps_mode", "passthrough", "-f", "rawvideo", "pipe:1", "-map", "0:a", "-f", "null", "NUL"])
    images = np.frombuffer(gray, dtype=np.uint8).reshape(-1, 96 * 64)
    if len(images) != len(pts) or len(pts) < 2:
        raise ValueError(f"Incomplete decoded image/timestamp coverage: {path}")
    delta = np.mean(np.abs(np.diff(images.astype(np.int16), axis=0)), axis=1)
    np.savetxt(out / "world-image-differences.csv", np.column_stack((pts[1:], delta)),
               delimiter=",", header="pts_seconds,mean_absolute_luma_difference", comments="")
    candidates = []
    exact_holds_with_moving_neighbors = []
    index = 0
    while index < len(delta):
        if delta[index] != 0:
            index += 1
            continue
        first = index
        while index < len(delta) and delta[index] == 0:
            index += 1
        if index - first >= 2 and first > 0 and index < len(delta) and delta[first - 1] > 1 and delta[index] > 1:
            exact_holds_with_moving_neighbors.append({
                "start_pts_seconds": float(pts[first]), "frames": index - first + 1,
                "duration_ms": (index - first + 1) / 120 * 1000,
                "previous_delta_luma": float(delta[first - 1]), "next_delta_luma": float(delta[index])})
    for threshold in (0, .05, .1, .25):
        repeats = delta <= threshold
        longest, length = 0, 0
        for repeat in repeats:
            length = length + 1 if repeat else 0
            longest = max(longest, length)
        candidates.append({"threshold_luma": threshold, "near_static_pairs_percent": float(repeats.mean() * 100),
                           "longest_near_static_run_ms": (longest + 1) / 120 * 1000})
    video = [stream for stream in media["streams"] if stream["codec_type"] == "video"]
    audio = [stream for stream in media["streams"] if stream["codec_type"] == "audio"]
    if len(video) != 1 or len(audio) != 1 or video[0]["r_frame_rate"] != "120/1":
        raise ValueError(f"Expected one 120 FPS video and one system-audio stream: {path}")
    if np.any(np.diff(pts) <= 0) or np.max(np.abs(np.diff(pts) - 1 / 120)) > .000002:
        raise ValueError(f"Invalid 120 FPS video timestamps: {path}")
    boundaries = None
    if len(video) == 1 and len(audio) == 1:
        vs, a = video[0], audio[0]
        vstart, astart = float(vs.get("start_time", 0)), float(a.get("start_time", 0))
        vend, aend = vstart + float(vs["duration"]), astart + float(a["duration"])
        boundaries = {"start_skew_ms": abs(vstart - astart) * 1000, "end_skew_ms": abs(vend - aend) * 1000}
        if max(boundaries.values()) > 50:
            raise ValueError(f"A/V boundary skew exceeds 50 ms: {path}")
    result = {"clip": str(path), "fully_decoded": True, "frames": len(pts),
              "width": video[0]["width"], "height": video[0]["height"],
              "frame_rate": video[0]["r_frame_rate"], "nonincreasing_pts": int(np.sum(np.diff(pts) <= 0)),
              "timestamp_intervals_ms": summarize(np.diff(pts) * 1000), "av_boundaries": boundaries,
              "image_difference_luma": summarize(delta), "near_static_candidates": candidates,
              "exact_hold_candidates_with_moving_neighbors": exact_holds_with_moving_neighbors,
              "hold_candidate_limitations": "At least two exact repeat pairs with neighboring differences above 1 luma; investigate visually, no source identities or proved capture loss.",
              "loss_free_acceptance": "undetermined: no source picture IDs; source stalls and static scenes also repeat imagery"}
    (out / "analysis.json").write_text(json.dumps(result, indent=2))
    return result


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("directory", type=Path)
    parser.add_argument("--start", type=float, default=10)
    parser.add_argument("--end", type=float, default=55)
    parser.add_argument("--clips", action="store_true")
    parser.add_argument("--jobs", type=int, choices=(1, 2), default=1)
    parser.add_argument("--ffmpeg", default=r"C:\Program Files\ffmpeg\bin\ffmpeg.exe")
    args = parser.parse_args()
    report = {"traces": [analyze_trial(path, args.start, args.end) for path in sorted(args.directory.glob("cap*-presentmon.csv"))]}
    report["workers"] = [analyze_worker(path) for path in sorted(args.directory.glob("cap*-worker.json"))]
    if any(not worker["health_passed"] for worker in report["workers"]):
        raise ValueError("Worker health verification failed; inspect individual reports.")
    if args.clips:
        paths = sorted((args.directory / "worker" / "clips").rglob("*.mp4"))
        if not paths:
            raise ValueError("No saved clips were found; media verification cannot pass without coverage.")
        with ThreadPoolExecutor(max_workers=args.jobs) as pool:
            report["clips"] = list(pool.map(lambda path: analyze_clip(path, args.ffmpeg, args.directory / "media-analysis"), paths))
    (args.directory / "analysis.json").write_text(json.dumps(report, indent=2))
    print(json.dumps(report, indent=2))
