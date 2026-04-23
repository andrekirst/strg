---
description: Review uncommitted or staged changes against strg coding standards using the review team
argument-hint: (no arguments)
---

# /review-pr

Spawn the review team (`.claude/agents/review-team.md`) against the current
diff to catch convention, security, and test-coverage issues before a merge.

## Usage

```
/review-pr
```

## Parameters

| Name       | Required | Description                                      |
|------------|----------|--------------------------------------------------|
| *(none)*   | —        | This command takes no arguments. It operates on  |
|            |          | the output of `git diff` and `git diff HEAD`.    |

## What This Does

Spawns — at minimum — a `code-reviewer` and a `pr-test-analyzer` teammate
(as defined in `.claude/agents/review-team.md`) to review all uncommitted
or staged changes against:

1. `CLAUDE.md` conventions.
2. Security checklist (path traversal, tenant isolation, auth enforcement).
3. Code style (forbidden patterns, naming conventions).
4. Test completeness (every issue `TC-xxx` has a test method).

## Steps

### Step 1: Get changed files

Run `git diff --name-only` and `git diff HEAD --name-only`. Abort if there
are no changes.

### Step 2: Read CLAUDE.md

Refresh understanding of all forbidden patterns and conventions.

### Step 3: Spawn the review team

Dispatch the two mandatory reviewers in parallel. Subagents do not share the
parent's context, so prompt templates must be **inlined**, not referenced.
Operational recipe per role:

1. Open `.claude/agents/review-team.md`.
2. Copy the "Prompt Template" block under the role's section verbatim.
3. Substitute `{ISSUE_ID}` with the issue this PR implements (derive from
   the branch name or PR title; if unknown, pass `unknown` and the reviewer
   will fall back to CLAUDE.md rules).
4. Substitute `{FILE_LIST}` with the output of
   `git diff --name-only HEAD` (newline-joined).
5. Invoke the Agent tool with `subagent_type: general-purpose` and the
   fully-substituted prompt as the `prompt` parameter.

Mandatory reviewers:
- `code-reviewer`  (prompt template in `review-team.md` → Agent: code-reviewer)
- `pr-test-analyzer` (prompt template → Agent: pr-test-analyzer)

Optional reviewers if the diff warrants:
- `type-design-analyzer` — if the diff adds or modifies public types.
- `comment-analyzer` — if the diff adds non-trivial comments or XML docs.

### Step 4: Security pass

Check each changed file for:

- Unvalidated user-supplied paths (missing `StoragePath.Parse()`).
- Tenant isolation bypass (`IgnoreQueryFilters()` outside documented
  carve-outs).
- Credentials or secrets in logs.
- Stack traces in error responses.
- `TenantId`, `ProviderConfig`, `StorageKey`, or `PasswordHash` in any API
  response.

### Step 5: Code-quality pass

Check for:

- Forbidden patterns listed in CLAUDE.md.
- Async/await correctness.
- Missing `CancellationToken` propagation.
- N+1 query patterns.

### Step 6: Test-coverage pass

For each changed production file, verify there is a corresponding test file
with tests for the changed functionality.

### Step 7: Report

Format findings as:

```
## Security Issues
❌ CRITICAL: [file:line] — [description]
⚠️ HIGH: [file:line] — [description]

## Code Quality Issues
⚠️ MEDIUM: [file:line] — [description]

## Missing Tests
⚠️ [file] — no test for [functionality]

## Summary
X critical, Y high, Z medium issues found.
```
