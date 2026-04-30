# strg — Project Overview

**strg** ("storage") is a self-hosted cloud storage platform written in C# / .NET 10, replacing Microsoft OneDrive with a fully owned, extensible, API-first platform.

**Repository:** github.com/andrekirst/strg
**License:** Apache 2.0

## Canonical documentation

The authoritative project guide is **`CLAUDE.md`** at the repo root. It is auto-loaded into Claude Code sessions and contains:

- Architecture and dependency rules (§ Architecture)
- Tech stack table (§ Technology Stack)
- Key patterns: multi-tenancy, soft-delete, path safety, repository pattern, outbox events, streaming (§ Key Patterns)
- Security rules — explicit "NEVER do these" list (§ Security Rules)
- Code conventions and naming (§ Code Conventions)
- Database / migrations / test policy (§§ Database, Running Tests)
- Issue tracking workflow + slash commands (§§ Issue Tracking, Agent Teams)
- Forbidden patterns (§ Forbidden Patterns)

When working in this repo, **read CLAUDE.md first** — it is a tight, high-signal reference and overrides any generic project-management defaults.

## Layered structure

```
src/
  Strg.Core/         Domain entities, interfaces — NO external NuGet deps
  Strg.Application/  CQRS handlers (Mediator) + behaviors
  Strg.Infrastructure/ EF Core, storage providers, OpenIddict
  Strg.GraphQl/      Hot Chocolate schema/types
  Strg.WebDav/       NWebDav server
  Strg.Api/          ASP.NET Core host (REST + TUS upload)
tests/
  Strg.Core.Tests/
  Strg.Api.Tests/
  Strg.GraphQl.Tests/
  Strg.Architecture.Tests/  NetArchTest layering assertions
  Strg.Integration.Tests/   Testcontainers (Postgres + RabbitMQ)
docs/
  issues/  requirements/  architecture/  decisions/  superpowers/
```

## Solution file

`strg.slnx` (NEVER reintroduce `strg.sln`).

## Project phases

The project memories at `~/.claude/projects/.../memory/MEMORY.md` index a series of "phase" decisions (Phase 1-13) that document design choices made during incremental delivery. These phases are referenced from doc frontmatter via `phase: N` and tag `phase-N`.
