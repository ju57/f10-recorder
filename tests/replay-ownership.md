# Replay ownership checks

The supplied output-demand integration and its separate independent cases are
described in [output demand checks](replay-output-demand.md). Production demand
remains unmeasured pending reference calibration; this is no storage-safety
acceptance claim. Production capture/output admission now requires qualification. The known-input-only fixtures below remain measurement controls without the production account ledger; see [producer commitments](../docs/storage-admission.md).

Run `./tests/replay-ownership.ps1 -Plan` to preview effects, then
`./tests/replay-ownership.ps1` to build and run the file-only checks. The pinned
SDK and locked dependencies are used. Evidence is written to a unique ignored
`artifacts/replay-ownership-*` directory, including `verification.json`.
The hard-link check needs ordinary filesystem access; a restricted sandbox can
deny links with Win32 error 5 even when the copy fallback works.

The storage slice uses `ReplayCacheBudgetBytes`, a positive decimal-byte
preference defaulting to 10,000,000,000 bytes. Portable Settings validation
rejects a budget below the nominal video-only history requirement; that is an
impossibility check, not a calibrated sufficient audio/container/burst estimate.
Run CoreChecks with `<output> --storage-budget-only` for the focused preference
cases. The full CoreChecks route also runs these cases and creates larger clock
fixtures; include those outputs in the experiment's artifact budget.
The Settings editor preserves the loaded custom budget when submitting
an unrelated edit. Its offscreen correction/Apply check submits a literal 30 GB
budget while correcting FPS and verifies the acknowledged value, without adding
storage controls or activating capture in that historical test. The separate
[Storage controls](storage-controls.md) route now verifies the editable budget,
three inset pickers and cache-location persistence. See the existing Desktop-only route in
[profile migration checks](profile-migration.md); retained worker snapshots are
historical inputs, not a new actual-worker persistence trial.

Production ReplayEngine supplies the configured budget and complete profile
cache root to ReplayCache. Startup retains old session directories. Cache
inventory includes retained/retiring sessions and snapshots; volume/file
identities prevent hard-link names or lifetime pins charging the same allocation
twice. Independent copied files count separately. Pending snapshot copy/concat
allocations reserve only their unmaterialized remainder. Save admission assumes
copy fallback, rechecks before fallback growth and preserves the current
one-save restriction. Failed cleanup does not establish reclaimed capacity.
Acquisition maintenance checks aggregate pressure even before completed footage
is available. Native file observations require identifiable local volumes and
reject reparse entries, unknown identity/allocation and permission errors.

Owned snapshot and retired-session directory removal now share the inventory
gate; successful cleanup cannot remove paths from an in-progress maintenance
scan. Retired-session registration is released in the same critical section.
Export, slow copies, task waits and source disposal remain outside that gate.
Two event-controlled regression schedules pause a real inventory observation,
request successful save cleanup or retirement, then release the observation and
await maintenance plus cleanup. They check deletion did not complete during the
held observation, successful publication stayed successful, and unrelated
footage stayed available. Bounded event timeouts guard the schedule; they are
not storage/performance measurements. Unknown, permission and inconsistent
observations still fail admission rather than being treated as reclaimed space.

The storage fixtures use tiny files and literal controlled allocation metadata
through ReplayFiles. They cover shared versus distinct identities/volumes,
retained old inputs, blocked retiring exports, copy growth, partial allocation
reconciliation, cleanup failure, invalid observations and overflow. The existing
retention-limit fixture now represents its large length as metadata rather than
creating a large file. Existing save/pin/publication regressions continue to use
ordinary local files and controlled export results. The module reports unknown
inventory as nullable usage plus a reason; ReplayEngine's existing numeric cache
metric uses -1 for unknown, rather than reporting an empty cache.

Known-input capacity admission additionally checks the snapshot's actual local
volume using caller-available bytes from `GetDiskFreeSpaceEx`. Copy lengths and
the exact UTF-8 concat text round up using the target allocation unit. All
snapshot targets share one volume/capacity pool; source-volume free space cannot
fund a different target. This is an in-memory commitment for the current profile,
not an operating-system allocation, quota or cross-process disk lease.

Admission installs the finite plan under the replay gate only after budget and
capacity checks. A later completed segment may enlarge it; the replacement plan
must fit before copying, or the accepted request reports failure. The initial
request does not have a proven unfinished-tail bound. Copy fallback and concat
growth recheck capacity. Inventory precedes the fresh capacity sample; only
`max(0, planned bytes - observed target allocation)` remains to be charged.
Already allocated cache/snapshot bytes reduce measured free space and are not
subtracted again. Hard-link success discharges its copy claim. Volume and
allocation unit must still match the accepted target before a materialized
extent can reduce that claim; changed/unknown/denied/invalid/overflow observations
fail. The single-save guard prevents another submission replacing those claims.
P05 budget preflight and acquisition maintenance retain their separate budget
checks without adding this save-capacity observation as a new stop condition.

Ten additional file/export cases cover target-unit rounding, literal equality
and one-byte shortages, same-volume aggregate demand, materialized remainders,
sampling order, hard-link/fallback, target volume/unit changes, observation
errors, late plan extension and concurrent retirement. They retain the original
41 replay regressions. A read-only production observer check compares an ordinary
task directory, its `.` alias and absent descendants without creating those
descendants, and rejects a regular-file ancestor. Other volume/capacity values
are controlled metadata, not physical second-volume or quota qualification.

This is partial issue #13 work. Output/finalization and unfinished-tail
commitments, below-5-GB warnings, stop-before-below-2-GB acquisition, persistent
explicit Resume, manual storage, Recovery UI, folder controls and reference
margin calibration remain pending. [The P05 worker route](profile-migration.md#budget-reduction-preflight-p05)
checks actual Apply reductions; module checks separately cover its read-only
proposed ceiling, finite equality and pending copy demand. Physical block sharing beyond
hard links, compressed/sparse/ReFS/dedup/quotas, supported path/race behavior,
actual stop timing and media integrity need separate qualification. The existing
automatic capture recovery does not satisfy low-space Resume requirements.

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
