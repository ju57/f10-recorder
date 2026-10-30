# Supplied replay output demand checks

This is a partial implementation of issue #13 criterion 2. Ordinary capture
sessions now read the account-private reference calibration catalog. No qualified
empirical model is shipped: missing, stale, malformed or inapplicable evidence
remains **Unmeasured**, with a bounded reason in worker status and Storage.
Production admission rejects unmeasured output; collectors without the account ledger retain necessary-only measurement routes. A
numeric output estimate is scoped to its actual closed replay selection, engine/
assemblies, source/audio/environment, destination volume and allocation unit.
It does not establish complete storage sufficiency or release acceptance.

The internal IReplaySessionSource seam can supply ReplayOutputDemand bound to a
ReplayOutputSelection: closed input identities/lengths/boundaries, source/profile,
original destination, cutoff and exporter identity. Numeric evidence also names
its kind/revision, actual volume/unit, total peak output/finalization allocation
and whether it covers the unfinished tail. It includes all coexisting output
and finalization allocation on that volume; it is not another full MP4 for
faststart or rename. A real provider must qualify scratch and filesystem
applicability, including dimensions/audio/configuration and exporter identity.
Neither native identity/EOF alone nor an enum value authenticates calibration.

ReplayCache checks supplied evidence before acceptance, after tail closure and
against the actual copied/linked snapshot before Export. A late extension must
fit before corresponding writes; failure retains original request semantics.
Reference unfinished-tail admission reserves the entire calibrated input-domain snapshot and output envelope, then rebinds closed inputs before copying/export. See [producer commitments](../docs/storage-admission.md).
Finite claims cannot later become unmeasured or change revision/exporter/volume.
Actual allocation observations precede fresh caller-free sampling. Remaining
copy/concat and output growth combine when their actual volumes match; spare
space on another volume cannot cover a shortage. Materialized allocation is
already reflected in caller-free and is not deducted twice.

Qualified claims are checked through Maintain and every 200 ms through the actual export adapter, even after acquisition stops. Lost freshness, capacity or qualification rejects before publication.
Polling and estimates do not enforce a kernel quota or arbitrary future writes.
An observed peak above its estimate invalidates the save before publication,
retaining partial output and snapshot. This detects a measured overrun; it does
not establish that a disk floor could never have been crossed before observation.

Numeric providers must report OutputProducerStopped from owned process evidence,
including exceptions. Unknown absence retains files/claims and blocks another
save, even during retirement. Explicitly confirmed later absence permits a new
request; retained files remain real allocated storage. Once export/finalization
has ended, rename creates no second payload. Successful publication stays Saved
when diagnostics or ordinary cleanup fail. The shared authority below persists
finite obligations across workers; reconciling abandoned owners and complete
crash recovery remain separate requirements.

## Nonmedia verification

Build WorkerChecks with the pinned SDK, locked audited restore and a dedicated
artifact path, using the existing package cache. Then run its exact built DLL:

```powershell
& $sdk <build>/F10Recorder.WorkerChecks.dll --replay-ownership <fresh-output>
& $sdk <build>/F10Recorder.WorkerChecks.dll --replay-output-validation <fresh-output>
```

The ownership route includes supplied-demand, loaded-model, shared-authority,
producer-absence and publication/release cases alongside earlier ownership checks.
The separate validation route has independently designed cases, with explicit
attribution in its report. Both use tiny real
files, controlled extent/capacity observations and synchronous file exporters.
They start no recorder/worker/engine/probe, GPU, native windows, shortcuts,
capture/audio, startup writes or installed-recorder changes. Malformed variants
of the new route reject before general WorkerChecks initialization. Retain
each fresh output, failed attempt and source/binary identity.

Future qualification must connect real source/profile evidence and conservative
calibration to this same exporter path. Queued/manual/recovery joins, complete
cross-profile acquisition accounting, actual mixed-volume/filesystem/quota/
exhaustion tests and ordinary low-space/Resume acceptance remain required work.
Criterion 2 and all release gates remain incomplete.

## Volatile segment manifests

Inventory reads `segments.csv` and `segments.csv.tmp` allocation, EOF and file ID
from Windows directory metadata without opening those leaves. A retained entry
still contributes its observed allocation if the producer renames it during the
scan. Only the exact manifest names use this route; ordinary growing media keeps
direct handle observations. Metadata can lag a writer, and a directory scan is
not an atomic whole-volume snapshot or a physical peak bound. The calibration
controller retains its growth/closure reserves, bounded traversal and polling
gap rejection. Unknown metadata and all nonterminal enumeration errors fail.

`--replay-storage-races <fresh-output>` is an early nonmedia WorkerChecks route.
It uses tiny files to replace a temporary manifest after metadata observation,
requires its allocation to remain charged, and includes native replacement
controls with a target reader, source reader, directory handle and no reader.
The target-reader cases deliberately demonstrate the rejected observation shape.
No engine, pixels/audio, window, GPU, profile or startup action occurs.

## Calibration sampler timing

