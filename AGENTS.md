# F10 Recorder agent guidance

## Start here

- Read [README.md](README.md) for product scope, architecture, setup and release requirements. Check the working tree before editing and preserve unrelated changes.
- Keep this file focused on durable working agreements. Put investigation status, measurements and next steps in the relevant reports; verify current code and runtime state before reusing historical findings.
- Use a feature branch for implementation and experiments. Follow the README's pull-request workflow. A local commit does not authorize pushing, merging, installing or publishing.

## Architecture and implementation

- F10 Recorder is a local rolling replay recorder. Preserve existing recording preferences and user media. Consult the README and settings code for current defaults rather than inferring them from the product name.
- `projects/F10Recorder.Core` owns portable configuration, clocks, replay selection and notification rules; `projects/F10Recorder.Windows` owns Windows capture/audio and native integrations; `projects/F10Recorder.Desktop` owns the Avalonia UI and worker integration. Keep captured frames out of UI controls.
- Several SDK projects link files from `src/`, which also supplies the legacy WinForms build. Inspect the relevant `.csproj` before editing shared code; retain legacy compiler compatibility where those files are shared.
- Keep buffering, saving and recovery independent of UI lifetime. Preserve per-profile worker ownership and the current-user restriction on the local control channel.
- Preserve independently decodable replay segments, exclusion of incomplete segments, and pinning/copying before export so pruning cannot remove a save's inputs. Do not use `-segment_wrap` for the rolling cache.
- Preserve the common A/V acquisition clock and timestamped audio through capture and export. Treat clock, pacing, retention and recovery changes as requiring focused regression checks.
- Follow nearby code conventions and keep changes scoped. Keep FFmpeg discovery separate from recording preferences; encoder enumeration alone does not establish usable hardware support.

## Build and verification

- `./build-avalonia.ps1` builds the development app; `./build-avalonia.ps1 -Publish` creates the self-contained package. Publishing a local package does not install it. `./build.ps1` builds the legacy reference.
- Use the pinned SDK from `global.json` (`.tools/dotnet/dotnet.exe` when locally provisioned). The portable core check commands are:

  ```powershell
  dotnet restore projects/F10Recorder.CoreChecks/F10Recorder.CoreChecks.csproj --locked-mode
  dotnet run --project projects/F10Recorder.CoreChecks/F10Recorder.CoreChecks.csproj -c Release --no-restore -- artifacts/core-checks-local
  ```

- [.github/workflows/ci.yml](.github/workflows/ci.yml) defines hosted checks. Choose checks appropriate to the changed paths; documentation-only edits need link/content review, not a capture run.
- For clock changes, follow [tests/audio-clock.md](tests/audio-clock.md) and run `./tests/audio-clock.ps1`. For worker lifecycle changes, use `./tests/worker.ps1`; for UI/native integration, consult `./tests/migration.ps1` and the relevant focused scripts in `tests/`. Build the legacy reference when changing shared sources.
- Read a test script's parameters and side effects before running it. Live desktop/system-audio capture needs authorization within the current task. Use isolated profiles and output directories; do not install or alter the user's active recorder as part of an ordinary build or test.
- Gaming and pacing work must follow [tests/game-benchmark.md](tests/game-benchmark.md) and [tests/frame-lineage.md](tests/frame-lineage.md). Local ignored `docs/verification.md` and `docs/shadowplay-investigation.md`, when present, provide historical evidence rather than current acceptance guarantees.

## Live investigations and evidence

- Use one session to control the game, NVIDIA settings and recorder during a measurement. Keep heavy decoding and other performance-affecting work outside timing windows.
- Preserve installed recorder/startup state, game/NVIDIA preferences and existing recordings. Restore temporary changes and stop only task-owned processes. Scope cleanup to verified task-created artifacts; retain evidence needed to reproduce accepted and rejected trials.
- Record the exact build, binaries, configuration, source and environment for each trial. Retain rejected/inconclusive attempts and reasons; do not relax acceptance gates to obtain a pass.
- Distinguish static reverse-engineering evidence, diagnostic instrumentation and ordinary production behavior. Name unresolved internals and qualify observer overhead before attributing a performance cause.
- Full decoding, timestamp ordering, source-picture cadence, A/V marker alignment/drift and gaming overhead establish different properties. Container boundary skew alone does not establish perceptual sync; synthetic checks and hosted CI do not establish live hardware or game acceptance.
- Report what changed, which checks ran, their results and material limits. Keep recordings, machine-specific reports, credentials, vendor binaries and raw third-party extracts out of commits. Store local trial evidence under ignored `artifacts/`; consult `THIRD_PARTY_NOTICES` and the README before release work.
