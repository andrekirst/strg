using HotChocolate.Execution;
using HotChocolate.Types;
using Microsoft.Extensions.DependencyInjection;
using Strg.Core.Storage;
using Strg.GraphQL.Tests.Helpers;
using Xunit;

namespace Strg.GraphQL.Tests;

public class ErrorFilterTests
{
    [Fact]
    public async Task StoragePathException_MapsTo_InvalidPathCode()
    {
        var executor = await GraphQLTestFixture.CreateExecutorAsync(
            configureSchema: b => b.AddType<ThrowingQuery>());

        var result = (IOperationResult)await executor.ExecuteAsync("{ throwPath }");

        Assert.Single(result.Errors!);
        Assert.Equal("INVALID_PATH", result.Errors![0].Code);
        Assert.DoesNotContain("StoragePath", result.Errors![0].Message);
    }

    [Fact]
    public async Task UnhandledException_MapsTo_InternalErrorCode()
    {
        var executor = await GraphQLTestFixture.CreateExecutorAsync(
            configureSchema: b => b.AddType<ThrowingQuery>());

        var result = (IOperationResult)await executor.ExecuteAsync("{ throwUnknown }");

        Assert.Single(result.Errors!);
        Assert.Equal("INTERNAL_ERROR", result.Errors![0].Code);
    }

    [ExtendObjectType("Query")]
    private sealed class ThrowingQuery
    {
        public string ThrowPath() => throw new StoragePathException("traversal attempt");
        public string ThrowUnknown() => throw new InvalidOperationException("internal bug");
    }
}
