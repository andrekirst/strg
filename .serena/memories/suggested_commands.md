# strg — Suggested Commands

## Build

```bash
dotnet build
```

## Run unit / architecture / GraphQL / API tests (no Docker)

```bash
dotnet test tests/Strg.Core.Tests tests/Strg.Api.Tests tests/Strg.GraphQl.Tests tests/Strg.Architecture.Tests
```

## Run a single integration test (filtered)

```bash
dotnet test tests/Strg.Integration.Tests --filter "FullyQualifiedName~<TestClassStem>"
```

Multiple stems OR'd:

```bash
dotnet test tests/Strg.Integration.Tests --filter "FullyQualifiedName~Stem1|FullyQualifiedName~Stem2"
```

**Do NOT run the full integration suite unsupervised** — see `task_completion_checklist` and `CLAUDE.md` § "Integration test execution policy".

## Format

```bash
dotnet format --verify-no-changes   # check
dotnet format                        # apply
```

A PostToolUse hook in `.claude/settings.json` runs `dotnet format --verify-no-changes` after every Edit/Write/MultiEdit.

## EF Core migrations

```bash
# Create
dotnet ef migrations add <Name> --project src/Strg.Infrastructure --startup-project src/Strg.Api

# Apply
dotnet ef database update --project src/Strg.Infrastructure --startup-project src/Strg.Api
```

## Coverage

```bash
dotnet test --collect:"XPlat Code Coverage"
```

## System

Standard Linux (`ls`, `find`, `grep`, `git`, `gh`). The repo uses GitHub Issues for STRG-XXX (not local files); fetch via `gh issue view <num>`.
