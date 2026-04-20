---
id: CC-003
title: Define Claude Code agent team configurations
milestone: setup
priority: high
status: open
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

- [ ] `.claude/agents/feature-dev-team.md` exists with correct team definition
- [ ] `.claude/agents/security-team.md` exists
- [ ] `.claude/agents/review-team.md` exists
- [ ] `.claude/commands/implement-issue.md` slash command exists
- [ ] `.claude/commands/review-pr.md` slash command exists
- [ ] Each team definition includes: purpose, agent list, agent types, expected inputs, expected outputs
- [ ] Slash commands include parameter documentation
- [ ] CLAUDE.md documents how to use the teams

## Test Cases

- **TC-001**: Run `/implement-issue CC-003` → verify agent reads the issue file and proceeds with implementation
- **TC-002**: Run `/review-pr` → verify it spawns at minimum a code-reviewer and pr-test-analyzer
- **TC-003**: Verify that the feature-dev team prompt includes the project architecture context

## Implementation Tasks

- [ ] Create `.claude/agents/` directory
- [ ] Write `feature-dev-team.md` with team definition and prompt template
- [ ] Write `security-team.md`
- [ ] Write `review-team.md`
- [ ] Create `.claude/commands/` directory
- [ ] Write `implement-issue.md` slash command
- [ ] Write `review-pr.md` slash command
- [ ] Add team usage documentation to CLAUDE.md

## Security Review Checklist

- [ ] Team prompts do not include hardcoded secrets
- [ ] Issue file reading does not allow path traversal in issue ID parameter
- [ ] Agent permissions are minimal for each team role

## Code Review Checklist

- [ ] Agent type names match available agent types in the system
- [ ] Prompts are self-contained (agent can follow them without prior context)
- [ ] Team definitions are consistent with the issue workflow

## Definition of Done

- [ ] All team and command files committed
- [ ] At least one team invoked successfully in a real session
- [ ] Usage documented in CLAUDE.md
