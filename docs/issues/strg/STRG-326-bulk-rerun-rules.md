---
id: STRG-326
title: Bulk re-run inbox rules with filter
milestone: v0.2
priority: low
status: open
type: implementation
labels: [inbox, graphql, bulk-operations]
depends_on: [STRG-308, STRG-311]
blocks: []
assigned_agent_type: feature-dev
estimated_complexity: medium
---

# STRG-326: Bulk re-run inbox rules with filter

## Summary

Implement the `rerunInboxRules` GraphQL mutation that resets a filtered set of files back to `Pending` status and re-triggers inbox rule evaluation for each. This is useful when a user changes their rules and wants to re-apply them to existing files (e.g., re-sort all images uploaded this month that were previously Skipped).

## Technical Specification

### New domain event (`src/Strg.Core/Events/InboxFileReadyForProcessingEvent.cs`)

```csharp
public record InboxFileReadyForProcessingEvent(
    Guid TenantId,
    Guid FileId,
    Guid DriveId,
    Guid UserId,
    long Size,
    string MimeType
) : IDomainEvent;
```

This is structurally identical to `FileUploadedEvent` but semantically distinct — it triggers re-processing of an already-uploaded file. `InboxProcessingConsumer` subscribes to both events.

### GraphQL mutation

```graphql
mutation {
  rerunInboxRules(filter: InboxRerunFilterInput!): InboxRerunResultPayload!
}

input InboxRerunFilterInput {
  mimeType: String           # wildcard supported, e.g. "image/*"
  nameGlob: String           # e.g. "*.jpg"
  uploadedAfter: DateTime
  uploadedBefore: DateTime
  driveId: ID                # restrict to a specific drive
  currentStatuses: [InboxFileStatus!]  # default: [SKIPPED, FAILED, PARTIAL_FAILURE]
}

type InboxRerunResultPayload {
  affectedCount: Int!
  message: String!
}
```

### Mutation implementation (`src/Strg.GraphQL/Mutations/InboxRuleMutations.cs`)

```csharp
public async Task<InboxRerunResultPayload> RerunInboxRulesAsync(
    InboxRerunFilterInput filter,
    [Service] StrgDbContext db,
    [Service] IBus bus,
    [Service] ICurrentUserContext user,
    CancellationToken ct)
{
    var statuses = filter.CurrentStatuses?.Length > 0
        ? filter.CurrentStatuses
        : new[] { InboxFileStatus.Skipped, InboxFileStatus.Failed, InboxFileStatus.PartialFailure };

    var query = db.Files.Where(f =>
        f.IsInInbox &&
        statuses.Contains(f.InboxStatus!.Value) &&
        f.CreatedBy == user.UserId);

    if (filter.DriveId.HasValue) query = query.Where(f => f.DriveId == filter.DriveId);
    if (!string.IsNullOrEmpty(filter.MimeType)) query = ApplyMimeFilter(query, filter.MimeType);
    if (!string.IsNullOrEmpty(filter.NameGlob)) query = query.Where(f => EF.Functions.Like(f.Name, GlobToSql(filter.NameGlob)));
    if (filter.UploadedAfter.HasValue) query = query.Where(f => f.CreatedAt >= filter.UploadedAfter);
    if (filter.UploadedBefore.HasValue) query = query.Where(f => f.CreatedAt <= filter.UploadedBefore);

    var files = await query.ToListAsync(ct);

    foreach (var file in files)
    {
        file.InboxStatus = InboxFileStatus.Pending;
        file.InboxExitedAt = null;
        file.IsInInbox = true;
    }

    await db.SaveChangesAsync(ct);

    // Publish one re-processing event per file
    foreach (var file in files)
        await bus.Publish(new InboxFileReadyForProcessingEvent(
            file.TenantId, file.Id, file.DriveId, file.CreatedBy, file.Size, file.MimeType), ct);

    return new InboxRerunResultPayload(files.Count,
        $"Re-queued {files.Count} file(s) for inbox processing.");
}
```

### Consumer update (STRG-308)

`InboxProcessingConsumer` also consumes `InboxFileReadyForProcessingEvent`:

```csharp
public sealed class InboxProcessingConsumer :
    IConsumer<FileUploadedEvent>,
    IConsumer<InboxFileReadyForProcessingEvent>
```

The `Consume` method for `InboxFileReadyForProcessingEvent` delegates to the same logic as `FileUploadedEvent`.

### Batch size guard

If the filtered result exceeds 500 files, return a validation error asking the user to narrow the filter. This prevents accidental mass reprocessing.

## Acceptance Criteria

- [ ] `rerunInboxRules` mutation returns count of files re-queued
- [ ] Default filter statuses: `Skipped`, `Failed`, `PartialFailure`
- [ ] Files are reset to `Pending`, `InboxExitedAt = null`, `IsInInbox = true`
- [ ] `InboxFileReadyForProcessingEvent` published for each affected file
- [ ] `InboxProcessingConsumer` consumes the new event type
- [ ] Filter exceeding 500 files returns a validation error
- [ ] Tenant isolation enforced: user can only re-run rules for their own files

## Test Cases

- TC-001: Filter by `status: [SKIPPED]` → only Skipped files are re-queued
- TC-002: Filter by `mimeType: "image/*"` → only images re-queued
- TC-003: 501 matching files → validation error with helpful message
- TC-004: Re-queued file → consumer picks it up and processes it with current rules
- TC-005: Re-run on files from another user's drives → they are not included

## Implementation Tasks

- [ ] Create `InboxFileReadyForProcessingEvent.cs`
- [ ] Add `rerunInboxRules` mutation to `InboxRuleMutations`
- [ ] Add `InboxRerunFilterInput` and `InboxRerunResultPayload` GraphQL types
- [ ] Update `InboxProcessingConsumer` to implement both consumer interfaces
- [ ] Add glob-to-SQL helper (`GlobToSql`) for name filter
- [ ] Register new consumer in MassTransit
- [ ] Write integration tests

## Definition of Done

- [ ] `dotnet build` passes with zero warnings
- [ ] All TC-001 through TC-005 tests pass
- [ ] Batch size guard prevents accidental mass reprocessing
