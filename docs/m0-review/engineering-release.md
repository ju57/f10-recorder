# M0 engineering research: package, dependency provenance and signing

Checked 24 October 2026 against the current working tree (HEAD `fd6fb31`) and primary vendor documentation. This is a planning assessment, not a legal opinion, signing approval, package build or runtime qualification. No installer, recorder, dependency executable or signing operation was run. Existing uncommitted work was preserved.

## Findings that affect M0

The accepted route remains a signed, per-user Windows x64 Inno Setup package, self-contained .NET/Avalonia application, bundled approved FFmpeg engine and explicit download/install updates. Research supports retaining that plan, but the repository does not yet contain the offline installer or signing pipeline. The current PowerShell development installer downloads the engine when needed and always enables sign-in startup, so it cannot serve as acceptance evidence for the agreed beta setup/upgrade behavior. [Build script](/D:/repository/f10-recorder/build-avalonia.ps1:11), [installer](/D:/repository/f10-recorder/install.ps1:27), [startup mutation](/D:/repository/f10-recorder/install.ps1:39)

M0 can freeze the proposed package architecture and concrete implementation gates. It cannot claim that Foundation signing is secured, that the selected engine satisfies both capture paths, or that corresponding-source distribution is complete.

## Current repository evidence

| Area | Verified source fact | Consequence |
| --- | --- | --- |
| SDK | `global.json` requests SDK 10.0.401, permits latest-patch roll-forward and excludes prereleases. | Record the SDK actually used; the file alone is not an exact compiler-binary identity. [Source](/D:/repository/f10-recorder/global.json:3) |
| Application | `net10.0-windows`, Avalonia Desktop/Fluent 12.1.4, trimming disabled; MIT license/notices copied to output/publish. | Retain this baseline; capture every resolved package/native asset and runtime identity in the package manifest. [Project](/D:/repository/f10-recorder/projects/F10Recorder.Desktop/F10Recorder.Desktop.csproj:4) |
| Dependency locking | RID-specific locks, CI locked restore, NuGet auditing and warnings-as-errors are configured. | Use the `win-x64` lock for release publishing; do not let release packaging silently restore different dependencies. [Properties](/D:/repository/f10-recorder/projects/Directory.Build.props:3) |
| CI | Hosted Windows builds the app and worker checks, but no release-installer or SignPath workflow is defined in the inspected CI. | Add an immutable package/signing path rather than presenting ordinary build CI as release provenance. [Workflow](/D:/repository/f10-recorder/.github/workflows/ci.yml:31) |
| Offline delivery | Self-contained `win-x64` publishing is implemented; the development installer calls `--ensure-media` and may download FFmpeg. | An offline installer still needs to contain and provision the verified engine package. [Publish](/D:/repository/f10-recorder/build-avalonia.ps1:11), [engine setup](/D:/repository/f10-recorder/install.ps1:27) |
| Upgrade/uninstall | Installation refuses running F10 processes and overwrites one application directory. Default uninstall preserves settings but removes application/private-engine directories; `-RemoveSettings` is broader. | No transactional upgrade, versioned rollback or beta uninstall retention evidence follows from these scripts. [Install](/D:/repository/f10-recorder/install.ps1:11), [uninstall](/D:/repository/f10-recorder/uninstall.ps1:1) |

Self-contained deployment includes the required .NET runtime, but native prerequisites must still be satisfied, and the embedded runtime is updated through a new application release. Do not infer that a machine's separately installed runtime patches the bundled runtime. [Microsoft publishing documentation](https://learn.microsoft.com/en-us/dotnet/core/deploying/#publish-as-self-contained)

## Exact FFmpeg baseline and remaining provenance gap

The current approved identity is specified in both code and notices:

| Artifact | Identity |
| --- | --- |
| Package | `ffmpeg-9.0.2-essentials_build.zip`, Gyan |
| Pinned package URL | `https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-9.0.2-essentials_build.zip` |
| ZIP SHA-256 | `60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba` |
| `ffmpeg.exe` SHA-256 | `3256173f3f8bffd7df12227c68adf68025edb1832273a9530688a7bb1ed8edec` |
| `ffprobe.exe` SHA-256 | `f0d36ecbbdd3bcfac3efa078c96c7271c2e68b3810595552ac3b7f17e9a65c52` |

