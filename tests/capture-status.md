# Capture status and delayed delivery checks

Preview `./tests/capture-status.ps1 -Plan`, then run the script to build and execute
its dedicated WorkerChecks route. Evidence is written to a unique ignored
`artifacts/capture-status-*` directory as `verification.json`. `-NoBuild` uses the
existing `bin/capture-status` output. The pinned SDK and locked dependencies are
used. Windows CI runs the dedicated `--capture-status` route.

## Contract

Capture supervision owns the current immutable phase/readiness/message snapshot.
Its status-changed event is a wake-up signal: the owner-thread callback reads the
latest status at delivery, after the engine-generation/disposal guard. Historical
payloads cannot overwrite a newer state. Capture attempts have identities distinct
from replay sessions, including during initialization before replay registration.
Retired attempts cannot publish ready or non-ready observations.

Only supervisor health/progress evidence can establish readiness. A status read
may reject readiness if the current capture process is unavailable; process
liveness alone can never establish or restore it. This is a point-in-time check,
not proof of advancing pictures or a guarantee about subsequent process lifetime.
Pause/restart/shutdown invalidate immediately. Source loss invalidates capture
before acquisition draining, while completed footage can remain available for
saving. Rebinding starts a fresh attempt and independent replay timeline.

The status module keeps transition/freshness rules together. The host owns queue
dispatch and presentation, without inspecting PID, pause or restart details to
validate an observation. Monitor and selected-window acquisition remain distinct.
ReplayCache retains accepted-save ownership, first-stop cutoffs and retirement.

Phases are explicit; English display text never determines a phase or recovery
notification. Logs record meaningful capture transitions at their authoritative
transition point, with sequence/attempt identity and reason. Duration-only updates
do not flood the log. Callbacks and logging run outside the status lock. Latest
capture status wins when the owner queue catches up: ended recovery notices may
be skipped, while diagnostic history remains. Pause confirmations and ordered
save results remain separate events.

## Coverage and effects

The suite exercises the production CaptureLifecycle and RecorderDispatch modules
with a controlled process-liveness query and owner queue. It covers delayed
notifications across pause/resume, restart, engine replacement/disposal, source
loss/rebind and shutdown; late initialization/publication/cleanup; reset completion
versus newer commands; process exit before delivery and supervisor-only recovery;
transition history without progress spam; typed notifications and separate ordered
save results.

The route exits before recorder initialization. It constructs no RecorderHost or
CaptureSession, registers no shortcuts, opens no windows, launches no recording
engine and uses no GPU, pixels, system audio, startup writes or installation.
Only local verification reports are written. These deterministic checks do not
exercise native worker integration, acquisition, process draining, source discovery,
playable output, A/V alignment, hardware performance or installed-state acceptance.
Those suites retain their separate authorization and acceptance requirements.
