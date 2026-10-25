# First-run setup Taste audit

22 October 2026, Europe/Lisbon. **Judgment: keep the layout and proceed to another focused first-run review. Do not freeze the interaction/error specification yet.** The ready state is coherent; the highest-value next work is to make accepted failures reviewable, retain keyboard focus, and remove the unnecessary responsive height jump.

## Scope, inputs and method

This is an audit of the **first-run product screen inside the HTML review board**, not an implementation review of the production recorder. Q73 and Overview hierarchy remain deferred. Overview was reached solely to verify Finish navigation. No wireframe, product code, recording preferences, startup settings or media were changed. No real capture, audio device, native folder picker, installation, commit or publication was performed.

Used the installed **taste-skill@taste-skill 2.1.4**, its package-relative entrypoint `skills/taste-skill/SKILL.md`, and its `references/app-screens.md`. The lead and all three independent read-only reviewers read both files in full. Review roles were (1) visual hierarchy/layout, (2) interaction/accessibility/scaling, and (3) comprehension/copy/decision fidelity. Their source/supplied-image findings were independently produced; the lead supplied browser verification and deduplicated the results. The original machine-specific audit is retained with local evidence; the published reference omits the user-profile installation prefix.

Applied the app-screen rules: a clear task, restrained app-sized type, meaningful grouping, one primary action, usable controls, honest sample data and reviewable states. Marketing imagery floors, page motion floors, decorative art direction and page scoring caps do not apply. No style was dealt, no seed/redraw exists, and no redesign/fix loop was applied: this was an existing-screen audit only.

**Frozen snapshot:** [manifest and hashes](../../artifacts/m0-review/taste-audit-input/manifest.json), created at `2026-10-22T12:16:40.4985671+01:00`, on `codex/m0-beta-decisions`, HEAD `fd6fb316b102a8686377f841729469ca70435d09`. HEAD alone does not identify the audited design: uncommitted inputs were included. Pre-existing changes in GLOSSARY.md, release-plan.md, ADR 0006 and m0-review were preserved.

| Exact input | SHA-256 of snapshot/source pair |
| --- | --- |
| [wireframes.html](wireframes.html) | `A72C799D74CC87F08DE5AA559A91A29B6599C198D7C7148F18CD9C1810D19896` |
| [app-icon.png](app-icon.png) | `D0CED24E63C4A6FFA532FCE9F0DE967CF96815020170B39F97BCCEDA60271A92` |
| [Review README](README.md) | `718FA3114048F09E7D092C870C753D03CB8F7A406CEC3746EB503B47747F9E85` |
| [release-plan.md](../release-plan.md) | `F303C759F2B5A70ADF54CEA0B85B44D54039DFC8C0A650CF9FC8B91FB463CBA7` |
| [Supplied latest screenshot](../../artifacts/m0-review/setup-microphone-icon-meter.jpg) | `F406DE465335EFB2BD1D0F3D6C0C474519B7FD525A603FE2B94DC819AFEC9300` |

AGENTS.md and the root README were also snapshotted and hashed. Browser `Page.getResourceContent` verified that the HTML and logo served at `http://127.0.0.1:8768/wireframes.html#setup?scenario=ready` matched the above hashes exactly; see [preview verification](../../artifacts/m0-review/taste-audit-input/preview-verification.json). All source line numbers below refer to this frozen HTML or release plan.

## Coverage and limits

- Visually examined the supplied JPG and browser viewport screenshots at **1440×900, 901×900, 900×900, 768×900, 390×844 and 1280×720 CSS pixels**. The 390px check is a stress test, not a request to ship a mobile product. Browser viewport emulation is not Windows DPI or Avalonia validation.
- Exercised the source selector, microphone icon, enabled-microphone Alt+F2 feedback, missing microphone/system sources, explicit continuation without system audio, sample folder dialog/Cancel, empty-folder Finish, every whole-minute value **1 through 30**, and invalid duration **31**. All 30 valid numeric values synchronized the slider and estimate; 31 kept setup open. The sample folder dialog opened with its Folder selector focused and Cancel returned focus to its launching icon.
- Reviewed labels, accessible names, pressed/disabled state, focus CSS and computed visual tokens. Real screen-reader speech, Windows UI Automation, native picker behavior, high contrast, text scaling, long real device names and physical keyboard/game conflicts were not tested. Full keyboard traversal and every combination of draft choices were not exhaustively exercised.
- **Taste lint:** completed in app mode, **0 FAIL / 7 WARN**. The warnings concern the outer board kicker, date/time zero-padding, and other screens' kickers; none establishes a first-run product defect. Raw output: [lint.json](../../artifacts/m0-review/taste-audit-input/lint.json).
- **Taste inspector:** attempted in app mode with outputs redirected to ignored evidence. Its separate Chrome process exited during crash-server startup. Therefore there is **no completed inspector score or pass**. Used the permitted in-app browser, DOM measurements and screenshots instead; retained [failure log](../../artifacts/m0-review/taste-audit-input/inspect.log). A shell attempt to fetch localhost was also denied; browser resource-content hashes supplied the successful identity check.
- Retained initial IAB screenshot attempts as `iab-cropped-*.png`; that screenshot API did not cover the full emulated viewport. They are rejected for overflow assessment. Final `ready-*.png` use explicit CDP viewport clips after scrolling to the top, and were visually inspected. See [screenshot method](../../artifacts/m0-review/taste-audit-input/screenshot-method.txt).

