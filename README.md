# F10

F10 aims to be a lightweight, high-performance, open-source ShadowPlay alternative for Windows and Linux. Fast replay saves and low CPU/GPU, RAM and disk overhead are primary design goals.

**Current status: Windows/NVIDIA prototype using C#/.NET and Avalonia. Linux capture is not implemented; public binary release preparation is incomplete.** The modern .NET/Avalonia application is the only maintained product. F10's own code is [MIT licensed](LICENSE); dependency licenses are described in [THIRD_PARTY_NOTICES](THIRD_PARTY_NOTICES).

The prototype saves recent local footage with a configurable global hotkey. It supports monitor capture and experimental selected-window capture, system audio, screenshots, and an independent recorder worker. Closing Settings hides the editor; **Quit** stops the application and worker when active work permits. No recording or telemetry is uploaded.

Replay length is configurable from **1 to 30 minutes**. Represented new-profile defaults are five minutes, primary monitor, system audio, native display resolution at 60 FPS, and H.264 at 20 Mbps. Existing valid preferences and shortcuts survive migration. Unversioned profiles wait for explicit Resume; migration itself never starts capture. The approved new-user setup and Overview shell remain separate implementation work.

Ordinary capture and replay output admission require applicable private storage calibration. **No qualified empirical model is shipped.** Missing or stale calibration rejects acquisition/save admission; building or installing the prototype does not establish recording readiness. See [storage admission](docs/storage-admission.md) and [the runtime architecture](docs/architecture.md).

## Run and install

Development uses the SDK pinned in [global.json](global.json). The initial package restore needs internet access.

```powershell
.\setup-development.ps1 # optional: installs the pinned SDK inside .tools only
.\build-avalonia.ps1 -Publish
```

For the Issue #83 Windows qualification build, pass `-QueueEnginePath <path-to-ffmpeg.exe>` to the same build command. It verifies the queue-enabled binary against the pinned SHA-256 and includes it in the build output. Explicit setup or repair uses that binary with the inspection tool and notices from the approved base archive. Omitting the option selects the stock engine.

The [production queue patch](patches/ffmpeg-9.0.2-queue.patch) applies to FFmpeg 9.0.2. Its [Windows layout check](tests/ffmpeg-queue-layout.c) reads the desktop filter’s options before queue initialization, while the queue lock is held, and after teardown without opening a capture device.

Without `-Publish`, the build goes to `bin/avalonia`; publishing produces the self-contained Windows application. End users do not need a separate .NET runtime. The prototype installer requires 64-bit Windows 10/11 and an NVIDIA GPU with NVENC; codec availability depends on the actual GPU, driver and engine build.

```powershell
.\install.ps1 -NoLaunch
```

Installation copies the application, runtime and license notices to `%LOCALAPPDATA%\F10Recorder\application`, prepares the approved managed FFmpeg dependency, and enables Windows sign-in startup. `-NoLaunch` suppresses the immediate application launch; startup is still enabled. Omitting it launches the app, subject to profile activation, pause and storage-admission checks. Quit existing recorder processes before reinstalling. Use an isolated profile and the relevant effects plan for verification.

Setup downloads the pinned FFmpeg 9.0.2 package when no verified managed copy exists; a verified copy can be reused offline. F10 authenticates its private engine before probes and launches and ignores external PATH/profile-controlled engines. Missing or rejected bytes require explicit **Repair recording engine** or setup; worker restarts do not download dependencies. The exact package's notices and corresponding-source arrangement remain release requirements.

## Basic usage

Double-click the tray icon, choose **Settings…**, or launch the executable again to open the running editor. Configure Capture, Audio, Controls and Storage there. Replay pause/resume is immediate; other edits use **Apply changes**. Valid existing preferences take precedence over defaults.

Shortcut defaults currently depend on how the profile was created:

| Action | Fresh `Settings` defaults | Profile created by `install.ps1` |
| --- | --- | --- |
| Save replay | F10 | Ctrl+Shift+F10 |
| Open/close overlay | Alt+Z | Ctrl+Shift+F9 |
| Take screenshot | Alt+F1 | Ctrl+Shift+F8 |

These differences are present in [Settings.cs](src/Settings.cs) and [install.ps1](install.ps1); this documentation cleanup does not change them. Check **Controls** for your actual bindings. A registered shortcut conflict is reported; choose an available shortcut.

Replays are MP4 files in an application-name subfolder below the recordings root, normally Videos/F10 Recorder. Naming uses the foreground application at save time; whole-monitor history can span several applications. Screenshots are full-resolution PNGs in their separate destination, normally Pictures/F10 Recorder, and remain available while replay is paused. Existing clips are never automatically deleted.

