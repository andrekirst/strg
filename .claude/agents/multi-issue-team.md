# Multi-Issue Team

## Purpose

Implement multiple strg issues end-to-end **in parallel** by driving a
batch of feature-dev-team pipelines under a dedicated Coordinator. This
file defines the two new roles introduced for batch work — Analyst and
Coordinator — and references `.claude/agents/feature-dev-team.md` for the
per-pipeline roles (code-explorer, code-architect, code-implementer,
impl-self-reviewer), which are reused without modification.

## When to Use

- A list of unblocked issues from `docs/issues/strg/` is ready to run as
  a batch (e.g. several REST endpoint issues, or independent feature
  work).
- The user invokes `/implement-issues <ISSUE> [<ISSUE> ...]`.

For single-issue work, use `/implement-issue` directly — it has fewer
moving parts and no Analyst/Coordinator layer.

## Relationship to `feature-dev-team.md`

- All architecture context (layered structure, multi-tenancy, soft
  delete, path safety, repository pattern, outbox events, streaming) and
  all codebase anchors are inherited verbatim from
  `.claude/agents/feature-dev-team.md`. They are NOT restated here.
- Per-pipeline roles (code-explorer, code-architect, code-implementer,
  impl-self-reviewer) are defined in `feature-dev-team.md`. Each pipeline
  spawned by the Coordinator runs the full feature-dev-team flow
  internally as one `general-purpose` subagent, embedding the
  role-specific prompt templates from that file verbatim, in sequence.
  This file does NOT redefine them.
- If a per-pipeline role's behaviour needs to change for batch work, fix
  it in `feature-dev-team.md` so both single-issue and multi-issue
  commands benefit. Do not fork.

## Agent Roster

Conceptual roles mapped to real Claude Code `subagent_type` values.

| Role         | subagent_type     | Stage                | Naming                       | Tool-scope intent                |
|--------------|-------------------|----------------------|------------------------------|----------------------------------|
| analyst      | `Explore`         | Pre-flight (one-shot)| unnamed                      | read-only (enforced)             |
| coordinator  | `general-purpose` | Whole batch (long-lived) | `coordinator`            | dispatch + routing (read/write)  |
| pipeline-N   | `general-purpose` | Per-issue, parallel  | `pipeline-<ISSUE_NUMBER>`    | read/write (required)            |

Each pipeline-N is one `general-purpose` subagent that internally runs
the feature-dev-team roles in sequence (code-explorer → code-architect →
code-implementer → impl-self-reviewer) — they are NOT separate subagents
per role. This keeps context focused per pipeline and matches
`/implement-issue`'s established shape for single-issue work.

**Tool-scope intent** is the role's *design* contract. Claude Code does
not let a project declare a per-role `allowed-tools` allowlist via
`subagent_type`, so the read-only label on the Analyst is conveyed by
the prompt template only. Keep the Analyst prompt purely analytical —
do not add instructions that require Write/Edit/Bash/PR work.

## Team Inputs

- The list of issue references provided to `/implement-issues`.
- All N GitHub issue bodies fetched by the parent via `gh issue view`.
- The project's `CLAUDE.md` (loaded automatically).
- `.claude/agents/feature-dev-team.md` for per-pipeline role templates.

## Team Outputs

- Per-issue source files created or modified per each issue's
  Implementation Tasks (each in its own git worktree).
- Per-issue test files covering every `TC-xxx`.
- Per-issue PR opened against `main` via `gh pr create` on branch
  `multi-issue/<ISSUE_NUMBER>`.
- A structured batch report with per-issue verdicts (GREEN / FAILED /
  SKIPPED), PR URLs, gate failure reasons, and short-circuit
  predecessors.

---

## Agent: analyst

**Role**: Pre-flight static analysis of all N issues. Produces a
structured dispatch map that the Coordinator uses to (a) decide which
pipelines run safely in parallel, (b) detect file-overlap and dependency
edges, (c) generate ready-to-embed cross-track hint paragraphs, and
(d) flag risk areas (security-sensitive, schema migrations, breaking
changes).

**subagent_type**: `Explore` (read-only enforced; no code modification
allowed at this stage).

**Lifetime**: One-shot. Spawned once before any pipeline starts.
Terminates after returning its dispatch map.

**Expected Inputs**:
- All N issue bodies verbatim, embedded in the prompt. The analyst MUST
  NOT chase any `docs/issues/...` link — the embedded body is
  authoritative.

**Expected Outputs**: a structured Markdown report with the following
sections, in order:

1. **Independence verdict per issue.** One line per issue:
   `ISSUE-<n>: independent | depends-on=ISSUE-<m>[,ISSUE-<o>...] | overlaps-with=ISSUE-<m>[,ISSUE-<o>...]`.
2. **Depends-on graph.** Every edge parsed from `Depends on:` /
   `depends_on:` lines in the issue bodies, formatted as
   `<successor> → <predecessor>`. Edges parsed from prose rather than a
   dedicated line are flagged `(low-confidence)`.
