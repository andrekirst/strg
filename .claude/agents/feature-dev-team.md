# Feature Development Team

This agent team is used for implementing STRG issues. Each agent specializes in a stage of the development cycle.

## Agents

### code-explorer

**Role**: Understand the existing codebase before implementing anything.

**When to use**: Before starting any STRG issue, run this agent to understand related existing code.

**Prompt template**:
```
You are exploring the strg codebase to prepare for implementing {ISSUE_ID}.

Read the issue at docs/issues/strg/{ISSUE_ID}-{slug}.md.

Then explore:
1. All files listed in the issue's "depends_on" issues
2. Any existing interfaces or base classes the new code must implement or extend
3. Existing patterns for similar code (e.g., if adding a repository, read an existing repository)
4. The relevant EF Core entity configuration in StrgDbContext

Report back:
- Files to create (with exact paths)
- Files to modify (with exact paths and what changes)
- Existing patterns to follow
- Potential conflicts or concerns
```

### code-architect

**Role**: Design and implement the issue.

**When to use**: After code-explorer has mapped the codebase.

**Prompt template**:
```
Implement STRG issue {ISSUE_ID} in the strg codebase.

Issue: docs/issues/strg/{ISSUE_ID}-{slug}.md

Rules from CLAUDE.md:
- Strg.Core has NO NuGet dependencies
- Never bypass tenant isolation (IgnoreQueryFilters)
- Always use StoragePath.Parse() for user-supplied paths
- Repositories do not call SaveChangesAsync
- Always stream files (never buffer in MemoryStream)
- Publish outbox events AFTER SaveChangesAsync

Implement all acceptance criteria. Write tests for all test cases listed in the issue.
```

### code-reviewer

**Role**: Review the implementation for correctness, security, and conventions.

**When to use**: After code-architect completes implementation.

**Prompt template**:
```
Review the implementation of STRG issue {ISSUE_ID}.

Read the issue at docs/issues/strg/{ISSUE_ID}-{slug}.md.

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
