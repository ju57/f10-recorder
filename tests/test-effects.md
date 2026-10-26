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
