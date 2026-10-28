# F10 Recorder agent guidance

## Work on the issue

- Use a feature branch and follow the [PR workflow](README.md#contributing).
- Own implementation, integration, verification and review fixes for the agreed delivery. Keep supporting changes with the complete, testable behavior they enable; justify separate prerequisite PRs.
- Exercise the relevant application path early and repair failures. Resolve routine technical choices within existing authorization; if blocked, identify the missing decision or evidence and continue independent work.
- Deliver the issue's acceptance criteria with appropriate checks. Report delivered behavior, verification and remaining gaps; keep release qualification separate unless it is part of the issue.
- Carry existing task authorization forward. Implementation or a local commit alone does not authorize pushing, merging, installing or publishing.
- Preserve user media, settings, installed recorder/startup state and game/NVIDIA preferences. Use isolated test profiles and outputs; restore temporary changes and stop only task-owned processes. Live desktop/system-audio capture needs authorization within the current task.

## Architecture

- The maintained product is the .NET/Avalonia app. `Core` owns portable rules; `Windows` owns capture/audio and native integrations; `Desktop` owns UI and worker integration. Keep captured frames outside UI controls. Inspect the relevant `.csproj` before editing linked `src/` files.
- Preserve worker independence from UI lifetime, per-profile ownership and the current-user control channel. Preserve the common A/V acquisition clock and independently decodable complete replay segments, pinned/copied before export. Never use `-segment_wrap` for the rolling cache.

## Read for the task

- Product scope/defaults: relevant [README](README.md) sections and settings code. Beta behavior: the issue and [beta spec](docs/beta-spec.md); verify current code before reusing historical findings.
- Issue operations and PR closeout: [issue tracker](docs/agents/issue-tracker.md). Triage changes: [label vocabulary](docs/agents/triage-labels.md).
- Terminology or architectural decisions: [domain docs](docs/agents/domain.md), the glossary and relevant ADRs. UI/identity work: [F10 design skill](skills/f10-design/SKILL.md).
- Builds: use the SDK pinned in [global.json](global.json) and `./build-avalonia.ps1`. Select checks for changed paths from [CI](.github/workflows/ci.yml) and focused `tests/` scripts. Documentation changes need content/link review.
- Native/UI checks: read [test effects](tests/test-effects.md) and preview supported `-Plan` routes before execution. Clock changes: [audio-clock checks](tests/audio-clock.md). Gaming/pacing measurements: [benchmark](tests/game-benchmark.md) and [frame lineage](tests/frame-lineage.md); expensive batches also require [experiment admission](tests/experiment-contract.md).
- Release work: [release requirements](README.md#public-release-milestones) and [dependency notices](THIRD_PARTY_NOTICES). Keep credentials, recordings, vendor extracts and machine-specific evidence out of commits; local evidence belongs under ignored `artifacts/`.
