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

## Launch effects

These route-specific declarations are indexed by [test-effects.md](test-effects.md).
Read the selected route's complete effects and bounds before execution.

### --replay-qualification-commitment-checks

`--replay-qualification-commitment-checks` checks remaining future-write promises
for initial, sealed and duplicate-sealed cases without files or native calls.

### --replay-capacity-drain-plan

`--replay-capacity-drain-plan` previews a separate actual capture-stop control.
Preparation reads display/default-audio metadata and pins a fresh plan; it records
no media. `tests/replay-capacity-drain.ps1 -PlanFile <plan> -Plan` previews it.
Run needs separate recording authorization and independent exact-plan review.
It uses a 15-second primary-desktop/default-system-audio acquisition deadline,
plus 25 seconds reserved for owned stopping/cleanup, with one owned moving
fixture/quiet chirp, using unchanged qualified binaries and a real
private catalog. One real capacity query is delayed for at most eight seconds.
It observes the actual engine pause fence, retained FFmpeg exit, audio producer
absence and exact retained growth claim, then releases the query and explicitly
retries closure with genuine producer absence if needed. The durable safety hold
remains. These sampled deadlines are controller policy, not an OS quota or a
universal producer-absence guarantee. Bounds: 300 seconds whole, 235-second fixture including a 180-second
handoff, 256 MiB output/192 MiB stop, 60 GB free floor, 1,024 files/128 directories,
eight helper/media processes plus one console, preparation and outer controller
(11 total). It preserves private media; it makes no save, normal-profile,
startup, installation, vendor or account-catalog change. It cannot establish
save latency, output accuracy or universal stop latency. The separate
`--replay-capacity-drain-claim-checks` route exercises 20 exact growth/expiry
witness acceptance/rejection cases in memory, without native/process/media effects.

### --replay-capacity-sampling-plan

`--replay-capacity-sampling-plan` previews three tiny native ledger/identity
cases. `--replay-capacity-sampling-checks <fresh-root>` checks capacity without
holder metadata or allocation credit, shared/separate volumes, negative cover,
and a concurrent identical-claims ledger round trip. It uses at most 256 KiB
under a fresh private root and a 30-second external single-helper deadline.
No child process, capture, audio, UI, normal profile or catalog action occurs.

### --replay-capacity-monitoring-plan

`--replay-capacity-monitoring-plan` previews six private guard/ledger cases.
`--replay-capacity-monitoring-checks <fresh-root>` uses controlled 2,000 ms
headroom, one 609 ms successful delay and released delays of at most five
seconds (capacity, actual held latch-read lease and producer sealing). It checks
stop delivery, retained claims, observer closure, low space before/after
acceptance and concurrent capture-generation closure. It
owns only helper threads/tasks and tiny private native files/locks; 20 seconds,
256 KiB, no child/capture/audio/UI/catalog/normal-profile effects. Controlled
fixtures do not establish actual capture drain or worker Saved latency.

### --replay-capacity-export-plan

`--replay-capacity-export-plan` previews six tiny synthetic actual-Cache saves.
`--replay-capacity-export-checks <fresh-root>` checks the 609 ms delay, blocked
capacity/producer sealing, and controlled write/finalization/postpublication
cleanup failures. It owns private ledger/guard files and one empty job, within
1 MiB and 25 seconds. It creates no child process, playable media, capture,
audio, UI, catalog or normal-profile changes.

### --replay-reservation-anchor-plan

`--replay-reservation-anchor-plan` previews eight direct native ancestor cases
and three actual-Cache publication cases. The check route uses tiny private
files/locks, at most 1 MiB, 10 seconds, 256 MiB and 2,048 handles, without a child,
media, capture, audio, UI, catalog or normal-profile changes. Restoring an
ancestor or peer fault must not permit publication; healthy checked publication
must remain possible.

### --capacity-observation-only

CoreChecks `--capacity-observation-only` exercises the single pending query,
single-use completion, actual start/completion ticks, genuine evidence expiry
and rejection of old 500 ms headroom. It also checks a lagging external epoch
against a 1,000-callback burst: full-work cadence remains separate from expiry,
with dirty/forced boundaries and busy retries preserved. The
`--capacity-cadence-only` selector isolates that deterministic clock control.
It uses only bounded managed events/tasks,
without files, native APIs, children, devices, UI, capture or audio.

