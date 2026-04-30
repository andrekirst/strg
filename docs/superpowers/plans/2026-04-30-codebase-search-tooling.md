# Codebase & Docs Search Tooling Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` (recommended) or `superpowers:executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Activate Serena MCP for C# semantic search, set up `docs/` as an Obsidian vault with REST-API MCP, retrofit 15 high-value docs with frontmatter, and codify tool-routing in CLAUDE.md and `.claude/settings.json`.

**Architecture:** Two MCPs on hard-split substrates — Serena indexes only `src/` + `tests/` via Roslyn LSP; Obsidian indexes only `docs/` via the Local REST API plugin. `Edit` / `Write` remains the canonical write path for content. MCP writes are restricted to graph-aware operations (`rename_symbol`, `rename_note`, `batch_update_tags`).

**Tech Stack:** Serena MCP (already loaded), `MarkusPfundstein/mcp-obsidian` (Python REST-API MCP), Obsidian Desktop + Local REST API community plugin, YAML frontmatter, relative markdown links.

**Spec:** `docs/superpowers/specs/2026-04-30-codebase-search-tooling-design.md` — read before starting any task.

---

## Pre-flight

- [ ] **Step 0.1: Re-read the spec end-to-end.** Pay attention to §3 (substrate split), §4.4 + §5.5 (write-path policy), §10 (retrofit table), §14 (open install-time questions).
- [ ] **Step 0.2: Confirm working directory.** `pwd` should print `/home/andrekirst/git/github/andrekirst/strg`. `git status` should be clean (the spec commit `5cfa6aa` is the latest).
- [ ] **Step 0.3: Verify Serena MCP is connected.** In a Claude Code session, the tool list should include `mcp__plugin_serena_serena__find_symbol`. If not, this plan is blocked until Serena is reconnected.

---

## Phase A — Serena Onboarding

### Task 1: Add `.serena/cache/` to `.gitignore` *before* creating any Serena state

**Why first:** Serena writes its symbol cache under `.serena/cache/` during onboarding. Adding the gitignore pattern *before* the cache exists means a single clean commit later — no accidental cache-staging.

**Files:**
- Modify: `.gitignore` (append a `.serena/` block)

- [ ] **Step 1.1: Read current `.gitignore` tail.**

```bash
tail -20 .gitignore
```

- [ ] **Step 1.2: Append the Serena block.**

Use `Edit` to add the following at the end of `.gitignore`:

```
# Serena MCP project state
.serena/cache/
```

