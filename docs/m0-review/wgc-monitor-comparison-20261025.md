# Monitor WGC comparison and investigation deferral

25 October 2026. The maintainer authorized one bounded next comparison after the [capture-recovery prototype](capture-recovery-20261025.md), with explicit direction to defer deeper investigation if it did not materially unblock capture. That stopping condition is met. **No further live capture trial is planned in this round.**

## Outcome

**Monitor-targeted Windows Graphics Capture works for the controlled windowed source, but stalls during qualified traditional exclusive fullscreen.** It does not supply a viable fullscreen route yet. The unresolved capture/recovery work is now tracked in [GitHub issue #9](https://github.com/ju57/f10-recorder/issues/9) for later spec/investigation work. This is a deferred engineering dependency, not an accepted backend or a reduced release requirement.

| Check | Measured evidence | Conclusion |
| --- | --- | --- |
| Exact target | Primary `HMONITOR` resolved from the owned source, validated as `\\.\DISPLAY2`, 1920×1080, primary flag set. Source and monitor identity retained. | Actual monitor capture, not selected-window capture or an assumed DXGI/Windows enumeration match. |
| Six-second windowed control | 171/171 decoded pictures have valid complementary source IDs/checksums, all distinct and strictly increasing, all present in actual source submissions. Media picture span 5.984362 s. Full decode and integer PTS/DTS ordering pass. | Useful fresh monitor WGC footage in this controlled windowed case. |
| Windowed relative picture timing | First-picture-normalized media intervals differ from identified source Present-return intervals by −11.334 to +23.3201 ms; maximum absolute error 23.3201 ms, within frozen 50 ms bound. | Relative source-picture timing passes. This does not establish absolute acquisition latency or A/V synchronization. |
| Filter-to-MP4 timing | Maximum difference in relative timestamps is 0.0005 ms (0.5 µs), within the frozen 1 ms check. | The MP4 preserves the emitted filter timeline. |
| Windowed presentation | 378 Composed GPU GDI events within the decoded source-picture span; 423 during the whole engine interval. 63 source samples remain foreground/S_OK and progressing. | Composed-source success; not exclusive-mode evidence. |
| Fullscreen source before capture | 68 Legacy Flip events between fullscreen acknowledgement and capture launch. | Traditional-exclusive source context established before starting WGC. |
| Fullscreen attempt | WGC/showinfo reports two filter frames at relative times 0 and 37.5357 ms, then no further filter-frame progress. Engine progress stays at zero completed output frames. Fourteen-second watchdog stops the owned engine. File is an unfinalized 48-byte MP4, not playable evidence. | Failed sustained capture. The two reported surfaces are not decoded source-ID evidence; no freshness claim is made for them. Zero encoded output does not isolate an encoder cause because upstream delivery also stops. |
| Fullscreen continuity during failure | 917 process-scoped Legacy Flip events span 14.4111405 s. 139 source samples remain foreground/API-fullscreen/S_OK and Present counts advance. | Failure was observed with a progressing traditional-exclusive source, not inferred merely from a fullscreen request or the size of the window. |

The combined comparison verdict is **failed**. There was useful diagnostic discrimination and a passing monitor control, but the accepted fullscreen capability remains blocked. NVIDIA Replay remained On; there was no matched Replay Off comparison and no attribution of causality to NVIDIA, the driver, WGC or encoder internals.

## Frozen method and limits

The same dedicated source, selected monitor, approved engine and encoder pipeline were used for a windowed control followed by one fullscreen attempt. Monitor acquisition was explicit:

```text
gfxcapture=hmonitor=<validated source-monitor handle>:max_framerate=30:capture_cursor=0:display_border=0,showinfo=checksum=0
```

Both used H264 NVENC, no audio/B frames, passthrough timestamps, a microsecond MP4 time base and a six-second output limit. No CFR/FPS duplication stage, affinity change, source substitution, engine replacement or production edit was introduced. A wall-clock watchdog bounds a stalled media clock. PresentMon is scoped to the owned source PID and a unique session. Exact commands, source/build/tool hashes, source samples and acknowledgements are retained in the prospective plan and receipts.

The [primary-source API research](wgc-monitor-api-20261025.md) establishes that this pinned filter subtracts the first WGC `SystemRelativeTime` before emission. Controller launch time is not substituted for that missing acquisition epoch. Matching decoded source IDs and their relative intervals can detect changing staleness; a constant entire-clip offset remains a limitation of that comparison. The source's first/last IDs also lie within the bounded capture window, but that is not an absolute per-picture latency measurement.

The first offline validator rejected the windowed control because it compared all showinfo frames with the output packet count. The fixed `-t 6` command deliberately excludes a logged frame at 6.0176725 s: 172 upstream reports yield 171 in-range output pictures. The retained offline v2 validator compares the complete pre-cutoff sequence, without changing any capture command, threshold, media or exclusive failure. Original validator/analysis remain retained; there was no additional live trial. The final comparison remains failed.

## Deferred investigation for the spec

[Issue #9](https://github.com/ju57/f10-recorder/issues/9) contains the problem, previous failed approaches, reproduction inputs, evidence locations, unresolved mechanisms, completion criteria and ownership. It includes the stale-picture problem from the duplication recovery prototype and this separate WGC stall. The separate global-affinity/NVIDIA incompatibility remains visible; one acquisition success would not resolve clean UI coexistence.

Next investigation must name a new discriminating hypothesis and observation method before collecting more footage. Potential future instrumentation must preserve actual source identity, native frame lifetime and raw acquisition timestamps. A fixture-only barcode discard rule, increasing PTS, a recreated interface, or a decodable black clip cannot close the issue. Additional tool/privilege/vendor scope must be concrete before execution; this deferral installs nothing and attaches to no vendor process.

Accepted Q41/Q52/Q57/Q157 source-loss, displayed-presentation, recovery and fixed-canvas rules remain. There is no silent window-to-monitor fallback, borderless substitution or release waiver. Real-game cadence, live A/V, overhead, resource budgets, two-hour soak and the supported hardware matrix remain separate qualification work.

The maintainer's latest direction permits deferring deeper investigation and returning to the release-plan conversation. **Product/visual decisions through Q157 remain accepted; engineering feasibility remains open.** A consolidated spec can reference the issue and the ADRs without pretending the capture problem is solved. Whether that tracked-dependency approach is sufficient for M0 planning sign-off is the next explicit planning question; this report does not mark M0 complete.

## Evidence and restoration

- [Frozen plan and identities](../../artifacts/wgc-monitor-20261025-a/plan.json), [monitor identity](../../artifacts/wgc-monitor-20261025-a/monitor.json), [collection receipt](../../artifacts/wgc-monitor-20261025-a/collection-stopped.json).
- [Final independent analysis](../../artifacts/wgc-monitor-20261025-a/analysis-v2.json), [original analysis retained](../../artifacts/wgc-monitor-20261025-a/analysis.json), [corrected offline validator](../../artifacts/wgc-monitor-20261025-a/validate-v2.py).
- [Windowed video](../../artifacts/wgc-monitor-20261025-a/windowed/recording.mp4), [source IDs](../../artifacts/wgc-monitor-20261025-a/windowed/decoded-ids.json), [packet inventory](../../artifacts/wgc-monitor-20261025-a/windowed/probe.json).
- [Fullscreen engine log](../../artifacts/wgc-monitor-20261025-a/fullscreen/engine.log), [source telemetry](../../artifacts/wgc-monitor-20261025-a/fullscreen/source-samples.json), [raw presentation trace](../../artifacts/wgc-monitor-20261025-a/mode-presentmon.csv), [source submission log](../../artifacts/wgc-monitor-20261025-a/source/dxgi-present.csv).
- [Read-only closure verification](../../artifacts/wgc-monitor-20261025-a/closure.json), [own-exe compatibility restoration](../../artifacts/wgc-monitor-20261025-a/compatibility-restored.json).

NVIDIA's complete sampled runtime state and original settings match the baseline, including Replay On. Startup and old gallery MP4 size/mtime metadata match. The guarded wrapper restored the dedicated test executable's original absent compatibility value. All four task-owned source/trace/engine PIDs are stopped; frozen identities still match. No existing media was deleted, and no installed recorder, global graphics configuration or game setting was changed. Raw footage and machine-specific evidence remain ignored locally; issue #9 contains a bounded summary, not an upload of those artifacts. No production code, install, commit, push or merge occurred.

The new evidence root occupies approximately **3.2 MB**, including its passing windowed control and failed fullscreen output. [Environment metadata](../../artifacts/wgc-monitor-20261025-a/environment.json) records current OS, GPU/driver and source commit; the frozen plan identifies the exact working-tree fixture and engine bytes.

## Planning follow-up: Q158 accepted

Accepted by the maintainer on 25 October 2026: M0 planning may close once the consolidated spec records accepted behavior/ADRs and assigns each unresolved engineering dependency an owner and clear pass conditions. Every accepted beta requirement remains mandatory before release. This separates planning sign-off from implementation/qualification. The [consolidated spec](../beta-spec.md) carries the owned release-gate register; issue #9 remains open. The maintainer also confirmed testing at the existing recorder/worker boundary plus actual saved-media inspection, focused core checks and separate hardware qualification. This follow-up changes no failed trial verdict.