These selectors target the unqualified issue 83 capacity-policy candidate in
[storage admission](../docs/storage-admission.md). Historical routes below
that explicitly assert the former 500 ms policy need affected-case refresh;
their previous passing reports do not qualify the replacement policy.

### --replay-qualification-commitment-checks

`--replay-qualification-commitment-checks` and
`--replay-capacity-admission-checks` exercise the shared initial peak calculation,
sequential materialization before confirmed case sealing, runtime shortfalls,
closure reserves and checked overflow using managed values only. They create no
file, process, device, UI, recording or catalog effects.

### --replay-calibration-preflight

`--replay-calibration-preflight <final-plan> <sha256>` invokes the owning final
admission through the real calibration Run entry, writes a receipt for that
exact plan/helper, and returns before current-device/source initialization.
`--replay-calibration-check-launch <final-plan> <sha256>` repeats those same
checks and verifies the prior receipt, stopping at the same boundary. Static
engine-file verification, host and fresh filesystem capacity queries occur;
no engine process, UI, capture or audio Start occurs. Actual calibration launch
requires the receipt, repeats admission, then checks current device metadata.
Failures produce bounded structured startup phase/cause reports.

### --replay-calibration-admission-checks

`--replay-calibration-admission-checks <fresh-root> <template-plan>` exercises
those real command routes and rejected actual-capture entries with file-only
fixtures, including the former duplicated-basename package, explicit role
failures, aliases/duplicate JSON, expiry/hash changes and stale validation.
It starts no child/source/window/audio/engine/capture/catalog operation and does
not qualify media. Run under its recorded finite milestone and reviewed bounds.

### --replay-observer-delivery-checks

`--replay-observer-delivery-checks <fresh-artifacts-root>` runs the existing
eight-case full observer comparison after focused successful/failed writer
post-disposal receipt checks. It reuses, hashes and validates the prior scoped
blocked-observer evidence rather than injecting another block. Native stopped
fixtures, own-process counters, trace/report/closure costs and existing bounds
match the follow-up route below; no capture, audio Start, worker, engine, UI,
catalog activation or child process runs. No performance-driven repeat is
permitted by this route's milestone admission.

### --replay-observer-followup-plan

`--replay-observer-followup-plan` previews a nonmedia follow-up without effects.
`--replay-observer-followup-checks <fresh-artifacts-root>` runs eight four-scan
native sampler cases with detailed diagnostics alternately disabled/enabled,
including trace persistence, reporting and closure. It queries its own process
CPU, allocations and I/O counters. A separate actual inventory call is held at
an open native handle for at most three seconds while a bounded in-process
dummy producer tests shutdown signaling and separate absence/observer closure.
A real task-private reservation and file lease remain until closure is proved.
It writes at most 2 MiB of dummy payload and bounded reports under artifacts;
starts no child, capture, engine, audio, UI or catalog action; and changes no
installed state. Run once under a reviewed60active/70whole-second,512MiB root,
100MB phase/free60GB controller. It retains failed evidence, does not qualify
live contention or Saved latency, and leaves the500ms policy unchanged.

### --replay-observer-mitigation-plan

`--replay-observer-mitigation-plan` prints the plan for the finite observer checks
without touching files, devices or processes. `--replay-observer-mitigation-checks
<fresh-artifacts-root>` verifies inventory-accounting equivalence, injected
freshness/capacity failures and bounded TS operation diagnostics. It runs 32
paired in-memory bookkeeping measurements with a 20-second measurement guard,
creates and deletes two sequential task-owned 4 KiB dummy TS files, and writes a small JSON
report. It opens native metadata handles and queries its own thread times. It
starts no children, capture, codec, audio, UI or catalog action. It does not
qualify a recording, reproduce an OS hang or measure worker Saved latency.

### --calibration-validation-work-checks

