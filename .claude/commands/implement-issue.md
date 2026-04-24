---
description: Implement a strg issue end-to-end from a GitHub issue using the feature-dev team workflow
argument-hint: <ISSUE>  e.g. 57, #57, or https://github.com/andrekirst/strg/issues/57
---

# /implement-issue

Implement a strg issue end-to-end from a **GitHub issue**, by driving the
feature-dev team workflow defined in `.claude/agents/feature-dev-team.md`.

## Usage

```
/implement-issue 57
/implement-issue #57
/implement-issue https://github.com/andrekirst/strg/issues/57
```

## Parameters

| Name | Required | Format                                                          | Description                                                                                              |
|------|----------|-----------------------------------------------------------------|----------------------------------------------------------------------------------------------------------|
| `$1` | yes      | `^(#?\d+\|https://github\.com/[^/]+/[^/]+/issues/\d+)$`         | GitHub issue reference. The command MUST reject any argument that does not match before invoking `gh`.   |

## What This Does

1. **Validates** `$1` against the regex above. If it does not match, abort
   with an error before invoking `gh`.
2. **Extracts** the numeric issue number (strip `#`, or take the trailing
   path segment of a URL).
3. **Verifies the repo**: `gh` infers the repository from the current working
   directory's git remote. If `$1` is a URL, its `<owner>/<repo>` segment
   MUST match the inferred repo — otherwise abort. This command does not
   run cross-repo.
4. **Fetches the issue** via `gh issue view`. Aborts if the issue is missing
   or closed (unless the caller explicitly confirms re-implementation).
5. **Explores** the codebase to understand existing patterns (code-explorer
   role, `subagent_type: Explore`).
6. **Plans** the implementation (code-architect role, `subagent_type: Plan`).
   The parent Claude keeps the returned plan in its own context and embeds
   it verbatim into the implementer's prompt — subagents do not share state.
7. **Implements** all acceptance criteria (code-implementer role,
   `subagent_type: general-purpose`).
8. **Writes tests** for every `TC-xxx` listed in the issue body.
9. **Self-reviews** (impl-self-reviewer role) against the Security Review
   and Code Review checklists embedded in the issue body.
10. **Closes the GitHub issue** only after explicit human confirmation —
    see Step 8 for the gating rules. Auto-close is forbidden.

## Relationship to `feature-dev-team.md`

The team file's prompt templates currently reference local issue paths
(`docs/issues/{prefix}/{ISSUE_ID}-*.md`). For this command, the **GitHub
issue body is the source of truth** and is embedded verbatim into each
subagent prompt — the team file's path reference is superseded for this
caller. The team file is otherwise untouched: roles, `subagent_type`
mapping, expected outputs, and the architecture/security guardrails still
apply. If the issue body links to a tracking file under `docs/issues/...`,
do NOT chase the link — the GitHub body is authoritative here.

## Steps

### Step 0: Validate the argument

Reject `$ARGUMENTS` if it does not match
`^(#?\d+|https://github\.com/[^/]+/[^/]+/issues/\d+)$`. No `gh` call, no
shell access, until this check passes.

**Enforcement honesty**: this regex is implemented as a prompt instruction,
not a harness-level sandbox. `argument-hint` in the frontmatter is purely
display. If stricter enforcement is needed later, add a `PreToolUse` hook
in `.claude/settings.json` that blocks `Bash(gh ...)` invocations when
`$ARGUMENTS` fails the regex.

### Step 1: Extract the issue number and verify the repo

- If `$ARGUMENTS` is `123` or `#123`, set `ISSUE_NUMBER = 123`.
- If `$ARGUMENTS` is `https://github.com/<owner>/<repo>/issues/<n>`:
  - Set `ISSUE_NUMBER = <n>`.
  - Run `gh repo view --json nameWithOwner -q .nameWithOwner` and require
    its output to equal `<owner>/<repo>`. On mismatch, abort with a clear
    error message naming both repos. This command does not run cross-repo.

### Step 2: Fetch the issue

```
gh issue view $ISSUE_NUMBER --json number,title,body,state,labels,milestone,assignees,url
```

- If the issue does not exist, abort.
- If `state == "CLOSED"`, abort unless the caller explicitly confirms
  re-implementation in their next message.
- The fetched `.body` is authoritative for: Summary, Technical
  Specification, Acceptance Criteria, Test Cases, Implementation Tasks,
  Security Review Checklist, Code Review Checklist, and any
  `Depends on:` / `Blocks:` cross-references.

### Step 3: Explore dependencies

For each issue reference in the body's `Depends on:` / `depends_on:` line
(e.g. `STRG-403 (#56)`, `#56`, or `STRG-403`), read the relevant source
files to understand the interfaces and patterns used. Use the `Explore`
subagent for anything wider than a handful of files. **Embed the issue
body verbatim** in the explorer's prompt instead of pointing it at a path.

### Step 4: Plan the implementation

Produce a numbered plan that lists every file to create or edit. Use the
`Plan` subagent. **Embed the issue body verbatim** in the architect's
prompt. Pass the explorer's report through verbatim too — subagents do
not share state.

### Step 5: Implement

Create or modify the files listed in the issue's Implementation Tasks
section. Follow all patterns described in `CLAUDE.md`. The implementer's
prompt embeds the issue body verbatim AND the architect's plan verbatim.

### Step 6: Write tests

Create test files covering every `TC-xxx` in the issue body. Run
`dotnet test`.

### Step 7: Self-review

Walk through each item in the Security Review Checklist and Code Review
Checklist in the issue body. Fix any violations before reporting completion.

### Step 8: Close the issue (gated — requires explicit user confirmation)

Closing or commenting on a GitHub issue is a shared-state action visible to
subscribers. The command MUST NOT auto-close, even when every gate is
green. The flow is:

1. Print a final report containing:
   - Files created / modified (with paths).
   - Tests added (with method names).
   - Each acceptance-criterion checkbox with evidence (`file:line` or test
     method name).
   - Each Security Review / Code Review checklist item with ✅ / ⚠️ / ❌.
2. Print the gate verdict — **green only when ALL of the following pass**:
   1. `dotnet test` is green (no failing tests).
   2. Every acceptance-criterion checkbox has concrete evidence.
   3. Security Review and Code Review checklists are all ✅.
   4. Either `/review-pr` was run and reported no CRITICAL / HIGH issues,
      OR the human caller explicitly states self-review is sufficient for
      this issue.
3. If the gate is green, **ask the user** whether to close the issue. On
   explicit confirmation only, run:
   ```
   gh issue close $ISSUE_NUMBER --comment "<implementation summary; commit SHA(s) or PR link>"
   ```
4. If the gate fails, do NOT close. Optionally offer to post a progress
   comment via `gh issue comment $ISSUE_NUMBER --body "..."` — also
   requires explicit user confirmation. Leave the issue OPEN with a short
   note explaining what still has to land.

Do NOT auto-close on self-review alone — the implementer is not an
independent reviewer.

### Step 9: Report

Summarise what was created/modified and confirm each acceptance criterion
is met, with references to the evidence. Include the GitHub issue URL and
state at the end of the report so the caller can navigate quickly.
