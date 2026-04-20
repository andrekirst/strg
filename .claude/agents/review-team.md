# Code Review Team

This agent team reviews pull requests and implementations for the strg project.

## Agents

### pr-reviewer

**Role**: Review a pull request for correctness, conventions, and test coverage.

**When to use**: Before merging any PR.

**Prompt template**:
```
Review the pull request implementing {ISSUE_ID} in strg.

Files changed: {FILE_LIST}

Review against:
1. CLAUDE.md conventions (naming, forbidden patterns, dependency rules)
2. The issue's Definition of Done checklist
3. Test coverage (are all test cases from the issue implemented?)
4. The Code Review Checklist in the issue file

Check for:
- Breaking changes to `Strg.Core` interfaces (plugin authors depend on these)
- Missing XML documentation on new public interfaces
- EF Core N+1 query problems (no lazy loading enabled)
- Async/await correctness (no .Result or .Wait())
- CancellationToken propagated to all async calls
- Disposable resources properly disposed (using statements)

Rate each area: ✅ Good | ⚠️ Needs attention | ❌ Must fix
```

### test-reviewer

**Role**: Review test quality and coverage.

**When to use**: When the implementation is complete and tests are written.

**Prompt template**:
```
Review the tests written for STRG issue {ISSUE_ID}.

Read the issue at docs/issues/strg/{ISSUE_ID}-{slug}.md.

Verify:
1. Each "Test Case" (TC-001, TC-002, ...) in the issue has a corresponding test
2. Integration tests use StrgWebApplicationFactory (not mocked HttpContext)
3. Tests are independent (no shared mutable state between tests)
4. Edge cases are covered (null, empty, boundary values)
5. Error scenarios are tested (not just happy path)
6. MassTransit test harness used to verify outbox events
7. Tests use FluentAssertions for readable assertions

Report: Which test cases are missing, which tests are too weak (only happy path).
```
