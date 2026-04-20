namespace Strg.Core.Storage;

public interface IStorageProviderConfig
{
    string? GetValue(string key);
    T? GetValue<T>(string key);
}
