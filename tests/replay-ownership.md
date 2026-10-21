# Replay ownership checks

Run `./tests/replay-ownership.ps1 -Plan` to preview effects, then
`./tests/replay-ownership.ps1` to build and run the file-only checks. The pinned
SDK and locked dependencies are used. Evidence is written to a unique ignored
`artifacts/replay-ownership-*` directory, including `verification.json`.
The hard-link check needs ordinary filesystem access; a restricted sandbox can
deny links with Win32 error 5 even when the copy fallback works.

The dedicated WorkerChecks entry point runs before recorder initialization.
It constructs the production `ReplayCache` with controlled capture state and
export outcomes. Manifests, snapshot files, pruning, partial-output cleanup and
publication use real local files. Completion signals control copy/export/wait
ordering; the segment-timeout check advances a modeled clock. Fixture files
are not playable media. No UI, shortcut registration, recording engine, GPU,
desktop/audio capture, startup writes or installation is involved.

The checks cover admission versus retirement, pinning during pruning and
copying, hard links and copy fallback, failed copies and subsequent saves,
export/start/timeout/output/publication failures, separate cleanup diagnostics,
stop and retirement during each save phase, profile-wide busy rejection,
orderly close, stale triggers and first-stop cutoffs, monitor application
sequences, completed manifests and audio pre-roll, fractional concat durations,
keyframe selection, retention limits, codec export arguments and throwing
notification observers. Regression checks also cover missing-cache status and
ordered notices for consecutive saves: an older completion must precede the
next save's start notice without holding save admission busy. Previously saved
files must survive every scenario.

An accepted save retains its original source, cutoff and destination. Closing a
selected window stops acquisition but preserves completed footage. Retirement
rejects new requests and waits for an accepted save before releasing the old
session's process job and files. Monitor capture remains one timeline across
application changes. A final MP4 rename establishes publication; cleanup and
notification failures do not reverse that outcome.

Orderly engine shutdown waits for retirement to finish rather than abandoning
it after a fixed join timeout and releasing protected profile ownership. The
existing segment wait and export timeout remain in effect; filesystem I/O can
still delay orderly shutdown. Forced process termination is not crash recovery.

Portable CoreChecks continue to exercise selection and clock rules. Windows CI
runs this suite without capture. The worker, window, audio-clock and migration
suites remain separate native/hardware acceptance checks; this suite does not
establish playable output, perceptual A/V alignment or gaming performance.
