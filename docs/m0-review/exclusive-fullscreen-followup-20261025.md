# Traditional exclusive fullscreen: qualification and capture failure

25 October 2026. This continues the [overlay/exclusion investigation](overlay-exclusion-followup-20261025.md). All new capture and native code is disposable ignored fixture code. No production F10 code was changed.

## Findings

**A controlled traditional exclusive-fullscreen source is now reproducible. NVIDIA successfully saved its fresh pictures, but the pinned FFmpeg monitor-capture path failed before its first video packet. A separate SDK-verified native probe observed duplication access loss.** Genuine fullscreen recording in F10 is therefore still open; source qualification is progress, not a capture pass.

| Check | Result | Claim boundary |
| --- | --- | --- |
| Dedicated apphost, default settings | 514 presentation events; 507 Independent Flip, zero Legacy Flip. Source foreground/API-fullscreen/Present progression qualified. | Reproduces optimized presentation; cannot qualify traditional exclusive. |
| User disabled fullscreen optimizations for this fixture only | First trial observed 149 Legacy Flip events over2.291329s, then the Python status reader encountered a file-sharing race. | Rejected in full; no capture admitted. No administrator/access-permission failure was inferred. |
| Fresh B mode qualification, bounded sharing retries | 521 Legacy Flip events spanning8.167471s; source foreground/S_OK/fullscreen and progressing Presents qualified.80 telemetry reads, zero retries/missing samples in this accepted run. | Controlled legacy-mode candidate established. This is a process-level mode check, not complete source-to-present/frame-rate/game acceptance. |
| B capture, input-lavfi route | Opened the display, probed8-bit format, failed at `DDA ReleaseFrame failed`; zero output packets. | Rejected DDA acquisition, not an encoder-speed miss. |
| C capture, complex-filtergraph route | Same first-frame release failure with the same source, engine, rate, format and NVENC settings. | Rejects the graph-route workaround. This was one prospectively changed source-integration variable, not a production patch. |
| C independent NVIDIA save |510/510 decoded pictures have valid source IDs,503 distinct, all in source submissions; all streams fully decode with strictly increasing packet DTS. Container8.505744s. | Fresh vendor footage from this controlled source; no audio-marker/perceptual sync, cadence, overhead or proprietary-mechanism claim. |
| C presentation interval |914 process-filtered Legacy Flip events.541 Legacy Flip events fall inside the exact source-submission QPC span of decoded NVIDIA IDs. | Supports the legacy-mode source context for that clip.913/914 rows have Runtime `Other`/driver-level identity; these are not per-picture API lineage or proof of NVIDIA's capture method. |
| Corrected native Windows API probe | Both DuplicateOutput1/BGRA and DuplicateOutput create successfully. Modern API completes5 acquisition/release pairs, then AcquireNextFrame returns `0x887A0026`; legacy API completes6 pairs, then returns the same code. Source remains foreground/fullscreen/S_OK; trace contains262 Legacy Flip events over4.150464s. | Observed duplication invalidation in a separate no-copy/no-encoder probe. Not the original FFmpeg ReleaseFrame HRESULT, a loss-free stream, or proof of the invalidation's cause. |

The successful decoded ID checks do not establish source-picture completeness: duplicates/gaps remain recorded. No real game, mode-transition continuity, interactive overlay, stretched-resolution scenario, FPS telemetry, A/V calibration or overhead batch was qualified.

## What the native error means

Microsoft documents `DXGI_ERROR_ACCESS_LOST` as requiring the duplication interface to be released and recreated; desktop switches, display-mode changes and fullscreen/DWM transitions are among its documented circumstances. Those possibilities do not identify the cause in this trial. [AcquireNextFrame contract](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/nf-dxgi1_2-idxgioutputduplication-acquirenextframe).

