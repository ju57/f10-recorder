# F10 Recorder agent guidance

## Product objective

- Prioritize [fast replay saves and low recording overhead](README.md#f10) across Windows and Linux.
- Support performance claims with end-to-end save latency and capture CPU/GPU, RAM and disk I/O measurements, including work moved between stages.

## Work on the issue

- Use a feature branch and follow the [PR workflow](README.md#contributing).
- Deliver the issue's agreed criteria through implementation, integration, verification and review fixes. Exercise the application path early and repair failures. Keep supporting changes with the complete, testable behavior they enable; justify separate prerequisite PRs.
- Resolve routine choices within existing authorization; if blocked, identify the missing decision or evidence and continue independent work. Report delivered behavior, verification and gaps; keep release qualification separate unless included in the issue.
- Carry existing task authorization forward. Implementation or a local commit alone does not authorize pushing, merging, installing or publishing.
- Preserve user media, settings, installed recorder/startup state and game/NVIDIA preferences. Use isolated test profiles and outputs; restore temporary changes and stop only task-owned processes. Live desktop/system-audio capture needs authorization within the current task.

## Architecture

- The maintained product is the .NET/Avalonia app. `Core` owns portable rules; `Windows` owns capture/audio and native integrations; `Desktop` owns UI and worker integration. Keep captured frames outside UI controls. Inspect the relevant `.csproj` before editing linked `src/` files.
- Preserve worker independence from UI lifetime, per-profile ownership and the current-user control channel. Preserve the common A/V acquisition clock and independently decodable complete replay segments, pinned/copied before export. Never use `-segment_wrap` for the rolling cache.

## Replay verification

- Support 1–30-minute replay. Use five minutes for primary optimization and repeated [reference benchmarks](docs/beta-spec.md#accepted-numerical-gates); check one- and thirty-minute accuracy, A/V alignment, continuity and resource/storage scaling. Five-minute timings give no other-duration latency guarantee.
- Before proposing new recording, reuse applicable evidence and justify the smallest missing check with finite bounds. Follow [evidence reuse and calibration review](tests/experiment-contract.md#before-new-recordings); acceptance and runtime storage/catalog requirements remain in force.
- Keep an active task or recording hold in force until the user explicitly lifts it. Guidance edits and evidence reviews do not resume held execution or authorize capture.

## Read for the task

- Issue #83 continuation: read the [paused checkpoint](docs/agents/issue-83-checkpoint.md) before planning or execution; instruction/documentation maintenance leaves that pause in force.
- Scope and document roles: [README](README.md#documentation), current issue and [beta spec](docs/beta-spec.md). Check settings/code for implemented behavior and defaults; historical reports describe their bound candidates.
- Issue operations and PR closeout: [issue tracker](docs/agents/issue-tracker.md). Triage changes: [label vocabulary](docs/agents/triage-labels.md).
- Terminology/architecture: [glossary](GLOSSARY.md) and relevant [ADRs](docs/adr/). Use established terms, flag real gaps, and identify/explain any proposed ADR contradiction. UI/identity: [F10 design skill](skills/f10-design/SKILL.md).
- Builds: use the SDK pinned in [global.json](global.json) and `./build-avalonia.ps1`. Select checks for changed paths from [CI](.github/workflows/ci.yml) and focused `tests/` scripts. Documentation changes need content/link review.
- Native/UI checks: read [test effects](tests/test-effects.md) and preview supported `-Plan` routes before execution. Clock changes: [audio-clock checks](tests/audio-clock.md). Gaming/pacing measurements: [benchmark](tests/game-benchmark.md) and [frame lineage](tests/frame-lineage.md); expensive batches also require [experiment admission](tests/experiment-contract.md).
- Replay validation/storage changes: [save-validation contract](docs/replay-save-validation.md) and [storage admission](docs/storage-admission.md), then the relevant [test route](tests/test-effects.md).
- Release work: [release requirements](README.md#public-release-milestones) and [dependency notices](THIRD_PARTY_NOTICES). Keep credentials, recordings, vendor extracts and machine-specific evidence out of commits; local evidence belongs under ignored `artifacts/`.
