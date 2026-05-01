namespace Strg.Plugin.Abstractions.Storage;

public interface IStorageProviderRegistry
{
    void Register(string providerType, Func<IStorageProviderConfig, IStorageProvider> factory);
    IStorageProvider Resolve(string providerType, IStorageProviderConfig config);
    bool IsRegistered(string providerType);
    IReadOnlyList<string> GetRegisteredTypes();
}
