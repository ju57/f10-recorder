# Audio/video acquisition clock

Run `tests/audio-clock.ps1` for common-epoch checks, bit-exact timestamped PCM
round trips in all five supported sample formats, and evaluation of the actual
FFmpeg timestamp expression. A delayed PCM write must preserve acquisition PTS.
The expression test checks a 100 ms acquisition-origin change and a later RTC
change without changing source frame intervals.

Native device positions order complete PCM packets. Continuous packets retain
their bytes even when individual QPC observations jitter; there is no padding or
trimming at those boundaries. The first trustworthy observation anchors this
stream to the existing session epoch. Subsequent observations update phase at
roughly one-second intervals, with a 0.1 smoothing factor and at most one
millisecond of correction per update. The declared modeled envelope is +/-500
ppm with +/-0.6 ms alternating timestamp jitter: thirty-minute core checks bound
settled timing error to 8 ms. This is a model, not live hardware acceptance.

Acquisition packet PTS may differ slightly from nominal PCM duration. The real
FFmpeg audio path uses `aresample=async=100:min_comp=0.001:min_hard_comp=0.1:comp_duration=1:flags=res`
to reconcile gradual drift without inserting zeros per packet. Soft compensation
can stretch/squeeze up to 100 samples/second; hard compensation remains possible
at an actual 100 ms discontinuity. Bit-exact decoding before that filter and
signal/marker checks after the actual filter/AAC encode establish different facts.
The synthetic source retains its existing filter.
Resampling initializes even when input/output rates match. A lazy initialization
at the first positive correction can reanchor PTS instead of continuing gradual
compensation; threshold fixtures cover both signs around 1 ms and positive steps
below/above the 100 ms hard threshold using the actual production filter constant.

Declared silence and actual inactivity have format-correct silence heartbeats.
After emitted inactivity, resumed packets reanchor to trustworthy QPC even on
endpoints whose device frame counter pauses and resumes without a counter gap.
Counter gaps/discontinuities establish a marked, trustworthy acquisition boundary.
Overlapping reset ranges, unmarked backwards counters, unexplained clock jumps
over 100 ms, initial timestamp errors or more than five seconds of timestamp
uncertainty fail capture for worker recovery. For a short timestamp-error run,
counted frames predict continuity and the next trustworthy counter reconciles it;
ignoring the reported counter during that run is a conservative policy. Negative
initial frames are explicitly clipped at the session origin. Late packets cannot
overwrite emitted idle audio. These policies do not silently discard a reset's
payload or reset the video/save epoch.

`tests/audio-clock.ps1` also decodes the actual portable packet-delivery fixtures
in all five supported formats, requiring bit-exact native payload after jitter,
silent flags and timestamp-error recovery. Separate fixtures verify true gaps,
idle-to-audio, pre-epoch clipping and overlapping-reset rejection. Live validation
still needs ordinary named-pipe/FFmpeg backpressure, coded A/V markers, repeated
five-minute exports and current CS2 listening before promotion.

Real system audio uses QPC positions on one session epoch. A streaming Matroska
PCM pipe preserves those positions at microsecond resolution while retaining the
100 ms audio delivery cushion. Video uses a one-time UTC bridge into the same
epoch, then retains ddagrab's timestamp intervals. Separate bounded input queues
still share FFmpeg's scheduler; delayed audio can throttle video acquisition.
The existing `fps`
filter supplies CFR pictures; a second output CFR conversion must not fabricate
frames before the first acquisition.

The first GOP is startup pre-roll while the two devices become available. It is
excluded from buffer readiness and export. Subsequent closed GOPs retain their
normal independent decoding and pruning/pinning rules. Export explicitly maps
the expected audio track so missing codec parameters cannot silently produce a
video-only successful save.

Live verification must identify the test binary and FFmpeg arguments, use an
isolated profile, and preserve installed settings and recordings. Compare coded
chirp/flash markers against source presentation logs on cold and warm starts,
then inspect a full five-minute replay: marker coverage, offset, drift, frame
repeats, all-track decoding, PTS/DTS, and segment boundaries. Container start/end
skew alone is not perceptual sync. Do not accept an absent audio track or loosen
source/pacing gates to make a trial pass. Physical DAC/scanout latency and an RTC
step during the initial bridge are outside this software-clock measurement.

References: [FFmpeg9.0.2 ddagrab source](https://github.com/FFmpeg/FFmpeg/blob/n9.0.2/libavfilter/vsrc_ddagrab.c),
[setpts expression semantics](https://ffmpeg.org/ffmpeg-filters.html#setpts_002c-asetpts),
[Matroska PCM mappings](https://www.matroska.org/technical/codec_specs.html), and
[Matroska element specification](https://www.matroska.org/technical/elements.html).


Save cutoffs on the common-clock system-audio path use the capture session's
existing QPC media epoch, sampled once when Save is handled before async work.
FFmpeg's `-copyts` progress reports can subtract a first-report origin; they
remain liveness/metrics only and must not be compared directly with manifest
times. Source stop freezes the first stop request; retained exports still clamp
to the same session's last completed segment. A trigger older than a replacement
epoch cannot save that replacement's footage. Synthetic/audio-off paths retain
their existing progress-based behavior.

Portable checks cover nonzero origins, report-origin differences, fractional
selection/export bounds, UTC independence, concurrent repeated stop, stale
generation and the five-minute/keyframe allowance. Live endpoint checks must
retain actual selected TS/snapshot inputs and exact trigger/epoch/cutoff receipts;
source submission age remains a bounded proxy, not acquisition/scanout or a
physical hotkey latency qualification. No constant offset should be fitted.
