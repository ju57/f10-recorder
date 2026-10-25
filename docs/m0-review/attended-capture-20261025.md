# Attended capture follow-up, 25 October 2026

The user returned to the PC and helped foreground the controlled source. No real game, production F10 build or installed app was tested. Existing working-tree changes were preserved on `codex/m0-beta-decisions`.

## Results

**Both monitor acquisition and NVIDIA replay now contain fresh controlled pictures.** This resolves the immediate inability to obtain useful monitor/NVIDIA content in yesterday's unattended environment, but does not identify the cause of that failure.

| Finite attempt | Result | Scope and limits |
| --- | --- | --- |
| Foreground monitor/NVIDIA qualification | DDA/NVENC clip: 347/347 valid distinct source IDs over a six-second file. NVIDIA clip: 539/539 valid IDs, 522 distinct, over an 8.983844-second container. All accepted IDs occur in the source submission log; all streams fully decode; packet DTS is present and strictly increasing. | Fresh-picture feasibility while this controlled source was foreground. Not 60-FPS completeness, A/V marker sync, gameplay overhead, production integration or universal coexistence. |
| Owned-overlay trial A | Rejected before useful footage: source foreground was false in the first sample after showing the ordinary owned overlay. User confirmed they did not switch away. Source was foreground again after cleanup. | Test/setup interruption, not a product failure or successful exclusion trial. |
| Owned-overlay trial B | Prospectively added a finite ten-second setup wait requiring one continuous second of foreground S_OK before capture; it failed to settle. In-capture acceptance was unchanged. Rejected; no media saved. | Showing the fixture overlay remains an unresolved focus/setup obstacle. Do not silently accept the overlay as equivalent foreground. Persist full failing setup history and owner/foreground HWND/PID transitions before another retry. |
| Independent fullscreen request, with no owned overlay | `SetFullscreenState` succeeded; queried state remained true, with foreground source samples. DDA video: 357/357 valid distinct source IDs over six seconds. NVIDIA: 529/529 valid IDs, 520 distinct, over an 8.805322-second container. All IDs belong to source submissions; full decoding and per-stream DTS ordering pass. | Supports fresh capture during this API-fullscreen workload only. No mode-transition continuity, native overlay visibility, latency, game or parity acceptance. |
| Presentation mode | Own-PID trace: 933 events on one swapchain; 920 `Hardware Composed: Independent Flip`, ten `Composed: Flip`, three `Composed: Copy with GPU GDI`; **zero Legacy Flip rows**. | API fullscreen true does not establish traditional exclusive fullscreen. Do not call this a genuine legacy exclusive pass. |

Both successful conditions ran with NVIDIA reporting Replay On, and their newly saved NVIDIA videos contained the controlled source. That is bounded simultaneous-recorder feasibility for these short fixtures. It does not qualify F10's future UI exclusion strategy or full live replay/save pipeline. Source ID gaps/duplicates are retained in the analyses; these are not loss-free picture-cadence results. No calibrated audio markers were generated, so decodable audio and ordered packets do not establish perceptual synchronization.

## Evidence and restoration

Exact plans, leased input hashes, source/build files, recordings, source-ID receipts and cleanup records stay under ignored artifacts:

- [Foreground qualification analysis](../../artifacts/foreground-check-20261025/analysis.json), [frozen plan](../../artifacts/foreground-check-20261025/frozen-plan.json), [restoration](../../artifacts/foreground-check-20261025/preservation-after.json).
- [Rejected overlay A](../../artifacts/coexistence-attended-20261025/failure.json) and [rejected overlay B](../../artifacts/coexistence-attended-20261025-b/failure.json). Their foreground gates and rejection records remain intact.
- [Independent fullscreen analysis](../../artifacts/fullscreen-attended-20261025/analysis.json), [mode analysis](../../artifacts/fullscreen-attended-20261025/mode-analysis.json), [fullscreen acknowledgement](../../artifacts/fullscreen-attended-20261025/fullscreen-ack.json), [frozen plan](../../artifacts/fullscreen-attended-20261025/frozen-plan.json), [restoration](../../artifacts/fullscreen-attended-20261025/preservation-after.json).

All four controllers stopped their owned source/capture/trace processes. The final NVIDIA state is Replay On, matching the user's explicit restoration choice. Saved settings, the F10 startup entry and pre-existing gallery file size/mtime metadata match each trial's baseline. Two new diagnostic replays remain in NVIDIA's gallery with hashed local copies. Microphone was required Off; vendor system-audio settings were preserved. Task-era rolling buffers were deliberately reset while the controlled source was visible. No user app was closed, no game/display/driver/security preference changed, no administrator elevation obtained, and no install, commit or publication occurred.

## Next work

Fix and qualify the test overlay's focus handling before another UI-affinity comparison. Keep the successful ordinary-monitor baseline and require fresh NVIDIA content on both sides; neither On status alone nor a historical protected-content result substitutes for that comparison. The current test's setup telemetry needs to preserve the failed settling samples rather than only the final rejection.

For genuine exclusive coverage, first identify and verify a workload/configuration that actually produces a legacy exclusive interval. Any temporary compatibility/game-setting change needs a specific preservation/restoration plan; this investigation did not change those settings. Then test fullscreen entry/exit, overlay visibility and source freshness, followed separately by admitted gaming performance and A/V trials. E2 and M0 remain open. The prior E1 controlled export-speed result is unchanged.

## Artifact retirement during this session

The user also requested substantial cleanup. [The cleanup report](../../artifacts/cleanup-20261025/REPORT.md) records 44,477 deletions, logical size reduced from about 261.3 to 18.6 GB and observed free-space recovery about 120.7 GB. Existing source/worktrees/reports, current capture evidence, selected reference media and pending review inputs remain; old repeated media/builds were explicitly retired. Historical links may be unavailable and original full-batch revalidation is no longer possible for deleted files. These retained findings are not freshly revalidated claims about retired outputs.

## Subsequent passive-popup correction

The [follow-up comparison](overlay-exclusion-followup-20261025.md) resolves the test fixture's focus defect through a matched negative/fixed control and verifies the correction in full capture. The popup's managed TopMost initialization was unnecessary for its owned-window ordering and took foreground. With it removed, ordinary and excluded monitor captures both remain fresh; exclusion removes the probe but stops NVIDIA Replay in this controlled comparison. Removing the probe and restarting Replay restores verified fresh footage. Global affinity therefore remains an unresolved coexistence constraint. A dedicated fullscreen apphost has been prepared, but its default trace still contains no Legacy Flip; a per-fixture compatibility-checkbox comparison is pending. These later findings do not rewrite the earlier A/B rejections or establish M0 acceptance.
