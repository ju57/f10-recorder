# Verification launch effects

Preview the actual launch plan before building, creating output directories or
starting a process:

```powershell
./tests/window-capture.ps1 -Plan | ConvertTo-Json -Depth 5
./tests/window-capture.ps1 -Mode Window -Plan | ConvertTo-Json -Depth 5
./tests/worker.ps1 -Plan | ConvertTo-Json -Depth 5
./tests/worker.ps1 -LiveDesktop -Plan | ConvertTo-Json -Depth 5
```

| Route | Real UI/focus | Real pixels/audio | GPU encoding | Startup writes | NVIDIA Replay interaction |
| --- | --- | --- | --- | --- | --- |
| Window checks, default Synthetic | No | No | Yes | No | No excluded UI surfaces |
| Window checks, Window | Yes | Controlled application window and default output audio | Yes | No | Treat visible UI as a potential interaction |
| Worker checks, default Synthetic | Yes | No | Yes | Settings apply may write the existing value; preservation is checked | Excluded monitor-mode UI can stop Replay |
| Worker checks, LiveDesktop | Yes | Primary display and default output audio | Yes | Same preservation contract | Excluded monitor-mode UI can stop Replay |

Generated media does not make the worker UI suite headless. UI/live routes keep
their existing desktop ownership and task-specific authorization requirements;
the plan adds no confirmation prompt. Headless checks still use the GPU and
isolated worker shortcuts, so they are not a performance-neutral concurrent task.

`verification-launch.ps1` supplies both the command and its effect declaration.
The scripts preview and launch that same object. A contradictory command,
headless flag or effect declaration is rejected before `Start-Process`.
`verification-launch-checks.ps1` previews the real scripts and exercises these
negative cases without launching UI, GPU work or capture. Windows CI runs it.
This protects known dispatch behavior; it cannot infer every indirect effect
inside arbitrary native code. Review the mapping when changing a test entry point.

`python -B tests/pilot_collector.py --plan` (also the default) is an inert R1
preparation preview: no root/tool stat, hash, directory creation, build or child.
`--assess <json>` reads only that caller-named JSON (at most 1 MiB), hashes those
small input bytes and prints supplied storage assumptions/eligibility; it writes
no files. Execution is always disabled. `test_pilot_collector.py` uses controlled
observations and public entry functions, retains tiny JSON inputs/receipts under
fresh ignored artifact directories, and launches no child/engine/probe/GPU/UI or
capture. No profile/startup/vendor changes or cleanup of prior evidence occurs.
See [pilot preparation](pilot-collector.md) for the deferred native/measurement
adapters and the distinction between supplied cleanup and actual process proof.

CoreChecks `<output> --storage-budget-only` runs only the portable budget/profile
cases and writes tiny local settings/original-backup fixtures. It starts no
recorder, native window, engine, GPU, capture or startup action. Full CoreChecks
also runs it, alongside the existing larger clock fixtures; budget their retained
output separately. Replay ownership storage cases control allocation/identity
metadata through the existing filesystem seam while retaining tiny real file,
snapshot, cleanup and publication operations. Ordinary Windows file-information
and hard-link APIs are used; no native UI or recording initialization is reached.

`./tests/profile-migration.ps1 -Plan` previews isolated paused/blocked migration
workers and hidden Ctrl+Alt+Shift+F4–F6 shortcuts. It sends task-owned hidden-window
Save/Screenshot messages without physical input and checks ownership/reconnect,
exact input/media-sentinel preservation and owned-process cleanup. `-DesktopOnly
-SnapshotsPath <worker-output>/worker-snapshots.json` uses offscreen Settings
controls and a controlled capability response. Neither route opens visible or
capture-excluded UI, launches an engine, uses a GPU, captures pixels/audio, writes
startup settings or touches the installed recorder. See
[profile migration checks](profile-migration.md) for format, commands and limits.
The worker/Desktop routes include large Unicode and near-limit opaque preferences,
with bounded status/correction replies and original-byte preservation.
`-StatusTransportOnly -Plan` previews the separate CI projection/protocol/headless
client checks. That route opens no native recorder/windows/shortcuts/pipes and
uses no GPU/capture/startup behavior; only local fixture/report files are created.
The early `--apply-acknowledgements <output>` WorkerChecks route likewise uses
only the production headless client and controlled replies, with local reports.
Its optional `--sensitivity-control` changes one response fixture, not production.
The existing offscreen Desktop profile route additionally exercises failed or
unconfirmed correction, retained drafts and an acknowledged retry, using no
platform Window, capture affinity, real engine or startup writer.
`./tests/profile-migration.ps1 -EntryOnly -Plan` previews the separate controlled
profile-entry route. It constructs persistent Settings controls and the headless
production client only, invokes the same one-time entry method consumed by the
attachment handler, and observes fake transport commands. Held/unsafe entries
send none; a separate trusted ordinary non-held entry exercises the retained
legacy one-time probe through a controlled reply. No native Window/App, worker,
pipe, real engine/probe/GPU, capture, startup write or vendor action occurs.
Snapshots, reports and bounded attention text renders are local artifacts.
The Windows CI entry step builds MigrationTests but invokes only this early
route, twice: normal and fixture sensitivity. Fresh per-invocation GUID output
roots retain distinct reports. `verify-profile-entry-report.ps1` only reads the
specified JSON and validates its exit/result/boolean flag and exact 27-check
contract; it does not launch anything or modify the report. The sensitivity
fixture must fail its sole named existing-context assertion. Hosted/offscreen
checks do not establish native attachment, button geometry, media or release
acceptance.

