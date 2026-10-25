# SignPath Foundation submission dossier — issue #48

Prepared **25 October 2026**. Status: **draft for maintainer and independent review; not an application or approval**. Historical source baseline: `4cb898e71ca61a0870942fbb3f82c94038d5ef93`, which included reviewed PR #59 while it was still unmerged. Scope: [T38 / #48](https://github.com/ju57/f10-recorder/issues/48), under [beta spec #10](https://github.com/ju57/f10-recorder/issues/10). Repository facts below were inspected at that baseline; public GitHub facts were read on the preparation date. Refresh both before submission.

The preferred free route is plausible enough to prepare, but **application readiness and eligibility are unestablished**. The largest immediate gaps are no demonstrated release in the intended installer form, no demonstrated reputation, incomplete component eligibility/source arrangements, no configured signing provenance, and unconfirmed people/MFA. No GitHub Releases were returned; distribution elsewhere was not established or ruled out. None of these gaps is resolved by this document. The provider alone decides acceptance.

## Package contents and authority

- [Dated official-source register](official-sources.md): exact primary URLs, source categories and access limitations.
- This dossier: requirement-to-evidence assessment, submission outline, role checklist and prerequisite owners.
- [Policy drafts](policy-drafts.md): text requiring ju57 approval and publication in the required locations; no claim that signing is already provided.
- [Hosted provenance and artifact plan](provenance-plan.md): inert design only, with no workflow, credentials or permissions enabled.

No application, account, key, provider contact, identity submission, purchase, permission/MFA change, installation or signing is authorized by this dossier. No identity documents, private contact details, credentials or vendor binaries belong in its public attachments.

