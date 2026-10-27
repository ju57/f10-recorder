# Parallel implementation and independent validation

Use this protocol when delegating implementation tickets or running parallel
delivery work. GitHub Issues remain the source of scope and dependencies.
Existing task authorization controls publication, live testing and merging.

## Keep delivery focused on acceptance

These rules apply to serial and parallel delivery. The coordinating agent owns
the route from the original criteria to acceptance, including supporting work.

1. **Define completion before starting.** Name the exact original criterion,
   observable behavior, required checks and remaining manual acceptance. Keep
   the original requirements intact. A PR or passing test suite is an intermediate
   result; completion requires the criterion's evidence and required acceptance.
2. **Deliver supporting work with the behavior it enables.** Keep necessary
   helpers, adapters, tests and prefactoring inside that delivery. Before splitting
   out a PR, record the concrete reason, such as independently useful behavior or
   a safer staged migration, and the remaining route to the original criterion.
   Size alone does not justify a chain of prerequisite PRs.
3. **Bound investigations by a decision.** Before starting, record the question,
   time and resource limits, expected evidence and the implementation decision
   that evidence will enable. Stop when the question is answered or a limit is
   reached. Report the result, uncertainty and options before extending the
   investigation or building further supporting infrastructure.
4. **Replan when prerequisites multiply.** If one prerequisite reveals another,
   reassess the whole route before expanding dependent work. Record what remains,
   available simpler routes and the recommended next action. Obtain a user
   decision when the new route changes agreed scope, resource limits or action
   authorization. Independent work within the existing scope may continue.
5. **Report progress against criteria.** Each handoff and PR summary states the
   delivered behavior, evidence, exact acceptance gaps, and next action and owner.
   Distinguish implementation, technical verification and required user/native
   acceptance. Keep criteria pending until their required acceptance is recorded;
   test counts and merged PRs alone do not establish completion. Follow the
   [issue closeout workflow](issue-tracker.md#pr-and-issue-closeout) when publication
   or closeout is authorized.

Keep one feature handoff in its ignored evidence directory, with role-specific
briefs and links to earlier snapshots. Preserve the tested patch identity,
failures, verification limits and supporting evidence locally. Put portable
behavior and test instructions in the appropriate source documentation. When
publication is authorized, derive a concise PR/issue acceptance summary from the
handoff; machine-specific reports and raw evidence remain local. Later candidate
or merge updates get a linked snapshot or closeout record so earlier evidence
keeps its original identity. Handoff preparation authorizes no commit, push,
merge, installation or recording.

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

## Dispatch fresh handoffs

Use the [handoff template](delivery-handoff-template.md) for each new scope.
Keep the dispatch brief to about 500 words, with pointers to authoritative spec,
standards and evidence rather than copied transcripts. Include the decision the
role must make and its completion condition. This is a context budget, not a
limit on the referenced acceptance criteria or a reason to omit safety facts.

Start new subagents with `fork_turns: "none"`. Give validators the accepted spec
and interfaces before the patch; give reviewers the frozen diff and their axis.
Prepare independent cases while implementation proceeds. Create user-owned chats
only when the user requests them. An ongoing orchestrator reads the current
handoff and changed state; it does not need to reload its planning history.

Reuse an agent for a closely related corrective delta. For a new scope, start
fresh. If inherited context is necessary, record the specific needed material,
why a bounded pointer is insufficient, the bias risk and its mitigation; retain
independent case design and both review axes. Preserve required prior decisions
through the authoritative spec. A shorter prompt in an old chat is not a fresh
context. A reviewer never approves its own corrective patch.

## Produce evidence that can disagree with the implementation

At admission, select one early tracer through the actual changed integration
boundary and its consumer, using an existing agreed seam. State the expected
observation, a consequential failure/sensitivity case and declared effects. For
example, profile transport needs a real isolated paused-worker reply consumed
by the client/editor; a size check on a saved file alone does not exercise it.
A launcher needs the actual wrapper's executable/argument admission path.

Run that tracer before expanding the implementation or its broad test matrix.
Use the least-effect route that exercises the boundary. If it needs unavailable
resources or undeclared live effects, record the missing proof and obtain the
required authorization; independent work may continue, but dependent integration
and acceptance remain pending. Mock checks do not replace that proof. For a
documentation-only slice, use a filled handoff and a consequential tabletop
counterexample; label these as document checks, not runtime enforcement.

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
Inventory relevant untracked sources and inputs as well as tracked files.
When commits are not authorized, review an immutable patch against a pinned base,
including untracked additions and a source hash manifest. Bind local evidence to
that snapshot; integration still requires a clean committed candidate and CI.
An ordinary dirty checkout or unchanged HEAD alone is not a frozen identity.

For each candidate delta, record an evidence decision in the handoff: reuse,
targeted refresh or invalidated, with a reason and the affected criteria. Check
relevant source (including additions/deletions and untracked files), binaries,
build/package/toolchain, inputs, configuration, commands and environment against
the original receipt. Missing identity or evidence makes reuse unestablished.
Changed behavior or any relevant dependency invalidates affected evidence;
refresh that coverage before advancing, retaining superseded/failed attempts.

A documentation-only followup may reuse unchanged production evidence when the
source/binary relationship and applicability still match. Review the document
delta separately. On an unchanged reviewed candidate, merge readiness refreshes
head/base, integration, required CI, conversations and acceptance disposition;
cite both axis reports instead of commissioning two full reviews again without
a concrete new risk. If main changes, assess the combined diff and dependencies
before choosing affected retests/reviews. Every resulting PR head still receives
required CI. Historical native acceptance keeps its original revision and scope;
reuse never upgrades it to new hardware, package, user or release acceptance.

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
