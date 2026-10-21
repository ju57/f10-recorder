# Windows + NVIDIA public beta release plan

Date: 20 October 2026; status reconciled 21 October 2026 (Europe/Lisbon). Original planning baseline: `e8713db24a7476b0e7db71548ae69b75c57886af`; security implementation baseline: `692c4dfd6d50ff0062c51947c1c5351ee01ca7de`.

**Status: planning only.** This document consolidates the agreed release direction and proposed acceptance criteria. It authorizes no implementation, live capture, installation, publication or release. Release milestones remain open. PR #4 integrated the five security remediations and screenshot-binding fix; remaining verification and final-candidate review still gate distribution. The separately agreed [workflow improvements](agents/workflow-improvements.md) are implementation work, distinct from authorization to release binaries.

## 1. Release goal and scope

Release a Windows + NVIDIA beta that lets people save recent gameplay or record a whole session, with clear controls, predictable audio and safe storage. Linux follows in a separate release. Retain C#/.NET, Avalonia and the independent recording worker; rebuilding the interface does not require replacing the application stack.

Agreed scope:

- Rebuild the desktop interface, interactive overlay, system tray context menu, recording indicator and notifications as one consistent experience.
- Add manual Start/Stop recording, named display selection, output resolution choices and essential audio controls.
- Make instant replay substantially faster to save without interrupting capture or damaging footage.
- Offer both full rolling-window saves and saves containing only new footage since the preceding successful replay save.
- Provide an installer, security remediation, dependency compliance, release qualification and an external feedback process.

The default proposed for replay saving is **Full replay each time**, preserving the overlapping-save behavior requested in planning. The alternative is **Only new footage after the previous replay save**. Both are beta requirements. Existing recording preferences and saved media must survive upgrades; new-user defaults and migrations must be specified before implementation.

Later scope: Linux X11/Wayland capture and packaging, per-application audio isolation, automatic highlights, game profiles, trimming and broader HDR support. Streaming, webcam composition, filters, driver management and a large performance dashboard are outside this beta. A basic recent-captures view is in scope; a full editor is not.

Use ShadowPlay as a workflow reference, implementing features independently. Do not ship NVIDIA code, icons, fonts or extracted resources. Full NVIDIA feature or performance parity is not a beta promise.

## 2. Evidence and current limitations

Read [prototype status](prototype-status.md), [UI behavior and coexistence observations](ui-specification.txt), [window-capture qualification](../tests/window-capture.md), [README](../README.md) and [dependency notices](../THIRD_PARTY_NOTICES) alongside this plan. These describe current behavior and historical limits; the criteria below describe intended release behavior.

| Input | What it establishes | Planning consequence |
| --- | --- | --- |
| Security assessment completed on 20 October | Combined repository/diff assessment reported three medium and two low findings; no critical/high findings. It recommended withholding public binaries. | Remediations integrated in PR #4; retain genuine different-account verification and final release-candidate review as outstanding. |
| Security revision scope | Repository/diff work started at `b4809597a3542b69ac29c6755baa2e59fa91a118`; a later delta covered PR head `73ab8d3fe8262039d566e641c57081471fbc15ca`. Merge `7170472` matched that reviewed tree. Planning baseline `e8713db` is later. | That assessment does not cover every later change. PR #4 integrates the remediation; record its validation and any later candidate delta separately. |
| Project retrospective, reconciled 21 October | Thirteen substantive chats and 385 turns inventoried, with 47 selected primary excerpts and explicit sampling/media limitations. Integration, security fixes, coordination and cleanup are resolved or partially resolved. | Apply the agreed modern-only retirement, test-effect checks, readiness qualification and storage budgets; preserve separate source, binary and performance gates. |
| User-reported five-minute replay save | Approximately 42 seconds; NVIDIA appeared almost immediate in the user's experience. This report has not been reproduced by this planning task. | Preserve as an unresolved slow-save case and identify its exact build/profile when benchmarking. |
| Separate prototype save trials | Approximately 16–17 seconds; clips were 299.650 and 299.400 seconds. | These are different trials, not a correction or explanation of the 42-second case. |
| Current save path | Already-compressed segments are protected, referenced through hard links where possible or copied, then joined into MP4 with stream copying and `faststart`. | Measure each stage before attributing latency to compression, copying, joining or file finalization. |
| Current features | Resolution is automatic; system audio follows the Windows default output; microphone capture and manual recording are absent. A second save is rejected while one is active. | These require engine/protocol work as well as interface controls. |
| Current gameplay evidence | Bounded decoding/timestamp checks and partial human review, with remaining smoothness and synchronization questions. | Qualify the shipping configuration; do not claim source-picture or perceptual parity from timestamps alone. |