`--replay-calibration-timing-checks <fresh-output>` checks the actual sampler's
wait and gap functions with supplied monotonic timing, including the observed
418ms scan. The wait uses only the remainder of100ms since the current scan
started; a long scan has no added100ms sleep or accumulated catch-up schedule.
High-resolution start/finish/stop coverage rejects any observed gap over500ms,
including fractional milliseconds and the final observation before shutdown.
The sampler stops and checks coverage before writing a successful batch result.

`--replay-calibration-sampler-check <fresh-output> <retained-root1> <retained-root2>`
runs20 real native inventory observations through the same sampler, retaining
4096file/48directory bounds,21GB cap,19.5GB stop,60GB caller-free floor and
1.256GB closure/poll reserve. It reads both retained failed attempts and writes
only its fresh trace/report root. It starts no child, engine, window, capture,
playback, GPU or recorder profile. Timing controls do not establish live load
coverage; the exact reviewed media run must retain the same gap rejection.

## Observation cost and evidence retention

Inventory queries an anchored directory's actual volume/allocation unit once,
then opens each ordinary leaf for metadata only. Every leaf still receives a
fresh native EOF/allocation observation and actual-volume/identity comparison
with the enumerated entry. A stale or retargeted entry fails; no closed-file
extent cache, data read or per-file free-space query is needed. Volatile manifest
names retain their directory-metadata path. Fresh caller-available capacity is
still queried separately after allocation observations.

Sampler traces separate lock wait, inventory, leaf total/count/maximum and total
coverage. `--replay-calibration-pause-check <fresh-root> <retained1> <retained2>
<retained3>` constructs exactly4096paths including the retained domain and its
trace, uses a bounded32MiB dummy writer and20 real sampler observations, and
retains any >500ms rejection. It starts no engine/media/UI/audio/GPU child.

The fresh calibration keeps all historical failed roots unchanged. After a new
source's producers/inspections have exited successfully, full new snapshot bytes
and original path/native identity/EOF/allocation/hash/mtime provenance are copied
to a bounded PAX TAR. Full readback and original equivalence precede retirement
of only those new duplicate loose entries. Archive copies remain real allocated
bytes and are admitted/charged with sources, outputs and any fallback copies;
retiring hard-link names does not credit their source payload a second time.

Heavy archive verification stays outside the sampler gate. Read-only leases pin
verified bytes; membership/native identity is rechecked inside the gate. Only
known members are retired and directory removal is nonrecursive, preserving any
unexpected addition. On failure original files and partial/full archives remain.
`--replay-calibration-archive-checks <fresh-root>` supplies tiny roundtrip, budget,
tamper, changed-original and owned-path controls. Archives preserve evidence;
they do not alter the exported video or establish calibration acceptance.

## Ordinary model catalog and shared volume claims

The read-only catalog uses the current user's protected
`F10Recorder.Security/storage-calibration/reference-v1.json` location. Recording
preferences cannot supply a provider, executable or arbitrary catalog path.
A valid data receipt binds the canonical recipe, current engine/Windows/Core
bytes, host/driver identity, three training and three independent held-out sources,
normal producer exits, all-track decode/source/audio evidence and active coverage.
The loader recomputes the training residual, finalization excess and held-out
coverage. Profile, endpoint, exporter, filesystem, volume/unit or environment
changes invalidate applicability. A rebuild invalidates assembly-bound receipts.
A pass flag or enum alone is insufficient; controlled test receipts are not
empirical qualification.

After T03 the collector freezes the catalog's canonical model revision. Only a
complete successful six-source batch can emit `qualified-reference.json`.
It does not copy that file into the ordinary catalog or activate it. Evidence
review and explicit operational registration remain separate; no model exists
from the four historical failed runs.

The ordinary ReplayEngine supplies a shared, current-user reservation authority
through protected file leases, independent of Windows session. It combines all
registered finite snapshot-copy/concat/output commitments on each actual volume.
It refreshes allocated prefixes before caller-free samples and installs an
update atomically. It does not expire orphan promises on PID absence alone.
Unknown file/anchor identity or failed persistence blocks admission without
erasing accepted obligations. Confirmed exporter absence ends growth; a failed
release before publication retains inspectable partial output/snapshot and reports
failure. Durable growth is released before rename or snapshot cleanup can remove
its bound paths. Publication remains Saved if later diagnostics or cleanup fail.

This authority covers implemented finite replay terms and, when qualified, the
active capture poll/stop allowance. Unmeasured output or acquisition/drain,
screenshots, manual, queued and recovery commitments
are not silently assigned zero or claimed complete. Orphan reconciliation and
those feature joins remain required before full global storage safety. The
5 GB warning / before-2 GB stop / durable explicit Resume policy and original
volume/quota/permission/exhaustion matrix remain incomplete.

## Active and offline calibration phases

New calibration code brackets fixture/capture/export work with active baseline
and terminal observations, through confirmed producer absence. Its 100 ms nominal
observation cadence retains the maximum 500 ms active gap, including waits and
native observation time. An excessive active gap rejects the batch.

