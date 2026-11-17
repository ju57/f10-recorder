# Verification launch effects

## Independent presentation reference: offline preparation only

The prospective v5 method requires the explicit
`ProtectionScope=fixture-marker-roi;global-mask-diagnostic-v1` and
`MarkerTransitionScope=fixed-H03-source-reference;forward-only-no-divisor-no-wrap;coalesced-same-id-only-v1`
operands. It decodes
the same acquired texture's 576x48 complementary marker ROI and retains the
global `ProtectedContentMaskedOut` flag in every trace row. That global flag is
diagnostic rather than a blanket ROI veto; no full-frame unmasked proof follows.
All existing ownership, continuity, current/next ID, native QPC, workload,
one-frame and 50ms gates remain. V3/v4 configurations and missing/other policy
scopes reject. V5 is restricted to the actual H03 source-reference fixture,
divisor one and its finite no-wrap frame domain; arbitrary content cannot use
this policy. After Ready, a coalesced row may repeat the previous marker ID,
retaining its original birth/link. A changed ID must still have Accumulated=1;
backward IDs, clocks, ownership and missing-next failures remain sticky. Every
raw row/counter/flag remains. This is a prospective method amendment: offline
preparation does not authorize v5 native use, another acquisition or relabeling
failed v4 evidence.

The prospective v6 method uses the distinct identity
`independent-desktop-presentation-interval-v6` with
`MarkerTransitionScope=fixed-H03-source-reference;forward-only-no-divisor-no-wrap;coalesced-adjacent-two-interval-v1`.
For a post-Ready adjacent marker change with two accumulated desktop updates,
the previous and current native presentation timestamps bound the transition.
Each source picture must pass the existing one-frame-plus-bridge test for every
time in that interval. The signed A/V calculation includes both residual
endpoints and keeps the existing uncertainty terms and 50 ms threshold. The
same fixture, protection scope, readset, and pre-Ready bootstrap apply. The
method identity and hash bind the live observer, closed reader, and retained
validation.

For a same-source confirmation with independent presentation, the controller
joins the fixture after the primary save and confirmed capture/audio drain.
Normal fixture exit and a complete observer closure precede the paired exact
export. The 25 ms paint check runs through fixture closure, and the closed
source oracle still checks current/next marker and audio coverage over the
full selected inputs.

The preserved pre-Ready bootstrap permits a coalesced image before computed Ready to
restart only the unproved bootstrap after all ownership, ordinal, native-clock
and backward-ID guards pass. All raw rows/flags remain. Two clean observed ID
transitions must then prove Ready within the unchanged 10s deadline. Under v5,
changed-ID coalescence after Ready fails. The closed parser replays this logic and
requires every prefix row's After at/before declared Ready and every subsequent
row's Before at/after it. Prefix IDs without births cannot qualify source or
output pictures; no captured frames are trimmed or substituted.

`--replay-presentation-protection-plan` previews the focused generated v5
policy/raster/ledger controls. `--replay-presentation-protection-checks
<fresh-report>` runs one helper for at most 30s, 256MiB/2048 handles, with a report
under 1MiB. It makes no native/device/desktop/UI/audio/codec calls or children.
The metadata checks below also exercise flagged closed traces, explicit method
policy, v3 rejection and malformed/omitted flagged rows. Old control counts keep
their original method/source scope; they are not new v5 executions.

`--replay-presentation-offline-plan` previews generated model, raster, reader and
real lifecycle-wrapper controls. `--replay-presentation-offline-checks <report>`
runs one helper for at most 30 seconds with one generated observer thread at a
time and a fresh report under 1 MiB. Its generated factories/sinks make no
desktop/device/UI/audio/codec acquisition or native file-ownership calls. A
blocked generated reader is explicitly released and joined; this is not live
native termination evidence.

`--replay-presentation-metadata-plan` previews generated JSON/CSV fixtures through
the actual closed-reference parser and early retained-method guard.
`--replay-presentation-metadata-checks <fresh-root>` uses one helper for at most
30 seconds, at most 512 MiB, 64 files and 1 MiB in a fresh ignored root. It reads
only its generated nine-file reference and creates no children, native file
owners, graphics/device/UI/audio/codec activity or capture. Its exact source and
launcher need independent review before invocation. These checks establish
parser rejection and guard behavior, without empirical media acceptance.