Detailed security reports and the retrospective remain local evidence, outside the public source tree. For maintainers with that evidence, the retrospective is under `artifacts/retro-all-sessions-20261021-101518/REPORT.md`; security reports are in the Codex Security scan store under the revision IDs above. This plan records actionable summaries without requiring those private/local files to exist in a clean clone. Preserve report identities and revision coverage in the release evidence index, excluding machine paths and raw third-party material from public commits.

## 3. Priorities

P0 protects security, privacy and footage integrity. P1 completes the promised beta experience. **Both P0 and P1 are required for public beta**; P2 work follows unless separately brought into scope. Priority does not imply every task must run serially.

| ID | Priority | Deliverable | Completion evidence |
| --- | --- | --- | --- |
| SEC | P0 | Verify integrated security remediations and review the final candidate | Finding-specific checks, Windows account/ACL checks and recorded revision coverage |
| CAP | P0 | Safe capture intent, clean footage and recording-software coexistence | Qualified display/application policy, transition tests and actual capture results |
| REP | P0 | Both replay-save policies with protected inputs and failure-safe coverage tracking | Selection/queue fixtures plus real exported-file checks |
| PKG | P0 | Trusted dependencies, license/source arrangements and safe install/upgrade/uninstall | Exact package inventory, signatures and clean-environment lifecycle results |
| SAVE | P1 | Faster replay saving with measurable completion feedback | Stage timings, repeated matched exports and final-file verification |
| REC | P1 | Manual recording alongside replay | Start/Stop, long-session, concurrent-save, failure and recovery results |
| AV | P1 | Capture/video selection and essential audio | Device/source matrix, track routing and synchronization results |
| UX | P1 | Desktop, overlay, tray, indicator, notices and onboarding | Reviewed flows, accessibility/focus checks and user task completion |
| QA | P1 | Release matrix, reusable checks and diagnostics | Candidate qualification record with no unresolved required cells |
| LAUNCH | P1 | Small external test followed by public beta | Tester results, support materials and maintainer release decision |
| NEXT | P2 | Linux and deferred recording/editor features | Separate scoped plans after beta feedback |

## 4. Milestones and dependencies

All milestone states are **open**. The maintainer owns scope and release decisions; each future work item must name an implementation owner and a reviewer/validator. No calendar release date is committed. Estimate delivery after M0 resolves the capture, audio and saving unknowns.

