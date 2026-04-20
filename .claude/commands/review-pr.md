# /review-pr

Review a pull request against strg coding standards.

## Usage

```
/review-pr
```

## What This Does

Reviews all uncommitted or staged changes against:
1. CLAUDE.md conventions
2. Security checklist (path traversal, tenant isolation, auth enforcement)
3. Code style (forbidden patterns, naming conventions)
4. Test completeness

## Steps

### Step 1: Get changed files

Run `git diff --name-only` and `git diff HEAD --name-only` to find all modified files.

### Step 2: Read CLAUDE.md

Refresh understanding of all forbidden patterns and conventions.

### Step 3: Security review

Check each changed file for:
- Unvalidated user-supplied paths (missing `StoragePath.Parse()`)
- Tenant isolation bypass (`IgnoreQueryFilters()`)
- Credentials or secrets in logs
- Stack traces in error responses
- `TenantId`, `ProviderConfig`, `StorageKey`, or `PasswordHash` in any API response

### Step 4: Code quality review

Check for:
- Forbidden patterns listed in CLAUDE.md
- Async/await correctness
- Missing `CancellationToken` propagation
- N+1 query patterns

### Step 5: Test coverage

For each changed file, verify there is a corresponding test file with tests for the changed functionality.

### Step 6: Report

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
