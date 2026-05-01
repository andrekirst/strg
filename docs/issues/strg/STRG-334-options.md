---
id: STRG-334
title: ThumbnailOptions configuration record + startup validation
milestone: v0.2
priority: medium
status: open
type: feature
labels: [thumbnails, phase-15, configuration]
depends_on: []
blocks: [STRG-331, STRG-336, STRG-338, STRG-342]
assigned_agent_type: feature-dev:code-architect
estimated_complexity: small
---

# STRG-334: ThumbnailOptions configuration record + startup validation

## Summary

Introduce `ThumbnailOptions` — the strongly-typed config record for the thumbnail subsystem — bound to `Thumbnails:*` configuration with fail-fast startup validation.

## Background / Context

Issue #52 mandates the config gates `Thumbnails:PdfEnabled` / `Thumbnails:OfficeEnabled` exist **from day 1** even though Phase 16 ships the actual generators. Adding them later would force a config-schema migration on operators. Resource-safeguard limits (`MaxSourceSizeBytes`, `MaxPixelArea`, `GenerationTimeoutSeconds`) live in this options record so they can be tuned per deployment without code changes.

## Technical Specification

### Options record — `src/Strg.Infrastructure/Thumbnails/ThumbnailOptions.cs`

```csharp
public sealed class ThumbnailOptions
{
    public const string SectionName = "Thumbnails";

    public bool Enabled { get; init; } = true;
    public IReadOnlyList<string> Variants { get; init; } = new[] { "thumb", "small", "medium" };
    public long MaxSourceSizeBytes { get; init; } = 256L * 1024 * 1024;   // 256 MiB
    public long MaxPixelArea { get; init; } = 100_000_000;                 // 100 MP
    public int GenerationTimeoutSeconds { get; init; } = 30;
    public int WebPQuality { get; init; } = 82;
    public bool PdfEnabled { get; init; } = true;
    public bool OfficeEnabled { get; init; } = false;
}
```

### Validator — `src/Strg.Infrastructure/Thumbnails/ThumbnailOptionsValidator.cs`

```csharp
internal sealed class ThumbnailOptionsValidator : IValidateOptions<ThumbnailOptions>
{
    public ValidateOptionsResult Validate(string? name, ThumbnailOptions options) { ... }
}
```

Rules — fail-fast at startup, not first-use:

- `Variants` is non-empty.
- Every variant in `Variants` is in the `ThumbnailVariants.All` whitelist.
- `MaxSourceSizeBytes > 0`.
- `MaxPixelArea > 0`.
- `GenerationTimeoutSeconds > 0` and `<= 600` (10 min ceiling — anything larger is a misconfiguration).
- `WebPQuality` in `[1, 100]`.

### Startup wiring — `src/Strg.Api/Program.cs`

```csharp
builder.Services
    .AddOptions<ThumbnailOptions>()
    .Bind(builder.Configuration.GetSection(ThumbnailOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton<IValidateOptions<ThumbnailOptions>, ThumbnailOptionsValidator>();
```

`.ValidateOnStart()` ensures invalid config crashes the host at boot, not on first thumbnail generation.

### Default `appsettings.json` block

```json
{
  "Thumbnails": {
    "Enabled": true,
    "Variants": ["thumb", "small", "medium"],
    "MaxSourceSizeBytes": 268435456,
    "MaxPixelArea": 100000000,
    "GenerationTimeoutSeconds": 30,
    "WebPQuality": 82,
    "PdfEnabled": true,
    "OfficeEnabled": false
  }
}
```

## Acceptance Criteria

- [ ] `ThumbnailOptions` exists with the fields and defaults above.
- [ ] `ThumbnailOptionsValidator` enforces all six rules; each invalid case produces a distinct `ValidateOptionsResult.Fail` with a human-readable message naming the offending field.
- [ ] `.ValidateOnStart()` is in `Program.cs` — bad config crashes at boot.
- [ ] `appsettings.json` ships the defaults so operators see the schema.
- [ ] `PdfEnabled` and `OfficeEnabled` both exist in v1 (no Phase-16 schema migration needed).

## Test Cases

- **TC-001**: Empty `Variants` → validator returns `Fail`.
- **TC-002**: `Variants = ["enormous"]` (not in whitelist) → `Fail` with the bad value in the message.
- **TC-003**: `GenerationTimeoutSeconds = 0` → `Fail`; `= 601` → `Fail`; `= 30` → `Success`.
- **TC-004**: `WebPQuality = 0` → `Fail`; `= 101` → `Fail`; `= 82` → `Success`.
- **TC-005**: Integration test boots with default config and the host starts (no validation failure).

## Implementation Tasks

- [ ] Add `ThumbnailOptions`.
- [ ] Add `ThumbnailOptionsValidator`.
- [ ] Wire `.AddOptions<>().Bind().ValidateOnStart()` in `Program.cs`.
- [ ] Add defaults to `appsettings.json` and `appsettings.Development.json`.
- [ ] Unit tests for the validator under `tests/Strg.Api.Tests/Thumbnails/`.

## Security Review Checklist

- [ ] No secrets in `ThumbnailOptions` (provider credentials live elsewhere).
- [ ] `MaxSourceSizeBytes` cannot be set negative (validator catches it).
- [ ] Defaults prevent unbounded resource consumption (256 MiB / 100 MP / 30 s — all explicit).

## Code Review Checklist

- [ ] `init`-only properties (immutable after binding).
- [ ] No magic-string section names — use `ThumbnailOptions.SectionName`.
- [ ] No reliance on environment variables for options not in this record.

## Definition of Done

- [ ] All acceptance criteria green.
- [ ] `dotnet test tests/Strg.Api.Tests` passes.
- [ ] Host fails to start on invalid config; documented with a representative error message.