| Milestone | Work and dependencies | Exit criteria |
| --- | --- | --- |
| M0 — Freeze beta behavior | Confirm this scope; review UI flows/states; define replay boundaries, queue limits, capture policy, supported matrix, installer route and numeric quality/performance budgets. | A versioned beta specification resolves the decision register below. Proposed targets are accepted or explicitly revised before trials. Work is split into scoped branches/PRs with owners. |
| M1 — Secure the foundation | SEC and CAP; identify exact shipping FFmpeg capabilities and packaging obligations. Depends on M0 decisions. | Integrated remediations and screenshot-binding fix retain focused regression evidence; genuine different-account verification and final-candidate delta review are complete; capture policy is qualified for supported configurations. |
| M2 — Complete recording capabilities | REP, SAVE, REC and AV. Design exploration may start earlier; engine changes depend on relevant M0/M1 contracts. | Both replay modes, rapid saves, manual recording, video controls and essential audio satisfy their criteria and regression checks. Save-speed results meet frozen targets. |
| M3 — Rebuild the experience | UX on top of the shared command/state model. Visual work can overlap M1/M2; final integration depends on their working behavior. | All required flows operate consistently across desktop, overlay, tray and shortcuts, including error/recovery states. First-run and existing-profile paths pass. |
| M4 — Qualify a release candidate | PKG and QA on a frozen build after M1–M3. | Exact binaries, dependencies and configuration recorded; security delta reviewed; installers and supported-matrix acceptance pass; notices/source arrangements complete; rollback/support information prepared. |
| M5 — Validate externally and release beta | LAUNCH after M4. Recruit a small group covering different supported configurations, then expand. | Required user tasks succeed; blocking feedback addressed; remaining limitations published; maintainer explicitly approves the exact public beta artifacts and announcement. |

Documentation acceptance for this planning change does not complete M0's product/design decisions or any implementation milestone.

## 5. Security and capture acceptance

### Security remediation

| Assessment item | Required release behavior and acceptance |
| --- | --- |
| Unauthenticated FFmpeg discovery candidates | Establish trusted executable identity before any probe or execution. Do not execute arbitrary PATH/common-folder candidates as a trust check. Safely reject substitution and repair a missing engine. |
| Shared/custom-profile runtime substitution | Keep recording-data location separate from executable authority. Validate trusted runtime provenance and filesystem boundaries; metadata or cached length/timestamp alone cannot authorize executable reuse. |
| System-audio pipe accepts an unintended client | Restrict access and authenticate the intended encoder process before sending audio. Verify denial/connection races using controlled local processes and two Windows accounts. |
| Cross-user profile-mutex squatting | Verify ownership/access behavior across accounts and sessions; another unprivileged user must not claim the profile's startup authority. The earlier assessment left live Windows ACL behavior untested. |
| Failed display-to-application apply exposes F10 UI | Failed or rejected settings changes preserve the prior capture/exclusion policy. Test failures before and after worker acknowledgement, including rollback. |

The screenshot window-identity race was conditionally reproduced and fixed in PR #4, with bounded native evidence. Preserve that regression and its limits. Genuine different-account verification remains outstanding; same-account checks do not replace it. Review installer, dependency/update handling, new recording/audio commands and all changes since the assessed heads. No unresolved known medium/high/critical issues or the two identified low findings remain in the beta candidate. This is a release criterion, not a claim that a review proves absence of vulnerabilities.

### Clean footage and coexistence

Display capture records what is on a display, so visible F10 controls would normally enter the footage. Windows can mark those controls as excluded from recordings. Earlier controlled local observations found that showing such a window made NVIDIA report protected content and stop Instant Replay. Ordinary windows did not. The precise NVIDIA internals and behavior on other drivers remain unknown.

Application capture can record only the selected application's picture while leaving F10's separate windows outside it. Current application mode therefore uses ordinary F10 windows. This changes capture semantics and does not automatically establish fullscreen, synchronization or recovery acceptance.

Release criteria:

- Declare supported display/application capture and game presentation modes. Unsupported HDR or exclusive-fullscreen behavior is unavailable or clearly identified before use; do not advertise it as qualified.
- Showing settings, overlay, HUD or notices preserves underlying gameplay pixels and recording continuity in every supported combination. No black replacement masks or silently hidden acquisition gaps.
- F10 does not unexpectedly stop another recorder in a combination advertised as supported, and never changes NVIDIA settings automatically. A warning alone is not proof of coexistence. If clean display capture and coexistence cannot both be qualified, change the design or explicitly narrow supported combinations before releasing.
- The source selector names displays and identifies them visually; it does not expose raw adapter indices as the main user interaction. Reordering/disconnection preserves intended source identity or asks the user to reselect.
- Application loss, ambiguity, minimize and recovery never broaden capture to the desktop. Readiness, retained footage and a newly filling buffer remain distinguishable.
- Settings apply/rollback, screenshots, focus return, Escape and UI reconnection are tested against actual captures, not just UI screenshots or successful API return values.

