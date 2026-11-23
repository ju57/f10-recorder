# Replay-save validation contract for issue 83

The maintainer approved fast normal checks with full fresh decoding retained for
qualification and explicit diagnostics on 2026-11-02. Subsequent direction keeps
F10's existing engine and prioritizes fast saves and low resource use on Windows
and Linux. This document records that agreed development contract; it does not
establish qualification, installation, release readiness or issue acceptance.

This is the owning replay-validation amendment linked from the
[beta specification](beta-spec.md#document-responsibility-and-approved-amendments).
Use the [current issue](https://github.com/ju57/f10-recorder/issues/83) for live
acceptance state; dated reconciliation and finite-authorization notes below
record their original scope and do not grant new execution authorization.

## Checks by path

| Path | Required work |
| --- | --- |
| Normal packet export | Bind independently established source packet/configuration/timing expectations before muxing; check the complete closed MP4's structure, packet counts, timing/endpoints and coded-source integrity; retain the checked object's identity and write protection through final publication. |
| Exact fallback | Retain its existing fresh full-output decode and timing/count checks before publication. An export process exit alone is insufficient. |
| Qualification | Independently fully decode outputs and check expected source content/lineage, A/V clock and endpoints. Exercise corruption/truncation/timing-drift controls and the actual worker path. |
| Explicit diagnostics | Additional deep decoding, detailed tracing and profiling can be offered separately. A diagnostic option does not disable required normal-save protections. No new diagnostics UI/toggle is implemented by issue 83's checking optimization. |

The normal packet gate does **not** establish that every compressed picture was
freshly decoded. Existing experiments found damaged picture data that lightweight
checks accepted and a full decoder rejected. This is an explicitly accepted
assurance change, not equivalent decoder sensitivity. Corruption controls must
state which damage they inject and which path must reject it; passing one class
does not establish detection of every malformed codec payload.

## Runtime protections retained

Each accepted save retains its exact requested source interval, complete
independently decodable inputs, input ownership before pruning, truthful warm-up,
shared A/V clock, output identity and producer/retirement ownership. Required
capacity/growth/reservation observations remain fresh and fail closed when
uncertain. Publication of the checked file remains the success point. A failure
before publication cannot report Saved; cleanup diagnostics cannot certify an
unknown producer exit.

Reducing repeated work must preserve these outcomes and existing alias,
replacement, growth, corrupt/truncated-output and timing-drift rejection controls.
A protected handle alone does not prove that the surrounding namespace cannot
change. Neither cached diagnostic bytes nor a reset timestamp supplies new
capacity evidence. The historical comparison baseline used 100 ms observation
scheduling and 500 ms maximum freshness. The current candidate uses a qualified
2,000 ms coverage horizon, backed by fresh catalog-bound poll/stop growth and
full outstanding reservations. This policy requires its own exact-candidate
qualification; the old 500 ms model cannot supply the longer cover.

On 2026-11-05 the maintainer authorized developing and testing a narrower
namespace-observation contract. Cache-wide namespace checks may run on the
existing 100 ms schedule and at forced transitions. This can miss a temporary
junction or reparse fault elsewhere in the cache that appears and disappears
between scans. That detection gap is accepted for the prototype; it is not
equivalent to inspecting the whole cache on every callback.

The prototype must still inspect main and temporary output identity, links and
growth on every callback. The applicable qualified fail-closed horizon includes
checking work. Only a genuine capacity observation supplies a new evidence epoch;
skipped, busy or coalesced checks cannot renew it. The separate completed-work
clock limits repeated full checks to the 100 ms schedule, while dirty/forced
transitions and consumption near evidence expiry remain immediate.
Opening inputs, creating links, pruning,
cleanup and publication must each remain safe against redirected paths. A later
healthy scan cannot repair an operation performed on the wrong object. Unknown
or unsupported ownership retains the conservative checking path. Source-content
and timing validation, and publication of the checked object, remain mandatory.

During full fallback decoding, the immutable decoder reader and its no-delete
pathname pin may supply fresh reservation metadata for that exact request.
This temporary binding grants no validation or publication rights. Each matching
observation queries the held handle again and checks the native output identity,
extent and links. Before binding, after revocation or for unsupported ownership,
the conservative path remains active. Revocation must finish under the observation
lock before any concurrent disposer can close the pin. Unknown decoder closure
revokes borrowing while retaining its physical input and uncertain request owners.

This authorization covers implementation and verification. Adoption requires
tests of both the accepted detection gap and attempts to exploit it for actual
file operations, followed by meaningful whole-save improvement measured with
checking and cleanup included. Isolated native-operation tests alone do not
establish a safe integrated save path or the latency target.

Qualification fixtures, injected faults, independent observers and repeated
benchmarks belong in test helpers. Their costs must be measured, and they must
not hide work after Saved or change ordinary measurement ownership. Full save
latency includes close, finalization, validation, publication, retirement and
observable event delivery. Report queue waiting separately. Account for work
moved into capture, including CPU/GPU, RAM, allocations and disk I/O.

## Acceptance reconciliation

The maintainer-authorized reconciliation was published to issue 83 on
11 November 2026 at 21:49:24 UTC. The exact reviewed body was verified at
21:49:26 UTC: only criterion 4 changed, all seven criteria remain unchecked,
and the issue remains open. The original wording below is retained as history.

**Before — exact historical criterion:**

> Preserve full output decode and timing/count validation before Saved, alongside the independent source-content/lineage checks used to qualify accuracy. Corrupt/truncated output and cumulative timing-drift sensitivity controls must still reject publication. Any accelerated or restructured validator must establish equivalent checks and failure sensitivity before replacing the current path.

**After — exact published criterion:**

> Before Saved, require the complete normal packet gate (pre-mux binding of independently established source packet/configuration/timing expectations; complete closed-MP4 structure, packet counts, timing/endpoints and coded-source integrity checks; checked-file identity and write protection through final publication) and the existing fresh full-output-decode and timing/count exact-fallback gate. Retain full fresh output decoding and independent source-content/lineage, A/V clock and endpoint checks in qualification. Corrupt/truncated output and cumulative timing-drift controls must reject publication according to the declared normal/fallback validation contract in `docs/replay-save-validation.md`; controls must state the injected damage and which path must reject it. Document the known loss of fresh-every-picture semantic decoding on normal packet saves, including malformed compressed-picture data that the normal gate may accept and a full decoder rejects; do not claim equivalent sensitivity for that class of corruption.

The replacement expands the existing Checks by path table into a self-contained
issue criterion. The same pre-mux expectations, complete closed-file gate and
checked-file publication protection remain mandatory. Exact fallback keeps
fresh full decoding before Saved; qualification keeps full decoding plus
independent expected source-content/lineage, A/V-clock and endpoint checks.
Qualification or later diagnostics do not become per-save semantic decoding.

The accepted limitation remains explicit: a normal packet save can pass its
structure, timing and coded-source-integrity checks even though malformed
compressed-picture data would fail a fresh decoder. Existing corruption,
truncation and cumulative-drift rejection controls remain required for their
declared paths. Each control must identify the injected damage and expected
rejecting path; success for one fault class does not establish equivalent
fresh-every-picture semantic failure sensitivity.

The publication changed no other criterion, checkbox, ownership or issue-body
text. The public issue remains open. All other issue 83
criteria remain: at least 20 matched reference exports, median <=3 seconds/p95
<=5 seconds through observable Saved, exact one-frame boundaries and 1/5/30-minute
coverage, warm-up/overlap/continued capture, physical feedback, resources,
exact-candidate qualification, independent review and maintainer acceptance.
Issue 15's queue and installation/release remain separate.

### Evidence and remaining acceptance

The maintainer recorded the implementation and integrated-product scopes on
23 November 2026. [Issue 83](https://github.com/ju57/f10-recorder/issues/83)
accepts the twenty-save measurement on `69b5f50` (2.670 s median, 3.024 s p95)
and the [private five-minute storage exception](storage-admission.md#private-five-minute-receipt)
on its verified consumer. [Issue 53](https://github.com/ju57/f10-recorder/issues/53)
owns ordinary one- and thirty-minute live admission, two advancing overlapping
saves per duration, and the integrated product's five-minute latency and
physical feedback. [Issue 55](https://github.com/ju57/f10-recorder/issues/55)
owns the concurrent-save CPU follow-up. Source integration and maintainer
acceptance remain part of issue 83.

Existing finite qualification, failure controls and worker receipts remain
usable only for the exact product, recipe, destination and workload they
actually establish. Reconciliation neither discards valid evidence nor marks
the revised criterion or the issue complete. Before acceptance, map applicable
retained evidence to each criterion and record the remaining gaps:

| Criterion | Evidence or disposition still required for acceptance |
| --- | --- |
| 1 — Reference latency | At least 20 prospectively matched cold/warm reference exports with the frozen identities/order/quantile method, median <=3 seconds and p95 <=5 seconds. Finite pilot means do not establish these percentiles. |
| 2 — Observable completion and feedback | Whole request-through-validated-publication/observable-Saved stage accounting and separate queue waiting; physical request-to-feedback <=200 ms remains required by issue 53. Headless client observations do not establish rendered physical feedback. |
| 3 — Accuracy and history | Applicable source-boundary, minimum-duration, 1/5/30-minute, warm-up, overlapping-history and common-clock/complete-input evidence; retain failure/cancel/retirement coverage where required by the coordinated qualification. |
| 4 — Revised validation contract | Map existing normal packet, full-decode fallback and full-decode/independent-lineage qualification results and fault controls to the selected final tuple. Keep structural/source-integrity/truncation/drift rejection separate from the accepted semantic decoder-detection gap. Reuse applicable controls and identify missing coverage without an equivalence claim. |
| 5 — Continuity and resources | Continued capture/accepted-save ownership and storage/producer/retirement protections, with worker/UI/engine/export CPU, GPU, RAM and I/O evidence. Partial CPU or logical-I/O counters do not replace issue 55's resource qualification. |
| 6 — Baseline and finite testing | Retain the reusable actual-path baseline, all failed/slow pilot observations, declared finite limits, pre-native independent review and installed/settings/startup/NVIDIA preservation. These prerequisites do not substitute for the remaining acceptance batch. |
| 7 — Exact qualification and acceptance | Exact selected binary/recipe/catalog applicability, resolved independent implementation/result review findings and maintainer acceptance; preserve provenance/availability when obsolete generated media is retired. |

No new validation-design decision is requested. Publication is complete. A
subsequent explicit authorization permitted one bounded retained-input protection
diagnostic against the frozen qualified reference, with helper-only changes
and independent plan/result review. That finite collection completed and its
limited attribution remains separate from issue acceptance. OBS research
informed the diagnostic. Product optimization, index reuse, capture, further worker pilots and the
20-export acceptance batch remain outside its scope. Research completion or a
timer does not authorize expansion.

## Coordination with issue 15

The future queue must preserve each accepted request's source endpoint and
protected-input ownership while waiting. Keep request identity and the timings
for request, acceptance, export start, publication and observable Saved distinct;
measure queue waiting separately. Transfer ownership explicitly when work starts,
and retain it through validation, publication or a joined failure cleanup.
Completion-ready inside the exporter is insufficient to measure observable Saved.
These are interface constraints for issue 15; this branch adds no waiting queue.
Decoder metadata borrowing is single-use and local to the active request; a
queued request must acquire its own owner on its serialized export thread.
Borrowing must never become a queue admission, validated-output or Saved signal.