(Project config `.serena/project.yml` is committed; the cache directory is not. `.serena/memories/` is left tracked-by-default for now — Serena's project-shared notes are valuable to commit if they accumulate; revisit if they prove transient.)

- [ ] **Step 1.3: Verify.**

```bash
grep -A1 "Serena MCP" .gitignore
```

Expected output:
```
# Serena MCP project state
.serena/cache/
```

- [ ] **Step 1.4: Commit.**

```bash
git add .gitignore
git commit -m "chore(gitignore): exclude Serena cache directory

Prepares repo for Serena MCP onboarding. .serena/project.yml will
be committed; .serena/cache/ is build state and is excluded.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

### Task 2: Create `.serena/project.yml` and run onboarding

**Files:**
- Create: `.serena/project.yml`

- [ ] **Step 2.1: Verify `.serena/` does not exist.**

```bash
ls .serena 2>/dev/null && echo "EXISTS — investigate" || echo "absent — proceed"
```

Expected: `absent — proceed`. If `EXISTS`, stop and ask the user whether to wipe and re-onboard.

- [ ] **Step 2.2: Create `.serena/project.yml` with `Write`.**

Path: `.serena/project.yml`

Content:

```yaml
project_name: strg
language: csharp
ignore_all_files_in_gitignore: true
ignored_paths:
  - "**/*.g.cs"
  - "**/bin/"
  - "**/obj/"
  - "**/Migrations/"
  - "artifacts/"
```

- [ ] **Step 2.3: Run Serena's `onboarding` tool.**

Invoke `mcp__plugin_serena_serena__onboarding` with no arguments. Expect a multi-minute first-run indexing pass (Roslyn LSP cold start + 537 .cs files).

If onboarding errors, capture the full error message verbatim before retrying. Do **not** rerun blindly — the most likely cause is the C# language-server backend struggling with .NET 10. If that's the failure, document it as a blocker in this task's checklist and stop.

- [ ] **Step 2.4: Verify symbol indexing succeeded.**

Invoke `mcp__plugin_serena_serena__find_symbol` with parameters that resolve `StoragePath`. Expected hit: `src/Strg.Core/Storage/StoragePath.cs`.

If the call returns no symbols, onboarding silently failed — re-run Step 2.3 and watch for output diagnostics.

- [ ] **Step 2.5: Verify referencing-symbols works.**

Invoke `mcp__plugin_serena_serena__find_referencing_symbols` for `StoragePath.Parse`. Expected: at least one hit per consuming surface (REST endpoints, GraphQL resolvers, WebDAV handlers, application handlers). Empty result = LSP didn't fully index references; re-run onboarding.

- [ ] **Step 2.6: Commit.**

```bash
git add .serena/project.yml
git commit -m "feat(tooling): add Serena MCP project config

Onboards strg/ into Serena with C# semantics and EF-generated paths
excluded. Symbol cache at .serena/cache/ is gitignored. Verified
find_symbol(StoragePath) and find_referencing_symbols(StoragePath.Parse)
return expected hits.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Phase B — Obsidian Vault + MCP

### Task 3: Manual host setup (per-user, not committable)

**Files:** None committed. This task documents the per-user setup that must precede Task 4.

- [ ] **Step 3.1: Install Obsidian Desktop.**

On Linux, choose one of:
- AppImage from https://obsidian.md (verified by user, not Claude)
- `snap install obsidian --classic`
- `.deb` package from the Obsidian release page

Verify by launching the app at least once.

- [ ] **Step 3.2: Open `docs/` as a vault.**

In Obsidian: *Open folder as vault* → select `/home/andrekirst/git/github/andrekirst/strg/docs`.

This creates `docs/.obsidian/` populated with default configs.

- [ ] **Step 3.3: Install the Local REST API community plugin.**

In Obsidian: *Settings → Community plugins → Browse → search "Local REST API" → Install*. Author: Adam Coddington.

- [ ] **Step 3.4: Enable the plugin and capture the API token.**

After enabling: *Settings → Local REST API → API Key (copy)*. Save the token; it's needed in Task 4.

The plugin stores it under `docs/.obsidian/plugins/obsidian-local-rest-api/data.json` (gitignored in Task 5).

- [ ] **Step 3.5: Apply Obsidian's link settings.**

*Settings → Files & Links*:
- *Use `[[Wikilinks]]`* → **OFF**
- *New link format* → **Shortest path when possible**

These changes write to `docs/.obsidian/app.json`, which Task 5 commits.

- [ ] **Step 3.6: Verify Local REST API responds.**

```bash
curl -s -k -H "Authorization: Bearer <PASTED_TOKEN>" https://127.0.0.1:27124/vault/ | head -50
```

Expected: a JSON listing of root vault files (e.g., `architecture/`, `requirements/`). Non-200 = the plugin isn't running or the token is wrong; do not proceed until this works.

(Note: Local REST API uses HTTPS with a self-signed cert on port 27124 by default; `curl -k` skips cert verification, which is acceptable for localhost.)

---

### Task 4: Install `mcp-obsidian` and register it in `.mcp.json`

**Files:**
- Create: `.mcp.json` at repo root
- Create: `.env.local` at repo root (per-user, gitignored in Task 5)

- [ ] **Step 4.1: Install `mcp-obsidian` (Python).**

```bash
pipx install mcp-obsidian
```

If `pipx` isn't installed, fall back to `pip install --user mcp-obsidian`. Verify the entry point:

```bash
which mcp-obsidian
```

Expected: a path inside `~/.local/bin/` or `~/.local/pipx/...`.

- [ ] **Step 4.2: Create `.env.local` with the Obsidian API key.**

Path: `.env.local`

Content (replace `<TOKEN>` with the value captured in Step 3.4):

```
OBSIDIAN_API_KEY=<TOKEN>
```

The env-var name `OBSIDIAN_API_KEY` is verified by source inspection of `mcp_obsidian/tools.py:12` and `server.py:26`. Host (127.0.0.1) and port (27124) are hardcoded defaults in `mcp_obsidian/obsidian.py:10-11` and not configurable via env-var; they match the Local REST API plugin's default listening address.

- [ ] **Step 4.3: Create `.mcp.json` registering the MCP server.**

Path: `.mcp.json`

Content:

```json
{
  "mcpServers": {
    "obsidian": {
      "command": "mcp-obsidian",
      "args": [],
      "env": {
        "OBSIDIAN_API_KEY": "${OBSIDIAN_API_KEY}"
      }
    }
  }
}
```

- [ ] **Step 4.4: Verify `${ENV_VAR}` substitution works in `.mcp.json`.**

Restart Claude Code. After restart, the tool list should include `mcp__obsidian__*` tools. If env-var substitution is unsupported, the server will fail to start — fall back to user-level config:

1. Move the `obsidian` server entry from `.mcp.json` to `~/.claude.json` under `mcpServers`, hardcoding the values from `.env.local`.
2. Delete `.mcp.json` (or leave it empty).
3. Update spec §5.3 with a note about the fallback.

- [ ] **Step 4.5: Verify the expected tool surface after MCP restart.**

After Step 4.4's restart, the tool list should include the following 12 tools (all prefixed `mcp__obsidian__`):

| Tool | Read/Write | Allowlisted? |
|---|---|---|
| `obsidian_list_files_in_vault` | R | yes |
| `obsidian_list_files_in_dir` | R | yes |
| `obsidian_get_file_contents` | R | yes |
| `obsidian_batch_get_file_contents` | R | yes |
| `obsidian_simple_search` | R | yes |
| `obsidian_complex_search` | R | yes |
| `obsidian_append_content` | W | no — explicit per-call approval |
| `obsidian_patch_content` | W | no — explicit per-call approval |
| `obsidian_delete_file` | W | no — explicit per-call approval |
| `obsidian_get_periodic_note` | R | no — periodic-notes workflow not used here |
| `obsidian_get_recent_periodic_notes` | R | no — same |
| `obsidian_get_recent_changes` | R | no — rare query, prompt-on-call is fine |

If any of the six allowlisted tools are missing, stop and reconcile. The spec was already updated for the missing-tool reality (`get_backlinks`, `find_notes_by_tag` as a dedicated tool, `rename_note`, `batch_update_tags` are all absent in v0.2.2 — see spec §5.1). Verified by source inspection of `mcp_obsidian/tools.py` at install time.

- [ ] **Step 4.6: Commit `.mcp.json` only (NOT `.env.local`).**

```bash
git add .mcp.json
git status   # confirm .env.local is NOT staged
git commit -m "feat(tooling): register obsidian MCP server in .mcp.json

Adds the MarkusPfundstein/mcp-obsidian v0.2.2 REST-API server. The
single env reference (OBSIDIAN_API_KEY) lives in .env.local
(gitignored in Task 5). Tool surface (12 tools, 6 allowlisted) is
documented in the spec §5.1 / §9.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

- [ ] **Step 4.7: Spec reconciliation.**

The spec was reconciled in advance of execution after the install-time tool-surface inspection revealed `get_backlinks`, `find_notes_by_tag`, `rename_note`, and `batch_update_tags` are absent from `mcp-obsidian` v0.2.2. The (b) read-write policy fully collapsed to read-only. Spec §§5.1, 5.3, 5.4, 5.5, 6.2, 8, 9, 11, 14 already reflect the real surface. No additional spec edits expected here unless Step 4.5 surfaces new divergences.

---

### Task 5: Commit the vault skeleton + extend `.gitignore`

**Files:**
- Modify: `.gitignore` (add `.obsidian/` patterns + `.env.local`)
- Add: `docs/.obsidian/app.json`, `appearance.json`, `core-plugins.json`, `community-plugins.json`

- [ ] **Step 5.1: Inspect what Obsidian wrote under `docs/.obsidian/`.**

```bash
ls docs/.obsidian/
ls docs/.obsidian/plugins/ 2>/dev/null
```

Expected files include `app.json`, `appearance.json`, `core-plugins.json`, `community-plugins.json`, `workspace.json`, and a `plugins/obsidian-local-rest-api/` directory.

- [ ] **Step 5.2: Append the Obsidian + env block to `.gitignore`.**

Use `Edit` to append at the end of `.gitignore`:

```
# Obsidian per-user state (vault config files are committed below)
docs/.obsidian/workspace.json
docs/.obsidian/workspace-mobile.json
docs/.obsidian/workspaces.json
docs/.obsidian/plugins/*/data.json

# Per-user environment (Obsidian REST API token, etc.)
.env.local
```

- [ ] **Step 5.3: Verify the gitignore matches both directions.**

```bash
git check-ignore -v docs/.obsidian/workspace.json   # should match
git check-ignore -v docs/.obsidian/app.json         # should NOT match
git check-ignore -v .env.local                      # should match
```

Expected: first and third return a line, second returns empty exit code 1.

- [ ] **Step 5.4: Stage the four committed Obsidian configs.**

```bash
git add docs/.obsidian/app.json docs/.obsidian/appearance.json docs/.obsidian/core-plugins.json docs/.obsidian/community-plugins.json
git add .gitignore
git status
```

Expected `git status`: those 5 files staged, `.env.local` and `workspace*.json` untracked-but-ignored.

- [ ] **Step 5.5: Commit.**

```bash
git commit -m "feat(tooling): commit Obsidian vault skeleton for docs/

Persists shared editor config (app/appearance/core-plugins/community-
plugins.json) for the docs/ vault. Per-user state (workspace*.json,
plugins/*/data.json — including the API token) and .env.local are
gitignored.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Phase C — Frontmatter Retrofit