## 6. Replay-save behavior

### User-selectable policies

The replay duration remains configurable. The examples below use five minutes. A successful save does not empty the buffer or stop capture in either mode.

| Mode | First successful save with a full buffer | Later successful save | Proposed default |
| --- | --- | --- | --- |
| Full replay each time | Last five minutes | Last five minutes ending at that request, even if it overlaps earlier files | Yes; preserve for existing profiles |
| Only new footage after the previous replay save | Last five minutes | Available footage after the preceding successful replay's actual end, capped by the rolling history still retained | No; explicit user choice |

Example with continuous capture and a full buffer:

| Request time | Full replay each time | Only new footage |
| --- | --- | --- |
| 12:10:00 | 12:05:00–12:10:00 | 12:05:00–12:10:00 |
| 12:10:20 | 12:05:20–12:10:20 | Approximately 12:10:00–12:10:20 |
| 12:11:00 | 12:06:00–12:11:00 | Approximately 12:10:20–12:11:00 |

Times describe intended content boundaries, subject to the explicit decoding-boundary tolerance below. Files remain independently playable MP4s. Screenshots and manual recordings do not advance the replay-save checkpoint.

### Boundary, queue and failure contract

- **Request time:** the shortcut/button press defines the end of the requested footage, not the time a queued export eventually starts. An accepted request protects the necessary retained inputs until export finishes or is explicitly cancelled. A closing segment may finish after the press; export still uses the request's boundary.
- **Continuous buffer:** pinning/snapshot protection prevents pruning a save's inputs; rolling capture and later saves continue. Do not use `-segment_wrap`, consume the replay history on save, or lower capture quality to make export look faster.
- **Coverage checkpoint:** new-footage mode tracks the actual end of successfully committed replay files for this profile and continuous capture session. It advances only after a valid final file is committed, never on acknowledgement, queue insertion, cancellation or failure.
- **Rapid saves:** use a bounded, ordered queue with limits on count and pinned storage, frozen in M0. Explain accepted, queued and rejected requests immediately. Rejected requests do not move the checkpoint. Avoid unlimited exports competing with capture.
- **Queued new-footage saves:** reserve enough input at acceptance to handle an earlier queued save failing. Resolve each request's start against the last successful checkpoint when its turn executes. A later request must not silently assume that failed predecessor footage was saved.
- **No new footage:** do not create an empty/duplicate new-footage file or claim success. Explain that there is no new independently savable footage yet. Debounce/minimum duration is an M0 decision.
- **Warm-up:** if less than five minutes exist, save the available history and show the actual duration. Do not pad the file or imply a full buffer.
- **Long gap:** if eight minutes have passed since the preceding save and only five minutes remain, new-footage mode can save those five minutes. State that the intervening three minutes have expired; do not imply that all eight were preserved.
- **Restart or discontinuity:** source change, capture restart/recovery, pause/resume that resets acquisition, or a recording-profile change starts a new coverage session. Never join unrelated timelines. The next new-footage save uses the available window and establishes a fresh checkpoint on success.
- **Mode change:** switching policies preserves existing files. Proposed rule: begin a fresh checkpoint on the next successful save, which uses the full available window; explain this in settings. Freeze exact behavior in M0.
- **Video boundaries:** current full-window selection may be roughly one second short because it starts at a safe decoding boundary. Proposed full-window tolerance is at most one second shorter than the available requested duration. New-footage mode should prefer a small, disclosed boundary overlap over silently omitting new pictures; proposed repeated-boundary allowance is at most one second. M0 must settle the exact rule, minimum duration and any encoder/container work needed. These are proposed targets, not current guarantees.
- **Failure/quit:** disk-full, missing source, queue cancellation, worker failure and shutdown preserve existing clips, release task-owned pins safely and never expose a partial file as a successful result. Pending work and unsaved coverage are visible to the user.

