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

## Skill instructions

When a skill says "publish to the issue tracker", create a GitHub issue.
When it says "fetch the relevant ticket", read the referenced GitHub issue.

## Pull requests as a triage surface

**PRs as a request surface: no.**
