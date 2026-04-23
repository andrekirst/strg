# Feature Development Team

## Purpose

Implement a single strg issue (STRG-xxx or CC-xxx) end-to-end: understand the
existing code, design and write the implementation, then self-review against
the acceptance criteria, security checklist, and code-review checklist in the
issue file.

## When to Use

- A new issue in `docs/issues/strg/` or `docs/issues/cc/` is unblocked and
  ready to implement.
- An existing issue's scope changes and the implementation must be revised.

## Architecture Context (for prompt substitution)

Every agent in this team MUST respect the layered structure below (reproduced
from `CLAUDE.md`):

```
Strg.Core          → NO external NuGet packages (pure BCL + MS abstractions)
Strg.Infrastructure → depends on Strg.Core
Strg.GraphQL       → depends on Strg.Core + Strg.Infrastructure
Strg.WebDav        → depends on Strg.Core + Strg.Infrastructure
Strg.Api           → depends on all above
```

Multi-tenancy: every entity inherits `TenantedEntity`; EF Core global query
filters enforce tenant isolation — never call `IgnoreQueryFilters()` in
application code. Soft-delete: `IsDeleted = true` + `DeletedAt` set. Paths:
every user-supplied path goes through `StoragePath.Parse()`. Repositories
do NOT call `SaveChangesAsync`. Outbox events publish AFTER `SaveChangesAsync`.

## Codebase Anchors

Canonical paths the agents will reference. Use them verbatim in prompts so
spawned teammates don't have to re-discover the layout:

| Concept                          | Path                                                                 |
|----------------------------------|----------------------------------------------------------------------|
| EF Core DbContext                | `src/Strg.Infrastructure/Data/StrgDbContext.cs`                      |
| Domain base entities             | `src/Strg.Core/Domain/{Entity,TenantedEntity}.cs`                    |
| Storage path value type          | `src/Strg.Core/Storage/StoragePath.cs`                               |
| Storage provider interface       | `src/Strg.Core/Storage/IStorageProvider.cs`                          |
| Integration-test web factory     | `tests/Strg.Integration.Tests/Auth/StrgWebApplicationFactory.cs`     |
| Coding-style skill               | `.claude/skills/csharp-strg.md`                                      |
| GraphQL-style skill              | `.claude/skills/graphql-strg.md`                                     |

If a path changes, update this table first — prompts reference it by name.

## Agent Roster

Conceptual roles are mapped to real Claude Code `subagent_type` values. Roles
do not correspond to custom subagent types — each role is spawned as the
`subagent_type` below with the role-specific prompt template in this file.

| Role                 | subagent_type      | Stage          | Tool-scope intent        |
|----------------------|--------------------|----------------|--------------------------|
| code-explorer        | `Explore`          | Before impl    | read-only (enforced)     |
| code-architect       | `Plan`             | Design         | read-only (enforced)     |
| code-implementer     | `general-purpose`  | Implementation | read/write (required)    |
| impl-self-reviewer   | `general-purpose`  | After impl     | read-only (**advisory**) |

**Tool-scope intent** is the role's *design* contract. Claude Code's
built-in `subagent_type` values do not let a project declare a per-role
`allowed-tools` allowlist, so the "advisory" row (`impl-self-reviewer`) is
conveyed only by the prompt template, not enforced by the harness. Treat
those prompts as read-only by convention and keep them that way.

## Team Inputs

- `ISSUE_ID` — e.g. `STRG-034` or `CC-003`. Must match `^(STRG|CC)-\d{3}$`.
- `{prefix}` — derived from `ISSUE_ID`: if `ISSUE_ID` starts with `STRG-`,
  `{prefix} = strg`; if it starts with `CC-`, `{prefix} = cc`. The issue
  file is therefore at `docs/issues/{prefix}/{ISSUE_ID}-*.md`.
- The project's `CLAUDE.md` (loaded automatically).

## Team Outputs

- Source files created or modified per the issue's Implementation Tasks.
- Test files covering every `TC-xxx` listed in the issue.
- A self-review report mapping each Acceptance Criterion and Security / Code
  Review checklist item to evidence (file + line or explanation).
- The issue frontmatter updated to `status: done` once all items pass.

---

## Agent: code-explorer

**Role**: Understand the existing codebase before any change is made.

**subagent_type**: `Explore`. In the prompt body, tell the explorer whether
to do a thorough cross-cutting sweep or a narrow scoped pass — the `Explore`
subagent picks its own search depth based on that instruction.

**Expected Inputs**:
- `ISSUE_ID`.
- List of `depends_on` issue IDs extracted from the issue's frontmatter.

**Expected Outputs**:
- Files to create (exact paths).
- Files to modify (exact paths + summary of changes).
- Existing patterns to follow (with file references).
- Potential conflicts, missing abstractions, or concerns.