### Task 6: Phase 1 retrofit — 15 files in one commit

**Files:**
- Modify: 15 markdown files per spec §10.

This task uses TDD-shape: define expected post-state, verify pre-state, apply changes, verify post-state, commit.

- [ ] **Step 6.1: Verify pre-state — none of the 15 files have frontmatter.**

```bash
FILES=(
  docs/issues/README.md
  docs/architecture/01-system-overview.md
  docs/architecture/02-storage-abstraction.md
  docs/architecture/03-identity.md
  docs/architecture/04-event-system.md
  docs/architecture/05-deployment.md
  docs/requirements/01-overview.md
  docs/requirements/02-functional-requirements.md
  docs/requirements/03-non-functional.md
  docs/requirements/04-api-design.md
  docs/requirements/05-data-model.md
  docs/requirements/06-plugin-system.md
  docs/requirements/07-security.md
  docs/requirements/08-innovative-features.md
  docs/decisions/001-adr-hybrid-api.md
)
for f in "${FILES[@]}"; do
  head -1 "$f" | grep -q "^---$" && echo "$f: already has frontmatter — STOP" || echo "$f: clean"
done
```

Expected: all 15 print `clean`. If any prints `STOP`, investigate before proceeding.

- [ ] **Step 6.2: Capture git creation date per file.**

