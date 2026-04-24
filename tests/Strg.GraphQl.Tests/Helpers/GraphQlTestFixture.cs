using HotChocolate.Execution;
using HotChocolate.Execution.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Strg.Application.Abstractions;
using Strg.Application.DependencyInjection;
using Strg.Core.Auditing;
using Strg.Core.Domain;
using Strg.Core.Storage;
using Strg.GraphQl.Errors;
using Strg.Infrastructure.Auditing;
using Strg.Infrastructure.Data;

namespace Strg.GraphQl.Tests.Helpers;

public static class GraphQlTestFixture
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

    /// <summary>
    /// Wires the Phase 2 CQRS pipeline (IMediator + pipeline behaviors + validators) plus every
    /// port Strg.Application handlers depend on. Call this from a mutation/query test's
    /// <c>configureServices</c> hook AFTER the test has already registered its own
    /// <c>ITenantContext</c> and <c>StrgDbContext</c>.
    /// </summary>
    public static IServiceCollection AddStrgApplicationForTests(this IServiceCollection services, Guid? currentUserId = null)
    {
        services.AddScoped<IStrgDbContext>(sp => sp.GetRequiredService<StrgDbContext>());
        services.AddScoped<ITagRepository, TagRepository>();
        services.AddScoped<IFileRepository, FileRepository>();
        services.AddScoped<IAuditService, AuditService>();
        services.AddSingleton<IStorageProviderRegistry>(new StubStorageProviderRegistry());
        services.AddSingleton<ICurrentUser>(new StubCurrentUser(currentUserId ?? Guid.NewGuid()));
        services.AddStrgApplication();
        return services;
    }

    private sealed class StubCurrentUser(Guid userId) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
    }

    // Minimal registry stub — claims "local" is registered so CreateDriveHandler accepts the
    // canonical provider type used across the Phase 2 tests. Resolve/Register throw because
    // these code paths are exercised by real storage-provider integration tests, not the
    // wire-shape tests that depend on this stub.
    private sealed class StubStorageProviderRegistry : IStorageProviderRegistry
    {
        public bool IsRegistered(string providerType) =>
            string.Equals(providerType, "local", StringComparison.OrdinalIgnoreCase);

        public void Register(string providerType, Func<IStorageProviderConfig, IStorageProvider> factory) =>
            throw new NotSupportedException("StubStorageProviderRegistry is read-only.");

        public IStorageProvider Resolve(string providerType, IStorageProviderConfig config) =>
            throw new NotSupportedException("StubStorageProviderRegistry does not resolve providers.");

        public IReadOnlyList<string> GetRegisteredTypes() => ["local"];
    }
}

internal sealed class TestHostEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = Environments.Development;
    public string ApplicationName { get; set; } = "Strg.GraphQl.Tests";
    public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

public sealed class TestExecutor(IRequestExecutor inner, IServiceProvider rootServices, IReadOnlyDictionary<string, object?>? globalState = null)
{
    public IServiceProvider Services => rootServices;

    public Task<IExecutionResult> ExecuteAsync(string query, CancellationToken cancellationToken = default)
    {
        if (globalState is null || globalState.Count == 0)
        {
            return inner.ExecuteAsync(query, cancellationToken);
        }

        var builder = OperationRequestBuilder.New().SetDocument(query);
        foreach (var (key, value) in globalState)
        {
            builder.SetGlobalState(key, value);
        }

        return inner.ExecuteAsync(builder.Build(), cancellationToken);
    }
}
