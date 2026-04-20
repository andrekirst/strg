using System.Text.Json;
using HotChocolate.Authorization;
using HotChocolate.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Strg.GraphQL.Mutations;
using Strg.GraphQL.Mutations.User;
using Strg.GraphQL.Tests.Helpers;
using Strg.GraphQL.Types;
using Strg.Infrastructure.Data;
using Xunit;

namespace Strg.GraphQL.Tests.Mutations;

[Collection("database")]
public class UserMutationsTests
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
                 .AddMutationType(m => m.Name("Mutation"))
                 .AddType<RootMutationExtension>()
                 .AddType<UserMutations>()
                 .AddType<UserMutationHandlers>()
                 .AddType<UserType>()
                 .AddGlobalObjectIdentification();
                b.Services.AddSingleton<IAuthorizationHandler, AllowAllAuthorizationHandler>();
            },
            globalState: new Dictionary<string, object?> { ["tenantId"] = tenantId, ["userId"] = userId });

    [Fact]
    public async Task UpdateProfile_DisplayNameTooLong_ReturnsValidationError()
    {
        var tenantId = Guid.NewGuid();
        SharedTenantCtx.TenantId = tenantId;

        var executor = await CreateExecutorAsync(tenantId, Guid.NewGuid(), Guid.NewGuid().ToString());

        var longName = new string('x', 256);
        var result = (IOperationResult)await executor.ExecuteAsync($$"""
            mutation {
              user {
                updateProfile(input: { displayName: "{{longName}}" }) {
                  user { displayName }
                  errors { code field }
                }
              }
            }
            """);

        var json = result.ToJson();
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("data", out var data), $"no data: {json}");
        var errorsEl = data.GetProperty("user").GetProperty("updateProfile").GetProperty("errors");
        var errors = errorsEl.EnumerateArray().ToList();
        Assert.NotEmpty(errors);
        Assert.Equal("VALIDATION_ERROR", errors[0].GetProperty("code").GetString());
        Assert.Equal("displayName", errors[0].GetProperty("field").GetString());
    }
}