`--calibration-validation-work-checks` exercises the actual storage freshness
gate with bounded observer delays and in-memory failure/guard-context controls.
It creates no files, native observer, child process, UI, audio or capture.
`--replay-validation-handoff-probe <plan> <sha256>` is a separately admitted
file-only diagnostic: three retained outputs, four declared natural/injected
modes each, actual output lease/pin/gate and shared qualification observer.
It records native substages and reads the gate's actual timestamp for attribution.
Injected 150 ms prior work and 400 ms observer contention are explicit controls,
not a claim about the original filesystem cause. All observer tasks join before
return. The 45-second helper/60-second controller, 256 MiB/1024-handle and
256 KiB report limits bind one helper plus possible OS console support. It reads
no media payload, launches no codec and changes no media/catalog/profile; it
cannot establish ordinary Saved latency or qualification.

### --calibration-producer-absence-checks

`--calibration-producer-absence-checks` runs thirteen pure receipt controls.
It distinguishes a confirmed launched-child exit from the explicit joined,
never-launched failure shape. Missing or uncertain producer identities remain
blocked. It starts no process and performs no filesystem, device or catalog
action. This reporting check cannot turn a failed save into a passed save.

### --replay-maintenance-inventory-checks

`--replay-maintenance-inventory-checks <fresh-root>` runs finite native metadata
controls and a 300-file, 20-observation maintenance/status comparison fixture. It writes
at most32MiB of private nonmedia bodies, starts no capture, worker, codec, audio
or UI and changes no catalog or settings. Each invocation requires a fresh root;
results establish maintenance behavior/cost only, not save latency or candidate
storage qualification. Use a bounded reviewed plan before native execution.

### --replay-calibration-report-checks

`--replay-calibration-report-checks <fresh-root> <completed-plan>` reads at most
six 128 KiB per-case JSON samples and a 64 KiB plan. It checks bounded terminal
report serialization and original-error preservation, writing small synthetic
reports. It starts no process, capture, decoder, audio, UI or catalog action and
does not recover or qualify a recording batch.

### --replay-composite-report-recovery

`--replay-composite-report-recovery <fresh-root> <plan> <sha256>` is a separately
admitted file-only verifier for a completed four-case continuation whose terminal
report exceeded its limit. It reads up to 64 MiB of bound metadata, the managed
engine identity, and final-file native identities without reading media payloads.
It reconstructs the original twelve typed source receipts and validates the
frozen model, observation hashes, closure and original resource limits. A new
verification receipt keeps the original collector's failed exit explicit. It
does not launch a process, capture, decoder, UI or audio, refit a model, activate
the account catalog, or establish Saved latency. Use a fresh bounded plan and
independent review before execution.

`--replay-private-reference-catalog <fresh-directory> <receipt> <sha256>
<engine-root>` makes a protected, isolated copy of an exact completed receipt and
checks it with the ordinary catalog loader and actual destination. It reads the
account catalog only to verify preservation. It starts no capture, decoder or UI,
and never activates or writes the account catalog. Use a separately bounded,
reviewed preparation plan; this action is not an ordinary-save timing result.

### --replay-composite-budget-checks

`--replay-composite-budget-checks <fresh-root> <reference-plan>` runs tiny managed
admission-shape and original-clock-evidence controls. It reads at most64KiB of
reference JSON, writes a small report and synthetic log lines, and starts no
engine, capture, audio, UI, worker or catalog action. It does not qualify any
recording or replace the exact bound prior-evidence continuation preflight.

### --replay-admission-readiness-checks

`--replay-admission-readiness-checks <fresh-root> <managed-engine-root>` uses
  three tiny NONMEDIA files per case, private ledger authorities and explicitly
  simulated model coefficients to exercise actual Cache admission. Fourteen finite
  cases cover immediate/delayed readiness, timeout/late progress, changed model,
  exporter/input/history, stopped/unhealthy capture, invalid scope/absent calibration/
  empty input, and concurrent retirement/second save. A single frozen request must
  keep its original cutoff. Deliberate managed exporter failures prevent publication.
This is not a real-source calibration or permission to use simulated numeric
evidence for recordings. No capture/audio/codec/UI/catalog or shared state is
started or changed. Native execution requires a fresh reviewed bounded plan;
the initial red regression is retained with its expected failure clearly marked.

### --replay-calibration-snapshot-inventory-checks

