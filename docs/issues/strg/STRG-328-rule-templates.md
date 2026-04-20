---
id: STRG-328
title: System-provided rule templates
milestone: v0.2
priority: low
status: open
type: implementation
labels: [inbox, graphql, templates]
depends_on: [STRG-302, STRG-310]
blocks: []
assigned_agent_type: feature-dev
estimated_complexity: small
---

# STRG-328: System-provided rule templates

## Summary

Add a library of system-provided inbox rule templates bundled as JSON files in `Strg.Infrastructure`. Templates describe common sorting patterns (sort images by month, sort videos, sort documents, etc.). Users can browse them via GraphQL and create a new rule pre-filled from a template with one mutation. Templates are defined in code as embedded resources — no DB table or migration required.

## Technical Specification

### Template definition model (`src/Strg.Infrastructure/Inbox/Templates/RuleTemplateDefinition.cs`)

```csharp
public sealed record RuleTemplateDefinition(
    string Id,
    string Name,
    string Description,
    string Category,      // e.g. "Sorting", "Archiving", "Media"
    string ConditionsJson,
    string ActionsJson
);
```

### Template files location

```
src/Strg.Infrastructure/Inbox/Templates/
  sort-images-by-month.json
  sort-videos.json
  sort-documents.json
  sort-audio.json
  archive-old-files.json
  large-files-folder.json
```

Each is marked `<EmbeddedResource>` in `Strg.Infrastructure.csproj`.

### Example template file (`sort-images-by-month.json`)

```json
{
  "id": "sort-images-by-month",
  "name": "Sort images by month",
  "description": "Automatically move image files to a /photos/{year}/{month}/ folder based on upload date.",
  "category": "Sorting",
  "conditionsJson": "{\"$type\":\"group\",\"operator\":\"And\",\"children\":[{\"$type\":\"mimeType\",\"mimeType\":\"image/*\"}]}",
  "actionsJson": "[{\"$type\":\"move\",\"targetPath\":\"/photos/{year}/{month}\",\"conflictResolution\":\"AutoRename\",\"autoCreateFolders\":true}]"
}
```

### Template loader (`src/Strg.Infrastructure/Inbox/Templates/RuleTemplateLoader.cs`)

```csharp
public static class RuleTemplateLoader
{
    private static readonly Lazy<IReadOnlyList<RuleTemplateDefinition>> _templates =
        new(() => LoadAll());

    public static IReadOnlyList<RuleTemplateDefinition> All => _templates.Value;

    private static IReadOnlyList<RuleTemplateDefinition> LoadAll()
    {
        var assembly = typeof(RuleTemplateLoader).Assembly;
        var prefix = "Strg.Infrastructure.Inbox.Templates.";

        return assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix) && n.EndsWith(".json"))
            .Select(n =>
            {
                using var stream = assembly.GetManifestResourceStream(n)!;
                using var reader = new StreamReader(stream);
                return JsonSerializer.Deserialize<RuleTemplateDefinition>(reader.ReadToEnd())!;
            })
            .ToList();
    }
}
```

### GraphQL queries + mutations

```graphql
type Query {
  inboxRuleTemplates: [InboxRuleTemplate!]!
  inboxRuleTemplate(id: String!): InboxRuleTemplate
}

type InboxRuleTemplate {
  id: String!
  name: String!
  description: String!
  category: String!
  # Preview of what the rule would look like (not yet saved)
  conditions: ConditionGroup!
  actions: [InboxAction!]!
}

type Mutation {
  createInboxRuleFromTemplate(input: CreateFromTemplateInput!): CreateInboxRulePayload!
}

input CreateFromTemplateInput {
  templateId: String!
  scope: RuleScope!
  driveId: ID          # required if scope = DRIVE
  name: String         # optional override for rule name; defaults to template name
  isEnabled: Boolean = true
  priority: Int = 0
}
```

### `createInboxRuleFromTemplate` implementation

```csharp
public async Task<CreateInboxRulePayload> CreateInboxRuleFromTemplateAsync(
    CreateFromTemplateInput input,
    [Service] StrgDbContext db,
    [Service] ICurrentUserContext user,
    CancellationToken ct)
{
    var template = RuleTemplateLoader.All.FirstOrDefault(t => t.Id == input.TemplateId)
        ?? throw new NotFoundException("RuleTemplate", input.TemplateId);

    var rule = new InboxRule
    {
        TenantId = user.TenantId,
        UserId = input.Scope == RuleScope.User ? user.UserId : null,
        DriveId = input.Scope == RuleScope.Drive ? Guid.Parse(input.DriveId!) : null,
        Name = input.Name ?? template.Name,
        Description = template.Description,
        Priority = input.Priority,
        IsEnabled = input.IsEnabled,
        ConditionsJson = template.ConditionsJson,
        ActionsJson = template.ActionsJson
    };

    db.InboxRules.Add(rule);
    await db.SaveChangesAsync(ct);
    return new CreateInboxRulePayload(rule);
}
```

### Built-in templates

| ID | Name | Condition | Action |
|---|---|---|---|
| `sort-images-by-month` | Sort images by month | MIME = `image/*` | Move to `/photos/{year}/{month}` |
| `sort-videos` | Sort videos | MIME = `video/*` | Move to `/videos/{year}` |
| `sort-documents` | Sort documents | MIME = `application/pdf` OR `text/*` | Move to `/documents` |
| `sort-audio` | Sort audio files | MIME = `audio/*` | Move to `/music` |
| `archive-old-files` | Archive older files | UploadDateTime before 30 days ago | Move to `/archive/{year}` |
| `large-files-folder` | Move large files | FileSize ≥ 100 MB | Move to `/large-files` |

## Acceptance Criteria

- [ ] Template JSON files exist as embedded resources in `Strg.Infrastructure`
- [ ] `RuleTemplateLoader.All` returns all 6 built-in templates without DB queries
- [ ] `inboxRuleTemplates` GraphQL query returns all templates without authentication
- [ ] `createInboxRuleFromTemplate` creates a rule pre-filled from the template
- [ ] Template `conditionsJson` and `actionsJson` are valid for the `InboxRule` domain model
- [ ] New templates can be added by dropping a `.json` file (no code changes needed)

## Test Cases

- TC-001: `inboxRuleTemplates` returns 6 templates with correct names and categories
- TC-002: `createInboxRuleFromTemplate(templateId: "sort-images-by-month", scope: USER)` → rule created with image MIME condition
- TC-003: Custom `name` in `CreateFromTemplateInput` overrides template name
- TC-004: Template JSON round-trips correctly through `InboxRule.ParseConditions()` and `ParseActions()`
- TC-005: Unknown `templateId` → `NotFoundException`

## Implementation Tasks

- [ ] Create `src/Strg.Infrastructure/Inbox/Templates/` directory
- [ ] Create 6 template JSON files
- [ ] Mark them as `<EmbeddedResource>` in `.csproj`
- [ ] Create `RuleTemplateDefinition.cs` record
- [ ] Create `RuleTemplateLoader.cs` static loader
- [ ] Add `inboxRuleTemplates` and `inboxRuleTemplate` queries to GraphQL
- [ ] Add `createInboxRuleFromTemplate` mutation
- [ ] Write unit tests for template loading and mutation

## Definition of Done

- [ ] `dotnet build` passes with zero warnings
- [ ] All TC-001 through TC-005 tests pass
- [ ] All 6 template JSON files parse correctly in unit tests
- [ ] No DB migration required
