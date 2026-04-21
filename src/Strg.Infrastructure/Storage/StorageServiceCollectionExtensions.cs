using Microsoft.Extensions.DependencyInjection;
using Strg.Core.Storage;

namespace Strg.Infrastructure.Storage;

/// <summary>
/// DI bootstrap for the storage subsystem. Keeps the "register the registry" and "register
/// builtin factories" steps atomic: if the registry were added with <c>AddSingleton</c> and the
/// factory wired separately in a hosted service, any component resolved before the hosted service
/// ran would see an empty registry. Building the factory list inside the singleton factory closes
/// that window.
/// </summary>
public static class StorageServiceCollectionExtensions
{
    public static IServiceCollection AddStrgStorageProviders(this IServiceCollection services)
    {
        services.AddSingleton<IStorageProviderRegistry>(_ =>
        {
            var registry = new StorageProviderRegistry();
            RegisterBuiltIns(registry);
            return registry;
        });
        return services;
    }

    private static void RegisterBuiltIns(IStorageProviderRegistry registry)
    {
        registry.Register("local", config =>
        {
            var rootPath = config.GetValue<string>("rootPath")
                ?? throw new InvalidOperationException(
                    "'local' storage provider requires 'rootPath' in ProviderConfig.");
            return new LocalFileSystemProvider(rootPath);
        });
    }
}
