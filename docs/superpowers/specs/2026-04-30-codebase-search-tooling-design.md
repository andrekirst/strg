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

`MarkusPfundstein/mcp-obsidian` (Python, REST-API based). At install time, verify that the MCP exposes:

- A rename-with-backlink-update tool (provisional name: `rename_note`).
- A batch-tag-update tool (provisional name: `batch_update_tags`).

If either is missing, the read-write policy in §5.5 collapses to read-only and §5.4 / §9 are amended accordingly.

### 5.2 One-time host setup (per-user, not in repo)

1. Install Obsidian Desktop (Linux: AppImage / snap / `.deb`).
2. Open `docs/` (this repo) as a vault.
3. Install community plugin **Local REST API** (Settings → Community plugins → Browse).
4. Enable plugin; copy generated API token. The token is auto-stored by the plugin under `docs/.obsidian/plugins/obsidian-local-rest-api/data.json` (gitignored, see §5.3); copy it manually into `.env.local` so the MCP server can read it.
5. Apply Obsidian settings (encoded in committed configs):
   - *Files & Links → Use `[[Wikilinks]]`:* **OFF**.
   - *Files & Links → New link format:* **"Shortest path when possible"**.
   - Result: typing autocompletes like wikilinks, emits as relative markdown links (GitHub-renderable).

### 5.3 Repo-side artifacts

- `.mcp.json` at repo root — registers the `obsidian-mcp` server with `${OBSIDIAN_API_TOKEN}` env reference. **Verify env-var substitution support in `.mcp.json` at install time;** if unsupported, fall back to user-level config (`~/.claude.json`) and amend the spec.
- `.env.local` — contains `OBSIDIAN_API_TOKEN=...`. Added to root `.gitignore`.
- `docs/.obsidian/` — split policy via additions to root `.gitignore`:
  - **Commit:** `app.json`, `appearance.json`, `core-plugins.json`, `community-plugins.json`.
  - **Gitignore:** `workspace.json`, `workspace-mobile.json`, `workspaces.json`, `plugins/*/data.json` (the API token lives in plugin data — never commit).

The token therefore exists in two gitignored locations: the plugin's own `data.json` (where Obsidian stores it) and `.env.local` (where the MCP server reads it). Both are excluded from version control.

### 5.4 Tool routing for docs questions

| Question shape | Tool |
|---|---|
| "Which docs reference X?" | `get_backlinks` |
| "All docs tagged `auth`" | `find_notes_by_tag` |
| "Search docs for term Y" | `search_notes` |
| Rename a note + update backlinks atomically | `rename_note` |
| Add tag X to many notes | `batch_update_tags` |

Editing markdown content (text, frontmatter values, code blocks) → `Edit` / `Write`. Single write path for content.

### 5.5 Write-path policy

- Content edits → `Edit` / `Write`.
- Rename + backlink update → Obsidian MCP `rename_note` (allowlisted).
- Mass tag operations → Obsidian MCP `batch_update_tags` (allowlisted).
- All other Obsidian write tools (create-note, delete-note, content-replace, etc.) — **not allowlisted**; require explicit per-call user approval.

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
| `related-issues: [STRG-022, STRG-074]` | any | GitHub issue IDs (string-only — no auto-link, but searchable via `search_notes`) |
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

### Obsidian — docs graph

`docs/` is an Obsidian vault. Use Obsidian MCP for graph queries:

| Question shape | Tool |
|---|---|
| "Which docs reference X?" | `get_backlinks` |
| "All docs tagged `auth`" | `find_notes_by_tag` |
| "Search docs for term Y" | `search_notes` |
| Rename a note + update backlinks atomically | `rename_note` |
| Add tag X to many notes | `batch_update_tags` |

Editing markdown *content* (text, frontmatter values, code blocks): always use `Edit` / `Write`. Single write path for content.
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
      "mcp__obsidian__get_note",
      "mcp__obsidian__search_notes",
      "mcp__obsidian__get_backlinks",
      "mcp__obsidian__find_notes_by_tag",
      "mcp__obsidian__rename_note",
      "mcp__obsidian__batch_update_tags"
    ]
  },
  "hooks": { /* existing dotnet format hook unchanged */ }
}
```

Tools NOT on the list (`replace_symbol_body`, `insert_after_symbol`, `insert_before_symbol`, `safe_delete_symbol`, generic create-note, delete-note, content-replace, etc.) require explicit per-call approval. Obsidian tool names are best-guess for the MarkusPfundstein variant; verify and adjust during install.

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
2. **Obsidian MCP reachable** — `get_backlinks("issues/README.md")` returns expected referrers (post-retrofit).
3. **Allowlist active** — `find_symbol` runs without a permission prompt.
4. **Existing format hook intact** — edit any `.cs` file; `dotnet format` hook fires.
5. **Frontmatter parse-clean** — Obsidian's tag pane lists every type/domain/phase tag from the 15 retrofitted files (no parse errors); `find_notes_by_tag("architecture")` returns the 5 files in `docs/architecture/`.

## 12. Rollback

Each component is independently reversible:

- **Serena** — delete `.serena/`; no source changes were made.
- **Obsidian MCP** — drop its entry from `.mcp.json`, remove `.env.local`, optionally uninstall the Local REST API plugin in Obsidian. Vault metadata (frontmatter, links) survives as plain markdown — harmless if the MCP is gone.
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

These resolve during implementation, not now:

1. Exact tool names exposed by `mcp-obsidian` (the MarkusPfundstein variant) — may differ from the placeholders used in §5.4 and §9. Will update both sections to match.
2. Whether `.mcp.json` supports `${ENV_VAR}` substitution. If not, the MCP registration moves to `~/.claude.json` (user-level) and §5.3 is amended.
3. Whether Serena's C# language-server backend handles .NET 10 cleanly. First-run onboarding (§4.2) is the verification.
