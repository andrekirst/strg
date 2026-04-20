using System.Text.Json;
using HotChocolate.Authorization;
using HotChocolate.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Strg.Core.Domain;
using Strg.GraphQL.Queries;
using Strg.GraphQL.Queries.Admin;
using Strg.GraphQL.Tests.Helpers;
using Strg.GraphQL.Types;
using Strg.Infrastructure.Data;
using Xunit;

namespace Strg.GraphQL.Tests.Queries;

[Collection("database")]
public class AuditQueriesTests
{
    private static readonly TestTenantContext SharedTenantCtx = TestTenantContext.Shared;

    [Fact]
    public async Task GetAuditLog_FilterByAction_ReturnsMatching()
    {
        var tenantId = Guid.NewGuid();
        SharedTenantCtx.TenantId = tenantId;
        var dbName = Guid.NewGuid().ToString();

        var executor = await GraphQLTestFixture.CreateExecutorAsync(
            configureServices: services =>
            {
                services.AddSingleton<ITenantContext>(SharedTenantCtx);
                services.AddDbContext<StrgDbContext>(o => o.UseInMemoryDatabase(dbName));
            },
            configureSchema: b =>
            {
                b.AddAuthorization()
                 .AddType<RootQueryExtension>()
                 .AddType<AdminQueries>()
                 .AddType<AuditLogQueries>()
                 .AddType<AuditEntryType>()
                 .AddType<UserType>()
                 .AddGlobalObjectIdentification();
                b.Services.AddSingleton<IAuthorizationHandler, AllowAllAuthorizationHandler>();
            });

        var userId = Guid.NewGuid();
        using (var scope = executor.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();
            db.AuditEntries.AddRange(
                new AuditEntry { TenantId = tenantId, UserId = userId, Action = "file.upload", ResourceType = "FileItem", ResourceId = Guid.NewGuid() },
                new AuditEntry { TenantId = tenantId, UserId = userId, Action = "file.delete", ResourceType = "FileItem", ResourceId = Guid.NewGuid() },
                new AuditEntry { TenantId = Guid.NewGuid(), UserId = userId, Action = "file.upload", ResourceType = "FileItem", ResourceId = Guid.NewGuid() });
            await db.SaveChangesAsync();
        }

        var result = (IOperationResult)await executor.ExecuteAsync(
            "{ admin { auditLog(first: 10, filter: { action: \"file.upload\" }) { nodes { id action } totalCount } } }");

        var json = result.ToJson();
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("data", out var data), $"no data: {json}");
        var totalCount = data
            .GetProperty("admin")
            .GetProperty("auditLog")
            .GetProperty("totalCount")
            .GetInt32();

        Assert.Equal(1, totalCount);
    }
}
