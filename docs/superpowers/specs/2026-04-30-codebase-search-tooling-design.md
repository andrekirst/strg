---
title: "Codebase & Docs Search Tooling Design"
tags: [spec, claude-code, search, mcp, serena, obsidian]
status: draft
created: 2026-04-30
updated: 2026-04-30
related-issues: []
---

# Codebase & Docs Search Tooling Design

> **Status:** Draft — pending user review
> **Origin:** Brainstorm session 2026-04-30
> **Implementation plan:** `docs/superpowers/plans/2026-04-30-codebase-search-tooling.md` (created via `writing-plans` skill after this spec is approved)

## 1. Goals

Address four codebase-search pain points identified in the brainstorm and add graph-aware docs navigation:

- **A — Latency:** raw `Grep` over 537 `.cs` files is slow when repeated across a session.
- **B — Query count:** following C# semantic relationships ("where used → what inherits → what overrides") through text search burns many turns.
- **C — Context bloat:** raw search output crowds out useful context.
- **D — Recall:** text search misses generics, overrides, virtual chains, and base-class members.

Concrete deliverables:

1. **Activate Serena MCP** for C# semantic search — currently loaded in the session but not onboarded for this project (`.serena/project.yml` does not exist).
2. **Treat `docs/` as an Obsidian vault** via the Local REST API plugin and a REST-API-based MCP server, giving Claude graph-aware queries (backlinks, tags) over the docs corpus.
3. **Codify tool-routing in CLAUDE.md** so Claude reaches for the right tool (Serena / Obsidian MCP / `Edit`-`Write` / `Grep`) instead of defaulting to `Grep`.
4. **Phase 1: one-shot frontmatter retrofit** on 15 high-value files. Going forward (Phase 2, rolling), every new doc is born with frontmatter; no further migration sweep.

## 2. Non-goals

- **GitHub MCP integration.** STRG-XXX issues live on GitHub and have a link-heavy dependency graph. Adding a GitHub MCP is the cleanest natural follow-up but is explicitly deferred (chosen over option `b3` from the brainstorm).
- **Per-layer `CLAUDE.md` files** in `src/Strg.*` — would help onboarding-cost pain (E), which was not selected.
- **`ast-grep` MCP** for AST-pattern search — held in reserve. Revisit only if Serena's symbolic model proves insufficient for "find every empty catch" CLAUDE.md "Forbidden Patterns" enforcement.
- **Auto-memory in vault.** Memory stays in `~/.claude/projects/.../memory/`. Different lifecycle, per-user not per-repo. May later open as a separate, peer Obsidian vault.
- **Repo-as-vault.** Vault is `docs/` only. Source code stays out of the Obsidian graph.

## 3. Architecture

Two MCPs on two non-overlapping substrates. `Edit` / `Write` remains the canonical write path for content edits to either substrate.

```
                      Claude Code agent
                     /        |        \
                    ▼         ▼         ▼
              ┌────────┐ ┌────────┐ ┌─────────────┐
              │ Serena │ │ Edit / │ │ Obsidian    │
              │ MCP    │ │ Write  │ │ REST-API    │
              │        │ │        │ │ MCP         │
              └────┬───┘ └───┬────┘ └──────┬──────┘
                   │ LSP     │             │ HTTP
                   ▼         │             ▼
              ┌────────┐     │       ┌─────────────┐
              │ Roslyn │     │       │ Obsidian +  │
              │ LSP    │     │       │ Local REST  │
              │ server │     │       │ API plugin  │
              └────┬───┘     │       └──────┬──────┘
                   ▼         ▼              ▼
              ┌────────┐ ┌──────┐     ┌──────────┐
              │ src/   │ │ ANY  │     │ docs/    │
              │ tests/ │ │ FILE │     │ (vault)  │
              │ (.cs)  │ │      │     │ (.md)    │
              └────────┘ └──────┘     └──────────┘
```

**Substrate split is hard.** Serena indexes only `src/` + `tests/` (`.cs`); Obsidian indexes only `docs/` (`.md`). The two indexes never see the same file.

**Write paths to `docs/`:**

- `Edit` / `Write` — content edits (default for everything except the two below).
- Obsidian MCP `rename_note` — atomic rename + backlink update across the vault.
- Obsidian MCP `batch_update_tags` — mass tag operations.

**Write paths to `src/` and `tests/`:**

- `Edit` / `Write` — content edits (default).
- Serena `rename_symbol` — atomic refactor + caller updates.

