using Microsoft.Extensions.DependencyInjection;
using Strg.Core.Storage;
using Strg.Infrastructure.Storage.Encryption;

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

        // Encrypting-writer factory: the writer itself is per-drive (binds to a per-drive
        // IStorageProvider that can only be resolved at request time from the registry above),
        // so the factory is the DI-friendly seam. Singleton lifetime — IKeyProvider is the
        // only stateful dependency and itself is registered as singleton.
        services.AddSingleton<IEncryptingFileWriterFactory, AesGcmFileWriterFactory>();

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
