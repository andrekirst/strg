using HotChocolate.Execution;
using HotChocolate.Execution.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Strg.GraphQL.Errors;

namespace Strg.GraphQL.Tests.Helpers;

public static class GraphQLTestFixture
{
    public static async Task<TestExecutor> CreateExecutorAsync(
        Action<IServiceCollection>? configureServices = null,
        Action<IRequestExecutorBuilder>? configureSchema = null,
        IReadOnlyDictionary<string, object?>? globalState = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        configureServices?.Invoke(services);

        var builder = services
            .AddGraphQLServer()
            .AddQueryType(q => q.Name("Query").Field("_placeholder").Type<HotChocolate.Types.BooleanType>().Resolve(() => true))
            .AddInMemorySubscriptions()
            .AddErrorFilter<StrgErrorFilter>();

        configureSchema?.Invoke(builder);

        var sp = services.BuildServiceProvider();
        var executor = await sp
            .GetRequiredService<IRequestExecutorResolver>()
            .GetRequestExecutorAsync();

        return new TestExecutor(executor, sp, globalState);
    }
}

internal sealed class TestHostEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = Environments.Development;
    public string ApplicationName { get; set; } = "Strg.GraphQL.Tests";
    public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

public sealed class TestExecutor(IRequestExecutor inner, IServiceProvider rootServices, IReadOnlyDictionary<string, object?>? globalState = null)
{
    public IServiceProvider Services => rootServices;

    public Task<IExecutionResult> ExecuteAsync(string query, CancellationToken ct = default)
    {
        if (globalState is null || globalState.Count == 0)
            return inner.ExecuteAsync(query, ct);

        var builder = OperationRequestBuilder.New().SetDocument(query);
        foreach (var (key, value) in globalState)
        {
            builder.SetGlobalState(key, value);
        }

        return inner.ExecuteAsync(builder.Build(), ct);
    }
}
