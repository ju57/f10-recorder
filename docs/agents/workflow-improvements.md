# Workflow improvements for beta delivery

Agreed direction from the retrospective discussion on 21 October 2026. Improve delivery of the Windows/NVIDIA beta by preventing observed workflow failures. Expand diagnostic infrastructure when a specific investigation needs it. Deliver the three changes below in order, with separate review boundaries. Final confirmation of shared understanding precedes implementation.

## Agreed safeguards

- Declare the effects of the worker/window test routes, including real UI and capture effects. Add an automated check that a route declared headless cannot dispatch the UI test. Keep intentional UI tests available with their effects clearly described; add no routine confirmation prompt before every test. Generated media alone does not establish absence of desktop effects.
- Qualify the setup before an expensive measurement batch, then freeze the measurement window and sample plan. Validate each trial for readiness before its window, process replacement or recovery during it, and complete end coverage. Stop further collection on a validity failure and preserve the rejected attempt. A valid but statistically inconclusive result does not justify automatic extra runs or relaxed thresholds. These gates apply to expensive measurements, not ordinary builds or core checks.
- Strengthen existing status checks before introducing deeper event instrumentation. Each experiment plan specifies its sampling interval, maximum allowed sample gap and measurement boundaries. Require coverage at both window boundaries and reject excessive gaps. Reports describe this as sampled health; stronger claims require separately justified instrumentation.
- Declare expected storage, a maximum storage budget and required retained evidence before an expensive experiment. Stop collection before exceeding the budget. At closure, record what should remain available and what could be retired. Automatic deletion is outside this first improvement; recordings, builds and worktrees require different treatment.

## Modern application scope

The [modern-only application decision](../adr/0004-maintain-only-the-modern-application.md) supersedes the proposal to add legacy compilation to CI. Remove the obsolete application and tooling exclusive to it after checking dependencies. The removal inventory must distinguish the old application from shared sources and current verification fixtures.

Keep modern verification tools and their temporary test-window helpers. Move still-useful security checks out of the old application's test machinery before removing it. CI builds the modern product and its relevant verification tools.

## Delivery sequence

### 1. Retire the legacy application

Remove the Framework compiler build in `build.ps1`, the legacy selection in `install.ps1`, and old application/UI code excluded from every modern project. Retire or replace launch scripts that use the old executable. Move applicable dependency-security checks into modern verification before removing their legacy host. Keep the 21 `src/` files currently linked into Core/Windows, current settings compatibility and modern verification helpers. A file's name or age is insufficient evidence that it is unused.

Update active README, agent, test and installer guidance to describe the modern-only product. Reconcile the release-plan draft with the current security implementation and outstanding verification, including the remaining different-account check. Preserve dated reports as historical evidence. Keep this retirement scoped; relocating active sources or redesigning working modules needs a concrete reason.

Completion requires modern application and relevant verification builds, portable core checks, and the migrated dependency-security checks with task-isolated storage. Review active launch paths and documentation for dependencies on removed machinery. Current user settings, installed binaries and saved media remain outside repository retirement. The first validation plan uses compilation and suitable isolated checks; live UI/capture tests retain their existing authorization and effect requirements.

### 2. Protect test launches

Start with `tests/window-capture.ps1` and `tests/worker.ps1`. Describe their route-specific effects and use a reviewable launch mapping. Verify that the headless window-check route selects only its headless entry point, while the synthetic worker/UI route remains explicitly classified as opening real windows. Descriptions cover applicable UI/focus, desktop capture, real audio, GPU encoding, startup writes and known NVIDIA Replay interaction.

Completion requires deterministic positive and negative route checks, including rejection of a headless declaration paired with a UI launch. Those dispatch checks must run without opening recorder UI or capturing the desktop. They verify known launch behavior; they do not automatically discover every indirect operating-system or driver effect.

### 3. Protect expensive experiments

Extract reusable validity rules from the existing readiness preflight and game-benchmark checks. Start with the game-benchmark route and explicit workload-specific adapters where status formats differ. Use offline trace fixtures to check readiness, process continuity, recovery, start/end coverage and sample gaps. Keep source/workload qualification distinct from recorder status. Do not require every producer to expose the same worker API.

The qualification run establishes that the prospective setup works. Freeze the batch's window and sample plan afterward; do not shift individual windows automatically to rescue late startup. Validate each trial before starting another and stop remaining collection on invalidity. Preserve failure reasons and valid inconclusive results.

Before collection, require the experiment's timing limits, expected storage, maximum budget and retained-evidence needs. Include qualification output, recordings and expected derivatives in the estimate. Check storage before starting a trial and during collection where practical; stop safely if the budget cannot be met and preserve completed evidence. A stopped trial is incomplete and contributes no valid measurement. Numeric limits are workload-specific plan inputs, not project-wide guesses; account for bounded shutdown/finalization output when enforcing storage limits.

At closure, record source/build/configuration identity, evidence dependencies and availability, including explicitly retired inputs. Distinguish recordings, build outputs and worktrees. Propose retirement candidates without automatic deletion. Existing reports whose media was retired remain historical evidence and cannot support fresh media verification.

Completion requires offline positive and negative fixtures, a plan validation mode that starts no capture, and runner integration that prevents the next trial after a validity failure. Tests cover missing plan fields, late readiness, changed process identity, recovery, missing boundaries, excessive sample gaps and storage limits. A valid inconclusive result must remain inconclusive without triggering extra runs or changed thresholds. No new live measurement campaign is needed to develop these mechanical checks.

## Scope and status

Implementation is pending. Supporting deterministic checks belong in the existing modern verification/CI routes where their environment permits. Broader FFmpeg transport checks, further native observer contracts, performance/parity research and product feature work are follow-ups when specifically justified. None is an additional gate on ordinary unrelated source changes.

Per-experiment sampling values, storage limits and retained sets are resolved when planning that concrete experiment. This plan establishes the required inputs and behavior, and does not authorize a live batch, installation, publication, push, merge or evidence deletion.