`--replay-presentation-method-plan <method-json> <sha256>` reads bounded pinned
nonmedia configuration and prints exact effects/readsets without executing them.
The v3 method retains all shared bridge/render-clock/CFR/interpolation/audio-grid
uncertainties and uses one native QPC tick instead of the unused GDI midpoint
half-width. The existing same-C00 offline checks exercise the actual uncertainty
function over generated values; they establish no real native timing result.
For the native-bound fixture, strict audio/GDI reference construction follows
the genuine validated request/release/foreground; audio Ready and another
foreground check precede the observer. The existing total 10s Ready deadline
includes that startup. Pre-admission GDI diagnostics are separate and unqualified
(2 MiB CSV plus 16 KiB summary); no pre-arm row can enter qualified paint/clock
evidence. All qualified 25ms GDI/2ms audio, one-frame and 50ms gates remain.
Fixture costs report process totals through the final disposal observation and
both phase coverage; the later exit tail and GPU cost remain explicitly unknown.
Generated phase/factory/TextWriter controls make no device or native FILE calls.
The separate fixture `--presentation-reference` route remains a native capture
effect needing an independently reviewed admission and explicit additional
acquisition scope. It adds one thread/device/duplication client, up to 150,000
rows, 110,592 bytes of GPU-to-CPU ROI data per desktop update (16,588,800,000-byte
finite ceiling), a 20 MiB trace and bounded metadata. No vendor/product rebuild,
ordinary Saved sample, preference change or new recording is authorized by the
offline routes. Native file-sink/atomic-poll/ownership controls must receive
their own exact source/effects review before execution; generated sinks do not
establish those Windows behaviors.

## Same-C00 v10 offline controls and held plan

`--replay-same-C00-offline-plan` previews generated metadata/timing/PCM controls;
`--replay-same-C00-offline-checks <fresh-report>` runs the actual marker classifier
and prospective source/selection/acceptance rules over generated memory only.
Bounds: one helper, 30s, 256MiB/2048 handles, 1MiB report. No native/device call,
media payload, codec, capture or empirical A/V acceptance is involved.
`--replay-same-C00-plan-shape <fresh-file>` emits implemented plan shapes and pure
argument/commitment metadata. `--replay-same-C00-freeze-request <metadata-config>
<fresh-file>` reads only the declared original receipt JSON/expected-host/review
metadata and emits a REQUEST, never a freeze or future recording receipt.
`--replay-same-C00-envelope-checks <retained-template> <fresh-report>` roundtrips
the actual v10 helper reader at 1/61/65 inputs and rejects unknown/changed outer
fields, using generated metadata only. The versioned v10 retained envelope allows
128KiB; historical plans retain their 64KiB owner limit.

The existing bounded `tests/replay-confirmation-offline.ps1 -Plan` owner accepts
`--replay-coverage-checks` for private JSON/ACL/NTFS dummy coverage controls,
including v10 loader/missing-parent/manifest/acceptance negatives. Same limits:
45s work/60s outer, 512MiB/4096 handles, 8MiB fresh dummy evidence, 60GB floor.
Its staged Cache control throws before codec creation. Native effects require
independent review; the sandbox may deny the private junction/ACL operations.

`tests/replay-confirmation-owner.ps1 -Plan -Template -PlanFile <template>
-ExpectedPlanSha256 <hash>` supports the actual prospective v10 templates without
inventing future files, hashes or timestamps. It reads pinned nonmedia/runtime
bindings and reports the conditional final path; a template cannot enter `-Run`.
Actual v10 C00/retained owners admit 16/9 managed roles, 1800/900 seconds and
fresh disjoint outputs. Their extra oracle scans add five to the six baseline
decoder passes, for eleven total. Source render work is one fixture thread,
not an extra acquisition/process. All raw/reference/CPU/IO/storage/time costs
belong to the finite plan. Actual source/reference/native closure qualification
and the one recording/four outputs remain ON HOLD until exact human release.

