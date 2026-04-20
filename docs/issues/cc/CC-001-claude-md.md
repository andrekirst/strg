---
id: CC-001
title: Create CLAUDE.md for the strg project
milestone: setup
priority: critical
status: open
type: infrastructure
labels: [claude-code, setup, documentation]
depends_on: []
blocks: [CC-002, CC-003]
assigned_agent_type: general-purpose
estimated_complexity: medium
---

# CC-001: Create CLAUDE.md for the strg project

## Summary

Create a comprehensive `CLAUDE.md` file at the repository root that gives Claude Code agents full context about the strg project — architecture, conventions, constraints, and workflows. This file is read at the start of every Claude Code session.

## Background / Context

Without a `CLAUDE.md`, every agent session starts cold. A well-written `CLAUDE.md` eliminates the need to re-explain conventions on every issue, prevents agents from introducing anti-patterns, and encodes architectural guardrails that must never be violated (e.g., "Strg.Core must not reference infrastructure packages").

## Technical Specification

The `CLAUDE.md` must cover:

1. **Project overview**: What strg is, tech stack, repo structure
2. **Architecture rules**: Clean architecture dependency rules (what can import what)
3. **Code conventions**: Naming, file organization, C# style
4. **Test conventions**: Where tests go, what to test, coverage targets
5. **Forbidden patterns**: Global state, hardcoded strings, raw SQL, `async void`
6. **Database rules**: Always use EF Core migrations; never alter tables manually
7. **Security rules**: Never log secrets, always validate input, no path traversal
8. **Plugin contracts**: Never change interfaces in Strg.Core.Plugins without major version bump
9. **How to run**: `dotnet run`, `dotnet test`, migration commands
10. **How to verify**: What constitutes "done" for a feature

## Acceptance Criteria

- [ ] `CLAUDE.md` exists at repository root
- [ ] Contains project overview with tech stack
- [ ] Lists all projects in the solution and their responsibilities
- [ ] Defines dependency rules (which project can reference which)
- [ ] Specifies naming conventions (entities, services, repositories, endpoints)
- [ ] Lists forbidden patterns with explanations
- [ ] Specifies test organization and coverage targets
- [ ] Includes `dotnet run` and `dotnet test` commands
- [ ] Includes EF Core migration commands
- [ ] Documents the plugin contract stability requirement
- [ ] Documents security-sensitive rules (input validation, path traversal, secret logging)
- [ ] Claude Code agents can read this and immediately start contributing without asking basic questions

## Test Cases

- **TC-001**: Spawn a Claude Code agent on a new issue; verify the agent does not ask about tech stack, naming, or architecture — it already knows from CLAUDE.md
- **TC-002**: Check that all 9 source projects are documented with their purpose
- **TC-003**: Verify that "forbidden patterns" section exists and covers at least 5 patterns

## Implementation Tasks

- [ ] Read existing docs to extract all relevant conventions
- [ ] Write project overview section
- [ ] Write solution structure section (all src/ and tests/ projects)
- [ ] Write architecture dependency rules section
- [ ] Write C# code conventions section (naming, organization)
- [ ] Write test conventions section
- [ ] Write forbidden patterns section
- [ ] Write security rules section
- [ ] Write plugin system rules section
- [ ] Write "how to run / develop" section
- [ ] Write "definition of done" checklist

## Security Review Checklist

- [ ] Ensure CLAUDE.md does not contain any secrets, API keys, or credentials
- [ ] Ensure security rules in CLAUDE.md are correct (not accidentally permissive)

## Code Review Checklist

- [ ] Content is accurate relative to the actual codebase structure
- [ ] Rules are unambiguous (an agent following them produces correct code)
- [ ] No contradictions between sections

## Definition of Done

- [ ] CLAUDE.md created at repo root
- [ ] Content reviewed by human for accuracy
- [ ] At least one agent session run with it to validate usefulness
