# Capture recovery prototype

25 October 2026. Continues the [exclusive-fullscreen investigation](exclusive-fullscreen-followup-20261025.md). The maintainer authorized a separate recovery prototype and a report before moving on. Only disposable code under ignored `artifacts/` changed; F10 production capture and recording preferences did not change.

## Result

**Recreation can resume fresh recording after leaving fullscreen, while keeping one clock. It did not establish reliable recording during exclusive fullscreen, and the complete freshness/timestamp test failed.** Two stale transition pictures were assigned apparently newer acquisition timestamps. A decodable MP4 and increasing timestamps alone would have missed this defect.

| Test | Observed result | Verdict |
| --- | --- | --- |
| A: start capture with source already in qualified traditional fullscreen | 267 Legacy Flip process events over 4.154559 s; all sampled source telemetry foreground/fullscreen/S_OK. 31 access-loss errors; 30 successful recreations after initial creation. Zero fresh pictures; bounded failure. | Failed steady-fullscreen recovery. Creating the interface successfully does not establish usable capture. |
| B: keep capture running through windowed → fullscreen → windowed | Twelve-second collector, 17 access losses, 18 duplication generations, 131 captured pictures. Final generation contains 90 fresh pictures, IDs 294–643, strictly advancing. First completed fresh picture after windowed acknowledgement: 73.533 ms. No more access losses for the final 5.677495 s. | Useful evidence of recovery after returning to windowed presentation, not sustained fullscreen capture. |
| B: source timing after return | All 90 recovered pictures belong to source submissions after the windowed acknowledgement. Acquisition QPC minus corresponding source Present-return QPC ranges from 0.5348 to 29.3764 ms, within the prospective 50 ms bound. | Recovered tail passes the scoped freshness/phase checks. Present return is a reference event, not photon or perceptual A/V timing. |
| B: complete picture sequence | All 131 decoded IDs have valid complementary barcodes/checksums and occur in source submissions. Only 129 are distinct. Frame 38 repeats old ID 176; frame 40 returns to ID 176 after ID 199. Their acquisition/source phase differences are 1175.8693 and 3108.8976 ms. | Failed whole-recording freshness, strict ID progression and source-picture timestamp binding. These pictures are not repaired or hidden in the result. |
| B: encoded timing and decode | 131 MP4 packets and decoded pictures; every MP4 PTS exactly equals the corresponding acquisition PTS, with zero microsecond error. PTS/DTS strictly increase; final packet duration exactly matches the 31521 µs stop tail. Full decode passes. | Encoding preserves the supplied timeline, including its gaps, but also faithfully preserves the two incorrect picture/time associations. This is not a whole-recording timestamp pass. |
| B: presentation context | 114 Legacy Flip and 193 Composed GPU GDI process events. The prospective middle phase contains 97 Legacy Flip events plus composed events, although sampled API fullscreen state is true. | Fails the pure-Legacy middle-phase gate. Transition mode is mixed; no uninterrupted traditional-fullscreen interval is silently accepted. |

Both independent `analysis.json` files say **`pass: false`**. B's `result.json` contains a `legacy_candidate_pass: true` field inherited from source collection: that only counts qualifying mode events and source telemetry. It is not the complete recovery verdict; the later independent picture/timestamp/mode analysis rejects B.

## Implementation and checks

The native helper uses the same selected monitor on the NVIDIA adapter throughout. It calls `DuplicateOutput1` with BGRA, copies actual acquired full-resolution pixels into its own staging texture, releases the frame, and maps the staging copy into bounded PNG files. It does not switch monitors or fall back to another source. There is no overlay or display-affinity manipulation in these trials.

On `DXGI_ERROR_ACCESS_LOST` from acquisition or release, the helper releases and recreates the duplication interface while retaining the device/context and a single acquisition-QPC epoch. It discards a copied picture if its release fails. Other errors remain explicit failures. Zero/unchanged presentation timestamps are skipped; backwards timestamps fail instead of being restamped. Limits cover duration, frame count, retry count, disk reserve and PNG bytes.

A prospectively allowed 30 recoveries bounds A. Its failure was retained before preparing B. B asks a different question: can capture resume after the source leaves fullscreen? It adds a 50 ms recovery backoff, allows at most 100 losses during its fixed twelve seconds, requests fullscreen after 2.5 seconds of controller time and windowed after 5.5 seconds, and requires at least ten pre-transition and ten recovered pictures plus a stable final tail. These B bounds do not revise A's failed acceptance criteria. Actual source command acknowledgements and mode traces, rather than scheduled offsets alone, are used in analysis.

The first emitted picture establishes the QPC epoch. Every later PTS, including after recreation, is computed from that same epoch. B's last acquisition is 11.921144 seconds after its first; the 12.003779-second collection is not shortened around the interruption. Variable timestamps preserve missed intervals as held pictures in playback. Missing source pictures are not reconstructed.

