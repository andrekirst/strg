# strg — Task Completion Checklist

When finishing a coding change, **before committing**:

## 1. Build

```bash
dotnet build
```

Must succeed. The repo enforces `EnforceCodeStyleInBuild` and braces-required style via `.editorconfig`.

## 2. Unit + architecture tests

```bash
dotnet test tests/Strg.Core.Tests tests/Strg.Api.Tests tests/Strg.GraphQl.Tests tests/Strg.Architecture.Tests
```

Must pass. These run quickly (no Docker).

## 3. Targeted integration tests (Claude only)

Per `CLAUDE.md` § "Integration test execution policy":

- **S1 (shared-infra)** — full handoff to user. Run no integration tests.
- **B3a (paired test exists)** — `dotnet test tests/Strg.Integration.Tests --filter "FullyQualifiedName~<stem>"`.
- **W2 (>5 distinct stems)** — full handoff.
- **No-match** — note `no integration test matches <File.cs>`.

The strictest bucket wins.

## 4. Format

```bash
dotnet format --verify-no-changes
```

The PostToolUse hook in `.claude/settings.json` already runs this after every edit.

## 5. Verification handoff (when claiming done)

```
Verification status
- Unit tests: PASS | FAIL (with failing test name)
- Integration tests I ran: <command> → PASS | FAIL  OR  NONE — <reason>
- Touched files & buckets:
  - <file>  → B3a (~<stem>)  |  S1 (full handoff)  |  test-only  |  no match
- Before you commit, please run:
  dotnet test tests/Strg.Integration.Tests
```

The "Before you commit" line is **always** present. The user runs the full integration suite before commit/PR.

## NEVER

- Run `dotnet test tests/Strg.Integration.Tests` without `--filter` unsupervised.
- Skip pre-commit hooks (`--no-verify`) unless the user explicitly asks.
- `git push --force` to main/master.
- Make claims of PASS/FAIL without command output.
