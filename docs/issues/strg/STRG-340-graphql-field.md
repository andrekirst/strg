---
id: STRG-340
title: GraphQL FileItem.thumbnail field + Thumbnail type + DataLoader + thumbnailReady subscription
milestone: v0.2
priority: high
status: open
type: feature
labels: [thumbnails, phase-15, graphql, dataloader, subscriptions]
depends_on: [STRG-339, STRG-341]
blocks: [STRG-342, STRG-343]
assigned_agent_type: feature-dev:code-architect
estimated_complexity: medium
---

# STRG-340: GraphQL FileItem.thumbnail field + Thumbnail type + DataLoader + thumbnailReady subscription

## Summary

Expose thumbnails through Hot Chocolate GraphQL: a `Thumbnail` object type, a `thumbnail(variant: ThumbnailVariant!)` field on `FileItem` (DataLoader-batched), and a live `thumbnailReady(fileId: ID!)` subscription bridged from `ThumbnailReadyEvent` (STRG-341).

## Background / Context

Grid-view UIs query many `FileItem.thumbnail` fields in a single GraphQL request — DataLoader batching is non-negotiable to avoid N+1 reads against `ThumbnailEntries`. The `url` field returns the REST endpoint (STRG-339) so we have a single source of truth for serving bytes; GraphQL is pure metadata.

The subscription lets a single-page app refresh thumbnails as they finish generating without polling.

## Technical Specification

### `Thumbnail` type — `src/Strg.GraphQl/Types/ThumbnailType.cs`

```csharp
public sealed record Thumbnail(
    string Url,
    int? Width,
    int? Height,
    long? SizeBytes,
    ThumbnailStatusGraphQl Status,
    string? Format,
    string? ErrorReason);

public sealed class ThumbnailType : ObjectType<Thumbnail>
{
    protected override void Configure(IObjectTypeDescriptor<Thumbnail> descriptor)
    {
        descriptor.Field(t => t.Url).Type<NonNullType<StringType>>();
        descriptor.Field(t => t.Status).Type<NonNullType<EnumType<ThumbnailStatusGraphQl>>>();
        descriptor.Field(t => t.ErrorReason)
                  .Description("Human-readable reason when status is FAILED or UNSUPPORTED. Null otherwise.");
    }
}

public enum ThumbnailStatusGraphQl { Pending, Ready, Failed, Unsupported }

// Maps domain → GraphQL enum (the names happen to match; explicit conversion centralizes future drift).
public static class ThumbnailStatusMap
{
    public static ThumbnailStatusGraphQl FromDomain(ThumbnailStatus s) => (ThumbnailStatusGraphQl)s;
}
```

`Url` is always populated — points to the REST endpoint. The client follows it; GraphQL doesn't stream bytes.

### `ThumbnailVariant` enum — `src/Strg.GraphQl/Types/ThumbnailVariantType.cs`

```csharp
public enum ThumbnailVariantGraphQl { Thumb, Small, Medium }
```

(Mirrors `ThumbnailVariants` strings; Hot Chocolate auto-derives the enum schema.)

### `FileItem.thumbnail` field — `src/Strg.GraphQl/Types/FileItemType.cs`

Add to the existing `FileItemType` configuration:

```csharp
descriptor
    .Field("thumbnail")
    .Argument("variant", a => a.Type<NonNullType<EnumType<ThumbnailVariantGraphQl>>>())
    .Type<ThumbnailType>()
    .Resolve(async ctx =>
    {
        var fileItem = ctx.Parent<FileItem>();
        var variant = ctx.ArgumentValue<ThumbnailVariantGraphQl>("variant");
        var loader = ctx.DataLoader<ThumbnailDataLoader>();
        return await loader.LoadAsync(
            new ThumbnailKey(fileItem.Id, variant.ToVariantString()),
            ctx.RequestAborted);
    });
```

### `ThumbnailDataLoader` — `src/Strg.GraphQl/DataLoaders/ThumbnailDataLoader.cs`

