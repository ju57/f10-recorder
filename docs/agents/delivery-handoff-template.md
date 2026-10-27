# Delivery handoff template

Copy the applicable fields into the slice's ignored evidence directory. Keep one
current packet, link earlier versions, and send only the role-specific brief and
packet path/hash. Use N/A with a reason for inapplicable fields; pending facts
need an owner and next action. This is a manual protocol, not a scheduler or an
automatic verifier. Follow the [delivery protocol](parallel-delivery.md).

## Admission and dispatch

- Scope: issue/spec URL or user request, frozen revision/content hash; accepted
  behavior increment and unchanged original criteria still pending.
- Baseline: integration ref/SHA, branch/worktree and dependencies; owned files,
  shared interfaces and resource reservations.
- Role: implementer, independent validator, Standards or Spec reviewer; exact
  question, required result and completion condition.
- Context: fresh subagent (`fork_turns: "none"`), related corrective continuation,
  or justified exception (needed material, necessity, bias risk, mitigation).
- Read: authoritative spec/standards and relevant interface pointers. Give the
  validator these before patch inspection; retain its independently frozen cases.
- Effects and authority: allowed checks/resources, profiles/output roots, byte
  budget, cleanup/preservation, and actions requiring further authorization.
- Early tracer: actual boundary/consumer, command or document exercise, expected
  observation and consequential failure case; authorized effects, result/receipt,
  or missing proof with owner/next action. Mark mocked/tabletop evidence explicitly.

## Candidate and evidence decisions

- Identity: base/head, clean state; or authorized-review patch hash plus complete
  tracked/untracked addition/deletion inventory and source hash manifest. Link
  binary/build/package, toolchain, input/configuration and environment identities.
- Delta: previous receipt/candidate and exact changed files/interfaces/behavior,
  including integration-base changes. Record identity checks and absent inputs.

| Criterion/check | Original receipt and identity | Decision and reason | Current proof or pending owner/action |
| --- | --- | --- | --- |
| Fill one row per affected criterion/evidence set | Original source/binary/input/environment binding | Reuse, targeted refresh or invalidated | Matching identities and applicability, new receipt, or explicit gap |

Link exact commands, expected/actual results and retained failures rather than
duplicating full logs. A matching hash alone does not establish applicability.

## Handoff result

- Implementation: delivered behavior and remaining original criteria.
- Independent validation: matrix frozen before patch inspection, consequential
  independently chosen case, positive/negative controls, results and limits;
  distinguish reproduction from independent test design and fixture mutation from
  production mutation.
- Standards: report/candidate identity, findings and disposition.
- Spec: separate report/candidate identity, criterion coverage, findings and limits.
- CI: exact head and required results, or pending publication; local checks are
  not hosted CI. Evidence reuse preserves both review axes.
- Preservation: retained bytes, failed attempts and owned-process cleanup, or N/A.
- Acceptance: user/native/package qualifications and every release gate remain
  pending/open unless their own criteria are independently established and their
  closeout is separately authorized.
- Next: exact permitted action and owner; publication, merge, install and release
  retain their existing authorization boundaries. Link the previous packet if
  this handoff supersedes one.
