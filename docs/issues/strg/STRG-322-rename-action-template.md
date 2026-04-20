---
id: STRG-322
title: Rename action with template engine
milestone: v0.2
priority: medium
status: open
type: implementation
labels: [inbox, actions, rename, template]
depends_on: [STRG-308]
blocks: []
assigned_agent_type: feature-dev
estimated_complexity: medium
---

# STRG-322: Rename action with template engine

## Summary

Implement the `RenameAction` and the `ITemplateEngine` that evaluates rename templates with format-string tokens. A template like `{date:yyyy}/{date:MM}/{basename}{ext}` produces a full relative path including path separators, effectively combining rename and move into one action. When the template output contains `/`, the file is moved accordingly.

## Technical Specification

### New action type (`src/Strg.Core/Domain/Inbox/InboxAction.cs`)

Add to the existing `[JsonDerivedType]` list and add the record:

```csharp
[JsonDerivedType(typeof(RenameAction), "rename")]
```

```csharp
/// <summary>
/// Rename the file using a template. If the template produces path separators,
/// the file is also moved to the resulting subfolder.
/// TargetDriveId = null → stay on the source file's drive.
/// </summary>
public record RenameAction(
    string Template,
    Guid? TargetDriveId = null,
    ConflictResolution ConflictResolution = ConflictResolution.AutoRename,
    bool AutoCreateFolders = true
) : InboxAction(ConflictResolution, AutoCreateFolders);
```

### Template engine interface (`src/Strg.Core/Inbox/ITemplateEngine.cs`)

```csharp
public record TemplateContext(
    FileItem File,
    int Counter          // current sequential counter for {counter} token
);

public interface ITemplateEngine
{
    /// <summary>
    /// Renders a template string against the given file context.
    /// Returns the rendered path (may contain '/' separators).
    /// </summary>
    string Render(string template, TemplateContext context);
}
```

### Supported tokens

| Token | Example output | Notes |
|---|---|---|
| `{name}` | `photo.jpg` | Full file name including extension |
| `{basename}` | `photo` | File name without extension |
| `{ext}` | `.jpg` | Extension with leading dot; empty string if none |
| `{date:format}` | `2026-04-19` | Upload timestamp in given format (standard .NET date format strings) |
| `{year}` | `2026` | Shorthand for `{date:yyyy}` |
| `{month}` | `04` | Shorthand for `{date:MM}` |
| `{day}` | `19` | Shorthand for `{date:dd}` |
| `{mime_type}` | `image/jpeg` | Full MIME type |
| `{mime_group}` | `image` | The part before the `/` |
| `{counter:format}` | `001` | Zero-padded sequential counter scoped to the target folder |
| `{random}` | `a3f7` | 4-character hex fragment of a new `Guid` |

### Implementation (`src/Strg.Infrastructure/Inbox/TemplateEngine.cs`)

```csharp
public sealed class TemplateEngine : ITemplateEngine
{
    private static readonly Regex TokenRegex = new(@"\{(\w+)(?::([^}]+))?\}", RegexOptions.Compiled);

    public string Render(string template, TemplateContext ctx)
    {
        var file = ctx.File;
        var ext = Path.GetExtension(file.Name);
        var basename = Path.GetFileNameWithoutExtension(file.Name);

        return TokenRegex.Replace(template, m =>
        {
            var token = m.Groups[1].Value.ToLowerInvariant();
            var format = m.Groups[2].Success ? m.Groups[2].Value : null;

            return token switch
            {
                "name"      => file.Name,
                "basename"  => basename,
                "ext"       => ext,
                "date"      => file.CreatedAt.ToString(format ?? "yyyy-MM-dd"),
                "year"      => file.CreatedAt.ToString("yyyy"),
                "month"     => file.CreatedAt.ToString("MM"),
                "day"       => file.CreatedAt.ToString("dd"),
                "mime_type" => file.MimeType,
                "mime_group"=> file.MimeType.Split('/')[0],
                "counter"   => ctx.Counter.ToString(format ?? "0"),
                "random"    => Guid.NewGuid().ToString("N")[..4],
                _           => m.Value  // unknown token — pass through unchanged
            };
        });
    }
}
```

### Counter resolution (in InboxProcessingConsumer)

When a `RenameAction` is executed and the template contains `{counter}`:
1. Determine the target folder from the rendered partial path (strip the filename part).
2. Count files in that folder matching the same basename pattern.
3. Render the template with `Counter = count + 1`.

### DI registration

```csharp
services.AddSingleton<ITemplateEngine, TemplateEngine>(); // stateless, safe as singleton
```

### GraphQL input type update

Add `RenameActionInput` to `InboxActionInput`:

```graphql
input RenameActionInput {
  template: String!
  targetDriveId: ID
  conflictResolution: ConflictResolution = AUTO_RENAME
  autoCreateFolders: Boolean = true
}
```

## Acceptance Criteria

- [ ] `RenameAction` record exists in `Strg.Core` with JSON serialization support
- [ ] `ITemplateEngine` interface in `Strg.Core` with zero external NuGet deps
- [ ] `TemplateEngine` implementation in `Strg.Infrastructure`
- [ ] All 11 token types render correctly
- [ ] Template with `/` separators triggers a move to the resulting subfolder
- [ ] `{counter:000}` produces a 3-digit zero-padded sequential number
- [ ] `{random}` produces a different value on each call
- [ ] Unknown tokens are passed through unchanged (no error)
- [ ] `RenameAction` registered as `[JsonDerivedType]` in `InboxAction.cs`

## Test Cases

- TC-001: `{year}/{month}/{name}` for a JPEG uploaded 2026-04-19 → `2026/04/photo.jpg`
- TC-002: `{mime_group}/{basename}_{counter:000}{ext}` → `image/photo_001.jpg` (first file in folder)
- TC-003: `{random}_{name}` → different prefix on each render
- TC-004: `{basename}{ext}` (no path) → same filename, no move
- TC-005: Template with unknown token `{foo}` → `{foo}` preserved in output; no exception
- TC-006: `{date:yyyy-MM-dd HH:mm}` → `2026-04-19 14:30` format

## Implementation Tasks

- [ ] Add `RenameAction` to `InboxAction.cs`
- [ ] Create `ITemplateEngine.cs` in `Strg.Core/Inbox/`
- [ ] Create `TemplateEngine.cs` in `Strg.Infrastructure/Inbox/`
- [ ] Implement counter resolution helper in `InboxProcessingConsumer`
- [ ] Add `[JsonDerivedType(typeof(RenameAction), "rename")]`
- [ ] Add `RenameActionInput` GraphQL input type
- [ ] Register `ITemplateEngine` as singleton
- [ ] Write unit tests for all token types

## Definition of Done

- [ ] `dotnet build` passes with zero warnings
- [ ] All TC-001 through TC-006 tests pass
- [ ] `ITemplateEngine` in `Strg.Core` with no external NuGet deps
