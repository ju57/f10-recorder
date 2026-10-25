---
name: f10-design
description: Design, implement or review F10 interfaces and visual assets using the approved brand, logo and interaction decisions. Use for F10 screens, controls, notifications, overlay, identity and UI copy.
---

# F10 design

Use the F10 checkout attached to the task. Read `docs/brand-guidelines.md` before designing or changing the interface. It is the canonical visual specification; this skill routes to it rather than duplicating its tokens.

## Resolve the relevant source

- Identity, type, palette, spacing and controls: `docs/brand-guidelines.md`.
- Approved logo and asset provenance: `docs/m0-review/assets/ASSETS.md` and `docs/m0-review/assets/f10-logo-approved.json`.
- Accepted layouts and behavior: the relevant screen section in `docs/m0-review/README.md`, then `docs/release-plan.md` for the corresponding decision. Historical proposals are superseded by later accepted decisions.
- HTML examples: `docs/m0-review/wireframes.html` and `docs/m0-review/f10-theme.css`. These illustrate intent; they do not establish native behavior.

## Preserve the settled identity

- The visible product name is **F10**. Explain its recording purpose through user-facing copy. Do not rename code namespaces, executables, existing folders or user media merely to change the visible brand.
- The approved logo is `docs/m0-review/assets/f10-logo.png`: the exact realistic dark keycap selected by the user on 24 October 2026. Preserve its shape, green F10 legend, perspective, highlights and transparency. The illustrated, softened-edge, abstract-monogram and reviewer-preferred hybrid candidates are alternatives, not approved replacements. Do not regenerate or redesign the logo unless the user asks.
- The selected keycap's material shading is an explicit identity-asset exception to the restrained flat UI. It does not authorize gradients, glow or photographic material on ordinary controls and panels.
- Hanken Grotesk remains the accepted rebuilt-interface typeface, with Segoe UI fallback. Current native source uses Segoe UI with Noto Sans fallback. Keep that implementation difference explicit. Do not claim the generated logo's lettering is an exact font match or recolour its pixels to an exact token without verification and an authorized asset change.

## Apply the interface decisions

Use the guide's neutral dark surfaces, green primary action, rounded controls and concise copy. Keep Phosphor solid UI icons and the accepted Lucide Animated hourglass exception for processing states. Provide names/tooltips for icon-only controls; retain independent replay and manual-recording meanings.

The accepted desktop shell keeps its normal window size and scrolls content. Reuse the shared rounded notification family, individual dismissal and state-specific behavior. Do not reintroduce the old rectangular green toast, redundant microphone status labels, or a logo in the deliberately unbranded passive recording indicator.

Consult the screen's decisions before changing interactions: optional audio does not block setup; accepted saves and manual finalization survive UI navigation and exit cancellation; dismissing an issue never clears the underlying condition. Do not reopen settled layouts or logo selection merely because another design style is available.

## Verify the actual change

Inspect the affected rendering at its intended size and on relevant backgrounds. Preserve logo aspect ratio and alpha; separate small-size export work from approval of the master artwork. Browser size samples do not establish Windows tray/DPI readability, exact font geometry or capture exclusion.

Keep the approved asset and source/provenance records aligned. Report what was inspected, what remains an implementation limit, and whether the change is only a review artifact. Visual approval alone does not approve native integration, installation, signing, release or performance claims.