The conditional release adds three bounded metadata helpers (freeze, fixed
outside inventory, final preflight) and two separately invoked named-review
acceptance helpers. `ProcessController.Metadata.ps1` restricts those four
selectors to 50s work/60s whole, 512MiB/4096 handles, 1MiB per diagnostic stream,
held PID/birth and post-disposal closure receipts. Known fixed summaries/bounded
admission errors are checked after drain; this is not an OS pipe/memory quota.
C00 preparation starts within T0+30s and ends within T0+240s; T0 and the
T0+1800/T0+3600 windows never refresh. Future preparation may query actual
host/volume metadata, but starts no source/media process. These helpers remain
unexecuted here and are distinct from the generated offline fixtures.

The old readiness controls below describe their original v9-bound pack; they do
not replace the separate v10 source policy or authorize either media operation.

## Confirmation readiness controls

`--replay-calibration-selected-history-plan` and
`tests/replay-confirmation-offline.ps1 -Plan -Helper <helper> -Root <fresh-root>`
preview the selected-only native dummy-file checks. After independent effects
review, the launcher admits one pinned helper, 45s work/60s outer including
cleanup, 512MiB/4096 handles, 8MiB private dummy output and a 60GB free floor.
It checks exact selected-object inventory, alias-write/unlink/parent-rename
denial, excluded-object availability, duplicate paths and partial-constructor
closure. It never enumerates a retained recording session. The existing
frozen-history selector checks sticky namespace failure and retained owners;
its outer appends one byte to its own dummy file only after actual helper exit
to prove lease release. These are native fixtures, not media/provenance proof.

`tests/replay-confirmation-controller-checks.ps1 -Plan -OutputFile <fresh-file>`
previews pure fabricated process/control/argv checks: no child/native inventory,
30s/1MiB fresh report. `tests/replay-confirmation-release-checks.ps1 -Plan -Root
<fresh-root>` previews pure generated nonempirical authorization/template/order
JSON fixtures under the same30s/1MiB policy, with no child/media/native inventory.
`tests/replay-confirmation-owner.ps1 -Plan -PlanFile
<final-plan> -ExpectedPlanSha256 <hash>` reads bounded NONMEDIA admission/runtime
bindings and previews one of the fixed C00 or retained-pair owner routes.
Its `-Run` route is a media operation requiring a separately approved invariant
template, actual release/current authorization and the original helper admission.
It supervises helper/native completion only; it does not supply the precise
independent source/A/V oracle or historical retained-content expectations.
No complete executable two-operation plan is supplied by these offline controls.
Recording and retained-media execution remain ON HOLD.

## Versioned coverage controls

`--replay-coverage-plan` previews the focused v9 route. After independent review,
`--replay-coverage-checks <fresh-ignored-root>` uses controlled private JSON/ACL/
NTFS metadata, 302 one-byte dummy inputs and actual serialized catalog resolution
for every integer 1–30. It checks malformed evidence, original-byte binding,
host/driver/recipe/volume/unit/domain/refit rejection, missing confirmations and
independent source/clock/endpoint acceptance, tier limits, full-tier tail/scratch
reservations and overflow. One actual Cache request with the C00 unmodelled
provider and private shared ledger reaches accepted staging, then deliberately
throws before any codec. An actual coordinator uses the serialized loader and
real destination checks through admission, Resume and a private capture
guard with **no producer launch**; removing only its private fixture bridge must stop that guard. No producer
is attempted and its explicit closure reports that fact.

Bounds: one helper, 45 seconds work/60 seconds outer, 512 MiB/4,096 handles,
8 MiB private logical output and 60 GB free floor. The outer controller samples
these bounds; they are not OS quotas. The fixture temporarily adds World Write
only to its dummy receipt, observes rejection and restores its ACL in `finally`.
It also reuses the existing native junction fixture to reject one catalog alias
inside this new private root, then removes only its reparse tag in `finally`.
Parent evidence is embedded bytes; traversal-like roles or path-reference fields
cannot select another evidence file. Run actual ACL controls outside filesystem
shims that suppress ACL mutations.
No engine, codec, media-payload reading/hashing/decoding, capture, audio, UI,
ordinary worker, account catalog activation, settings/startup or vendor changes.
Controlled positive receipts are nonempirical and never qualify this computer.

