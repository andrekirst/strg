---
id: STRG-321
title: EXIF/IPTC condition support + MetadataExtractor integration
milestone: v0.2
priority: medium
status: open
type: implementation
labels: [inbox, conditions, metadata, exif]
depends_on: [STRG-306, STRG-320]
blocks: []
assigned_agent_type: feature-dev
estimated_complexity: medium
---

# STRG-321: EXIF/IPTC condition support + MetadataExtractor integration

## Summary

Add `ExifCondition` and `TagCondition` condition types to the inbox evaluator. `ExifCondition` uses the `MetadataExtractor` NuGet package (drewnoakes/metadata-extractor-dotnet) to extract EXIF/IPTC values from image files. Metadata extraction is **lazy**: it occurs only when the first condition that needs it is reached during tree evaluation. `TagCondition` checks whether a file already has a specific tag applied via the existing tag system.

## Technical Specification

### New condition types (`src/Strg.Core/Domain/Inbox/InboxCondition.cs`)

Add to the existing `[JsonDerivedType]` list:

```csharp
[JsonDerivedType(typeof(ExifCondition), "exif")]
[JsonDerivedType(typeof(TagCondition), "tag")]
```

```csharp
/// <summary>
/// Matches an EXIF or IPTC field value.
/// Tag is a dot-notation key like "Exif.DateTimeOriginal" or "IPTC.Keywords".
/// </summary>
public record ExifCondition(
    string Tag,
    string Value,
    StringComparison Comparison = StringComparison.OrdinalIgnoreCase
) : InboxCondition;

/// <summary>Matches files that have (or do not have) a specific tag applied.</summary>
public record TagCondition(string TagName, bool MustExist = true) : InboxCondition;
```

### Metadata provider interface (`src/Strg.Core/Inbox/IFileMetadataProvider.cs`)

```csharp
public interface IFileMetadataProvider
{
    /// <summary>
    /// Extracts all available metadata from the file's storage stream.
    /// Returns an empty dictionary if the file type is not supported.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> ExtractAsync(FileItem file, CancellationToken ct = default);
}
```

### Implementation (`src/Strg.Infrastructure/Inbox/FileMetadataProvider.cs`)

```csharp
public sealed class FileMetadataProvider(IStorageProviderRegistry registry) : IFileMetadataProvider
{
    public async Task<IReadOnlyDictionary<string, string>> ExtractAsync(FileItem file, CancellationToken ct)
    {
        var provider = registry.Get(file.ProviderType);
        await using var stream = await provider.ReadAsync(StoragePath.Parse(file.Path).Value, ct);

        var directories = ImageMetadataReader.ReadMetadata(stream);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in directories)
        foreach (var tag in dir.Tags)
        {
            var key = $"{dir.Name}.{tag.Name}";
            var value = tag.Description ?? string.Empty;
            result[key] = value;
        }

        return result;
    }
}
```

**NuGet**: Add `MetadataExtractor` to `Strg.Infrastructure.csproj` only. `Strg.Core` has zero external NuGet deps.

### Updated evaluation context

The `InboxEvaluationContext` lazy metadata function is now populated by `FileMetadataProvider`:

```csharp
// In InboxProcessingConsumer
IReadOnlyDictionary<string, string>? cachedMetadata = null;

var evalCtx = new InboxEvaluationContext(file, key =>
{
    cachedMetadata ??= _metadataProvider.ExtractAsync(file, ct).GetAwaiter().GetResult();
    return cachedMetadata.TryGetValue(key, out var val) ? val : null;
});
```

This closure is called at most once per file (metadata is cached after first extraction).

### Updated evaluator (`src/Strg.Infrastructure/Inbox/InboxConditionEvaluator.cs`)

Add cases:

```csharp
ExifCondition e => EvaluateExif(e, ctx),
TagCondition t => EvaluateTag(t, ctx.File),
```

```csharp
private static bool EvaluateExif(ExifCondition e, InboxEvaluationContext ctx)
{
    var actual = ctx.GetMetadataValue(e.Tag);
    return actual != null && string.Equals(actual, e.Value, e.Comparison);
}

private bool EvaluateTag(TagCondition t, FileItem file)
{
    // Check _db.FileTags (from STRG-046) whether the file has the tag
    var hasTag = _db.FileTags.Any(ft => ft.FileId == file.Id && ft.Tag.Name == t.TagName);
    return t.MustExist ? hasTag : !hasTag;
}
```

### DI registration

```csharp
services.AddScoped<IFileMetadataProvider, FileMetadataProvider>();
```

## Acceptance Criteria

- [ ] `ExifCondition` and `TagCondition` types exist in `Strg.Core` with `[JsonDerivedType]`
- [ ] `IFileMetadataProvider` interface in `Strg.Core` with zero external deps
- [ ] `FileMetadataProvider` in `Strg.Infrastructure` uses `MetadataExtractor` NuGet
- [ ] Metadata extraction is lazy — only called when an `ExifCondition` or EXIF-dependent condition is reached
- [ ] Metadata is cached per evaluation (only one read of the file stream per file processing)
- [ ] `ExifCondition("Exif.Image Width", "4032")` evaluates correctly for a matching image
- [ ] `TagCondition("vacation", MustExist: true)` returns true only if file has the "vacation" tag
- [ ] `MetadataExtractor` NuGet is in `Strg.Infrastructure.csproj`, not `Strg.Core.csproj`

## Test Cases

- TC-001: `ExifCondition("Exif SubIFD.Date/Time Original", "2026:04:19")` matches a JPEG with that EXIF date
- TC-002: `ExifCondition` on a non-image file (e.g. PDF) returns false (no EXIF data)
- TC-003: `TagCondition("photo", MustExist: true)` — true when file has "photo" tag
- TC-004: `TagCondition("photo", MustExist: false)` — true when file does NOT have "photo" tag
- TC-005: File with both an `ExifCondition` and `MimeTypeCondition` — metadata extracted once
- TC-006: `ExifCondition` in an `OR` group with `MimeTypeCondition` that matches — metadata never extracted

## Implementation Tasks

- [ ] Add `ExifCondition` and `TagCondition` to `InboxCondition.cs`
- [ ] Create `IFileMetadataProvider.cs` in `Strg.Core/Inbox/`
- [ ] Create `FileMetadataProvider.cs` in `Strg.Infrastructure/Inbox/`
- [ ] Add `MetadataExtractor` to `Strg.Infrastructure.csproj`
- [ ] Update `InboxConditionEvaluator` with new condition handlers
- [ ] Update `InboxProcessingConsumer` to inject `IFileMetadataProvider` and pass it to evaluation context
- [ ] Add GraphQL input types for `ExifConditionInput` and `TagConditionInput` (update STRG-310 mutations)
- [ ] Write unit + integration tests

## Definition of Done

- [ ] `dotnet build` passes with zero warnings
- [ ] All TC-001 through TC-006 tests pass
- [ ] `Strg.Core` has zero new external NuGet dependencies
- [ ] `MetadataExtractor` only in `Strg.Infrastructure`