The board's device names, static segmented meters, paths and storage figures are deliberately illustrative. No finding treats them as defective production integrations. Permission/capture/startup behavior is assessed only as represented in the design; the HTML cannot establish hardware or production acceptance.

## Prioritized findings

Priority means urgency for the next design review: **P1** blocks completion of this first-run review; **P2** should be addressed in the next iteration; **P3** is lower priority. These are design-review priorities, not vulnerability or release severity ratings.

### F1 · P1 · Accepted blocking errors and permission recovery are not reviewable

**Confirmed wireframe coverage gap, with a reproduced sample-flow defect.** Q72 accepts inline errors, retained choices, blocked completion and failed-start retry (release plan line 221). Setup markup at HTML lines 86–102 has no folder/source/storage/start-failure error presentation. The folder field at line 94 has no required/error state; Finish at line 125 checks only duration and missing-system-audio acknowledgment. Clearing Save folder and clicking Finish navigated to `#overview?scenario=filling`. No real filesystem operation occurred.

Microphone absence is represented, but permission denial, retained named-device identity and explicit re-enable after restoring permission are not. Those distinctions are accepted in round 13/Q40 (release plan lines 219–220 and 356).

**Impact:** reviewers cannot judge the accepted failure layouts or recovery path, and the sample flow implies that invalid destination choices succeed. This is a gap in the review artifact, not evidence that the production recorder writes to an invalid folder or opens a denied device.

**Remedy:** add safe simulated cases for empty/invalid/unwritable folder, unavailable selected video source, low storage, denied microphone and failed start. Put short errors next to their affected controls, preserve other draft choices, keep setup open and offer an appropriate retry. For denied access, retain the selected microphone name, show an inactive meter and concise restoration guidance, then require explicit re-enable. No actual folder probing or audio access is needed. This implements existing decisions rather than reopening them.

Evidence: [interaction probes](../../artifacts/m0-review/taste-audit-input/interaction-probes.json), HTML lines 94–101/125, release plan lines 219–221. The successful missing-source state is evidence worth retaining, not a substitute for these remaining cases.

### F2 · P2 · Updating setup removes keyboard focus

**Confirmed in browser.** Pressing Space on the microphone icon changed its state but left `document.activeElement` as BODY; the accessibility tree reported focus on the web area. ArrowDown on Capture source changed to Application window and also lost focus. HTML line 112 replaces all stage content, invoked by the microphone action at line 120 and selection handler at line 139. Folder selection also calls render at line 124; its focus outcome was not independently exercised. Cancel does return focus correctly.

**Impact:** repeated keyboard adjustments lose their starting point and require users to find the controls again. A visible focus style cannot help after the focused control has been removed.

**Remedy:** update affected controls in place or restore focus using stable control identifiers after rendering; preserve selection/caret where appropriate. Make folder selection return to its launching icon or destination field. Keep status announcements targeted. The entire stage is a polite live region (line 29), so excessive announcements are a separate risk requiring screen-reader testing, not a measured failure here.

Evidence: [interaction probes](../../artifacts/m0-review/taste-audit-input/interaction-probes.json), HTML lines 112/120/124/139. No accepted layout or icon-only decision needs to change.

### F3 · P2 · One pixel of viewport width adds 341.5px of form height

**Confirmed layout behavior.** The form is 770px wide at both browser widths 901px and 900px. It changes from two columns to one at the global 900px media query (HTML lines 9–10), increasing its own height from **658.5px to 1000px**, despite no loss of usable form width. This pushes startup and Finish much farther down. The causal measurement is inside the product panel, independent of the board toolbar's height.

