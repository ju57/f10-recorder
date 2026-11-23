# Prospective accounting for the ten-pair reference batch

These rules are frozen with the collection inputs. They do not establish a
resource pass before collection and independent review. Raw failures, slow saves,
uncovered intervals and partial pairs remain in the report.

## Identities and windows

Join processes by both PID and UTC birth, never PID alone. The pair result and
closure record fixture/UI/capture identities and capture-ready/stopped,
worker-stopped and fixture-stopped QPC endpoints. The controller records the
worker-host and its own identities, exact held children and final counters.
Export, staged preparation and normal validator receipts supply additional exact
child identities/resources, including children too short for CIM sampling.
Ambiguous/missing identity is missing coverage, not zero cost.

Recorder work includes the worker host (including in-process packet mux/checks,
audio and helper observation), actual hidden UI, capture, all export/staging
children and every pre-Saved normal validator. Fixture, console and outer
controller/sampler are reported separately. Supplemental full decoders, file
hashes and shutdown are separate later work; they do not enter save-window CPU.
Never add a child's final counters twice when it appears in both receipts and
controller observations. Preserve both sources and reconcile disagreement.

Use the common QPC frequency recorded by the save timing. The two concurrent-save
windows are `[client requestAt, client savedAt]`; the Saved point includes event
delivery. Capture-only is the interval from capture-ready through actual capture
absence minus those two windows. Report the first ten seconds after capture-ready
as startup separately, as well as its inclusion in capture-only totals. Later
worker/UI/fixture closure and supplemental-decode intervals remain separate.
Queue must be zero; all schedule lateness and worker-trigger offsets remain.

## CPU and process I/O

CPU is total user+kernel CPU milliseconds. Divide by wall milliseconds times
the frozen logical-processor count, then multiply by100 for total-machine CPU.
Use duration-weighted totals, not an unweighted average of per-save percentages.
Report each pair, capture-only, each save, combined save windows and controller
overhead. The5% criterion concerns combined recorder work; separately report
recorder plus controller/sampler cost. No estimated observer cost is subtracted.

For long-lived processes, bracket each boundary with monotonic cumulative
counters from the same held birth. A known process birth supplies zero initial
CPU/I/O; an exact final receipt supplies the final cumulative counter. A row's
CPU/I/O reads occur between `qpc` and `processReadsCompletedQpc`; select the
whole bracket on the appropriate side of a window boundary. Do not
interpolate missing data as exact. For start counters `[Slo,Shi]` and end counters
`[Elo,Ehi]`, retain delta bounds `[max(0,Elo-Shi),max(0,Ehi-Slo)]`.
Require sample brackets no wider than five seconds. A child whose complete
lifetime is proven inside a save window contributes its exact final CPU/I/O
once. Unknown or straddling lifetime requires bounds or an explicit gap.

Classify CPU pass only when the conservative upper bound is at most5%; classify
failure when the lower bound exceeds5%; otherwise report inconclusive. Missing
required identities/brackets/counters prevents a resource pass. Native
GetProcessIoCounters read/write/other values include pipes, devices and metadata;
they are process I/O, not physical-disk bytes. Report exact lifetime totals and
window bounds separately, including work after Saved and all late diagnostics.

## RAM, GPU and disk

Report simultaneous per-sample sum of working sets and, separately, private
bytes for recorder processes. Shared pages can be counted more than once in
the working-set sum. Sum of independently timed peaks is not a simultaneous
peak. It may be shown as a conservative upper bound only when each participating
process has a complete lifetime peak witness. A live query of the native
high-water value establishes the peak only through that query; any unobserved
remaining lifetime stays a coverage gap. Preserve native peak queries, process
lifetimes and all final receipts. Settings stay hidden; RAM threshold is
1,000,000,000 bytes. A simultaneous lower bound over threshold fails; only a
complete conservative upper bound below threshold passes; otherwise inconclusive.

Report GPU utilization by physical adapter/engine and exact held process birth,
and dedicated/shared GPU process memory. Attribution requires the same retained
birth alive across adjacent observations and born before the previous PDH
timestamp. Three-sample query generations re-expand wildcard instances; reset
the interval proof at each generation and mark its first GPU row N/A. Per-path
valid/invalid counts remain visible. A process with no two
valid observations, including a short export, is N/A. Absence of a GPU instance
does not establish zero GPU cost. Do not add engine percentages into a purported
overall GPU percentage or invent unmeasured short-child peaks. Full GPU resource
acceptance remains incomplete if required work lacks coverage.

