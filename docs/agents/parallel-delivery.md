# Parallel implementation and independent validation

Use this protocol when delegating implementation tickets or running parallel
delivery work. GitHub Issues remain the source of scope and dependencies.
Existing task authorization controls publication, live testing and merging.

## Admit a bounded slice

The orchestrator pins the issue/spec revision, current main commit, owned files
and interfaces, accepted test seams, observable acceptance cases, declared test
effects, artifact byte budget and completion conditions. Reuse seams already
agreed in the task; clarify genuinely new boundaries before writing tests.
Start only dependency-ready work. A dependency graph is insufficient when two
tickets modify the same worker/configuration contract: serialize that contract
or agree an interface first.

Each production writer owns an isolated branch/worktree. Assign separate output,
profile and evidence directories. A reviewer reads the frozen candidate; it does
not edit that candidate while verification is running. With four active agent
slots, use orchestrator, implementer, independent validator and reviewer; stage
the two review axes as slots become available. Additional writers require both
dependency independence and disjoint file/interface ownership.

## Produce evidence that can disagree with the implementation

The implementer works in observable behavior slices and runs the relevant checks.
The independent validator derives cases from the issue before reading the patch,
then reproduces selected checks against the exact candidate and adds at least
one independently chosen consequential case. Include a positive control and a
negative/sensitivity control where a suitable seam exists. Identify whether the
control changes a fixture or mutates production; they establish different things.
Expected outcomes come from the spec or independent examples, not a copy of the
implementation's algorithm. Two agents agreeing is not acceptance evidence.

Run standards and spec reviews separately, following Matt's code-review skill.
The standards review checks repository rules and preservation of existing fixes;
the spec review maps every criterion to implementation, evidence and remaining
limits. Another agent reviews a reviewer's own corrective patch. Resolve material
findings before advancing. Repeated execution is reproduction, not independent
test design; record which kind was performed.

## Freeze and bind the result

Freeze a clean candidate commit before final validation. Retain a compact receipt
with issue/spec revision, base/head SHAs, relevant source/binary/input/package
hashes, exact commands and arguments, expected outcomes, actual results, effects,
environment, artifact budget/usage, preservation and owned-process cleanup.
Store machine-specific receipts and media under ignored artifacts; publish a
portable criterion-to-evidence summary and truthful limits on the issue/PR.

Source changes invalidate affected build/test/review evidence. A documentation-only
followup may reuse unchanged production binaries when source hashes are checked
and that relationship is explicit; the resulting PR head still receives CI.
Historical native acceptance stays bound to its original commit. Decide targeted
retests from the changed behavior and risk; do not relabel old measurements as new.

## Serialize integration and shared hardware

One orchestrator integrates candidates with freshly fetched main, preserving
previously merged fixes. Verify main ancestry, inspect the combined diff, rerun
affected regressions and require CI on the resulting head. An old green head is
not approval of a conflict resolution. Preserve a recoverable pre-update ref;
use a lease for an authorized rewritten feature branch, never an unguarded force
push. Changes to main, merges, installation and release retain their task-specific
authorization boundaries.

Parallel portable checks and builds need separate output locations. One named
controller owns Windows desktop/GPU/live capture, recorder/NVIDIA state and
restoration during a trial. Other work must respect its resource reservation;
keep heavy decoding outside performance timing windows. Read each route's effects
and preview its plan before execution. Agent agreement and hosted CI do not
establish live hardware, fullscreen, audible A/V or shipping-package acceptance.

Keep compact receipts for rejected attempts and failures needed to reproduce
them. Apply the declared storage budget and provenance rules when retiring large
artifacts. After two fix/retest cycles without new evidence, stop blind retries,
report the unchanged blocker and switch to a diagnostic plan; independent work
may continue within its own scope.

## Advance without conflating states

For implementation handoffs, PR preparation and authorized merge closeout, follow
[the issue closeout workflow](issue-tracker.md#pr-and-issue-closeout).

The orchestrator reports implementation, independent validation, standards/spec
review, CI, user acceptance and release gates separately. Ready for integration
does not clear a release gate or authorize merging. Keep unresolved engineering
requirements and their owners visible. Current automation reuses repository test
routes and CI; introduce further scheduling or receipt tooling only as a scoped,
reviewed change rather than assuming it exists.