| Browser viewport | Product panel width × height | Finish top, page at scroll 0 |
| --- | --- | --- |
| 1440×900 | 770 × 658.5px | 782.5px |
| 901×900 | 770 × 658.5px | 814px |
| 900×900 | 770 × 1000px | 1155.5px |
| 768×900 | 729 × 1000px | 1155.5px |
| 390×844 | 351 × 1000px | 1304.5px |
| 1280×720 | 770 × 658.5px | 782.5px |

**Impact:** unnecessary scrolling at desktop-sized effective widths. The 1280×720 board also places Finish below the initial viewport, but its top offset includes review chrome; that alone is not a native-window defect. A roughly 770px native window would inherit the one-column behavior if the viewport rule were transferred literally.

**Remedy:** decide the setup breakpoint from the actual form width and minimum useful field widths, using a container-aware layout or equivalent native rules. Validate the product panel separately at short working-area heights, with scrolling that always reaches Finish. Do not shrink text or remove required choices just to fit the board toolbar.

Evidence: [layout metrics](../../artifacts/m0-review/taste-audit-input/layout-metrics.json), [901px](../../artifacts/m0-review/taste-audit-input/ready-901.png) versus [900px](../../artifacts/m0-review/taste-audit-input/ready-900.png), [short viewport](../../artifacts/m0-review/taste-audit-input/ready-1280x720.png). Windows DPI, native minimum size and resizing remain untested.

### F4 · P2 · Setup microphone feedback contradicts its enabled state

**Confirmed sample feedback defect; setup mute semantics need clarification.** With the microphone enabled, Alt+F2 displays “Enable the microphone using its icon first.” The icon still has accessible name “Disable microphone” and pressed state true. This follows line 140, which always gives that message on the setup route. Q74 names Enable/Mute/Unmute and Alt+F2 for enabled microphones (release plan line 209); setup instead toggles enabled/off (HTML lines 54/120).

**Impact:** the board teaches conflicting microphone behavior and gives an impossible instruction to an enabled user.

**Remedy:** keep the single icon, tooltip, accessible state and accepted default. Give truthful shortcut feedback. Clarify whether setup intentionally provides enable/disable while the running app provides mute/unmute; document that scope if intentional, or represent Q74 consistently. This is an unresolved interpretation of existing decisions, not permission to reverse the default or add visible On/Off labels.

Evidence: [shortcut feedback screenshot](../../artifacts/m0-review/taste-audit-input/shortcut-feedback.png), [probes](../../artifacts/m0-review/taste-audit-input/interaction-probes.json). Native Alt+F2 registration was not tested.

### F5 · P2 · Display-capture scope is left implicit

**Confirmed missing explanation; comprehension impact is a risk to validate.** The screen says Whole display but does not state that other visible applications are included (HTML lines 90–101 and supplied screenshot). The accepted capture promise explicitly calls for this explanation (release plan line 81).

**Impact:** a person expecting game-only recording may select a broader source than intended. No user misunderstanding was measured in this audit.

**Remedy:** add one conditional line near the source, for example “Records everything visible on this display.” For selected-window mode, concisely explain its scope and that an unavailable window will not switch to desktop capture. Keep this near the source rather than restoring the removed Finish footer. This fulfills an accepted requirement.

### F6 · P3 · Normal meters use the warning colour

**Measured colour choice; interpretation risk.** Both ordinary sample levels use `--warn: #e6bb75` (HTML lines 8/14/95–96), producing the largest repeated amber marks on the form. The root README reserves amber for paused/recovering states and also uses it for startup/conflict.

**Impact:** ordinary sound activity may look cautionary before any problem exists. The audit did not measure that interpretation or real meter calibration.

**Remedy:** preserve the approved segmented shape and placement; establish a normal-signal token and reserve amber for a defined caution/level threshold. This is a semantic refinement, not a reason to remove the meters.

### F7 · P3 · Control boundaries deserve low-vision validation

**Computed styling; practical impact untested.** Rendered styles confirm border `#405047`, input `#29312c`, setup ground `#171b19`. Relative-luminance calculations give border/ground approximately **2.03:1**, border/input **1.56:1**, and input/ground **1.30:1**. Text is strong: primary/ground **15.56:1**, secondary/ground **9.13:1**, input text/fill **11.97:1**.

**Impact:** control perimeters may be difficult to locate for some users even though the labels and values remain readable.

**Remedy:** test a slightly brighter interactive boundary while keeping structural borders quiet; verify hover, focus, disabled states and Windows high contrast in the eventual native UI. These ratios are not an accessibility certification or a blanket assertion of failure.

