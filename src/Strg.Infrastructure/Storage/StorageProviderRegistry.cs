using System.Collections.Concurrent;
using Strg.Core.Storage;

namespace Strg.Infrastructure.Storage;

public sealed class StorageProviderRegistry : IStorageProviderRegistry
{
    private readonly ConcurrentDictionary<string, Func<IStorageProviderConfig, IStorageProvider>> _factories = new(StringComparer.OrdinalIgnoreCase);

    public void Register(string providerType, Func<IStorageProviderConfig, IStorageProvider> factory)
    {
        ArgumentNullException.ThrowIfNull(providerType);
        ArgumentNullException.ThrowIfNull(factory);
        _factories[providerType] = factory;
    }

    public IStorageProvider Resolve(string providerType, IStorageProviderConfig config)
    {
        if (!_factories.TryGetValue(providerType, out var factory))
        {
            throw new InvalidOperationException($"No storage provider registered for type '{providerType}'. Registered types: {string.Join(", ", _factories.Keys)}");
        }
        return factory(config);
    }

    public bool IsRegistered(string providerType) => _factories.ContainsKey(providerType);

    public IReadOnlyList<string> GetRegisteredTypes() => _factories.Keys.ToList();
}
