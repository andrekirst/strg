using System.Text.Json;
using HotChocolate.Authorization;
using HotChocolate.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Strg.Core.Domain;
using Strg.GraphQl.Mutations;
using Strg.GraphQl.Mutations.Storage;
using Strg.GraphQl.Tests.Helpers;
using Strg.GraphQl.Types;
using Strg.Infrastructure.Data;
using Xunit;

namespace Strg.GraphQl.Tests.Mutations;

[Collection("database")]
public class TagMutationsTests
{
    private static readonly TestTenantContext SharedTenantCtx = TestTenantContext.Shared;

    private Task<TestExecutor> CreateExecutorAsync(Guid tenantId, string dbName) =>
        GraphQlTestFixture.CreateExecutorAsync(
            configureServices: services =>
            {
                services.AddSingleton<ITenantContext>(SharedTenantCtx);
                services.AddDbContext<StrgDbContext>(o => o.UseInMemoryDatabase(dbName));
                services.AddStrgApplicationForTests();
            },
            configureSchema: b =>
            {
                b.AddAuthorization()
                 .AddMutationType(m => m.Name("Mutation"))
                 .AddType<RootMutationExtension>()
                 .AddType<StorageMutations>()
                 .AddType<TagMutations>()
                 .AddType<TagType>()
                 .AddGlobalObjectIdentification();
                b.Services.AddSingleton<IAuthorizationHandler, AllowAllAuthorizationHandler>();
            },
            globalState: new Dictionary<string, object?> { ["tenantId"] = tenantId, ["userId"] = Guid.NewGuid() });

    [Fact]
    public async Task AddTag_KeyTooLong_ReturnsValidationError()
    {
        var tenantId = Guid.NewGuid();
        SharedTenantCtx.TenantId = tenantId;

        var executor = await CreateExecutorAsync(tenantId, Guid.NewGuid().ToString());

        var longKey = new string('x', 256);
        var fileId = Guid.NewGuid();
        var result = (IOperationResult)await executor.ExecuteAsync($$"""
            mutation {
              storage {
                addTag(input: { fileId: "{{fileId}}", key: "{{longKey}}", value: "v", valueType: STRING }) {
                  tag { id }
                  errors { code field }
                }
              }
            }
            """);

        var json = result.ToJson();
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("data", out var data), $"no data: {json}");
        var errorsEl = data.GetProperty("storage").GetProperty("addTag").GetProperty("errors");
        var errors = errorsEl.EnumerateArray().ToList();
        Assert.NotEmpty(errors);
        Assert.Equal("VALIDATION_ERROR", errors[0].GetProperty("code").GetString());
        Assert.Equal("key", errors[0].GetProperty("field").GetString());
    }
}