### Replay acceptance cases

REP passes only when fixtures and exported-file inspection establish all of the following:

1. Three full-window saves twenty seconds apart each contain the requested five-minute window within the frozen tolerance; expected overlap is present and capture continues.
2. In new-footage mode, the same requests yield about five minutes, twenty seconds and twenty seconds, subject to the frozen decoding-boundary rule.
3. Failure of the first or middle request does not lose coverage through an advanced checkpoint; retry/next-save results and any expired history are explicit.
4. Queued requests use their original press times, remain safe under pruning and obey count/storage limits, including failure of an earlier queued request.
5. Warm-up, no-new-footage, rapid presses, long gaps, source loss, mode change, restart and separate profiles follow the contracts above.
6. Successful files fully decode, have valid ordered per-stream timestamps, preserve intended A/V content and track mapping, and are independently playable. Buffer and user-media preservation are verified after failures.

## 7. Faster replay saving

Treat save latency as a core release feature. Preserve the approximately 42-second user report and the separate 16–17-second trials with their own provenance; neither is a controlled measurement of the future candidate.

Measure request receipt, last-segment availability, input protection/linking/copying, process startup and input probing, stream joining, MP4 finalization, final-file commit, notification delivery and cleanup separately. Existing `faststart` performs another pass to move playback information to the beginning of MP4; it is a candidate cost, not a proven explanation. Reuse of encoded data already exists, so do not propose avoiding re-encoding as if it were absent.

Provisional targets for M0 ratification:

| Measure | Proposed target | Measurement boundary |
| --- | --- | --- |
| Accepted/queued/error feedback | Within 200 ms | Physical shortcut/button request to truthful visible feedback |
| Single five-minute export, unqueued | Median at most 3 seconds; 95th percentile at most 5 seconds | Request to valid, closed, playable final MP4 |
| Reference profile | 1920×1080, 60 FPS, H.264 at 20 Mbps, system audio, local SSD | Declare GPU/driver, exact FFmpeg/app hashes, filesystem and source/output disks |
| Evidence | At least 20 matched exports spanning cold/warm conditions | Preserve sample order, individual times, failures, quantile calculation and file sizes; report stages and distribution |

These are performance goals, not measured results or public promises. If feasibility work requires revising them, record the reason and obtain a scope decision before qualifying the candidate; do not move the threshold after a failed batch. Higher bitrates, other codecs, separate tracks and slow/cross-volume storage need their own measurements and honest expectations. Report queue waiting separately and also report total request-to-file time.

Compare optimizations on matched builds/profiles with capture running; verify export duration/content, decode, timestamp ordering, A/V markers and gameplay overhead. Reuse existing retained/synthetic inputs for offline stage analysis first. Heavy decoding stays outside live timing windows. Removing redundant copies/passes or changing buffer/container organization are investigation options, not selected implementations. Preserve independent segments, protection before pruning, crash recovery and playback compatibility.

Success notices occur only after the final file is committed. A quick acknowledgement is not a completed save. If a save takes longer, show truthful progress or elapsed state plus recent results; do not fake percentages.

## 8. Manual recording, video and audio acceptance

### Manual Start/Stop recording

- Start/Stop is available through desktop, overlay, tray and configurable shortcut, with elapsed time and an unambiguous active state.
- Replay buffering and manual recording are distinct. Starting/stopping a manual session or saving a replay does not unexpectedly reset the other operation or its coverage checkpoint.
- A manual recording longer than the replay retention window retains its whole requested session; rolling-cache cleanup cannot delete its inputs. Validate at least a 30-minute session, concurrent replay saves and a multi-hour soak in the supported matrix.
- Stop produces a valid playable file and truthful finalization feedback. Crash, sleep, device loss, disk-full and shutdown preserve recoverable footage and explain incomplete results.
- Decide shared versus additional encoding from measured overhead and recovery behavior. Preserve the common acquisition clock and worker ownership; do not move frames into UI controls.