3. **File-overlap matrix.** For every pair of issues that touches the
   same file path (per "Implementation Tasks" enumeration in the body),
   one row: `<file path> ← ISSUE-<n>, ISSUE-<m>`. Heuristic detections
   (issue body names a concept, not an explicit file path) are flagged
   `(low-confidence)`.
4. **Risk tags per issue.** Each issue gets zero or more tags from:
   `security-sensitive`, `schema-migration`, `breaking-change`. Issues
   with no tags are listed as `(none)` so the Coordinator sees the full
   set explicitly.
5. **Cross-track hint paragraphs.** For every overlap or dependency, one
   short paragraph (≤4 sentences) addressed to a specific pipeline
   track. Format: `### Hint for pipeline-<n>` followed by the paragraph.
   The Coordinator will paste these verbatim into the targeted
   pipeline's prompt — write them as ready-to-deliver instructions, not
   analysis.
6. **Confidence summary.** Final paragraph naming any low-confidence
   detections so the Coordinator can decide whether to fall back to
   serial execution for the affected pair(s).

**Prompt Template**:
```
You are the pre-flight Analyst for a multi-issue strg batch run.

The following GitHub issue bodies are embedded verbatim. Read all of
them in full before producing your output. Do NOT chase any
docs/issues/... links — the embedded bodies are authoritative.

[N issue bodies inserted here, each prefixed with "## ISSUE <number>: <title>"]

Your job is to produce a structured dispatch map that the Coordinator
will use to spawn parallel implementation pipelines safely.

Output sections, in this exact order, all in Markdown:

1. **Independence verdict per issue** — for each issue, one line:
   `ISSUE-<n>: independent | depends-on=ISSUE-<m>[,...] | overlaps-with=ISSUE-<m>[,...]`.

2. **Depends-on graph** — every edge parsed from `Depends on:` lines in
   the issue bodies, as `<successor> → <predecessor>`. Mark
   prose-derived edges as `(low-confidence)`.

3. **File-overlap matrix** — for every pair of issues that touches the
   same file path (per "Implementation Tasks" lists in the bodies), one
   row: `<file path> ← ISSUE-<n>, ISSUE-<m>`. Mark heuristic detections
   (concept-only, not explicit file path) as `(low-confidence)`.

4. **Risk tags per issue** — for each issue, zero or more of:
   `security-sensitive`, `schema-migration`, `breaking-change`. List
   issues with no tags as `(none)`.

5. **Cross-track hint paragraphs** — for every depends-on edge or file
   overlap, one paragraph (≤4 sentences) addressed to a specific
   pipeline track. Format: `### Hint for pipeline-<n>` followed by the
   paragraph. The Coordinator pastes these verbatim into the targeted
   pipeline's prompt — write them as ready-to-deliver instructions
   ("treat the X interface as frozen", "use the existing Y extension
   point"), not analysis ("there is overlap between X and Y").

6. **Confidence summary** — final paragraph naming any low-confidence
   detections. Be explicit: the Coordinator may serialize the affected
   pair instead of running them in parallel based on what you say here.

Do NOT write or edit any code. Do NOT spawn any subagents. Read-only,
analysis-only.
```

---

## Agent: coordinator

**Role**: Long-lived orchestrator for the whole batch. Receives the
Analyst's dispatch map, spawns N parallel per-issue pipelines, routes
cross-track hints via `SendMessage`, applies tiered failure logic, and
aggregates per-pipeline reports.

**subagent_type**: `general-purpose`. Spawned with `name: "coordinator"`
so the parent (and the pipelines, transitively) can address it via
`SendMessage` if needed.

**Lifetime**: From when the parent spawns it until the aggregated batch
report is returned. The Coordinator does NOT survive across slash-command
invocations.

**Expected Inputs** (all embedded verbatim in the prompt):
- The Analyst's dispatch map.
- All N GitHub issue bodies.
- The list of issue numbers and (for URL-fetched issues) their full URLs.
- A reference to `.claude/agents/feature-dev-team.md` so the Coordinator
  embeds the per-pipeline role templates verbatim into each pipeline's
  prompt.

**Expected Outputs** — the Coordinator returns ONE structured report to
the parent containing, per issue:
- Verdict: GREEN / FAILED / SKIPPED.
- For GREEN: PR URL, commit SHA, evidence per acceptance criterion, gate
  outcomes (tests / security / code-review checklists), worktree path,
  branch name.
- For FAILED: which gate failed, file:line evidence, recovery
  recommendation.
- For SKIPPED: the failed predecessor issue number that triggered the
  short-circuit.

**Operational rules**:
1. **Parallel dispatch**: spawn all N pipelines in a SINGLE message with
   N `Agent` tool calls. This is required for true parallelism —
   sequential `Agent` calls run one-at-a-time.
2. **Background runs**: each pipeline is spawned with
   `run_in_background: true` so the Coordinator gets completion
   notifications and can keep coordinating without blocking on any one
   pipeline.
3. **Worktree isolation**: each pipeline is spawned with
   `isolation: "worktree"` so it operates in its own git worktree. The
   worktree path and branch name are returned in the pipeline's result
   if changes were made.
4. **Naming**: each pipeline gets `name: "pipeline-<ISSUE_NUMBER>"` so
   `SendMessage` can target it for live hint routing.
5. **Hint forwarding**: after dispatch, the Coordinator monitors
   completion and interim signals. When a pipeline reports an
   observation that affects a sibling (per the Analyst's overlap
   matrix), the Coordinator sends the relevant hint paragraph via
   `SendMessage` to the named sibling.
6. **Tiered failure**: when a pipeline reports FAILED, the Coordinator
   consults the depends-on graph from the Analyst. Every direct or
   transitive successor is sent a stop signal via `SendMessage` and
   reported as SKIPPED in the final report. Independent siblings are
   unaffected.
7. **No auto-close**: the Coordinator does NOT close any issues. Closing
   is the parent's responsibility under the two-tier gate documented in
   `.claude/commands/implement-issues.md` Step 9.

**Prompt Template**:
```
You are the Coordinator for a multi-issue strg batch run. You are
spawned as a long-lived `general-purpose` subagent named `coordinator`.
The parent will not interact with you again until you return your final
aggregated report.

You have received:
1. The Pre-Flight Analyst's dispatch map (verbatim, below).
2. All N GitHub issue bodies (verbatim, below).
3. The list of issue numbers being processed in this batch.

[Analyst dispatch map inserted here]

[N issue bodies inserted here]

Your responsibilities:

A. **Dispatch N parallel pipelines.** In a SINGLE response, emit N
   `Agent` tool calls (one per issue). Each pipeline:
   - Has `subagent_type: general-purpose`.
   - Has `name: "pipeline-<ISSUE_NUMBER>"` so you can `SendMessage` to
     it later.
   - Has `run_in_background: true` so you get completion notifications.
   - Has `isolation: "worktree"` so it operates in its own git worktree.
   - Receives a prompt that embeds, verbatim:
     a. The pipeline's specific GitHub issue body.
     b. Any cross-track hint paragraphs from the Analyst targeted at
        `pipeline-<this issue number>` (filter from the dispatch map).
     c. The per-pipeline role flow from
        `.claude/agents/feature-dev-team.md`: code-explorer →
        code-architect → code-implementer → impl-self-reviewer. Embed
        the prompt templates from that file verbatim, with `{ISSUE_ID}`
        substituted for the GitHub issue number.
     d. Instructions to commit on branch `multi-issue/<ISSUE_NUMBER>`,
        push, and open a PR via `gh pr create` with a body that
        references the GitHub issue (e.g. "Implements #<ISSUE_NUMBER>").
     e. Instructions to report back a structured success or failure
        record (verdict, gate outcomes, file:line evidence per
        acceptance criterion, PR URL on success, failure reason on
        failure).

B. **Route hints mid-flight.** When a pipeline reports an interim
   observation (e.g. "I am about to change `IStorageProvider`"), check
   the Analyst's overlap matrix. If a sibling pipeline is affected,
   send the corresponding hint paragraph to the named sibling via
   `SendMessage`.

C. **Apply tiered failure logic.** When a pipeline reports FAILED:
   - Look up the failed pipeline's issue in the Analyst's depends-on
     graph.
   - For every direct or transitive successor, send a stop instruction
     via `SendMessage` and mark the successor as SKIPPED in your
     internal tracking.
   - Independent siblings continue unaffected.

D. **Aggregate and return.** When all pipelines have terminated
   (success, failure, or skipped), return ONE structured report to the
   parent with per-issue verdict, PR URL or failure reason, worktree
   path, and branch name.

Non-negotiable rules from CLAUDE.md (apply to every pipeline you spawn —
embed these in the pipeline prompt verbatim too):
- Strg.Core has NO NuGet dependencies.
- Tenant isolation must not be bypassed (no `IgnoreQueryFilters()` in
  application code outside documented pre-auth carve-outs).
- All user-supplied paths go through `StoragePath.Parse()`.
- Repositories do not call `SaveChangesAsync` — the caller commits.
- Outbox events publish AFTER `SaveChangesAsync`.
- Streaming for large files; never buffer in memory.
- `cancellationToken` parameter name; braces on every control-flow body.

Do NOT close any GitHub issues yourself. Do NOT add or remove items from
the parent's gate. Closing is gated on explicit user confirmation in the
parent's Step 9 (`/implement-issues` command body).
```

---

## Per-pipeline roles

Per-pipeline roles are defined in `.claude/agents/feature-dev-team.md`:

- `code-explorer` (subagent_type: `Explore`)
- `code-architect` (subagent_type: `Plan`)
- `code-implementer` (subagent_type: `general-purpose`)
- `impl-self-reviewer` (subagent_type: `general-purpose`)

This file does NOT duplicate their definitions. Each pipeline-N spawned
by the Coordinator runs all four roles internally as a single
`general-purpose` subagent (the Coordinator embeds the role-specific
prompt templates verbatim, in sequence). This matches how
`/implement-issue` already operates for single-issue work.

If a per-pipeline role's behaviour needs to change for batch work, fix
it in `feature-dev-team.md` so both single-issue and multi-issue
commands benefit. Do not fork.