```bash
FILES=(
  docs/issues/README.md
  docs/architecture/01-system-overview.md
  docs/architecture/02-storage-abstraction.md
  docs/architecture/03-identity.md
  docs/architecture/04-event-system.md
  docs/architecture/05-deployment.md
  docs/requirements/01-overview.md
  docs/requirements/02-functional-requirements.md
  docs/requirements/03-non-functional.md
  docs/requirements/04-api-design.md
  docs/requirements/05-data-model.md
  docs/requirements/06-plugin-system.md
  docs/requirements/07-security.md
  docs/requirements/08-innovative-features.md
  docs/decisions/001-adr-hybrid-api.md
)
for f in "${FILES[@]}"; do
  echo -n "$f → created="
  git log --diff-filter=A --follow --format=%aI -- "$f" | tail -1
done
```

Save the output — each file's `created:` value comes from this. (The `updated:` value is `2026-04-30` for all 15.)

- [ ] **Step 6.3: For each file, read content + insert frontmatter using `Edit`.**

Per spec §10, the 15 files break into 4 buckets:

**Bucket A — `docs/issues/README.md`** (1 file, type=`reference`)

Read the H1 of `docs/issues/README.md`. Construct frontmatter:

```yaml
---
title: "<H1 text or fallback 'Issues Index'>"
tags: [reference, claude-code-workflow]
status: active
created: <git creation date>
updated: 2026-04-30
---

```

