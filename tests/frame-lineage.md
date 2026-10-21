# Repeatable capture and frame lineage

This procedure defines evidence for source-picture lineage, controlled gaming
overhead and NVIDIA-parity claims. A diagnostic observer result alone does not
justify installation or a performance claim. Early prototype source integration
is separate; see [prototype status](../docs/prototype-status.md). Preserve the
installed recorder, recordings and game/NVIDIA preferences. Restore temporary
changes to their recorded baseline after each control window.

## Freeze each attempt

Use one owner for the game, NVIDIA controls and capture. Pin the game build,
executable, demo, display, driver, source fixture, FFmpeg, baseline and candidate
assemblies. Keep an ordered manifest of every attempt and rejection. Give each
attempt a new isolated profile and output directory. Do not install a build or
decode clips during timing windows. Release the computer-use helper before
performance collection; its presence can also interrupt NVIDIA capture.

A saved game replay is preferable to integrating held camera input independently
on every trial. Qualify two independent loads, one fixed recorded player POV,
three matching observed tick/landmark anchors, and continuous motion throughout
the selected interior. An accepted command or seek target does not establish the
actual rendered camera, simulation tick or measurement phase. Record the actual
source-tick/QPC relationship separately from input-submission timestamps.

Qualify the current game build and replay route rather than reusing historical
seek/prediction results. Record and restore temporary camera, prediction and FPS
settings, including `cl_demo_predict` if changed. Bot-free rotation measures a
limited rendering workload, not online multiplayer, input latency or combat
effects. Keep rejected replay routes and their reasons with the trial evidence.

## Keep picture identity separate from time

Use a checksummed rendered source ID and complementary row, with source rate
at least 1.5 times output rate (target twice output), separately for 60 and
120 FPS. Log explicit source ID and `GetLastPresentCount` immediately after
Present. Validate the completed inventory offline:

```powershell
python tests/validate-frame-submissions.py <source-directory> --output <new-json>
```

That counter identifies Present calls. `GetFrameStatistics` describes a
presentation snapshot which can precede the current rendered ID; its SyncQPCTime
does not identify a Desktop Duplication picture. Never join those identities by
nearest timestamp. A submission-cadence result is not displayed-source cadence.
See Microsoft's [frame-statistics contract](https://learn.microsoft.com/en-us/windows/win32/api/dxgi/ns-dxgi-dxgi_frame_statistics),
[Present-call counter](https://learn.microsoft.com/en-us/windows/win32/api/dxgi/nf-dxgi-idxgiswapchain-getlastpresentcount)
and [Desktop Duplication frame information](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/ns-dxgi1_2-dxgi_outdupl_frame_info).

The native chain must retain exact allocation generations, AVFrame ref/move/
unref occurrences, FPS FIFO selections, encoder acceptance and output packets.
An object address, storage address or periodic PTS alone is insufficient. An
unref call can release a buffer before returning; cross-thread reuse during its
remaining cleanup requires explicit interval/generation accounting. Reject
ambiguous, missing or unexplained relevant edges and observer overflow/errors.

Freeze the observer **after** saving the clip while capture remains alive, then
drain callbacks, export traces and stop the isolated owner. Freezing before save
can omit encoded packets later included in the saved clip. Record boundary
in-flight frames separately from losses.

For the pinned clear H.264 MP4 path, compare exact ordered VCL NAL bytes from
native encoder packets and physical saved packet ranges. Validate a bounded
ffprobe `show_data` prefix against those physical bytes and retain the avcC
configuration. Require a unique complete alignment; timestamps may validate an
already-established identity, but cannot choose it. Join actual packet PTS to
complete decoded-frame PTS and checksummed IDs. Do not retain a huge hex dump
when bounded physical reads and a byte-equality witness suffice. Other codecs,
encrypted/transformed samples and fragmented containers need separate contracts.

## Acceptance remains separate

- Measure same-binary observer-off/on brackets before treating instrumented
  performance as ordinary performance. Proposed bounds are 2% FPS and 5% p99.
- Preserve five counterbalanced paired blocks per claimed output mode, 40-second
  traces with a prospectively frozen inner window, plus recorder-off drift
  controls. The historical [10,35) window is usable only when qualification
  establishes readiness and complete source/status coverage around it. Apply
  [experiment admission](experiment-contract.md) before an expensive batch. Retain
  rejected attempts and replace whole pairs. Use paired run-level uncertainty,
  not frames as independent replicates. Proposed upper confidence bounds are
  2% FPS loss and 10% p99 increase.
- Require complete selected-source-to-saved-picture coverage, no backward or
  invalid IDs, and explain holds longer than two output periods. Barcode repeats
  alone do not identify where a frame was lost.
- Fully decode every saved audio/video stream. Reject missing, equal or decreasing
  integer per-track DTS and non-increasing actual decoded PTS. Check rational
  output cadence and A/V boundary skew within 50 ms. Boundary timestamps do not
  establish perceptual synchronization.
- Separately verify physical save shortcut, default five-minute/system-audio
  behavior, coded A/V event alignment, and a 30-minute run per claimed mode.
  Proposed event bounds are 50 ms alignment and 20 ms drift. A soak establishes
  durability, not the cause of stalls.

Retain the independent evidence review and any scheduler reconciliation alongside
each performance claim. This procedure alone does not establish NVIDIA's private
algorithm, loss-free parity, broad driver coverage or an installation decision.
