# Security remediation

## Agreed scope

Fix all five findings reported by the 20 October 2026 audit: unauthenticated FFmpeg discovery, profile-controlled FFmpeg substitution, audio-pipe client authentication, Global profile-mutex squatting, and premature UI capture-exclusion changes during settings application. Investigate the deferred screenshot window-identity race, fix it if validated, and retain an inconclusive result as outstanding rather than claiming closure.

The implementation starts from main at e8713db24a7476b0e7db71548ae69b75c57886af. The audit reviewed an earlier tree; current source inspection confirmed all five findings remain present at this baseline.

## Accepted decisions

- Use the [Windows account security boundary](adr/0001-windows-account-security-boundary.md), with the launched encoder as the exclusive audio recipient.
- Use the [managed recording engine policy](adr/0002-managed-recording-engine.md), including automatic setup, verified offline reuse, explicit repair, and preservation of existing profiles and media.
- Keep dependency installation separate from a [recording profile](../GLOSSARY.md).
- Use [protected profile ownership](adr/0003-protected-profile-ownership.md) for worker, startup and interactive-instance coordination. Canonical profile paths and directory leases keep alias changes from redirecting an active worker.
- Reject an affected operation when the intended audio recipient or capture target cannot be established. Audio-enabled recording must not silently become video-only, and window capture must not fall back to monitor capture.
- Retain UI capture exclusion throughout uncertain settings transitions.
- Close a finding only with focused regression evidence, applicable modern builds, and a final security review of the branch. Cross-account protection requires genuine cross-account testing before it is described as fully verified; otherwise retain verification as outstanding.

## Implementation agreement

1. Replace automatic external-engine discovery with the approved managed engine. Establish executable trust before capability probes and later launches; reject modification even when file size and timestamps are preserved. Separate the engine trust root from custom recording profiles, and preserve automatic setup, verified offline reuse and explicit repair.
2. Restrict the system-audio pipe to the recorder account and admit only the launched encoder process before delivering bytes. Reject unintended clients and fail the affected operation clearly if a valid recipient cannot be established.
3. Replace the vulnerable profile-ownership mechanism while preserving one worker per user/profile across Windows sessions. A different account must not be able to impersonate a legitimate profile owner through a predictable object name. Preserve rejection before replay-cache cleanup and release ownership after task termination.
4. Make UI capture exclusion follow the acknowledged active capture state. Cover pending, rejected and successful transitions in both directions, preserving exclusion while the active mode is uncertain.
5. Investigate screenshot window-identity changes between validation, capture binding and publication. Fix a validated issue and test stale-output rejection; retain any unresolved binding-identity proof gap explicitly.
6. Update affected setup instructions and checks, run focused regressions and applicable modern builds, then review the complete branch diff for security. Prepare concrete capture and second-account test procedures after the patch and harness exist; obtain task-specific authorization before those steps. Report implemented fixes separately from outstanding verification.

Use isolated profiles and evidence directories, preserving existing preferences, media, installed recorder state and unrelated checkout changes. Once confirmed, this agreement authorizes local implementation and verification within those boundaries; pushing, merging, installing and publishing remain separate actions.

## Interview status

Questions 1 through 8 and the implementation agreement were confirmed by the user. The fixes and focused regression harnesses are implemented. On 21 October the user authorized native UI and live system-audio checks, which passed after NVIDIA Instant Replay was stopped and then restored. The user accepted skipping the different-account check because no second Windows account is available; that verification remains explicitly unexecuted.

## Implementation and verification status