Report sampled physical-disk read/write rates for the frozen destination's disk
instance and for the other disks, with units and sample times. These rates
include other applications and cache/writeback; they are not causal attribution
or a replacement for process-I/O counters. Do not add `_total` to its component
disk values. No gaming-overhead or end-to-end causal improvement follows from
this observational resource collection.

## Statistical and failure record

Only twenty successful matched observations from ten fully closed valid pairs
can pass the speed gate. The overall median is the mean of sorted positions10/11;
nearest-rank p95 is position19. First/repeat strata contain ten values each:
medianmean5/6, p95position10; report paired repeat-minus-first differences.
Twenty observations are correlated within ten worker processes; no OS-cache-cold
or invented confidence-interval claim follows.

The first invalid pair stops the batch without substitution. In v2, the
prospective2,000,000,000-byte complete sealed root+admission stop is an experiment
storage limit. If breached, preserve the full12GB current slot and fail the
entire experiment, even if its Saved files were valid; never launch another
source or accept a20-success quantile. This is not a qualified product-output
bound. All prior failed-batch promises/media/two observations remain separate;
fresh twenty-sample collection imports none of them. Top-level output
must link partial observed Saved values, attempted acknowledgements/last-stage,
closure and controller/resource evidence, preserving the complete failed12GB
promise. Slow valid samples remain in the statistics. Sampler/tool failures are
recorded separately from product failures; neither silently becomes a successful
twenty-sample run. Resource acceptance, physical feedback,1/30-minute coverage and
release/maintainer acceptance remain distinct from the speed decision.

## Launch effects

These route-specific declarations are indexed by [test-effects.md](test-effects.md).
Read the selected route's complete effects and bounds before execution.

### tests/replay-observer-gpu-checks.ps1 -Plan

`tests/replay-observer-gpu-checks.ps1 -Plan` previews a generated-source GPU
discovery control. Run starts one verified nine-second320x192/60H264 color
encoder to the null sink, born after the first PDH query. It acquires no desktop,
system/microphone audio and creates no playback or UI. One SDK helper owns that
encoder through the ordinary verified executable lease/job/process receipt.
The outer observer requires positive GPU engine and memory samples for that
exact held birth in a later refreshed query generation, plus normal closure.
Limits:30seconds whole/25work5cleanup,64MiB root/32MiB early stop,60GB floor,
four named processes including helper/encoder/one console/outer. It changes no
installed/startup/vendor/profile/catalog state and is no live/resource/speed
acceptance. Its fresh absolute ignored root preserves diagnostics on failure.

### --replay-acceptance-batch-prepare

`--replay-acceptance-batch-prepare` prepares ten fresh worker first/repeat
pairs against the unchanged qualified five-minute product and passed frozen UI
host. Preparation reads display/default-audio metadata and preservation files,
hashes inputs and writes plans; it records nothing. Preview with
`tests/replay-acceptance-batch.ps1 -PlanFile <plan> -Plan`. Run requires separate
direct recording authorization and independent exact-plan review. No capture
authorization follows from selecting the cold/warm definition.

The batch permits ten primary-monitor/default-system-audio acquisitions, each
580 seconds and 900 seconds whole (5,800 acquisition seconds/9,060 seconds
whole batch), twenty unqueued Saved observations, a private moving fixture and
hidden actual UI per pair. First client requests target media epoch306+phase;
repeat requests target the next same fractional phase at/after316+phase and
prior Saved+250ms. Order/phase, quantiles, CPU/RAM accounting and resource gaps
are frozen before Run. OS file cache is not flushed. First invalid pair stops
the entire batch; no substitutions or retries are included. Slow values remain.
Only after both Saved and capture/UI/worker closure do two supplemental full
decoders run. No output reader enters either measured request.

