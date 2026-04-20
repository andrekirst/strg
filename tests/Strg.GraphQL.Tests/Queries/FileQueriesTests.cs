using System.Text.Json;
using HotChocolate.Authorization;
using HotChocolate.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Strg.Core.Domain;
using Strg.GraphQL.Queries;
using Strg.GraphQL.Queries.Storage;
using Strg.GraphQL.Tests.Helpers;
using Strg.GraphQL.Types;
using Strg.Infrastructure.Data;
using Xunit;

namespace Strg.GraphQL.Tests.Queries;

[Collection("database")]
public class FileQueriesTests
{
    private static readonly TestTenantContext SharedTenantCtx = TestTenantContext.Shared;

    [Fact]
    public async Task GetFiles_FilterByNameContains_ReturnsMatching()
    {
        var tenantId = Guid.NewGuid();
        var driveId = Guid.NewGuid();
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
                 .AddType<StorageQueries>()
                 .AddType<FileQueries>()
                 .AddType<FileItemType>()
                 .AddType<FileVersionType>()
                 .AddGlobalObjectIdentification();
                b.Services.AddSingleton<IAuthorizationHandler, AllowAllAuthorizationHandler>();
            });

        using (var scope = executor.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();
            db.Files.AddRange(
                new FileItem { TenantId = tenantId, DriveId = driveId, Name = "report.pdf", Path = "/report.pdf" },
                new FileItem { TenantId = tenantId, DriveId = driveId, Name = "notes.txt", Path = "/notes.txt" });
            await db.SaveChangesAsync();
        }

        var result = (IOperationResult)await executor.ExecuteAsync(
            $"{{ storage {{ files(driveId: \"{driveId}\", filter: {{ nameContains: \"report\" }}) {{ nodes {{ id name }} totalCount }} }} }}");

        var json = result.ToJson();
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("data", out var data), $"no data: {json}");
        var totalCount = data
            .GetProperty("storage")
            .GetProperty("files")
            .GetProperty("totalCount")
            .GetInt32();

        Assert.Equal(1, totalCount);
    }

    [Fact]
    public async Task GetFile_InaccessibleFile_ReturnsNull()
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
                 .AddType<StorageQueries>()
                 .AddType<FileQueries>()
                 .AddType<FileItemType>()
                 .AddType<FileVersionType>()
                 .AddGlobalObjectIdentification();
                b.Services.AddSingleton<IAuthorizationHandler, AllowAllAuthorizationHandler>();
            });

        var result = (IOperationResult)await executor.ExecuteAsync(
            $"{{ storage {{ file(id: \"{Guid.NewGuid()}\") {{ id }} }} }}");

        using var doc = JsonDocument.Parse(result.ToJson());
        var fileElement = doc.RootElement
            .GetProperty("data")
            .GetProperty("storage")
            .GetProperty("file");

        Assert.Equal(JsonValueKind.Null, fileElement.ValueKind);
    }
}
