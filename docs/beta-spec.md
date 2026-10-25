# F10 Windows + NVIDIA beta specification

Version **1.0**, 25 October 2026. Consolidates accepted product decisions through **Q158**, approved screen/state reviews and ADRs 0001–0006. The maintainer confirmed the testing boundaries on the same date. Later accepted refinements take precedence over earlier interview wording and historical prototype behaviour.

Published tracker: [consolidated specification #10](https://github.com/ju57/f10-recorder/issues/10), labelled ready-for-agent. **M0 planning is complete under Q158; implementation and release qualification remain open.**

This is the M0 planning baseline. It specifies intended behaviour, not completed implementation or release qualification. Q158 permits planning closeout with explicitly owned unresolved engineering work; **every release gate below remains mandatory**. No calendar release date is committed. Publication of this spec authorizes neither installation nor public binary release. ju57 retains approval of exact release artifacts.

## Problem Statement

People need to save the gameplay that just happened or record an entire session without interrupting it. The current prototype does not deliver the complete agreed experience: replay exports can miss requested boundaries or take too long, manual recording and essential microphone controls need integration, and capture/exclusion/recovery are not qualified for the required fullscreen and NVIDIA coexistence cases. Recording state, accepted saves and recovery material must remain trustworthy even when the UI, source, device or storage fails.

An attractive interface, a successful capture API call or a playable file alone cannot establish that the intended fresh footage was recorded. The beta needs one consistent user experience backed by independently verified media, safe ownership, explicit failure states and an offline installer.

## Solution

Deliver **F10**, a local Windows + NVIDIA replay and manual recorder using the maintained C#/.NET/Avalonia application and independent worker. Support whole-display and selected-application capture, qualifying windowed, borderless and genuine exclusive fullscreen separately. Offer exact-duration rolling replays and optional new-footage-only saves, manual recording, screenshots, named audio inputs and one mixed or exactly two ordered audio tracks.

Provide the accepted setup, seven-section desktop, edge overlay, optional passive indicator, tray, notifications, recovery and safe-exit flows. Preserve existing preferences and media. Package verified runtime/engine dependencies in a signed per-user installer, qualify the complete supported matrix, run a small voluntary external beta and release only after the maintainer approves the exact candidate.

Unresolved mechanisms become owned engineering work with pass conditions. They do not silently change the user contract or become accepted implementations merely by appearing in a research report.

## User Stories

1. As a new user, I want a short one-time setup with the primary monitor preselected, so that I can understand what will be captured before recording starts.
2. As a new user, I want my unfinished setup choices remembered, so that closing setup does not discard my work or start capture/startup registration early.
3. As a new user, I want Finish to activate the accepted configuration, so that a failed start leaves my choices available for Retry.
4. As a user, I want a named display or application-window selection, so that the capture target is explicit.
5. As a privacy-conscious user, I want selected-window capture never to fall back to the desktop, so that an unavailable target cannot broaden what is recorded.
6. As a display-capture user, I want recording to continue when a foreground app closes or minimizes, so that desktop capture remains continuous.
7. As a gamer, I want supported traditional fullscreen and borderless applications recorded correctly, so that capture does not depend on an undisclosed presentation-mode restriction.
8. As a user, I want F10 controls absent from footage and compatible with the supported NVIDIA recording configuration, so that opening controls does not corrupt capture or unexpectedly stop another recorder.
9. As a user, I want unavailable sources identified truthfully, so that a black or stale picture is not presented as healthy recording.
10. As a user, I want the original selected source restored safely after it returns, so that recovery does not choose a different source without my action.
11. As a gamer, I want video to match the displayed presentation, including intentional stretching and visible borders, so that recorded gameplay resembles what I saw.
12. As a user, I want a recording's output dimensions to stay fixed when its source resizes, so that the file remains valid and visually continuous.
13. As a user, I want replay duration adjustable from one to thirty minutes with linked slider/numeric controls, so that I can choose the history I need.
14. As a user, I want a full replay to contain the configured available interval ending at my request, so that five minutes means the last five minutes rather than a segment-rounded substitute.
15. As a user, I want available-footage saves during warm-up, so that I can preserve a moment before the full buffer fills.
16. As a user, I want Full replay as the default, so that repeated saves can overlap without consuming history.
17. As a user, I want an explicit Only new footage option, so that subsequent successful saves avoid already saved footage when history permits.
18. As a user, I want the checkpoint preserved when only the save mode changes, so that switching modes does not forget what was saved.
19. As a user, I want a fresh checkpoint after a capture-session change, so that unrelated timelines are not joined.
20. As a user, I want every accepted press to retain its original request time and settings, so that later queue execution or edits do not move the requested endpoint.
21. As a user, I want one export and two waiting requests with immediate capacity feedback, so that rapid presses are predictable and bounded.
22. As a user, I want failed or canceled predecessors not to discard unsaved coverage, so that later new-footage requests can still preserve it.
23. As a user, I want sub-second/no-new-footage requests explained without empty files, so that a skipped request is not mistaken for a save.
24. As a user, I want expired history disclosed, so that a new-footage save does not imply missing earlier minutes were preserved.
25. As a user, I want accepted/queued feedback distinguished from Saved, so that I know when a valid final file actually exists.
26. As a user, I want replay saving to meet the agreed speed targets while capture continues, so that preserving a moment does not disrupt recording.
27. As a user, I want cancellation before publication and truthful handling of a late cancellation, so that a completed file is never deleted or mislabeled Canceled.
28. As a user, I want manual Start/Stop independent of replay, so that I can record a whole session while replay is paused or save a replay during manual recording.
29. As a user, I want manual recording without a thirty-minute cap, so that duration is constrained by safe storage rather than replay retention.
30. As a user, I want a session-ending manual interruption reported and preserved for recovery, so that I decide when to start another recording.
31. As a user, I want replay pause, manual stop, Stop all capture and Exit to have distinct effects, so that one control does not unexpectedly end another activity.
32. As a user, I want source/video/audio settings shared between replay and manual recording, so that their controls describe the same selected setup.
33. As a user, I want screenshots from the applied source at source resolution, so that video downscaling does not reduce screenshot detail.
34. As a user, I want screenshots to leave replay checkpoints unchanged, so that image capture cannot consume new replay footage.
35. As a user, I want video choices limited to qualified combinations, so that a listed codec does not falsely promise usable hardware support.
36. As a user, I want to choose playback and microphone devices independently by identity, so that changing a Windows default cannot silently substitute an input.
37. As a user, I want microphone enabled initially when available/permitted and a clear mute control, so that setup is usable without ambiguous On/Off labels.
38. As a user, I want either or both audio inputs optional, so that missing or intentionally disabled audio never blocks setup.
39. As a user, I want denied microphone permission distinguished from disconnection, so that I can take the right recovery action.
40. As a user, I want silence and a warning while a chosen audio device is absent, so that video and other audio continue without recording an unintended device.
41. As a user, I want ordinary same-device reconnection to preserve mute/enabled intent, so that reconnection does not unexpectedly open my microphone.
42. As a user, I want explicit microphone enabling after permission restoration, so that restored access is not treated as recording consent.
43. As a user, I want one playable mixed track or exactly two separate tracks, so that I can choose simple playback or editing flexibility.
44. As an editor, I want system audio first and microphone second, with a silent microphone track when required, so that file structure stays predictable.
45. As a user, I want separate 0–100% recorder levels and live meters, so that I can check input activity without altering Windows device volume.
46. As a user, I want optional hold-to-talk and safe release on lock/suspend/uncertain key state, so that microphone capture does not remain latched.
47. As a user, I want each enabled audio track synchronized to video across long sessions and recovery, so that the files are useful for playback and editing.
48. As an existing user, I want an upgrade to preserve valid preferences, startup/pause intent and saved media, so that migration does not reset my workflow or repeat full setup.
49. As a user, I want only necessary migration corrections requested, so that a partially invalid profile does not lose every valid choice.
50. As a user, I want saved pending settings distinguished from applied settings, so that I know what the recorder is actually using.
51. As a user, I want Apply changes once, with no second confirmation screen, so that restart/refill consequences are clear before the action.
52. As a user, I want restart-required settings saved during manual recording and applied after safe completion, so that edits do not interrupt the recording or need a second Apply.
53. As a user, I want failed manual saves and blocked pending settings reported separately, so that neither failure nor the reason for deferral disappears.
54. As a user, I want restart-free changes applied without resetting replay, so that shortcuts, notices and mute adjustments do not destroy useful history.
55. As a user, I want separate recording, screenshot and replay-cache folders with inset picker buttons, so that I can select directories without typing paths.
56. As a user, I want cache estimates, actual usage and an editable budget, so that I can judge whether my chosen quality/history fits.
57. As a user, I want protected inputs and retained recovery material preserved even under storage pressure, so that new work cannot destroy old work.
58. As a user, I want low-storage warnings and safe stopping before exhaustion, so that recording failure does not silently damage accepted work.
59. As a user, I want explicit Resume after a low-storage stop, so that fluctuating free space does not repeatedly restart capture.
60. As a user, I want folder changes to affect future work while active/accepted work keeps its destination, so that settings edits do not move an in-flight recording.
61. As a user, I want a compact Overview with separate replay/manual states and shared audio, so that I can understand readiness at a glance.
62. As a user, I want icons, elapsed time, shortcut chips and accessible explanations, so that recording states remain legible without excessive labels.
63. As a user, I want a stable normal window size with scrolling content, so that settings and libraries do not enlarge the application unpredictably.
64. As a user, I want active requests above saved media, so that ongoing work remains visible.
65. As a user, I want recordings and screenshots separated into thumbnail tabs, so that I can identify media quickly.
66. As a user, I want bounded scrollable libraries and multi-selection, so that managing many files does not enlarge the window.
67. As a user, I want confirmed removal of selected saved files to the Windows Recycle Bin, so that accidental deletion is recoverable.
68. As a user, I want a missing saved file distinguished from its historical successful result, so that a later move/delete does not rewrite save history.
69. As a user, I want actionable save-folder failures, so that Review save folder opens the relevant setting.
70. As a user, I want one consistent notification family with independent dismissal and a small stack, so that simultaneous outcomes do not overwrite each other.
71. As a user, I want success actions and discoverable result history, so that missing a short notice does not lose access to my file.
72. As a user, I want configurable notice preferences without hidden underlying failures, so that suppressing pop-ups does not imply recovery.
73. As a user, I want key-combination capture, conflict feedback and per-action defaults, so that shortcuts are easy to configure safely.
74. As a user, I want a narrow configurable left/right overlay on the active application's monitor, so that I can operate F10 while keeping the background visible.
75. As a user, I want overlay dismissal to return focus safely, so that closing controls does not accidentally activate the underlying application.
76. As a user, I want the overlay available over desktop applications as well as games, so that I need not close the desktop app to use quick controls.
77. As a user, I want restart-free quick settings and an in-overlay Gallery, so that common actions do not require leaving the overlay.
78. As a user, I want gallery recordings to expose playback/seek, volume, file location and confirmed delete controls, so that I can inspect and manage them directly.
79. As a user, I want overlay export feedback without a permanent export-details area, so that the panel stays focused.
80. As a user, I want an optional minimal recording indicator with size/corner/content controls, so that I can see only the status I need.
81. As a user, I want to drag the indicator preview and snap to the nearest corner, so that placement is direct and predictable.
82. As a user, I want the indicator hidden over ordinary desktop/browser/Explorer contexts unless Sticky is enabled, so that it does not clutter unrelated work.
83. As a gamer, I want an optional actual application FPS counter, so that the value describes the application rather than recording FPS.
84. As a user, I want unavailable FPS shown truthfully, so that unsupported measurement cannot produce invented data.
85. As a user, I want temporary indicator issue text to disappear without concealing the underlying warning, so that the passive strip remains compact.
86. As a user, I want tray activation and actions consistent with the desktop/overlay, so that controls behave the same when the main window is hidden.
87. As a user, I want current Diagnostics separated from technical history and last-known data, so that disconnected values cannot look current.
88. As a user, I want a guarded troubleshooting restart and an explicit Reconnect action, so that recovery controls cannot interrupt active accepted work or imply success.
89. As a user, I want local redacted diagnostics that I preview before export/share, so that troubleshooting does not upload my media or expose private paths.
90. As a user, I want buffering/saving/recovery independent of UI lifetime, so that a closed or crashed desktop window does not end recording work.
91. As a user, I want unfinished recordings discoverable in Captures with Back, Recover and Discard, so that recovery remains a deliberate action.
92. As a user, I want cancellation or failed recovery to retain original material, so that I can retry later.
93. As a user, I want explicitly partial recovery with actual duration and missing-content disclosure, so that an incomplete result is not represented as a complete recording.
94. As a user, I want disjoint recovered intervals as separate chronological clips, so that missing time cannot be hidden by concatenation.
95. As a user, I want recovery to wait when ongoing recording/accepted saves need resources, so that repair does not disrupt current work.
96. As a user, I want permanent discard limited to the specifically confirmed unfinished item, so that it cannot delete unrelated recovery or saved media.
97. As a user, I want direct Exit when idle and truthful finalization progress otherwise, so that accepted work is not silently abandoned.
98. As a user, I want Keep waiting/Force exit only with clear stalled-work handling and a safe-default confirmation, so that a timeout alone cannot destroy unfinished work.
99. As a user, I want Return/Escape/close during pending Exit to cancel only exit intent, so that work continues without automatically restarting acquisition.
100. As a user, I want definite finalization failures visible before exiting, so that I can review them or acknowledge exit deliberately.
101. As a user, I want one offline per-user installer with verified dependencies, so that I do not need to install runtimes/FFmpeg manually or grant unnecessary elevation.
102. As a user, I want upgrades to wait for active manual/accepted work and offer cancel/wait, so that installation does not force recordings to end.
103. As a user, I want safe rollback and uninstall behaviour that preserves my media, so that installation lifecycle failures do not erase recordings.
104. As a user, I want signed, versioned beta downloads with checksums and compatibility information, so that I can identify the exact supported package.
105. As a user, I want a consistent F10 identity, accessible controls and honest limitations, so that the utility is understandable without unsupported parity claims.
106. As a maintainer, I want owned, evidence-backed release gates and explicit approval of exact artifacts, so that planning acceptance cannot be mistaken for a qualified beta.

## Implementation Decisions

### 1. Architecture and ADR reconciliation

- Keep the maintained modern C#/.NET/Avalonia product. Portable Core owns configuration rules, clocks, replay selection and notification rules; Windows owns acquisition/audio/native integration and the worker; Desktop owns presentation and worker interaction. Captured frames stay outside UI controls.
- Preserve the current-user, bounded, versioned local control channel; independent per-profile worker lifetime; and canonical filesystem-identity profile leases. UI reconnect reconciles actual worker state rather than blindly repeating operations.
- **ADR 0001, Windows account boundary:** ordinary same-account processes remain inside the trust boundary; retain cross-account denial and session-owned audio-recipient restrictions.
- **ADR 0002, approved managed engine:** authenticate the exact engine independently of profile metadata. Accepted beta bundling refines prototype download-first setup: offline installation uses verified bundled runtime/engine. Explicit repair may provision an approved replacement; worker restart must not repeatedly download or use an unapproved fallback.
- **ADR 0003, protected ownership:** retain protected file leases, alias/session exclusivity and directory-handle identity. Different profiles retain independent ownership.
- **ADR 0004, modern-only product:** do not reintroduce the legacy application/build as a shipping or CI target. Disposable native/WinForms source fixtures remain verification tools only.
- **ADR 0005, replay ownership:** the replay module owns accepted requests, pins/copies and retired-session resources until all accepted work resolves. Q9/Q23 intentionally extend the old no-queue prototype to one active export plus two waiting requests. Retain serial execution, no automatic retry and publication-based success.
- **ADR 0006, displayed presentation:** record the source's visible stretching/borders and use Q157's fixed session canvas. Do not weaken source/privacy boundaries to implement a transform.

### 2. New-user defaults and setup

| Choice | Accepted new-profile baseline |
| --- | --- |
| Source | Primary monitor, with explicit whole-display/selected-window choice and conditional scope explanation |
| Replay | Enabled only after successful Finish; Full replay each time; five minutes; editable 1–30 minutes |
| Video | Native displayed size, 60 FPS, H.264, 20 Mbps |
| Audio | Current Windows playback and microphone devices preselected by name/identity; microphone enabled when available/permitted; one mixed track; recorder levels 100% |
| Audio optionality | Either or both inputs can be off, unconfigured or unavailable without blocking setup or requiring a separate continue-without-audio confirmation |
| Startup | Start F10 with Windows checked but inactive until setup completes; follow saved intent thereafter |
| Folders | Recordings: Videos/F10 Recorder; screenshots: Pictures/F10 Recorder; separate configurable temporary replay-cache location |
| Cache budget | Editable 10 GB new-profile replay-cache budget; validate actual estimated demand before activation |
| Indicator | Enabled, top right; replay/manual/microphone status; application FPS initially off; Sticky initially off |

Existing valid preferences override new-profile defaults. F10 is the product name; internal executable/project/folder identities are not renamed automatically.

Setup uses the approved logo and Initial F10 setup title, concise labels, Video Default Settings, linked integer-minute slider/field, named device selectors and segmented meters. The microphone icon sits below its device selector beside its meter; omit visible On/Off and duplicate Unavailable text. Reserve inset space for folder-picker buttons and select chevrons. Do not restore removed helper paragraphs or the footer divider. Source/folder/storage failures block completion with inline reasons; optional audio absence does not. Finish shows Starting… and becomes Retry after actual startup failure, retaining choices and opening Overview only on success.

### 3. Replay admission, boundaries and ownership

- Full replay is the default for new/existing profiles. Once sufficient continuous footage exists, save the configured duration ending at the original request, within one output frame of video boundary/duration rounding. Do not substitute padding, altered duration metadata or the old one-second shortfall.
- Warm-up saves contain actual available footage, clearly showing available/configured duration. Require at least one second. Saving never consumes rolling history.
- New-footage replay begins at the most recent successful replay endpoint in the same capture session, regardless of the policy used for that earlier save. Without a checkpoint, use available history. Disclose history that expired before the request.
- A policy-only change preserves buffer/session/checkpoint. A genuinely new capture session, including replay Resume, starts fresh history/checkpoint. Foreground-app changes during whole-display capture do not create a new session.
- Admit at most one exporting and two waiting requests per profile; storage can reject sooner. Retain FIFO and original request timestamp, source, session, policy and destination. Rejection is immediate and truthful.
- Protect enough input at acceptance to survive pruning and a predecessor failing. Resolve queued new-footage starts against actual prior successful publication, not prior acceptance. A queued request resolving below one second becomes Skipped: no new footage with its original timestamp and no file/checkpoint change.
- Independently decodable completed segments and protect-before-export semantics remain invariant; incomplete segments are excluded and rolling storage must not overwrite inputs through segment wrapping.
- Publication of a valid closed final file establishes Saved. Acknowledgement, queue insertion, temporary-file existence, notice delivery and cleanup are distinct events. A failure/cancellation never advances coverage; late cancellation after committed success retains Saved and its checkpoint.
- Keep accepted old-session work alive across source changes, pause, UI lifetime and session retirement. It cannot advance a different session's checkpoint. Cancellation releases only task-owned resources no longer needed by others; no automatic retry.

### 4. Capture, video, manual recording and screenshots

- Whole-display capture records visible display content continuously across application close/minimize. Selected-window loss/minimize stops that source, retains footage and reports unavailability; no desktop fallback or silently recorded black/stale output. Restoring the original source may resume replay in a fresh session. Replacement selection requires user action.
- Display loss retains footage and reports Display unavailable. Returning the same display may restore replay in a fresh session; a changed primary designation alone never retargets capture. Deliberate pause and low-storage stops remain respected.
- Require true exclusive fullscreen as well as windowed/borderless coverage for both capture intents. Native overlay focus, clean-footage exclusion and NVIDIA coexistence require separate measured evidence. The failure tracked in issue #9 remains unresolved.
- Video choices are Native/720p/1080p/1440p/2160p, 24/30/60/120 FPS, 5–100 Mbps, and H.264/HEVC/AV1 where the exact engine/GPU combination is qualified. Enumeration alone is insufficient.
- A 1280×960 game visibly stretched across 1920×1080 must be recordable with that stretched appearance. Preserve visible aspect-ratio borders. Resolve Native at each recording/replay session start; keep that canvas fixed. Fit later source resizing without cropping/new stretching, permitting padding only when actual displayed aspect ratio changes.
- Manual recording shares selected settings with replay but has independent controls and no replay-duration cap. An active replay and a newly started manual recording may resolve Native at different source sizes; encoder sharing must not force either onto the wrong canvas.
- Pause replay stops replay acquisition while manual recording continues. Stop recording finalizes manual while replay continues. Stop all capture immediately stops new audio/video acquisition, then finalizes manual/accepted saves. Existing paused replay history remains savable within bounds until Resume/exit; Resume starts fresh history.
- A session-ending crash/sleep/source interruption preserves/report recoverable manual material and requires explicit new Start. Replay may recover automatically if previously enabled and safe, respecting pause and explicit low-storage resumption.
- Screenshots are PNG at applied source resolution independently of video scaling, omit the cursor by default and exclude F10 controls. Unavailable selected-window screenshots fail without desktop fallback. Screenshots never change replay checkpoints.

### 5. Audio and common time

- Retain explicit selected device identity rather than silently following changed Windows defaults or a matching friendly name. Follow Windows default mode is deferred.
- Device absence uses silence plus visible warning while healthy video/other input continue; retain the choice and resume the same returning device respecting enabled/muted intent. Denied microphone permission leaves other capture usable and requires explicit enable after access returns.
- The single microphone icon enables a disabled input by explicit action, mutes an active input and unmutes a muted input. The mute shortcut only toggles an already enabled microphone; it does not implicitly enable disabled/permission-blocked capture.
- Offer exactly one mixed track or two separate tracks: system audio first, microphone second. In two-track mode retain a valid silent microphone track when disabled/muted/unavailable as agreed; do not add a third compatibility mix.
- Levels are recorder-owned 0–100%, default 100%, no boost and no Windows device-volume changes. Levels/mute are live. Device/track/microphone-mode choices use Apply/Reset with truthful restart/defer behaviour.
- Hold-to-talk is optional and initially unassigned. Ordinary focus change does not release a genuinely held key; lock/suspend/uncertain key state closes the microphone gate and requires a new press.
- Preserve a common acquisition timeline for video, audio and save cutoffs, including video-only operation. Arrival time, media duration labels and a rebased downstream timestamp are not substitutes for acquisition evidence. Endpoint format/channel normalization and shared-versus-separate encoder selection remain measured engineering decisions, not adopted research proposals.

### 6. Settings, migration and storage

- Preserve valid old preferences, startup/pause intent and saved media. Existing users open Overview; request only necessary migration corrections. New profiles get full setup. Migration itself does not activate capture.
- Show edited/pending versus applied configuration truthfully. Use one Apply changes action and Reset edits; no second review/confirmation page. Explain restart/refill before Apply. Restart-free changes preserve session/history/checkpoint.
- Save restart-required settings during active manual recording/finalization; apply automatically after it ends and resources safely release. Report manual-save failure separately and retain recovery material. If safe release is unproven, keep settings pending with a blocker; no second Apply is required once safe.
- On apply failure, preserve/report actual state and restore the last working preferences where possible. Versioning, durable desired/applied generations, journaling and rollback transactions are engineering designs to validate, not facts established by wireframes.
- Output-folder edits affect future requests. Active manual and accepted replays retain their destinations. Cache-location changes wait for manual/accepted exports, then start a new replay session while preserving pause. Do not move/delete saved media automatically.
- Show compact cache usage/budget and estimated need, with separate inset directory pickers for recordings/screenshots/cache. Omit the removed Drive space/reservation dashboard; safety accounting still exists internally.
- The 10 GB cache budget includes rolling history, protected old-session footage and temporary input copies. Manual working storage and export/finalization output space are separate. A high-bitrate thirty-minute profile may require an explicit higher budget; never delete protected inputs or silently lower quality to fit.
- Use decimal GB and per-volume unreserved free space after accepted-output/finalization commitments. Warn below 5 GB and stop new acquisition before less than 2 GB remains. Handle same/different volumes without double counting. Exact estimator/burst/scratch margins and supported paths need calibration.
- Preserve saved clips, accepted work and retained recovery material under pressure. Recovered originals do not expire automatically to admit new capture. Explain the blocker and link to Recovery when retained material prevents capture.
- After a low-storage stop, require explicit Resume replay once safety checks pass; manual requires explicit Start. No automatic restart loop as space fluctuates. Unexpected external disk exhaustion remains a reportable failure despite reservation bookkeeping.

### 7. Desktop, library and notification behaviour

- Use the accepted normal desktop shell, standard minimize/maximize/close controls, logo and seven sections: Overview, Captures, Capture & Video, Audio, Controls, Storage, Diagnostics. Closing/hiding UI is distinct from explicit Exit. Remove maintainer branding from the shell.
- Maintain normal Overview window size across sections; scroll content and media lists rather than increasing the overall window. Support maximizing, short windows and usable-width-based form stacking without shrinking text.
- Overview emphasizes Instant replay with separate manual state and shared Audio. Use Replay ready to save specifically for replay admission, with truthfully dominant problem/queue/unavailable states. Put a pulsing buffering icon or paused icon beside Instant replay, with tooltip/accessibility text; use consistent manual-state icons and elapsed time. Keep Save replay stable while duration conveys available footage. Source/video/audio summaries link to their settings.
- Keep Stop all capture quiet but visible and separate, with a stop icon. Integrate current hotkeys into actions. Sidebar Overlay is a button with its hotkey; omit Quit there. Active navigation uses an inset/detached green marker sized to the selected row.
- Captures places active exports and ordered request/results above saved files. Preserve original times, duration, cancellation and truthful outcomes. Recordings/Screenshots tabs use thumbnails, scrollable lists and file details/actions. Support multi-selection, Clear selection and an accessible bin icon opening a count-specific Recycle Bin confirmation. Missing files do not retroactively erase a successful result.
- Use Review save folder for actionable output-folder failures. Recovery-needed messaging uses the shared notice family and remains discoverable through Captures.
- Shared notices are rounded, slightly translucent (96% opaque background), with opaque content and individual dismiss X. Stack simultaneous messages with recording-blocking issues first; do not overwrite unrelated outcomes. Dismissal never clears the underlying problem.
- Distinguish Accepted/Queued/Saving/Saved/Skipped/Canceled/Failed. Saved occurs only after final publication, offers Open file/Open folder and expires around five seconds with hover/keyboard-focus pause; history remains in Captures. Problem notices do not auto-expire. Notice preferences suppress presentation, not the state/history itself.

### 8. Controls, overlay, passive indicator and tray

| Action | New-user shortcut |
| --- | --- |
| Save replay | F10 |
| Toggle overlay | Alt+Z |
| Screenshot | Alt+F1 |
| Start/stop manual recording | Alt+F9 |
| Mute/unmute enabled microphone | Alt+F2 |
| Hold-to-talk | Unassigned until configured |

- All bindings are customizable. Capture combinations rather than free text, support Escape cancel and per-row defaults, report conflicts beside the action and retain prior working registration. Do not silently displace another action/application. Preserve valid existing-profile shortcuts on migration.
- Overlay is a narrow configurable left/right edge panel, with background visible; it is available whenever F10 runs, including desktop/browser contexts and while desktop UI remains open. Place it on the active application's monitor, otherwise last valid application monitor, otherwise primary. Placement does not retarget capture and remains stable during that opening, subject to safe visibility after monitor removal.
- Opening the interactive overlay deliberately takes input; capture continues. Escape/Close/overlay shortcut or outside click dismiss it and return focus to the previous application when appropriate. Consume the dismissal click; it must not trigger the underlying application. Passive surfaces never take focus.
- Overlay home includes recent thumbnails/Gallery, replay Save/Pause/Resume, manual Start/Stop/time, screenshot, microphone control and contextual settings. Show Alt+F2 as a meaningful microphone shortcut chip, not isolated helper text. Keep quick actions open. Provide only settings qualified as restart-free; explicit desktop-settings navigation remains available.
- Remove permanent export-count/No saves in progress/Export details rows from the overlay. Shared notices communicate saves; full progress/cancel/request management belongs in desktop Captures.
- Gallery uses the same Recordings/Screenshots tab style as Captures. A selected recording opens an adjacent viewer with play/pause, seeking, time, volume, Open file location and count/item-specific confirmed Recycle Bin removal. Screenshots omit recording-only controls. Keep viewer dismissal distinct from closing the overlay; no illustrative-agent placeholder labels in product UI.
- The optional indicator is a minimal unbranded passive strip, without logo/product name/export counts. Independently select replay, manual, microphone and actual application-FPS content, including FPS-only. Provide size and four-corner placement; dragging preview snaps to nearest corner and synchronizes selection. Preview scaling bounds are provisional native implementation parameters, not qualified requirements.
- By default show the indicator only over games/fullscreen applications, hiding over ordinary desktop/browser/Explorer. Sticky opts into persistence. Indicator target follows the configured capture display/application display, distinct from overlay placement. Game detection, FPS measurement, native placement and overhead need qualification; unavailable FPS is unavailable, not configured/acquired/encoded FPS.
- Indicator problem text lasts about five seconds; the compact warning/state stays truthful, with details/actions in interactive surfaces. No passive dismiss/action/focus targets. Native F10 surfaces must satisfy clean-footage/fullscreen/coexistence gates.
- Tray single left-click opens/restores Overview; right-click opens the menu. Open F10 first; recording actions next; navigation next; Exit F10 alone last. Use separate replay/manual status, retained/elapsed durations, aligned current shortcuts and disabled-action reasons consistent with other surfaces.

### 9. Diagnostics, recovery and Exit

- Diagnostics shows current service/source/replay/manual/audio state. Disconnected values are Unknown; label last-known values. Current problems have specific actions; healthy state says No current problems, with technical history expandable.
- Restart replay buffer explains history loss and requires confirmation; disable during manual/finalization/accepted exports and explain why. Do not queue an automatic troubleshooting restart. Reconnect is distinct from a reported successful restart, and deliberate pause remains respected.
- Preview a redacted local diagnostics snapshot before export, including build, OS/GPU/driver, applied profile, state and relevant errors. Omit media, filenames/full paths and sensitive endpoint identifiers. Export that inspected snapshot locally; sharing is explicit. No automatic telemetry, crash or recording uploads.
- Recovery is inside the normal shell under Captures, with logo/title, Back to Captures, item identity/time/last-known duration, Recover and Discard. Back leaves unfinished work discoverable and replaces Review later.
- Recovery needs explicit user action. Show actual Recovering state without invented percentage/ETA; block duplicate starts. Add complete output only after validation/publication. Failure/Cancel retains entry and originals for retry; late cancellation retains a committed result. Permanent item-specific discard uses a safe-default Cancel and explicit Discard permanently, limited to that item's unfinished data.
- Partial recovery is explicitly Partially recovered with actual duration and missing footage/audio disclosure. Preserve originals after partial success. Disjoint usable intervals become separate chronological clips showing original ranges/gaps, never an apparently uninterrupted concatenation. Active recording and accepted replays take priority; recovery waits with a reason until resources are safe.
- Exit immediately when no work remains. Otherwise stop acquisition and show a compact separate Finishing recordings window containing actual manual/save work. Do not invent progress/ETA. The stall notice belongs inside this window.
- Thirty seconds without measurable finalization/export progress exposes Keep waiting and Force exit. Dismissing/Keep waiting hides the notice only; actions and pending work remain. Force exit opens a safe-default Keep waiting confirmation naming unfinished work and recovery uncertainty. Force exit interrupts, not discards, and does not guarantee recovery.
- Return to F10, Escape or progress-window close cancels only exit intent, preserving finalization/saves and stopped acquisition. A definite failure remains visible with Review in Captures or Exit F10; review does not restart capture. Successful completion exits without a new confirmation screen.

### 10. Identity, accessibility and package direction

- Product name is F10. Preserve the exact approved realistic dark F10 keycap (option B), not rejected abstract/illustrated variants or an agent-preferred replacement. Small native/tray exports require legibility/DPI work; preserve the chosen identity.
- Use approved neutral dark surfaces, green #76B900 primary/active emphasis, Hanken Grotesk with Segoe UI fallback, 24/14/12 px baseline type hierarchy, 8 px controls/12 px dialogs, Phosphor solid icons and the explicit rounded-outline Lucide Animated hourglass exception. Retain its asset attribution/licensing. The logo's original raster shading is an asset exception, not a gradient/glass UI direction.
- Keep concise labels, no em dashes in product copy, visible focus, accessible icon names/tooltips, meaningful colour-plus-icon/text states, sufficient hit areas and keyboard/screen-reader/scaling checks. NVIDIA/Discord are reference principles; ship no extracted vendor brand/font/icon assets.
- Use one per-user Inno Setup installer, normally without elevation, bundling approved runtime/engine for offline installation. Explicit download/install updates only; automatic updates are deferred. Wait/cancel when manual/accepted work blocks upgrade; no forced recording shutdown. Preserve settings/startup/pause/media/recovery and provide a validated rollback route.
- Release under ju57 via GitHub Releases pre-release with signed intended F10 artifacts, checksums, exact version/revision, notices/corresponding-source arrangements, quick start, compatibility matrix and limitations. Keep upstream component signatures/identities distinct from F10 signing.
- Assess free SignPath Foundation first, retain ju57 branding and accept Foundation certificate identity if approved. Eligibility, application approval, human roles/MFA, exact artifact signing pipeline and any fallback/budget remain explicit gates. No application/contact/purchase is authorized merely by this spec.

### 11. Engineering work packages and ownership

The primary integration boundary is the existing recorder/worker command and status interface. Extend it only as required to represent the agreed operations; do not prescribe new production seams before need is demonstrated. Portable rules remain below that boundary, and native capture/media adapters supply externally verifiable results. These are scoped work packages for later branches/PRs, not an instruction to implement the entire beta in one change.

| Work package | Scope | Technical owner | Validator / decision owner |
| --- | --- | --- | --- |
| WP1: capture foundation | Resolve fullscreen/stale-picture failure and clean-footage/coexistence before dependent production promises | Codex; investigation #9 | Codex prepares evidence; ju57 accepts scope/candidate; named independent review for resulting change |
| WP2: replay and manual engine | Durable accepted-request ownership/queue/checkpoints, exact export integration, independent manual lifecycle and validation | Codex | Codex automated/media checks; ju57 acceptance; scoped reviewer named before merge |
| WP3: audio and common clock | Explicit endpoints, mix/two tracks, levels/mute/hold, recovery, synchronization and encoding comparison | Codex | Codex clock/media checks; ju57 live acceptance |
| WP4: storage, migration and recovery | Reservation calibration, desired/applied settings, upgrade rollback, safe repair/cancel/partial publication | Codex | Codex failure-injection/native checks; ju57 acceptance |
| WP5: desktop/native UX | Approved screens, worker state, focus/accessibility, Gallery, indicator/FPS/tray and reusable brand assets | Codex | Codex UI/native checks; ju57 design/task acceptance |
| WP6: package, security and signing | Exact dependency provenance, installer lifecycle, final security coverage and signing dossier/pipeline | Codex prepares; ju57 owns external identity/roles/application approval | Independent security review arranged by ju57; ju57 signing/release approval |
| WP7: qualification and external beta | Freeze matrix/plans/hosts, execute mandatory gates, support material and voluntary external test | Codex prepares/runs authorized checks; ju57 allocates hosts/testers | Named per-session controller/validator; ju57 exact-artifact release approval |

No unconfirmed external tester or reviewer is assumed to have agreed to work. Assign the named reviewer and concrete live host/controller before each affected run/change. Until then, the coverage/ownership gate stays open.

## Testing Decisions

### Confirmed boundaries and useful tests

On 25 October the maintainer confirmed testing through the **existing recorder/worker interface**, followed by inspection of **actual saved media**, with focused core checks and separate real-hardware timing/audio/fullscreen qualification.

- Prefer observable operations and outcomes: request replay/manual/screenshot/settings/recovery/exit; inspect returned state, accepted-request identity, final files, retained inputs and real source content. Do not test private call counts or rewrite expected results to match an implementation defect.
- Reuse portable configuration/clock/replay-selection/notification checks for deterministic rules, including queue/session/checkpoint and late-cancel boundaries when implemented. Reuse existing ownership/failure-injection checks rather than exposing test-only production internals.
- Extend existing worker lifecycle, window-capture, migration, audio-clock, frame-lineage and admitted benchmark protocols. UI/native checks exercise focus, accessibility, applied state, reconnect and lifecycle through the same product boundary. New native adapters need independent source/timestamp/media oracles, not readiness flags alone.
- Qualify source workload/mode before attributing failure or performance to capture. Retain exact build/tool/profile/source/driver identities, ordered failed attempts, admission/stop/restoration receipts and media availability. Synthetic, offline, instrumented and ordinary live-product evidence remain distinct.
- Protect the installed recorder, existing media/settings and NVIDIA state. Live collection needs a concrete authorized effects plan and one session controller; do heavy decoding outside timing windows. Use bounded storage/reserves and retain essential rejected evidence. Documentation publication does not start a live run.

### Accepted numerical gates

| Property | Mandatory acceptance |
| --- | --- |
| Request feedback | Truthful accepted/queued/rejected feedback within 200 ms of physical shortcut/button request |
| Reference save speed | Unqueued five-minute 1920×1080, 60 FPS, H.264/20 Mbps/system-audio/local-SSD exports while capture continues: median ≤3 s, p95 ≤5 s, at least 20 matched exports spanning cold/warm conditions |
| Save measurement | Request through valid closed playable final MP4, including tail availability, input protection, export, runtime validation and publication; separately report queue wait and total request-to-file time |
| Replay precision | At most one configured output frame of video-boundary/duration rounding; actual requested source interval, not metadata/padding; test 1/5/30-minute durations and full supported selection range |
| Audio alignment | Each enabled track within 50 ms of video content, with ≤20 ms additional drift over two hours; coded beginning/middle/end markers including cold start/device recovery |
| Gaming overhead | At most 2% average application FPS loss and 10% p99 frame-time increase versus recording disabled, using at least five matched counterbalanced pairs and existing source/observer/lineage/uncertainty gates |
| Reference CPU/RAM | Combined worker/UI/engine/export ≤5% average total-machine CPU; ≤1 GB RAM settings hidden and ≤1.5 GB open; separate capture-only/concurrent-save conditions and report GPU memory |
| Failure/recovery | Visible failure within 2 s; healthy capture within 10 s of the selected source/device returning, respecting pause/manual restart; full replay history still needs refill |
| Stalled Exit | Offer explicit stalled-work choices after 30 s without measurable finalization/export progress, never automatic abandonment |
| Soak | Two uninterrupted hours of manual plus replay with thirty-minute retention and repeated saves; complete valid tracks, bounded resources, synchronization and no unexplained stops; deliberately interrupted recovery trials are separate |

Freeze exact cold/warm definitions, quantile calculation, sample order/windows, RAM accounting/reference hardware, other-profile budgets and evidence/storage plan prospectively before qualification. Research recommendations such as a particular cold/warm split or scratch multiplier are not automatically accepted constants. Preserve failures and report uncertainty; do not move thresholds or drop slow/invalid cases after seeing results. Full-file decode/source-ID/marker qualification is separate from a bounded runtime publication validator, whose scope/cost must be demonstrated inside the save-speed gate.

### Required scenario coverage

- Full/new-footage/warm-up/minimum-duration/expired-history requests; FIFO capacity and storage rejection; failure/cancel predecessors; queued policy/source/destination edits; retirement/UI crash/worker failure; publication/cancel races and valid final-file preservation.
- Concurrent manual/replay/screenshot/save, replay pause/resume, new manual canvases after source resize, no thirty-minute manual cap, session-ending interruption and explicit manual restart.
- Explicit monitor/window identities, minimize/close/handle reuse, monitor loss/return/primary change, rotation/scaling/stretched 4:3 presentation, fixed-canvas resize, genuine fullscreen transitions, clean F10 surfaces and NVIDIA coexistence for each claimed route.
- No audio/either input/both inputs; one/two tracks; mute/disable/permission; same-device return versus new identity/default changes; hold-to-talk/focus/lock/suspend; device-format/clock changes, silence versus corrupt/missing audio and two-hour content synchronization.
- Cache-budget changes, high-demand profiles, same/different volumes, path/permission/quota/full-disk/copy-fallback failures, protected extents, retained recovery, safe stop/resume and destination ownership.
- New setup/draft/retry, existing and partially invalid profile migration, preserved pause/startup, desired versus applied changes, deferred apply after success/failure, crash/power-loss boundaries and rollback compatibility.
- Saved-file library/Recycle Bin confirmation and errors; complete/failed/canceled/partial/disjoint recovery; safe resource priority; Exit idle/pending/stalled/failure/force/return states; passive versus interactive focus and notice dismissal persistence.
- Exact offline ordinary-account install, blocked active-work upgrade, failed install/rollback/uninstall, dependency trust/repair, final security delta/cross-account checks, signing provenance and corresponding source/notices.
- Keyboard-only, screen reader, Windows scaling, maximized/short windows, multi-monitor native placement, configurable/conflicting shortcuts, actual FPS/unavailable measurement and truthful disconnected states across all surfaces.

### Mandatory release-gate register

Every row is open until the exact candidate has its required retained evidence and acceptance. Q158 allows M0 planning closure with this register; it passes none of the rows. Codex owns technical preparation/checks unless otherwise stated; ju57 owns scope/release decisions and arranges required human/independent validation.

| Gate | Owner and validation responsibility | Clear pass condition | Current evidence / unresolved work |
| --- | --- | --- | --- |
| G-CAP: fullscreen and freshness | Codex investigation; ju57 accepts scope/candidate and names validator | Both capture intents record sustained fresh intended pictures in qualified required presentation modes; actual acquisition/source/encoded timing and transitions satisfy the contract | **Blocked/deferred**, [issue #9](https://github.com/ju57/f10-recorder/issues/9). DDA recreation returned stale transition pictures; monitor WGC passed a windowed control but stalled during traditional fullscreen. No backend chosen. |
| G-COEX: clean surfaces/focus | Codex native implementation; ju57 live/design validation | F10 UI/indicator/notices excluded from footage while required fullscreen focus and NVIDIA coexistence work, with real fresh output and restoration evidence | Open. Global capture affinity interrupted NVIDIA Replay in a controlled setup; source acquisition success alone cannot close this gate. Related to #9, not assumed fixed by it. |
| G-REPLAY: exact fast saves and independent manual | Codex engine/media verification; ju57 acceptance | Full/new-footage/FIFO/cancellation/retirement and manual independence pass; actual source boundaries and cold/warm request-to-file targets pass during live capture on exact shipping engine/profile | Open. Controlled indexed-reader/AAC packet-copy experiment met its measured warm five-minute targets; this is feasibility evidence, not live integration or full codec/track/profile acceptance. |
| G-AV: audio/clock/encoding | Codex audio/media checks; ju57 live acceptance | Named inputs, optionality/mix/two-track/silence/mute/hold/reconnection, fixed-canvas concurrency and calibrated 50 ms/20 ms content alignment/drift all pass; sharing/separate encoding is selected from measured isolation/resource results | Open. Endpoint normalization, precise clock mapping and shared-versus-additional encoding choice remain engineering work. |
| G-STORE: storage/recovery integrity | Codex reservation/recovery checks; ju57 acceptance | Calibrated conservative cache/manual/output/finalization claims, safe thresholds/paths/races and complete/partial/cancel/discard recovery pass without destroying protected or saved inputs | Open. Exact burst/scratch margins, supported path/filesystem envelope, recovery scheduling and publication durability are not measured guarantees. |
| G-MIGRATE: apply/upgrade durability | Codex migration/failure-injection checks; ju57 acceptance | Valid preferences/startup/pause/media survive migration and rollback; pending/apply failure/reconnect/active-work upgrade/crash boundaries preserve truthful actual state and accepted work | Open. Schema versions, journals, atomic activation/rollback and complete settings classification need scoped design/validation. |
| G-UX: native accepted experience | Codex UI/native checks; ju57 design/task acceptance | All approved screen/state tasks, library/gallery/delete, notifications, overlay/indicator/tray, safe Exit, accessibility/focus/DPI and actual FPS/unavailable behaviour operate through the real worker | Open. Wireframes/brand/logo are accepted; they do not qualify native APIs, game classification, FPS privileges/overhead or asset scaling. |
| G-MATRIX: plans, resources and soak | Codex test-plan/evidence preparation; ju57 supplies hosts/controllers and names validators | Exact OS/build/GPU/driver/game/source/codec/audio/storage matrix and budgets frozen; every required cell passes numeric/overhead/two-hour/recovery tests with complete evidence and no required failed/not-run cell | Open. At least two NVIDIA generations including AV1 and one hybrid laptop; desktop/laptop cases; Microsoft-supported Windows 11 and Windows 10 22H2 ESU policy. Actual hosts/controllers and ordinary-product evidence remain to be assigned/collected. |
| G-SEC: exact-candidate security | Codex focused checks; independent reviewer arranged by ju57 | Integrated remediation regressions, genuine different-account denial and final-candidate delta coverage pass against exact release revisions | Open. Historical assessments/merges do not cover every future change. |
| G-PKG: trusted offline package | Codex packaging/compliance checks; ju57 acceptance | Exact Inno/runtime/engine inputs/licenses/capabilities pinned; offline per-user install/upgrade/uninstall/rollback pass; complete component/notices/corresponding-source distribution and manifests available | Open. Builder version/license arrangements and shipping engine/source/package lifecycle must be qualified; no mutable latest/unapproved fallback. |
| G-SIGN: signing and provenance | ju57 owns roles/MFA/provider decisions; Codex prepares dossier/pipeline | Approved signing route, required human roles and trustworthy build/signing provenance produce valid signed intended F10 artifacts with correct identity/metadata | Open external dependency. Assess Foundation first; if rejected/unavailable or cost/identity changes are needed, return for explicit fallback/budget decision. No signing approval is assumed. |
| G-BETA: external tasks and publication | ju57 owns tester recruitment/release approval; Codex prepares checklist/support/evidence | Six to ten voluntary testers cover agreed matrix/tasks, blocking feedback resolved, known limits/support/security route/changelog ready, all prior gates pass and ju57 approves exact versioned artifacts | Open. No tester agreement or public-release authorization follows from this spec. |

The OS coverage policy requires checking Microsoft's supported editions/builds again at candidate freeze; it is not a promise that an outdated lifecycle table is current. Required scope cannot be reduced through a missing matrix cell. The accepted wait-for-quality/no-fixed-deadline policy remains.

## Out of Scope

- Linux capture/packaging for this Windows beta, AMD/Intel support expansion, per-application audio isolation, automatic highlights, profiles beyond agreed recording preferences, trimming/full editing, streaming, webcam composition, filters, driver management, broad HDR support and a large performance dashboard.
- Automatic updating, unapproved external engine discovery, silent audio/source substitution, automatic recovery/discard or protected-media expiry.
- Shipping NVIDIA code/brand assets, universal codec/device compatibility, guaranteed Foundation approval or NVIDIA performance parity without separate matched evidence.
- Selecting an unproven capture backend/encoder-sharing design, assigning invented resource constants, running more capture experiments, installing/signing/publishing binaries or implementing the whole beta as part of this documentation task.

## Further Notes

- **Q158 is accepted.** The product/UI interview has no remaining question blocking this consolidation. Publication of this versioned spec and owned gate register closes M0 planning; M1–M5 and all mandatory engineering/release gates remain open. Later evidence can require a concrete scope/design decision, but no accepted requirement is weakened by deferral.
- The primary known capture investigation is [issue #9](https://github.com/ju57/f10-recorder/issues/9). Further live retries are deferred until a new bounded hypothesis and discriminating observation plan exist. Do not repeatedly collect the same stalled/black footage or treat a success flag as progress.
- Proposed research mechanisms remain candidates: indexed reader/AAC packet-copy live integration, capture backend, encoder sharing, normalized audio adapters, durable migration/apply journal, storage estimator and publication validation. Their outcomes must satisfy this contract, with new questions only for real evidenced product trade-offs.
- Planning provenance is the accepted release-plan decision log, glossary, ADRs 0001–0006, approved brand/logo and screen/state review pack. The local supporting records include `docs/release-plan.md`, `docs/brand-guidelines.md`, `docs/m0-review/README.md` and `docs/m0-review/engineering-closeout.md`. They distinguish historical prototype facts, research proposals and later accepted refinements. These source records are not newly measured runtime evidence.
- Exact-machine logs/media/tool binaries, raw vendor extracts and credentials remain outside public commits and this issue. Local reports retain provenance and artifact-retirement availability limits; a retired historical output cannot support a fresh decode claim.
- The spec's ready-for-agent label denotes a sufficiently consolidated work contract for scoped follow-up. It does not clear dependencies, authorize a blind background capture run or certify the product as release-ready. Scoped implementation tickets/branches, reviewers and concrete test plans remain the next execution step.

### Decision coverage index

| Accepted source decisions | Consolidated contract |
| --- | --- |
| Initial six recommendations; Q7–Q21, Q69–Q72, Q74, Q77–Q91 and later setup refinements | Scope/defaults/setup; later microphone-on, optional audio and Finish/Retry refinements supersede earlier defaults/copy |
| Q8–Q9, Q16–Q17, Q22a–Q28, Q42, Q75–Q76, Q98–Q101, Q149 | Replay admission/boundaries/checkpoints/queue, original-request ownership, publication/cancellation and result feedback |
| Q11–Q12, Q29–Q32, Q37, Q41, Q48, Q51–Q53, Q105–Q107, Q152, Q157 | Capture/video/manual/screenshots, source loss, deferred Apply, displayed presentation and fixed session canvas |
| Q10, Q20–Q21, Q27, Q38–Q40, Q54, Q74, Q77, Q88–Q90, Q93, Q108–Q111 | Named audio/permission/reconnection/levels/hold-to-talk and one/two-track clock rules |
| Q33–Q36, Q49–Q50, Q104, Q106a, Q116–Q118, Q147–Q156 | Migration/settings/storage, reservations, Recycle Bin versus recovery discard, low-space resumption and retained recovery |
| Q45–Q47, Q68, Q73 resolved by Q92, Q94–Q103, Q112–Q115, Q119–Q143 | Seven desktop sections, Overview/library/notices, Controls/Diagnostics, overlay/indicator/tray/recovery/Exit flows |
| Q84–Q87; approved logo/name and hourglass refinements; Q129–Q131 follow-ups | Accepted visual/asset system, minimal configurable indicator and actual application FPS |
| Q13–Q14, Q53–Q67, Q138–Q143, Q158 | Numeric evidence, supported matrix, offline signed package, responsibilities, external beta and planning/release separation |
| ADRs 0001–0006; Q9/Q14/Q23/Q25/Q61/Q157 refinements | Account/engine/profile ownership, modern-only product, queued accepted-save lifetimes and displayed presentation |

The numbered interview is historical provenance, not a second competing spec. Where an early row was superseded, the latest accepted behaviour above controls. Any later change requires an explicit recorded decision and corresponding spec version.
