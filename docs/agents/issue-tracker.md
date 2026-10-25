# Issue tracker: GitHub

Issues and specs live in GitHub Issues for `ju57/f10-recorder`.
Use the `gh` CLI from this repository.

## Conventions

- Create: `gh issue create --title "..." --body-file <path>`.
- Read: `gh issue view <number> --json number,title,body,labels,comments`.
- List: `gh issue list --state open --json number,title,body,labels,comments`.
  Add label and state filters as needed.
- Edit a body: `gh issue edit <number> --body-file <path>`.
- Comment: `gh issue comment <number> --body-file <path>`.
- Apply or remove labels: `gh issue edit <number> --add-label "..."` or
  `--remove-label "..."`.
- Close: `gh issue close <number>`.
- Create a child issue: `gh issue create --parent <number> ...`.
- Link an existing child: `gh issue edit <parent> --add-sub-issue <child>`.
- Record a dependency: `gh issue edit <number> --add-blocked-by <blocker>`.

For multiline content, write the exact text to a temporary file and use
`--body-file`. Use `docs/agents/triage-labels.md` for triage label names.

## PR and issue closeout

An issue records its accepted deliverable. Release gates and parent epics have
separate criteria and authorization; completing a task leaves those states
unchanged unless their own completion is established and authorized.

1. **Map acceptance before PR preparation or merge review.** Read every original
   issue referenced by the PR. Map each criterion to delivered behavior or
   documents, verification evidence and any remaining work. Record required task
   acceptance; evaluate the entire issue even when CI passes. Include this
   disposition in an implementation handoff.

2. **Choose the reference that matches the scope.**

   | Original issue outcome | PR body | After an authorized merge |
   | --- | --- | --- |
   | All criteria delivered, required acceptance recorded, completed by this merge | `Closes #123` for each completed issue | Verify closure and its completion evidence |
   | Partial delivery or pending issue acceptance | `Refs #123` | Keep open with remaining criteria, next owner or linked work |
   | User explicitly requests the issue stay open | `Refs #123` | Keep open and record the reason |

   Before merging, check the PR's closing-issue references and commit messages
   against this disposition; partial or explicitly kept-open issues must have no
   automatic closing reference.

   Preserve the original criteria. Record gaps as remaining work; moving or
   reducing that scope requires agreement. A research ticket requesting a draft
   can complete with the reviewed draft while external approval or operational
   integration remains pending in the appropriate gate or follow-up work.

3. **Respect the current action authorization.** A ready implementation or
   completed checklist does not authorize a merge. Once the user authorizes a
   merge, routine closeout of that PR's fully completed original issues is
   included, unless the user directs otherwise. Limit state changes to those
   scoped issues; keep explicitly open issues, release gates and unrelated work
   in their existing states.

4. **Verify after merge before declaring closeout complete.** Confirm the live
   merged commit and each original issue's state. Closing keywords may not fire
   for a PR targeting another branch; older PRs may have used only `Refs`. For a
   fully completed scoped issue still open, update its checklist and record
   criterion-to-evidence links, required acceptance, merged PR/commit and material
   limits, then close as completed:

   ```powershell
   gh issue close <number> --reason completed
   gh issue view <number> --json number,state,stateReason,closedAt
   ```

   For partial work, leave the issue open and record the gaps rather than marking
   the issue complete. Verify the resulting states, including any explicitly
   preserved release gates. If a closeout action fails or required acceptance is
   missing, report the pending disposition and reason. Correct an unexpected
   closure only within the agreed scope and authorization; otherwise report the
   discrepancy.

The final handoff reports the merged PR/commit, original issues closed or left
open with reasons, verification limits and the disposition of affected release
gates. A merged PR alone is not the closeout completion criterion.

## Skill instructions

When a skill says "publish to the issue tracker", create a GitHub issue.
When it says "fetch the relevant ticket", read the referenced GitHub issue.

## Pull requests as a triage surface

**PRs as a request surface: no.**
