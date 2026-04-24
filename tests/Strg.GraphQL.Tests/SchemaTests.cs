using System.Text.Json;
using HotChocolate.Execution;
using Microsoft.Extensions.DependencyInjection;
using Strg.GraphQL.Tests.Helpers;
using Strg.GraphQL.Types;
using Xunit;
using GraphQLDriveType = Strg.GraphQL.Types.DriveType;

namespace Strg.GraphQL.Tests;

public class SchemaTests
{
    [Fact]
    public async Task DriveType_ProviderConfig_NotInSchema()
    {
        var executor = await GraphQlTestFixture.CreateExecutorAsync(
            configureSchema: b => b.AddType<GraphQLDriveType>());

        var result = (IOperationResult)await executor.ExecuteAsync("""
            {
              __type(name: "Drive") {
                fields { name }
              }
            }
            """);

        var fields = GetFieldNames(result, "__type");

        Assert.DoesNotContain("providerConfig", fields);
        Assert.DoesNotContain("tenantId", fields);
    }

    [Fact]
    public async Task FileItemType_HasIsFolder_NotIsDirectory()
    {
        var executor = await GraphQlTestFixture.CreateExecutorAsync(
            configureSchema: b => b
                .AddType<FileItemType>()
                .AddType<FileVersionType>());

        var result = (IOperationResult)await executor.ExecuteAsync("""
            {
              __type(name: "FileItem") {
                fields { name }
              }
            }
            """);

        var fields = GetFieldNames(result, "FileItem");

        Assert.Contains("isFolder", fields);
        Assert.DoesNotContain("isDirectory", fields);
        Assert.DoesNotContain("tenantId", fields);
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
