---
id: STRG-335
title: ADR — image library selection + quota policy + encrypted-drive carve-out
milestone: v0.2
priority: medium
status: open
type: docs
labels: [thumbnails, phase-15, adr, architecture]
depends_on: []
blocks: [STRG-336]
assigned_agent_type: feature-dev:code-explorer
estimated_complexity: small
---

# STRG-335: ADR — image library selection + quota policy + encrypted-drive carve-out

## Summary

Author the architecture decision record at `docs/architecture/06-thumbnails.md` (NOT `05-` — `05-deployment.md` exists). Captures decisions D1–D17 from issue #52 with rationale durable enough to survive contributor churn.

## Background / Context

Issue #52 made 17 binding decisions (D1–D17) about thumbnail generation. These decisions need a durable home so future engineers don't re-litigate them. Particular pressure points: image-library choice (D5 — ImageSharp's license change is a ship-stopper), encrypted-drive carve-out (D17 — requires STRG-347 to lift), and the Phase 16 extension contract.

## Technical Specification

### File — `docs/architecture/06-thumbnails.md`

YAML frontmatter (mirrors `04-event-system.md`):

```yaml
---
title: "Thumbnail Generation"
tags: [architecture, thumbnails, plugins, phase-15]
status: active
created: 2026-05-01
updated: 2026-05-01
---
```

H1 + H2 sections only (no H3 per existing ADR convention). Use `---` between major sections. Include code blocks with language hints. Use markdown tables for the decision matrix.

### Required sections

1. **Design** — async generation, blob storage in same provider, dedicated `ThumbnailEntry` entity, encryption-inheritance from drive posture.
2. **Decisions** — verbatim D1…D17 table from issue #52 (option / chosen / why) — this is the canonical reference.
3. **Image Library Selection (D5)** — Magick.NET (Apache 2.0) vs NetVips vs SkiaSharp vs ImageSharp; capture the ImageSharp license-change ship-stopper, the Magick.NET-Q8-x64 native-libheif Docker-base-image consequence (STRG-337), and the `IImageThumbnailer` indirection that keeps the door open for swap.
4. **Quota Policy (D16)** — operator absorbs thumbnail bytes; parallels `FileVersion.BlobSizeBytes` encryption-overhead precedent. Documented so v0.2 quota work isn't reopened.
5. **Encrypted-Drive Carve-out (D17)** — explain why `IEncryptingFileReader` doesn't exist today (`ChunkedGcmDecryptStream` is `internal sealed` in `AesGcmFileWriter`'s self-test only). Document the consumer's check (`drive.EncryptionEnabled` → `Unsupported{encrypted-drive-not-yet-supported}`). Forward-pointer to STRG-347.
6. **Extension Map (Phase 16 readiness)** — table of extension points the v1 architecture must hit so PDF/Office land additively:

   | Extension point | Where | What changes when PDF (STRG-345) lands |
   |---|---|---|
   | `IThumbnailGenerator.CanHandle` | Core interface | new generator self-declares `application/pdf`; consumer untouched |
   | `MimeSniffer` whitelist | Core | `%PDF-` already covered; no change |
   | `ThumbnailStorageKeyBuilder` | Core | `Format` parameterizes; no change |
   | `ThumbnailEntry.Format` | Core entity | `Format` is a string column; no schema change |
   | `IConsumer<ThumbnailGenerationRequestedEvent>` | backfill | same event covers PDF; no change |
   | `Thumbnails:PdfEnabled` config | options | already in `ThumbnailOptions`; flips on |

7. **Event Flow** — table mirroring `04-event-system.md:76`:

   | Event | Handler | Action |
   |---|---|---|
   | `FileUploadedEvent` | `ThumbnailGenerationConsumer` | Generate per-variant thumbnails |
   | `ThumbnailGenerationRequestedEvent` | `ThumbnailGenerationConsumer` | Backfill (admin-triggered) |
   | `FileDeletedEvent` | `ThumbnailCleanupConsumer` | Soft-delete rows + best-effort blob delete |
   | `ThumbnailReadyEvent` | `GraphQlSubscriptionPublisher` | Push to `thumbnailReady(fileId)` subscribers |

8. **Resource Safeguards** — pixel-area cap (header probe BEFORE decode), per-generation timeout via `CancellationTokenSource`, source-size cap (file-size probe). In-process for v1; STRG-344 documents process-isolation follow-up.

9. **Out of Scope (Phase 15)** — RAW formats, SVG input, animated thumbnails (first-frame only), generic fallback icons (frontend), AI auto-tagging ordering coupling.

10. **License Hygiene** — Magick.NET Apache 2.0; bundled ImageMagick Apache-like; PDFium MIT/BSD (Phase 16); LibreOffice MPL (Phase 16, opt-in). All compatible with the project's Apache 2.0 license.

11. **Re-generation on algorithm change** — `ThumbnailEntry.GeneratorVersion` field; not wired in v1 but cheap to include now.

### Cross-reference style

Per existing ADR convention (`01-system-overview.md` … `05-deployment.md`):

- No internal-doc cross-links (each ADR is self-contained).
- External links allowed (e.g., Magick.NET GitHub URL, libheif page).
- No `STRG-xxx` issue citations inline (issue numbers belong in the issue tracker, not in architecture).

## Acceptance Criteria

- [ ] `docs/architecture/06-thumbnails.md` exists with the YAML frontmatter above.
- [ ] All 11 sections above are present.
- [ ] D1–D17 decision table is verbatim from issue #52.
- [ ] D5 (image library), D16 (quota policy), and D17 (encrypted-drive) each have a dedicated section with rationale.
- [ ] Phase 16 extension-map table is present.
- [ ] Event-flow table mirrors `04-event-system.md:76`.
- [ ] No internal-doc cross-links (matches existing ADR style).
- [ ] No STRG-xxx citations inline (architecture documents are issue-tracker-agnostic).

## Test Cases

- **TC-001**: Markdown lint — file renders correctly in Obsidian (manual check).
- **TC-002**: A new contributor reading only the ADR can answer: "why Magick.NET and not ImageSharp", "do thumbnails count against user quota", and "why don't encrypted drives get thumbnails in v1" — without follow-up questions.

## Implementation Tasks

- [ ] Author `docs/architecture/06-thumbnails.md` per the spec above.
- [ ] Verify the file renders via Obsidian (vault root is `docs/`).
- [ ] Confirm the file does not collide with existing 05-* (it shouldn't — 05 is `05-deployment.md`, 06 is free).

## Security Review Checklist

- [ ] No secrets, no internal infra details (e.g., specific S3 buckets, credentials).
- [ ] License-hygiene section is accurate (Apache 2.0, MIT/BSD, MPL — verified).
- [ ] Encrypted-drive section does NOT publish exploitable detail about the encryption-at-rest envelope format beyond what's already in `02-storage-abstraction.md`.

## Code Review Checklist

- [ ] Frontmatter keys match other ADRs (`title`, `tags`, `status`, `created`, `updated`).
- [ ] H1 title matches frontmatter title.
- [ ] H2-only section structure (no H3).
- [ ] Code blocks have language hints (`csharp`, `json`, `yaml`).

## Definition of Done

- [ ] ADR published at `docs/architecture/06-thumbnails.md`.
- [ ] Reviewed for accuracy against the actual code shape (post-implementation pass — schedule for after STRG-336).
