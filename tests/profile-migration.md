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

Worker status sends a separate copy of represented settings, plus
`RetainedPreferenceCount`. Opaque preference values remain worker-owned and never
consume the reply envelope. The desktop's correction request still merges with
the worker's retained preferences before persistence. Disk serialization retains
opaque data; the framed protocol keeps its 1 MiB limit. A raw document admitted
at load can still require correction before canonical persistence (for example,
Unicode escaping can expand it); that rejection must remain bounded/actionable
and preserve originals without activating capture.

After an authenticated capability response, a partly invalid activation-held
profile can retain its valid unavailable saved codec while correcting another
field. That item is labelled a saved unavailable preference, never an initialized
encoder. Apply keeps activation held. Ordinary active/current-profile codec
selection still requires an available choice; empty/stale/missing capability
responses do not enable Apply. No GPU compatibility is inferred from preservation.

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
./tests/profile-migration.ps1 -Plan -StatusTransportOnly
```

`-Dotnet`, `-BuildDirectory`, `-DataDirectory` and `-NoBuild` support isolated SDK,
outputs and fixtures. Locked restores may contact NuGet/audit services. Worker
fixtures need an ordinary desktop token; a restricted sandbox can prevent
shortcut/ownership/pipe admission.

The worker route uses isolated hidden Ctrl+Alt+Shift+F4–F6 shortcuts, bounded
protocol calls and task-owned hidden-window Save/Screenshot messages, with no
physical keyboard input. It covers legacy, current paused/stopped, partial-invalid,
future settings/state, malformed/interrupted state, reconnect and competing-owner
rejection. It also covers small opaque preferences, literal Unicode expansion and
near-limit ASCII documents whose full status would exceed the reply envelope.
Workers use an isolated missing-engine root and stay paused/blocked:
no visible/capture-excluded UI, encoder/GPU, pixels/audio/mic, startup writes,
downloads or installed-recorder mutation. Reports retain process IDs, input and
media-sentinel hashes and owned-worker exit.

The paused-window regression uses an impossible fixture executable/class and
checks its identity-specific unavailable result, without creating a source window
or acquiring pixels. It verifies target resolution survives deliberate pause;
successful screenshot pixel output remains outside this route's acceptance.

Desktop consumes all eleven actual worker snapshots through the production client and
offscreen Settings controls, checks stale-worker rejection, and renders correction
text. It also consumes the actual bounded canonical-write rejection through the
client notification path. A controlled capability response tests no silent codec substitution. No
platform Window, affinity, GPU, engine probe or live device is used. This verifies
controls/feedback, not native window/focus/accessibility/DPI acceptance.

`-StatusTransportOnly` is the maintained Windows CI route. It exercises the
production snapshot projection, framed Write/Read and headless worker client with
small, Unicode and near-limit ASCII inputs. It checks reconnect, source-object
and disk preservation and the opaque-count receipt. Its unprojected oversized
control must still throw before any header/body bytes are written; it does not
make a manually oversized reply valid. This route creates only isolated local
files: no native recorder, window, shortcut, named pipe, engine, GPU or capture.
The real worker route separately protects Snapshot wiring, ownership and actual
pipe/client behavior.

File sentinels establish byte preservation, not playable-media/A/V acceptance.
No genuine cross-account/session/reparse-alias trial, live acquisition, saved-video
decode, installer rollback, new setup or final Overview acceptance is claimed.
All release gates, including the unresolved #9 investigation, remain open.

## Profile-aware editor entry

The current Settings editor distinguishes existing/new/correction context from
incompatible, interrupted or unavailable profile presentation. This is the
existing editor and a read-only compatibility view, not completed Setup or
Overview. The generic Review preferences action selects Capture only on explicit
action; it does not infer field navigation from diagnostic text. Supplied
correction/compatibility reasons remain visible. Unknown, null or inconsistent
status fails closed without treating missing information as a new profile.
Individually valid colliding shortcut choices remain editable on a real
correction-required profile; ordinary-valid claims still require central
Settings validation on a throwaway copy, without normalizing the source config.
A blocked profile's main recording header reports unavailable status instead of
repeating untrusted active/readiness/save hints; underlying worker intent and
telemetry are left unchanged.

One persistent panel retains draft values, selected tab and last-known attention
context across polls/reconnect/replacement. Entry/status updates do not apply,
resume, probe, write preferences or open a new window. Unsafe visible actions
and their invoked handlers are blocked. A genuinely missing initial config is
hidden/read-only until the first credible profile can initialize the editor;
existing drafts are never reloaded from subsequent snapshots. Codec draft intent
is separate from the latest engine capability admission: disconnect clears stale
choices/Apply readiness, explicit recheck restores an available draft choice, and
an unavailable draft is not silently replaced or labelled initialized.

The existing attachment handler calls the one-time EnterAsync path. New,
migrating, correction, activation-held, incompatible/interrupted and unavailable
entry suppress its automatic engine probe. Returning trusted status does not
launch that suppressed probe later. The legacy ordinary current compatible
non-held editor retains its single automatic attachment check; tests observe it
through controlled replies, not actual GPU/engine execution. Explicit checks
remain user actions subject to profile admission.

Preview the maintained offscreen route with
`./tests/profile-migration.ps1 -EntryOnly -Plan`. It runs the production controls
and worker client against controlled snapshots, with no native Window/App,
worker, pipe, engine, GPU, capture, startup write or vendor changes. The direct
MigrationTests `--profile-entry-ui --sensitivity-control` route changes only the
positive existing-profile fixture into a consistent new profile while retaining
the literal existing presentation assertion; exactly that check must fail.
The method used by the attachment handler is exercised directly offscreen;
actual native attachment/focus/visual-root behavior remains separate acceptance.

Windows CI restores the locked, audited MigrationTests dependency graph, builds
Release with ContinuousIntegrationBuild, then runs only the early
`--profile-entry-ui` route and its `--sensitivity-control` variant. Each CI
invocation uses a fresh GUID output root with separate normal/sensitivity reports.
`tests/verify-profile-entry-report.ps1` checks both process exit and JSON: normal
requires exit 0, result passed, boolean sensitivityControl false and exactly 27
passing checks; sensitivity requires exit 1, result failed, boolean true and
exactly 27 checks with only `existing-context-fixture-sensitivity` failed.
Missing/malformed reports, other failures and unexpected exits fail the job.
The JSON root and each check must be objects; singleton arrays are rejected
without relying on PowerShell's collection/member enumeration.

For bounded local verification, the same locked/audited Release build may use an
isolated `--artifacts-path`, `CopyLocalLockFileAssemblies=false` and
`CopyDebugSymbolFilesFromPackages=false`, then stage hash-verified managed and
win-x64 native assets from the unchanged locked package cache. Invoke the built
MigrationTests DLL with the pinned dotnet host and the same early-route arguments
and report validator. This avoids copying every runtime's native assets locally;
it reproduces the suite/report contract, not the hosted default copy/build path.
Hosted execution and its platform/runtime availability still need CI on the PR.

Attention text/border examples are rendered offscreen. The non-rooted fixture
does not qualify the real Fluent button appearance/geometry; content,
accessible name and explicit click behavior are checked separately. Current
native palette/type and full branded shell migration remain distinct work.
The historical eleven-worker-input Desktop route remains a separate regression;
one manual correction fixture now explicitly uses the actual correction-required
outcome rather than an inconsistent current outcome with correction flags.

Full Setup/Overview, named devices/audio, native usability/accessibility/DPI and
installed upgrade acceptance remain pending. Issue #12 and all gates stay open.

## Real paused-worker corrections

`-RealWorkerCorrection` exercises production WorkerRecorder.ApplyAsync over the
current-user named pipe to an actual worker, then reads the real persisted
settings/state pair and original backups. Supply an existing approved engine
and a separate task-only shared private engine root explicitly:

```powershell
./tests/profile-migration.ps1 -RealWorkerCorrection -EngineSource <retained-approved-ffmpeg.exe> -EngineRoot <isolated-shared-engine-root> -DataDirectory <isolated-output> -Plan
```

Review the plan, then omit `-Plan` for an authorized native run. `-NoBuild`,
`-BuildDirectory` and `-Dotnet` retain their existing meanings. The route copies
at most one pinned engine into the private task root using TrustedStorage rules;
subsequent runs verify/reuse that copy. Source/copy hashes are recorded. Only
hash/lease verification occurs: it does not execute/probe the engine, download,
repair or install anything. Never point the task root at installed engine storage.

Seven serial scenarios cover independent stored pause/stopped intent, one FPS
correction with valid HEVC/false/zero/folder/shortcut/opaque siblings, actual
correlated completion, paired persistence, exact original backups, same-worker
reconnect and normal Quit/reopen. Controls cover a held settings-file read lease,
future settings, an interrupted journal, a mismatched original-state backup and
failure of the supplied startup callback after a completed commit. The latter
must still roll back prior valid settings. A failure before the commit completes
must not rewrite raw originals using salvaged/defaulted values as a rollback.
Interrupted journal/temp files and rejected attempts remain retained for review.

Every child starts with `paused:true`, an activation-held profile, a nonexistent
selected application and explicit isolated ownership/engine roots. The parent
allows only status, Apply and Quit; reconnect uses a deliberately invalid fallback
executable so unexpected fixture death cannot start a generic worker. The real
Windows startup value and kind are read/compared, while the worker's existing
startup callback writes only fixture intent. No Resume, restart, save, screenshot,
probe, repair, capture, GPU encoding or visible/capture-excluded UI is exercised.
Hidden task-owned Ctrl+Alt+Shift+F4-F6 shortcuts can conflict; a conflict fails
without stopping or reconfiguring its owner. Cleanup requires normal owned-worker
Quit; fallback termination is recorded as failed cleanup.

This supplies paused real-worker correction/persistence evidence. Media files
are preservation sentinels, not playable results. Full setup/Overview, named
device behavior, native accessibility/focus/DPI, live capture/A/V, cross-account
qualification and shipping upgrade/power-loss durability remain pending. Issue
#12 and all mandatory release gates remain open.

## Correction acknowledgements

### Budget-reduction preflight (P05)

A numerical decrease of `ReplayCacheBudgetBytes` checks complete current cache
allocations and pending copy obligations before engine verification, shortcut
registration, transaction notifications, capture disposal, persistence or startup
callbacks. Active engines use their synchronized replay module. Paused/held
profiles with no engine use the same read-only accounting; an absent cache is
accepted only after its readable parent confirms the entry is absent. Unknown,
denied, inconsistent or overflowing observations reject rather than implying
zero usage. Preflight neither changes the current budget nor prunes footage.
Existing profile/validation/busy guards remain; unchanged/increased budgets do
not acquire this reduction-only blocker. This is finite demand admission, not a
calibrated acquisition-growth or free-space guarantee.

The existing actual-worker route has a focused selector:

```powershell
./tests/profile-migration.ps1 -RealWorkerCorrection -BudgetApplyOnly -EngineSource <approved-task-engine> -EngineRoot <existing-private-task-engine-root> -DataDirectory <task-budget-fixture> -BuildDirectory <task-build> -NoBuild -Plan
```

Save/review that concrete plan before an authorized run. The engine root must
already contain the approved engine: this selector verifies/reuses it read-only
and cannot provision it. One deterministic, normally written 45,000,000-byte
payload is retained and reused across invocations; its actual allocation must
exceed the prospectively declared 40,000,000-byte proposal. A 1-minute/5-Mbps
profile keeps that proposal above the 37,500,000-byte nominal video floor.
Tiny controlled metadata is not reported as real worker allocation evidence.

The fixed held profile checks whole-Apply rejection, matching error completion,
exact document/backup preservation, startup-callback absence and reconnect.
A temporary hidden helper owns Ctrl+Alt+Shift+F7 to verify storage rejection
precedes shortcut conflict; existing worker F4-F6 bindings stay intact. A hard
link shares the retained allocation; one tiny copied file makes equality unsafe,
then its explicit fixture removal allows a matching equality retry. Locked
payload observation rejects a reduction but does not newly block unchanged or
increased budgets. A second serial held profile verifies positively absent
cache behavior. Only status/Apply/Quit are sent. Worker/helper identity and
normal cleanup are checked; real startup value/kind hashes must match.

Every invocation retains small before/after evidence in a unique `runs/` folder.
Only the known small control/tiny-fixture files are reset; the large payload and
alias remain unchanged. Reuse is refused after failed normal cleanup or while
the recorded task worker is still active. A fallback kill is a failed cleanup,
never a passing result. No visible/excluded UI, physical input, engine/probe,
GPU/capture, real startup write, download/repair/install or user-media action is
part of this route. The caller must serialize the hidden shortcut reservation
and include payload, aliases, reports and build output in its declared cap.

Offscreen correction/retry coverage includes a controlled matching storage
error and a non-round 12,345,678,901-byte budget. It checks retained FPS/folder
drafts, old actual configuration, pause/hold and closure only after a fresh
acknowledgement. These controlled replies establish client/editor behavior,
not a new budget control or actual storage failure through native UI.

Active acquisition growth between preflight and later mutation, actual live
engine shutdown, per-volume output/finalization/free space, warning/stop/Resume,
calibration and broader filesystem/native/media acceptance remain pending.

### Existing acknowledgement rules

The Desktop reports Apply or engine repair as successful only when the reply
contains a fresh completion for that exact operation from the original worker,
and the client's accepted current state confirms the same completed operation.
An error-bearing completion remains an error. Missing, unrelated, still-applying,
stale or retired-worker replies do not report success or close Settings. A newer
matching poll may confirm the same operation before its reply arrives. Engine
capability context may legitimately change during Apply or repair.

The existing offscreen Desktop route also checks missing/unrelated completions
and a reported write failure: the editor retains its FPS/folder draft and does
not set Submitted or request closure. After an explicit fresh capability check,
a matching retry closes once and retains the pause/activation hold. Controlled
capability/completion replies establish client integration, not successful
settings persistence or engine/media application by an actual worker.
An offscreen rerun can use hash-verified retained actual worker snapshots as
inputs when worker behavior is unchanged; record their original revision and
the source/copy hashes separately. Such a run tests the newly built client and
controls against those inputs, not a fresh worker or live-engine execution.

The `F10Recorder.WorkerChecks` entry point `--apply-acknowledgements <output>`
is dispatched before native recorder initialization. Thirteen headless client
checks cover positive correction/repair, errors, identity/revision correlation
and delayed polling. `--sensitivity-control` changes only the unrelated-operation
reply fixture into a legitimate matching completion: exactly that rejection
assertion must fail. Windows CI runs and validates both routes. These checks
create only local reports; no native windows, shortcuts, pipes, recorder, engine,
GPU, capture or startup writes occur.