`--replay-coverage-C00-admission-checks` checks the single-source preparation,
full future commitments, fixed purpose window and rejection of six-source/refit
shapes in memory. Existing duration/model/commitment selectors remain focused
offline controls. `--replay-coverage-parent-checks <fresh-root> <5m-receipt>
<sha256> <30m-receipt> <sha256> <omission-review-hash>` reads only those bounded
original JSON receipts, checks original hashes/schemas/training/heldouts and
reports the rederived union. It creates no measured model or operational bridge;
historical process omissions remain missing. No media payload is opened.

Future operations are separate from these checks. `--replay-coverage-freeze
<request> <sha256> <new-output>` captures current canonical host metadata and
freezes reviewed parent evidence/target context. The existing final calibration
Run accepts `issue83-C00-current-context-frozen-union-v9`, one dynamic H03-shaped
pair, five minutes/420-second collector lifetime, 13 total process roles,
1,800-second whole/600-second offline bounds, with bound `coverage-freeze.json`
and the unchanged explicit recording-authorization/foreground/native admission.
Its unmodelled source/two-output/scratch/copy/trace/closure promises remain charged;
normal SafeToResume is not bypassed or fabricated. C00 supplies no benchmark Saved
sample and cannot refit the frozen union. No C00 operation ran in offline checks.

`--replay-coverage-retained-plan` previews one future fixed final-minute pair;
`--replay-coverage-retained-pair <reviewed-plan> <sha256>` requires fresh disjoint
outputs, bound original ordered native selection, protected retained roots and
reviewed original-content/current-path mappings. It copies at most 200 MB/65
complete inputs through held readers, validates expected hashes, and runs the
ordinary packet/exact gates and two independent fresh decoders using the existing
sampler/native ledger/paired-owner paths. Seven total process roles, 600 seconds
whole, 130 seconds per export/60 per independent inspection, explicit retained/
physical commitments, 8,192 files/128 directories maximum and 60 GB free floor.
It makes no acquisition and no thirty-minute reexport. Exact-plan approval is
still required; this media operation is excluded from offline checks. Failures
retain evidence and admit no retry or alternate window.

`--replay-coverage-issue <accepted-confirmation-request> <sha256> <fresh-private-catalog>` creates a new receipt from the frozen union and validates both
genuine confirmations plus independent source/clock/endpoint acceptance, writes
only a fresh ignored private catalog and tests ordinary 1–30 resolution. It never
writes an account catalog or starts capture. Missing genuine evidence is a
rejection, not permission to synthesize confirmation fields.

## Existing focused routes

