# Application architecture and prototype behavior

This page describes the maintained application and the working-tree replay candidate.
It separates implemented mechanisms from their remaining qualification limits.
Accepted beta behavior is specified in [the beta specification](beta-spec.md);
current task scope and completion belong to the linked GitHub issue. See the
[documentation responsibilities](../README.md#documentation) before reconciling differences.

## Application stack

F10 uses **C# on .NET 10 LTS with Avalonia UI**, retaining native capture/audio integrations and FFmpeg/NVENC. [Avalonia's rendering architecture](https://docs.avaloniaui.net/docs/fundamentals/architecture) supports a custom interface without embedding a browser. [.NET 10's support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core) makes it a suitable maintained runtime target. [global.json](../global.json) pins the SDK; the project files and NuGet lockfiles pin application dependencies. Dependency auditing remains enabled.

| UI choice | Benefit for F10 Recorder | Cost or limitation |
| --- | --- | --- |
| C# + Avalonia | Keeps one application language, allows custom styling, and supports Windows/Linux UI | Requires platform-specific capture and overlay work |
| Electron + native recorder | Strong HTML/CSS ecosystem and flexible visual design | Bundles Chromium and Node, adds a JavaScript/native control boundary, and needs ongoing browser-runtime maintenance |
| Tauri + native recorder | HTML/CSS UI using the operating system's webview | Introduces Rust plus a web UI alongside the existing C# recorder; webview behavior varies by platform |

See the official [Electron process model](https://www.electronjs.org/docs/latest/tutorial/process-model) and [Tauri architecture](https://v2.tauri.app/concept/architecture/). Resource use must be measured on the finished app; this comparison is about architecture, not benchmark results. The [approved F10 visual guide](brand-guidelines.md) uses NVIDIA App-inspired neutral dark surfaces and green emphasis with Discord-inspired rounded controls and solid icons. It is a review-board direction; production UI migration, keyboard access and readable recording state remain separate work.

The application separates three SDK projects: `F10Recorder.Core` contains configuration, replay selection and notification rules; `F10Recorder.Windows` contains native capture/audio, encoder probing, screenshots, application identity, global shortcuts and Windows session/display/power handling; `F10Recorder.Desktop` contains the Avalonia interface. The migrated app has no WinForms or System.Drawing dependency. Native Win32 message dispatch replaces the old helper window, and icons are embedded assets. Frames stay in the capture/encoder pipeline rather than passing through UI controls.

See [ADR 0004](adr/0004-maintain-only-the-modern-application.md) for the maintained-product boundary and treatment of legacy code and verification fixtures.

## Worker and profile ownership

The Windows recorder now runs in an independent per-user worker process. It owns buffering, replay saves, shortcuts and display/session recovery; the Avalonia process connects for settings, status and notifications. If the UI crashes, buffering continues and reopening the app reconnects to the existing buffer. **Quit** explicitly stops both processes; an active settings change, replay save or screenshot must finish before Quit is accepted. Closing Settings only hides the window. The worker uses the same installed executable in an internal worker mode, so installation requires no additional service or elevated privileges. Moving the rest of retention/save/recovery into the shared engine and implementing Linux adapters remain outstanding.

The local control channel is a versioned named pipe restricted to the current Windows user, with separate identities for each session and configured data directory. Messages and concurrent connections are bounded; there is no network listener. Pause state persists across worker restarts. The worker tracks the attached UI process so application-name folders retain the external application's identity, and screenshot capture waits for the UI to acknowledge hiding its overlay.

Application-window screenshots verify window lifetime before capture and saving, including when Windows reuses a handle. If Windows denies the required lifetime marker, the screenshot fails with a reason; this can affect elevated applications. Ordinary recording keeps its existing policy, and a rejected window screenshot does not fall back to the desktop.

The recorder uses protocol version 7, including configurable replay-cache locations, authenticated engine capability responses and profile compatibility/activation results. Quit older recorder processes before opening this build; mixed versions are rejected so an older editor cannot reset a cache location it does not understand, or a new UI delegate to a worker without the current settings contract.

## Storage controls and admission

Storage provides separate inset folder pickers for recordings, screenshots and replay cache, plus an editable decimal-GB cache budget and Reset edits. New profiles keep Videos/F10 Recorder, Pictures/F10 Recorder and a 10 GB budget; valid existing choices are retained. Apply preserves unrelated preferences and pause, and rejects invalid budgets without discarding the draft. Cache-folder changes use a separate per-profile child in the selected folder, leave existing media/cache in place and keep retained old cache charged to the budget. See [Storage controls and integration checks](../tests/storage-controls.md). [Replay output admission](../tests/replay-output-demand.md) now connects a protected reference-model catalog, explicit unmeasured status and shared finite replay commitments to ordinary workers. Ordinary capture now has a durable storage stop and explicit Resume checks, including shared qualified growth claims. No qualified empirical model is shipped: unmeasured acquisition and production output admission reject before unsafe work. The reference unfinished-tail envelope and export supervision are documented in [storage admission and producer commitments](storage-admission.md); other profiles, queued/manual/recovery writers and the native failure matrix remain unqualified.

A session-independent file lock in account-protected storage permits only one worker to open a given user's recording profile, so a second Windows session cannot clear an active replay cache. Profile identity includes its filesystem identity, so path aliases share ownership. Concurrent sessions must use separate `--data-dir` profiles. Focused checks exercise rejection before cache cleanup and reuse after ownership is released; genuine cross-account execution remains a separate acceptance check.

## Cross-platform direction

The initial hardware scope is **NVIDIA on Windows and Linux**. GPU probing must determine which NVENC codecs actually work; installing FFmpeg with an encoder listed does not prove hardware support. AMD and Intel support is outside the initial release scope.

| Platform | Current state | Backend/integration scope |
| --- | --- | --- |
| Windows + NVIDIA | Working prototype with limited acceptance coverage | Desktop Duplication, WASAPI loopback, native shortcuts, startup and composited overlay |
| Linux X11 + NVIDIA | Not implemented | Validated native capture/audio backend, shortcuts, startup and window identity |
| Linux Wayland + NVIDIA | Not implemented | ScreenCast portal/PipeWire capture, desktop-supported shortcuts, session restoration and overlay behavior |

[Avalonia currently defaults to X11/XWayland on Linux](https://docs.avaloniaui.net/docs/platform-specific-guides/linux); that UI support does not supply a recording backend. Wayland capture needs the [ScreenCast portal](https://flatpak.github.io/xdg-desktop-portal/docs/doc-org.freedesktop.portal.ScreenCast.html), and shortcuts need supported desktop integration such as the [GlobalShortcuts portal](https://flatpak.github.io/xdg-desktop-portal/docs/doc-org.freedesktop.portal.GlobalShortcuts.html). Permission persistence, application identification, global hotkeys, and overlay placement depend on the compositor. Automatic capture without a prompt and NVIDIA-style overlays in every fullscreen game cannot yet be promised. Application-name folders need a documented fallback wherever the desktop does not expose the foreground application.

## Capture and replay pipeline

Windows Desktop Duplication captures the monitor. FFmpeg encodes with NVIDIA NVENC into independently decodable one-second segments. H.264 and HEVC use MPEG-TS; AV1 uses Matroska. Precise MP4 export preserves the physical picture boundaries and the shared audio/video acquisition clock. WASAPI captures audio from the default Windows playback device, including continuous silence when nothing plays. A supervisor checks progress and completed segments rather than merely checking whether FFmpeg exists.

### Acquisition clock and cadence

System-audio capture uses one acquisition clock for WASAPI and Desktop Duplication. A streaming Matroska PCM input preserves audio acquisition timestamps with a 100 ms delivery cushion; video uses a one-time UTC bridge into the same session epoch and retains its timestamp intervals. The separate bounded input queues still share FFmpeg's scheduler, so delayed audio can throttle video acquisition. The path without system audio retains a direct filter-graph source and uses the same QPC save epoch and one-time video bridge. Output passthrough avoids a second CFR conversion. See [the acquisition-clock checks](../tests/audio-clock.md) for startup, sample and timestamp verification.

The acquisition ceiling is four times output FPS (240 Hz at 60 FPS). [FFmpeg documents ddagrab as a maximum polling rate with no background buffering](https://ffmpeg.org/ffmpeg-filters.html#ddagrab), so that ceiling does not promise fresh pictures. GPU frames pass through an FPS filter with nearest-frame rounding. `ConfiguredCaptureRateLimit` and `ConfiguredOutputFramesPerSecond` describe the configured policy; decoded picture identities establish actual cadence. UI integration and controlled fixtures do not establish real-game performance, audible audio quality, perceptual A/V alignment or installation readiness.

### Replay export and publication

Saving waits for the current segment to finish and protects the selected completed inputs with local hard links (copy fallback) while recording continues. Eligible H.264 60 FPS MPEG-TS footage uses protected packet indexing: a partial head is re-encoded when needed, complete interior pictures and independent tail prefixes are copied, and AAC packets retain genuine source preroll with sample-clock priming and tail edits. Valid unsupported layouts, codecs and frame rates use the full-interval decode/re-encode exporter and its explicit concat inpoints/durations. Recognized input corruption fails the save. Both paths retain exact physical cuts rather than losing a head GOP. Full replay remains the default and repeated saves overlap. Warm-up requires at least one second and reports saved/requested duration and unavailable earlier history.

An accepted request is separate from Saved. The candidate hybrid path checks the complete closed MP4's container, enabled samples, configuration, clocks, boundaries and coded bytes against an immutable proof established from protected inputs before muxing. It keeps the checked file immutable through storage release and publishes that file identity. A missing or mismatched hybrid proof fails. This normal check does not decode every picture or audio frame: corruption already present when input evidence is established can escape it. Full decoding remains mandatory for qualification and diagnostics, and the legacy exporter retains its full software decoder before publication. This deliberately changes the earlier full-decode-before-every-Saved promise.

The candidate selects bounded cancellable batch indexing and direct packet writing, with exact re-encoding as its compatibility fallback. It is under qualification and has not met the complete three-to-five-second reference target. Offline construction pilots do not establish ordinary worker/Saved or cold/warm percentile acceptance. Validation cost is reported separately from total save cost, with coded and decoded counts distinguished. Changed binaries and scratch allocation require fresh exact-candidate storage calibration covering both export variants, and ordinary production admission rejects stale or absent calibration. See [exact replay checks](../tests/replay-precision.md) and [latency investigation and qualification boundaries](../tests/replay-latency.md). Wait for the final save result before quitting.

### Disk cache and diagnostics

The rolling buffer uses disk, not RAM. At 20 Mbps, five minutes is about 760 MB plus approximately ten seconds of retention headroom. A bounded segment manifest, time pruning, a byte limit, per-session cache directories, and Windows process jobs control temporary storage and encoder lifetime. Clips are never automatically deleted. No recording or telemetry is uploaded.

Configuration and rotating diagnostic logs live in `%LOCALAPPDATA%\F10Recorder`. Settings changes rebuild the buffer. Only one interactive instance runs for your Windows user. If another app owns F10, choose a different hotkey; the tool reports the conflict. Windows startup is at **user sign-in**, when the desktop becomes available.
