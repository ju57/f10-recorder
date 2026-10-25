# Controlled precision/export prototype (E1, throwaway)

This experiment answers whether the current segment-copy route or experimental alternatives can preserve an arbitrary five-minute H.264 frame interval and meet the accepted three-second median/five-second p95 export targets. It is not a production exporter, a game benchmark, or fullscreen/coexistence work.

`precision-export-prototype.py --plan` is read-only. Subsequent stages write only an explicit ignored `artifacts/` experiment directory, run hash-authenticated FFmpeg/ffprobe, and use NVENC with generated `testsrc2` pictures and synthetic audio. There are no capture devices, windows, global shortcuts, startup writes, installation, NVIDIA-setting changes or worker-control commands. GPU/CPU/disk load is real. Do not run it inside another performance collection window.

## Reproduction

Use the workspace Python runtime with NumPy. Run from the repository root, using a fresh directory. Windows and a usable NVIDIA encoder are required. Hash identities are read from `src/MediaDependency.cs`, not PATH or profile metadata. The official ZIP and both tools must match those pins before any tool runs; tool leases deny write/delete while each subprocess executes. A cached ZIP can be supplied with `--package` but is independently hash-checked.

```powershell
python tests/precision-export-prototype.py --root artifacts/precision-export-20261024-new --plan
python tests/precision-export-prototype.py --root artifacts/precision-export-20261024-new --bootstrap
python tests/precision-export-prototype.py --root artifacts/precision-export-20261024-new --qualify
python tests/precision-export-prototype.py --root artifacts/precision-export-20261024-new --qualify-indexed
python tests/precision-export-prototype.py --root artifacts/precision-export-20261024-new --revalidate-short
python tests/precision-export-prototype.py --root artifacts/precision-export-20261024-new --qualify-direct
python tests/precision-export-prototype.py --root artifacts/precision-export-20261024-new --prepare --source reference --seconds 312
python tests/precision-export-prototype.py --root artifacts/precision-export-20261024-new --benchmark
python tests/precision-export-prototype.py --root artifacts/precision-export-20261024-new --validate-reference
```

The script is intentionally specific to this experiment, including its frame rate, codec, marker geometry and artifact-family budget. It is not a reusable test framework. Bootstrap/download and hard-link creation can require execution outside the Windows sandbox. Never substitute an unverified system FFmpeg or silently retry an existing output directory.

## Source and independent checks

The generated source is 1920x1080 at 60 FPS, H.264 NVENC p3/low-latency/CBR20 Mbit/s, closed one-second GOPs, no B-frames, and stereo48 kHz AAC192 kbit/s. Every picture has a 20-bit frame ID plus complementary pixel row in 16x16 cells. The expected source interval is calculated from rational request boundaries; decoded pixels establish picture identity instead of inferring it from PTS, file duration or count alone.

Validation rejects ambiguous/complement-invalid IDs, gaps/duplicates, more than one output frame of start/end/duration error, non-increasing per-stream DTS/PTS and incorrect video frame spacing. It records the stricter exact-ceiling interval separately. Whole-file software decoding uses `-xerror`; packet/schema and audio-duration checks are separate. Generated 1 kHz pulses every second provide a phase-continuity check, **not unique absolute audio identity**: a whole-second shift can be ambiguous. This cannot establish device-clock synchronization or the two-hour A/V target.

An indexed/edit-list candidate is also decoded with edits ignored. That exposes compressed preroll retained outside the displayed interval; decoder compliance and physical trimming are different properties. Tests on FFmpeg alone do not establish every player's edit-list behavior.

## Strategies and prospective schedules

- Current ceil-GOP stream copy is a negative-control policy, not a precise arbitrary-boundary candidate.
- Full exact re-encoding is a correctness/speed control, not an optimized design.
- Hybrid output re-encodes partial head/tail GOPs, copies complete interior GOPs and encodes the audio interval once.
- Indexed edit-list copy includes preliminary MP4 indexing in its timing.
- Direct timestamp-filter copy retains the preceding IDR through stream-copy admission, then shifts timestamps with `setts` before MP4 edit-list publication.

Each controlled reference export snapshots already closed inputs. Timing starts immediately before snapshotting, after the artifact-budget check and initial receipt/directory setup. It includes input linking/manifest generation, tool authentication, subprocesses, assembly/encoding, muxing/faststart, structural ffprobe and final rename. It excludes live acquisition-tail closure, actual hotkey/control latency, and subsequent exhaustive pixel/audio/packet analysis. It is not ordinary-product request-to-file acceptance.

The frozen schedule declares20 hybrid and20 direct-copy observations (ten offsets in first/repeat passes), with individual current/full/indexed controls. First/repeat is not a claim of uncached-cold I/O. Method order is not counterbalanced. Never present n=1 controls as percentiles, regardless of generic summary fields. A separate real-time generated NVENC producer runs during collection and is stopped before analysis. The prototype checks its process liveness; it does not prove continuously healthy60 FPS capture from bracketed progress samples.

Retain every attempt, error, rejected candidate and output. A storage-guard defect in the original attempt counted hard links repeatedly; the corrected guard accounts for unique file identities. The interrupted batch and its three direct-copy timings remain separate from a fresh20-observation direct-copy batch. No previous timings are silently pooled. Later finalization/probe hypotheses have separate prospective amendments and single-control outputs, not invented twenty-sample success distributions.

`--probe-finalization --without-faststart` tests an MP4-finalization hypothesis. `--probe-configuration --input-probe skip-duration` and then `bounded` test nested per-file demux probing; they retain full final validation and explicit stream schema. The latter uses256 KiB/100 ms input probe limits on this known fixture, not universal recording defaults. Combined follow-up evidence has its own90 GB cap and40 GB free-space reserve. No deletion is used to admit another attempt.

## Production boundary and retained results

The fixture manifests use `-safe 0` for owned absolute paths. FFmpeg classifies per-file `option` directives as unsafe, whereas production currently uses `-safe 1`. Do not copy that flag into production without preserving generated-manifest authority, canonical contained filenames, immutable pinned inputs, exact option/value allowlists and file-only protocols. Limited input probing must not become permission to skip output validation or accept missing parameters. See [source review](../docs/m0-review/precision-command-review.md).

No production source, settings, recorder state or installer was changed. Machine-specific timings, raw media, tool bytes, environment metadata and complete results are retained locally under ignored `artifacts/precision-export-20261024-*`; the consolidated local report is under `artifacts/precision-export-results-20261024/`. Those files are not release artifacts. Exact recorded binaries/commands, qualified source identities, retained failure receipts and full validation must accompany any later claim. Live pipeline, source/device clocks, other codecs/FPS/track schemas, clean capture, fullscreen, gameplay overhead and broad player compatibility remain separate gates.