`--replay-calibration-snapshot-inventory-checks <fresh-root>` also runs tiny
native main-output observer controls: fresh growth/truncation/allocation,
same-name replacement and disappearance rejection, open versus wrong/closed
validated-owner transitions, borrowed-owner disposal/write protection, custom
provider delegation and actual Cache/ledger publication, overlap and retirement.
Additional controls reject an opted-in protected-input alias, retain an actual
unconfirmed writer and observer against a subsequent save, distinguish native
handle closure before/after injected errors, preserve an export failure across
observer cleanup failure and release real parent-directory pins before removal.
The outputs use explicit simulated NONMEDIA validation. No codec, capture,
audio, GPU, UI, catalog or qualified empirical model is used. The retained
observer has attribute access only; each allocation query matches fresh native
namespace identity before and after. Its owned handle and directory pins close
before publication, followed by fresh accounting through the already validated
read owner. Native fixture execution needs a fresh reviewed bounded admission.

### --replay-outside-partition-checks

WorkerChecks `--replay-outside-partition-checks` performs pure arithmetic controls
for a prospective partition of the same fixed10GB outside charge. The separate
`--replay-outside-metadata-checks <fresh-artifact-root>` writes tiny synthetic JSON
fixtures and tests the real metadata consumer: exact exclusion sets, bound native
proof/forecast identities, deduplicated alias charges, failed proofs and invalid
reserves. These controls invoke no native allocation API, capture, media process,
worker, audio, UI, catalog or model; synthetic receipts are not measured evidence.

### --replay-export-cadence

WorkerChecks `--replay-export-cadence <fresh-root>` drives the real replay cache
with tiny nonmedia inputs, a controlled output estimate and synthetic read delays.
It checks six cooperative 110 ms reads, six covered 650 ms reads and one read
250 ms beyond the current 2,000 ms capacity horizon. Covered reads must make real
capacity progress and publish; the over-horizon read must reject before
publication. The ordinary ownership suite also runs these three cases. The
runtime contract and other guard deadlines remain unchanged. Stage/guard counters are written after each
case; the suite launches no child, recorder, media engine, GPU, capture/audio,
window/shortcut, startup or installation action. It writes only task-owned tiny
files/links and reports and requires ordinary Windows filesystem access.

The existing `--replay-storage-runtime` route also checks never-launched
admission holds across coordinator replacement, explicit safe Resume, and
retention of unknown-producer holds. `--replay-output-validation` checks that
stopping before an accepted endpoint fails without copying/export/publication
and preserves original inputs through retirement. These use tiny nonmedia
files and controlled sources/capacity, with no actual recording. The existing
paused-host Resume route also verifies a zero-expiry storage problem event.
Portable Core checks exercise the warning/stop policy's numeric boundaries,
overflow rejection, unmeasured rejection and volume binding without Windows.

### --replay-storage-host-resume

`--replay-storage-host-resume <fresh-root>` supplies supervisor observations to
an isolated paused host and checks explicit Resume intent while the previous
durable guard is still present, later healthy buffering, an effective subsequent
Pause command and a new storage stop. It owns only a hidden shortcut window and
tiny profile/report files; no capture engine, child, media, audio, model, visible
UI or startup write runs. Native acquisition/producer shutdown remain separate
verification requirements.

### --replay-calibration-snapshot-inventory-checks

`--replay-calibration-snapshot-inventory-checks <fresh-root>` uses tiny private native files,
with no child/capture/audio/model action. It checks that acquisition files keep
short metadata leases, sealed snapshot handles refresh real allocation/EOF,
replacement revalidates identity, atomic metadata remains unheld and namespace
retirement closes its handles. It also checks vanished-input release, default
custom-provider observation dispatch and an explicit native-retention opt-in
through successful/failing Cache saves. Native allocation/EOF growth stays
fresh, and captured actual SafeHandles must be closed before external fixture
retirement. These are controlled nonmedia files, with no storage model.
The native cache bounds retained handles at 4096;
its 500 ms guard remains unchanged. A changed product DLL still needs its own exact
calibration identity and runtime verification.

### --replay-calibration-export-slow-checks

