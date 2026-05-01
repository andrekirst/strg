---
id: STRG-330
title: IThumbnailService + IThumbnailGenerator + registry + MimeSniffer + key builder
milestone: v0.2
priority: high
status: open
type: feature
labels: [thumbnails, phase-15, abstractions, core]
depends_on: [STRG-329]
blocks: [STRG-331, STRG-336, STRG-339, STRG-340, STRG-342]
assigned_agent_type: feature-dev:code-architect
estimated_complexity: medium
---

# STRG-330: IThumbnailService + IThumbnailGenerator + registry + MimeSniffer + key builder

## Summary

Introduce all `Strg.Core` abstractions for thumbnail generation. Bundles four units of work — `IThumbnailService` (orchestrator), `IThumbnailGenerator` (generator contract with self-declaring `CanHandle`), `IThumbnailGeneratorRegistry` (resolve-by-MIME), `MimeSniffer` (zero-dep magic-byte whitelist), and `ThumbnailStorageKeyBuilder` (centralized key scheme). They are co-designed: splitting them forces churn on each side.

## Background / Context

Decision **D11** of issue #52 chose **handler-declared via `IThumbnailGenerator.CanHandle(mimeType, magicBytes)`** so the consumer never branches on MIME — new generators (PDF in STRG-345, Office in STRG-346) plug into the registry without consumer/API/DB changes. Decision **D12** chose **magic-byte sniffing at thumbnail time** because `FileItem.MimeType` is client-provided and untrusted. The registry mirrors `IStorageProviderRegistry` (first registered match wins). The key builder centralizes the `thumbnails/{driveId}/{fileVersionId}/{variant}.{format}` scheme so call sites never concatenate strings — same discipline as the existing `StorageKey` handling in `FileVersionStore`.

## Technical Specification

All types live in `Strg.Core` (zero NuGet deps).

### `IThumbnailGenerator` — `src/Strg.Core/Services/IThumbnailGenerator.cs`

```csharp
public interface IThumbnailGenerator
{
    bool CanHandle(string mimeType, ReadOnlySpan<byte> magicBytes);

    Task<ThumbnailGenerationOutcome> GenerateAsync(
        Stream source,
        ThumbnailRequest request,
        CancellationToken cancellationToken);
}

public sealed record ThumbnailRequest(
    string Variant,
    int TargetEdgePixels,
    string TargetFormat,
    long SourceSizeBytes,
    string SourceMimeType);

public abstract record ThumbnailGenerationOutcome
{
    public sealed record Success(Stream Output, int Width, int Height, string Format)
        : ThumbnailGenerationOutcome;
    public sealed record Unsupported(string Reason) : ThumbnailGenerationOutcome;
    public sealed record SourceCorrupt(string Reason) : ThumbnailGenerationOutcome;
    public sealed record ResourceLimitExceeded(string Reason) : ThumbnailGenerationOutcome;
    public sealed record TimedOut(TimeSpan Limit) : ThumbnailGenerationOutcome;
}
```

A typed result (sum type) — not exceptions — because each outcome maps to a different `ThumbnailEntry.Status`/`ErrorReason` and metric label.

### `IThumbnailGeneratorRegistry` — `src/Strg.Core/Services/IThumbnailGeneratorRegistry.cs`

```csharp
public interface IThumbnailGeneratorRegistry
{
    IThumbnailGenerator? Resolve(string mimeType, ReadOnlySpan<byte> magicBytes);
}
```

First-registered-wins. Implementation lives in Infrastructure (DI-resolves all `IThumbnailGenerator` registrations).

### `IThumbnailService` — `src/Strg.Core/Services/IThumbnailService.cs`

```csharp
public interface IThumbnailService
{
    Task GenerateAllAsync(
        Guid fileVersionId,
        Guid driveId,
        IReadOnlyList<string> variants,
        CancellationToken cancellationToken);
}
```

The orchestrator — the consumer talks to this, not to individual generators. Implementation in Infrastructure (STRG-331 wires the consumer; the service itself is provider-agnostic).

### `ThumbnailVariant` — `src/Strg.Core/Media/ThumbnailVariant.cs`

```csharp
public static class ThumbnailVariants
{
    public const string Thumb = "thumb";
    public const string Small = "small";
    public const string Medium = "medium";

    public static int EdgePixelsFor(string variant) => variant switch
    {
        Thumb => 256,
        Small => 512,
        Medium => 1024,
        _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, "Unknown variant"),
    };

    public static IReadOnlyList<string> All => new[] { Thumb, Small, Medium };
}
```

String constants (not enum) so future operator-tunable variants (config-driven) don't force a code change.

### `MimeSniffer` — `src/Strg.Core/Media/MimeSniffer.cs`

```csharp
public static class MimeSniffer
{
    public static string? Detect(ReadOnlySpan<byte> head);
}
```

Whitelist (first ~16 bytes), NOT libmagic:

| Format | Signature |
|---|---|
| JPEG | `FF D8 FF` |
| PNG | `89 50 4E 47 0D 0A 1A 0A` |
| GIF | `47 49 46 38 37 61` / `47 49 46 38 39 61` |
| WebP | `RIFF`?? `WEBP` (offset 8) |
| PDF | `25 50 44 46 2D` (`%PDF-`) |
| HEIC/HEIF | `ftyp` (offset 4) + brand in {`heic`, `heix`, `mif1`, `msf1`, `heim`, `heis`, `hevc`, `hevx`} |

