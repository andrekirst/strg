# /implement-issue

Implement a strg issue end-to-end.

## Usage

```
/implement-issue STRG-034
```

## What This Does

1. **Reads** the issue file at `docs/issues/strg/{ISSUE_ID}-*.md`
2. **Explores** the codebase to understand existing patterns
3. **Implements** all acceptance criteria
4. **Writes tests** for all test cases listed in the issue
5. **Reviews** the implementation against the Security Review and Code Review checklists

## Steps

### Step 1: Read the issue

Read `docs/issues/strg/$ARGUMENTS-*.md` to understand what needs to be built.

### Step 2: Explore dependencies

For each issue listed in `depends_on`, read the relevant source files to understand the interfaces and patterns used.

### Step 3: Implement

Create or modify the files listed in the issue's "Implementation Tasks" section. Follow all patterns described in `CLAUDE.md`.

### Step 4: Write tests

Create test files for all "Test Cases" (TC-001, TC-002, etc.) listed in the issue.

### Step 5: Self-review

Go through each item in the "Security Review Checklist" and "Code Review Checklist" in the issue. Fix any violations before reporting completion.

### Step 6: Report

Summarize what was created/modified and confirm each acceptance criterion is met.