`--replay-calibration-export-slow-checks <fresh-root> <engine-root>` runs one
CPU-only16x16/null-output child for4.5seconds with330msactual observation work,
250msunseal/600msseal callbacks and a12second adapter deadline. It requires at
least four post-seal observations,successful exit and every measured evidence gap
at most500ms. The old completion-based200mswait fails this control. The corrected
start cadence includes scan work. The existing600msobservation sensitivity still
must reject and confirm owned-child absence. No desktop/audio/GPU/model action.

### --replay-storage-initial-worker

`--replay-storage-initial-worker <fresh-root> <WorkerChecks.exe>` is the initial
admission variant of the Resume tracer. It starts two serial task-owned paused,
activation-held workers using an absent private engine root, current-user pipes
and hidden Ctrl+Alt+Shift F4-F6 shortcuts. It verifies rejection before Commit or
engine discovery without an existing storage hold. No engine/probe/capture,
playback/GPU/visible UI, startup write or catalog activation occurs. Ordinary
settings, retained sentinels and startup state are checked unchanged.

### --replay-calibration-publication-checks

`--replay-calibration-publication-checks <fresh-root>` runs the actual native
sampler alongside production storage supervision, durable guards and 40 scalar
ledger updates/normal closure, under a 30-second deadline. It writes under 2 MiB
of private tiny files and uses controlled 12 KiB capture headroom. Atomically
replaced claim metadata uses fresh anchored native directory-entry allocation,
EOF and identity, without opening conflicting leaf handles; no bytes are omitted.
The 500 ms active gate remains. It starts no child, engine, capture/audio, GPU,
window/shortcut, startup or account-catalog action. It does not qualify media.

### --replay-calibration-audit-receipt

`--replay-calibration-audit-receipt <fresh-root> <historical-receipt> <sha256>`
reads a hash-bound historical qualification and copies it into a private fixture
catalog solely to confirm loader rejection after a binary change. It reads live
host metadata, verifies the default catalog is unchanged and starts no worker,
engine, GPU, capture/audio or UI. It does not activate the historical model.

### --replay-storage-contention

`--replay-storage-contention <fresh-root>` exercises actual native reservations,
capture supervision and accepted snapshot/export ownership using 300 tiny text
segments and a 2 KiB stand-in output. An 80 ms supervisor-held lock must recover;
persistent accepted-work contention must fail boundedly before export, preserving
inputs; persistent capture contention must stop under the unchanged 500 ms
freshness rule. It uses private fixture profiles/ledgers, native hardlinks/copy
fallback and background threads, under 16 MiB and a 30-second save deadline.
Two empty named native-job fixtures check accepted producer sealing and claim
release under transient contention; no process is assigned or started.
New admission/polling remain nonblocking; accepted mutations may retry for at
most 200 ms. A busy capture poll retries after 5 ms without refreshing evidence.
No child, engine, playable media, recording/audio, GPU, window/shortcut, startup,
installed-profile, vendor or account-catalog action occurs. All reports/failures
remain; ordinary successful fixture cleanup removes only its own tiny inputs.

`tests/storage-admission.ps1 -Plan -DataDirectory <fresh-root>
-WorkerExecutable <built-WorkerChecks.exe> -EngineRoot <existing-private-engine>`
previews the exact isolated paused-worker command/effects without building,
creating profiles or launching processes. Omit `-Plan` only for the admitted
status/client tracer: one held worker, current-user pipe, hidden Ctrl+Alt+Shift
F4-F6 shortcuts, tiny preservation sentinels and read-only engine hashing.
No engine/probe/capture/GPU workload/audio/visible UI/startup write/install occurs.

### --replay-calibration-catalog-checks

`--replay-calibration-catalog-checks` uses bounded controlled receipts and private
ACL/file fixtures with controlled host metadata and read-only native volume observation. This does not qualify hardware or media.
`--replay-staging-evidence-costs-plan` previews a smaller follow-up component.
`--replay-staging-evidence-costs <fresh-artifact-root>` executes four fixed
301-call local/full/full/local loops on an idle private Cache's unchanged native
output-evidence method, after a separate untimed 301-throw shape check. Reflection
supplies only synthetic method context; the first-chance counter is detached
before every timed arm. No accepted save, producer or guard is intercepted. It includes reflection overhead and
excludes global policy, linking and worker/media behavior: 1 MiB, 10 seconds
internally, 40 seconds externally, no children/media/devices/UI/profile/catalog.

