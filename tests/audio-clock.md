# Audio/video acquisition clock

Run `tests/audio-clock.ps1` for common-epoch checks, bit-exact timestamped PCM
round trips in all five supported sample formats, and evaluation of the actual
FFmpeg timestamp expression. A delayed PCM write must preserve acquisition PTS.
The expression test checks a 100 ms acquisition-origin change and a later RTC
change without changing source frame intervals.

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