These are repository-approved constants, not hashes freshly measured from a downloaded package in this research. The vendor checksum endpoint could not be read reliably through the web tool. Verification of the downloaded release input remains mandatory. [Constants](/D:/repository/f10-recorder/src/MediaDependency.cs:24), [notices](/D:/repository/f10-recorder/THIRD_PARTY_NOTICES:16)

The loader authenticates tool hashes before execution; provisioning verifies the ZIP, retains the package's documentation/licenses/presets and replaces an engine only after validation. Preserve that trust boundary when provisioning from an installer: bundled content is not allowed to authorize itself through profile-controlled metadata. [Tool verification](/D:/repository/f10-recorder/src/MediaDependency.cs:46), [provisioning](/D:/repository/f10-recorder/src/MediaDependency.cs:135)

The capability validator checks monitor/audio/filter/muxer primitives, H.264 NVENC/AAC/PNG, and timestamp bitstream-filter support. It does **not** require `gfxcapture` or initialize every advertised codec/profile. Meanwhile the experimental selected-window notes identify assessed git build `7fd2be97b9`. Those are different identities; prior window trials cannot qualify the pinned package. [Validator](/D:/repository/f10-recorder/src/MediaDependency.cs:90), [window evidence boundary](/D:/repository/f10-recorder/tests/window-capture.md:71)

