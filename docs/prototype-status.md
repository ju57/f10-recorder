# Early prototype integration — 20 October 2026

This source integration includes the independent Windows worker, replay controls
and passive notices, common acquisition clock, replay save cutoff correction,
native audio packet continuity, and optional experimental window capture.
Monitor capture remains the default for existing profiles. Linux capture is
not implemented. This is not a public binary release or installation approval.

The integration starts from candidate `b4809597a3542b69ac29c6755baa2e59fa91a118`.
Separate experimental delivery/pacing changes are outside this integration.

## Bounded gameplay evidence

The latest local CS2 session produced two overlapping replay windows of 299.650
and 299.400 seconds, with 1920×1080 H.264 at 60 FPS and 48 kHz stereo AAC.
Both complete streams passed strict decoding, with no missing, equal or decreasing
per-stream packet DTS or decoded PTS. Video timestamp intervals were 1/60 second.
These checks establish media integrity and timestamp cadence, not fresh source
pictures, perceptual synchronization, or broad hardware/game acceptance.

The user reviewed approximately the first two minutes of the first replay:
no visible stutters were reported, but occasional slight smoothness/FPS drops
were reported and remain under investigation. Neither clip has complete human
review. An earlier listening check reported clean, synchronized audio; this is
subjective evidence, not a coded A/V marker measurement.

Both saves succeeded in about 16–17 seconds. Success notices were requested for
both; the user missed the first notice. Actual notice visibility was not measured.
The session helper's final 20-second continuation was incomplete because the user
closed the game. Owned processes shut down normally; this is not a completed
continuation test. Local reports record preserved recorder settings, startup,
candidate binaries and existing user media.

Rare synthetic 50 ms picture holds remain unresolved: nine across 18 recent
clips, three in each instrumentation condition. Cause and observer overhead have
not been qualified. These findings remain visible limitations of this prototype,
not a claim of capture parity with NVIDIA.

Machine-specific reports and media remain under ignored `artifacts/`. Some older
bulky evidence was retired; historical results without retained inputs cannot
be represented as fully reproducible.

## Verification and release boundary

Portable checks cover replay selection, settings migration, clock mapping and
audio packet policies. The audio-clock script additionally checks actual FFmpeg
PCM round trips and compensation thresholds. Synthetic worker checks exercise
lifecycle/recovery and exports. Hosted Ubuntu core checks and Windows builds are
required for merging; neither hosted CI nor synthetic checks proves live capture.

Before distributing an alpha binary, settle exact FFmpeg dependency notices and
corresponding-source packaging, validate sign-in/reboot and deliberate pause,
and declare the supported hardware/driver/game matrix. Fullscreen/HDR behavior,
device changes, long runs, storage failures, coded A/V alignment, window-capture
coexistence and complete human gameplay review retain their separate acceptance
requirements. No installation, release assets or announcement accompanies this
source integration.
