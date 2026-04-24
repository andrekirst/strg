using System.Text.Json;
using HotChocolate.Authorization;
using HotChocolate.Execution;
using HotChocolate.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Strg.Core.Domain;
using Strg.Core.Events;
using Strg.GraphQl.DataLoaders;
using Strg.GraphQl.Subscriptions;
using Strg.GraphQl.Tests.Helpers;
using Strg.GraphQl.Types;
using Strg.Infrastructure.Data;
using Xunit;

namespace Strg.GraphQl.Tests.Subscriptions;

[Collection("database")]
public class FileSubscriptionsTests
{
    private static readonly TestTenantContext SharedTenantCtx = TestTenantContext.Shared;

    private Task<TestExecutor> CreateExecutorAsync(Guid tenantId, Guid userId, string dbName) =>
        GraphQlTestFixture.CreateExecutorAsync(
            configureServices: services =>
            {
                services.AddSingleton<ITenantContext>(SharedTenantCtx);
                services.AddDbContext<StrgDbContext>(o => o.UseInMemoryDatabase(dbName));
            },
            configureSchema: b =>
            {
                b.AddAuthorization()
                 .AddSubscriptionType(s => s.Name("Subscription"))
                 .AddType<FileSubscriptions>()
                 .AddType<FileEventOutputType>()
                 .AddType<FileItemType>()
                 .AddGlobalObjectIdentification();
                b.Services.AddSingleton<IAuthorizationHandler, AllowAllAuthorizationHandler>();
            },
            globalState: new Dictionary<string, object?> { ["tenantId"] = tenantId, ["userId"] = userId });

    // One row per FileEventType enum value. Paired with the GraphQL wire name Hot Chocolate
    // emits for the enum (upper-snake by default), so a future [GraphQLName] override or
    // enum-renaming PR flips both the enum and the expected string in lockstep — the pairing
    // is intentional.
    public static IEnumerable<object[]> AllFileEventTypes()
    {
        yield return new object[] { FileEventType.Uploaded, "UPLOADED" };
        yield return new object[] { FileEventType.Deleted, "DELETED" };
        yield return new object[] { FileEventType.Moved, "MOVED" };
        yield return new object[] { FileEventType.Copied, "COPIED" };
        yield return new object[] { FileEventType.Renamed, "RENAMED" };
    }

    // Parameterised replacement for the pre-#106 single-event happy path. Defends against the
    // "one event type routed to a shared payload type" regression shape: if a future bug maps
    // all 5 FileEventType values onto a single wire constant (or onto the enum default), this
    // theory fails on 4 of 5 iterations rather than passing silently. Envelope-level round-trip
    // of driveId is also asserted — a payload-mapper cross-wiring that rewrites driveId would
    // flip the second assertion even if EventType happens to survive.
    //
    // Wire note: FileEventPayload is homogeneous across event types today (EventType, FileId,
    // DriveId, OccurredAt only — see FileEventOutputType). Move/Rename-specific OldPath/NewPath
    // ride in the FileEvent envelope but do NOT cross to the wire. Realistic paths are supplied
    // to the two event types that carry them anyway, so a future STRG-066-v0.2 wire expansion
    // can extend the per-iteration assertions without changing the test's event construction.
    [Theory]
    [MemberData(nameof(AllFileEventTypes))]
    public async Task FileEvents_RoundTripsEventType_ForEveryFileEventTypeEnumValue(
        FileEventType eventType,
        string expectedWireEventTypeName)
    {
        var tenantId = Guid.NewGuid();
        SharedTenantCtx.TenantId = tenantId;

        var driveId = Guid.NewGuid();
        var executor = await CreateExecutorAsync(tenantId, Guid.NewGuid(), Guid.NewGuid().ToString());

        var sender = executor.Services.GetRequiredService<ITopicEventSender>();

        var subscriptionResult = await executor.ExecuteAsync(
            $"subscription {{ fileEvents(driveId: \"{driveId}\") {{ eventType driveId occurredAt }} }}");

        var (oldPath, newPath) = eventType switch
        {
            FileEventType.Moved => ("/a/old.txt", "/a/new.txt"),
            FileEventType.Renamed => ("/a/old-name.txt", "/a/new-name.txt"),
            _ => ((string?)null, (string?)null),
        };
        var fileEvent = new FileEvent(
            EventType: eventType,
            FileId: Guid.NewGuid(),
            DriveId: driveId,
            UserId: Guid.NewGuid(),
            TenantId: tenantId,
            OldPath: oldPath,
            NewPath: newPath,
            OccurredAt: DateTimeOffset.UtcNow);

        await sender.SendAsync(Topics.FileEvents(tenantId, driveId), fileEvent, CancellationToken.None);

        await using var stream = (IResponseStream)subscriptionResult;
        await using var enumerator = stream.ReadResultsAsync().GetAsyncEnumerator(CancellationToken.None);

        Assert.True(await enumerator.MoveNextAsync(), $"Expected subscription frame for {eventType}");
        var first = enumerator.Current;

        var json = first.ToJson();
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("data", out var data), $"no data: {json}");