Insert via `Edit` at the very top of the file (above the H1 line).

**Bucket B — `docs/architecture/*.md`** (5 files, type=`architecture`, phase per spec §10 table)

For each architecture file, read its H1 and the content's first paragraph to determine domain tags. Compose frontmatter:

```yaml
---
title: "<H1 text>"
tags: [architecture, <domain tags from content>, phase-<N or omit>]
status: active
created: <git creation date>
updated: 2026-04-30
---

```

Domain-tag hints from the spec:
- `01-system-overview.md` → `tags: [architecture, phase-1]` (no specific domain — system-wide)
- `02-storage-abstraction.md` → `tags: [architecture, storage, path, phase-4]`
- `03-identity.md` → `tags: [architecture, auth, identity, phase-3]`
- `04-event-system.md` → `tags: [architecture, outbox, events, audit, phase-8]`
- `05-deployment.md` → `tags: [architecture]` (no phase tagged in §10 table)

Refine each by reading the actual content; if the content reveals additional domain hooks (e.g., `tenancy` mentioned heavily), add the tag.

**Bucket C — `docs/requirements/*.md`** (8 files, type=`requirement`)

Per spec §6.2, requirements may carry `priority:`. Read each file's content to determine: must-have (default for v0.1), nice-to-have, or future.

Frontmatter shape:

```yaml
---
title: "<H1 text>"
tags: [requirement, <domain tags from content>, phase-<N if applicable>]
status: active
priority: must-have   # adjust per content
created: <git creation date>
updated: 2026-04-30
---

```

Phase mapping from §10:
- `04-api-design.md` → phase-7
- `05-data-model.md` → phase-1
- `06-plugin-system.md` → phase-11
- `07-security.md` → phase-10

Files without a phase column omit `phase-<N>` from `tags:`.

**Bucket D — `docs/decisions/001-adr-hybrid-api.md`** (1 file, type=`decision`)

Read the file. Determine `decision-date:` from the document body if stated, else fall back to git creation date. Frontmatter:

```yaml
---
title: "<H1 text>"
tags: [decision, graphql, phase-7]
status: active
decision-date: <date from doc or git>
created: <git creation date>
updated: 2026-04-30
---

```

- [ ] **Step 6.4: Verify all 15 files now parse as frontmatter.**

```bash
FILES=(
  docs/issues/README.md
  docs/architecture/01-system-overview.md
  docs/architecture/02-storage-abstraction.md
  docs/architecture/03-identity.md
  docs/architecture/04-event-system.md
  docs/architecture/05-deployment.md
  docs/requirements/01-overview.md
  docs/requirements/02-functional-requirements.md
  docs/requirements/03-non-functional.md
  docs/requirements/04-api-design.md
  docs/requirements/05-data-model.md
  docs/requirements/06-plugin-system.md
  docs/requirements/07-security.md
  docs/requirements/08-innovative-features.md
  docs/decisions/001-adr-hybrid-api.md
)
for f in "${FILES[@]}"; do
  head -1 "$f" | grep -q "^---$" && echo "$f: ok" || echo "$f: MISSING frontmatter"
done
```