Choose the route below, read its complete effects and bounds, then preview its
supported `-Plan`/`--...-plan` entry before execution. An index entry or preview
does not authorize a live run. Preserve current recording holds, user state and
owned-process cleanup. New collection also follows
[evidence reuse and experiment admission](experiment-contract.md#before-new-recordings).

The detailed effects formerly on this page now live with the owning tests.
Generated-media, file-only, native and live routes have different effects even
when they share a launcher. Read the selected mode, including its engine/UI,
process, time and storage limits. Exact prospective plans remain authoritative
for their bound execution; historical plan numbers are not generic budgets.

## Route index

| Area / entry point | Effects to distinguish | Procedure and detailed launch effects |
| --- | --- | --- |
| Portable CoreChecks | Managed rules and local fixtures; no device capture or GPU; selected routes can write reports/files | [Profile rules](profile-migration.md), [replay precision](replay-precision.md#launch-effects), [storage rules](replay-output-demand.md#launch-effects) |
| `migration.ps1` | Full suite opens UI, injects input, probes codecs and captures desktop/system audio; `-FocusOnly` is a separate paused UI route | [Application migration](migration.md) |
| `worker.ps1` | Synthetic mode uses NVENC and capture-excluded UI that can stop NVIDIA Replay; live modes capture pixels/audio | [Worker lifecycle and soak](worker.md) |
| `window-capture.ps1` | Default generated/headless NVENC versus real-window pixel capture | [Window capture](window-capture.md) |
| `frame-pacing.ps1` | Visible controlled source, desktop/system-audio acquisition, GPU encoding and temporary awake/timer requests | [Controlled pacing](frame-pacing.md) |
| `audio-clock.ps1` | Generated PCM/timestamp checks and declared engine work; live content-sync evidence is separate | [Audio clock](audio-clock.md) |
| Game benchmark / lineage | Actual game/controller, capture and sampling effects; expensive batches require prospective admission | [Benchmark](game-benchmark.md), [frame lineage](frame-lineage.md), [admission](experiment-contract.md) |
| `profile-migration.ps1` | Paused/held workers, hidden shortcut registration or offscreen controls by mode; no capture/GPU | [Profile migration](profile-migration.md#launch-effects) |
| Storage UI / problem notices | Offscreen controls versus explicitly owned visible passive notices | [Storage controls](storage-controls.md#launch-effects) |
| `engine-trust.ps1`, `settings-validation.ps1`, security checks | Isolated trust/repair, generated media or hidden shortcuts as declared; settings validation does not launch an engine | [Engine trust](engine-trust.md#launch-effects), [security](security.md#launch-effects) |
| Replay ownership / retirement | Tiny file/claim fixtures, controlled export outcomes and separately bounded nonmedia process-lifetime controls | [Replay ownership](replay-ownership.md#launch-effects) |
| `capture-status.ps1` | Controlled managed freshness/queue checks; no recorder, native window or encoder | [Capture status](capture-status.md#launch-effects) |
| Replay exact/staged export and desktop actions | Generated-media NVENC/decoder routes versus tiny nonmedia controls, retained-source diagnostics and explicit live qualification | [Replay precision](replay-precision.md#launch-effects) |
| Latency baseline, pilots and operation diagnostics | Retained/generated media and native file operations; explicitly live baseline/worker routes capture desktop/system audio | [Replay latency](replay-latency.md#launch-effects) |
| Acceptance batch, observer and UI attachment checks | Live ten-pair batch versus counter-only, generated GPU, managed-only and paused UI controls | [Acceptance accounting and effects](replay-acceptance-accounting.md#launch-effects), [batch context](replay-latency.md#candidate-and-acceptance-boundary) |
| Capacity, catalog, storage observation and admission | Tiny native/managed fixtures and paused workers versus explicitly live capture-stop qualification | [Output demand and effects](replay-output-demand.md#launch-effects), [storage policy](../docs/storage-admission.md) |
| Calibration and continuation | Exact-plan capture/fixture/codec/process cohorts, retained-source routes and historical replacement charges | [Calibration effects and plan bounds](replay-calibration-effects.md) |
| Completed-history owner/fallback | Native file ownership/fallback checks without capture | [Completed history](replay-completed-history.md#launch-effects), [file comparison](replay-completed-history-file.md) |
| Owned-history qualification | Separately declared six-acquisition model and conditional four-save comparison | [Owned-history qualification](replay-owned-history-qualification.md) |
| Output metadata / manifest publication | Bounded dummy-file observations; manifest software route uses explicitly declared media children | [Output metadata](replay-output-metadata.md#launch-effects), [manifest](replay-manifest.md#launch-effects) |
| Pilot collector / storage / process | Inert preview versus explicit tiny-file observation or finite nonmedia children; exact reviewed host/helper identity required | [Collector](pilot-collector.md#launch-effects), [storage](pilot-storage.md#launch-effects), [process](pilot-process.md#launch-effects) |

## UI and capture preview

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

## Storage review regressions

Storage regressions are grouped under [output demand](replay-output-demand.md#launch-effects),
[Storage UI](storage-controls.md#launch-effects) and
[calibration plan bounds](replay-calibration-effects.md#launch-effects).
Each retains its specific effects and evidence applicability.

## Replay storage delivery checks

Use [output demand](replay-output-demand.md#launch-effects) for contention,
preflight, observations and publication; [replay precision](replay-precision.md#launch-effects)
for staging; and [ownership](replay-ownership.md#launch-effects) for producer recovery,
decode leases and retirement. Historical 500 ms contracts remain explicitly
scoped; they do not qualify the [2,000 ms candidate](../docs/storage-admission.md).