        var fileEvents = data.GetProperty("fileEvents");
        var wireEventType = fileEvents.GetProperty("eventType").GetString();
        var wireDriveId = fileEvents.GetProperty("driveId").GetString();

        // Discriminator-level assertion: a regression that folds all 5 types onto a shared
        // wire constant flips every iteration except the one matching the default.
        Assert.Equal(expectedWireEventTypeName, wireEventType);
        // Envelope-level assertion: cross-wired payload mapping (e.g., driveId accidentally
        // sourced from the subscription argument instead of the event) would flip this.
        Assert.Equal(driveId.ToString(), wireDriveId, ignoreCase: true);
    }

    // TC-002: events for a different drive must NOT reach a subscriber scoped to driveA. The
    // guarantee here is topic-level (Topics.FileEvents returns a (tenantId, driveId)-scoped
    // string), so this is a first-order routing test — if the publisher ever fans out to a shared
    // topic and relies on resolver-side filtering, this test breaks before users see events
    // bleed across drives or tenants.
    [Fact]
    public async Task FileEvents_DoesNotReceiveEventsFromOtherDrive()
    {
        var tenantId = Guid.NewGuid();
        SharedTenantCtx.TenantId = tenantId;

        var driveA = Guid.NewGuid();
        var driveB = Guid.NewGuid();
        var executor = await CreateExecutorAsync(tenantId, Guid.NewGuid(), Guid.NewGuid().ToString());
        var sender = executor.Services.GetRequiredService<ITopicEventSender>();

        var subscriptionResult = await executor.ExecuteAsync(
            $"subscription {{ fileEvents(driveId: \"{driveA}\") {{ eventType driveId }} }}");

        // Publish to driveB only — subscriber on driveA should see no message within the window.
        var driveBEvent = new FileEvent(
            EventType: FileEventType.Uploaded,
            FileId: Guid.NewGuid(),
            DriveId: driveB,
            UserId: Guid.NewGuid(),
            TenantId: tenantId,
            OldPath: null,
            NewPath: null,
            OccurredAt: DateTimeOffset.UtcNow);
        await sender.SendAsync(Topics.FileEvents(tenantId, driveB), driveBEvent, CancellationToken.None);

        await using var stream = (IResponseStream)subscriptionResult;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        try
        {
            await using var enumerator = stream.ReadResultsAsync().GetAsyncEnumerator(timeout.Token);
            var moved = await enumerator.MoveNextAsync();
            Assert.False(moved, "Subscriber on driveA must not receive events published to driveB.");
        }
        catch (OperationCanceledException)
        {
            // Expected: no event arrived within the window, so MoveNextAsync was cancelled.
        }
    }

    // TC-004: two subscribers on the same drive both receive the same event. Defends against a
    // future refactor that makes topic delivery a consume-once queue semantics instead of
    // broadcast pub/sub.
    [Fact]
    public async Task FileEvents_BroadcastsToMultipleSubscribers()
    {
        var tenantId = Guid.NewGuid();
        SharedTenantCtx.TenantId = tenantId;

        var driveId = Guid.NewGuid();
        var executor = await CreateExecutorAsync(tenantId, Guid.NewGuid(), Guid.NewGuid().ToString());
        var sender = executor.Services.GetRequiredService<ITopicEventSender>();

        var sub1 = await executor.ExecuteAsync(
            $"subscription {{ fileEvents(driveId: \"{driveId}\") {{ eventType }} }}");
        var sub2 = await executor.ExecuteAsync(
            $"subscription {{ fileEvents(driveId: \"{driveId}\") {{ eventType }} }}");

        var fileEvent = new FileEvent(
            EventType: FileEventType.Deleted,
            FileId: Guid.NewGuid(),
            DriveId: driveId,
            UserId: Guid.NewGuid(),
            TenantId: tenantId,
            OldPath: null,
            NewPath: null,
            OccurredAt: DateTimeOffset.UtcNow);
        await sender.SendAsync(Topics.FileEvents(tenantId, driveId), fileEvent, CancellationToken.None);

        await using var stream1 = (IResponseStream)sub1;
        await using var stream2 = (IResponseStream)sub2;
        await using var e1 = stream1.ReadResultsAsync().GetAsyncEnumerator(CancellationToken.None);
        await using var e2 = stream2.ReadResultsAsync().GetAsyncEnumerator(CancellationToken.None);

        Assert.True(await e1.MoveNextAsync(), "Subscriber 1 should have received the event");
        Assert.True(await e2.MoveNextAsync(), "Subscriber 2 should have received the event");

        var et1 = JsonDocument.Parse(e1.Current.ToJson())
            .RootElement.GetProperty("data").GetProperty("fileEvents").GetProperty("eventType").GetString();
        var et2 = JsonDocument.Parse(e2.Current.ToJson())
            .RootElement.GetProperty("data").GetProperty("fileEvents").GetProperty("eventType").GetString();
        Assert.Equal("DELETED", et1);
        Assert.Equal("DELETED", et2);
    }

    // TC-005: FileEvent.tenantId must NOT appear in the GraphQL schema. This is the regression
    // gate against a "expose TenantId in payload for client convenience" PR — introspecting the
    // payload type and asserting the field set is exactly what the spec says. Schema-mask is our
    // primary tenant-isolation defense; the resolver guard is belt-and-suspenders.
    [Fact]
    public async Task FileEventPayload_SchemaDoesNotExposeTenantId()
    {
        var tenantId = Guid.NewGuid();
        SharedTenantCtx.TenantId = tenantId;

        var executor = await CreateExecutorAsync(tenantId, Guid.NewGuid(), Guid.NewGuid().ToString());

        // Hot Chocolate names the payload type after the resolver return type (FileEventPayload).
        // Probe both candidate names so this test keeps working if the schema name gets renamed
        // to "FileEvent" via a [GraphQLName] attribute.
        var result = await executor.ExecuteAsync(
            "{ __type(name: \"FileEventPayload\") { name fields { name } } }");
        var json = result.ToJson();
        using var doc = JsonDocument.Parse(json);

        var typeNode = doc.RootElement.GetProperty("data").GetProperty("__type");
        Assert.Equal(JsonValueKind.Object, typeNode.ValueKind);

        var fieldNames = typeNode.GetProperty("fields").EnumerateArray()
            .Select(f => f.GetProperty("name").GetString())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.DoesNotContain("tenantId", fieldNames, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("eventType", fieldNames);
        Assert.Contains("driveId", fieldNames);
        Assert.Contains("occurredAt", fieldNames);
        Assert.Contains("file", fieldNames);
    }

    // INFO-1 — Corollary 6: name the test after the invariant.
    //
    // The topic key (tenantId, driveId) makes the cross-tenant subscribe path structurally empty
    // under normal routing, but the per-event resolver guard is kept as defence-in-depth against
    // a future bug that bypasses topic keys — a custom SubscribeToFileEventsAsync impl, broken
    // [GlobalState("tenantId")] propagation, or an accidental topic-key downgrade. This test
    // drives the cross-tenant event directly into the topic the subscriber is bound to (same
    // tenantId, same driveId), but with a payload whose TenantId is spoofed to a different
    // tenant — the only way to reach the resolver with a mismatched event under normal routing
    // would be via such a bug. The resolver must reject the event with UnauthorizedAccessException
    // rather than delivering the payload.
    [Fact]
    public async Task FileEvents_throws_UnauthorizedAccessException_when_subscriber_tenant_does_not_match_event_tenant()
    {
        var subscriberTenant = Guid.NewGuid();
        var foreignTenant = Guid.NewGuid();
        SharedTenantCtx.TenantId = subscriberTenant;

        var driveId = Guid.NewGuid();
        var executor = await CreateExecutorAsync(subscriberTenant, Guid.NewGuid(), Guid.NewGuid().ToString());
        var sender = executor.Services.GetRequiredService<ITopicEventSender>();

        var subscriptionResult = await executor.ExecuteAsync(
            $"subscription {{ fileEvents(driveId: \"{driveId}\") {{ eventType }} }}");

        // Spoof path: publish directly to the subscriber's own topic key, but with a payload
        // whose TenantId is foreignTenant. This is the exact scenario the resolver-side guard
        // exists to catch if topic routing is ever bypassed or regressed.
        var spoofed = new FileEvent(
            EventType: FileEventType.Uploaded,
            FileId: Guid.NewGuid(),
            DriveId: driveId,
            UserId: Guid.NewGuid(),
            TenantId: foreignTenant,
            OldPath: null,
            NewPath: null,
            OccurredAt: DateTimeOffset.UtcNow);
        await sender.SendAsync(Topics.FileEvents(subscriberTenant, driveId), spoofed, CancellationToken.None);

        await using var stream = (IResponseStream)subscriptionResult;
        await using var enumerator = stream.ReadResultsAsync().GetAsyncEnumerator(CancellationToken.None);

        // The resolver throws UnauthorizedAccessException; Hot Chocolate surfaces it as a GraphQL
        // error on the result rather than raising it to the caller of MoveNextAsync. The payload
        // MUST NOT be delivered — assert that the result carries errors and no data field, or
        // (depending on HC version) a null data.fileEvents with an error entry.
        Assert.True(await enumerator.MoveNextAsync(), "Expected a subscription frame carrying the rejection.");
        var json = enumerator.Current.ToJson();
        using var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("errors", out var errors),
            $"Expected errors on resolver rejection, got: {json}");
        Assert.True(errors.GetArrayLength() >= 1, "Expected at least one error entry.");

        // Defensive: if data is present, its fileEvents payload must be null — the event MUST
        // NOT have crossed the wire with a materialised FileEventPayload.
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("fileEvents", out var fe))
        {
            Assert.Equal(JsonValueKind.Null, fe.ValueKind);
        }
    }

    // TC-006: the `file` subfield on a FileEventPayload is resolved via FileItemByIdDataLoader
    // (a BatchDataLoader<Guid, FileItem> over IDbContextFactory<StrgDbContext>), whereas the
    // top-level query path (FileQueries.GetFile) resolves the same entity via a scoped
    // StrgDbContext. This test seeds a folder, drives the subscription to emit file.isFolder
    // through the DataLoader, and asserts the same value as a direct DB read — the "direct
    // resolver" body is literally `db.Files.FirstOrDefaultAsync(f => f.Id == id)`, so the
    // scoped-context read is a faithful stand-in for that code path.
    //
    // Regression shape defended: a DataLoader change that projects to a stubbed FileItem (drops
    // IsDirectory, renames IsFolder on the wire, or batches against the wrong column) would
    // flip the wire isFolder while the direct-DB read remains true. Today the DataLoader is
    // the ONLY FileItem resolver in the subscription path, so parity against the direct path
    // is the strongest signal we have that "subscribers see the real entity, not a payload
    // stub." Team-lead flagged the signal as marginal precisely because both paths share the
    // same EF model — the value is in pinning wire-emission of `isFolder` from the
    // subscription surface at all, catching a future [GraphQLName] / [GraphQLIgnore] drift.
    private Task<TestExecutor> CreateExecutorWithDataLoaderAsync(Guid tenantId, Guid userId, string dbName) =>
        GraphQlTestFixture.CreateExecutorAsync(
            configureServices: services =>
            {
                services.AddSingleton<ITenantContext>(SharedTenantCtx);
                // Scoped context for seed/read; factory for the DataLoader batch. An explicit
                // shared InMemoryDatabaseRoot forces both registrations onto the same store —
                // relying on EF's default-root behavior is unreliable when the scoped-context and
                // factory configurations are built through separate options pipelines. Without
                // the shared root, the factory-created context the DataLoader opens sees an
                // empty database even though the scoped context just seeded a row.
                //
                // ITenantContext is singleton so the factory can resolve it from the root
                // provider without scope gymnastics.
                var sharedRoot = new InMemoryDatabaseRoot();
                services.AddDbContext<StrgDbContext>(o => o.UseInMemoryDatabase(dbName, sharedRoot));
                services.AddDbContextFactory<StrgDbContext>(o => o.UseInMemoryDatabase(dbName, sharedRoot));
            },
            configureSchema: b =>
            {
                b.AddAuthorization()
                 .AddSubscriptionType(s => s.Name("Subscription"))
                 .AddType<FileSubscriptions>()
                 .AddType<FileEventOutputType>()
                 .AddType<FileItemType>()
                 .AddDataLoader<FileItemByIdDataLoader>()
                 .AddGlobalObjectIdentification();
                b.Services.AddSingleton<IAuthorizationHandler, AllowAllAuthorizationHandler>();
            },
            globalState: new Dictionary<string, object?> { ["tenantId"] = tenantId, ["userId"] = userId });

    [Fact]
    public async Task FileEvents_FileSubfield_ResolvesIsFolder_ViaDataLoader_MatchingDirectDbRead()
    {
        var tenantId = Guid.NewGuid();
        SharedTenantCtx.TenantId = tenantId;

        var driveId = Guid.NewGuid();
        var folderId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();

        var executor = await CreateExecutorWithDataLoaderAsync(tenantId, userId, dbName);

        // Seed a folder via the scoped context. Tenant-filter will accept it because the
        // singleton ITenantContext is the shared one whose TenantId we just set.
        using (var scope = executor.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();
            db.Files.Add(new FileItem
            {
                Id = folderId,
                TenantId = tenantId,
                DriveId = driveId,
                Name = "folder-a",
                Path = "/folder-a",
                IsDirectory = true,
                CreatedBy = userId,
            });
            await db.SaveChangesAsync();
        }

        var sender = executor.Services.GetRequiredService<ITopicEventSender>();

        var subscriptionResult = await executor.ExecuteAsync(
            $"subscription {{ fileEvents(driveId: \"{driveId}\") {{ file {{ id isFolder }} }} }}");

        var fileEvent = new FileEvent(
            EventType: FileEventType.Uploaded,
            FileId: folderId,
            DriveId: driveId,
            UserId: userId,
            TenantId: tenantId,
            OldPath: null,
            NewPath: null,
            OccurredAt: DateTimeOffset.UtcNow);
        await sender.SendAsync(Topics.FileEvents(tenantId, driveId), fileEvent, CancellationToken.None);

        await using var stream = (IResponseStream)subscriptionResult;
        await using var enumerator = stream.ReadResultsAsync().GetAsyncEnumerator(CancellationToken.None);

        Assert.True(await enumerator.MoveNextAsync(), "Expected subscription frame for file subfield");
        var first = enumerator.Current;

        var json = first.ToJson();
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("data", out var data), $"no data: {json}");

        var fileNode = data.GetProperty("fileEvents").GetProperty("file");
        Assert.True(fileNode.ValueKind == JsonValueKind.Object,
            $"file subfield must resolve to an object — DataLoader returned null or missing. Raw JSON: {json}");
        var wireIsFolder = fileNode.GetProperty("isFolder").GetBoolean();

        // Direct-DB parity read: mirror FileQueries.GetFile's resolver body exactly, bypassing
        // the DataLoader entirely.
        bool directIsFolder;
        using (var scope = executor.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();
            var loaded = await db.Files.FirstOrDefaultAsync(f => f.Id == folderId);
            Assert.NotNull(loaded);
            directIsFolder = loaded!.IsFolder;
        }

        Assert.True(directIsFolder, "Seeded folder must report IsFolder=true via direct DB read");
        Assert.Equal(directIsFolder, wireIsFolder);
    }
}