**Prompt Template**:
```
You are exploring the strg codebase to prepare for implementing {ISSUE_ID}.

Read the issue at docs/issues/{prefix}/{ISSUE_ID}-*.md.

Then explore:
1. All files listed in the issue's "depends_on" issues
2. Any existing interfaces or base classes the new code must implement or extend
3. Existing patterns for similar code (e.g., if adding a repository, read an
   existing repository)
4. The relevant EF Core entity configuration in StrgDbContext

Report back:
- Files to create (with exact paths)
- Files to modify (with exact paths and what changes)
- Existing patterns to follow
- Potential conflicts or concerns

Do NOT write or edit code — research only.
```

---

## Agent: code-architect

**Role**: Turn the explorer's findings into a concrete implementation plan.

**subagent_type**: `Plan`.

**Expected Inputs**:
- The explorer's report.
- The issue's Technical Specification and Acceptance Criteria.

**Expected Outputs**:
- Step-by-step implementation plan referencing exact file paths.
- Interface sketches (method signatures, not full bodies).
- Test plan mapping each `TC-xxx` to a concrete assertion.

**Prompt Template**:
```
Design the implementation plan for strg issue {ISSUE_ID}.

You have the code-explorer's report and the issue file
(docs/issues/{prefix}/{ISSUE_ID}-*.md).

Produce a numbered plan that:
- Names every file to create or edit.
- Sketches the public surface (interfaces, method signatures).
- Maps each acceptance criterion to the file(s) that satisfy it.
- Maps each test case TC-xxx to a concrete assertion.

Respect the CLAUDE.md rules (Strg.Core has zero NuGet deps, tenant isolation
is enforced via global filters, StoragePath.Parse for all user-supplied paths,
etc.). Do NOT write code yet.
```

---

## Agent: code-implementer

**Role**: Write code and tests per the architect's plan.

**subagent_type**: `general-purpose`.

**Expected Inputs**:
- The architect's plan. *Handoff contract*: the parent Claude (the one
  running `/implement-issue`) keeps the architect's returned plan in its own
  context and embeds it verbatim into the `code-implementer` prompt below —
  spawned subagents do not share state, so the plan must travel through the
  parent.
- The issue file (authoritative for acceptance criteria and test cases).

**Expected Outputs**:
- All files created or edited per the plan.
- Tests for every `TC-xxx` that compile and pass.
- `dotnet test` green at the end.

**Prompt Template**:
```
Implement strg issue {ISSUE_ID} per the architect's plan.

Issue: docs/issues/{prefix}/{ISSUE_ID}-*.md

Non-negotiable rules from CLAUDE.md:
- Strg.Core has NO NuGet dependencies
- Never bypass tenant isolation (no IgnoreQueryFilters() in application code)
- Always use StoragePath.Parse() for user-supplied paths
- Repositories do not call SaveChangesAsync
- Always stream files (never buffer large files in MemoryStream)
- Publish outbox events AFTER SaveChangesAsync
- CancellationToken parameters are named `cancellationToken`
- Braces on every if/else/for/foreach/while/do/using/lock body

Implement all acceptance criteria. Write tests for every TC-xxx listed in
the issue. Run `dotnet test` before reporting completion.
```

---

## Agent: impl-self-reviewer

**Role**: Self-review the implementation before handoff. Distinct from the
`code-reviewer` defined in `review-team.md` — that one runs at PR time; this
one runs in the same session as the implementer to catch obvious issues
before the code leaves the feature-dev workflow.

**subagent_type**: `general-purpose` (advisory read-only — the prompt is
framed as "review", but the harness does not enforce a tool allowlist).

**Expected Inputs**:
- The implemented diff (`git diff HEAD`).
- The issue file.

**Expected Outputs**:
- Per-checklist-item verdict: ✅ pass, ⚠️ concern, ❌ fail.
- File + line references for every finding.
- Severity-ranked fix list (CRITICAL / HIGH / LOW).

**Prompt Template**:
```
Review the implementation of strg issue {ISSUE_ID}.

Read the issue at docs/issues/{prefix}/{ISSUE_ID}-*.md.

Review criteria (from the issue's checklists):
1. Security Review Checklist — check every item
2. Code Review Checklist — check every item
3. Acceptance Criteria — verify each is met by the code
4. Test Cases — verify each has a corresponding test

Look specifically for:
- Tenant isolation bypasses (IgnoreQueryFilters)
- Missing StoragePath.Parse() on user-supplied paths
- Stack traces exposed in error responses
- ProviderConfig or TenantId exposed in DTOs/GraphQL types
- Synchronous file I/O
- Missing CancellationToken parameters
- SaveChangesAsync called before event publish

Report issues as: CRITICAL (must fix), HIGH (should fix), LOW (nice to have).
```