Expected: all 15 print `ok`.

- [ ] **Step 6.5: Verify YAML is well-formed.**

```bash
FILES=(
  docs/issues/README.md
  docs/architecture/01-system-overview.md
  docs/architecture/02-storage-abstraction.md
  docs/architecture/03-identity.md
  docs/architecture/04-event-system.md
  docs/architecture/05-deployment.md
  docs/requirements/01-overview.md
  docs/requirements/02-functional-requirements.md
  docs/requirements/03-non-functional.md
  docs/requirements/04-api-design.md
  docs/requirements/05-data-model.md
  docs/requirements/06-plugin-system.md
  docs/requirements/07-security.md
  docs/requirements/08-innovative-features.md
  docs/decisions/001-adr-hybrid-api.md
)
for f in "${FILES[@]}"; do
  python3 -c "import yaml,sys; yaml.safe_load(open('$f').read().split('---')[1])" && echo "$f: yaml ok" || echo "$f: YAML PARSE ERROR"
done
```

Expected: all 15 print `yaml ok`. Fix any errors before continuing.

- [ ] **Step 6.6: Verify against Obsidian via complex_search (tag query).**

Invoke `mcp__obsidian__obsidian_complex_search` with a JsonLogic query that filters notes whose `tags` array contains `architecture`:

```json
{"in": ["architecture", {"var": "tags"}]}
```

Expected: returns exactly the 5 files in `docs/architecture/`.

Then with `requirement`: expect exactly the 8 files in `docs/requirements/`.

If counts disagree, inspect file frontmatter for typos. If the JsonLogic shape rejects (e.g., `tags` not exposed at top level — may need `frontmatter.tags`), adjust the query path and retry. Document the working query shape inline so subsequent verifications use the same form.

- [ ] **Step 6.7: Commit the retrofit.**

```bash
git add docs/issues/README.md docs/architecture/ docs/requirements/ docs/decisions/
git commit -m "docs: phase 1 frontmatter retrofit on 15 high-value docs

Adds YAML frontmatter (title, tags, status, created, updated, plus
optional priority/decision-date/phase per spec §6) to:
- docs/issues/README.md (1)
- docs/architecture/*.md (5)
- docs/requirements/*.md (8)
- docs/decisions/001-adr-hybrid-api.md (1)

Verified by obsidian_complex_search with tag-containment JsonLogic
query: architecture → 5 files, requirement → 8 files. Phase 2
(rolling) handles new docs going forward.

Spec: docs/superpowers/specs/2026-04-30-codebase-search-tooling-design.md

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Phase D — Conventions

### Task 7: CLAUDE.md tool-routing addendum

**Files:**
- Modify: `CLAUDE.md` (insert new section between line 223 and line 224 — after "Code Conventions", before "Database")

- [ ] **Step 7.1: Verify the insertion anchor.**

```bash
sed -n '220,228p' CLAUDE.md
```

Expected: line 223 is the last line of the "Code Conventions" section (the Microsoft conventions link), line 224 is blank, line 225 is `## Database` (or close — confirm with output and adjust the next step accordingly).

- [ ] **Step 7.2: Insert the addendum via `Edit`.**

`old_string`: the closing line of "Code Conventions" plus the blank line and `## Database` heading (capture exact text including newlines from Step 7.1 output).

`new_string`: the same closing line, blank line, then the new section, then a blank line, then `## Database`.

New section content:

```markdown
## Codebase & Docs Search Tooling

Two MCP servers augment text search. Reach for them per the tables below; fall back to built-in tools (`Grep`, `Glob`, `Read`) only when neither fits.

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

**Editing markdown** — always `Edit` / `Write`. The MCP exposes `obsidian_append_content`, `obsidian_patch_content`, and `obsidian_delete_file`, but they are NOT allowlisted; require explicit per-call approval.

**Backlinks** — this MCP variant does not expose a backlinks tool. To find references to a doc, use `Grep` for the filename or wikilink syntax.

**Renames** — there is no atomic rename-with-backlink-update. Rename via `Write` (move file) plus `Grep`-and-replace for referrers.

```

