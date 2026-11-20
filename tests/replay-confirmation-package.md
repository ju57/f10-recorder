# Static confirmation-package preparation

`replay-confirmation-package.py` maintains the reusable static part of the
existing C00 / retained-minute package workflow. It constructs prospective
metadata from an explicit recipe, recomputes dependent hashes after serialization,
checks the serialized result, and publishes the invariant manifest last.

This standalone tooling runs on current main without the unfinished issue-83
product implementation. Its generated PowerShell controller/release checks and
owner template previews are independent of that implementation. Matching C#
reader/helper integration is checked separately when those sources and a built
helper are explicitly available; this tooling does not add them to main.

```powershell
python -B tests/replay-confirmation-package.py plan
python -B tests/replay-confirmation-package.py build --recipe artifacts/local-package-inputs/recipe.json --output artifacts/local-confirmation-package
python -B tests/replay-confirmation-package.py verify --manifest artifacts/local-confirmation-package/invariants.json --expected-sha256 $manifestSha256
```

Use the `manifestSha256` returned by `build` for `$manifestSha256`. Output must be
a fresh child of this checkout's ignored `artifacts/` directory. An existing root
is preserved. Validation happens in memory before output creation; a later I/O
failure can leave an incomplete root, which must be preserved and reviewed.

## Recipe and template inputs

The recipe is JSON with these keys. Unknown keys and case-insensitive duplicate
JSON members reject. Keep machine-specific recipes and evidence ignored.

| Key | Meaning |
| --- | --- |
| `kind` | Exactly `confirmation-package-recipe-v1`. |
| `profile` | Exactly `maintained-v10-approval-clock-v1`. |
| `revision` | The identity of this new static package. |
| `templates` | Source JSON paths keyed by `C00` and `retained-H03-1m`. |
| `bindings` | Lists of role/path/source declarations for those two operations. |
| `pins` | Explicit input path-to-uppercase-SHA256 map for the intended binaries and other direct owner dependencies. |
| `runtimeManifests` | Source JSON paths keyed by `qualification` and `controller`; each contains absolute file paths and their expected uppercase digests. |
| `controllerRuntime` | The exact pinned PowerShell executable path. |
| `owner` | Optional; when present, must name this checkout's maintained `tests/replay-confirmation-owner.ps1`. |

Input paths resolve relative to the recipe. Preserve the declared absolute
Windows path spelling: the owning PowerShell reader compares some path strings
exactly. Runtime maps are verified against their existing digests, not refreshed
to make changed binaries appear unchanged. The owner and all its shared
controller files are pinned automatically. Declare the actual dotnet, helper,
FFmpeg and console image pins and the engine root expected by that owner.

Use complete prospective owner templates, retaining their full `CalibrationPlan`
payload and any required environment/resource fields. In a checkout containing
the matching issue-83 helper, its `--replay-same-C00-plan-shape` metadata supplies
those shapes. That selector is absent from current main; the standalone tests
use explicitly generated nonempirical templates instead. The builder requires the
maintained finite shape; it does not invent missing measurement or review facts.
Provide empty or absent `Files` and `EvidenceRoles`: the recipe derives them.
The retained template must contain the caller's actual `MappingReviewHash` and
`MappingReviewer`; the tool never assigns a reviewer. The versioned static
profile retains the outer field set from the matching issue-83 `ReadRetainedFinal`
reader. Source-parity checking is explicit when `ReplayCoverageRetained.cs` is
present and visibly skipped when that unfinished source is absent.

Path references inside JSON values are structural:

```json
{"$packagePath": "admissions/C00"}
```

```json
{"$inputPath": "managed-engine"}
```

A reference object contains exactly one key. Package paths use forward slashes
and cannot escape the new root. Ordinary strings and PowerShell source are never
rewritten. Known owner paths, runtime-map paths, time placeholders and retained
C00 dependency paths are derived from the fixed package layout.

A static role declaration copies explicitly named metadata:

```json
{"role": "outside-inventory-plan", "path": "admissions/C00/outside-inventory-plan.json", "source": "inventory-input.json"}
```

Omitting `source` declares an absent future dependency:

```json
{"role": "coverage-freeze", "path": "admissions/coverage-freeze.json"}
```

C00 requires the common admission roles, `run-instructions`, all outside roles
and `coverage-freeze`. Retained dependencies use the existing named C00 roles.
`run-instructions` copies Markdown bytes and must be static. The existing
`source-presentation-method` role is supported as static metadata only. Late
roles are restricted to the maintained release reader's vocabulary and ceiling
of 24. Static role paths and future role paths cannot alias each other.

The outside inventory plan, outstanding-write forecast and forward envelope
must be static C00 metadata. The builder updates the envelope's
`baselinePlanSha256` and `forecastSha256` from the final generated bytes.

## Verification and effects

The tool reads declared nonmedia metadata, source and executable bytes for
hashing. It prepares the ordinary `live` parent and the two metadata admission
directories. It leaves both live operation roots absent. It creates no human
authorization, independent review, readiness event, actual admission plan,
release, acceptance or media output. The manifest retains the existing
`issue83-two-confirmation-invariants-v1` kind and reports static HOLD status.

Limits follow the existing consumers: 512 KiB invariant manifest, 64 KiB
prospective templates (the release reader's stricter ceiling), 256 KiB helper
runtime map, 512 KiB controller runtime map and at most 1,024 runtime members.
`verify` requires the exact expected manifest hash and rechecks static pins,
role bindings, runtime members, envelope digests, directory prerequisites and
the absence of operational state.

A successful static verification is not native preflight, current host/volume
acceptance, review approval or permission to run. A matching issue-83 helper
is still required to establish complete resource-schema, exact runtime-closure,
environment, storage and operational acceptance. Current main does not contain
that unfinished helper behavior. Existing execution clocks and release semantics
remain unchanged. Dated artifact v4 packages include worker-ready-clock and
additional late-role amendments that this maintained profile does not silently
import; those packages cannot be relabelled as this profile.

## Focused checks

```powershell
python -B -m unittest discover -s tests -p test_confirmation_package.py -v
```

The checks use fresh ignored generated metadata and inert dummy executable
bytes. They cover dependent-hash drift, path and role errors, missing preparation
directories, future-state rejection, unchanged input identities and exact
consumer size boundaries. On Windows with PowerShell 7, two bounded
`-Template -Plan` interpreter calls read the emitted templates through the real
maintained owner (20seconds per call); these previews never launch the declared
helper or media executable.

The default standalone run visibly skips the C# source-parity check when the
matching issue-83 reader is absent and skips the helper-parser check unless
`CONFIRMATION_CHECKS_SDK` is explicitly set. These skips do not count as passed
integration checks. The remaining strict profile, role, identity and size checks
continue to run.

In a checkout with the matching issue-83 sources and a prebuilt Release
WorkerChecks helper, set `CONFIRMATION_CHECKS_SDK` to the approved dotnet host to
opt into the generated-envelope parser roundtrip. It has a 35-second outer
timeout and makes no native/media calls. Hosted CI opts in only when the matching
managed routes are present; on standalone main those routes remain explicitly
deferred. No runtime fallback or product-policy change supplies them.

Generated results establish no capture, source lineage, A/V timing, Saved latency
or hardware acceptance.