PDF inclusion is intentional — the registry lookup will return `null` (no PDF generator in v1), which writes `Unsupported`. When STRG-345 adds the PDF generator, the sniffer is unchanged.

Returns canonical IANA MIME (`image/jpeg`, `image/png`, `image/gif`, `image/webp`, `application/pdf`, `image/heic`) or `null` for unknown.

Truncated input (less than the format's signature length) returns `null` — handlers must self-check.

### `ThumbnailStorageKeyBuilder` — `src/Strg.Core/Services/ThumbnailStorageKeyBuilder.cs`

```csharp
public static class ThumbnailStorageKeyBuilder
{
    public static string Build(Guid driveId, Guid fileVersionId, string variant, string format);
}
```

Returns `thumbnails/{driveId:D}/{fileVersionId:D}/{variant}.{format}`. `{variant}` and `{format}` MUST be from the validated whitelist; the builder asserts. Callers (consumer + endpoint) MUST wrap the result in `StoragePath.Parse()` before hitting `IStorageProvider` — this is documented on the method.

### Domain events — `src/Strg.Core/Events/`

```csharp
// ThumbnailReadyEvent.cs
public sealed record ThumbnailReadyEvent(
    Guid TenantId, Guid FileId, Guid FileVersionId,
    string Variant, string Format, int Width, int Height) : IDomainEvent;

// ThumbnailGenerationRequestedEvent.cs (backfill — dedicated, not republished FileUploadedEvent)
public sealed record ThumbnailGenerationRequestedEvent(
    Guid TenantId, Guid FileId, Guid FileVersionId, Guid DriveId) : IDomainEvent;
```

The dedicated backfill event — NOT republished `FileUploadedEvent` — avoids double-writing audit entries via `AuditLogConsumer`. The generation consumer (STRG-331) handles both event types by sharing a private orchestration method.

## Acceptance Criteria

- [ ] All interfaces, types, sniffer, key builder, and events live under `Strg.Core` with zero NuGet deps.
- [ ] `IThumbnailGenerator.CanHandle(string, ReadOnlySpan<byte>)` is the only branching surface for MIME — consumer never `if (mime == ...)`.
- [ ] `MimeSniffer.Detect` covers JPEG/PNG/GIF/WebP/PDF/HEIC and rejects truncated input.
- [ ] `ThumbnailStorageKeyBuilder.Build` produces stable, deterministic keys; xml-docs require `StoragePath.Parse()` wrapping at call sites.
- [ ] `ThumbnailGenerationOutcome` is a closed sum type (sealed records).
- [ ] No exceptions thrown for expected failure modes (`Unsupported`, `SourceCorrupt`, `ResourceLimitExceeded`, `TimedOut` — typed results).

## Test Cases

- **TC-001**: `MimeSniffer.Detect` returns `image/jpeg` for `[FF, D8, FF, ...]`, `image/png` for the 8-byte PNG signature, `image/webp` for `RIFF????WEBP`, `application/pdf` for `%PDF-`, `image/heic` for `....ftypheic`, and `null` for `[]` (truncated).
- **TC-002**: `ThumbnailStorageKeyBuilder.Build(d, v, "small", "webp")` returns the canonical scheme; calling with an unknown variant throws.
- **TC-003**: A test `IThumbnailGeneratorRegistry` registered with two generators returns the first registration whose `CanHandle` matches.
- **TC-004**: Architecture test confirms `Strg.Core` has no NuGet refs after this PR.

## Implementation Tasks

- [ ] Add `IThumbnailGenerator` + outcome sum type + `ThumbnailRequest`.
- [ ] Add `IThumbnailGeneratorRegistry`.
- [ ] Add `IThumbnailService`.
- [ ] Add `ThumbnailVariants` static class.
- [ ] Add `MimeSniffer` with the whitelist above.
- [ ] Add `ThumbnailStorageKeyBuilder`.
- [ ] Add `ThumbnailReadyEvent` + `ThumbnailGenerationRequestedEvent` under `Strg.Core/Events/`.
- [ ] Unit tests under `tests/Strg.Core.Tests/Media/MimeSnifferTests.cs` and `tests/Strg.Core.Tests/Thumbnails/`.

## Security Review Checklist

- [ ] `MimeSniffer` is a whitelist — unknown signatures return `null`, never a guess.
- [ ] `ThumbnailStorageKeyBuilder` asserts variant/format are from the whitelist; never accepts user input directly.
- [ ] `ThumbnailGenerationRequestedEvent` carries `TenantId` explicitly — consumer reads tenant from payload, not ambient context.
- [ ] No exception leaks user-controlled bytes; outcome's `Reason` strings are static.

## Code Review Checklist

- [ ] No reflection, no IL emit — pure interfaces and records.
- [ ] Records are `sealed`.
- [ ] xml-doc on each public type.
- [ ] CancellationToken parameter named `cancellationToken` (per CLAUDE.md).

## Definition of Done

- [ ] All acceptance criteria green with concrete evidence.
- [ ] Unit tests pass: `dotnet test tests/Strg.Core.Tests`.
- [ ] Architecture tests pass: `dotnet test tests/Strg.Architecture.Tests`.
- [ ] No NuGet packages added to `Strg.Core`.
