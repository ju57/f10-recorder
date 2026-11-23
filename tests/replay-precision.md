# Exact full replay checks (issue #14)

The controls on this page qualify the full-interval exact re-encoding path from
[the precision experiment](precision-export-prototype.md). It remains the
candidate's conservative fallback. The selected normal packet/boundary path and
its different checking contract are described in [latency checks](replay-latency.md).
Completed inputs keep the acquisition timeline, independent GOPs and per-request
pins. A generated, file-only safe concat manifest includes explicit inpoints;
decoder preroll outside each interval is excluded. Video restores its configured
frame clock at a 48 kHz timebase before trimming. Audio retains the same requested
origin. No duration metadata change, edit-list preroll, history consumption or
synthetic video padding substitutes for the selected pictures.

Admission freezes the endpoint and available start, requires one second, and
reserves the preceding partial head before pruning. Missing frozen boundaries,
gaps and overlapping source ranges fail. Warm-up/unavailable history is reported
with available/requested duration. Repeated full saves may overlap.

An accepted request is not Saved. The worker reply's `ReplayAccepted` describes
admission only; `ReplaySaveState`, interval fields, validation kind/frame count
and validation time are carried in status metrics. In this exact fallback,
publication follows a closed MP4's full software decode, expected tracks, video frame count, initial PTS,
cadence, cumulative PTS against frame index and combined endpoint checks.
Validation failure prevents rename.
The candidate normal packet path instead requires complete packet/configuration,
source-byte, count/timing and checked-object publication evidence. Fresh full
decoding remains mandatory for its qualification and diagnostics; it is not an
equivalent fresh semantic check before every normal Saved. Runtime decoding in
the exact path does not recognize arbitrary picture content; the independent
generated-source oracle below supplies that separate evidence.

## Reproduce without device capture

Build with the pinned SDK and `build-avalonia.ps1`; build WorkerChecks to
`bin/issue14`. Supply an existing approved managed engine root, not a PATH tool.
The script never provisions or downloads an engine. Preview effects before
preparing a fresh plan:

```powershell
./tests/replay-precision.ps1 -Plan -Mode short -EngineRoot <approved-root> -OutputRoot artifacts/issue14/short-new -PlanFile artifacts/issue14/plans/short-new.json
./tests/replay-precision.ps1 -Prepare -Mode short -EngineRoot <approved-root> -OutputRoot artifacts/issue14/short-new -PlanFile artifacts/issue14/plans/short-new.json
./tests/replay-precision.ps1 -Run -Mode short -EngineRoot <approved-root> -OutputRoot artifacts/issue14/short-new -PlanFile artifacts/issue14/plans/short-new.json
```

Short mode checks H.264, HEVC and AV1 at 24 FPS with warm-up, after-IDR,
fractional-frame and repeated overlapping requests, plus a corrupt-MP4 negative
control. `rates` checks 30/60/120 FPS across the same codecs. `windows` checks
one/five/thirty-minute H.264 intervals, overlap, warm-up and an exact IDR.
`clock` checks a nonzero source epoch, excluded startup segment and both audio-on
and video-only exports at 60 FPS. Use fresh plan/output names for each mode;
preserve failed attempts.

`integration` uses three actual children (a 24-second generated capture, export
and validator) through `CaptureSession`, with a 45-second wall limit and 256 MiB
local cap in addition to the family/free-space bounds. It records independent
export/validation receipts and requires the same capture PID with advancing
frame counters and completed segments across publication. This is backend
wiring evidence, separate from the content-ID oracle and live devices.
`corrupt` uses the same three-child boundary but replaces only its closed owned
partial file with invalid bytes. Actual backend validation must reject it and
publish no final MP4 while generated capture continues.
`drift` admits two children in45seconds: generate a decodable60-frame file with
small17.5ms steps, then require the actual validator to reject accumulated drift
that nominal frame count and individual-step tolerances would miss. The
verifier uses FFmpeg's [select timestamp/index variables](https://www.ffmpeg.org/doxygen/9.0/f__select_8c_source.html).