The vendor currently lists release 9.0.2 and links it to FFmpeg commit `946fcce07b`; the matching source contains `vsrc_gfxcapture_winrt.cpp`. This is source-level feasibility evidence, not proof of binary inclusion, successful initialization, fullscreen behavior or timestamp equivalence. [Gyan release/source mapping](https://www.gyan.dev/ffmpeg/builds/), [matching source](https://raw.githubusercontent.com/FFmpeg/FFmpeg/946fcce07b/libavfilter/vsrc_gfxcapture_winrt.cpp)

Recommendation: keep 9.0.2 as the initial frozen candidate, then execute a bounded inventory/initialization/clock/capture-path gate on those exact bytes before calling it the shipping engine. If the candidate fails a required feature, choose a new immutable identity and repeat the gates; do not silently use a tested local git binary or a mutable latest download.

The repository records this build as GPLv3-or-later, and Gyan describes its builds as GPLv3. F10's own MIT license does not replace the engine's license or its external-library notices. Upstream explains that GPL components and version-3 configuration change the resulting FFmpeg license. [Notices](/D:/repository/f10-recorder/THIRD_PARTY_NOTICES:26), [Gyan](https://www.gyan.dev/ffmpeg/builds/), [FFmpeg license](https://raw.githubusercontent.com/FFmpeg/FFmpeg/946fcce07b/LICENSE.md)

Proposed distribution route: release the installer alongside a clearly linked corresponding-source archive for the exact engine/dependency build, configuration, patches and relevant build scripts/notices. Preserve a manifest mapping source revisions to the distributed bytes and identify any upstream sources reused. A tag link or generic FFmpeg home-page link alone is not the completed arrangement. GPLv3 defines Corresponding Source and its network-distribution option; this proposal still needs exact-build review. Reproducing identical binary hashes is a stronger provenance property than merely naming the corresponding source. [GPLv3 sections 1 and 6](https://raw.githubusercontent.com/FFmpeg/FFmpeg/946fcce07b/COPYING.GPLv3), [FFmpeg distribution guidance](https://ffmpeg.org/legal.html), [existing release gate](/D:/repository/f10-recorder/THIRD_PARTY_NOTICES:35)

## Inno Setup candidate and installer mechanics

Recommend pinning **Inno Setup 7.1.0 x64**, the stable version on the official download page on the checked date. Acquire it from that page's immutable `is-7_1_0` release URL, verify its published signature and record its SHA-256 before the first package build; this report does not invent a compiler checksum. [Official downloads](https://jrsoftware.org/isdl.php)

The tagged license permits commercial use subject to its preservation/origin conditions. The vendor requests purchases from commercial users and defines its revenue threshold, including relevant OSS donations; its FAQ explicitly says purchase is not strictly required. Do not describe a paid license as a technical prerequisite or infer ju57's financial classification. No purchase was made. [7.1.0 license](https://raw.githubusercontent.com/jrsoftware/issrc/is-7_1_0/license.txt), [commercial FAQ](https://jrsoftware.org/isorder.php)

Proposed installer contract:

- Use `PrivilegesRequired=lowest` for the accepted per-user route; validate on an ordinary non-admin account. The default is elevated administration, so this must be explicit. [Directive](https://jrsoftware.org/ishelp/topic_setup_privilegesrequired.htm)
- Avoid generic forced application shutdown. Inno's default application-close behavior can stop/restart applications, including during silent installation. Use an F10 worker-aware readiness handshake implementing Q62: wait/cancel while recording/finalization/accepted saves exist, then quiesce safely before replacement. [CloseApplications](https://jrsoftware.org/ishelp/topic_setup_closeapplications.htm)
- Stage a complete versioned application/engine package, validate its manifest, switch activation only after a safe boundary, and preserve a rollback pointer plus compatible settings backup. Never remove saved media or retained recovery data as upgrade cleanup. This is a proposed design; current scripts do not implement it.
- Sign the intended F10 release artifacts and installer through the approved provenance pipeline; explicitly resolve generated uninstallers/self-copies in artifact policy. Inno supports signed uninstallers, including externally signed cached uninstallers. Never apply a blanket sign flag to upstream FFmpeg/native libraries. [SignedUninstaller](https://jrsoftware.org/ishelp/topic_setup_signeduninstaller.htm), [SignTool](https://jrsoftware.org/ishelp/topic_setup_signtool.htm)

## SignPath Foundation readiness

Published conditions require maintained, documented OSS, an already released artifact form and verifiable reputation; acceptance is discretionary. No Portugal exclusion is stated in the reviewed criteria, but this is not confirmation of eligibility. The certificate publisher would be SignPath Foundation, already accepted in Q63a. Own binaries may be signed; upstream OSS binaries may be included without being re-signed as F10. Requirements include MFA, named author/reviewer/approver roles, a signing/privacy policy, consistent product/version metadata, verifiable builds and manual approval for every release. [Foundation terms](https://signpath.org/terms.html)

For GitHub integration, the documented OSS provenance path requires GitHub-hosted jobs leading to the signing request, with an uploaded workflow artifact. Keep hardware qualification separate: it may review the candidate's identity, but must not replace the hosted build with a locally modified upload. [SignPath GitHub integration](https://docs.signpath.io/trusted-build-systems/github)

Current readiness gaps:

| Gap | Next evidence/action | Owner |
| --- | --- | --- |
| Existing release/reputation | Public release pages could not be fetched reliably in this pass; recheck accessible release history and prepare evidence. Do not publish an unsigned beta just to satisfy a prerequisite. | Codex prepares; ju57 decides any submission/release |
| Foundation approval | No approval/application status is established. Prepare a concrete dossier, then obtain explicit authorization before applying/contacting the provider. | ju57 |
| Policy/MFA/roles | No published signing/privacy policy was found in the inspected README/CI; MFA and human role membership are not established by local Git access. | Codex drafts; ju57 confirms people/MFA and approves publication |
| Immutable signing path | Release packaging, metadata enforcement, unsigned-artifact upload and signing provenance are absent from the inspected CI. | Codex implements with scoped review |
| Upstream/generated artifacts | Confirm intended signing coverage for F10 executables, Inno-generated uninstaller and installer; retain upstream identities/notices. | Codex inventory; ju57/provider approval |
| Free-route rejection/unavailability | Return for a fallback/budget decision; do not drop the signed-beta requirement or promise approval. | ju57 |

## Proposed closure gates

For **M0 planning closeout**, ratify the Inno candidate and offline/versioned package design; freeze the initial engine candidate plus pass/fail replacement gate; assign implementation/validation owners; retain Foundation eligibility as an explicit unresolved external dependency with a fallback decision trigger. If M0 requires final shipping-engine and guaranteed signing identities, those rows remain open until their evidence exists.

Before **binary distribution**, require: exact component/source/license manifest; matching source distribution; offline install and failed-install recovery; active-work upgrade blocking; startup/pause/settings/media/recovery preservation; rollback compatibility; ordinary-account install/uninstall; signed artifact metadata/provenance verification; and maintainer approval of those exact artifacts. A successful ordinary build or this research report satisfies none of those runtime/release gates.

The smallest remaining human choices are approval of the proposed Inno/package baseline and whether M0 may close with explicitly owned external-signing and exact-engine qualification dependencies. Financial classification, actual MFA membership and provider acceptance cannot be manufactured from source research. No new product-scope reduction is recommended.
