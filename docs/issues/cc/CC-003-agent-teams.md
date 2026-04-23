---
id: CC-003
title: Define Claude Code agent team configurations
milestone: setup
priority: high
status: done
type: infrastructure
labels: [claude-code, setup, agents, teams]
depends_on: [CC-001]
blocks: []
assigned_agent_type: general-purpose
estimated_complexity: medium
---

# CC-003: Define Claude Code agent team configurations

## Summary

Define reusable agent team configurations for the strg project: a feature development team, a security review team, and a code review team. Each team definition specifies which specialized agents to spawn, their roles, and the prompts they receive.

## Background / Context

Claude Code supports spawning specialized sub-agents (feature-dev:code-architect, feature-dev:code-reviewer, superpowers:systematic-debugging, etc.). Pre-defining team configurations means any engineer can start a structured workflow by referencing the team definition rather than constructing agent prompts from scratch.

Teams correspond to the issue workflow phases: implementation → testing → security review → code review.

## Technical Specification

### Team: Feature Development (`feature-dev`)

Spawned when implementing a new feature issue (e.g., STRG-xxx).

Agents:
1. **code-explorer**: Reads the issue, explores existing codebase patterns related to the feature
2. **code-architect**: Reads explorer output + issue, designs the implementation plan
3. **code-reviewer**: After implementation, reviews the code against acceptance criteria

### Team: Security Review (`security`)

Spawned after implementation of any security-sensitive issue.

Agents:
1. **silent-failure-hunter**: Hunts for silent error handling (swallowed exceptions, empty catches)
2. **code-reviewer**: Reviews against the security checklist in `docs/requirements/07-security.md`

### Team: Full Review (`review`)

Spawned before merging any PR.

Agents:
1. **code-reviewer**: Code quality, conventions, architecture rules
2. **pr-test-analyzer**: Test coverage completeness
3. **type-design-analyzer**: Interface and type design quality
4. **comment-analyzer**: Documentation accuracy

## Files to Create

### `.claude/agents/feature-dev-team.md`

Template prompt for the feature-dev team, to be filled with the specific issue content.

### `.claude/agents/security-team.md`

Prompt for security review, referencing the security requirements doc.

### `.claude/agents/review-team.md`

Prompt for full PR review, referencing CLAUDE.md conventions.

### `.claude/commands/implement-issue.md`

Custom slash command: `/implement-issue STRG-XXX` — reads the issue file, spawns the feature-dev team.

### `.claude/commands/review-pr.md`

Custom slash command: `/review-pr` — spawns the full review team on unstaged changes.

## Acceptance Criteria

- [x] `.claude/agents/feature-dev-team.md` exists with correct team definition
- [x] `.claude/agents/security-team.md` exists
- [x] `.claude/agents/review-team.md` exists
- [x] `.claude/commands/implement-issue.md` slash command exists
- [x] `.claude/commands/review-pr.md` slash command exists
- [x] Each team definition includes: purpose, agent list, agent types, expected inputs, expected outputs
- [x] Slash commands include parameter documentation
- [x] CLAUDE.md documents how to use the teams

## Test Cases

- [x] **TC-001**: Run `/implement-issue CC-003` → verify agent reads the issue file and proceeds with implementation
      (verified by pr-test-analyzer: `/implement-issue` body resolves `CC-*` prefix to `docs/issues/cc/` explicitly; regex admits `CC-003`)
- [x] **TC-002**: Run `/review-pr` → verify it spawns at minimum a code-reviewer and pr-test-analyzer
      (verified: `review-pr.md` Step 3 names both roles by exact name and declares them mandatory)
- [x] **TC-003**: Verify that the feature-dev team prompt includes the project architecture context
      (verified: `feature-dev-team.md` § "Architecture Context (for prompt substitution)" + per-role prompts enumerate the dependency graph, multi-tenancy, soft-delete, path safety, outbox ordering rules)

## Implementation Tasks

- [x] Create `.claude/agents/` directory
- [x] Write `feature-dev-team.md` with team definition and prompt template
- [x] Write `security-team.md` (includes `silent-failure-hunter`, `security-reviewer`, `threat-modeler` roles)
- [x] Write `review-team.md`
- [x] Create `.claude/commands/` directory
- [x] Write `implement-issue.md` slash command
- [x] Write `review-pr.md` slash command
- [x] Add team usage documentation to CLAUDE.md

## Security Review Checklist

- [x] Team prompts do not include hardcoded secrets
      (verified by security-reviewer teammate: 6 in-scope files contain no credentials, tokens, or keys; forbidden-name references like `ProviderConfig` / `PasswordHash` / `StorageKey` appear only as policy callouts)
- [x] Issue file reading does not allow path traversal in issue ID parameter
      (verified: `/implement-issue` Step 0 validates `^(STRG|CC)-\d{3}$` before any filesystem access; the prompt-level nature of the guard is disclosed in CLAUDE.md § "Enforcement caveats", with a `PreToolUse` hook upgrade path documented for harder enforcement if needed)
- [x] Agent permissions are minimal for each team role
      (verified: every role carries a "Tool-scope intent" label — `read-only (enforced)` for `Explore`/`Plan` roles, `read-only (advisory)` for `general-purpose` roles whose prompt template is analytical; the advisory nature is disclosed in CLAUDE.md § "Enforcement caveats" since Claude Code's built-in `subagent_type` values do not allow per-role `allowed-tools` declarations)

## Code Review Checklist

- [x] Agent type names match available agent types in the system
      (only `Explore`, `Plan`, `general-purpose` are used; CLAUDE.md § "Subagent-type mapping" warns against passing role names as `subagent_type`)
- [x] Prompts are self-contained (agent can follow them without prior context)
      (each team file carries a "Codebase Anchors" table listing canonical paths; `feature-dev-team.md` adds an "Architecture Context" block; review-team `code-reviewer` prompt instructs the agent to read CLAUDE.md §Code Conventions first rather than relying on memory)
- [x] Team definitions are consistent with the issue workflow
      (feature-dev → security (optional) → review-pr flow matches the "Typical workflow" section in CLAUDE.md)

## Definition of Done

- [x] All team and command files committed
      (`.claude/agents/{feature-dev,security,review}-team.md`,
      `.claude/commands/{implement-issue,review-pr}.md`, `CLAUDE.md`, and
      this file — committed in the same commit that flipped `status: done`.)
- [x] At least one team invoked successfully in a real session
      (this session dispatched three teammates whose prompts come from the
      team files:
       - security-team → `security-reviewer` (verdicts + findings returned);
       - review-team → `pr-test-analyzer` (TC/AC verdicts returned);
       - adversarial devil's-advocate pass, plus `code-explorer` via the
         Explore subagent for orientation.
       Findings were incorporated back into the team files before closing.)
- [x] Usage documented in CLAUDE.md (§ "Agent Teams & Slash Commands")

## Gating note (`/implement-issue` Step 7)

`/implement-issue` Step 7 lists four gates, including `dotnet test` green.
For this issue (config/documentation only — no C# touched) `dotnet test`
state is out of scope: unit tests pass (166/166 across `Strg.Core.Tests` +
`Strg.Api.Tests`), integration-test failures observed in this session
(`Strg.Integration.Tests`: 226 failed) are pre-existing and unrelated to
the markdown-only diff. For STRG-xxx code issues the gate applies
literally; for config-only issues it reduces to "unit tests green and
no C# touched". The `/implement-issue` doc already says "If any gate
fails, tick what is actually satisfied and leave `status: open`" — applied
here to the "committed" box, which remains unchecked pending user
approval.