Each pair retains its unchanged qualified 12 GB envelope. The replacement v2
experiment uses a31 GB whole-batch cap/30.5 GB stop/60 GB floor and a prospective
2,000,000,000-byte stop for each complete sealed root+admission footprint. After
closure, either all retained outputs/logs/profile/resource/decoder reports fit
that limit and collection may advance, or the entire experiment fails with
the full12 GB current slot retained. A breach never permits another capture,
substitution or20-success quantile, even if Saved outputs were valid. This is
an experiment storage limit, not a new qualified product-output upper bound.
Nine checked sealed pairs+one complete12GB current/failed slot+128MiB reports
forecast30,134,217,728 bytes. Prior35GB batch/full12GB failed promise/media and
two prior observations stay preserved and are not imported into the fresh ten.
The preparation/collection tree allows at most14 concurrent/141 cumulative
named processes, including preparation helpers, fixture/UI/capture/export sets,
consoles and one outer controller; operator tool/Release hosts are separate.
The one-second resource sampler uses a runspace in
that controller, adding no OS process. GPU attribution needs the same held
birth alive across endpoints and born before the prior PDH timestamp; short
children without coverage are N/A. Physical-disk counters include other apps;
process I/O includes pipes. No causal gaming/physical-feedback/1or30-minute/
release acceptance follows from this reference-speed batch.
The bound [accounting rules](replay-acceptance-accounting.md) define exact
role/window joins, CPU/I/O bounds, RAM/GPU coverage and inconclusive decisions.
The in-memory `ReplayAcceptanceNative.cs` supplies read-only held-process
I/O/memory queries and the helper's fixed2MiB stdout/stderr prefix copier; an
overflow is retained as a failure while the owned pipe continues draining.

### tests/replay-resource-observer-checks.ps1 -Plan

`tests/replay-resource-observer-checks.ps1 -Plan` previews a separate read-only
three-sample counter probe: at most12 seconds, one current-process held birth,
32 MiB report and60 GB floor. It starts no child, UI, capture, audio, ETW or
settings change. Run requires a fresh absolute ignored output path. It checks
all five GPU/memory/physical-disk counter paths, exact held-process samples and
confirmed runspace closure. Sampler rows are capped individually and as an
incremental31MiB prefix before enqueue; metadata/final report total32MiB.
`-Samples 6` uses a15-second whole bound and checks two finite three-sample
query generations. Each query re-expands wildcard instances and re-primes;
GPU interval proof resets, so every generation's first row is N/A. Native
held-process image/I/O/memory and process-read QPC brackets are checked.

### tests/replay-acceptance-stream-checks.ps1 -Plan

`tests/replay-acceptance-stream-checks.ps1 -Plan` previews six managed-only
checks of that actual prefix copier: empty/exact/overflow inputs, prefix bytes,
drain, cancellation and read failure. No Win32 call, process, UI, media or file
output occurs; its small test streams stay in memory.

### tests/replay-acceptance-admission-checks.ps1 -Plan

`tests/replay-acceptance-admission-checks.ps1 -Plan` previews fourteen
managed-only controls through the actual child-adoption function, sealed-pair
threshold and pre-launch reseal loop. Fake process getters exercise pin-before-
birth ordering and failed-candidate disposal; injected inventories exercise
2GB minus/exact/plus one byte and prior sealed growth. It compiles a small
in-memory fake, calls no native API and starts no process, UI or acquisition.
It writes no files or settings.

### --replay-acceptance-ui-plan

`--replay-acceptance-ui-plan` previews the frozen UI attachment check. It runs
the unchanged Avalonia App and real WorkerRecorder in a separate task-owned
process, connected to one private paused worker through its actual current-user
pipe. A capture-argument observer rejects any acquisition request; both hosts
use a private unavailable media root, so encoder probes cannot fall back to the
installed engine. It requires all three shortcuts before UI launch and passes
`--paused`. The UI joins a single-process job before App startup; job accounting
rejects fallback-child attempts, including ones too short for CIM polling.
Ready receipts are published atomically and checked against held birth identities.
The exact sticky `SettingsPanel.loaded` Boolean must remain false at startup,
every100ms and closure, with hidden panels; a two-second control wait exercises
deferred attachment work. A passing run establishes no settings-entry/probe path
occurred in that run. Any violation fails and ends that pair.
It requires
paused/no capture, hidden settings/overlay, actual worker PID/birth/instance,
normal owned UI Quit and worker joins. It makes no save, source/encoder/decoder,
system/microphone acquisition, playback, provisioning, startup, vendor or
account-catalog change. The only UI state is its private tray/normal lifecycle.
Preparation freezes the UI/product/framework files; preview the plan with
`tests/replay-acceptance-ui.ps1 -PlanFile <plan> -Plan` and obtain independent
review before Run. Bounds: 70 seconds whole/50 work/20 cleanup, 64 MiB output/
48 MiB stop, 60 GB floor, 256 files/64 directories, one UI child and at most one
console (five processes including helper, preparation and outer controller).
This check establishes attachment only, not live/resource/feedback acceptance.

The separate `--replay-memory-observation-check` control commits64MiB in its own
process and reads current native memory counters; require the prospective
10second/256MiB hash-bound outer controller. It writes a small local report and
starts no child, recorder, engine, capture, UI or device work.
