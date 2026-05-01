using System.Text.Json;
using HotChocolate.Execution;
using Microsoft.Extensions.DependencyInjection;
using Strg.GraphQl.Tests.Helpers;
using Strg.GraphQl.Types;
using Xunit;
using GraphQLDriveType = Strg.GraphQl.Types.DriveType;

namespace Strg.GraphQl.Tests;

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

    /// <summary>
    /// STRG-300 — pins that the new <c>isDefault</c> field is exposed on the GraphQL Drive type.
    /// A future change that adds <c>.Ignore()</c> to <c>DriveType</c> would silently hide it from
    /// the wire and break the inbox-feature consumer; this test catches that regression.
    /// </summary>
    [Fact]
    public async Task DriveType_IsDefault_InSchema()
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
        Assert.Contains("isDefault", fields);
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