Evidence: HTML lines 8–9, [computed styles](../../artifacts/m0-review/taste-audit-input/computed-styles.json), [desktop screenshot](../../artifacts/m0-review/taste-audit-input/ready-1440.png).

## Separate documentation consistency issue

**D1 · P2:** current audio requirements at release-plan.md lines 351 and 353 still state that the microphone defaults off. Round 13 at line 219 and the first-run summary at line 379 say enabled when available/permitted. Reconcile the current requirements before implementation. Earlier chronological decisions can remain as history with supersession indicated. The latest default is already accepted; this is not a proposed product reversal. No release-plan edits were made in this audit.

## Strengths to retain

- Recognizable existing logo, concise noun title at 25px and app-sized Segoe UI typography. No decorative imagery or marketing header is needed.
- Balanced desktop columns with consistent edges: 338 + 24 + 338 = 700px of inner form width. Capture/display and the two named audio devices form useful pairs. The inset folder action is clear.
- One differentiated Finish action; no competing secondary completion choice. Keep the accepted removal of its explanatory footer.
- Whole display/primary display, five minutes, mixed track, enabled available microphone and checked startup match the latest first-run decisions. Existing-profile preservation remains a separate implementation obligation.
- Linked duration controls genuinely cover all 30 whole-minute values. [Checks](../../artifacts/m0-review/taste-audit-input/duration-values.json) recorded the synchronized values and estimates; invalid 31 did not complete setup.
- Explicit labels, accessible icon names/tooltips, microphone pressed/slash states, a visible 2px focus rule, and a named sample modal provide a useful accessibility foundation. The icon is not differentiated by colour alone.
- Missing sources show inactive meters; absent microphone disables the icon and shows Unavailable. Missing system audio blocks Finish until explicit continuation. Restoring the sample microphone leaves it off until enabled. Those browser-observed states are consistent with the intended protective behavior.
- Concise product copy without em dashes, flat quiet surfaces and relevant feedback. Sample-only meters and device names are appropriate for this review stage.

## Optional improvements and decisions that would need reopening

1. Move the compact replay/cache estimate closer to duration and folder. It currently follows the audio/video row (HTML line 100), separated from the settings that explain it. Consider “Estimated replay cache” if that accurately describes the eventual storage model. This is optional hierarchy/copy work, not a blocker.
2. When two tracks is selected, consider a short conditional helper about editing/playback, such as “Separate tracks for editing; some players play one source.” The current option correctly names exactly two sources. Player behavior still needs validation, and no third compatibility mix is proposed.
3. Conduct a brief first-time-reader check: what is captured, whether meters already listen, whether anything is saved during setup, what Finish starts, and what startup means. The audit cannot establish comprehension from screenshots alone.
4. **Reopening round 13 would be required** to reverse microphone-on by default, restore visible On/Off text, rename Finish, or restore its removed explanatory footer. None is recommended automatically here. If comprehension tests expose a problem, present evidence and alternatives explicitly for that product decision. Source-scope guidance in F5 can be added without reversing these choices.

## Reviewer agreement and differences

All three reviewers found the desktop ready state coherent and favored retaining its structure. Interaction and comprehension reviewers independently identified the unrepresented error/permission states. The visual reviewer prioritized the breakpoint and meter semantics; browser measurements promoted the breakpoint from a predicted risk to confirmed behavior. The lead also confirmed focus loss, enabled-microphone shortcut feedback and empty-folder completion.

The comprehension reviewer rated missing error coverage P1; the interaction reviewer rated it P2. This synthesis uses **P1 only as a blocker to completing the first-run design review**, not as a production defect rating. The visual reviewer was comfortable proceeding with layout review; the others required interaction/state work before freezing it. Those judgments are compatible with the readiness decision below. Meter colour, contrast boundaries and estimate placement remain risks/options rather than universally proven usability failures. The setup enable/disable versus running mute/unmute distinction remains open for clarification.

## Next-review readiness

**Ready for another first-run wireframe review; not ready for final first-run sign-off or production acceptance.** Preserve the visual foundation. The three changes with the greatest value are:

1. Represent Q72 and permission/start-failure cases safely, with inline errors and retained choices.
2. Preserve keyboard focus and resolve contradictory microphone shortcut feedback.
3. Base the setup layout on available form width and review short-height behavior separately from board chrome.

The next review should walk those cases plus normal activation using the agreed defaults, then assess source-scope comprehension. Q73 and Overview hierarchy stay deferred. No conclusion here qualifies real audio acquisition, permission restoration, replay activation, persistence, startup registration, Windows scaling, hardware support or capture acceptance.