### Capture and video choices

- Named display selection with Identify, explicit application selection where qualified, and applied-source information are available without confusing pending settings with active capture.
- Native output plus useful downscaling presets preserve aspect ratio. Specify upscaling policy, odd dimensions, rotation, resize and multi-monitor behavior. Do not silently switch sources or upscale without user intent.
- Provide understandable quality presets with advanced FPS/bitrate/codec controls. Preserve current profiles; offer H.264/HEVC/AV1 only after actual initialization and qualification on the shipping engine/GPU.
- Distinguish configured output FPS from measured fresh-picture cadence. Unsupported combinations are prevented or explained before recording.

### Essential audio

- Select Follow Windows default or a specific playback device; select a microphone independently. Show applied device names, input meters and an explicit microphone test. New-user microphone capture defaults to off.
- Provide system/microphone levels, microphone mute and push-to-talk with visible state and shortcut conflict handling.
- Offer a simple mixed track and an editing mode with separate system/microphone tracks. Freeze track ordering, labels and the default playback track in M0; validate in supported players/editors and explain how to hear both sources. A compatibility mix alongside isolated tracks is an option to decide, not an already-selected format.
- Separate system/microphone tracks do not isolate a game from Discord or other system sounds. Per-application audio is deferred and labelled separately.
- Device removal, default-device changes, reconnect, silence, mute and microphone permission failure have defined behavior and actionable feedback. Do not silently activate a different microphone.
- Replay and manual recording use the common acquisition clock for every enabled track. Coded cold/warm markers and long-session markers must satisfy numeric initial-sync and drift limits frozen in M0; listening alone or container start/end skew is insufficient acceptance.

## 9. Interface and end-user experience

Review flows and sketches before polished implementation: first run, filling, ready, paused, manual recording, queued/saving, saved, failed, device missing, application unavailable, recovery and disconnected worker. Use one worker-backed state/command model across all surfaces.

| Surface | Required behavior |
| --- | --- |
| Desktop app | Overview, recent captures, capture/video, audio, controls, storage and troubleshooting. Display actual applied source/devices, replay availability, manual status and current save policy. Preserve pending edits until Apply. |
| Interactive overlay | Save replay, Start/Stop recording, screenshot, microphone controls and settings. Clear dismissal and focus return; opening it preserves capture. Qualify actual game presentation modes. |
| Recording indicator/HUD | Optional compact replay/manual/microphone state and elapsed time, with configurable position/visibility. It must follow the clean-footage policy. |
| System tray context menu | Status, Save replay, Start/Stop recording, screenshot, Pause/Resume replay, microphone mute, open captures, settings, overlay and Quit. Keep buffer restart in troubleshooting. |
| Notices and history | Distinguish request accepted, queued, saving and successfully saved. Offer Open file/Open folder and retain recent results so a missed notice remains discoverable. Failures have a useful next action. |

Acceptance includes keyboard-only operation, screen-reader names, readable contrast, Windows display scaling, multi-monitor placement, shortcut validation and rollback, and no focus theft from passive notices. Distinguish Pause replay from Stop manual recording. Define what a privacy/stop-all action and Quit do to both recording modes and queued saves; do not imply replay pause mutes all recording.

First run explains what will be captured, where temporary/saved data goes and expected storage use, then offers a short test clip. Startup is a user choice; upgrades preserve disabled startup and deliberate pause. Storage controls show estimated versus actual use, low-space warnings and bounded disposable cache. Never silently delete saved clips. Diagnostics are local, inspectable and redacted before sharing; uploads of logs or media require user action. Checking for updates must not interrupt a recording.

## 10. Installer and distribution

