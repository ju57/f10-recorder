# Indexed-reader prototype (E1, throwaway)

This generated-footage experiment tests whether an index prepared once for closed chunks can avoid repeated FFmpeg concat child analysis while preserving the accepted exact-video interval and median <=3 s / nearest-rank p95 <=5 s save targets. It is separate from production F10 and from fullscreen/coexistence work.

## Method and boundaries

`indexed-reader-prototype.py` uses the existing controlled H.264 1920x1080/60 FPS, closed-GOP, no-B-frame, stereo 48 kHz AAC footage from the earlier precision prototype. It imports that prototype's pinned-engine leases, generated-source producer and independent pixel/packet/audio validator. No media library is installed and no NVIDIA code is used.

Preparation hashes each source against the retained reference inventory, links the immutable source files and builds a small MPEG-TS metadata index. It records PES timestamp locations, transport PCR locations, original video epoch and the admitted stream layout. Packet payload bytes remain in the source files; a merged video or complete payload cache is not prepared outside the save timer. Preparation wall/CPU and per-chunk costs are reported separately. This post-capture measurement does not establish live acquisition/index-maintenance overhead.

At save time the reader snapshots the required source chunks, re-encodes only partial head/tail video GOPs, reads source bytes on demand, rewrites transport timing onto the known fixture epoch, regenerates continuity counters and pipes one continuous MPEG-TS input to the pinned FFmpeg executable. FFmpeg still demuxes/probes that one input and writes the MP4; this is not a native direct-packet mux implementation. Full interior video remains copied, while audio is trimmed and encoded once. Transport timestamp rewriting is specific to this known fixture and is not a general TS editor.

The admitted layout is deliberately narrow: clear 188-byte packets, expected PAT/PMT/PIDs, H.264/AAC, unsplit PES timing headers, no B-frames and no source/schema changes. Unsupported layouts fail rather than being treated as compatible. Playback qualification covers FFmpeg, not every player or encoder-parameter transition. It does not implement recovery, FIFO or production index authentication.

This index describes transport timestamps and offsets, not arbitrary codec dependencies. One-second closed GOPs, a leading IDR and the filename-to-session-time relationship come from the hashed generated fixture; the reader does not recover those contracts for unknown media. Header admission alone cannot detect every unsupported codec/schema transition. The full frame-identity checks qualify the selected fixture intervals.

The transport-field review used the tagged FFmpeg 9.0.2 [MPEG-TS writer](https://raw.githubusercontent.com/FFmpeg/FFmpeg/n9.0.2/libavformat/mpegtsenc.c) and [demuxer](https://raw.githubusercontent.com/FFmpeg/FFmpeg/n9.0.2/libavformat/mpegts.c) as primary references for timestamp coding and continuity behavior. The prototype remains a narrow independent experiment, not a replacement for those format implementations.

## Commands

Use the existing workspace Python runtime with NumPy, from the repository root. Start with a fresh explicit `artifacts/` directory. An existing seed contains the hash-pinned FFmpeg 9.0.2 executables, generated short/reference sources and retained source inventory.

```powershell
python tests/indexed-reader-prototype.py --root artifacts/indexed-reader-new --plan
python tests/indexed-reader-prototype.py --root artifacts/indexed-reader-new --seed artifacts/precision-export-20261024-b --prepare
python tests/indexed-reader-prototype.py --root artifacts/indexed-reader-new --qualify
python tests/indexed-reader-prototype.py --root artifacts/indexed-reader-new --probe
python tests/indexed-reader-prototype.py --root artifacts/indexed-reader-new --benchmark
python tests/indexed-reader-prototype.py --root artifacts/indexed-reader-new --validate
```

`--plan` is read-only. Later actions write only the selected ignored experiment root and run authenticated media tools. Windows hard links and deny-write/delete executable leases can require an authorized outside-sandbox invocation. Tool and original source hashes are checked; do not substitute PATH tools or reuse a partially completed timing schedule as a fresh batch.

## Effects and measurement contract

There is real GPU/CPU/storage load from generated NVENC video and export work. There is no desktop/system-audio/microphone capture, window, UI automation, shortcut, startup write, recorder-worker control, installation or NVIDIA setting change. Keep unrelated measurement sessions idle. Stop only owned subprocesses and retain their shutdown receipts. No exhaustive validation decoding runs while reference export timing is collected.

Timing begins before source snapshots and includes boundary encoding, executable authentication, virtual-reader I/O/timestamp rewriting/pipe handoff, muxing/faststart, a structural ffprobe and final rename. Initial directory/receipt setup and storage admission are outside, matching the earlier prototype boundary. Full pixel/audio/packet validation runs afterward, with the producer stopped. Already closed fixture inputs omit live acquisition-tail closure and actual hotkey/request latency; this is not product end-to-end acceptance. Outputs are not fsync-qualified physical durability measurements.

Short qualification checks five phase-varied intervals. A separate five-minute candidate and legacy-hybrid control run before a fresh twenty-observation candidate collection. The two ten-offset passes preserve the earlier rational phases; first/repeat labels do not prove OS-cold I/O and observations are not pooled with controls or historical results. No post-hoc dropping of slow saves is permitted.

The concurrent generated encoder reports progress at coarse one-second intervals; a 250 ms monitor records process liveness/frame counters bracketing each trial. Its added gate checks advancing progress, poll gaps <1.5 s and observed stalls <2.5 s. A minimum 30 counter-frames/s is only a coarse stalled-producer rejection threshold, not a relaxed 60 FPS capture acceptance threshold. The per-trial actual rates remain in receipts. These samples do not prove uninterrupted source-picture acquisition or gaming behavior.

Full validation checks every coded frame ID/complement, contiguity, expected exact-ceiling interval and <=one-frame boundary rounding, strict per-stream PTS/DTS, frame cadence, schema and decoded audio duration. Every candidate is also decoded ignoring MP4 edit lists, exposing any hidden leading video pictures. The existing periodic audio markers establish phase modulo one second; they do not uniquely identify absolute audio or establish live device-clock sync. That limitation remains explicit rather than inventing stronger audio acceptance from the reused footage.

The experiment budgets 30 GB of unique-file storage, a 2 GB next-trial reserve and 40 GB minimum free space. Hard-link aliases are counted once. The initial expected total is 22 GB. Retain failures, rejected candidates, source/harness versions and all trial outputs; do not delete evidence to admit another trial. Machine-specific results belong under ignored `artifacts/indexed-reader-*`.

## Remaining production work

A useful controlled result still needs a production reader/index interface, source identity/integrity and lifecycle rules, bounded ongoing index cost, exact clock/schema mapping, recovery, audio/codec matrix and broad playback checks. Preserve immutable chunks and per-request pins. Do not replace production safe-manifest/protocol rules with unrestricted byte streams or weaken validation to meet a latency number. The native research and proposed independent adaptation are documented in [the ShadowPlay save investigation](../docs/m0-review/shadowplay-save-investigation.md).
