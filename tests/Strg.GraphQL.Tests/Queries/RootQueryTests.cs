using System.Text.Json;
using HotChocolate.Execution;
using Microsoft.Extensions.DependencyInjection;
using Strg.GraphQL.Mutations;
using Strg.GraphQL.Queries;
using Strg.GraphQL.Tests.Helpers;
using Xunit;

namespace Strg.GraphQL.Tests.Queries;

public class RootQueryTests
{
    [Fact]
    public async Task Query_HasStorageInboxAdminMeFields()
    {
        var executor = await GraphQLTestFixture.CreateExecutorAsync(
            configureSchema: b => b
                .AddAuthorization()
                .AddType<RootQueryExtension>());

        var result = (IOperationResult)await executor.ExecuteAsync(
            "{ __type(name: \"Query\") { fields { name } } }");

        var fields = GetFieldNames(result, "Query");
        Assert.Contains("storage", fields);
        Assert.Contains("inbox", fields);
        Assert.Contains("admin", fields);
        Assert.Contains("me", fields);
    }

    [Fact]
    public async Task Mutation_HasStorageUserAdminInboxFields()
    {
        var executor = await GraphQLTestFixture.CreateExecutorAsync(
            configureSchema: b => b
                .AddMutationType(m => m.Name("Mutation"))
                .AddType<RootMutationExtension>());

        var result = (IOperationResult)await executor.ExecuteAsync(
            "{ __type(name: \"Mutation\") { fields { name } } }");

        var fields = GetFieldNames(result, "Mutation");
        Assert.Contains("storage", fields);
        Assert.Contains("user", fields);
        Assert.Contains("admin", fields);
        Assert.Contains("inbox", fields);
    }

    private static List<string> GetFieldNames(IOperationResult result, string typeName)
    {
        using var doc = JsonDocument.Parse(result.ToJson());
        return doc.RootElement
            .GetProperty("data")
            .GetProperty("__type")
            .GetProperty("fields")
            .EnumerateArray()
            .Select(f => f.GetProperty("name").GetString()!)
            .ToList();
    }
}
