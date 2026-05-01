---
title: "Thumbnail Generation"
tags: [architecture, thumbnails, plugins, phase-15]
status: active
created: 2026-05-01
updated: 2026-05-01
---

# Thumbnail Generation

## Design

strg generates image thumbnails asynchronously after each upload. Three variants are produced (`thumb=256`, `small=512`, `medium=1024`), output as WebP at quality 82, letterboxed onto a white square canvas. Generation runs inside the API host process for v1; `STRG-344` documents the runway for moving to a sandboxed worker process.

The subsystem is structured around three contracts in `Strg.Core`:

- `IThumbnailGenerator` — handlers self-declare via `CanHandle(mimeType, magicBytes)`. The consumer never branches on MIME.
- `IThumbnailGeneratorRegistry` — first-registered match wins.
- `IThumbnailService` — orchestrator the consumer talks to.

Behind these, `Strg.Infrastructure` ships `MagickNetImageThumbnailer` (Apache 2.0 licensed Magick.NET, including native libheif for HEIC). Phase 16 will add a PDF generator and an Office generator without changing the consumer, the API, the database schema, or any of the events.

A `ThumbnailEntry` row tracks each generated thumbnail. The row carries `Status` (`Pending` / `Ready` / `Failed` / `Unsupported`), the storage key, dimensions, and a `GeneratorVersion` field that lets us trigger forced regeneration when the algorithm changes.

---

## Decisions

| # | Decision | Options | Chosen |
|---|----------|---------|--------|
| D1 | When to generate | eager / async / lazy | **async** — upload latency matters more than first-view latency |
| D2 | What stores the thumbnail blob | same `IStorageProvider` + namespaced key / dedicated provider / inline bytea | **same provider, `thumbnails/{driveId}/{fileVersionId}/{variant}.{format}`** |
| D3 | What tracks state/metadata | new `ThumbnailEntry` entity / fields on `FileVersion` / cache-only | **new `ThumbnailEntry` entity** |
| D4 | Encryption | plaintext always / inherit drive posture / fresh DEK | **inherit drive posture** |
| D5 | Image library | Magick.NET / NetVips / SkiaSharp / ImageSharp | **Magick.NET (Apache 2.0)** behind `IImageThumbnailer` interface. ImageSharp's license change is a ship-stopper |
| D6 | PDF library (Phase 16) | PDFtoImage / Docnet.Core / PdfPig | **PDFtoImage or Docnet.Core** — PdfPig can't rasterize |
| D7 | Office documents (Phase 16) | LibreOffice headless / Office Interop / skip | **LibreOffice headless, opt-in, feature-flagged** |
| D8 | Variants | fixed vs on-demand | **fixed `[thumb=256, small=512, medium=1024]`** |
| D9 | Output format | WebP only / WebP + JPEG fallback / AVIF | **WebP primary, JPEG fallback only if UA can't** |
| D10 | Crop vs contain | letterbox / cover / per-variant | **contain/letterbox, white background** |
| D11 | What types get thumbnails | whitelist MIME / sniff magic bytes / handler-declared | **handler-declared via `IThumbnailGenerator.CanHandle(mimeType, magicBytes)`** |
| D12 | MIME trust | trust client / sniff at upload / sniff at thumbnail time | **sniff at thumbnail time (magic bytes)** |
| D13 | Generation failure mode | retry forever / mark unsupported / mark failed+retry budget | **three-state: Ready / Failed / Unsupported** |
| D14 | Resource safeguards | nothing / pixel-cap + timeout + mem cap / process isolation | **pixel-area cap + timeout + max-source-size, in-proc for v1**; sandbox as follow-up |
| D15 | Backfill for existing files | leave empty / admin-triggered / auto on first access | **admin-triggered bulk consumer, idempotent** |
| D16 | Quota accounting | count against user / operator absorbs | **operator absorbs** (parallels encryption overhead) |
| D17 | Encrypted drives in v1 | ship decryption-aware generator / defer | **defer** — no public decryption abstraction exists yet |

---

## Image Library Selection (D5)

| Library | License | HEIC | EXIF | Streaming | Verdict |
|---|---|---|---|---|---|
| Magick.NET (`Magick.NET-Q8-x64`) | Apache 2.0 | Yes (native libheif) | Full | Yes | **Chosen** |
| NetVips | LGPL | Yes (libheif) | Full | Yes | LGPL not aligned with project preference for permissive dependencies |
| SkiaSharp | MIT | No | No EXIF | Yes | HEIC blocks v1 |
| ImageSharp (Six Labors) | Six Labors Split License | Yes | Full | Yes | **License-change ship-stopper.** The Six Labors split license shifted commercial use behind a paid plan in late 2024 — incompatible with the project's permissive posture |