Plans bind the SDK, consumers, runtime metadata, approved engine and harness;
preparation archives consumer/harness bytes. Execution admits at most 45/117/19
children for short/rates/windows, or 26 for clock, with 600/600/1800/600-second whole-run deadlines,
an 8 GB cap across retained sibling attempts and 20 GB caller-free floor. Sources
are 320x192 generated pictures with absolute binary IDs/complementary rows and a
synthetic tone. There is no device capture, audio playback, visible UI, shortcut,
user profile/startup/vendor change, download or calibration activation.

Generated correctness checks do not expand production storage applicability.
The empirical reference recipe is primary1920x1080, H.264,60FPS,20Mbps, system
audio and a five-minute setting. Other durations, codecs, FPS, audio-off/window
capture and dimensions remain unmeasured for ordinary production admission.
Live reference calibration uses a separately authorized/reviewed admission; see
[launch effects](test-effects.md). Its worker case uses an isolated candidate
catalog and leaves the account-default catalog untouched. Required user acceptance
and the broader G-REPLAY matrix remain separate from these technical results.

Every saved file is independently decoded with MP4 edits ignored. All picture
IDs must form the matching source sequence, with no gaps/duplicates and at most
one output frame of start/end/duration rounding against the original request.
Strict exact-ceiling equality is reported separately: submicrosecond requests
near a frame boundary can round onto that boundary in the 48 kHz export timebase.
Original source hashes must survive overlapping saves. Reports keep
accepted export-work time (excluding admission/task scheduling), validator
process duration and offline content checks separate;
generated small pictures do not qualify production resolution/resource costs,
latency percentiles, live acquisition, perceptual A/V sync or hardware coverage.

Portable CoreChecks cover 1/5/30-minute rules at 24/30/60/120 FPS, minimum duration,
missing history and gaps/overlaps. `--replay-output-validation` checks failure
before publication, subsecond admission, combined endpoint rounding, worker
acknowledgement semantics and completion-owned notices. Existing replay ownership
checks retain hard-link/copy fallback, pruning, retirement, capacity and failure
coverage. A hard-link-denying sandbox exercises fallback; native hard-link
qualification requires permission to create links among owned fixture files.

`MigrationTests <fresh-root> --replay-notice-ui` renders the actual warm-up notice
and checks disclosure fits. See [test effects](test-effects.md) for its bounded
passive window and explicit exclusion-disabled behavior.

## Production acceptance still required

`tests/replay-calibrated-validation.ps1 -Reencode -Plan` previews one combined
three-process/210second diagnostic using retained captured TS, an actual frozen
calibrated model, private durable ledger, the ordinary native snapshot reader,
real exact encoding and full runtime decoding. The500ms observations remain
active throughout; publication is suppressed. The38GBcap/36GBstop/60GBfloor
bounds include prior evidence. Guard and callback timing, terminal output identity
and metadata release are retained separately. It exercises the collector's
evidence hooks without new capture; capture-claim retirement still needs live
verification. See [launch effects](test-effects.md).

The existing-source `tests/replay-captured-transition.ps1 -Plan` route previews
three children in210seconds: one helper, one NVENC encoder and one CPU decoder.
It has no new acquisition, window or audio playback. The helper checks lossless
trace draining under a650ms writer stall, nonblocking queue-overflow rejection
and sticky writer errors, then injects a650ms trace write stall during actual
native storage sampling and encoding. Fresh observations retain their500ms
requirement. The writer queue holds at most1024 rows of8192bytes and64MiB total
output; overflow, writer error, missing rows or failure to drain within3seconds
prevents qualification. The26GB retained-family cap and60GB free-space floor
remain enforced. A diagnostic sentinel suppresses publication; earlier native
failures stay failed.

The new exporter/capture recipe invalidates copy-export storage calibration.
Production admission remains fail-closed; old calibration receipts are not
relabelled. Legacy live calibration/continuation plans reject before device/UI
work because they omit the new recipe and runtime-validation child allowance.
New empirical storage qualification and ordinary-worker/native task acceptance
need a separately reviewed finite admission and current-task authorization for
desktop/system-audio capture. Generated files and CI do not supply that evidence.
Issue #14 stays open until its required validator acceptance is recorded;
G-REPLAY and the other release gates remain separate.