Recommended beta route: a signed per-user `setup.exe`, normally without elevation; Inno Setup is a candidate to evaluate. MSI is optional later for managed deployment, unless M0 identifies a concrete need. Select one supported installer/update route rather than maintaining two during beta.

PKG acceptance:

- Package the self-contained application and a trusted, qualified media engine with exact versions, hashes, capabilities, dependency notices and required matching source/build materials. Verify that the chosen engine supports every advertised capture path; do not assume the currently downloaded fallback matches a separately tested experimental build.
- Complete the corresponding-source arrangements for the actual FFmpeg/dependency build and preserve applicable native-component notices. F10's MIT license does not replace them.
- Sign the application and installer with a consistent verified publisher identity. Verify signatures on final artifacts. Explain that a newly signed release can still receive SmartScreen warnings; do not promise warning-free installation.
- A clean supported Windows user can install without a development SDK. Test no-network behavior for the selected bundled/download design and safe recovery after interruption.
- Upgrade handles a running worker, active manual recording and queued saves explicitly; it preserves preferences, paused/startup state and media. Test failed/partial upgrades and a documented recovery path.
- Uninstall removes task-owned application/startup entries and preserves saved recordings/screenshots by default. Optional data deletion is separate and explicit.
- Start with an explicit verified download/install update workflow. A future automatic updater needs authenticated packages/metadata, interruption recovery and its own security review.
- Release assets include version, source revision, dependency inventory, checksums, notices/source links, compatibility matrix, known limitations and rollback instructions. Signing credentials never enter the repository or PR logs.

## 11. Verification and retrospective follow-through

Reuse the [existing CI](../.github/workflows/ci.yml), [audio-clock checks](../tests/audio-clock.md), [worker checks](../tests/worker.ps1), [migration checks](../tests/migration.ps1), [game benchmark protocol](../tests/game-benchmark.md) and [frame-lineage protocol](../tests/frame-lineage.md). Review each script's parameters and side effects before execution. This planning change calls for documentation/link checks only.

| Retrospective recommendation | Required planning/delivery response |
| --- | --- |
| Declare test effects | Each entry point identifies UI/focus, live display/audio capture, synthetic encoding, startup changes, hardware needs and expected NVIDIA interactions. Synthetic does not imply no desktop effects. Select the existing headless route when it provides the needed coverage. |
| Validate readiness before batches | Preflight exact builds, source/process identity, free space and a timing window entirely after Ready. Perform a bounded admission smoke before an expensive frozen batch. Reject invalid runs; keep valid inconclusive results. |
| Separate decisions | Track source merge, binary distribution and performance/parity claims independently. Historical research holds are not automatic vetoes on unrelated source integration. |
| Reuse offline checks | Extract useful existing diagnostic/trace checks instead of building another test system. Keep native diagnostic correctness separate from ordinary product acceptance. |
| Close specific CI gaps | Maintain modern builds and relevant verification only. Retire the legacy app and port useful checks according to the workflow plan. Pinned FFmpeg clock/PCM checks and tiny malformed timestamp fixtures remain targeted follow-ups. Hosted builds are not live GPU/game acceptance. |
| Budget evidence | Record build/source/configuration, purpose, size, retention class and dependent review. Keep representative accepted/rejected inputs and compact receipts; retire evidence only under the user's policy. Ignored source worktrees are not disposable recordings. |
| Keep one live owner | One session owns the game, NVIDIA controls and recorder during each measurement. Keep heavy analysis outside timing windows and use a compact ownership handoff. |
| Keep documentation usable | Use bounded evidence retrieval, verify tracked links and keep durable guidance separate from dated research results. Do not recreate coordination already retired. |

The release matrix names exact supported Windows builds, NVIDIA GPU/driver configurations, codecs, capture sources, display modes, audio devices and storage profiles. Begin with a narrow qualified set. Unsupported HDR/exclusive-fullscreen combinations remain explicitly unavailable/unqualified; expanding the matrix requires evidence.