The thumbnailer sits behind `IThumbnailGenerator`. If a future ImageMagick CVE forces a swap, the contract is unchanged — only the implementation is replaced.

**Native dependencies.** `Magick.NET-Q8-x64` includes most format support, but HEIC requires the host's libheif system library. The Docker base image installs `libheif1`, `libheif-plugin-aomdec`, and `libheif-plugin-libde265`. If libheif is absent at runtime, HEIC reads fall through to `SourceCorrupt` — graceful degradation rather than a crash.

**Quality and bit depth.** Q8 is sufficient for thumbnails at 256/512/1024 px. Q16 doubles the in-memory bitmap size for no perceptual gain at these dimensions.

---

## Quota Policy (D16)

Thumbnail bytes do **not** count against the user's quota. The operator absorbs the cost. This parallels the encryption-overhead precedent: `FileVersion.BlobSizeBytes` already excludes the AES-GCM frame overhead from user-visible quota, on the same reasoning — operator-driven storage amplification is not the user's concern.

If thumbnail bytes were charged to users, every algorithm change (e.g., bumping `WebPQuality` from 82 to 90) would silently shift quota usage across the entire fleet. Centralising the cost on the operator decouples that knob from the user contract.

The thumbnail blobs are still subject to per-drive storage limits at the storage-provider layer. Operators who run tight on space can reduce variant count or quality via `ThumbnailOptions`.

---

## Encrypted-Drive Carve-out (D17)

A drive with `EncryptionEnabled = true` stores ciphertext blobs that only the per-version `FileKey`-derived DEK can decrypt. The thumbnail consumer reads via `IStorageProvider.ReadAsync`, which on an encrypted drive returns ciphertext bytes — not plaintext.

In v1, no public decryption abstraction exists. `ChunkedGcmDecryptStream` is `internal sealed` and used only inside `AesGcmFileWriter`'s write-back self-test. The consumer cannot decrypt the source without an `IEncryptingFileReader` extraction — which is its own substantial piece of work (it must integrate with the `FileKey` resolver, replicate the AAD framing, and gracefully handle key-rotation events).

The carve-out: when `drive.EncryptionEnabled == true`, the consumer writes a single `ThumbnailEntry { Status = Unsupported, ErrorReason = "encrypted-drive-not-yet-supported" }` and exits. The REST endpoint serves 404 for the missing thumbnail; GraphQL surfaces `status: UNSUPPORTED, errorReason: "encrypted-drive-not-yet-supported"` so clients can render an icon.

A future issue (planned as STRG-347) extracts `IEncryptingFileReader` and unblocks both encrypted-drive thumbnails AND the regular encrypted-drive download path. That work is the prerequisite, not the thumbnail subsystem itself.

---

## Extension Map (Phase 16 Readiness)

The v1 architecture is engineered so the Phase 16 PDF and Office generators land additively. **No** consumer change, API change, schema change, or event change is needed:

| Extension point | Where | What changes when PDF (Phase 16) lands |
|---|---|---|
| `IThumbnailGenerator.CanHandle` | `Strg.Core/Services/IThumbnailGenerator.cs` | New generator self-declares `application/pdf`. Consumer untouched. |
| `MimeSniffer` whitelist | `Strg.Core/Media/MimeSniffer.cs` | `%PDF-` already covered. No change. |
| `ThumbnailStorageKeyBuilder` | `Strg.Core/Services/ThumbnailStorageKeyBuilder.cs` | `Format` parameterizes; same scheme. No change. |
| `ThumbnailEntry.Format` | `Strg.Core/Domain/ThumbnailEntry.cs` | Free-text column. No schema migration. |
| `IConsumer<ThumbnailGenerationRequestedEvent>` | backfill consumer | Same event covers PDF backfill. No change. |
| `Thumbnails:PdfEnabled` config flag | `ThumbnailOptions` | Already present in v1; flips on. |
| `Thumbnails:OfficeEnabled` config flag | `ThumbnailOptions` | Already present in v1; flips on. |

A test in the v1 suite proves the contract: a fake generator registered via DI is routed to by the backfill mutation without any code in the consumer / API / DB layer changing.

---

## Event Flow

