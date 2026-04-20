---
id: CC-002
title: Configure Claude Code hooks for automated quality gates
milestone: setup
priority: high
status: open
type: infrastructure
labels: [claude-code, setup, hooks, automation]
depends_on: [CC-001]
blocks: []
assigned_agent_type: general-purpose
estimated_complexity: small
---

# CC-002: Configure Claude Code hooks for automated quality gates

## Summary

Configure Claude Code hooks in `.claude/settings.json` to automatically run quality gates after each coding session: build check, test run, lint, and security scan. This ensures agents never submit broken code.

## Background / Context

Claude Code hooks run shell commands in response to lifecycle events (session end, file save, pre-commit). By registering hooks, we ensure every agent session ends with a passing build and tests — without the human having to manually check.

## Technical Specification

Configure the following hooks in `.claude/settings.json`:

### Hook: PostToolUse (on Edit/Write)
After any file edit, run:
1. `dotnet build --no-restore` — catch compile errors immediately
2. If the edited file is a test file, run `dotnet test --no-build` (fast feedback)

### Hook: Stop (session end)
When Claude Code finishes:
1. `dotnet build` — full build
2. `dotnet test` — all tests
3. `dotnet list package --vulnerable` — security audit
4. If any fails, print failure summary and block completion

### Hook: PreToolUse (before destructive Bash commands)
Block or warn on:
- `rm -rf` with broad globs
- `git reset --hard`
- `git push --force`

## File: `.claude/settings.json`

```json
{
  "hooks": {
    "PostToolUse": [
      {
        "matcher": "Edit|Write",
        "hooks": [
          {
            "type": "command",
            "command": "dotnet build src/Strg.Api --no-restore --verbosity quiet 2>&1 | tail -5"
          }
        ]
      }
    ],
    "Stop": [
      {
        "hooks": [
          {
            "type": "command",
            "command": "echo '=== Build ===' && dotnet build --verbosity quiet && echo '=== Tests ===' && dotnet test --no-build --verbosity quiet && echo '=== Security ===' && dotnet list package --vulnerable"
          }
        ]
      }
    ]
  }
}
```

## Acceptance Criteria

- [ ] `.claude/settings.json` exists with hook configuration
- [ ] PostToolUse hook runs `dotnet build` after every file edit
- [ ] Stop hook runs build + test + vulnerability scan
- [ ] Hook failures print a clear, actionable error message
- [ ] Hooks do not run during plan mode (readonly)
- [ ] `.claude/` directory is in `.gitignore` for local overrides; `settings.json` is committed

## Test Cases

- **TC-001**: Edit a .cs file with a syntax error → PostToolUse hook reports the compile error
- **TC-002**: End a session with failing tests → Stop hook reports test failures and does not silently succeed
- **TC-003**: End a session with a vulnerable package → Stop hook reports the CVE
- **TC-004**: Plan mode session → hooks do not run (no false positives during planning)

## Implementation Tasks

- [ ] Create `.claude/` directory at repo root
- [ ] Write `.claude/settings.json` with PostToolUse hooks
- [ ] Write `.claude/settings.json` Stop hook
- [ ] Test each hook manually by triggering its condition
- [ ] Document hook behavior in CLAUDE.md (CC-001)

## Security Review Checklist

- [ ] Hooks do not expose secrets in command output
- [ ] Build command does not allow arbitrary code execution via crafted filenames
- [ ] Hook scripts do not have overly broad permissions

## Code Review Checklist

- [ ] JSON is valid and parseable
- [ ] Hook commands use `--verbosity quiet` to avoid noise
- [ ] Error output is piped correctly (stderr → stdout for capture)

## Definition of Done

- [ ] `.claude/settings.json` committed to repo
- [ ] All three hooks verified working in a real Claude Code session
- [ ] CLAUDE.md updated with hook documentation