QA acceptance covers full five-minute retention, both replay policies, saving while recording, complete manual sessions, strict decoding/timestamp checks, coded A/V alignment/drift, source-picture continuity, human gameplay/audio review, measured overhead, device/display changes, UI/worker termination/reconnection, source loss, disk exhaustion, sleep/resume, sign-in/reboot and deliberate-pause persistence. Installer checks use clean environments; capture checks need actual hardware. Preserve task-independent recorder/NVIDIA settings and user media.

M0 must freeze numerical A/V, gaming-overhead, memory/storage, recovery-time and save-speed budgets plus sample plans. A release candidate cannot pass with these limits unset. Record each criterion as pass/fail/not-run with artifact/build IDs, validation owner, limitations and retained evidence. No required supported-matrix cell may remain failed or not-run at M4. Scope reductions require an explicit product decision and matching UI/release documentation, not a silent exemption.

## 12. External beta and release decisions

After M4, use a small external group across the supported matrix before a broad announcement. Essential tasks: install, finish setup, save the first replay, make repeated saves in both modes, record a manual session with microphone audio, recover from a device/source change, find the result, upgrade and uninstall safely.

Gather installation/first-capture success, failed-save reports, request-to-file times, audio/sync problems and recovery failures through voluntary reports or inspectable diagnostics. Do not add hidden telemetry. Publish a clear quick start, compatibility/known-issues page, issue templates, changelog and private vulnerability-reporting route.

Publish the first public binary as an explicitly labelled beta/pre-release, with a concise gameplay/manual/audio demonstration after the product works reliably. Broader community promotion follows external results and support readiness; it is not a prerequisite for engineering qualification. Keep positioning focused on local recording, useful controls and predictable behavior; do not claim lower overhead, instant saving or NVIDIA parity without appropriate matched evidence.

| Decision | Required basis | State |
| --- | --- | --- |
| Merge a source change | Scoped review and relevant automated/local checks | Existing prototype integration is historical; future PRs assessed separately |
| Distribute this beta | M1–M5 evidence, exact package/security coverage, support readiness and maintainer approval | Open |
| Claim performance improvement/parity | Matched repeated ordinary-product trials, valid instrumentation, frozen budgets and disclosed limits | Open; not implied by beta distribution |

## 13. Decision register before implementation

Agreed: Windows + NVIDIA beta first; Linux later; manual recording, rebuilt interfaces, essential audio, installer, faster saving and both replay-save policies are in scope. Source integration and current stability are not release qualification.

M0 must settle these proposals and unknowns:

1. Full replay as the default, existing-profile migration, policy-switch/checkpoint rules, decoding-boundary tolerance and minimum new-footage duration.
2. Queue count/storage limits and stop/quit/privacy behavior across replay, manual recording and pending exports.
3. Supported clean-footage/coexistence policy and initial hardware/driver/source/presentation-mode matrix.
4. Audio track layout/default playback mix, device fallback rules and shared versus additional encoding for manual recording.
5. Save-speed feasibility and frozen numerical A/V, resource, recovery and gaming-overhead budgets, with reproducible sample plans.
6. Installer builder, signing route/budget, exact shipping FFmpeg package/capabilities and dependency/source distribution arrangements.
7. Reviewed UI flows, work-item ownership, external tester coverage and delivery estimates after the preceding unknowns are bounded.

Reference material: [NVIDIA recording workflows](https://www.nvidia.com/en-us/geforce/news/nvidia-app-download-and-features/), [Windows window capture exclusion](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowdisplayaffinity), [FFmpeg format/faststart documentation](https://ffmpeg.org/ffmpeg-formats.html), [FFmpeg licensing](https://ffmpeg.org/legal.html), [Inno Setup capabilities](https://jrsoftware.org/isinfo.php) and [Microsoft signing/SmartScreen guidance](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/smartscreen-reputation). These support the technical options; they do not qualify F10's implementation.
