---
description: Implement multiple strg issues end-to-end in parallel via the multi-issue agent team
argument-hint: <ISSUE> [<ISSUE> ...]  e.g. 57 #58 https://github.com/andrekirst/strg/issues/60
---

# /implement-issues

Implement a list of strg issues end-to-end **in parallel** by driving the
multi-issue team workflow defined in `.claude/agents/multi-issue-team.md`.
Each issue lands in its own git worktree, on its own branch, opens its own
PR, and is closed only after the all-checks-green gate plus explicit human
confirmation (two-tier batch prompt).

## Usage

```
/implement-issues 57 #58 https://github.com/andrekirst/strg/issues/60
/implement-issues 57           # N=1 is allowed; runs the full Analyst+Coordinator flow
```

## Parameters

| Name        | Required | Format                                                                | Description                                                                                                       |
|-------------|----------|-----------------------------------------------------------------------|-------------------------------------------------------------------------------------------------------------------|
| `$1...$N`   | yes (≥1) | each token: `^(#?\d+\|https://github\.com/[^/]+/[^/]+/issues/\d+)$`   | Space-separated GitHub issue references. Each token validated independently. Whole batch rejected on any mismatch. |

Practical guidance: 5–10 issues per call. Larger batches inflate parent
context, reduce hint-routing precision, and stress the gate-summary
output; the command does not enforce a hard cap, but the user is the
backstop.

## What This Does

1. **Validates** every token against the regex. Whole batch rejected on
   first mismatch — no `gh` invocations until validation passes.
2. **Verifies the repo**: for any URL token, `<owner>/<repo>` MUST match
   the local `gh repo view --json nameWithOwner` output. Cross-repo runs
   are aborted.
3. **Fetches each issue** via `gh issue view`. Aborts on missing or
   `state == "CLOSED"` issues unless explicitly confirmed.
4. **Spawns a Pre-Flight Analyst** (`subagent_type: Explore`) once. The
   Analyst reads all N issue bodies and emits a structured dispatch map:
   per-issue file-touch list, depends_on edges, file-overlap matrix, risk
   tags, and ready-to-embed cross-track hint paragraphs.
5. **Spawns a Coordinator** (`subagent_type: general-purpose`,
   `name: "coordinator"`) that lives for the whole batch. The Coordinator
   receives the Analyst's map verbatim and is responsible for the rest of
   the dispatch.
6. **Coordinator dispatches N parallel pipelines** (one per issue), each
   in its own git worktree on its own branch, each running the full
   feature-dev-team flow internally (explore → plan → implement → test →
   self-review). Pipelines open their own PRs on success.
7. **Coordinator routes hints** from the Analyst map and live signals via
   `SendMessage` to specific named pipelines mid-flight.
