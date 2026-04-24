using System.Globalization;

namespace Strg.Core.Storage;

/// <summary>
/// In-memory <see cref="IStorageProviderConfig"/> backed by a string dictionary. Used by tests
/// and by the bootstrap path that deserializes <see cref="Domain.Drive.ProviderConfig"/> JSON
/// into a flat key/value view before handing it to the registry. Kept in Core because it has
/// no external dependencies and provider factories register in Infrastructure need to accept
/// it without a reverse reference.
/// </summary>
public sealed class DictionaryStorageProviderConfig(IDictionary<string, string?> values) : IStorageProviderConfig
{
    private readonly Dictionary<string, string?> _values = new(values, StringComparer.OrdinalIgnoreCase);

    public string? GetValue(string key) => _values.GetValueOrDefault(key);

    public T? GetValue<T>(string key)
    {
        var raw = GetValue(key);
        if (raw is null)
        {
            return default;
        }

        // Narrow conversion set covers the shapes a storage provider config actually needs
        // (rootPath, flags, size thresholds). Falls through to Convert.ChangeType for anything
        // else so callers retain an escape hatch without forcing a generic JSON binder.
        var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        if (targetType == typeof(string))
        {
            return (T)(object)raw;
        }
        if (targetType == typeof(bool))
        {
            return (T)(object)bool.Parse(raw);
        }
        if (targetType == typeof(int))
        {
            return (T)(object)int.Parse(raw, CultureInfo.InvariantCulture);
        }
        if (targetType == typeof(long))
        {
            return (T)(object)long.Parse(raw, CultureInfo.InvariantCulture);
        }

        return (T?)Convert.ChangeType(raw, targetType, CultureInfo.InvariantCulture);
    }
}
