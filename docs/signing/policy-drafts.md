# Signing and privacy policy drafts

Prepared 25 October 2026 for [issue #48](https://github.com/ju57/f10-recorder/issues/48). **Unapproved text; signing service not active or confirmed.** This file does not change the application's behavior or publish a policy on the homepage. Approval, publication and implementation remain separate prerequisites. Source references are in the [official register](official-sources.md); evidence and gaps are in the [dossier](dossier.md).

## Code signing policy — proposed public copy

F10 is maintained publicly by **ju57** at [ju57/f10-recorder](https://github.com/ju57/f10-recorder). We intend to sign approved F10 Windows application and installer releases using SignPath Foundation, subject to provider acceptance and verified build provenance. Signing is not currently available through an approved F10 Foundation configuration.

After approval and activation, replace that status sentence with the required credit, preserving its links:

> Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org).

The certificate publisher will be the Foundation if accepted; **ju57** and **F10** remain public maintainer/product branding. A signature does not certify recorder feature completeness or every release gate. A new signed binary may still receive a SmartScreen warning; [Microsoft explains the reputation signals](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/smartscreen-reputation).

Proposed accountable people: owner/committer **@ju57**, source/build reviewer **@ju57 pending confirmation and any additional reviewer**, signing approver **@ju57 pending confirmation**. Before publishing the active policy, replace all pending assignments with the confirmed public roster or permission-group links. Resolve provider acceptance of overlapping roles and name an incident contact; do not publish this draft as an established team. AI assistance does not supply a human signing approval.

Proposed release practice: record immutable source revision, reviewed build/installer/dependency inputs, hosted run/artifact IDs, unsigned/signed file hashes, exact component inventory, signature verification and approving human. Sign only the approved F10-owned allowlist; retain upstream binary identity and notices. A signing failure blocks release. Any change after approval requires review of the affected artifacts again. Every release requires the confirmed human's manual signing approval and a separate exact-artifact publication decision by ju57.

Confirmed team members will use MFA for source and signing access. CI submission will use a separate restricted identity; credentials and recovery material will never be committed. Suspected misuse will stop further requests while the named owner investigates, disables affected access and coordinates with the provider. These are proposed operational commitments requiring human acceptance and implementation evidence.

## Privacy policy — proposed public copy

F10 saves replay video, screenshots and recording preferences locally. The current prototype captures its applied monitor or selected window and optional system audio. Its rolling replay cache is stored on disk. Saved media remains in the selected local folders; choosing a synchronized or network folder can cause that folder's service to transmit files under its own settings. Select local storage if that behavior is unwanted. Future manual recording and microphone features require updated policy and exact-version verification before being represented as available.

F10 does not automatically upload recordings, telemetry or crash reports. It stores local diagnostic logs and profile/worker state; these may expose paths, application/device details or errors. Review and redact information before choosing to share it. The accepted beta design includes an inspectable redacted diagnostic export, but this draft does not claim that the complete export feature exists in the prototype. Do not attach raw media or private logs to public issues.

The current prototype installer and explicit recording-engine repair can download a pinned FFmpeg package from **Gyan**, checking approved hashes before execution. This request exposes ordinary network connection information, including the address seen by the download host. It is a dependency download, not a media upload. A verified installed engine can be reused offline; worker restarts do not initiate dependency downloads. The intended beta bundles the approved engine/runtime for offline installation, but that installer is not yet qualified. There is no implemented automatic updater asserted here.

Downloads from GitHub Releases, explicit issue/report sharing, and any chosen cloud/network folder are interactions with those providers. Review their policies when using them: [GitHub privacy statement](https://docs.github.com/en/site-policy/privacy-policies/github-general-privacy-statement). The engine download source is [Gyan's build page](https://www.gyan.dev/ffmpeg/builds/); **a separately applicable Gyan privacy statement has not been verified in this dossier**, so clarify affected-service disclosure before publishing an active policy. [SignPath's privacy statement](https://signpath.io/privacy-policy) applies to provider-service interactions; CI signing sends release binaries/build provenance to the provider, not user recordings. Its account/usage information is distinct from recorder telemetry. Confirm applicable processing terms before enabling integration.

Installation writes application/dependency files and may configure Windows sign-in startup. The current development script enables startup and prints the startup notice after the registry change; it does not establish advance warning/choice or the accepted beta consent flow. The beta must explain capture, audio, disk use and startup before Finish, preserve existing choices on upgrade, and provide ordinary uninstallation. The current [uninstall instructions](../../README.md#uninstall) explain optional settings removal and saved-clip preservation. Local log/cache retention and uninstall behavior must be checked for the exact beta package before publishing stronger promises.

Before activation, supply a monitored privacy/security contact approved by ju57 and verify the policy against the exact shipped version and every affected component/service. This draft supplies no legal entity, legal address or statutory compliance representation.

## Publication checklist — pending

- ju57 approves accurate present-tense wording and commits to the proposed practices; eliminate pending public assignments.
- Publish/link **Code signing policy** and privacy policy on the actual project home and download/release pages. This scoped docs-only candidate deliberately does not edit README or release pages.
- Use the service credit only after acceptance; verify product/version restrictions and all intended signed artifacts in the provider configuration.
- Resolve affected third-party privacy disclosures, actual support contact, exact-version first-run/system-change/uninstall behavior, and source/license obligations before binary release.

Current-code evidence: [README](../../README.md), [installer](../../install.ps1), [uninstaller](../../uninstall.ps1), [download implementation](../../src/MediaDependency.cs), [local log implementation](../../src/Settings.cs), [beta spec](../beta-spec.md). This was static inspection, not a network trace or privacy audit.
