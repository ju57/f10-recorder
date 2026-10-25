# Official signing source register

Retrieved **25 October 2026** for issue #48. Research notes only: no application,
account, key, signing configuration or permission was created. Sources are the
Foundation, SignPath product documentation, and Inno Setup's own documentation.
The Foundation decides admission; a documented product capability is not an
approval or a relaxation of Foundation conditions. Recheck these changing pages
before submission or integration.

| ID | Official source and title | Verified claim or use | Limits / uncertainty |
| --- | --- | --- | --- |
| S1 | [Foundation terms / conditions for Open Source projects](https://signpath.org/terms.html) | Eligibility: OSS licensing, maintenance, existing release, documentation, reputation/control. Constraints: own projects/binaries; upstream bundling exception; privacy, configuration warnings, uninstall; MFA; roles; published policies; metadata; verifiable builds; manual approval. | Page labels its code of conduct draft. Admission is discretionary. No quantified reputation threshold or Portugal exclusion found. This is not confirmation of F10 eligibility. |
| S2 | [Free Code Signing for Open Source software](https://signpath.org/) | Foundation describes a free OSS service, repository-to-binary verification, HSM-held keys, and use of its name without personal identification. | Its introductory warning-free marketing does not establish SmartScreen behavior for F10. No project account or certificate was verified in this research. |
| S3 | [Apply for a free SignPath.io subscription](https://signpath.org/apply.html); [official website source: apply.md](https://github.com/SignPath/fdn-website/blob/main/docs/apply.md) | Application entry point exists. The source embeds a HubSpot form script. | Text extraction exposes the heading, not the rendered fields or validation. Required contact fields, consents, personal information and application workflow remain unverified. Do not fabricate a completed form. |
| S4 | [About SignPath Foundation](https://signpath.org/about.html) | Foundation says it is currently operated by SignPath GmbH and links its acceptance policies. | Distinguish the Foundation program/publisher from its commercial operator and from F10's public project identity. This page does not validate any F10 maintainer's legal identity. |
| S5 | [Trusted Build Systems: GitHub](https://docs.signpath.io/trusted-build-systems/github) | Connector checks workflow origin, GitHub-provided metadata and uploaded workflow artifact. For OSS requests, every preceding workflow job must use GitHub-hosted agents. The predefined GitHub.com trusted system must be linked. Audit-log evaluation requires the GitHub App with repository access. | Examples are illustrative, not enabled F10 configuration. Source has inconsistent policy-key spellings between example and table; validate actual schema rather than copying keys blindly. Select and pin reviewed action commits during implementation. |
| S6 | [Origin Verification](https://docs.signpath.io/origin-verification/) | Required for Open Source Code Signing. Verifies repository, branch, commit and build URL; build reproducibility checks vary by CI. Build configuration belongs in source control, and reviews must include scripts/CI and external content. | Origin verification is not a proof that all downloaded dependencies are benign or that the whole package is bit-for-bit reproducible. It does not substitute for dependency review or product qualification. |
| S7 | [Setting up Projects](https://docs.signpath.io/projects) | Signing policies define certificate, submitters and approval process; approvers/quorum are configurable. Trusted-build verification disallows interactive submitters. OSS requires trusted-build and origin verification. Repository URL and allowed release branches constrain origin. Artifact configurations can be versioned. | General examples include non-Foundation configurations. Their no-approval examples do not override S1. A single human may hold multiple documented roles only if accepted; this research establishes no F10 assignments. |
| S8 | [Managing Users](https://docs.signpath.io/users/) | Interactive users administer/approve; CI users authenticate only with API tokens. SignPath recommends dedicated low-privilege CI users and CI-secret storage. Administrative and signing-request changes have audit logs. Support access is off by default and, when authorized, is administrative. | No F10 MFA, roles, account recovery, token storage or repository permissions were inspected. Never put an administrator token in a build. User administration can escalate privileges, so named ownership requires review. |
| S9 | [Artifact Configuration](https://docs.signpath.io/artifact-configuration/) | Configurations describe files, signing directives, constraints and optional signature verification. Generated configurations must be manually reviewed to exclude third-party signing instructions. Multiple/nested artifacts can be configured. | Do not apply recursive EXE/DLL signing to a self-contained .NET package. Generated defaults may include upstream files. Configuration availability is not permission to sign them. |
| S10 | [Artifact Configuration: Reference](https://docs.signpath.io/artifact-configuration/reference) | PE EXE/DLL and PowerShell support Authenticode. PE restrictions include product name/version, file version, company and original filename. Authenticode verification checks timestamp/certificate and Windows trust rules. MSI/CAB/MSIX and ZIP are documented composite formats; PE is not. | `setup.exe` can be a PE signing target, but this reference does not establish automatic unpack/repack/deep signing of an Inno executable. An installer signature does not individually sign its payload. Verify resulting signer, chain, timestamp and exact outputs separately. |
| S11 | [Inno Setup: SignTool](https://jrsoftware.org/ishelp/topic_setup_signtool.htm) | Compiler hook can sign Setup, uninstallers when enabled, and explicitly flagged source files. The signing command is supplied separately. | Product capability only. No Inno version/license arrangement, compiler hook or signing service has been qualified for F10. Avoid flags that sign all bundled third-party inputs. |
| S12 | [Inno Setup: SignedUninstaller](https://jrsoftware.org/ishelp/topic_setup_signeduninstaller.htm) | Uninstaller signing is distinct: compiler can call a configured signer or produce a persistent uninstaller file for external signing and later embedding. Builder/directive changes may generate a different file. | Establish provenance, metadata and Foundation eligibility for the generated uninstaller before promising coverage. Reusing an old signed stub must not conceal a changed build or evade policy. |
| S13 | [SignPath Privacy Policy](https://signpath.io/privacy-policy) | Account registration requests name, business email and administrator job title; usage metadata includes IP/session/request time. | No personal-identification requirement for the Foundation certificate does not imply anonymous service accounts. No F10 registration was performed. Legacy `about.signpath.io/privacy-policy` timed out twice; current linked page was read. |
| S14 | [GitHub General Privacy Statement](https://docs.github.com/en/site-policy/privacy-policies/github-general-privacy-statement) | Current official policy linked for explicit GitHub download/report/service interactions. | Not a privacy audit of F10 or proof every selected external service is covered. Gyan-specific policy remains unverified. |
| S15 | [Microsoft: SmartScreen reputation for Windows app developers](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/smartscreen-reputation) | A new signed binary can still trigger a reputation warning. | Do not copy the Foundation homepage's broad warning-free marketing into F10's promises. No SmartScreen test was performed. |

## Submission and implementation questions retained

- How does Foundation apply its existing-release and reputation criteria to a
  new recorder with no GitHub Releases returned and other distribution unknown? No numeric threshold or
  preapproval is inferred. Do not publish an unsafe or unsigned beta just to
  create an application exhibit.
- Which exact application contact fields, consents and declarations appear in
  the live form? A human must inspect these when submission is authorized.
- Will Foundation accept the intended hosted-build packaging sequence and
  generated Inno installer/uninstaller artifacts under its own-binaries rule?
  A blanket certificate over upstream executables is outside the proposed scope.
- Which human accepts each author, reviewer, approver and administration role,
  and how will MFA compliance for all relevant accounts be evidenced privately?
  A GitHub handle, agent review or successful repository access answers none of
  these questions.
- What exact upstream dependencies will ship, with which licenses, notices,
  source arrangements and retained upstream signatures? That requires the final
  package inventory, not an encoder enumeration or an installer signature.

The absence of a geographic restriction on the reviewed Foundation pages is a
bounded observation, not a legal conclusion or a guarantee of service from
Portugal. The Foundation route uses the program's certificate identity; public
`ju57` / F10 branding and a maintainer's legal identity are separate questions.

## Research verification

All fifteen source groups above were opened or returned in the official-source
search results during this pass; inaccessible legacy documentation paths were
replaced with the current `docs.signpath.io` pages. Application-form rendering was
not inspected, and no external party was contacted. No raw pages, identity
documents, vendor executables or media were downloaded. This source register is
research authorship; independent claims/spec review remains separate.