## Launch effects

These route-specific declarations are indexed by [test-effects.md](test-effects.md).
Read the selected route's complete effects and bounds before execution.

### --replay-staging-native-plan

`--replay-staging-native-plan` previews three actual staged-process controls
using two generated seven-second H.264/AAC fixtures. They acquire no desktop,
system or microphone audio and produce no playback/UI. The first control starts
a real waiting staged child, encounters valid long interior GOPs, joins that
child and requires the exact exporter plus full decode before publication;
a later decoder independently checks all 360 ordered absolute source IDs.
The other controls cancel before feeding and during a real paced pipe feed;
both must join actual child/feed and publish no MP4. Pacing/probe arguments change
only the latter test child. Private SourceBound fixture promises and real native
ledger/Cache paths are not an empirical calibration or live/performance claim.
Preparation freezes the runtime/product/controller inputs; preview its fresh
plan with `tests/replay-staging-native.ps1 -PlanFile <plan> -Plan`, obtain independent
review, then Run. Bounds: 120 seconds whole, 512 MiB output/384 MiB stop, 60 GB
free floor, 512 files/64 directories and eight media children (12 total including
helper, console, preparation and outer controller). Original generated inputs,
failure reports and published acceptance example stay under ignored artifacts.
No installed profile, startup, vendor, account-catalog or installation change.
`--replay-staging-native-identity-checks` runs eight in-memory checks of the
auxiliary oracle's published-object comparison: volume/file ID/EOF/unit remain
mandatory, while allocation size is not immutable after closing writer handles.
It starts no process or native/media work. The actual native route persists both
full extents and the publication/identity/allocation comparisons before its gate.

### --staged-process-checks

WorkerChecks `--staged-process-checks` runs managed fake transport/stream checks
before recorder initialization. It covers accepted-only launch, withheld input,
first/second candidate rejection without healthy repair, original-owner guards,
inclusive expiry, blocked feeder cancellation, source-hash failure and uncertain
join retention. In-memory checks also reject changed final-index hash/size/PPS,
unsupported boundary attributes/links/growth, and a never-completing notification
at the original five-second tail deadline. No real child, native API, media,
device, UI, worker control or capture is started. A fresh local runner bounds
the actual pinned dotnet process to50active/60whole seconds,512MiB and64KiB logs.

The staged-Cache diagnostic additionally calls `ReplayStagedWorkspaceChecks`
only inside its separately reviewed native plan. It creates empty task-owned
scratch through rooted native directory/file capabilities and a fake process
transport; rejects live/duplicate capability transfer, duplicate binding and
foreign workspace reuse; then verifies actual rooted work retirement. These
controls start no real media process. The separately declared retained-footage
comparison owns its exact engine/product/helper bytes, real media process roles,
storage/time/resource bounds and independent review; managed controls do not
admit those effects or establish actual-worker/performance acceptance.

### --boundary-projection-only

CoreChecks `--boundary-projection-only` exercises shared local/full physical
picture-clock and partial-boundary rules with in-memory metadata fixtures. It
covers1/5/30minutes,24/60/120FPS, adjacent floating-point cutoffs, warm-up/preroll,
transport wrap and changed-source/configuration/later-source/AAC rejection.
It returns before fixture-directory creation and starts no child process,
native API, media decoder, device, UI or capture. It proves planning only;
staged-process ownership, actual media and performance need separate checks.

### tests/replay-calibrated-validation.ps1 -Plan

`tests/replay-calibrated-validation.ps1 -Plan` previews an A/B of the retention
adapter and ordinary native snapshot reader with the actual frozen calibrated
model and a private durable ledger. It uses existing captured TS/MP4 only:
one helper and two CPU decoders in210seconds,32GB family cap/30GB stop/60GB floor.
No acquisition, encoder, UI, playback, settings/startup or catalog activation
occurs. Product guard/estimator timings stay in bounded memory receipts. Owned
MP4 evidence aliases are identity-deduplicated; metadata handles close before
exit. Publication is suppressed and prior failures remain failed.

