using System.Text.Json;
using HotChocolate.Authorization;
using HotChocolate.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Strg.GraphQL.Mutations;
using Strg.GraphQL.Mutations.Storage;
using Strg.GraphQL.Tests.Helpers;
using Strg.GraphQL.Types;
using Strg.Infrastructure.Data;
using Xunit;
using GraphQLDriveType = Strg.GraphQL.Types.DriveType;

namespace Strg.GraphQL.Tests.Mutations;

[Collection("database")]
public class DriveMutationsTests
{
    private static readonly TestTenantContext SharedTenantCtx = TestTenantContext.Shared;

    private Task<TestExecutor> CreateExecutorAsync(Guid tenantId, string dbName) =>
        GraphQLTestFixture.CreateExecutorAsync(
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
                 .AddType<DriveMutations>()
                 .AddType<GraphQLDriveType>()
                 .AddGlobalObjectIdentification();
                b.Services.AddSingleton<IAuthorizationHandler, AllowAllAuthorizationHandler>();
            },
            globalState: new Dictionary<string, object?> { ["tenantId"] = tenantId });

    [Fact]
    public async Task CreateDrive_InvalidName_ReturnsValidationError()
    {
        var tenantId = Guid.NewGuid();
        SharedTenantCtx.TenantId = tenantId;

        var executor = await CreateExecutorAsync(tenantId, Guid.NewGuid().ToString());

        var result = (IOperationResult)await executor.ExecuteAsync("""
            mutation {
              storage {
                createDrive(input: { name: "My Invalid Drive!", providerType: "local", providerConfig: "{}", isEncrypted: false }) {
                  drive { id }
                  errors { code field }
                }
              }
            }
            """);

        var json = result.ToJson();
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("data", out var data), $"no data: {json}");
        var errorsEl = data.GetProperty("storage").GetProperty("createDrive").GetProperty("errors");
        Assert.Equal(JsonValueKind.Array, errorsEl.ValueKind);
        var errors = errorsEl.EnumerateArray().ToList();
        Assert.NotEmpty(errors);
        Assert.Equal("VALIDATION_ERROR", errors[0].GetProperty("code").GetString());
        Assert.Equal("name", errors[0].GetProperty("field").GetString());
    }

    [Fact]
    public async Task CreateDrive_ValidInput_ReturnsDrive()
    {
        var tenantId = Guid.NewGuid();
        SharedTenantCtx.TenantId = tenantId;

        var executor = await CreateExecutorAsync(tenantId, Guid.NewGuid().ToString());

        var result = (IOperationResult)await executor.ExecuteAsync("""
            mutation {
              storage {
                createDrive(input: { name: "my-drive", providerType: "local", providerConfig: "{}", isEncrypted: false }) {
                  drive { id name }
                  errors { code }
                }
              }
            }
            """);

        var json = result.ToJson();
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("data", out var data), $"no data: {json}");
        var driveEl = data.GetProperty("storage").GetProperty("createDrive").GetProperty("drive");
        Assert.Equal(JsonValueKind.Object, driveEl.ValueKind);
        Assert.Equal("my-drive", driveEl.GetProperty("name").GetString());
    }
}