The overlay offers Save replay, Screenshot, Pause/Resume and Recording settings. Escape, its close button or the backdrop dismisses it. Tray actions and `F10Recorder.exe --overlay` / `--screenshot` invoke the corresponding controls. Wait for the final save result before quitting: request acceptance is distinct from a checked, published MP4. Warm-up saves disclose available/requested history and require at least one second.

True exclusive fullscreen, HDR fidelity, source/device transitions and NVIDIA coexistence are not broadly qualified. Showing capture-excluded F10 surfaces interrupted NVIDIA Instant Replay in a controlled comparison, including paused UI. See [window capture](tests/window-capture.md) and [the architecture](docs/architecture.md) for the precise limits. The [accepted visual direction](docs/brand-guidelines.md) describes the intended UI rebuild.

## Verify

Select checks by changed behavior and their [documented launch effects](tests/test-effects.md). Preview supported `-Plan` routes before execution. Live desktop/system-audio recording requires authorization within the current task; an active recording hold remains in force until explicitly lifted.

Portable checks need no GPU or capture:

```powershell
dotnet run --project projects/F10Recorder.CoreChecks -- artifacts/core-checks
```

Use the [effects index](tests/test-effects.md) to find worker, migration, security, clock, replay and native checks. Expensive game measurements also require [experiment admission](tests/experiment-contract.md). Hosted CI and synthetic checks do not establish live capture, gaming overhead or release qualification. Local evidence belongs under ignored `artifacts/`.

## Contributing

Use a feature branch and open a pull request against `main`. The protected branch requires successful **Core checks (Ubuntu)** and **Windows build** checks, resolved review conversations and linear history. Direct pushes, force pushes and branch deletion are blocked, including for administrators. The maintainer controls merges; public access does not grant write or administrative permissions. Required approval count is zero so the sole maintainer can merge their own reviewed changes through a passing pull request without needing a second maintainer.

CI runs portable core checks on Linux and builds the Windows Avalonia app. Hosted CI does not validate GPU capture or fullscreen games; use the local Windows/NVIDIA migration suite for those implemented acceptance checks. GitHub Actions have read-only repository permissions, and action versions are pinned to commit hashes. Do not add recordings, machine paths, credentials, vendor binaries or raw reverse-engineering extracts to a pull request.

## Public release milestones

The near-term target is a **Windows + NVIDIA public beta**, with Linux following separately. The [beta specification](docs/beta-spec.md) owns accepted behavior and mandatory release gates; the [release plan](docs/release-plan.md) retains milestone ordering and the dated planning decisions. Current delivery scope and completion are recorded in GitHub issues. Source integration, planning closeout and passing CI do not establish binary-release acceptance.

Remaining beta work includes faster qualified replay saves, new-footage save policy and queues, manual recording, audio/device integration, the accepted interface, supported hardware/game qualification, trusted offline packaging, signing and external-beta acceptance. The [ticket graph](docs/beta-implementation-tickets.md) is the publication-time map; follow its live issue links for current dependencies and state.

## Cross-platform direction

Initial hardware scope is NVIDIA on Windows and Linux; AMD and Intel are outside the initial release scope. Linux X11 and Wayland need separately qualified capture/audio, shortcuts, ownership and packaging. Avalonia UI support supplies no recording backend. See [platform and architecture details](docs/architecture.md#cross-platform-direction).

## Documentation

| Source | Responsibility |
| --- | --- |
| Current GitHub issue | Agreed delivery scope, acceptance and live completion/dependency state |
| [Beta specification](docs/beta-spec.md) | Accepted product requirements, numerical gates and release criteria |
| [ADRs](docs/adr/) and [glossary](GLOSSARY.md) | Architectural decisions and shared terminology |
| [Brand guide](docs/brand-guidelines.md) | Approved visual direction; [design skill](skills/f10-design/SKILL.md) routes UI work |
| [Architecture](docs/architecture.md) | Runtime mechanisms and candidate-specific limitations |
| [Test-effects index](tests/test-effects.md) and linked test pages | Verification procedures, effects, bounds and evidence limits |
| [Agent guidance](AGENTS.md) and [issue workflow](docs/agents/issue-tracker.md) | Repository work and issue/PR closeout conventions |
| Dated research, review and verification reports | Findings bound to their stated candidate, workload and retained evidence |

An approved amendment identifies the requirement it changes, records its approval/source, and is linked from the affected documentation. A newer timestamp alone does not override an accepted requirement. Keep each rule in its owning document and use links elsewhere. Current task scope does not silently reduce product requirements or release gates. Historical reports preserve their original results; verify applicability before reusing them.

## Uninstall

Quit from the tray menu, then run `./uninstall.ps1`. This removes the app, its managed FFmpeg package and private ownership state; external FFmpeg installations are preserved. Add `-RemoveSettings` to remove configuration, logs, and temporary cache as well. Saved MP4 clips are preserved.