`./tests/profile-migration.ps1 -RealWorkerCorrection -EngineSource <retained-approved-engine> -EngineRoot <task-private-shared-root> -Plan`
previews the separate seven-case actual-worker correction route. It stages at
most one hash-pinned task copy, verifies its private ACL/hash without executing
it, and uses paused/activation-held workers, current-user named pipes and hidden
Ctrl+Alt+Shift+F4-F6 shortcuts. Ownership, engine and fixture profiles are isolated.
Only status/Apply/Quit are sent. An existing startup callback records/optionally
rejects fixture intent; the user's actual startup value/kind is read and compared
without writing. No engine probe/launch, capture, GPU, visible/excluded UI,
download, repair, installation or vendor changes occur. Controlled file-write,
backup and callback failures retain originals and incomplete paths. Review the
explicit source/root/output plan and serialize native ownership before running.

`-RealWorkerCorrection -BudgetApplyOnly` is the focused P05 extension of that
existing worker route. Save its explicit `-Plan`: it lists the fixed retained
profile, serial empty-cache profile, ownership/build/engine paths and a temporary
task-owned F7 helper in addition to F4-F6. It verifies/reuses an existing task
engine read-only, never provisioning it. One ordinary45,000,000-byte fixture is
reused with hashes and allocation identity; a hard-link alias adds no second
payload, and one tiny copy is explicitly removed as fixture setup. Every run
keeps unique small input/result snapshots, checks real startup hashes and normal
worker/helper cleanup, and refuses reset after failed cleanup. It remains
paused/held: no engine/probe/GPU/capture, visible/excluded UI, physical input,
real startup or installed-recorder changes. See the P05 section of
[profile migration checks](profile-migration.md#budget-reduction-preflight-p05).

`./tests/settings-validation.ps1` exercises a paused direct recorder host with
missing and untrusted engines in isolated storage. It creates hidden shortcut
windows and temporarily registers Ctrl+Alt+Shift+F1–F6; an occupied shortcut fails
the check without stopping its owner. It shows no capture-excluded UI, records
no pixels/audio, probes no GPU, changes no startup setting and installs nothing.
Use an ordinary Windows desktop token; a restricted sandbox can deny shortcut
registration before the check reaches engine preflight.
The check verifies rejection before proposed-shortcut admission, transaction
notifications, preference persistence or capture startup. Portable CoreChecks
exercise preference validation without engine initialization separately.

`./tests/replay-ownership.ps1 -Plan` previews the dedicated replay ownership
route without building or writing evidence. Running it exercises the same replay
module interface used by capture supervision, with local manifest/file fixtures
and controlled export outcomes. It opens no windows, registers no shortcuts,
launches no recording engine, uses no GPU, captures no pixels/audio, changes no
startup setting and installs nothing. Fixture output is not playable media and
does not establish live capture, A/V alignment or performance acceptance.

The same replay route checks known input-copy/concat capacity through controlled
volume/allocation/free-byte observations and tiny ordinary file/export fixtures.
Its production observer smoke reads actual local volume identity, allocation
unit and caller-available bytes for a task-owned directory, a lexical `.` alias
and absent child paths; observation does not create those children. It also
rejects a task-owned regular-file ancestor. No mount, reparse or ACL mutation,
physical second-volume test, real disk exhaustion, recorder/engine process or
startup action is added. Capacity is a point-in-time necessary input condition;
it is not output/finalization/tail sufficiency or protection against later
capture/external growth. See [known-input capacity checks](replay-ownership.md).

`./tests/capture-status.ps1 -Plan` previews deterministic capture-status checks.
The dedicated `--capture-status` route exits before recorder initialization and
uses the production status/owner-dispatch modules with controlled process liveness
and delayed queue delivery. It creates no windows, shortcuts, recorder or encoder;
it captures no pixels/audio, uses no GPU, changes no startup setting and installs
nothing. Its only runtime output is a local verification report. See
[capture status checks](capture-status.md) for coverage and limits.

`./tests/engine-trust.ps1 -Plan` previews the isolated trust/repair worker and
offscreen Settings checks. See [engine trust checks](engine-trust.md) for exact
generated-input, hidden-shortcut and retained-evidence effects. This route creates
no visible or capture-excluded UI and never acquires desktop/device audio.