All paths land in git as reviewable diffs. The MCP-mediated graph operations (`rename_note`, `rename_symbol`) typically produce *multi-file* diffs (the renamed file plus updated references); content edits via `Edit` / `Write` typically produce single-file diffs. Both are normal review experiences.

## 4. Component A — Serena onboarding

### 4.1 Project config

`.serena/project.yml` at repo root:

```yaml
project_name: strg
language: csharp
ignore_all_files_in_gitignore: true
ignored_paths:
  - "**/*.g.cs"        # auto-generated source
  - "**/bin/"
  - "**/obj/"
  - "**/Migrations/"   # EF-generated; high noise, low query value
  - "artifacts/"
```

### 4.2 Onboarding sequence

1. Write `.serena/project.yml`.
2. Call Serena's `onboarding` tool — indexes via Roslyn LSP (2-5 min on first run).
3. Verify with `find_symbol("StoragePath")` — expect a hit at `src/Strg.Core/Storage/StoragePath.cs`.

### 4.3 Tool routing for code questions

| Question shape | Tool |
|---|---|
| "Where is `X` defined?" | `find_symbol` |
| "Who calls `X`?" | `find_referencing_symbols` |
| "What's in this file/class?" | `get_symbols_overview` |
| Forbidden-pattern enforcement (e.g., `IgnoreQueryFilters` outside the carve-out) | `search_for_pattern` |
| Rename a symbol + update all callers atomically | `rename_symbol` |

Fall back to `Grep` only for non-symbol text — string literals, comments, log messages, JSON values.

### 4.4 Write-path policy

- Content edits → `Edit` / `Write` (default).
- Refactor symbol + update all callers → Serena `rename_symbol` (allowlisted).
- Other Serena edit tools — `replace_symbol_body`, `insert_after_symbol`, `insert_before_symbol`, `safe_delete_symbol` — **not allowlisted**; require explicit per-call user approval.

## 5. Component B — Obsidian Local REST API + MCP

### 5.1 MCP variant

`MarkusPfundstein/mcp-obsidian` v0.2.2 (Python, REST-API based). Installed via `pipx install mcp-obsidian`.

**Install-time verification (resolved 2026-04-30):** the MCP exposes 12 tools — `obsidian_list_files_in_vault`, `obsidian_list_files_in_dir`, `obsidian_get_file_contents`, `obsidian_batch_get_file_contents`, `obsidian_simple_search`, `obsidian_complex_search`, `obsidian_append_content`, `obsidian_patch_content`, `obsidian_delete_file`, `obsidian_get_periodic_note`, `obsidian_get_recent_periodic_notes`, `obsidian_get_recent_changes`. **Notably absent:** `get_backlinks`, `find_notes_by_tag` (as a dedicated tool), `rename_note`, `batch_update_tags`.

Consequences:

- Tag-based queries are recovered via `obsidian_complex_search` (JsonLogic — e.g., `{"in": ["auth", {"var": "tags"}]}`).
- Backlink queries cannot be done via this MCP — fall back to `Grep` over markdown.
- Atomic rename-with-backlink-update is not available — rename via `Write` + `Grep` sweep for referrers.
- The §5.5 read-write policy fully collapses to read-only: MCP write tools (`obsidian_append_content`, `obsidian_patch_content`, `obsidian_delete_file`) remain not allowlisted.

The env-var the MCP reads is **`OBSIDIAN_API_KEY`** (verified by source — `mcp_obsidian/tools.py:12-14`), not `OBSIDIAN_API_TOKEN` as earlier draft assumed.

### 5.2 One-time host setup (per-user, not in repo)

1. Install Obsidian Desktop (Linux: AppImage / snap / `.deb`).
2. Open `docs/` (this repo) as a vault.
3. Install community plugin **Local REST API** (Settings → Community plugins → Browse).
4. Enable plugin; copy generated API token. The token is auto-stored by the plugin under `docs/.obsidian/plugins/obsidian-local-rest-api/data.json` (gitignored, see §5.3); copy it manually into `.env` so the MCP server can read it.
5. Apply Obsidian settings (encoded in committed configs):
   - *Files & Links → Use `[[Wikilinks]]`:* **OFF**.
   - *Files & Links → New link format:* **"Shortest path when possible"**.
   - Result: typing autocompletes like wikilinks, emits as relative markdown links (GitHub-renderable).