```csharp
public sealed record ThumbnailKey(Guid FileId, string Variant);

public sealed class ThumbnailDataLoader(
    IBatchScheduler scheduler,
    IDbContextFactory<StrgDbContext> dbFactory,
    DataLoaderOptions options,
    LinkGenerator linkGenerator)
    : BatchDataLoader<ThumbnailKey, Thumbnail>(scheduler, options)
{
    protected override async Task<IReadOnlyDictionary<ThumbnailKey, Thumbnail>> LoadBatchAsync(
        IReadOnlyList<ThumbnailKey> keys, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        // Group by variant for predicate efficiency, then collect.
        var fileIds = keys.Select(k => k.FileId).Distinct().ToList();
        var variants = keys.Select(k => k.Variant).Distinct().ToList();

        // Latest version per file → corresponding ThumbnailEntry per (variant, format=webp).
        var rows = await (from t in db.ThumbnailEntries
                          join v in db.FileVersions on t.FileVersionId equals v.Id
                          where fileIds.Contains(v.FileId)
                                && variants.Contains(t.Variant)
                                && t.Format == "webp"
                          select new { v.FileId, t })
                         .ToListAsync(cancellationToken);

        // For each (FileId, Variant), pick the row tied to the LATEST version.
        // (Cleanup ensures stale-version rows are gone, but defence in depth.)
        return rows
            .GroupBy(r => new ThumbnailKey(r.FileId, r.t.Variant))
            .ToDictionary(
                g => g.Key,
                g => MapToGraphQl(g.OrderByDescending(r => r.t.GeneratedAt ?? DateTimeOffset.MinValue).First().t));
    }

    private Thumbnail MapToGraphQl(ThumbnailEntry t) => new(
        Url: BuildRestUrl(t.FileVersionId, t.Variant),
        Width: t.Status == ThumbnailStatus.Ready ? t.Width : null,
        Height: t.Status == ThumbnailStatus.Ready ? t.Height : null,
        SizeBytes: t.Status == ThumbnailStatus.Ready ? t.SizeBytes : null,
        Status: (ThumbnailStatusGraphQl)t.Status,
        Format: t.Status == ThumbnailStatus.Ready ? t.Format : null,
        ErrorReason: t.ErrorReason);
}
```

The `BuildRestUrl` helper uses `LinkGenerator.GetPathByName("GetFileThumbnail", new { fileId = ..., variant = ... })` to avoid hard-coding paths.

### Subscription — `src/Strg.GraphQl/Subscriptions/ThumbnailSubscriptions.cs`

```csharp
[ExtendObjectType("Subscription")]
public sealed class ThumbnailSubscriptions
{
    [Subscribe(With = nameof(SubscribeToThumbnailReady))]
    public Thumbnail ThumbnailReady(
        Guid fileId,
        [EventMessage] ThumbnailReadyEvent evt,
        [Service] LinkGenerator linkGenerator) =>
        new Thumbnail(
            Url: BuildRestUrl(linkGenerator, evt.FileId, evt.Variant),
            Width: evt.Width, Height: evt.Height,
            SizeBytes: null,                        // not in event; client can re-fetch via DataLoader
            Status: ThumbnailStatusGraphQl.Ready,
            Format: evt.Format,
            ErrorReason: null);

    public ValueTask<ISourceStream<ThumbnailReadyEvent>> SubscribeToThumbnailReady(
        Guid fileId,
        [Service] ITopicEventReceiver receiver,
        CancellationToken cancellationToken) =>
        receiver.SubscribeAsync<ThumbnailReadyEvent>(
            $"thumbnail-ready:{fileId}", cancellationToken);
}
```

The publisher (`GraphQlSubscriptionPublisher`, STRG-341) sends to topic `thumbnail-ready:{fileId}` so the subscription scopes per-file.

### Schema example