8. **Tiered failure handling**: independent failures don't stop sibling
   pipelines. Pipelines flagged as dependents of a failed predecessor
   (per the Analyst's depends_on edges) are short-circuited and reported
   as SKIPPED.
9. **Aggregates and reports.** Coordinator returns a structured per-issue
   verdict to the parent. Parent prints the batch summary.
10. **Two-tier close-gate**: parent prompts the user with all green
    pipelines listed. Default action closes all green; user can opt out
    per issue. Failed/skipped pipelines are NEVER auto-closed.

## Relationship to `/implement-issue` and team files

- This command does NOT delegate to `/implement-issue` even when N=1. The
  same code path is used to keep behaviour predictable and testable.
- Per-pipeline roles (code-explorer, code-architect, code-implementer,
  impl-self-reviewer) are defined in `.claude/agents/feature-dev-team.md`.
  The new team file `.claude/agents/multi-issue-team.md` references that
  file for those roles instead of duplicating them. Only the new roles
  (Analyst, Coordinator) are defined in full in the new team file.
- Each pipeline embeds its issue body verbatim — the team file's
  `docs/issues/{prefix}/{ISSUE_ID}-*.md` reference is superseded for this
  caller, exactly as in `/implement-issue`.

## Steps

### Step 0: Validate the arguments

Reject `$ARGUMENTS` if any token does not match
`^(#?\d+|https://github\.com/[^/]+/[^/]+/issues/\d+)$`. Tokens are split
on whitespace. No `gh`, no shell access, until every token passes.
Required: at least one token.

**Enforcement honesty**: this regex is a prompt-level convention, not a
harness sandbox. `argument-hint` in the frontmatter is purely display.
If stricter enforcement is needed later, add a `PreToolUse` hook in
`.claude/settings.json` that blocks `Bash(gh ...)` invocations when any
token in `$ARGUMENTS` fails the regex. The same hook structure documented
in `/implement-issue` applies — extend the regex to handle multi-token
input.

### Step 1: Extract issue numbers and verify repo

For each token:
- If `<n>` or `#<n>`, set `ISSUE_NUMBER = <n>`.
- If `https://github.com/<owner>/<repo>/issues/<n>`:
  - Set `ISSUE_NUMBER = <n>`.
  - Run `gh repo view --json nameWithOwner -q .nameWithOwner` ONCE per
    invocation (cache the result). Every URL token's `<owner>/<repo>`
    MUST equal that value. Abort on the first mismatch with a clear
    error naming both repos. This command does not run cross-repo.

### Step 2: Fetch all issues

For each issue:

```
gh issue view $ISSUE_NUMBER --json number,title,body,state,labels,milestone,assignees,url
```

If any issue is missing, abort. If any has `state == "CLOSED"`, abort
unless the caller explicitly confirms re-implementation in their next
message. The fetched `.body` is authoritative for each issue's Summary,
Technical Specification, Acceptance Criteria, Test Cases, Implementation
Tasks, Security Review Checklist, Code Review Checklist, and any
`Depends on:` / `Blocks:` cross-references.

### Step 3: Spawn the Pre-Flight Analyst

Embed all N issue bodies verbatim in the Analyst's prompt. Subagent type
is `Explore`. The expected output schema is documented in
`.claude/agents/multi-issue-team.md` → `Agent: analyst`. Wait for the
Analyst's report before proceeding — every downstream step depends on
its dispatch map.

### Step 4: Spawn the Coordinator

Spawn the Coordinator with `subagent_type: general-purpose` and
`name: "coordinator"`. Embed in its prompt:
- The Analyst's dispatch map verbatim.
- All N issue bodies verbatim.
- The list of issue numbers being processed.
- A reference to `.claude/agents/multi-issue-team.md` and
  `.claude/agents/feature-dev-team.md` so the Coordinator can build each
  pipeline's prompt from the per-pipeline role templates.

The Coordinator is responsible for everything from Step 5 onward, up to
returning the aggregated batch report. The parent stays passive (apart
from receiving the Coordinator's final return value) until the
Coordinator returns.

### Step 5: Coordinator dispatches pipelines

(Performed by the Coordinator; documented here so the user understands
what's happening.)

The Coordinator emits N parallel `Agent` calls in a single message, each
spawning a pipeline subagent named `pipeline-<ISSUE_NUMBER>` with
`run_in_background: true` and `isolation: "worktree"`. Each pipeline
prompt embeds:
- The pipeline's GitHub issue body verbatim.
- The Analyst's hint paragraphs targeted at this specific pipeline
  (filtered from the dispatch map).
- The full feature-dev-team prompt sequence to run internally
  (code-explorer → code-architect → code-implementer →
  impl-self-reviewer), with `{ISSUE_ID}` substituted.
- Instructions to commit, push, and open a PR on branch
  `multi-issue/<ISSUE_NUMBER>`.

### Step 6: Pipelines execute

Each pipeline runs in its own git worktree (allocated via
`isolation: "worktree"` on the `Agent` tool, or by invoking
`superpowers:using-git-worktrees` from inside the pipeline if more
control is needed), on its own branch
(`multi-issue/<ISSUE_NUMBER>`), running the existing feature-dev-team
flow internally. On success: commits, pushes, opens its PR via
`gh pr create`. On failure: reports a structured failure to the
Coordinator, no PR.

### Step 7: Coordinator applies tiered failure logic and aggregates

Independent failures do NOT stop sibling pipelines. Pipelines flagged as
dependents of a failed predecessor (per the Analyst's depends_on edges)
are signalled to abort via `SendMessage` and reported as SKIPPED.

When all pipelines have terminated, the Coordinator returns a structured
report per issue:
- GREEN: PR URL, commit SHA, gate verdict.
- FAILED: failure reason (which gate failed, file:line evidence).
- SKIPPED: predecessor issue number that caused the short-circuit.

### Step 8: Final report

The parent prints a batch summary:

```
Batch summary
- ISSUE-N: GREEN — PR: <url>
- ISSUE-N: FAILED — <gate>: <reason>
- ISSUE-N: SKIPPED — predecessor ISSUE-M failed

Verification
- Targeted tests run per pipeline: <commands>
- Before you commit, please run: dotnet test tests/Strg.Integration.Tests
```

The "Before you commit" line is emitted ONCE, deduplicated across all
pipelines (per the integration-test execution policy in `CLAUDE.md`).

### Step 9: Two-tier close-gate (gated — requires explicit user confirmation)

Closing GitHub issues is a shared-state action visible to subscribers.
The command MUST NOT auto-close, even when every gate is green per
pipeline.

1. The parent lists all GREEN pipelines with their PR URLs. FAILED and
   SKIPPED pipelines are listed separately and are NEVER candidates for
   auto-close.
2. The parent presents a single batch prompt:

   ```
   Close all <N> green issues?
   - ISSUE-A (PR: <url>)
   - ISSUE-B (PR: <url>)
   ...

   [yes / opt-out / no]
   ```

3. On `yes`: the parent runs `gh issue close <n> --comment "..."` per
   green issue, where the comment includes the PR URL.
4. On `opt-out`: the parent re-prompts per green issue with
   `[close / keep open]`. Issues kept open may optionally receive a
   progress comment via `gh issue comment` — also gated on explicit
   confirmation.
5. On `no`: nothing is closed. All issues remain open.

Failed and skipped pipelines may optionally receive a progress comment
via `gh issue comment <n> --body "..."` — explicit confirmation required
per issue. They remain OPEN.

Do NOT auto-close on self-review alone — the implementer is not an
independent reviewer. The same gate criteria as `/implement-issue`
Step 8 apply per pipeline before the issue is even considered for the
close prompt.

### Step 10: Done

Print the GitHub issue URLs and final state for every pipeline so the
caller can navigate quickly. Worktree paths and branch names returned by
each pipeline are preserved in the report so the user can inspect or
clean them up manually if desired.