### 5.3 Repo-side artifacts

- `.mcp.json` at repo root — uses a bash wrapper as `command` to source `.env` before exec'ing `mcp-obsidian`. Concretely: `command: "bash", args: ["-c", "set -a && source .env && set +a && exec mcp-obsidian"]`. The wrapper inherits Claude Code's cwd (repo root), sources `.env` from there, then `exec` replaces the bash process with the MCP — env-var inheritance is straightforward shell semantics, no MCP-loader env-var-substitution magic involved. (We initially assumed `mcp-obsidian`'s built-in `load_dotenv()` would read `.env` from cwd; it doesn't — python-dotenv's `find_dotenv()` searches from the caller's `__file__` path under pipx's venv, not from cwd. The wrapper sidesteps this gotcha.)
- `.env` — contains `OBSIDIAN_API_KEY=...`. Already covered by the existing `.env` and `.env.*` rules in root `.gitignore`; no new gitignore entry required.
- `docs/.obsidian/` — split policy via additions to root `.gitignore`:
  - **Commit:** `app.json`, `appearance.json`, `core-plugins.json`, `community-plugins.json`.
  - **Gitignore:** `workspace.json`, `workspace-mobile.json`, `workspaces.json`, `plugins/*/data.json` (the API token lives in plugin data — never commit).

The token therefore exists in two gitignored locations: the plugin's own `data.json` (where Obsidian stores it) and `.env` (where the MCP server reads it). Both are excluded from version control.

### 5.4 Tool routing for docs questions

| Question shape | Tool |
|---|---|
| "Search docs for term Y" | `obsidian_simple_search` |
| "All docs with tag `auth`" or other frontmatter queries | `obsidian_complex_search` (JsonLogic) |
| "Read this specific note" | `obsidian_get_file_contents` |
| "Read N specific notes in one call" | `obsidian_batch_get_file_contents` |
| "List vault root contents" / "list a folder" | `obsidian_list_files_in_vault` / `obsidian_list_files_in_dir` |
| **"Which docs reference X?" (backlinks)** | **`Grep`** — this MCP variant has no backlinks tool |
| **Rename a note** | **`Write` move + `Grep` sweep for referrers** — no atomic rename available |

Editing markdown content (text, frontmatter values, code blocks) → `Edit` / `Write`. Single write path for content.

### 5.5 Write-path policy

The (b) write-path policy from earlier drafts has fully collapsed to read-only because the MCP variant exposes no atomic rename or batch-tag tools (see §5.1).

- Content edits → `Edit` / `Write`.
- All Obsidian MCP write tools (`obsidian_append_content`, `obsidian_patch_content`, `obsidian_delete_file`) — **not allowlisted**; require explicit per-call user approval.
- Single write path for content; no MCP-mediated mutations to `docs/`.

## 6. Frontmatter schema

### 6.1 Core (required on every retrofitted file)

```yaml
---
title: "Identity & Authentication Architecture"
tags: [architecture, auth, phase-3]
status: active           # draft | active | superseded | deprecated
created: 2026-04-30
updated: 2026-04-30
---
```

### 6.2 Optional (per-folder, opt-in)

| Field | Where used | Purpose |
|---|---|---|
| `supersedes: [old-doc.md]` | architecture, decisions | this doc replaces others |
| `related-issues: [STRG-022, STRG-074]` | any | GitHub issue IDs (string-only — no auto-link, but searchable via `obsidian_simple_search`) |
| `phase: 3` | architecture, requirements | matches MEMORY.md Phase 1-13 entries |
| `priority: must-have` | requirements | one of `must-have | nice-to-have | future` |
| `decision-date: 2026-04-15` | decisions | ADR ratification date |

### 6.3 Design rule

Frontmatter is cheap to *add* later but expensive to *retrofit*. Start minimal; extend in Phase 2 (rolling) only when a real query demands a missing field. No speculative metadata.

## 7. Tag taxonomy

Three orthogonal axes per file.

### 7.1 Type (exactly one)

`architecture | requirement | decision | spec | issue-cc | reference`

### 7.2 Domain (zero or more)

Seed list (extracted from project memory entries):

`auth | identity | storage | path | tenancy | quota | tagging | versioning | graphql | outbox | events | audit | webdav | tus | upload | encryption | rate-limit | csp | validation | plugins | inbox`

Not exhaustive — new tags introduced organically. PR review polices duplicates (e.g., `auth` vs. `authentication` — pick one and stick to it).

### 7.3 Phase (zero or one)

`phase-1` … `phase-13` — corresponds to the project-memory phase entries (`project_phase1_decisions.md` through `project_phase13_*` and `project_inbox_decisions.md`).

## 8. CLAUDE.md addendum

A new section to add to CLAUDE.md, slotted between "Code Conventions" and "Database":

```markdown
## Codebase & Docs Search Tooling

Two MCP servers augment text search. Reach for them per the tables below; fall back to built-in tools (Grep, Glob, Read) only when neither fits.

### Serena — semantic C# search

| Question shape | Tool |
|---|---|
| "Where is `X` defined?" | `find_symbol` |
| "Who calls `X`?" | `find_referencing_symbols` |
| "What's in this file/class?" | `get_symbols_overview` |
| Forbidden-pattern enforcement (e.g., `IgnoreQueryFilters` outside the carve-out) | `search_for_pattern` |
| Rename a symbol + update all callers atomically | `rename_symbol` |

Fall back to `Grep` only for non-symbol text (string literals, comments, log messages, JSON values).

### Obsidian — docs read-only queries

`docs/` is an Obsidian vault. Use Obsidian MCP for **read-only** queries:

| Question shape | Tool |
|---|---|
| "Search docs for term Y" | `obsidian_simple_search` |
| "All docs with tag `auth`" or other frontmatter queries | `obsidian_complex_search` (JsonLogic) |
| "Read this specific note" | `obsidian_get_file_contents` |
| "Read N specific notes in one call" | `obsidian_batch_get_file_contents` |
| "List vault root contents" / "list a folder" | `obsidian_list_files_in_vault` / `obsidian_list_files_in_dir` |

**Editing markdown** — always `Edit` / `Write`. The MCP variant exposes `obsidian_append_content`, `obsidian_patch_content`, and `obsidian_delete_file`, but they are NOT allowlisted; require explicit per-call user approval.

**Backlinks** — this MCP variant does not expose a backlinks tool. To find references to a doc, use `Grep` for the filename or wikilink syntax.

**Renames** — there is no atomic rename-with-backlink-update. Rename via `Write` (move file) plus `Grep`-and-replace for referrers.
```

## 9. `.claude/settings.json` allowlist

Adds a `permissions.allow` array. The existing `hooks.PostToolUse` block (the `dotnet format` hook) is preserved unchanged.

```json
{
  "permissions": {
    "allow": [
      "mcp__plugin_serena_serena__find_symbol",
      "mcp__plugin_serena_serena__find_referencing_symbols",
      "mcp__plugin_serena_serena__get_symbols_overview",
      "mcp__plugin_serena_serena__search_for_pattern",
      "mcp__plugin_serena_serena__list_dir",
      "mcp__plugin_serena_serena__find_file",
      "mcp__plugin_serena_serena__rename_symbol",
      "mcp__obsidian__obsidian_simple_search",
      "mcp__obsidian__obsidian_complex_search",
      "mcp__obsidian__obsidian_get_file_contents",
      "mcp__obsidian__obsidian_batch_get_file_contents",
      "mcp__obsidian__obsidian_list_files_in_vault",
      "mcp__obsidian__obsidian_list_files_in_dir"
    ]
  },
  "hooks": { /* existing dotnet format hook unchanged */ }
}
```

Tools NOT on the list (`replace_symbol_body`, `insert_after_symbol`, `insert_before_symbol`, `safe_delete_symbol`, `obsidian_append_content`, `obsidian_patch_content`, `obsidian_delete_file`, periodic-notes tools) require explicit per-call approval. Tool names are verified against the installed `mcp-obsidian` v0.2.2 surface (see §5.1).

## 10. Phase 1 retrofit checklist

Files retrofitted in a single PR (15 total):

| # | File | Type | Phase |
|---|---|---|---|
| 1 | `docs/issues/README.md` | reference | — |
| 2 | `docs/architecture/01-system-overview.md` | architecture | 1 |
| 3 | `docs/architecture/02-storage-abstraction.md` | architecture | 4 |
| 4 | `docs/architecture/03-identity.md` | architecture | 3 |
| 5 | `docs/architecture/04-event-system.md` | architecture | 8 |
| 6 | `docs/architecture/05-deployment.md` | architecture | — |
| 7 | `docs/requirements/01-overview.md` | requirement | — |
| 8 | `docs/requirements/02-functional-requirements.md` | requirement | — |
| 9 | `docs/requirements/03-non-functional.md` | requirement | — |
| 10 | `docs/requirements/04-api-design.md` | requirement | 7 |
| 11 | `docs/requirements/05-data-model.md` | requirement | 1 |
| 12 | `docs/requirements/06-plugin-system.md` | requirement | 11 |
| 13 | `docs/requirements/07-security.md` | requirement | 10 |
| 14 | `docs/requirements/08-innovative-features.md` | requirement | — |
| 15 | `docs/decisions/001-adr-hybrid-api.md` | decision | 7 |

Per file:

- `title:` from H1 (fallback: derived from filename).
- `tags:` type (column above) + domain (extracted while reading content) + phase (column above).
- `status: active`.
- `created:` from `git log --diff-filter=A --follow --format=%aI -- <file> | tail -1`.
- `updated: 2026-04-30`.

Phase mapping is provisional and refined per-file as content is read during implementation. This is a one-time refinement during Phase 1, not a future re-migration.

## 11. Verification

After install, all of the following should hold:

1. **Serena onboarding succeeded** — `find_symbol("StoragePath")` returns `src/Strg.Core/Storage/StoragePath.cs`.
2. **Obsidian MCP reachable** — `obsidian_list_files_in_vault()` returns the vault top-level (`architecture/`, `requirements/`, etc.). (Original "backlinks" gate dropped — see §5.1.)
3. **Allowlist active** — `find_symbol` runs without a permission prompt.
4. **Existing format hook intact** — edit any `.cs` file; `dotnet format` hook fires.
5. **Frontmatter parse-clean** — Obsidian's tag pane lists every type/domain/phase tag from the 15 retrofitted files; `obsidian_complex_search` with JsonLogic `{"in": ["architecture", {"var": "tags"}]}` returns the 5 files in `docs/architecture/`. (Exact JsonLogic shape verified at install.)

## 12. Rollback

Each component is independently reversible:

- **Serena** — delete `.serena/`; no source changes were made.
- **Obsidian MCP** — drop its entry from `.mcp.json`, remove `.env`, optionally uninstall the Local REST API plugin in Obsidian. Vault metadata (frontmatter, links) survives as plain markdown — harmless if the MCP is gone.
- **Phase 1 frontmatter / link retrofit** — `git revert` the retrofit commit.
- **CLAUDE.md addendum** — `git revert`.
- **`.claude/settings.json` allowlist** — `git revert`.

Retrofit content is **strictly additive** — rollback only removes tooling; doc files remain as plain markdown.

## 13. Future work (deferred)

- **GitHub MCP integration** — addresses the issue/PR graph navigation pain (option `b3` from the brainstorm). Most natural follow-up.
- **`ast-grep` MCP** — adds AST-pattern search for CLAUDE.md "Forbidden Patterns" enforcement. Revisit only after living with Serena for 1-2 weeks.
- **Per-layer `CLAUDE.md` files** in `src/Strg.*` for onboarding-cost reduction (pain E).
- **Auto-memory as a peer Obsidian vault** rooted at `~/.claude/projects/.../memory/` — opened separately, not folded into the strg vault.

## 14. Open questions / install-time verification

Status as of 2026-04-30:

1. **Exact tool names exposed by `mcp-obsidian`** — RESOLVED. The MarkusPfundstein variant v0.2.2 exposes 12 tools (see §5.1); none of `get_backlinks`, `find_notes_by_tag`, `rename_note`, or `batch_update_tags` exist. §§5.4, 5.5, 8, 9 updated to match. Env-var name corrected to `OBSIDIAN_API_KEY`.
2. **Whether `.mcp.json` supports `${ENV_VAR}` substitution.** RESOLVED — moot. We don't rely on Claude Code substitution at all. The `.mcp.json` `command` is a bash wrapper that sources `.env` from cwd and exec's `mcp-obsidian`. (Initial assumption that `mcp-obsidian`'s own `load_dotenv()` would read `.env` from cwd turned out wrong — python-dotenv's `find_dotenv()` walks up from `__file__`, not cwd. The wrapper makes the env load explicit and cwd-relative.)
3. **Whether Serena's C# language-server backend handles .NET 10 cleanly.** RESOLVED. `find_symbol(StoragePath)` and `find_referencing_symbols(StoragePath.Parse)` both succeed against the live codebase; LSP indexes 30+ references across project boundaries.