The same script's `-Reencode -Plan` route previews a combined retained-source
transition: one helper, one NVENC encoder and one CPU full decoder in210seconds,
38GB retained-family cap/36GB stop/60GB free-space floor. It uses actual completed
TS inputs, the frozen calibrated provider/applicability, private durable claims,
the ordinary native snapshot reader and the collector's evidence wrapper. All
500ms guards remain active through encoding and validation. Output preservation
creates a hardlink during that transition; identity/length queries and metadata
release occur after producer absence. Bounded guard/estimate telemetry is retained
in128-row files, alongside producer-callback/hardlink timing and terminal alias
proof. A sentinel suppresses publication. No acquisition, window, audio playback,
installed-state change or account calibration activation occurs. Passing this
diagnostic does not qualify a failed live source or prove capture retirement.

Existing-source encode/validation and bounded trace-writer fault effects are
listed in [exact replay checks](replay-precision.md#production-acceptance-still-required).
Preview `tests/replay-captured-transition.ps1 -Plan` before its separately
reviewed three-child diagnostic; it performs no new acquisition.

The issue14 exact native route requires current-task authorization and an
independently reviewed hash-bound admission. Preparation reads current formats
and filesystem allocation without acquisition. Live execution also requires a
cumulative phase receipt and passed nonmedia rehearsal. The explicit rehearsal
route `--replay-calibration-issue14-rehearse` uses real cache snapshots and the
storage sampler with dummy payloads, no devices, media children or window.
Retained dummy inputs may be linked read-only; a new output grows under the
actual export storage callback. Shared payload geometry is recorded explicitly.

### tests/replay-calibration.ps1

`tests/replay-calibration.ps1` without `-Run` previews the live launch; its `-Run`
is **not** the nonmedia rehearsal. The issue14 route captures primary-display
pixels/default WASAPI playback audio, displays owned fullscreen fixtures and
plays quiet chirps. Bounds: six acquisitions under six minutes each,48 total
media/helper processes including preparatory helpers,20GB retained allocation,
18GB stop,60GB caller floor and90minutes including charged preparation. Three
training cases precede three held-out cases; exceedance fails without refitting.
H03 uses the actual worker/host/protocol with private authority and a candidate
catalog override. It never activates the account-default catalog. Accepted-source
snapshots, outputs and failure receipts remain. Failed-case cleanup closes the
owned process job before joining accepted work; the outer launcher enforces an
absolute deadline with cleanup reserve. No microphone, installed-profile/startup,
display/audio preference, game/NVIDIA change, installation or release is admitted.
The separately reviewed correction boundary charges two prior acquisitions and
24 prior media/helper processes, then permits six fresh acquisitions under an
eight-acquisition/68-process cumulative ceiling. It keeps the20GB/18GBstop/60GB
floor/90minute bounds, with stricter850MB source and800MB output components.
The old binary's qualification is never transferred to the corrected binary;
all six sources are collected again only after explicit task authorization.
`--replay-calibration-issue14-checks` tests both exact numeric/path boundaries
with tiny files and no device/child/media work, and runs in Windows CI.
The separately admitted output-lease boundary uses nine cumulative acquisitions,
80 media/helper processes,26GB cap/24GB stop,60GB floor and120minutes including
prior attempts. It requires the full-size nonmedia metadata comparison, retains
output metadata handles through active work, and measures every declared final
after a30s bounded offline release. Pre-release peaks remain in the evidence;
final allocation is recorded after release. The scope requires explicit current
task approval; a plan alone does not authorize expanded effects. All three exact
resource boundaries have positive and one-over nonmedia checks.

Precision `integration` mode starts exactly three media children: a bounded
24-second synthetic `CaptureSession`, its real export and full validator. It
requires 45 s whole-run/35 s active+cleanup reserve and 256 MiB local storage, under
the 8 GB family/20 GB caller-floor bounds. It checks separate actual child receipts
and advancing generated capture across publication; no device or desktop/audio
capture, visible UI, profile/startup/vendor change or model activation runs.
The same three-child `corrupt` sensitivity replaces only a closed owned partial
with invalid bytes and requires actual decoder rejection without publication.
Precision `drift` mode admits two generated-media children in45s: create a small
60-frame file with gradual timestamp drift and run the actual validator. Full
decoding must succeed while cumulative timing validation rejects; no devices,
UI or user state are involved. The same family/free-space bounds apply.

### F10Recorder.MigrationTests

`F10Recorder.MigrationTests <fresh-root> --replay-notice-ui` shows one owned
passive notice for under 10 s and renders only that notice to PNG. It verifies
available/requested duration and unavailable-history copy fit the actual card.
The isolated process disables capture exclusion before constructing the window;
no recording/engine, desktop/device/audio capture, preference/startup/vendor
change or capture-affinity request runs.

### F10Recorder.MigrationTests

`F10Recorder.MigrationTests <fresh-root> --replay-action-checks` exercises the
actual offscreen Settings Save button and worker-client event policy with
controlled replies. It checks admission, busy clicks, validated publication,
warm-up disclosure, explicit rejection and validation failure separately. It
creates no native Window/worker/engine, capture, input or preference/startup write.
Media validity and real worker publication are separate checks.

### tests/replay-precision.ps1 -Plan

`tests/replay-precision.ps1 -Plan` previews a finite generated-media route.
`-Prepare` freezes the built consumer/SDK/managed-engine/harness hashes in an
ignored plan, without launching media. `-Run` requires that unchanged plan and
a fresh ignored output root. Short mode admits at most45 owned children in 600 s;
windows mode admits 19 in 1800 s; clock mode admits 26 in 600 s for a nonzero
epoch, excluded startup and audio-on/video-only exports; rates mode admits 117 in 600 s for 30/60/120 FPS
across all three codecs. Each retains an 8 GB evidence cap across retained
sibling attempts and 20 GB
caller-free floor, bounded child output/deadlines and owned-process receipts.
It generates 320x192/24 FPS absolute picture IDs with complementary rows and a
synthetic tone, then exercises real replay snapshots, exact export, runtime
full decode, final publication and independent whole-file pixel-ID decoding
with MP4 edits ignored. No devices, desktop/system-audio/microphone capture,
audio playback, UI, shortcut, user profile/startup, NVIDIA preference, engine
installation/download or calibration activation runs. Failed attempts remain;
no automatic retries. These generated checks do not qualify live acquisition,
storage calibration, ordinary-worker latency or the supported hardware matrix.

### --replay-staging-admission-plan

`--replay-staging-admission-plan` previews actual Cache unmeasured-admission and
native staged-workspace controls. `--replay-staging-admission-checks <fresh-root>`
uses tiny 188-byte and 4096-byte dummy inputs with simulated transports to verify that unmeasured
training stays unstaged, live/unjoined boundaries cannot transfer, closed
capabilities transfer once, and ordinary cleanup retires owned scratch. A staged
launch worker invokes the actual Cache producer callback against its held
authority session, with a blocked ledger write; stop remains available and claims
remain held through callback/producer closure. Known joined cancellation/failure
releases ownership and admits another request without publishing either failed
output. Another actual Cache control suspends a read-only capacity policy after
controlled validation, preserves a checked2048byte dummy publication, and checks
owner-thread cleanup plus a second successful save with 4,097 unrelated empty files in the destination. A third request delays the rename past the capacity horizon and must leave only the two successful final MP4s. Its deliberately inline
completion task can expose the former asynchronous thread migration; the staged
path must keep that wait synchronous outside the Cache gate. A final control
withholds one completed tail, requires reserved preparation to have started
while no input snapshot has yet been materialized, then requires the complete
protected set and checked publication after tail closure. Its timing asserts
zero pre-tail protection and protection beginning at or after `TailClosed`. Bounds: 1 MiB of payload plus metadata for 4,097 empty fixture files, 45 seconds,
at most one empty job at a time, simulated transports and one bounded managed
completion thread; no child processes, playable media, capture, audio, UI, profiles
or account-catalog changes.
`--replay-volume-reservations` uses tiny files, controlled capacities, native
hardlinks and short owned nonmedia children for cross-process serialization.
`--replay-calibration-phase-checks` writes a bounded 1 MiB dummy file and injects
650 ms active/offline observer delays plus an offline deadline failure. These
are enforcement controls, not full-size rehearsal or calibration.