The Obsidian table reflects `mcp-obsidian` v0.2.2's actual surface (verified during install).

- [ ] **Step 7.3: Verify the addendum is in place.**

```bash
grep -A2 "## Codebase & Docs Search Tooling" CLAUDE.md | head -3
```

Expected: the heading and following lines from the addendum.

- [ ] **Step 7.4: Commit.**

```bash
git add CLAUDE.md
git commit -m "docs(claude): add codebase & docs search tooling addendum

Codifies the tool-routing convention: Serena for C# symbols, Obsidian
MCP (read-only, v0.2.2) for docs queries, Edit/Write for content
edits. Single write path for content; Serena rename_symbol is the
only graph-aware MCP write allowlisted.

Spec: docs/superpowers/specs/2026-04-30-codebase-search-tooling-design.md §8

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

### Task 8: `.claude/settings.json` permissions allowlist

**Files:**
- Modify: `.claude/settings.json` (add a `permissions.allow` array; preserve existing `hooks` block)

- [ ] **Step 8.1: Read current `.claude/settings.json`.**

```bash
cat .claude/settings.json
```

Expected: a JSON object with one key, `hooks`, containing the existing `dotnet format` PostToolUse hook.

- [ ] **Step 8.2: Replace via `Edit` to add `permissions` alongside `hooks`.**

`old_string`:

```json
{
  "hooks": {
```

`new_string`:

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
  "hooks": {
```

The Obsidian entries match `mcp-obsidian` v0.2.2's actual tool names (verified during install). The 6 Obsidian write/periodic tools (`obsidian_append_content`, `obsidian_patch_content`, `obsidian_delete_file`, `obsidian_get_periodic_note`, `obsidian_get_recent_periodic_notes`, `obsidian_get_recent_changes`) are intentionally NOT allowlisted; they require explicit per-call approval.

- [ ] **Step 8.3: Verify the JSON is well-formed.**

```bash
python3 -c "import json; json.load(open('.claude/settings.json'))" && echo "ok"
```

Expected: `ok`. Any error here is a JSON syntax issue from the edit — fix and re-run before continuing.

- [ ] **Step 8.4: Verify the allowlist takes effect.**

Restart Claude Code (or reload settings — `/clear` is not enough; the harness reads settings at session start). Invoke `mcp__plugin_serena_serena__find_symbol` for `StoragePath`. Expected: no permission prompt; tool runs immediately.

If a prompt appears, the allowlist key may differ in this Claude Code version (e.g., `permissions.allowed` vs. `permissions.allow`); consult the harness docs and adjust.

- [ ] **Step 8.5: Verify the existing `dotnet format` hook still fires.**

Edit any `.cs` file (e.g., add a trailing newline to `src/Strg.Core/Storage/StoragePath.cs`, then revert), and confirm the hook output appears. Expected: `dotnet format --verify-no-changes` runs and reports clean (or whatever the current state is).

- [ ] **Step 8.6: Commit.**

```bash
git add .claude/settings.json
git commit -m "feat(claude): allowlist Serena and Obsidian MCP read tools

Adds permissions.allow for the read-only Serena and Obsidian MCP
tools plus three graph-aware writes (rename_symbol, rename_note,
batch_update_tags). All other MCP write tools (replace_symbol_body,
delete_note, etc.) continue to require explicit per-call approval.
Existing dotnet format PostToolUse hook is unchanged.

Spec: docs/superpowers/specs/2026-04-30-codebase-search-tooling-design.md §9

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>"
```

---

## Phase E — Verification

### Task 9: Final verification (5 gates from spec §11)

This task runs no edits — only verifies. If any gate fails, drop back into the responsible task and re-execute.

- [ ] **Step 9.1: Gate 1 — Serena symbol lookup.**

Invoke `mcp__plugin_serena_serena__find_symbol("StoragePath")`. Expected: returns `src/Strg.Core/Storage/StoragePath.cs`.

- [ ] **Step 9.2: Gate 2 — Obsidian MCP reachable.**

`mcp-obsidian` v0.2.2 has no backlinks tool. Substitute reachability check: invoke `mcp__obsidian__obsidian_list_files_in_vault` (no args). Expected: returns vault top-level entries (`architecture/`, `requirements/`, `issues/`, `decisions/`, `superpowers/`).

If the call errors with auth failure → `OBSIDIAN_API_KEY` mismatch between Obsidian's plugin token and `.env.local`. If the call errors with connection-refused → Obsidian Desktop not running or the Local REST API plugin disabled.

- [ ] **Step 9.3: Gate 3 — Allowlist active.**

`mcp__plugin_serena_serena__find_symbol` runs without a permission prompt (already validated in Step 8.4 — re-confirm).

- [ ] **Step 9.4: Gate 4 — Existing format hook intact.**

Touch any `.cs` file via `Edit`, confirm the `dotnet format` hook output appears. (Already validated in Step 8.5 — re-confirm if multiple commits have happened since.)

- [ ] **Step 9.5: Gate 5 — Frontmatter parse-clean.**

Invoke `mcp__obsidian__obsidian_complex_search` with the JsonLogic query `{"in": ["architecture", {"var": "tags"}]}`. Expected: 5 files (the ones in `docs/architecture/`).

Then with `{"in": ["requirement", {"var": "tags"}]}`. Expected: 8 files.

(If the JsonLogic shape rejects, refer to Step 6.6's adjusted query — same shape used here.)

- [ ] **Step 9.6: Final summary.**

Write a short verification report into the close-out commit message — not a new file. Sample:

```
verification: all 5 gates pass

- Serena find_symbol(StoragePath) → src/Strg.Core/Storage/StoragePath.cs
- Obsidian list_files_in_vault → architecture/ requirements/ issues/
  decisions/ superpowers/
- Allowlist active: find_symbol prompted? no
- dotnet format hook fires on .cs edit? yes
- complex_search tag=architecture → 5; tag=requirement → 8
```

There's no commit here — verification doesn't change any file. If you want a marker, the optional Step 9.7 covers it.

- [ ] **Step 9.7 (optional): Mark plan complete.**

Add a `**Status: implemented** (verified <date>)` line at the top of this plan file and commit. Skip if you'd rather track this externally.

---

## Open install-time questions (carry forward from spec §14)

Status:

1. **Tool name divergence** — RESOLVED 2026-04-30. `mcp-obsidian` v0.2.2 has no `get_backlinks`, `find_notes_by_tag`, `rename_note`, or `batch_update_tags`; the (b) read-write policy fully collapsed to read-only. Spec §§5.1, 5.4, 5.5, 8, 9, 11 reflect actual surface.
2. **`.mcp.json` env-var substitution** — open. Verified during Task 4.4 (post-restart). Fallback to user-level config if unsupported.
3. **Serena C# backend on .NET 10** — RESOLVED 2026-04-30. Verified by Task 2.5: `find_symbol(StoragePath)` and `find_referencing_symbols(StoragePath.Parse)` both return rich, accurate results across project boundaries.

---

## Rollback (per spec §12)

Per-component, independently reversible. If a Phase fails partway:

- **Phase A failure** — `git revert` Tasks 1-2's commits; delete `.serena/`.
- **Phase B failure** — `git revert` Tasks 3-5's commits; remove the obsidian entry from `.mcp.json`; delete `.env.local`; uninstall the Local REST API plugin in Obsidian (optional).
- **Phase C failure** — `git revert` Task 6's commit. Frontmatter is strictly additive; reverting leaves the docs as plain markdown.
- **Phase D failure** — `git revert` Tasks 7-8's commits.

Vault metadata (frontmatter, links) survives any tooling rollback as plain markdown.
