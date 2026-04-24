using System.Text.Json;
using HotChocolate.Authorization;
using HotChocolate.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Strg.GraphQl.Mutations;
using Strg.GraphQl.Mutations.Storage;
using Strg.GraphQl.Tests.Helpers;
using Strg.GraphQl.Types;
using Strg.Infrastructure.Data;
using Xunit;

namespace Strg.GraphQl.Tests.Mutations;

[Collection("database")]
public class FileMutationsTests
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
                 .AddMutationType(m => m.Name("Mutation"))
                 .AddType<RootMutationExtension>()
                 .AddType<StorageMutations>()
                 .AddType<FileMutations>()
                 .AddType<FileItemType>()
                 .AddType<FileVersionType>()
                 .AddGlobalObjectIdentification();
                b.Services.AddSingleton<IAuthorizationHandler, AllowAllAuthorizationHandler>();
            },
            globalState: new Dictionary<string, object?> { ["tenantId"] = tenantId, ["userId"] = userId });

    [Fact]
    public async Task CreateFolder_PathTraversal_ReturnsInvalidPathError()
    {
        var tenantId = Guid.NewGuid();
        SharedTenantCtx.TenantId = tenantId;
        var executor = await CreateExecutorAsync(tenantId, Guid.NewGuid(), Guid.NewGuid().ToString());

        var driveId = Guid.NewGuid();
        var result = (IOperationResult)await executor.ExecuteAsync($$"""
            mutation {
              storage {
                createFolder(input: { driveId: "{{driveId}}", path: "../../etc/passwd" }) {
                  file { id }
                  errors { code field }
                }
              }
            }
            """);

        var json = result.ToJson();
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("data", out var data), $"no data: {json}");
        var errorsEl = data.GetProperty("storage").GetProperty("createFolder").GetProperty("errors");
        var errors = errorsEl.EnumerateArray().ToList();
        Assert.NotEmpty(errors);
        Assert.Equal("INVALID_PATH", errors[0].GetProperty("code").GetString());
        Assert.Equal("path", errors[0].GetProperty("field").GetString());
    }

    [Fact]
    public async Task DeleteFile_NotFound_ReturnsNotFoundError()
    {
        var tenantId = Guid.NewGuid();
        SharedTenantCtx.TenantId = tenantId;
        var executor = await CreateExecutorAsync(tenantId, Guid.NewGuid(), Guid.NewGuid().ToString());

        var fileId = Guid.NewGuid();
        var result = (IOperationResult)await executor.ExecuteAsync($$"""
            mutation {
              storage {
                deleteFile(input: { id: "{{fileId}}" }) {
                  fileId
                  errors { code }
                }
              }
            }
            """);

        var json = result.ToJson();
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("data", out var data), $"no data: {json}");
        var errorsEl = data.GetProperty("storage").GetProperty("deleteFile").GetProperty("errors");
        var errors = errorsEl.EnumerateArray().ToList();
        Assert.Equal("NOT_FOUND", errors[0].GetProperty("code").GetString());
    }
}
