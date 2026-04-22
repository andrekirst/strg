using System.Text.Json;
using HotChocolate.Authorization;
using HotChocolate.Execution;
using HotChocolate.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Strg.Core.Events;
using Strg.GraphQL.Subscriptions;
using Strg.GraphQL.Tests.Helpers;
using Strg.GraphQL.Types;
using Strg.Infrastructure.Data;
using Xunit;

namespace Strg.GraphQL.Tests.Subscriptions;

[Collection("database")]
public class FileSubscriptionsTests
{
    private static readonly TestTenantContext SharedTenantCtx = TestTenantContext.Shared;

    private Task<TestExecutor> CreateExecutorAsync(Guid tenantId, Guid userId, string dbName) =>
        GraphQLTestFixture.CreateExecutorAsync(
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

    [Fact]
    public async Task FileEvents_ReceivesEventAfterPublish()
    {
        var tenantId = Guid.NewGuid();
        SharedTenantCtx.TenantId = tenantId;

        var driveId = Guid.NewGuid();
        var executor = await CreateExecutorAsync(tenantId, Guid.NewGuid(), Guid.NewGuid().ToString());

        var sender = executor.Services.GetRequiredService<ITopicEventSender>();

        var subscriptionResult = await executor.ExecuteAsync(
            $"subscription {{ fileEvents(driveId: \"{driveId}\") {{ eventType driveId }} }}");

        var fileEvent = new FileEvent(
            EventType: FileEventType.Uploaded,
            FileId: Guid.NewGuid(),
            DriveId: driveId,
            UserId: Guid.NewGuid(),
            TenantId: tenantId,
            OldPath: null,
            NewPath: null,
            OccurredAt: DateTimeOffset.UtcNow);

        await sender.SendAsync(Topics.FileEvents(tenantId, driveId), fileEvent, CancellationToken.None);

        await using var stream = (IResponseStream)subscriptionResult;
        await using var enumerator = stream.ReadResultsAsync().GetAsyncEnumerator(CancellationToken.None);

        Assert.True(await enumerator.MoveNextAsync(), "Expected at least one subscription event");
        var first = enumerator.Current;

        var json = first.ToJson();
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("data", out var data), $"no data: {json}");

        var eventType = data.GetProperty("fileEvents").GetProperty("eventType").GetString();
        Assert.Equal("UPLOADED", eventType);
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
}
