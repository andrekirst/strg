# /next-issue

Find the next issue ready to implement based on the dependency graph.

## Usage

```
/next-issue
```

## What This Does

1. Reads `docs/issues/README.md` for the full issue list and dependency graph
2. Checks which issues have `status: done` in their frontmatter
3. Finds issues where all `depends_on` issues are `status: done`
4. Recommends the highest-priority unblocked issue

## Steps

### Step 1: Scan completed issues

Read all issue files in `docs/issues/strg/`. Extract `status` from frontmatter.

### Step 2: Find unblocked issues

An issue is ready when:
- `status: open`
- ALL issues in `depends_on` have `status: done`

### Step 3: Prioritize

Among unblocked issues, select by:
1. `priority: critical` first
2. `priority: high` next
3. Lowest STRG number (earliest in the sequence)

### Step 4: Report

```
## Ready to Implement

### STRG-034: TUS upload endpoint (StrgTusStore)
Priority: critical | Complexity: large
Depends on: STRG-031 ✅, STRG-032 ✅, STRG-024 ✅

To implement: /implement-issue STRG-034

## Also Unblocked (3 issues)
- STRG-037: File download endpoint (priority: high)
- STRG-038: File listing endpoint (priority: high)
- STRG-082: Rate limiting (priority: high)
```
