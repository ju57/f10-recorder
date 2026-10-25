# F10 beta implementation tickets

Approved and published 25 October 2026. Parent: [consolidated spec #10](https://github.com/ju57/f10-recorder/issues/10). M0 planning is complete; all12 mandatory engineering/release gates remain open until their own evidence passes. Approval: “Approve and publish as drafted.”

## Published ticket graph

Each numbered item links to the published ticket with acceptance criteria, owner/validator responsibility and native prerequisites. T01-T48 are review IDs, not GitHub issue numbers. Parent title/body/labels/state are preserved. Existing [investigation #9](https://github.com/ju57/f10-recorder/issues/9) is reused and remains deferred/open.

1. **[T01: Reject untrusted engines and expose explicit verified repair](https://github.com/ju57/f10-recorder/issues/11)**

   **Blocked by:** None.

   **Delivers:** Users can see which qualified engine capabilities are available, and recover an unavailable or rejected managed engine without an unapproved executable being run.

2. **[T02: Migrate profiles without changing recording intent](https://github.com/ju57/f10-recorder/issues/12)**

   **Blocked by:** None.

   **Delivers:** Existing users reopen Overview with valid preferences intact; only necessary corrections are requested and migration never starts capture by itself.

3. **[T03: Enforce replay-cache budgets and safe storage admission](https://github.com/ju57/f10-recorder/issues/13)**

   **Blocked by:** [#11](https://github.com/ju57/f10-recorder/issues/11), [#12](https://github.com/ju57/f10-recorder/issues/12).

   **Delivers:** Storage settings show usable cache estimates and prevent unsafe capture/save admission without deleting protected footage.

4. **[T04: Save precise full replays and honest warm-up clips](https://github.com/ju57/f10-recorder/issues/14)**

   **Blocked by:** [#11](https://github.com/ju57/f10-recorder/issues/11), [#13](https://github.com/ju57/f10-recorder/issues/13).

   **Delivers:** A full replay contains the requested available interval ending at the request, while rolling capture continues and completed files are published truthfully.

5. **[T05: Queue replay presses with frozen request identity](https://github.com/ju57/f10-recorder/issues/15)**

   **Blocked by:** [#14](https://github.com/ju57/f10-recorder/issues/14).

   **Delivers:** Rapid presses create an ordered bounded queue, and each accepted replay keeps its original request time, source, policy and destination.

6. **[T06: Save only new footage from successful session checkpoints](https://github.com/ju57/f10-recorder/issues/16)**

   **Blocked by:** [#15](https://github.com/ju57/f10-recorder/issues/15).

   **Delivers:** Users can switch to Only new footage and receive the actual unsaved interval, including after a queued predecessor fails.

7. **[T07: Cancel replay work safely across publication and retirement](https://github.com/ju57/f10-recorder/issues/17)**

   **Blocked by:** [#16](https://github.com/ju57/f10-recorder/issues/16).

   **Delivers:** Users can cancel queued or unfinished exports without losing another request’s footage or deleting an already saved file.

8. **[T08: Record the selected monitor with truthful loss and recovery](https://github.com/ju57/f10-recorder/issues/18)**

   **Blocked by:** [#9](https://github.com/ju57/f10-recorder/issues/9), [#14](https://github.com/ju57/f10-recorder/issues/14).

   **Delivers:** Whole-display recording stays on the selected physical monitor, continues across application changes and reports actual source loss/recovery.

9. **[T09: Record only the selected application and preserve privacy on loss](https://github.com/ju57/f10-recorder/issues/19)**

   **Blocked by:** [#9](https://github.com/ju57/f10-recorder/issues/9), [#14](https://github.com/ju57/f10-recorder/issues/14).

   **Delivers:** Selected-window recording never broadens to desktop capture when the original target minimizes, closes or becomes invalid.

10. **[T10: Match displayed presentation in a fixed recording canvas](https://github.com/ju57/f10-recorder/issues/20)**

   **Blocked by:** [#18](https://github.com/ju57/f10-recorder/issues/18), [#19](https://github.com/ju57/f10-recorder/issues/19).

   **Delivers:** Capture & Video settings produce the displayed stretching/borders at the selected quality while each active recording retains its initial canvas.

11. **[T11: Capture source-resolution PNG screenshots without F10 UI](https://github.com/ju57/f10-recorder/issues/21)**

   **Blocked by:** [#20](https://github.com/ju57/f10-recorder/issues/20).

   **Delivers:** Screenshot actions save an original-source-resolution PNG even when video is downscaled, without changing replay coverage.

12. **[T12: Capture the explicitly selected playback device](https://github.com/ju57/f10-recorder/issues/22)**

   **Blocked by:** [#11](https://github.com/ju57/f10-recorder/issues/11), [#12](https://github.com/ju57/f10-recorder/issues/12).

   **Delivers:** The Audio page selects a named system-audio endpoint whose sound reaches recordings on the common clock, independently of Windows default changes.

13. **[T13: Record an optional microphone with explicit enable and mute](https://github.com/ju57/f10-recorder/issues/23)**

   **Blocked by:** [#22](https://github.com/ju57/f10-recorder/issues/22).

   **Delivers:** Users select a microphone, verify its meter and enable/mute it with one consistent icon without permission surprises.

14. **[T14: Choose one mixed or exactly two ordered audio tracks](https://github.com/ju57/f10-recorder/issues/24)**

   **Blocked by:** [#23](https://github.com/ju57/f10-recorder/issues/23).

   **Delivers:** Audio tracks selection creates either one playable mix or exactly system-audio track one plus microphone track two.

15. **[T15: Apply live recorder levels and safe hold-to-talk](https://github.com/ju57/f10-recorder/issues/25)**

   **Blocked by:** [#24](https://github.com/ju57/f10-recorder/issues/24).

   **Delivers:** Users adjust per-source recorder volume and optional hold-to-talk without restarting replay or altering Windows device volume.

16. **[T16: Recover the same audio device without clock substitution](https://github.com/ju57/f10-recorder/issues/26)**

   **Blocked by:** [#24](https://github.com/ju57/f10-recorder/issues/24), [#25](https://github.com/ju57/f10-recorder/issues/25).

   **Delivers:** A missing selected audio input produces silence/warning and recovers the same device with original intent and trustworthy timing.

17. **[T17: Select a bounded manual and replay encoding strategy](https://github.com/ju57/f10-recorder/issues/27)**

   **Blocked by:** [#14](https://github.com/ju57/f10-recorder/issues/14), [#24](https://github.com/ju57/f10-recorder/issues/24).

   **Delivers:** A finite candidate comparison produces real manual/replay files and a justified strategy for independent consumers before production manual integration.

18. **[T18: Start and stop manual recording independently of replay](https://github.com/ju57/f10-recorder/issues/28)**

   **Blocked by:** [#27](https://github.com/ju57/f10-recorder/issues/27).

   **Delivers:** A manual Start/Stop operation records a whole session independently, with actual worker state and a usable final file.

19. **[T19: Apply pending settings once recording resources safely release](https://github.com/ju57/f10-recorder/issues/29)**

   **Blocked by:** [#28](https://github.com/ju57/f10-recorder/issues/28), [#26](https://github.com/ju57/f10-recorder/issues/26).

   **Delivers:** Users save settings once, see desired versus actual state, and get safe automatic application after manual finalization succeeds or fails.

20. **[T20: Recover or explicitly discard one unfinished recording](https://github.com/ju57/f10-recorder/issues/30)**

   **Blocked by:** [#28](https://github.com/ju57/f10-recorder/issues/28).

   **Delivers:** Captures exposes retained unfinished recordings with real Recover/Discard outcomes and a Back route that does not delete work.

21. **[T21: Cancel recovery safely and give active recording priority](https://github.com/ju57/f10-recorder/issues/31)**

   **Blocked by:** [#30](https://github.com/ju57/f10-recorder/issues/30), [#17](https://github.com/ju57/f10-recorder/issues/17).

   **Delivers:** Recovery can wait or be canceled without disrupting current recording, accepted replay saves or retained originals.

22. **[T22: Recover partial and disjoint intervals honestly](https://github.com/ju57/f10-recorder/issues/32)**

   **Blocked by:** [#31](https://github.com/ju57/f10-recorder/issues/31).

   **Delivers:** Damaged recordings can yield clearly partial playable results with visible missing intervals while preserving originals.

23. **[T23: Drain recording work through safe Exit and stalled-work choices](https://github.com/ju57/f10-recorder/issues/33)**

   **Blocked by:** [#31](https://github.com/ju57/f10-recorder/issues/31).

   **Delivers:** Exit stops acquisition, finishes accepted work and presents safe return/wait/force choices without silently abandoning recordings.

24. **[T24: Complete first-run setup and the compact branded Overview](https://github.com/ju57/f10-recorder/issues/34)**

   **Blocked by:** [#28](https://github.com/ju57/f10-recorder/issues/28).

   **Delivers:** New users finish the accepted compact setup and see a real Overview, while existing users retain their preferences and open Overview directly.

25. **[T25: Unify request, result and problem notices without losing history](https://github.com/ju57/f10-recorder/issues/35)**

   **Blocked by:** [#34](https://github.com/ju57/f10-recorder/issues/34), [#17](https://github.com/ju57/f10-recorder/issues/17).

   **Delivers:** Users receive truthful shared notices for requests/results/problems with independent dismissal and discoverable history.

26. **[T26: Manage saved recordings and screenshots in a scrollable library](https://github.com/ju57/f10-recorder/issues/36)**

   **Blocked by:** [#34](https://github.com/ju57/f10-recorder/issues/34), [#17](https://github.com/ju57/f10-recorder/issues/17), [#30](https://github.com/ju57/f10-recorder/issues/30).

   **Delivers:** Captures shows actual jobs/results and thumbnail media tabs, supporting safe multi-selection and recoverable deletion.

27. **[T27: Expose current diagnostics and an inspectable redacted report](https://github.com/ju57/f10-recorder/issues/37)**

   **Blocked by:** [#34](https://github.com/ju57/f10-recorder/issues/34), [#29](https://github.com/ju57/f10-recorder/issues/29), [#30](https://github.com/ju57/f10-recorder/issues/30), [#15](https://github.com/ju57/f10-recorder/issues/15).

   **Delivers:** Diagnostics distinguishes current problems from history and exports only the user-inspected redacted snapshot.

28. **[T28: Configure conflict-safe shortcuts and stateful microphone controls](https://github.com/ju57/f10-recorder/issues/38)**

   **Blocked by:** [#34](https://github.com/ju57/f10-recorder/issues/34), [#29](https://github.com/ju57/f10-recorder/issues/29).

   **Delivers:** Controls captures actual key combinations and applies working shortcuts without replay restart or unintended microphone enabling.

29. **[T29: Operate recording from the accepted native tray menu](https://github.com/ju57/f10-recorder/issues/39)**

   **Blocked by:** [#38](https://github.com/ju57/f10-recorder/issues/38).

   **Delivers:** The native tray opens/restores Overview and offers current recording/navigation/Exit actions with the same worker semantics.

30. **[T30: Use an edge overlay for quick recording controls and settings](https://github.com/ju57/f10-recorder/issues/40)**

   **Blocked by:** [#38](https://github.com/ju57/f10-recorder/issues/38).

   **Delivers:** A narrow native left/right overlay controls F10 over desktop applications or games while leaving the background visible.

31. **[T31: Preview and manage actual media inside the overlay Gallery](https://github.com/ju57/f10-recorder/issues/41)**

   **Blocked by:** [#40](https://github.com/ju57/f10-recorder/issues/40), [#36](https://github.com/ju57/f10-recorder/issues/36).

   **Delivers:** Overlay Gallery lists real recording/screenshot thumbnails and provides usable adjacent media inspection without leaving the overlay.

32. **[T32: Configure a minimal passive recording indicator](https://github.com/ju57/f10-recorder/issues/42)**

   **Blocked by:** [#34](https://github.com/ju57/f10-recorder/issues/34), [#29](https://github.com/ju57/f10-recorder/issues/29).

   **Delivers:** Users choose a minimal indicator’s content, size and snapped corner, with default game/fullscreen visibility and optional Sticky behaviour.

33. **[T33: Show measured application FPS or truthful unavailability](https://github.com/ju57/f10-recorder/issues/43)**

   **Blocked by:** [#42](https://github.com/ju57/f10-recorder/issues/42).

   **Delivers:** The optional indicator FPS component measures the actual application and can be the only displayed content.

34. **[T34: Qualify clean F10 surfaces and NVIDIA coexistence](https://github.com/ju57/f10-recorder/issues/44)**

   **Blocked by:** [#21](https://github.com/ju57/f10-recorder/issues/21), [#35](https://github.com/ju57/f10-recorder/issues/35), [#40](https://github.com/ju57/f10-recorder/issues/40), [#42](https://github.com/ju57/f10-recorder/issues/42).

   **Delivers:** Supported F10 UI/overlay/indicator/notices stay absent from intended footage without disrupting the required native focus and NVIDIA Replay behaviour.

35. **[T35: Validate keyboard, accessibility and DPI across native flows](https://github.com/ju57/f10-recorder/issues/45)**

   **Blocked by:** [#32](https://github.com/ju57/f10-recorder/issues/32), [#33](https://github.com/ju57/f10-recorder/issues/33), [#37](https://github.com/ju57/f10-recorder/issues/37), [#39](https://github.com/ju57/f10-recorder/issues/39), [#41](https://github.com/ju57/f10-recorder/issues/41), [#43](https://github.com/ju57/f10-recorder/issues/43).

   **Delivers:** All accepted native flows remain legible and operable with keyboard, assistive naming, scaling and constrained window size.

36. **[T36: Install a verified self-contained development package offline](https://github.com/ju57/f10-recorder/issues/46)**

   **Blocked by:** [#34](https://github.com/ju57/f10-recorder/issues/34).

   **Delivers:** An ordinary user can install a development package containing the maintained app, runtime and approved engine without dependency downloads.

37. **[T37: Upgrade and roll back without ending active recordings](https://github.com/ju57/f10-recorder/issues/47)**

   **Blocked by:** [#46](https://github.com/ju57/f10-recorder/issues/46), [#29](https://github.com/ju57/f10-recorder/issues/29), [#33](https://github.com/ju57/f10-recorder/issues/33), [#32](https://github.com/ju57/f10-recorder/issues/32).

   **Delivers:** The installer waits/cancels safely around active work and can roll back a failed version while preserving preferences and all user media.

38. **[T38: Prepare the SignPath Foundation approval dossier](https://github.com/ju57/f10-recorder/issues/48)**

   **Blocked by:** None.

   **Delivers:** ju57 receives a concrete signing-readiness dossier and role/provenance plan for the preferred free Foundation route.

39. **[T39: Package exact dependency notices and corresponding source](https://github.com/ju57/f10-recorder/issues/49)**

   **Blocked by:** [#46](https://github.com/ju57/f10-recorder/issues/46).

   **Delivers:** Every packaged dependency has exact provenance, required notices and usable matching source distribution alongside the intended release materials.

40. **[T40: Approve signing and establish a verified artifact pipeline](https://github.com/ju57/f10-recorder/issues/50)**

   **Blocked by:** [#48](https://github.com/ju57/f10-recorder/issues/48), [#49](https://github.com/ju57/f10-recorder/issues/49).

   **Delivers:** An approved signing route produces verifiably signed intended F10 development artifacts through the agreed provenance and human approval process.

41. **[T41: Verify recorder security boundaries with genuine separate accounts](https://github.com/ju57/f10-recorder/issues/51)**

   **Blocked by:** [#11](https://github.com/ju57/f10-recorder/issues/11), [#12](https://github.com/ju57/f10-recorder/issues/12).

   **Delivers:** The current-user recorder/engine/audio/profile boundaries have real account-separation evidence rather than only synthetic checks.

42. **[T42: Freeze qualification plans, coverage and actual host ownership](https://github.com/ju57/f10-recorder/issues/52)**

   **Blocked by:** None.

   **Delivers:** Required beta claims have executable prospective evidence plans, actual host/controller allocation and budgets before expensive trials begin.

43. **[T43: Qualify precise live replay saves and reference export latency](https://github.com/ju57/f10-recorder/issues/53)**

   **Blocked by:** [#17](https://github.com/ju57/f10-recorder/issues/17), [#20](https://github.com/ju57/f10-recorder/issues/20), [#26](https://github.com/ju57/f10-recorder/issues/26), [#28](https://github.com/ju57/f10-recorder/issues/28), [#52](https://github.com/ju57/f10-recorder/issues/52).

   **Delivers:** The integrated product demonstrates exact requested footage and the agreed five-minute save speed while real capture continues.

44. **[T44: Qualify two-hour manual plus replay and separate recovery trials](https://github.com/ju57/f10-recorder/issues/54)**

   **Blocked by:** [#29](https://github.com/ju57/f10-recorder/issues/29), [#32](https://github.com/ju57/f10-recorder/issues/32), [#33](https://github.com/ju57/f10-recorder/issues/33), [#20](https://github.com/ju57/f10-recorder/issues/20), [#52](https://github.com/ju57/f10-recorder/issues/52).

   **Delivers:** Long manual/replay operation and intentionally interrupted recovery have separate verified media, synchronization and resource evidence.

45. **[T45: Qualify gaming overhead and combined recorder resource budgets](https://github.com/ju57/f10-recorder/issues/55)**

   **Blocked by:** [#44](https://github.com/ju57/f10-recorder/issues/44), [#45](https://github.com/ju57/f10-recorder/issues/45), [#53](https://github.com/ju57/f10-recorder/issues/53).

   **Delivers:** Supported recording profiles have matched ordinary-product gaming/CPU/RAM evidence, including concurrent saves and optional FPS/HUD cost.

46. **[T46: Freeze and independently qualify the exact release candidate](https://github.com/ju57/f10-recorder/issues/56)**

   **Blocked by:** [#47](https://github.com/ju57/f10-recorder/issues/47), [#50](https://github.com/ju57/f10-recorder/issues/50), [#51](https://github.com/ju57/f10-recorder/issues/51), [#54](https://github.com/ju57/f10-recorder/issues/54), [#55](https://github.com/ju57/f10-recorder/issues/55).

   **Delivers:** One exact signed candidate has a complete auditable mandatory-gate evidence index and no required failed or not-run supported cell.

47. **[T47: Run voluntary external-beta tasks and finish support materials](https://github.com/ju57/f10-recorder/issues/57)**

   **Blocked by:** [#56](https://github.com/ju57/f10-recorder/issues/56).

   **Delivers:** A small consenting external group validates the qualified candidate and receives clear compatibility/support guidance before wider release.

48. **[T48: Approve and publish the exact qualified public beta](https://github.com/ju57/f10-recorder/issues/58)**

   **Blocked by:** [#57](https://github.com/ju57/f10-recorder/issues/57).

   **Delivers:** ju57 makes an explicit release decision for the exact fully qualified signed artifacts and publishes a labelled GitHub pre-release only after approval.

## Starting frontier

Immediate independent starting points: [T01 / #11](https://github.com/ju57/f10-recorder/issues/11), [T02 / #12](https://github.com/ju57/f10-recorder/issues/12), [T38 / #48](https://github.com/ju57/f10-recorder/issues/48), [T42 / #52](https://github.com/ju57/f10-recorder/issues/52). These are starting prerequisites, not authorization to run undeclared live tests.

T40 signing approval/pipeline and T48 exact-artifact release are labelled ready-for-human; the other new tickets are ready-for-agent with their native blockers retained. No external role, host or tester is assumed to have agreed.

No implementation, installation, signing request or release was performed by ticket publication.

## Mandatory release-gate coverage

| Gate (mandatory) | Published contributing tickets |
| --- | --- |
| G-AV | [T12 / #22](https://github.com/ju57/f10-recorder/issues/22), [T13 / #23](https://github.com/ju57/f10-recorder/issues/23), [T14 / #24](https://github.com/ju57/f10-recorder/issues/24), [T15 / #25](https://github.com/ju57/f10-recorder/issues/25), [T16 / #26](https://github.com/ju57/f10-recorder/issues/26), [T17 / #27](https://github.com/ju57/f10-recorder/issues/27), [T18 / #28](https://github.com/ju57/f10-recorder/issues/28), [T44 / #54](https://github.com/ju57/f10-recorder/issues/54), [T46 / #56](https://github.com/ju57/f10-recorder/issues/56) |
| G-BETA | [T47 / #57](https://github.com/ju57/f10-recorder/issues/57), [T48 / #58](https://github.com/ju57/f10-recorder/issues/58) |
| G-CAP | [T08 / #18](https://github.com/ju57/f10-recorder/issues/18), [T09 / #19](https://github.com/ju57/f10-recorder/issues/19), [T10 / #20](https://github.com/ju57/f10-recorder/issues/20), [T11 / #21](https://github.com/ju57/f10-recorder/issues/21), [T46 / #56](https://github.com/ju57/f10-recorder/issues/56) |
| G-COEX | [T34 / #44](https://github.com/ju57/f10-recorder/issues/44), [T46 / #56](https://github.com/ju57/f10-recorder/issues/56) |
| G-MATRIX | [T33 / #43](https://github.com/ju57/f10-recorder/issues/43), [T42 / #52](https://github.com/ju57/f10-recorder/issues/52), [T43 / #53](https://github.com/ju57/f10-recorder/issues/53), [T44 / #54](https://github.com/ju57/f10-recorder/issues/54), [T45 / #55](https://github.com/ju57/f10-recorder/issues/55), [T46 / #56](https://github.com/ju57/f10-recorder/issues/56) |
| G-MIGRATE | [T02 / #12](https://github.com/ju57/f10-recorder/issues/12), [T19 / #29](https://github.com/ju57/f10-recorder/issues/29), [T24 / #34](https://github.com/ju57/f10-recorder/issues/34), [T37 / #47](https://github.com/ju57/f10-recorder/issues/47), [T46 / #56](https://github.com/ju57/f10-recorder/issues/56) |
| G-PKG | [T01 / #11](https://github.com/ju57/f10-recorder/issues/11), [T36 / #46](https://github.com/ju57/f10-recorder/issues/46), [T37 / #47](https://github.com/ju57/f10-recorder/issues/47), [T39 / #49](https://github.com/ju57/f10-recorder/issues/49), [T46 / #56](https://github.com/ju57/f10-recorder/issues/56) |
| G-REPLAY | [T04 / #14](https://github.com/ju57/f10-recorder/issues/14), [T05 / #15](https://github.com/ju57/f10-recorder/issues/15), [T06 / #16](https://github.com/ju57/f10-recorder/issues/16), [T07 / #17](https://github.com/ju57/f10-recorder/issues/17), [T17 / #27](https://github.com/ju57/f10-recorder/issues/27), [T18 / #28](https://github.com/ju57/f10-recorder/issues/28), [T23 / #33](https://github.com/ju57/f10-recorder/issues/33), [T43 / #53](https://github.com/ju57/f10-recorder/issues/53), [T46 / #56](https://github.com/ju57/f10-recorder/issues/56) |
| G-SEC | [T01 / #11](https://github.com/ju57/f10-recorder/issues/11), [T02 / #12](https://github.com/ju57/f10-recorder/issues/12), [T09 / #19](https://github.com/ju57/f10-recorder/issues/19), [T27 / #37](https://github.com/ju57/f10-recorder/issues/37), [T41 / #51](https://github.com/ju57/f10-recorder/issues/51), [T46 / #56](https://github.com/ju57/f10-recorder/issues/56) |
| G-SIGN | [T38 / #48](https://github.com/ju57/f10-recorder/issues/48), [T40 / #50](https://github.com/ju57/f10-recorder/issues/50), [T46 / #56](https://github.com/ju57/f10-recorder/issues/56) |
| G-STORE | [T03 / #13](https://github.com/ju57/f10-recorder/issues/13), [T05 / #15](https://github.com/ju57/f10-recorder/issues/15), [T07 / #17](https://github.com/ju57/f10-recorder/issues/17), [T18 / #28](https://github.com/ju57/f10-recorder/issues/28), [T19 / #29](https://github.com/ju57/f10-recorder/issues/29), [T20 / #30](https://github.com/ju57/f10-recorder/issues/30), [T21 / #31](https://github.com/ju57/f10-recorder/issues/31), [T22 / #32](https://github.com/ju57/f10-recorder/issues/32), [T23 / #33](https://github.com/ju57/f10-recorder/issues/33), [T26 / #36](https://github.com/ju57/f10-recorder/issues/36), [T31 / #41](https://github.com/ju57/f10-recorder/issues/41), [T44 / #54](https://github.com/ju57/f10-recorder/issues/54), [T46 / #56](https://github.com/ju57/f10-recorder/issues/56) |
| G-UX | [T10 / #20](https://github.com/ju57/f10-recorder/issues/20), [T11 / #21](https://github.com/ju57/f10-recorder/issues/21), [T13 / #23](https://github.com/ju57/f10-recorder/issues/23), [T15 / #25](https://github.com/ju57/f10-recorder/issues/25), [T19 / #29](https://github.com/ju57/f10-recorder/issues/29), [T20 / #30](https://github.com/ju57/f10-recorder/issues/30), [T21 / #31](https://github.com/ju57/f10-recorder/issues/31), [T22 / #32](https://github.com/ju57/f10-recorder/issues/32), [T23 / #33](https://github.com/ju57/f10-recorder/issues/33), [T24 / #34](https://github.com/ju57/f10-recorder/issues/34), [T25 / #35](https://github.com/ju57/f10-recorder/issues/35), [T26 / #36](https://github.com/ju57/f10-recorder/issues/36), [T27 / #37](https://github.com/ju57/f10-recorder/issues/37), [T28 / #38](https://github.com/ju57/f10-recorder/issues/38), [T29 / #39](https://github.com/ju57/f10-recorder/issues/39), [T30 / #40](https://github.com/ju57/f10-recorder/issues/40), [T31 / #41](https://github.com/ju57/f10-recorder/issues/41), [T32 / #42](https://github.com/ju57/f10-recorder/issues/42), [T33 / #43](https://github.com/ju57/f10-recorder/issues/43), [T35 / #45](https://github.com/ju57/f10-recorder/issues/45), [T46 / #56](https://github.com/ju57/f10-recorder/issues/56) |

Closing a feature ticket does not by itself close a release gate. T46 aggregates candidate-specific technical qualification, T47 external-beta evidence and T48 ju57 approval of exact artifacts. No unresolved engineering requirement is removed or weakened.

## User-story coverage

| Spec story | Published ticket(s) |
| --- | --- |
| 1 | [#34](https://github.com/ju57/f10-recorder/issues/34) |
| 2 | [#34](https://github.com/ju57/f10-recorder/issues/34) |
| 3 | [#34](https://github.com/ju57/f10-recorder/issues/34) |
| 4 | [#18](https://github.com/ju57/f10-recorder/issues/18), [#19](https://github.com/ju57/f10-recorder/issues/19) |
| 5 | [#19](https://github.com/ju57/f10-recorder/issues/19) |
| 6 | [#18](https://github.com/ju57/f10-recorder/issues/18) |
| 7 | [#18](https://github.com/ju57/f10-recorder/issues/18), [#19](https://github.com/ju57/f10-recorder/issues/19), [#44](https://github.com/ju57/f10-recorder/issues/44) |
| 8 | [#44](https://github.com/ju57/f10-recorder/issues/44) |
| 9 | [#18](https://github.com/ju57/f10-recorder/issues/18), [#19](https://github.com/ju57/f10-recorder/issues/19) |
| 10 | [#18](https://github.com/ju57/f10-recorder/issues/18), [#19](https://github.com/ju57/f10-recorder/issues/19) |
| 11 | [#20](https://github.com/ju57/f10-recorder/issues/20) |
| 12 | [#20](https://github.com/ju57/f10-recorder/issues/20) |
| 13 | [#14](https://github.com/ju57/f10-recorder/issues/14), [#34](https://github.com/ju57/f10-recorder/issues/34) |
| 14 | [#14](https://github.com/ju57/f10-recorder/issues/14), [#53](https://github.com/ju57/f10-recorder/issues/53) |
| 15 | [#14](https://github.com/ju57/f10-recorder/issues/14) |
| 16 | [#14](https://github.com/ju57/f10-recorder/issues/14) |
| 17 | [#16](https://github.com/ju57/f10-recorder/issues/16) |
| 18 | [#16](https://github.com/ju57/f10-recorder/issues/16) |
| 19 | [#16](https://github.com/ju57/f10-recorder/issues/16) |
| 20 | [#15](https://github.com/ju57/f10-recorder/issues/15) |
| 21 | [#15](https://github.com/ju57/f10-recorder/issues/15) |
| 22 | [#16](https://github.com/ju57/f10-recorder/issues/16), [#17](https://github.com/ju57/f10-recorder/issues/17) |
| 23 | [#16](https://github.com/ju57/f10-recorder/issues/16) |
| 24 | [#16](https://github.com/ju57/f10-recorder/issues/16) |
| 25 | [#14](https://github.com/ju57/f10-recorder/issues/14), [#15](https://github.com/ju57/f10-recorder/issues/15), [#35](https://github.com/ju57/f10-recorder/issues/35) |
| 26 | [#14](https://github.com/ju57/f10-recorder/issues/14), [#53](https://github.com/ju57/f10-recorder/issues/53) |
| 27 | [#17](https://github.com/ju57/f10-recorder/issues/17) |
| 28 | [#27](https://github.com/ju57/f10-recorder/issues/27), [#28](https://github.com/ju57/f10-recorder/issues/28) |
| 29 | [#27](https://github.com/ju57/f10-recorder/issues/27), [#28](https://github.com/ju57/f10-recorder/issues/28) |
| 30 | [#28](https://github.com/ju57/f10-recorder/issues/28), [#54](https://github.com/ju57/f10-recorder/issues/54) |
| 31 | [#28](https://github.com/ju57/f10-recorder/issues/28), [#33](https://github.com/ju57/f10-recorder/issues/33) |
| 32 | [#27](https://github.com/ju57/f10-recorder/issues/27), [#28](https://github.com/ju57/f10-recorder/issues/28) |
| 33 | [#21](https://github.com/ju57/f10-recorder/issues/21) |
| 34 | [#21](https://github.com/ju57/f10-recorder/issues/21) |
| 35 | [#11](https://github.com/ju57/f10-recorder/issues/11), [#20](https://github.com/ju57/f10-recorder/issues/20) |
| 36 | [#22](https://github.com/ju57/f10-recorder/issues/22) |
| 37 | [#23](https://github.com/ju57/f10-recorder/issues/23), [#34](https://github.com/ju57/f10-recorder/issues/34) |
| 38 | [#22](https://github.com/ju57/f10-recorder/issues/22), [#23](https://github.com/ju57/f10-recorder/issues/23), [#34](https://github.com/ju57/f10-recorder/issues/34) |
| 39 | [#23](https://github.com/ju57/f10-recorder/issues/23), [#26](https://github.com/ju57/f10-recorder/issues/26) |
| 40 | [#26](https://github.com/ju57/f10-recorder/issues/26) |
| 41 | [#26](https://github.com/ju57/f10-recorder/issues/26) |
| 42 | [#23](https://github.com/ju57/f10-recorder/issues/23), [#26](https://github.com/ju57/f10-recorder/issues/26) |
| 43 | [#23](https://github.com/ju57/f10-recorder/issues/23), [#24](https://github.com/ju57/f10-recorder/issues/24) |
| 44 | [#24](https://github.com/ju57/f10-recorder/issues/24) |
| 45 | [#22](https://github.com/ju57/f10-recorder/issues/22), [#25](https://github.com/ju57/f10-recorder/issues/25) |
| 46 | [#25](https://github.com/ju57/f10-recorder/issues/25) |
| 47 | [#26](https://github.com/ju57/f10-recorder/issues/26), [#54](https://github.com/ju57/f10-recorder/issues/54) |
| 48 | [#12](https://github.com/ju57/f10-recorder/issues/12), [#47](https://github.com/ju57/f10-recorder/issues/47) |
| 49 | [#12](https://github.com/ju57/f10-recorder/issues/12) |
| 50 | [#29](https://github.com/ju57/f10-recorder/issues/29) |
| 51 | [#29](https://github.com/ju57/f10-recorder/issues/29) |
| 52 | [#29](https://github.com/ju57/f10-recorder/issues/29) |
| 53 | [#29](https://github.com/ju57/f10-recorder/issues/29) |
| 54 | [#29](https://github.com/ju57/f10-recorder/issues/29), [#38](https://github.com/ju57/f10-recorder/issues/38) |
| 55 | [#13](https://github.com/ju57/f10-recorder/issues/13) |
| 56 | [#13](https://github.com/ju57/f10-recorder/issues/13) |
| 57 | [#13](https://github.com/ju57/f10-recorder/issues/13) |
| 58 | [#13](https://github.com/ju57/f10-recorder/issues/13) |
| 59 | [#13](https://github.com/ju57/f10-recorder/issues/13) |
| 60 | [#13](https://github.com/ju57/f10-recorder/issues/13), [#29](https://github.com/ju57/f10-recorder/issues/29) |
| 61 | [#34](https://github.com/ju57/f10-recorder/issues/34), [#39](https://github.com/ju57/f10-recorder/issues/39) |
| 62 | [#34](https://github.com/ju57/f10-recorder/issues/34), [#45](https://github.com/ju57/f10-recorder/issues/45) |
| 63 | [#34](https://github.com/ju57/f10-recorder/issues/34), [#45](https://github.com/ju57/f10-recorder/issues/45) |
| 64 | [#36](https://github.com/ju57/f10-recorder/issues/36) |
| 65 | [#36](https://github.com/ju57/f10-recorder/issues/36), [#41](https://github.com/ju57/f10-recorder/issues/41) |
| 66 | [#36](https://github.com/ju57/f10-recorder/issues/36) |
| 67 | [#36](https://github.com/ju57/f10-recorder/issues/36), [#41](https://github.com/ju57/f10-recorder/issues/41) |
| 68 | [#36](https://github.com/ju57/f10-recorder/issues/36) |
| 69 | [#36](https://github.com/ju57/f10-recorder/issues/36) |
| 70 | [#35](https://github.com/ju57/f10-recorder/issues/35) |
| 71 | [#35](https://github.com/ju57/f10-recorder/issues/35) |
| 72 | [#35](https://github.com/ju57/f10-recorder/issues/35) |
| 73 | [#38](https://github.com/ju57/f10-recorder/issues/38) |
| 74 | [#40](https://github.com/ju57/f10-recorder/issues/40) |
| 75 | [#40](https://github.com/ju57/f10-recorder/issues/40), [#44](https://github.com/ju57/f10-recorder/issues/44) |
| 76 | [#40](https://github.com/ju57/f10-recorder/issues/40) |
| 77 | [#40](https://github.com/ju57/f10-recorder/issues/40) |
| 78 | [#41](https://github.com/ju57/f10-recorder/issues/41) |
| 79 | [#40](https://github.com/ju57/f10-recorder/issues/40) |
| 80 | [#42](https://github.com/ju57/f10-recorder/issues/42) |
| 81 | [#42](https://github.com/ju57/f10-recorder/issues/42) |
| 82 | [#42](https://github.com/ju57/f10-recorder/issues/42) |
| 83 | [#43](https://github.com/ju57/f10-recorder/issues/43), [#55](https://github.com/ju57/f10-recorder/issues/55) |
| 84 | [#43](https://github.com/ju57/f10-recorder/issues/43) |
| 85 | [#42](https://github.com/ju57/f10-recorder/issues/42) |
| 86 | [#39](https://github.com/ju57/f10-recorder/issues/39) |
| 87 | [#37](https://github.com/ju57/f10-recorder/issues/37) |
| 88 | [#37](https://github.com/ju57/f10-recorder/issues/37) |
| 89 | [#37](https://github.com/ju57/f10-recorder/issues/37) |
| 90 | [#12](https://github.com/ju57/f10-recorder/issues/12), [#28](https://github.com/ju57/f10-recorder/issues/28), [#51](https://github.com/ju57/f10-recorder/issues/51) |
| 91 | [#30](https://github.com/ju57/f10-recorder/issues/30) |
| 92 | [#30](https://github.com/ju57/f10-recorder/issues/30), [#31](https://github.com/ju57/f10-recorder/issues/31) |
| 93 | [#32](https://github.com/ju57/f10-recorder/issues/32) |
| 94 | [#32](https://github.com/ju57/f10-recorder/issues/32) |
| 95 | [#31](https://github.com/ju57/f10-recorder/issues/31), [#54](https://github.com/ju57/f10-recorder/issues/54) |
| 96 | [#30](https://github.com/ju57/f10-recorder/issues/30) |
| 97 | [#33](https://github.com/ju57/f10-recorder/issues/33) |
| 98 | [#33](https://github.com/ju57/f10-recorder/issues/33) |
| 99 | [#33](https://github.com/ju57/f10-recorder/issues/33) |
| 100 | [#33](https://github.com/ju57/f10-recorder/issues/33) |
| 101 | [#11](https://github.com/ju57/f10-recorder/issues/11), [#46](https://github.com/ju57/f10-recorder/issues/46), [#49](https://github.com/ju57/f10-recorder/issues/49) |
| 102 | [#47](https://github.com/ju57/f10-recorder/issues/47) |
| 103 | [#47](https://github.com/ju57/f10-recorder/issues/47) |
| 104 | [#48](https://github.com/ju57/f10-recorder/issues/48), [#49](https://github.com/ju57/f10-recorder/issues/49), [#50](https://github.com/ju57/f10-recorder/issues/50), [#56](https://github.com/ju57/f10-recorder/issues/56), [#58](https://github.com/ju57/f10-recorder/issues/58) |
| 105 | [#34](https://github.com/ju57/f10-recorder/issues/34), [#45](https://github.com/ju57/f10-recorder/issues/45), [#57](https://github.com/ju57/f10-recorder/issues/57) |
| 106 | [#48](https://github.com/ju57/f10-recorder/issues/48), [#50](https://github.com/ju57/f10-recorder/issues/50), [#51](https://github.com/ju57/f10-recorder/issues/51), [#52](https://github.com/ju57/f10-recorder/issues/52), [#55](https://github.com/ju57/f10-recorder/issues/55), [#56](https://github.com/ju57/f10-recorder/issues/56), [#57](https://github.com/ju57/f10-recorder/issues/57), [#58](https://github.com/ju57/f10-recorder/issues/58) |
