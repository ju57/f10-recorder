# Recording profile migration checks

Issue [#12](https://github.com/ju57/f10-recorder/issues/12) contributes to
G-MIGRATE/G-SEC; these checks close neither gate.

## Format and recovery

Settings and `worker-state.json` use integer `SchemaVersion: 1`. No version means
legacy version 0; unsupported/null/duplicate versions are incompatible. Loads do
not write. The worker acquires protected profile ownership before loading.
`ProfileStatus` exposes `new-profile`, `migration-required`, `current`,
`correction-required`, `incompatible` or `interrupted`, plus existing-user,
correction, write-admission and activation results. Invalid fields request only
necessary corrections and retain valid siblings. Effective paused state includes
a migration hold/stopped intent; it does not manufacture a stored user pause.
Paused/held launch constructs no engine and clears no retained cache inputs.

Explicit compatible correction/Resume commits settings and state. Correction
retains pause/stopped intent and an activation hold; Resume clears it deliberately.
Incompatible/interrupted profiles cannot be overwritten through ordinary
Apply/Resume. Existing-user Overview routing and new-user setup/Finish remain
unimplemented shell work; current Settings feedback does not complete those flows.

Before replacement, exact original bytes are retained as
`<document>.original-<SHA256>`. Backup creation/mismatch failures reject before
changing originals. Both encoded documents must fit the reader's 1 MiB bound
before a paired commit starts. JSON escaping can expand valid Unicode input;
rejection preserves opaque data rather than dropping it. Backups are not pruned.

A paired commit flushes `profile-migration.json`, replaces each document through
an exclusive `.tmp` file, then removes the journal. A leftover journal/temporary
path blocks activation and automatic persistence. There is no automatic recovery
or inferred generation precedence: stop F10, inspect retained originals/journal,
restore a compatible pair, then remove reviewed incomplete paths. A newer schema
requires a compatible application. This mechanism does not qualify power-loss,
filesystem durability or installer rollback across the shipping matrix.

## Represented-field inventory

Defaults apply only to absent fields; explicit null is validated separately.

| Field | Missing default | Valid explicit values |
| --- | --- | --- |
| ReplayMinutes | 5 | Integer 1–30; zero invalid |
| FramesPerSecond / BitrateMbps | 60 / 20 | 24/30/60/120 FPS; integer 5–100 Mbps |
| VideoCodec | h264_nvenc | H.264/HEVC/AV1 NVENC identifiers; unavailable choice stays intent |
| SystemAudio / DisplayIndex | true / -1 | Boolean false retained; display indices -1–31 include zero |
| CaptureMode | monitor | monitor/window; invalid/unknown/null fails closed to window correction |
| WindowExecutable / WindowClass | empty | Absolute normalized executable path or empty; bounded class or empty; absence of source does not erase identity |
| OutputDirectory / ScreenshotDirectory | Videos/F10 Recorder / Pictures/F10 Recorder | Nonempty resolvable paths; no media moves |
| SaveKey / SaveModifiers | F10 / none | Existing F1–F12, modifier bits 0–7 including zero |
| OverlayKey / OverlayModifiers | Z / Alt | Existing letters/F1–F12, modifier bits 1–7 |
| ScreenshotKey / ScreenshotModifiers | F1 / Alt | Existing letters/F1–F12, modifier bits 1–7 |
| Shortcut combinations | Distinct | Collision requests correction; valid existing bindings survive |
| State Paused / UserStopped | false / false | Booleans independently retained; null/invalid state blocks writing |
| State ActivationRequired | true | Missing intent cannot activate capture; explicit current false retained |

Unknown preferences/state remain opaque JSON, including device identity, mute and
zero values. This does not implement/apply named audio devices, microphone,
levels/tracks, stable display identities, resolution/save-mode/cache/HUD or
manual/setup/shell fields. Migration never writes external startup preferences.
Obsolete `FfmpegPath` is excluded from current settings but remains in the exact
original; it never controls engine trust.

## Commands and effects

Portable checks need no Windows/GPU/engine:

```powershell
dotnet restore projects/F10Recorder.CoreChecks/F10Recorder.CoreChecks.csproj --locked-mode
dotnet run --project projects/F10Recorder.CoreChecks -c Release --no-restore -- artifacts/profile-core-unique --profile-only
dotnet run --project projects/F10Recorder.CoreChecks -c Release --no-build -- artifacts/profile-control-unique --profile-only --profile-sensitivity
```

The control must return 1 with the HEVC/valid-sibling preservation failure. It
mutates one fixture codec while retaining the literal oracle, not production.
Full CoreChecks also runs migration and the existing portable rules. Use unique
output directories; interrupted/write-failure evidence remains retained.

Preview the actual worker/offscreen routes, then omit `-Plan`:

```powershell
./tests/profile-migration.ps1 -Plan
./tests/profile-migration.ps1 -Plan -DesktopOnly -SnapshotsPath <worker-output>/worker-snapshots.json
```

`-Dotnet`, `-BuildDirectory`, `-DataDirectory` and `-NoBuild` support isolated SDK,
outputs and fixtures. Locked restores may contact NuGet/audit services. Worker
fixtures need an ordinary desktop token; a restricted sandbox can prevent
shortcut/ownership/pipe admission.

The worker route uses isolated hidden Ctrl+Alt+Shift+F4–F6 shortcuts, bounded
protocol calls and task-owned hidden-window Save/Screenshot messages, with no
physical keyboard input. It covers legacy, current paused/stopped, partial-invalid,
future settings/state, malformed/interrupted state, reconnect and competing-owner
rejection. Workers use an isolated missing-engine root and stay paused/blocked:
no visible/capture-excluded UI, encoder/GPU, pixels/audio/mic, startup writes,
downloads or installed-recorder mutation. Reports retain process IDs, input and
media-sentinel hashes and owned-worker exit.

Desktop consumes actual worker snapshots through the production client and
offscreen Settings controls, checks stale-worker rejection, and renders correction
text. A controlled capability response tests no silent codec substitution. No
platform Window, affinity, GPU, engine probe or live device is used. This verifies
controls/feedback, not native window/focus/accessibility/DPI acceptance.

File sentinels establish byte preservation, not playable-media/A/V acceptance.
No genuine cross-account/session/reparse-alias trial, live acquisition, saved-video
decode, installer rollback, new setup or final Overview acceptance is claimed.
All release gates, including the unresolved #9 investigation, remain open.
