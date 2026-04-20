---
id: STRG-306
title: Inbox condition evaluators (v0.1)
milestone: v0.1
priority: high
status: open
type: implementation
labels: [inbox, domain, rules]
depends_on: [STRG-302, STRG-031]
blocks: [STRG-308, STRG-320, STRG-327]
assigned_agent_type: feature-dev
estimated_complexity: medium
---

# STRG-306: Inbox condition evaluators (v0.1)

## Summary

Implement the condition evaluation engine for inbox rules. For v0.1, the evaluator handles `ConditionGroup` nodes with `And` operator and four concrete condition types: `MimeTypeCondition`, `NameGlobCondition`, `FileSizeCondition`, and `UploadDateTimeCondition`. The evaluator is designed for easy extension in v0.2 (STRG-320 adds OR/NOT; STRG-321 adds EXIF/tag conditions).

## Technical Specification

### Evaluation context (`src/Strg.Core/Inbox/InboxEvaluationContext.cs`)

```csharp
/// <summary>
/// All data available to condition evaluators for a single file.
/// Metadata extraction is lazy — the Func is called at most once per evaluation.
/// </summary>
public record InboxEvaluationContext(
    FileItem File,
    Func<string, string?> GetMetadataValue  // key → value; returns null if not extracted / key absent
);
```

### Evaluator interface (`src/Strg.Core/Inbox/IInboxConditionEvaluator.cs`)

```csharp
public interface IInboxConditionEvaluator
{
    bool Evaluate(InboxCondition condition, InboxEvaluationContext context);
}
```

### Implementation (`src/Strg.Infrastructure/Inbox/InboxConditionEvaluator.cs`)

```csharp
public sealed class InboxConditionEvaluator : IInboxConditionEvaluator
{
    public bool Evaluate(InboxCondition condition, InboxEvaluationContext ctx) =>
        condition switch
        {
            ConditionGroup g => EvaluateGroup(g, ctx),
            MimeTypeCondition m => EvaluateMime(m, ctx.File.MimeType),
            NameGlobCondition n => EvaluateGlob(n.Pattern, ctx.File.Name),
            FileSizeCondition s => EvaluateSize(s, ctx.File.Size),
            UploadDateTimeCondition d => EvaluateDateTime(d, ctx.File.CreatedAt),
            _ => throw new NotSupportedException($"Condition type '{condition.GetType().Name}' is not supported in v0.1.")
        };

    private bool EvaluateGroup(ConditionGroup g, InboxEvaluationContext ctx) =>
        g.Operator switch
        {
            LogicalOperator.And => g.Children.All(c => Evaluate(c, ctx)),
            // Or and Not are v0.2 (STRG-320)
            _ => throw new NotSupportedException($"Logical operator '{g.Operator}' is not supported in v0.1.")
        };

    private static bool EvaluateMime(MimeTypeCondition m, string actualMime)
    {
        if (m.MimeType.EndsWith("/*"))
        {
            var prefix = m.MimeType[..^2]; // strip "/*"
            return actualMime.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);
        }
        return string.Equals(actualMime, m.MimeType, StringComparison.OrdinalIgnoreCase);
    }

    private static bool EvaluateGlob(string pattern, string name)
    {
        // Translate shell glob to regex: * → .*, ? → ., escape rest
        var regexPattern = "^" + Regex.Escape(pattern)
            .Replace(@"\*", ".*")
            .Replace(@"\?", ".") + "$";
        return Regex.IsMatch(name, regexPattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
    }

    private static bool EvaluateSize(FileSizeCondition s, long bytes)
    {
        if (s.MinBytes.HasValue && bytes < s.MinBytes.Value) return false;
        if (s.MaxBytes.HasValue && bytes > s.MaxBytes.Value) return false;
        return true;
    }

    private static bool EvaluateDateTime(UploadDateTimeCondition d, DateTimeOffset uploadedAt)
    {
        if (d.DaysOfWeek?.Length > 0 && !d.DaysOfWeek.Contains(uploadedAt.DayOfWeek)) return false;
        if (d.HourFrom.HasValue && uploadedAt.Hour < d.HourFrom.Value) return false;
        if (d.HourTo.HasValue && uploadedAt.Hour > d.HourTo.Value) return false;
        if (d.After.HasValue && uploadedAt < d.After.Value) return false;
        if (d.Before.HasValue && uploadedAt > d.Before.Value) return false;
        return true;
    }
}
```

### DI registration (`src/Strg.Infrastructure/DependencyInjection.cs`)

```csharp
services.AddScoped<IInboxConditionEvaluator, InboxConditionEvaluator>();
```

## Acceptance Criteria

- [ ] `IInboxConditionEvaluator` interface exists in `Strg.Core` (no infra deps)
- [ ] `InboxEvaluationContext` record with lazy `GetMetadataValue` func exists in `Strg.Core`
- [ ] `InboxConditionEvaluator` implementation in `Strg.Infrastructure`
- [ ] MIME wildcard `image/*` matches `image/jpeg`, `image/png`, `image/webp`
- [ ] MIME exact `image/jpeg` does NOT match `image/png`
- [ ] Glob `*.jpg` matches `photo.jpg` but not `photo.jpeg`
- [ ] Glob `report-??-*.pdf` matches `report-Q1-2026.pdf`
- [ ] File size: `MinBytes=1024, MaxBytes=null` matches 5000 bytes; rejects 500 bytes
- [ ] Upload date/time: `DaysOfWeek=[Monday]` matches a Monday upload
- [ ] `ConditionGroup(And)` returns false when ANY child is false
- [ ] Unsupported `Or`/`Not` operators throw `NotSupportedException`
- [ ] Registered as `IInboxConditionEvaluator` scoped in DI

## Test Cases

- TC-001: `MimeTypeCondition("image/*")` — true for `image/jpeg`, false for `video/mp4`
- TC-002: `MimeTypeCondition("application/pdf")` — exact match only
- TC-003: `NameGlobCondition("*.jpg")` — true for `photo.jpg`, false for `photo.png`
- TC-004: `NameGlobCondition("report-??-*")` — true for `report-Q1-2026.pdf`
- TC-005: `FileSizeCondition(1024, 5242880)` — true for 2MB, false for 500B, false for 6MB
- TC-006: `UploadDateTimeCondition(DaysOfWeek: [Saturday, Sunday], ...)` — true on Sunday, false on Wednesday
- TC-007: `ConditionGroup(And, [MimeType("image/*"), NameGlob("*.raw")])` — false when only MIME matches
- TC-008: `ConditionGroup(Or, [...])` throws `NotSupportedException` in v0.1

## Implementation Tasks

- [ ] Create `src/Strg.Core/Inbox/` directory
- [ ] Create `InboxEvaluationContext.cs`
- [ ] Create `IInboxConditionEvaluator.cs`
- [ ] Create `src/Strg.Infrastructure/Inbox/InboxConditionEvaluator.cs`
- [ ] Register in DI
- [ ] Write comprehensive unit tests (no EF Core needed — pure logic)

## Definition of Done

- [ ] `dotnet build` passes with zero warnings
- [ ] All TC-001 through TC-008 tests pass
- [ ] No external NuGet packages required (only `System.Text.RegularExpressions` from BCL)
- [ ] `Strg.Core` has zero external NuGet dependencies after this issue