Encoding runs **after all live collectors stop**, using the retained pinned H264 NVENC engine, no audio, no B frames and a microsecond media time base. The oracle decodes the resulting MP4, independently reads both barcode rows and checksums, maps each ID to the source's actual submission log, and compares picture/source timing. An explicit final-packet duration preserves the stop tail. A separate four-frame 1080p smoke control with unequal gaps also preserved all four requested PTS exactly and the 50 ms final duration. Earlier rejected encoder controls remain retained.

Both native builds completed with zero warnings/errors. Independent parsing of Microsoft's retained SDK headers verifies COM method slots for acquisition/release, texture copy/creation, description and mapping. Frozen source/build/tool/header identities still match at closure. No production clock change or product acceptance test is claimed by these isolated checks.

## What applies to F10

- A bounded invalidation/recreation state machine is useful, and keeping one clock across it is feasible. Publish failure when recovery does not converge; successful construction alone must not restore a ready state.
- Validate source-picture identity as well as increasing packet timestamps. Here two old pictures acquired apparently current timestamps. The cause of those returns is unresolved; this experiment does not attribute it to NVIDIA Replay, the driver, DWM or a particular implementation defect.
- The test barcode is an oracle, not a proposed production filter. Dropping frames because they fail a fixture barcode, accepting only selected successful generations, or treating identical pixels as necessarily stale would not establish a general solution for static desktop content.
- Recovery after returning to windowed presentation does not satisfy the accepted exclusive-fullscreen requirement. No source or presentation-mode fallback is authorized as an implicit workaround.

Further blind recreation retries are unlikely to resolve the demonstrated limitation. **The next recommended experiment is a separately planned monitor-targeted Windows Graphics Capture comparison against the same real source IDs and mode evidence.** This report does not start that comparison or select a production backend. If it also cannot produce fresh pictures during qualified traditional fullscreen, return to the maintainer with the evidence and the concrete deeper instrumentation/backend trade-off. A matched NVIDIA On/Off test would need its own finite plan and final On restoration; this run made no such causal comparison.

The earlier [global display-affinity/NVIDIA incompatibility](overlay-exclusion-followup-20261025.md) is separate and remains unresolved even if another acquisition path succeeds. E2 and M0 stay open.

## Evidence and preservation

- A: [frozen plan](../../artifacts/capture-recovery-20261025-a/trial-01/plan.json), [native calls](../../artifacts/capture-recovery-20261025-a/trial-01/native/events.jsonl), [independent rejection](../../artifacts/capture-recovery-20261025-a/trial-01/analysis.json), [closure](../../artifacts/capture-recovery-20261025-a/closure.json).
- B: [frozen plan](../../artifacts/capture-recovery-20261025-b/trial-01/plan.json), [native frame manifest](../../artifacts/capture-recovery-20261025-b/trial-01/native/capture.json), [independent rejection](../../artifacts/capture-recovery-20261025-b/trial-01/analysis.json), [recovered-tail and stale-frame audit](../../artifacts/capture-recovery-20261025-b/post-audit.json), [closure](../../artifacts/capture-recovery-20261025-b/closure.json).
- Video: [complete B diagnostic recording, including rejected stale transition pictures](../../artifacts/capture-recovery-20261025-b/trial-01/native/recording.mp4), [source-mode trace](../../artifacts/capture-recovery-20261025-b/trial-01/mode-presentmon.csv), [encoded packet audit](../../artifacts/capture-recovery-20261025-b/trial-01/native/encoded-timestamps.json).
- Controls: [ABI verification](../../artifacts/capture-recovery-20261025-b/abi-verification.json), [unequal-gap timestamp smoke](../../artifacts/capture-recovery-20261025-a/timestamp-smoke-fullhd-tail/encoded-timestamps.json).

The capture adapter is the RTX 4070 Ti, selected output `\\.\DISPLAY2`, 1920×1080; current Windows NVIDIA driver version is 32.0.16.2101. Plans retain exact helper/source/engine/probe/PresentMon hashes. The fixture is disposable WinForms/native code; this is not a change to the maintained Avalonia product.

Each wrapper temporarily reapplied only the exact compatibility value the user previously selected for the dedicated `F10FullscreenProbe.exe`, with compare-before-write protection, and restored the original absence in `finally`. NVIDIA Replay is On and its complete sampled status matches the pre-trial baseline. Original NVIDIA settings, F10 startup entry and old gallery MP4 size/mtime metadata match. No prior recording was deleted; metadata preservation does not assert a new hash audit of all old media. All six task-owned live processes across A/B are confirmed stopped. No administrator token, install, commit, push or merge was used.

The initial read-only closure draft falsely marked startup unequal by comparing a Python registry tuple with its JSON-list snapshot. Those `closure-v1-type-mismatch.json` receipts are retained. Normalizing the representation and recording both values confirms equality; this correction did not write a registry setting or change either failed capture verdict.

Both new evidence roots total approximately **7.3 MB** at closure. Rejected trials, source logs, actual PNGs, diagnostic MP4 and verification receipts are retained. No large prior capture corpus was regenerated. PNG spooling and source/status/PresentMon observation have overhead; this is video-only correctness evidence, not live encoder speed, source completeness, audio synchronization, game performance or long-duration recovery acceptance.
