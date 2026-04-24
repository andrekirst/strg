using System.Text.Json;
using HotChocolate.Authorization;
using HotChocolate.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Strg.Core.Domain;
using Strg.GraphQl.Queries;
using Strg.GraphQl.Queries.Storage;
using Strg.GraphQl.Tests.Helpers;
using Strg.Infrastructure.Data;
using Xunit;
using GraphQLDriveType = Strg.GraphQl.Types.DriveType;

namespace Strg.GraphQl.Tests.Queries;

[Collection("database")]
public class DriveQueriesTests
{
    private static readonly TestTenantContext SharedTenantCtx = TestTenantContext.Shared;

    [Fact]
    public async Task GetDrives_ReturnsOnlyCurrentTenantDrives()
    {
        var tenantId = Guid.NewGuid();
        SharedTenantCtx.TenantId = tenantId;
        var dbName = Guid.NewGuid().ToString();

        var executor = await GraphQlTestFixture.CreateExecutorAsync(
            configureServices: services =>
            {
                services.AddSingleton<ITenantContext>(SharedTenantCtx);
                services.AddDbContext<StrgDbContext>(o => o.UseInMemoryDatabase(dbName));
            },
            configureSchema: b =>
            {
                b.AddAuthorization()
                 .AddType<RootQueryExtension>()
                 .AddType<DriveQueries>()
                 .AddType<GraphQLDriveType>()
                 .AddGlobalObjectIdentification();
                b.Services.AddSingleton<IAuthorizationHandler, AllowAllAuthorizationHandler>();
            });

        using (var scope = executor.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();
            db.Drives.AddRange(
                new Drive { TenantId = tenantId, Name = "Drive A", ProviderType = "local" },
                new Drive { TenantId = tenantId, Name = "Drive B", ProviderType = "local" },
                new Drive { TenantId = Guid.NewGuid(), Name = "Other Drive", ProviderType = "local" });
            await db.SaveChangesAsync();
        }

        var result = (IOperationResult)await executor.ExecuteAsync(
            "{ storage { drives(first: 10) { nodes { id name } totalCount } } }");

        var json = result.ToJson();
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("data", out var data), $"no data: {json}");
        Assert.True(data.ValueKind == JsonValueKind.Object, $"data is null: {json}");
        var totalCount = data
            .GetProperty("storage")
            .GetProperty("drives")
            .GetProperty("totalCount")
            .GetInt32();

        Assert.Equal(2, totalCount);
    }

    [Fact]
    public async Task GetDrive_OtherTenantDrive_ReturnsNull()
    {
        var tenantId = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();
        var otherDriveId = Guid.NewGuid();
        SharedTenantCtx.TenantId = tenantId;

        var executor = await GraphQlTestFixture.CreateExecutorAsync(
            configureServices: services =>
            {
                services.AddSingleton<ITenantContext>(SharedTenantCtx);
                services.AddDbContext<StrgDbContext>(o => o.UseInMemoryDatabase(dbName));
            },
            configureSchema: b =>
            {
                b.AddAuthorization()
                 .AddType<RootQueryExtension>()
                 .AddType<DriveQueries>()
                 .AddType<GraphQLDriveType>()
                 .AddGlobalObjectIdentification();
                b.Services.AddSingleton<IAuthorizationHandler, AllowAllAuthorizationHandler>();
            });

        using (var scope = executor.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();
            db.Drives.Add(new Drive
            {
                Id = otherDriveId,
                TenantId = Guid.NewGuid(),
                Name = "Other Tenant Drive",
                ProviderType = "local"
            });
            await db.SaveChangesAsync();
        }

        var result = (IOperationResult)await executor.ExecuteAsync(
            $"{{ storage {{ drive(id: \"{otherDriveId}\") {{ id }} }} }}");

        using var doc = JsonDocument.Parse(result.ToJson());
        var driveElement = doc.RootElement
            .GetProperty("data")
            .GetProperty("storage")
            .GetProperty("drive");

        Assert.Equal(JsonValueKind.Null, driveElement.ValueKind);
    }
}