All **12 mandatory gates** remain open: G-CAP, G-COEX, G-REPLAY, G-AV, G-STORE, G-MIGRATE, G-UX, G-MATRIX, G-SEC, G-PKG, G-SIGN and G-BETA. See the [authoritative gate register](../beta-spec.md#mandatory-release-gate-register). G-CAP's [issue #9](https://github.com/ju57/f10-recorder/issues/9) was verified OPEN; it remains deferred/unresolved. After the original preparation, PR #59 was squash-merged into main as `cce63cf6dee2eab94fc52523fb18b562cad1ed52`; this changes its integration status, not the historical research baseline or any release-gate acceptance. T38 documentation completion is distinct from G-SIGN completion and exact-artifact release approval.

## Public identity and certificate identity

| Field | Proposed submission value / evidence | What remains unknown |
| --- | --- | --- |
| Project and public brand | **F10**, maintained publicly as **ju57**; [repository](https://github.com/ju57/f10-recorder). Accepted in the [brand guidelines](../brand-guidelines.md#approved-identity) and [beta spec](../beta-spec.md). | No separate trademark or corporate identity is established. Internal project, executable and existing folder identifiers are not automatically renamed. |
| Maintainer location | Portugal, supplied by the human task context and accepted plan. | No address, legal name, entity status or identity document was inspected. |
| Certificate publisher | **SignPath Foundation**, conditional on approval; [Foundation explanation](https://signpath.org/). | No issued certificate, subject string, thumbprint or active subscription exists in the inspected evidence. |
| Public labels | Preserve ju57/F10 name, product metadata and release attribution. | A GitHub handle is not a validated legal publisher. Do not promise a ju57 certificate subject. |

The Foundation advertises a route without personal identification, vouching through its own identity and verifiable OSS provenance. That does not establish F10's acceptance or eliminate account/project-control checks. The [service privacy policy](https://signpath.io/privacy-policy) lists account name, business email and administrator job title; anonymous account eligibility is not inferred. No reviewed Foundation page states a Portugal exclusion; absence of a stated exclusion is not geographic approval. This dossier does not assume a Portuguese individual qualifies for another provider. [Foundation homepage](https://signpath.org/), [terms](https://signpath.org/terms.html).

## Requirements matrix

Requirement labels are concise pointers to the [Foundation conditions](https://signpath.org/terms.html), with technical detail in the source register. “Partial” means evidence exists, not compliance certification.

| Requirement | Current project evidence | Assessment / action before submission |
| --- | --- | --- |
| Active, documented OSS | Public repository; [MIT license](../../LICENSE); substantial [README](../../README.md); source baseline includes current development. | Partial. GitHub API on 25 October: public, not archived, created 13 October 2026; description explains recorder scope. Recheck maintained status and actual download page. |
| OSI components; no commercial dual licensing/proprietary code except system libraries | MIT covers F10 only. [Notices](../../THIRD_PARTY_NOTICES) identify .NET/Avalonia/native dependencies and GPLv3-or-later FFmpeg. | Incomplete. Exact dependency/license/source inventory and any system-library exceptions need review under G-PKG. Never infer all-component eligibility from the root license. |
| Released in intended form | `gh release list` returned `[]`; no Inno release workflow or `.iss` package source in baseline. Distribution elsewhere is unknown. | Unresolved prerequisite. Do not create an unsigned release to manufacture compliance. ju57 must authorize clarification of the first-release situation before application. |
| Reputation and repository control | GitHub API: 0 stars, 0 forks; newly created repository. `.github/CODEOWNERS` names `@ju57`. | Unproven. Counts are observations, not a provider threshold. Prepare truthful history/adoption references if any; no known-user claims. CODEOWNERS does not prove all access/protection settings. |
| Own project/binaries; upstream exclusion | F10 source/SDK projects identifiable; bundled native/runtime/engine dependencies separate. | Partial. Use the exact allowlist in the artifact plan. Generated apphost/Inno stubs require explicit scope confirmation; never blanket-sign every DLL/EXE. |
| Privacy, system-change warnings, uninstall | README describes local recording, startup and dependency download. [Install](../../install.ps1) enables startup and prints its startup notice after mutation; [uninstall](../../uninstall.ps1) exists. | Incomplete. Current script does not evidence advance warning/choice or the accepted first-run/offline installer. Draft policy separates existing behavior from future acceptance. G-UX/G-MIGRATE/G-PKG remain open. |
| Team MFA; author/reviewer/approver roster | Proposed responsibilities below; no account-security evidence collected. | Pending ju57 confirmation. Repository access and AI review do not establish human MFA or role membership. |
| Public signing/privacy policy | Drafts supplied in this directory. | Pending human approval and publication/linking from home and release/download pages. No enabled-service credit may be published prematurely. |
| Product/version metadata restrictions | Project assembly names inspected; no explicit release Product/version convention found in inspected project/props files. | Pending compiled-file inventory and enforced configuration. No binary metadata was measured. |
| Verifiable builds; manual release signing | CI has pinned actions, hosted builds and locked restore; no release artifact upload/signing path. | Partial starting point. Implement and independently review the inert provenance plan later. No local binary upload may substitute for verified hosted origin. |
| No malware/PUA or prohibited security-tool features; investigate complaints | Documented purpose is local replay capture. README states independent implementation and no shipped NVIDIA application code. | Source-purpose evidence only; no exhaustive malware/prohibited-feature or package audit is claimed. ju57 must accept complaint investigation obligations and name a monitored contact. |

Current source checks do not establish legal compliance, current service approval, trustworthy binary provenance or successful recorder behavior.

## Proposed named roles and MFA checklist

These are **proposals, not configured assignments**. `@ju57` is the only public human identifier established in this task. Public terms do not specify three distinct people or a minimum two-person quorum; neither is invented here. A single person occupying multiple roles is not assumed accepted by the provider. Codex prepares/reviews evidence but is not a human SignPath approver or substitute for an accountable reviewer. Role capabilities and CI submission are described in the official user/project docs listed in the source register.

| Role | Proposed named member | Confirmation/evidence still required |
| --- | --- | --- |
| Project owner / policy accountable person | `@ju57` | Confirm control, actual authorized committers, recovery contact and provider-account owner; no legal name is inferred. |
| Author/committer | `@ju57`; others only if explicitly confirmed | Enumerate every human with write/administration rights and accepted contribution-review path. |
| Human source/build reviewer | `@ju57` proposed; additional person **unassigned** | Name the reviewer for each release, including build scripts, CI, dependency updates and installer inputs. Resolve meaningful independence and provider acceptance if a one-person team. |
| Signing approver / release decision | `@ju57` proposed | Confirm trusted human SignPath approval role and explicit manual approval of every release; release publication stays a separate ju57 action. |
| CI submitter | Dedicated non-human account, name **unassigned** | Least-privilege submit-only policy; secret in approved CI secret storage; no approval or administrative privilege. No token/account created. |
| Backup / incident owner | **Unassigned**, ju57 to decide | Confirm revocation/escalation, token rotation and continuity plan; do not invent another maintainer. |

Before activation, ju57 must confirm each actual team member uses MFA on GitHub and SignPath, record confirmation date/owner privately, verify invitations and effective permissions, remove obsolete access, and verify recovery arrangements. Never collect passwords, recovery codes, MFA seeds or identity scans in Git, issue comments, artifacts or this checklist. Any provider-supported approval/quorum and audit retention must be selected and tested against the approved plan. Product capabilities do not prove configuration or the provider's acceptance of a small team.

## Submission outline, ready to complete by ju57

This is an evidence outline, **not a verified transcription of application form fields**. The application page's extracted text did not expose its dynamic form. Recheck the live form after explicit application authorization.

| Submission topic | Prepared text / attachment | Missing fact |
| --- | --- | --- |
| Project | “F10 is an MIT-licensed local rolling replay recorder for Windows/NVIDIA, written in C#/.NET with Avalonia; Linux capture is planned.” README and repository URL. | Final public download/documentation URL and intended artifact version. |
| Requested route | Free SignPath Foundation assessment; ju57 brand retained, Foundation publisher accepted if approved. | Provider decision and application/contact authorization. |
| Source and builds | Public repository; immutable source/build/dependency plan and exact signing allowlist. | Actual hosted candidate/run/artifact IDs, metadata, hashes and provider project/policy IDs. |
| Maintenance/reputation | Repository creation date and present public counts stated honestly. | Existing release-form clarification; verifiable adoption/history or affiliations, only if true and consented. |
| People/security | Proposed roster and MFA checklist. | Human contact supplied privately, confirmed members/roles/MFA and any requested control proof. |
| Policies/compliance | Policy drafts; LICENSE/notices; package classification plan. | Human approval/publication, final component/source arrangements and generated-artifact eligibility answer. |

Do not claim the intended beta features are already shipped. Do not include recordings, machine logs, proprietary reverse-engineering extracts or identity documents as project evidence. Provider-requested private information belongs only in the approved provider channel after separate authorization.

## Prerequisites and explicit fallback decision

1. **ju57 / provider:** resolve intended-form first-release and reputation uncertainty honestly; decide whether/when to authorize contact or application. There is no claim of a minimum star count or promised exception.
2. **ju57:** approve public policy text/links, accountable people, private contact, MFA confirmations and role separation/continuity. Confirm any commercial dual-licensing or non-OSS component concerns; absence is not established from the root MIT file.
3. **Codex, later scoped tickets / ju57 acceptance:** complete G-PKG exact component/source inventory, license obligations, qualified shipping engine, pinned installer builder, offline package and lifecycle. Establish the F10/generated/upstream signing classification and seek provider confirmation of unresolved generated stubs.
4. **Codex, later scoped integration / independent reviewer:** implement hosted packaging, metadata enforcement, provider origin checks, manual signing policy, signature/timestamp verification and failure controls. Human provisions accounts, credentials and permissions only with explicit authorization.
5. **ju57 / provider:** obtain acceptance and a usable configuration; run a separately authorized exact-artifact trial. Preserve all other engineering/release gates and require exact-artifact release approval.

**Fallback trigger:** if Foundation rejects F10, declines its intended first-release/package/role model, is unavailable when the required release candidate needs signing, or cannot cover the required artifacts under an acceptable provenance route, stop signing-route advancement and return to ju57 for an explicit **provider/identity/budget/schedule decision**. Record the dated reason. Indefinite approval delay is not approval: ju57 must name a decision checkpoint before release scheduling. Paid service/certificate research may follow only as authorized work; no purchase, publisher substitution, unsigned beta or weakened G-SIGN follows automatically. Refresh alternative-provider geographic and identity eligibility when that decision is requested.

## Validation and review boundary

The coordinating chat supplied an independently derived requirements baseline on 25 October 2026, before the reviewer saw this draft. Its claim IDs map as follows: E1 → component/license row; E2 → maintained/released/reputation rows; I1 → identity table; A1 → own/upstream row and artifact allowlist; U1/P1 → privacy/system-change row and policy draft; R1 → roster/MFA and manual approval; W1 → publication checklist; M1 → metadata/hosted provenance plan. Its consequential current-download negative case and offline-reuse limiting control are retained in the policy. This crosswalk records incorporation of the independent baseline, not a final review verdict. Local reviewer receipt remains outside tracked documentation because it contains machine-specific context.

Checks for this lane: primary-source retrieval and claim mapping, baseline file/metadata inspection, read-only GitHub queries, local Markdown-link/content/whitespace checks, scope diff review. No build, capture, GPU, recorder, installer or signing run is needed for these documentation changes. Source facts are inspected facts; future pipeline/policy wording is a proposal. The `.github` workflow is read as source, not evidence of a successful exact-candidate run.

The delegated researcher authored the official-source register. That is research authorship, not independent acceptance. **Independent source/claims and standards/spec review is assigned by the coordinating chat and remains pending at initial candidate handoff.** The review receipt must name the reviewer, exact candidate SHA, independently checked consequential claim, findings and resolutions; any corrective revision needs affected review again. No independent pass is asserted here.
