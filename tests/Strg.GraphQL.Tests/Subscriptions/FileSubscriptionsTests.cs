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

        await sender.SendAsync(Topics.FileEvents(driveId), fileEvent, CancellationToken.None);

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
}
