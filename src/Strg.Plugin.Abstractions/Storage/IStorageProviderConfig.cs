namespace Strg.Plugin.Abstractions.Storage;

public interface IStorageProviderConfig
{
    string? GetValue(string key);
    T? GetValue<T>(string key);
}
