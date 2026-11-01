# Exact full replay checks (issue #14)

The maintained replay exporter uses the full interval re-encoding correctness
candidate from [the precision experiment](precision-export-prototype.md).
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
and validation time are carried in status metrics. Publication follows a closed
MP4's full software decode, expected tracks, video frame count, initial PTS,
cadence, cumulative PTS against frame index and combined endpoint checks.
Validation failure prevents rename.
Runtime decoding does not recognize arbitrary picture content; the independent
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
