# Experimental selected-window capture

This development branch adds an explicit **Application window** video source. Old
profiles retain monitor capture. No installation or default switch is implied.
`tests/window-capture.ps1` defaults to headless synthetic monitor compatibility;
`-Mode Window` records a controlled visible window and Windows default system audio
with an isolated production worker and UI. Coordinate exclusive desktop ownership
and obtain capture authorization before the live mode. Neither mode changes
startup. The live test closes only its owned fixture/UI/worker processes.

## Source and lifecycle contract

- Persist executable path and window class as application intent, never HWND, PID
  or process start time. The picker does not select a regex or a first match.
  Capture/reconnection requires exactly one matching visible top-level window.
  Multiple matching windows wait; inaccessible metadata also waits. F10's own
  executable is excluded. The UI picker is bounded; worker matching separately
  scans until ambiguity is established rather than truncating all applications.
- The worker validates HWND, PID, process start and window class. Native destroy
  notifications invalidate a lease across handle reuse. Validate again immediately
  before launch and after initialization. Generations fence retired sources.
- Minimize/loss clears current readiness, stops acquisition and retains completed
  footage for saving. Restoring/rebinding starts a fresh buffer; separate session
  timelines are never joined. Explicit pause/restart/apply retain their existing
  buffer-reset semantics. No desktop fallback, masking or hidden capture pause is
  used to omit UI. Legitimately static frames are not diagnosed as target failure.
- Output starts at the client dimensions rounded down to even pixels, then fits
  resizes with preserved aspect ratio in that initial canvas. Black bars from an
  aspect-ratio change are possible. A new session chooses new initial dimensions.
- Screenshots use the selected window in this mode and fail if it is unavailable.
  Whole-system audio still follows the Windows default endpoint. Clip folders
  follow the selected executable, independently of foreground F10 controls.
- Window-mode UI uses ordinary affinity-zero surfaces. Monitor mode retains its
  experimental exclusion policy and unresolved NVIDIA coexistence gate. The app
  never changes NVIDIA settings as a workaround. Protocol version 2 rejects v1
  peers; stop an older build before reusing the same profile. Existing profile
  ownership and current-user pipe restrictions remain.

## Clock boundary and acceptance

The assessed FFmpeg is Gyan git **7fd2be97b9**, not an inferred numbered release.
Its matching `libavfilter/vsrc_gfxcapture_winrt.cpp` reads SystemRelativeTime in a
1/10,000,000 timebase and subtracts the first timestamp. The adapter preserves
relative deltas and re-anchors once at downstream first-frame delivery using the
existing RTC bridge. **That delivery anchor does not prove the first acquisition
epoch or A/V alignment.** The first-frame delay remains unknown. This path is
experimental until coded cold/warm A/V markers and drift are qualified. The
monitor acquisition policy, 100 ms audio cushion and PCM acquisition timestamps
are unchanged. Exact graph and experimental delivery-anchor diagnostics are logged.

Required independent gates:

1. Portable configuration/status rules and rejection of ambiguous/missing source;
   legacy/shared-source builds; protocol, restart and persisted pause checks.
2. Real worker source loss/minimize/restore/resize, retained saves, UI detachment,
   source recreation/handle reuse and save/apply/cancel transition races.
3. Full decode; exact per-track missing/equal/decreasing DTS and decoded PTS;
   underlying client pixels beneath visible separate F10 surfaces. Preserve
   rejected attempts. A valid short clip is not a full requested replay.
4. Coded A/V alignment/drift and full default five-minute retention/export;
   screenshots of live targets, device switches, process/UI restart and long runs.
5. Actual CS2 gameplay with real F10 controls: keyboard, Escape, focus return,
   settings changes and continuity; classify presentation mode. Borderless fixture
   results do not prove true exclusive fullscreen, game performance or HDR.
6. User-coordinated sign-in/reboot acceptance, preserving deliberate pause.

Local machine-specific reports and third-party source extracts remain in ignored
`artifacts/`. Binary release and installation acceptance remain incomplete until
clean-footage/coexistence and applicable acceptance gates are met. Early prototype
source integration does not assert those gates passed; see
[prototype status](../docs/prototype-status.md).
