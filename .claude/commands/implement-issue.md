---
description: Implement a strg issue end-to-end using the feature-dev team workflow
argument-hint: <ISSUE_ID>  e.g. STRG-034 or CC-003
---

# /implement-issue

Implement a strg issue end-to-end by driving the feature-dev team workflow
defined in `.claude/agents/feature-dev-team.md`.

## Usage

```
/implement-issue STRG-034
/implement-issue CC-003
```

## Parameters

| Name       | Required | Format                | Description                         |
|------------|----------|-----------------------|-------------------------------------|
| `$1`       | yes      | `^(STRG\|CC)-\d{3}$`  | Issue identifier. The command MUST  |
|            |          |                       | reject any argument that does not   |
|            |          |                       | match this regex before touching    |
|            |          |                       | the filesystem (path-traversal      |
|            |          |                       | guard — an argument like            |
|            |          |                       | `../../etc/passwd` must be          |
|            |          |                       | rejected outright).                 |

## What This Does

1. **Validates** `$1` against `^(STRG|CC)-\d{3}$`. If it does not match,
   abort with an error before reading any file.
2. **Resolves the prefix**: if `$1` starts with `STRG-`, the issue is at
   `docs/issues/strg/$1-*.md`; if it starts with `CC-`, it is at
   `docs/issues/cc/$1-*.md`.
3. **Reads** the resolved issue file.
4. **Explores** the codebase to understand existing patterns (code-explorer
   role, `subagent_type: Explore`).
5. **Plans** the implementation (code-architect role, `subagent_type: Plan`).
   The parent Claude keeps the returned plan in its own context and embeds
   it verbatim into the implementer's prompt — subagents do not share state.
6. **Implements** all acceptance criteria (code-implementer role,
   `subagent_type: general-purpose`).
7. **Writes tests** for every `TC-xxx` listed in the issue.
8. **Self-reviews** (impl-self-reviewer role) against the Security Review
   and Code Review checklists.
9. **Updates** the issue frontmatter — see Step 7 below for the gating
   rules; `status: done` is NOT flipped automatically on self-review alone.

## Steps

### Step 0: Validate the argument

Reject `$ARGUMENTS` if it does not match `^(STRG|CC)-\d{3}$`. No globbing,
no filesystem access, until this check passes.

**Enforcement honesty**: this regex is implemented as a prompt instruction,
not a harness-level sandbox. `argument-hint` in the frontmatter is purely
display. If stricter enforcement is needed later, add a `PreToolUse` hook
in `.claude/settings.json` that blocks `Read`/`Glob` on `docs/issues/**`
when `$ARGUMENTS` fails the regex.

### Step 1: Resolve the prefix and read the issue

- If `$ARGUMENTS` starts with `STRG-`, set `prefix = strg`.
- If `$ARGUMENTS` starts with `CC-`, set `prefix = cc`.
- Read the issue file at `docs/issues/<prefix>/$ARGUMENTS-*.md` — e.g.
  `/implement-issue CC-003` → `docs/issues/cc/CC-003-*.md`.

### Step 2: Explore dependencies

For each issue in the `depends_on` frontmatter, read the relevant source
files to understand the interfaces and patterns used. Use the `Explore`
subagent for anything wider than a handful of files.

### Step 3: Plan the implementation

Produce a numbered plan that lists every file to create or edit. Use the
`Plan` subagent.

### Step 4: Implement

Create or modify the files listed in the issue's Implementation Tasks
section. Follow all patterns described in `CLAUDE.md`.

### Step 5: Write tests

Create test files covering every `TC-xxx` in the issue. Run `dotnet test`.

### Step 6: Self-review

Walk through each item in the Security Review Checklist and Code Review
Checklist in the issue. Fix any violations before reporting completion.

### Step 7: Close the issue (gated)

Update the issue file — but `status: done` is flipped only when ALL of the
following gates pass:

1. `dotnet test` is green (no failing tests).
2. Every acceptance-criterion checkbox has evidence (`file:line` or test
   method name) in the final report.
3. The Security Review and Code Review checklists are all ✅.
4. Either `/review-pr` was run and reported no CRITICAL / HIGH issues, OR
   the human caller explicitly confirms self-review is sufficient for this
   issue (set in the issue PR description — do not assume).

If any gate fails, tick what is actually satisfied and leave `status: open`
with a short note explaining what remains. Do NOT auto-close on self-review
alone — the implementer is not an independent reviewer.

### Step 8: Report

Summarise what was created/modified and confirm each acceptance criterion is
met, with a reference to the evidence.