| Audit area | Implementation | Verification boundary |
| --- | --- | --- |
| Automatic FFmpeg discovery | Approved package and executable digests; no external search; locked launch-time verification | Legacy dependency regression, offline reuse, same-size/timestamp tampering and launch lease checks |
| Profile-controlled runtime substitution | Separate account-protected engine root; old profile manifests/runtime ignored and preserved | Profile preservation, storage ACL rejection and direct-launch rejection checks |
| System-audio recipient | Explicit account ACL, local-only pipe and kernel-reported expected-child PID before bytes | Marker-byte admission tests on modern .NET and Framework; live WASAPI/FFmpeg recording and both-track decode passed; genuine cross-account check skipped |
| Ownership squatting | Protected file leases replace worker/startup/UI mutex coordination; canonical directory binding | Alias/competing-owner/crash-release checks and two paused workers with a retargeted junction; genuine cross-account check outstanding |
| Settings capture exclusion | Exclusion during uncertainty; operation acknowledgement before trusting window mode | Headless pending/rejected/lost/stale/replaced-worker checks; native pending/rejected/successful window affinity transitions passed while paused |
| Screenshot identity | Screenshot-specific window property and monotonic epoch, checked before capture, during acquisition and before publication | Exact HWND/PID/start/class reuse reproduced with delayed destroy-event delivery; patched path rejects replacement pixels, preserves normal capture and cleans markers |

An independent candidate review identified profile rebinding and remaining local startup mutex routes. Both were addressed within the ownership boundary. Failed/inconclusive fixture attempts are retained with local evidence; native directory sharing is tested with a real directory move, and cache isolation is tested by applying settings to one paused worker after its original alias points at another worker's profile.

## Validation facts

Synthetic worker media bypasses the production system-audio pipe. Its success cannot establish pipe admission or live WASAPI integration. A dedicated kernel-pipe check can exercise expected-child acceptance and wrong-PID rejection without recording audio.

Native affinity checks can inspect pending and rejected settings transitions without recording desktop footage. A genuinely different Windows security token is required to demonstrate cross-account pipe and ownership behaviour; ACL inspection alone is more limited evidence.

Coordinated checks passed with the ordinary Windows user token: the paused Settings window retained native affinity `0x11` during pending/rejected changes, released it only after a successful window-mode acknowledgement, and restored it before the monitor transition. No recording started. A separate eight-second real default-output WASAPI trial used generated video, advanced the recording buffer and saved a replay whose required video and audio tracks both fully decoded. This establishes live pipe integration, not perceptual audio quality or A/V sync. All NVIDIA overlay status fields matched the initial state after restoring Instant Replay.

The different-account fixture was not run, as accepted by the user. Protected DACL rejection, wrong-PID rejection before audio bytes, competing-owner tests and source inspection support the design, but do not provide a calibrated probability or executed cross-account proof. Full cross-account verification remains outstanding; no account was created.

The same-HWND question was investigated with genuine Win32 windows and the approved FFmpeg build. With destroy-event dispatch deliberately withheld, Windows recycled the exact HWND after 32,766 recreations in approximately 109 seconds, preserving PID, process start, executable and class. The prior tracker retained generation 1 and the production screenshot helper published the replacement's black pixels instead of the original white window. This establishes a conditional continuity gap, not ordinary event latency or an attack frequency.

Window screenshots now establish a private lifetime property before exposing a screenshot epoch. Loss of that property or a change in the underlying tracking generation retires the epoch. Reinspection verifies the configured application intent after stamping. Recording retains its original tracking policy; Windows refusal to establish the marker rejects only the affected screenshot with a reason. In particular, an ordinary recorder may refuse screenshots of an elevated application under UIPI.

The native post-patch trial reused the exact HWND while ordinary tracking still reported generation 1. The original property was absent on the replacement, the screenshot epoch changed, and no replacement PNG was published. A later screenshot of the newly bound window succeeded. Independent trackers and normal disposal preserved/removed only their own properties. An opt-in repository fixture is described in [the security checks](../tests/security.md).

This closes the demonstrated replacement-after-binding gap within the accepted trusted-owner boundary. A permitted process can enumerate/copy window properties; the marker is not an authentication mechanism against an actively malicious window owner. The experiment deliberately delayed notifications and does not establish a naturally occurring end-to-end worker exploit. No desktop or audio was captured during this investigation.