| Event | Handler | Action |
|---|---|---|
| `FileUploadedEvent` | `ThumbnailGenerationConsumer` | Generate per-variant thumbnails |
| `ThumbnailGenerationRequestedEvent` | `ThumbnailGenerationConsumer` | Backfill (admin-triggered or algorithm-bump regen) |
| `FileDeletedEvent` | `ThumbnailCleanupConsumer` | Soft-delete thumbnail rows + best-effort blob delete |
| `ThumbnailReadyEvent` | `GraphQlSubscriptionPublisher` | Push to `thumbnailReady(fileId)` GraphQL subscribers |

The backfill event is **dedicated**, NOT a republished `FileUploadedEvent`, because republishing would double-write `AuditEntry` rows via `AuditLogConsumer`. The two events share the consumer; only the upload path triggers audit.

Consumer guarantees apply uniformly:

- **At-least-once delivery.** Idempotency via `(FileVersionId, Variant, Format)` unique index, name pinned in `ThumbnailConstraintNames.UniqueIndex`. The consumer catches `PostgresException.SqlState == "23505"` with exact `ConstraintName` equality (mirror of the audit consumer's pattern).
- **Outbox semantics.** Every `IPublishEndpoint.Publish` call happens BEFORE `SaveChangesAsync`, so the event and the row update commit atomically.
- **Tenant from payload.** The consumer never reads ambient `ITenantContext` (which is empty in consumer scope); tenant ID is on every event payload.

---

## Resource Safeguards

Three layered checks defend against decompression bombs and runaway generators:

1. **Source-size cap.** `FileVersion.Size > Thumbnails:MaxSourceSizeBytes` (default 256 MiB) → `Unsupported{too-large}`. Rejected before any blob read.
2. **Pixel-area cap.** `MagickImageInfo` reads the first 64 KiB to extract dimensions, then `Width * Height > Thumbnails:MaxPixelArea` (default 100 MP) → `ResourceLimitExceeded` → `Unsupported{pixel-cap}`. Rejected before full decode.
3. **Generation timeout.** Per-call `CancellationTokenSource(Thumbnails:GenerationTimeoutSeconds)` (default 30 s) linked with the consumer's CT → `TimedOut` → `Failed{timeout}`. Distinguishes from caller cancellation.

The timeout cancellation is observable via the `strg_thumbnails_generated_total{status=timed-out}` counter, so operators can tune the limit against real workloads without modifying code.

For long-term hardening (process isolation, sandboxed worker), see `07-thumbnail-sandboxing.md` (planned design document — the current safeguards are deliberately conservative because in-process is the v1 ceiling).

---

## Out of Scope (Phase 15)

- **PDF rasterization.** Reserved for Phase 16. PDFium / Docnet.Core integration with the same `IThumbnailGenerator` contract.
- **Office documents.** Reserved for Phase 16. LibreOffice headless out-of-process, opt-in via `Thumbnails:OfficeEnabled`.
- **Encrypted-drive thumbnails.** Carved out per D17. Requires `IEncryptingFileReader` extraction first (planned).
- **RAW formats** (CR2/NEF/ARW/DNG). Magick.NET can extract embedded JPEG previews but RAW handling is its own size budget. Deferred.
- **SVG input.** Security swamp (embedded scripts, external refs). If shipped, must rasterize via librsvg with network and script disabled.
- **Animated thumbnails.** First frame only. Animated thumbnails are a distinct UX feature.
- **Generic fallback icons.** Backend reports `Unsupported`; the client picks the icon. Backend stays UI-agnostic.
- **AI auto-tagging coupling.** Auto-tagging needs the unmodified source EXIF. It runs on a separate consumer path, not on `ThumbnailReadyEvent`.

---

## License Hygiene

| Component | License | Compatible? |
|---|---|---|
| Magick.NET-Q8-x64 (managed wrapper) | Apache 2.0 | Yes |
| Bundled ImageMagick (native) | ImageMagick (Apache-like) | Yes |
| libheif (system, runtime-linked) | LGPL | Yes (dynamic link) |
| PDFium (Phase 16) | BSD 3-Clause + Apache 2.0 | Yes |
| LibreOffice (Phase 16, out-of-process) | MPL 2.0 | Yes (out-of-process — no static link) |

All compatible with strg's Apache 2.0 license. No GPL dependencies enter the binary.

---

## Re-generation on Algorithm Change

`ThumbnailEntry.GeneratorVersion` is a string field set when the row is written. It is not wired to any auto-regeneration trigger in v1; it exists so a future operator-action ("force regenerate everything older than version X") can target the right rows without scanning blob bytes.

The admin backfill mutation (planned) accepts a candidate predicate. A version-bump regeneration would extend that predicate to `WHERE GeneratorVersion != currentVersion`. Cheap to include now, costly to retrofit later — same logic as audit-row foreign keys: add the column at table-creation time even if unused.