```graphql
type FileItem {
  id: ID!
  name: String!
  thumbnail(variant: ThumbnailVariantGraphQl!): Thumbnail
  # ... other fields
}

type Thumbnail {
  url: String!
  width: Int
  height: Int
  sizeBytes: BigInt
  status: ThumbnailStatusGraphQl!
  format: String
  errorReason: String
}

enum ThumbnailStatusGraphQl { PENDING, READY, FAILED, UNSUPPORTED }
enum ThumbnailVariantGraphQl { THUMB, SMALL, MEDIUM }

type Subscription {
  thumbnailReady(fileId: ID!): Thumbnail!
}
```

## Acceptance Criteria

- [ ] `Thumbnail` GraphQL type exposes `url`, `width`, `height`, `sizeBytes`, `status`, `format`, `errorReason`.
- [ ] `FileItem.thumbnail(variant: ThumbnailVariantGraphQl!)` field is registered.
- [ ] `ThumbnailDataLoader` batches lookups within a single GraphQL request (no N+1 SQL on the thumbnails table).
- [ ] `url` is built via `LinkGenerator.GetPathByName("GetFileThumbnail", ...)` — no hard-coded path.
- [ ] `thumbnailReady(fileId: ID!)` subscription is registered and fires on `ThumbnailReadyEvent` (STRG-341 wires the publisher).
- [ ] Status enum maps the four domain states.
- [ ] `errorReason` is null for `Ready`; populated for `Failed` and `Unsupported`.

## Test Cases

- **TC-001**: GraphQL query `{ files { id thumbnail(variant: SMALL) { url status width height } } }` for 50 files runs ONE batched SQL query against `ThumbnailEntries` (verified via EF logging or test diagnostics).
- **TC-002**: Field returns `status: PENDING` immediately after upload, `status: READY` after the consumer fires.
- **TC-003**: Subscription `subscription { thumbnailReady(fileId: $id) { url status } }` receives a payload after `ThumbnailReadyEvent` fires.
- **TC-004**: `thumbnail(variant: MEDIUM)` for an encrypted-drive file returns `status: UNSUPPORTED, errorReason: "encrypted-drive-not-yet-supported"`.
- **TC-005**: `url` is the REST endpoint path including the correct variant query.

## Implementation Tasks

- [ ] Add `Thumbnail`, `ThumbnailType`, `ThumbnailStatusGraphQl`, `ThumbnailVariantGraphQl`.
- [ ] Add `ThumbnailDataLoader` (batch).
- [ ] Extend `FileItemType` with the `thumbnail` field.
- [ ] Add `ThumbnailSubscriptions` extending `Subscription` type.
- [ ] Register all in the existing GraphQL configuration.
- [ ] Tests under `tests/Strg.GraphQl.Tests/Thumbnails/` (DataLoader test) + `tests/Strg.Integration.Tests/Thumbnails/ThumbnailGraphQlTests.cs` (end-to-end + subscription).

## Security Review Checklist

- [ ] DataLoader respects tenant filter (inherits from `StrgDbContext` which has the global filter).
- [ ] `url` is server-built — never reflected from user input. Variant comes from the GraphQL enum (already validated).
- [ ] `errorReason` exposed in GraphQL is bounded (max 256 chars per the entity column).
- [ ] Subscription topic is per-file — subscribing to `fileId=X` only delivers events for that file.
- [ ] Subscription does NOT bypass auth — `RequireAuthorization` on the subscription endpoint.
- [ ] No `entry.StorageKey` is exposed in any GraphQL payload.

## Code Review Checklist

- [ ] DataLoader extends `BatchDataLoader<,>` (Hot Chocolate v15+).
- [ ] DataLoader uses `IDbContextFactory<>` (DataLoaders may outlive a request scope).
- [ ] No `Task.Wait`, no `.Result`.
- [ ] `ITopicEventReceiver` from `HotChocolate.Subscriptions` (matches existing pattern in `GraphQlSubscriptionPublisher`).

## Definition of Done

- [ ] All acceptance criteria green.
- [ ] Tests pass.
- [ ] Banana Cake Pop / Nitro can introspect the new schema.
- [ ] DataLoader batching verified by EF Core query logging in TC-001.
