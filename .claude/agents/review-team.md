# Code Review Team

## Purpose

Full pull-request review before merge: correctness, conventions, test
coverage, and architecture rules from `CLAUDE.md`.

## When to Use

- A PR is open or a branch is ready to merge.
- Before marking an issue `status: done`.

## Agent Roster

Conceptual roles are mapped to real Claude Code `subagent_type` values.
Role names align with the `TC-002` acceptance criterion in CC-003
(`code-reviewer` + `pr-test-analyzer` must be spawned at minimum).

| Role                   | subagent_type     | Stage     | Tool-scope intent        |
|------------------------|-------------------|-----------|--------------------------|
| code-reviewer          | `general-purpose` | Pre-merge | read-only (**advisory**) |
| pr-test-analyzer       | `general-purpose` | Pre-merge | read-only (**advisory**) |
| type-design-analyzer   | `general-purpose` | Pre-merge | read-only (**advisory**) |
| comment-analyzer       | `general-purpose` | Pre-merge | read-only (**advisory**) |

**Tool-scope intent** is the role's *design* contract. All four roles are
framed as read-only reviewers, but Claude Code does not let a project
declare a per-role `allowed-tools` allowlist via `subagent_type`, so the
constraint is conveyed by the prompt only. Keep these prompts purely
analytical — do not add instructions that require Write/Edit/Bash.

This file's `code-reviewer` is **distinct** from the `impl-self-reviewer`
role defined in `feature-dev-team.md`. Both use `general-purpose` but serve
different stages (PR review vs. implementer's own sanity check).

## Codebase Anchors

Paths the review agents will reference:

| Concept                          | Path                                                                 |
|----------------------------------|----------------------------------------------------------------------|
| Project conventions              | `CLAUDE.md` (§Code Conventions, §Forbidden Patterns)                 |
| C# style skill                   | `.claude/skills/csharp-strg.md`                                      |
| GraphQL style skill              | `.claude/skills/graphql-strg.md`                                     |
| Integration-test web factory     | `tests/Strg.Integration.Tests/Auth/StrgWebApplicationFactory.cs`     |
| MassTransit test harness example | any existing test that instantiates `ITestHarness` from `MassTransit.Testing` — grep for `ITestHarness` to find the canonical example in `tests/Strg.Integration.Tests/`. |

## Team Inputs

- Changed file list (`git diff --name-only` / `git diff HEAD --name-only`).
- The issue file referenced by the PR.
- `CLAUDE.md` (conventions) and `docs/issues/**/*.md` (acceptance criteria).

## Team Outputs

- Per-area verdict: ✅ Good / ⚠️ Needs attention / ❌ Must fix.
- File + line references for every finding.
- A "missing tests" list (test cases from the issue without a corresponding
  test method).

---

## Agent: code-reviewer

**Role**: Correctness, conventions, and architecture-rule enforcement.

**subagent_type**: `general-purpose`.

**Expected Inputs**:
- Changed files.
- Issue ID associated with the PR.

**Expected Outputs**:
- Per-file verdict for each review area.
- Fix list ranked by severity.

**Prompt Template**:
```
Review the pull request implementing {ISSUE_ID} in strg.

Files changed: {FILE_LIST}

Step 1: Read CLAUDE.md §"Code Conventions" and §"Forbidden Patterns" first.
Enumerate every rule in those sections and check the diff for violations —
do not rely on your memory of project conventions.

Step 2: Review against:
1. The full set of rules you enumerated from CLAUDE.md.
2. The issue's Definition of Done checklist.
3. Test coverage (are all test cases from the issue implemented?).
4. The Code Review Checklist in the issue file.

Check specifically for:
- Breaking changes to Strg.Core interfaces (plugin authors depend on these)
- Missing XML documentation on new public interfaces
- EF Core N+1 query problems (no lazy loading enabled)
- Async/await correctness (no .Result or .Wait())
- CancellationToken parameters named `cancellationToken` (not `ct` / `token`)
- CancellationToken propagated to all async calls
- Disposable resources properly disposed (using statements)
- Magic strings for MIME types / headers / claim types / HTTP methods
  (must come from `MediaTypeNames`, `HeaderNames`, `ClaimTypes`, etc.)
- Solution file is `strg.slnx` — flag any reintroduction of `strg.sln`

Rate each area: ✅ Good | ⚠️ Needs attention | ❌ Must fix
```

---

## Agent: pr-test-analyzer

**Role**: Test coverage and test quality.

**subagent_type**: `general-purpose`.

**Expected Inputs**:
- Changed files.
- Issue file (for TC-xxx list).

**Expected Outputs**:
- TC-to-test-method mapping.
- List of missing test cases.
- List of weak tests (happy-path only, missing edge cases, shared mutable
  state, etc.).

**Prompt Template**:
```
Review the tests written for strg issue {ISSUE_ID}.

Read the issue at docs/issues/{prefix}/{ISSUE_ID}-*.md.

Verify:
1. Each "Test Case" (TC-001, TC-002, ...) in the issue has a corresponding
   test method (name the test method and file).
2. Integration tests use StrgWebApplicationFactory (not mocked HttpContext).
3. Tests are independent (no shared mutable state between tests).
4. Edge cases are covered (null, empty, boundary values).
5. Error scenarios are tested (not just happy path).
6. MassTransit test harness used to verify outbox events.
7. Tests use FluentAssertions for readable assertions.

Report: which test cases are missing, which tests are too weak.
```

---

## Agent: type-design-analyzer

**Role**: Evaluate the quality of new public types and interfaces.

**subagent_type**: `general-purpose`.

**Expected Inputs**:
- New or modified public types (classes, records, interfaces).

**Expected Outputs**:
- Per-type verdict on: cohesion, invariants upheld by constructors/inits,
  mutability boundaries, nullability annotations.
- Recommended refactors (e.g., "make this record sealed", "move this mutable
  property behind a method").

**Prompt Template**:
```
Review the public type and interface design in the diff.

For each new or modified public type, assess:
- Is it sealed by default (per CLAUDE.md)?
- Are invariants upheld by the constructor / init-only properties?
- Is mutability explicit and minimal?
- Are nullability annotations correct?
- Does it leak infrastructure concerns into Strg.Core (which must stay
  dependency-free)?

Report concerns with file + line and a concrete refactor suggestion.
```

---

## Agent: comment-analyzer

**Role**: Ensure comments and XML docs are accurate and valuable.

**subagent_type**: `general-purpose`.

**Expected Inputs**:
- Changed files.

**Expected Outputs**:
- List of comments that restate code ("what" instead of "why").
- List of stale comments (refer to removed code or old PR context).
- Missing XML docs on new public APIs.

**Prompt Template**:
```
Review comments and XML documentation in the diff.

Flag:
- Comments that explain what the code does (redundant — code shows that).
- Comments that reference transient context (issue numbers, PR numbers,
  "added for the X flow") — those belong in git history, not code.
- Missing XML docs on new public interfaces in Strg.Core.
- Stale comments that no longer match the code they describe.

Report file:line and a one-line recommendation per finding.
```