### --replay-calibration-publication-checks

`--replay-calibration-publication-checks` uses two joined managed threads and
fabricated extent records, without files, devices, profiles or child processes.
It tests observation progress during slow publication preparation, the protected
publication action, failure rejection and checked-output identity attribution.
The separately admitted composite storage rehearsal may explicitly select
`retain-files-validated-owner-v1` in both plans. It writes full-size non-playable
dummy files and three extra4KiB controls, exercises actual qualification
publication with simulated validation ownership, and checks native identity,
sharing denial and release without writing through the permission probe. These
native operations and handle closes retain the active500ms limit. No capture,
audio, encoder, actual validator or empirical model is involved.

### --replay-calibration-inventory-checks

`--replay-calibration-inventory-checks` writes under4MiB of dummy payload and
1000 empty files. It checks fresh allocation through reused native metadata
handles, replacement, removal, volatile lists, disposal and root admission,
then compares ten paired metadata scans. It launches no engine or child and
does not establish full-workflow timing reliability. Only explicitly declared
retained roots may reuse handles. A hash-bound plan can also enable current-tree
metadata handles with read/write/delete sharing. Every scan enumerates names and
matches their identities to the same established open native handles; allocation
and EOF are queried afresh. Volatile segment lists never retain handles. Verified
directory retirement releases observer handles while holding the inventory
mutation gate before deletion. Capacity and500ms checks remain in force.
Current `chunk-*` capture files and MP4s deliberately use short-lived handles:
retaining the observer through producer close can delay allocation trimming.
Only fully closed cases may retain those payload handles. A focused native
last-close experiment records allocation before/after releasing its own handle;
the policy checks preserve fresh EOF/allocation reads and producer-close behavior.


`--replay-storage-safety <fresh-root>` uses controlled byte/headroom data and
bounded private hold/guard files, with task-owned ACL/failure/junction fixtures.
`--replay-storage-runtime <fresh-root>` uses the actual coordinator and controlled
capacity, catalog and stop callbacks; it includes intentional late observations
and a shared-lock Busy sensitivity. Both start no engine, capture or audio.

### --replay-storage-prelaunch

`--replay-storage-prelaunch <fresh-root> <existing-private-engine>` reads and
verifies an existing approved engine but rejects in the actual CaptureSession
constructor callback before StartProcess. Synthetic settings avoid WASAPI metadata
and acquisition. It creates native process-job handles and tiny task-owned
profiles/guards, hashes the engine before/after, and starts no media producer.

### --replay-storage-resume-worker

`--replay-storage-resume-worker <fresh-root> <WorkerChecks.exe>` uses two serial
isolated paused/held worker incarnations, hidden Ctrl+Alt+Shift F4-F6 shortcuts
and the current-user pipe/client. It sends status, Resume, environment restart
and Quit using a nonexistent engine root; missing-model Resume must reject before
Commit/discovery/probe/acquisition. It compares profile/media sentinels, hold ID,
startup value and normal owned exits. Preview its exact argv/profile/effects in
the task's prospective plan before running. It opens no visible or excluded UI,
captures no pixels/audio, changes no startup setting and installs nothing.

### --replay-output-validation

WorkerChecks `--replay-output-validation <fresh-root>` runs tiny file/source
fixtures through ReplayCache with controlled allocation/capacity and export
outcomes. It starts no recorder/worker, engine/probe, GPU, window/shortcut,
capture/audio, startup or installation route. Reports and failed fixtures are
retained locally. Unknown owned-producer outcomes are controlled observations,
not actual leaked processes. See [supplied output demand](replay-output-demand.md).

### <output> --storage-budget-only

CoreChecks `<output> --storage-budget-only` runs only the portable budget/profile
cases and writes tiny local settings/original-backup fixtures. It starts no
recorder, native window, engine, GPU, capture or startup action. Full CoreChecks
also runs it, alongside the existing larger clock fixtures; budget their retained
output separately. Replay ownership storage cases control allocation/identity
metadata through the existing filesystem seam while retaining tiny real file,
snapshot, cleanup and publication operations. Ordinary Windows file-information
and hard-link APIs are used; no native UI or recording initialization is reached.