The [FFmpeg9.0.2 source](https://github.com/FFmpeg/FFmpeg/blob/n9.0.2/libavfilter/vsrc_ddagrab.c#L1099) logs a generic failure when ReleaseFrame fails and returns an external-library error without printing that HRESULT. The tested binary's default format is already8-bit, so explicitly selecting BGRA would not test a different format here. The independent native probe's access-loss result is a useful diagnostic lead, not a substitute for the missing original call's return code. No FFmpeg rebuild or vendor modification was performed.

## Rejected diagnostic work retained

The initial native probe A used incorrect COM method offsets: Acquire selector9 and Release selector15. Microsoft defines IDXGIOutputDuplication as inheriting IDXGIObject, so the correct selectors are8 and14. Its first apparent INVALID_CALL results came from the wrong method and are invalid evidence about acquisition. That probe is explicitly rejected.

The corrected B probe was built from current corrected source, and an independent check parses Microsoft's [SDK vtable declaration](https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/shared/dxgi1_2.h) to verify actual selectors. It detects the original mismatch and accepts8/14. The header's retained digest, verification script, source and rebuilt DLL identities are preserved. Earlier source, binary and logs remain unchanged; no rejected attempt was relabeled a pass.

The status-reader correction permits six reads over at most25ms for transient sharing/missing/partial snapshots. Missing telemetry after that bound still rejects the trial. Qualification excludes pre-acknowledgement snapshots by their recorded QPC, requires at least20 post-acknowledgement samples, and retains the foreground/fullscreen/S_OK and progression gates. No threshold was relaxed to accept the interrupted first trial.

## Evidence

- [Default dedicated-apphost baseline](../../artifacts/exclusive-probe-20261025/baseline/result.json).
- [First interrupted/rejected mode trial](../../artifacts/exclusive-probe-20261025/optimizations-disabled/result.json).
- [Accepted B mode qualification](../../artifacts/exclusive-probe-20261025-b/optimizations-disabled/result.json).
- [B input-route failure](../../artifacts/exclusive-probe-20261025-b/capture/display.log).
- [C graph-route failure](../../artifacts/exclusive-probe-20261025-c/capture/display.log), [DDA rejection](../../artifacts/exclusive-probe-20261025-c/capture/dda-rejected.json), [NVIDIA full decode/IDs](../../artifacts/exclusive-probe-20261025-c/capture/analysis.json), [exact-ID source-clock/mode interval](../../artifacts/exclusive-probe-20261025-c/capture/mode-content-span.json).
- [Corrected native API returns](../../artifacts/native-duplication-20261025-b/legacy/native/analysis.json), [raw calls](../../artifacts/native-duplication-20261025-b/legacy/native/native.jsonl), [mode/source result](../../artifacts/native-duplication-20261025-b/legacy/result.json), [independent ABI verification](../../artifacts/native-duplication-20261025-b/abi-verification.json).

New native/API traces are diagnostic observations and have observer cost. The two duplication APIs ran sequentially in one source run, not a counterbalanced performance comparison. The probe intentionally performs no GPU pixel copy, encoding or media publication.

## Next bounded experiment

**Follow-up completed:** the separately authorized [capture-recovery prototype](capture-recovery-20261025.md) did not pass sustained fullscreen or whole-recording freshness. It recovered fresh recording after returning to windowed presentation, preserving one clock, but found stale transition pictures with newer timestamps. That report supersedes the proposed next step below; no alternative backend has yet been tested in this follow-up.

Build an isolated duplication-recreation prototype that records the original acquire/release HRESULT, destroys the invalid duplication object and reconstructs it within a prospectively bounded retry/time budget. Require fresh source pictures after recovery, preserved acquisition clock/timestamps and explicit failure when recovery does not converge. Measure repeated invalidations; merely reacquiring one frame is not a stable recording pass. Compare NVIDIA On/Off only under a separate matched finite plan with final On restoration, rather than assuming NVIDIA caused this invalidation.

If that remains unstable, qualify monitor-targeted Windows Graphics Capture as a separate candidate against the same actual exclusive source and frame-ID oracle. Any change in presentation mode during capture remains visible in the trace; an optimized-mode fallback cannot silently become an exclusive pass. This is a research recommendation, not a selected or implemented production backend.

The independently measured global window-affinity/NVIDIA incompatibility remains a separate architecture decision. No capture API fix alone proves that F10 can hide its controls from monitor recording while preserving NVIDIA desktop Replay.

## Preservation

The user applied Microsoft's [per-executable fullscreen-optimization setting](https://devblogs.microsoft.com/directx/demystifying-full-screen-optimizations/) to the dedicated disposable `F10FullscreenProbe.exe`. Its original current-user compatibility value was absent. The exact observed user-applied value was saved; corrected retries reapplied only that same value to the same path with compare-before-write protection, and every wrapper restored the original absence in `finally`. The shared dotnet host, installed F10, game files, global graphics preferences and other compatibility entries were not changed. No Windows administrator elevation or tool installation was needed.

The source restored windowed state and balanced keep-awake on exit. All task-owned source, capture, trace and probe processes stopped. NVIDIA Replay is On; preferences, startup and old gallery file size/mtime metadata are preserved. The new independent NVIDIA clip remains in the normal gallery and has a hashed local copy. Recovery/soak, performance, real-game and full product acceptance are still outstanding. E2/M0 are not closed.

All roots added during this follow-up total about24.3MB at closure; the earlier cleanup remains effective. Recordings, machine-specific data, SDK extracts and prototype binaries remain ignored under artifacts.