Closed-media inspection and archival require separate capacity admission and
phase deadlines. Each full inspection admits32MB/180seconds; each short control
inspection admits4MB/150seconds. The cumulative offline analysis allowance is
1800seconds, charging all retained wall time when its phase split is unknown.
Each child must fit its complete deadline before starting. Archive admission
counts original sources, fallback copies and
new archive coexisting; retirement cannot fund the write in advance. Offline
delays are judged against those prospective phase bounds. Historical v6-v9 runs
remain failures under their original global rule. Historical evidence is still
inventoried; no growing-file extent cache was introduced.

Focused nonmedia commands, using fresh output directories:

```powershell
dotnet <WorkerChecks.dll> --replay-calibration-catalog-checks <fresh-output>
dotnet <WorkerChecks.dll> --replay-volume-reservations <fresh-output>
dotnet <WorkerChecks.dll> --replay-calibration-phase-checks <fresh-output>
./tests/storage-admission.ps1 -Plan -DataDirectory <fresh-output> -WorkerExecutable <WorkerChecks.exe> -EngineRoot <existing-private-engine>
```

The first three use controlled data/tiny files and native metadata. Volume checks
include bounded nonmedia children and actual cross-process lock serialization.
Phase controls include one 1 MiB dummy write and intentional active/offline
650 ms delays; they are not a full-size workflow rehearsal. The actual-worker
wrapper launches one isolated paused/activation-held worker, hidden F4-F6 test
shortcuts and the real client/pipe, with read-only engine hashing and no encoder,
capture, GPU workload, playback, visible UI, startup write or install. Preview
before execution. Reports, failures and source/runtime identities stay local.

A full realistic workflow rehearsal and new calibration execution need a fresh
reviewed capacity/effects/identity plan and resource decision. The previous
21 GB/38-child contract does not authorize a fresh 42-child batch or larger cap.

## Capture headroom data and durable safety component

A receipt may additionally carry independently measured capture poll and stop
terms for its actual scope/cache volume/allocation unit. The controller sums
positive native allocation deltas without credit for pruning, samples rolling
500ms growth and measures drain after its last completed pre-stop prefix.
That prefix is conservative when the observer is busy; requesting stop does not
wait behind observer I/O. Each term uses its training maximum plus the larger of
16MiB or five percent, rounded to the allocation unit. The held-out samples must
fit the frozen terms; they cannot refit them. The receipt revision binds both
terms. An output-only model keeps capture headroom unmeasured.

These are empirical sampled allowances, not bounds on unseen transients or an
OS quota. Full-size rehearsal, fresh authorized collection and ordinary runtime
acceptance are still required. No numerical capture allowance is shipped.

`--replay-storage-safety <fresh-output>` checks policy and private durable hold
records using controlled capacity/headroom and tiny files. Below five decimal
GB warns. With valid qualified terms, at or below two decimal GB plus poll and
stop growth stops, and explicit Resume needs fresh space above that boundary.
Without terms, acquisition rejects at any capacity, remains explicitly unmeasured and cannot clear an existing storage stop. No zero-growth or invented margin is substituted for missing qualification.

Ordinary capture arms a durable capture guard before acquisition. Normal completion needs
confirmed producer absence and the same guard token; a storage stop or abandoned
guard cannot be cleared by normal completion. Malformed, inaccessible or pending
state remains held. Explicit qualified Resume atomically replaces the old hold
with a new capture guard; failed publication retains the old stop. Capacity or
device return does not clear it. Typed cache-budget exhaustion also pauses and
preserves footage, requiring an explicit budget/settings or retained-media
change and safe Resume rather than automatic capture retries.

The ordinary Host and monitor/window paths use this coordinator. Actual
CaptureSession callback controls reject before any process/audio Start, and a
paused-worker/client tracer exercises missing-model Resume rejection before
Commit or engine discovery, including replacement of the worker. These are
nonmedia integration checks. A capture guard additionally records the exact
worker PID/start time and a random named, non-breakaway producer job. Evidence
is initially unsealed and becomes sealed only after process assignment and
audio startup. Each export durably unseals its reservation before creation and
reseals it only after assignment to the same job. A crash in that interval keeps
the claim unknown. A replacement worker requires both the original worker's
absence and an empty or destroyed native job before recovering sealed claims.
An exited worker alone is insufficient. Materialized files remain in place;
only future growth is discharged. Ledger version 2 prevents older writers from
discarding that proof. A clean confirmed stop also persists closure evidence.
Neither recovery nor returning capacity clears the stop: explicit Resume still
needs current qualified headroom, identity and capacity checks. Incomplete,
inaccessible or legacy evidence retains its hold and needs explicit repair.

`--replay-producer-recovery <fresh-root>` checks actual nonmedia worker/job
lifetimes, a live child after its leader exits, the last-job-handle boundary,
unsealed launch rejection, preserved footage and explicit qualified Resume.
Its controlled model is fixture data and establishes no empirical margin.
See [test effects](test-effects.md) before execution. Complete native
stop/drain, Apply/recovery, volume/quota/exhaustion acceptance remains open.

