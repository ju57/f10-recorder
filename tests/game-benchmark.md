# Native CS2 recorder benchmark

Run the rebuilt independent worker on a separate data directory. Do not install
the test build or apply startup preferences. Preserve the installed application,
game video settings, key bindings and FPS cap, and restore temporary game changes
when the test ends. Record the branch and commit, FFmpeg build, GPU/driver,
display mode, game build and demo hashes with the local evidence.

The PowerShell harness requires the desktop build in `bin/avalonia` and the
portable PresentMon 2.6.0 executable at
`.tools/presentmon/PresentMon-2.6.0-x64.exe`. The Python analyzer requires NumPy
and FFmpeg/ffprobe. All recordings and machine-specific evidence stay in ignored
`artifacts/`; they must not be committed.

## Procedure

1. Start a fresh isolated worker with `-Action Start -DataDirectory <path>`.
   This creates a one-minute, 120 FPS, H.264/NVENC, system-audio profile with
   distinct test shortcuts. The benchmark measures this profile, not the
   installed application's defaults.
2. Reload a local professional CS2 replay for each short trial, pause it, seek to a recorded demo tick,
   and confirm the game FPS cap. Keep the console and replay controls hidden
   during measurements. Repeat the same tick and camera setup for each trial.
   Backward seeking alone can retain previous HUD notifications; preserve and
   qualify affected trials rather than treating them as visually identical.
3. Launch `-Action Off` or `-Action On` with a new `-Label`, `-Seconds 70`, and
   `-WaitForResume`. The harness reaches the requested worker state and writes
   `<label>-armed.json` before collecting a trace.
4. Resume the replay, bracket the input submission with UTC timestamps, then
   write `<label>-resume.json` containing `beforeUtc`, `afterUtc` and `demoTick`.
   The harness begins PresentMon after this marker. Replay/ETW alignment is an
   approximation, not a source-picture identity mapping.
5. Compare the complete replay-relative 10..55 second windows. An incomplete
   window is inconclusive. Use repeated, interleaved Off/On trials to estimate
   run variability; a single pair cannot isolate a small overhead difference.
   Pause the worker during setup. Do not decode clips during timed trials.
6. For a soak, use `-Seconds 1800`. The harness saves at 45 seconds, then every
   minute, plus a final save. The first clip can include paused setup imagery;
   exclude that imagery from motion conclusions. Report retained duration and
   any overlap/gaps rather than claiming full source-frame coverage.
7. After the timed run, pause the worker and analyze every saved clip. Inspect
   candidate holds in moving scenes before drawing conclusions. Quit the
   isolated worker, restore the game preferences, and verify cleanup and the
   installed recorder/startup state.

## Interpretation

PresentMon measures game presentation timing, not captured source-picture IDs.
Report mean presentation FPS, interval quantiles, long gaps, presentation mode,
and the recording-off/on differences. Distinguish the game's render resolution
from the desktop capture resolution. Game-only GPU timing does not isolate the
recorder's GPU cost. CPU percentages from worker metrics are normalized across
logical processors and omit the replay-remux child process.

The analyzer fully decodes the selected video and audio streams, requires one
120 FPS video stream and one audio stream, checks strictly increasing 8.333 ms
video timestamps, and limits A/V boundary skew to 50 ms. Worker checks require
the correct foreground game, the requested capture state, no recovery, and a
bounded rolling cache. These checks cover media integrity and runtime health.

World-region image differences are reported at several thresholds. Static
views, scene cuts, game presentation stalls, lossy encoding and sampler clock
phase affect them. They are candidates for investigation, not a unique-frame
acceptance gate. FFmpeg's output drop/duplicate counters do not count duplicates
inside the `fps` filter. A constant-rate MP4 does not prove loss-free capture.

Professional replay playback covers actual game rendering and exclusive
fullscreen capture on the tested PC. It does not validate live input latency,
online multiplayer performance, broad hardware/driver coverage, or NVIDIA's
private capture algorithms. Pair this benchmark with the checksummed source-ID
fixture for controlled capture-pacing measurements.
