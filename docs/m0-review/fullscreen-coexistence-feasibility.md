# Fullscreen and NVIDIA coexistence feasibility

24 October 2026. Source HEAD `fd6fb316b102a8686377f841729469ca70435d09`, with the existing working-tree specification preserved. This is a finite controlled-fixture investigation. It is not game, production, performance or M0 acceptance.

## Result

**Selected-window capture produced fresh controlled pictures. Display capture and genuine exclusive fullscreen remain unqualified, and NVIDIA's On status did not establish usable recording.** No production changes were made.

| Check | Measured result | Meaning |
| --- | --- | --- |
| WGC targeting the owned source HWND, encoded with NVENC | 180/180 decoded frames had valid complementary/checksummed source IDs; all 180 IDs were distinct and present in the source submission log. Video duration 3.233333 seconds; packet DTS strictly increasing. | Limited fresh-content feasibility for this selected-window source. No completeness, steady 60 FPS or game claim. |
| Separate ordinary owned overlay during WGC | No magenta test pixels in its known 256x128 region across all 180 frames. | This specific separate window was absent from the selected-window clip without capture-exclusion affinity. This is not interactive-overlay or all-popup qualification. |
| Desktop Duplication, before fps filtering or encoding | Opened output 0 at 1920x1080, then delivered no frame to a one-frame null sink within 15 seconds. A separate fps-filtered probe also timed out. Earlier encoded attempt timed out after 40 seconds. | Acquisition-stage obstacle in this unattended environment. Not evidence that NVENC or the export prototype is slow. Cause remains unresolved. |
| Generated-source NVENC control | Encoded 60 frames and exited successfully in approximately 0.50 seconds of child wall time. | Encoder can function here; this control is not a capture or export-speed benchmark. |
| Fresh NVIDIA replay saved while ordinary probe was visible | File existed, stabilized, and fully decoded. Its five video frames had no valid source IDs and were entirely zero in full-resolution decoded grayscale. | Reject as useful capture/coexistence evidence, despite runtime Replay On and a successful save acknowledgement. Audio presence/ordering does not establish A/V sync. |
| Owned window requested `WDA_EXCLUDEFROMCAPTURE` | Set and readback succeeded with value 17. NVIDIA continued to report Replay On over four one-second-spaced polls. | Status observation only. The preceding NVIDIA clip was already black; no exclusion/coexistence pass or contradiction of historical protected-content findings follows. |
| Genuine exclusive fullscreen | Initial legacy D3D11 request returned `DXGI_ERROR_NOT_CURRENTLY_AVAILABLE`; later strict attempts could not obtain foreground ownership. Computer Use activation also failed. No valid exclusive presentation trace was obtained. | Not tested successfully. Borderless rendering and `GetFullscreenState=false` are not exclusive fullscreen. |

The source was visible, uncloaked and returning `S_OK` from Present during the final borderless trial, even though keyboard foreground remained elsewhere. That permitted a narrowly scoped window-content check, not relaxation of the exclusive-fullscreen gate. Full decode and source-picture checks were performed only after live collection stopped.

## Evidence and rejected attempts

The final [local report](../../artifacts/fullscreen-coexistence-20261024-f/report.md), [frozen plan](../../artifacts/fullscreen-coexistence-20261024-f/frozen-plan.json), [analysis](../../artifacts/fullscreen-coexistence-20261024-f/analysis.json), and [final restoration check](../../artifacts/fullscreen-coexistence-20261024-f/final-readonly-check.json) retain exact inputs and results. Machine-specific logs, binaries, source copies, NVIDIA extracts and recordings stay under ignored `artifacts/`.

Attempts A through E remain retained separately. A requested fullscreen too early and also timed out in DDA; B added strict raw-Present/foreground checks and rejected an occluded source; C obtained successful borderless Presents but no foreground; D allowed an explicit Computer Use activation stage, which failed with `foreground window did not report a process id`; E isolated borderless coexistence but stopped at the first encoded DDA timeout. F separated acquisition, filtering, encoding and independent selected-window/NVIDIA checks. None of the rejected attempts was renamed a pass. PresentMon could start/stop a scoped trace without administrator elevation, but the missing usable presentation CSV does not classify an exclusive interval.

The supporting [public API research](fullscreen-coexistence-api-research.md) describes documented capabilities and failure conditions separately from these measurements. In particular, public fullscreen support does not prove this live environment works, and a vendor On state is not a source-freshness oracle.

## Preservation

The user explicitly chose **restore the saved On setting** when the initial runtime Off state disagreed with the saved preference. NVIDIA Replay was left On and verified again after offline analysis. Other runtime fields, saved NVIDIA settings, the F10 startup entry and pre-existing recording path/size/modification metadata matched the baseline. Existing recordings were not decoded, overwritten or deleted. One new NVIDIA diagnostic replay remains in its normal gallery and an identical hashed copy is retained in the local evidence folder. Temporary task-created replay resets replaced rolling history; the initial rolling buffer was not recoverable.

All owned source, FFmpeg, PresentMon and Computer Use helper processes were stopped; the fixture's keep-awake request was balanced on exit. No game was launched, user application closed, display/driver preference changed, installed F10 modified, package installed, commit made or publication performed. No Windows administrator elevation was obtained or needed for the successful WGC/encoder checks.

## Next useful step

Do not repeat unattended exclusive-mode trials without changing the unmet foreground prerequisite. With the user at the PC, run a short controlled-source check after the user brings the fixture to the foreground, confirming fresh monitor and NVIDIA pictures before proceeding to mode transitions or capture-exclusion comparisons. Genuine FSE needs actual presentation-mode evidence; if event collection is incomplete, diagnose that route before a game batch. Keep the same content and preservation gates.

If display capture remains stalled while the source is foreground, instrument adapter/output identity and frame-acquisition HRESULTs/timeouts in a separate diagnostic harness. The current evidence localizes the failure before encoding but does not identify the driver, display power, session state or NVIDIA policy as its cause. Do not change security/driver settings or bypass protected content on that assumption.

For F10, retain selected-window capture as a viable path to investigate, with explicit source loss and no silent desktop fallback. The required default display capture, displayed stretching/scaling, genuine fullscreen overlay visibility, NVIDIA coexistence, application FPS, A/V alignment, recovery and gaming overhead are still open. The earlier controlled AAC export result is unchanged; E2 and M0 are not closed.
