# Overlay fixture correction and exclusion compatibility

25 October 2026. Follow-up to the [attended checks](attended-capture-20261025.md). This changes disposable test fixtures only; production F10, installed/startup state and accepted capture intent are unchanged.

## Result

**The passive test-popup focus defect is fixed and verified in the full capture fixture. Public window exclusion works for the tested monitor capture, but stopped NVIDIA Instant Replay in this controlled environment. Removing the excluded window and restarting Replay restored fresh NVIDIA footage.** This is one bounded ordinary/excluded/recovery comparison, not a universal Windows/driver claim or a finished coexistence solution.

### Diagnosis and matched regression

The previous failures occurred before useful media collection. A five-second two-window harness reproduced them with no GPU source, FFmpeg, NVIDIA control or PresentMon. It logs the actual foreground HWND/PID, owned popup handle, visibility and activation events. A later version avoided creating handles merely to inspect telemetry; the same failure remained.

The popup requested no activation, but its managed `TopMost=true` initialization activated it while creating its window. Removing only that managed initialization retained ownership and the no-activation settings. An owned window stays above its topmost owner; a separate managed TopMost assignment was unnecessary for this passive probe. The [pinned .NET 10.0.12 Form source](https://github.com/dotnet/winforms/blob/v10.0.12/src/System.Windows.Forms/System/Windows/Forms/Form.cs#L1901) sets topmost position without `SWP_NOACTIVATE`, and re-applies it during handle creation. [Microsoft documents](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowpos) the activation effect and owned-window ordering. These sources corroborate the measured cause; no native call-stack trace was collected.

Same compiled minimal C binary, fresh trials:

| Condition | Main stays foreground | Popup visible | Outcome |
| --- | --- | --- | --- |
| Original managed TopMost negative control | 0/44 | 44/44 | Reproduces defect; popup takes foreground in all 44 samples. |
| Corrected owned-popup path | 44/44 | 44/44 | Pass. |
| Corrected confirmation after negative control | 45/45 | 45/45 | Pass. |

Native no-activation ShowWindow by itself did not correct the original TopMost path. A disabled-Form alternative was rejected by WinForms before display and is not a passing candidate. Initial telemetry that eagerly created the popup handle was corrected in a fresh diagnostic root; its attempts remain retained and are not used as independent acceptance evidence. No input injection or forced focus restoration was added to the fix.

Minimal reproduction: `artifacts/overlay-focus-20261025-c/run.py original <fresh-trial>` fails; the same command with `no-topmost` passes. Each trial stores source/binary hashes, raw activation samples, result and confirmed process stop. These are visible owned-window checks, not headless portable tests.

## Corrected full capture comparison

The [frozen C plan](../../artifacts/coexistence-attended-20261025-c/frozen-plan.json) retains the original strict source foreground gate. Its setup and in-capture samples confirm main foreground for both six-second acquisitions. Failure setup samples now persist before rejection. No traditional fullscreen request is part of this comparison.

| Phase | Content validation | Overlay / NVIDIA result |
| --- | --- | --- |
| Ordinary popup, affinity 0 | Monitor: 351/351 valid source IDs, all distinct. NVIDIA: 588/588 valid IDs, 571 distinct. | Magenta probe occupies the full known region in every monitor frame. NVIDIA baseline also contains the visible probe during its phase. Replay was On and produced fresh pictures before exclusion. |
| Same owned-popup geometry, affinity 17 | Set/readback succeeded. Monitor: 350/350 valid source IDs, 349 distinct. | Zero magenta probe pixels in the recorded region; 333/333 non-stripe source-color samples under that region match the expected source color ordering. NVIDIA reports Replay Off after the six-second capture. No excluded-phase NVIDIA save was attempted while Off. |
| Probe removed, Replay restarted | NVIDIA: 471/471 valid source IDs, 454 distinct. | Every accepted recovery picture belongs to a source submission after the hide acknowledgement. The clip is fresh, not an On-status-only recovery claim. |

All four saved clips fully decode; packet DTS is present and strictly increasing for all streams. The two monitor files are six seconds; NVIDIA containers are approximately 9.797289 and 7.847356 seconds. Source membership, source-color ordering and packet checks establish different properties; no loss-free cadence, calibrated color fidelity, audio-marker alignment, overhead, request latency, game, product or release acceptance follows.

Evidence: [content analysis](../../artifacts/coexistence-attended-20261025-c/analysis.json), [underlying-region/recovery audit](../../artifacts/coexistence-attended-20261025-c/phase-audit.json), [native affinity events](../../artifacts/coexistence-attended-20261025-c/source-exclusive-request/events.jsonl), [NVIDIA Off status](../../artifacts/coexistence-attended-20261025-c/excluded-status.json), [final preservation](../../artifacts/coexistence-attended-20261025-c/preservation-after.json). New media remains ignored with original/copy hashes. Earlier rejected A/B trials remain unchanged.

## Product implication

The public global window-affinity method must not be described as clean coexistence with NVIDIA desktop Replay on the tested setup. Keep this as an unresolved architecture constraint. Further work must establish a capture-path-specific approach or an explicitly accepted compatibility policy. Do not silently change display intent to selected-window capture, stop another recorder as a production workaround, bypass protected-content policy, or mark the overlay decision implemented from this passive test. An interactive game overlay's input/focus behavior is separate from the passive probe corrected here.

## Exclusive-fullscreen preparation

A dedicated disposable `F10FullscreenProbe.exe` now runs from the existing pinned runtime through child-only runtime-root environment variables. It requires no installation or system runtime change. A mode-only baseline collected 514 events, including 507 Independent Flip events and **zero Legacy Flip**; foreground, progressing successful Presents and API fullscreen state were qualified. No capture or NVIDIA control occurred in that baseline.

The remaining qualification route uses Microsoft's [per-executable fullscreen-optimization control](https://devblogs.microsoft.com/directx/demystifying-full-screen-optimizations/), applied only to this disposable executable. Original per-user state is absent and recorded in [the compatibility snapshot](../../artifacts/exclusive-probe-20261025/compatibility-original.json). Properties were opened and the user was asked to enable the checkbox. At this report's initial close, the change had not been applied and the adjusted-mode trial had not run. `compatibility.py` can arm the exact user-applied value and restore only that entry with compare-before-write protection; it does not change games, the shared runtime, global settings or another application's entry. This preparation is not proof that disabling the feature will produce Legacy Flip on this machine.

## Preservation and storage

The full C controller stopped owned source/capture/trace processes. NVIDIA Replay was restored On, with saved NVIDIA settings, F10 startup and existing media size/mtime unchanged. Microphone remained Off; no user app, game, driver or security preference was changed. The focus-only and mode-only probes made no NVIDIA control calls. No installation, production edit, commit or publication occurred.

These new diagnostic roots total about **16.7 MB** at closure, including builds and retained clips. The prior 120.7 GB disk cleanup remains effective; no repeated export batch or multi-gigabyte media set was regenerated. E2 and M0 remain open.

## Subsequent exclusive-mode results

The user completed the per-file checkbox step. The [exclusive follow-up](exclusive-fullscreen-followup-20261025.md) now establishes a reproducible controlled Legacy Flip workload. Both pinned FFmpeg DDA source-integration routes fail before their first packet; NVIDIA independently saves510 verified fresh pictures. An SDK-verified native probe observes DXGI access loss after successful acquire/release pairs, pointing toward a bounded duplication-recovery investigation without establishing the original FFmpeg HRESULT or cause. The first interrupted status-reader trial and an incorrect initial COM probe remain explicitly rejected. All temporary compatibility changes were restored, NVIDIA remains On, and cumulative new evidence totals about24.3MB. The global-affinity coexistence constraint above remains unresolved.
