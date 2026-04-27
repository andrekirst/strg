using System.Globalization;
using System.Text.Json;

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

    /// <summary>
    /// Parses a <see cref="Domain.Drive.ProviderConfig"/> JSON blob into a flat
    /// string→string? dictionary view. Empty input or <c>"{}"</c> yields an empty config; an
    /// explicit JSON object is deserialized as-is. Canonical entry point for every caller that
    /// reaches the storage-provider registry from a drive — used by the upload, download,
    /// WebDAV, and abandoned-upload-cleanup paths in production plus the integration test
    /// fixtures.
    ///
    /// <para><b>Shape note.</b> This is the <em>strict</em> parser — non-string property
    /// values throw. <c>StorageHealthCheck</c> and <c>FileVersionStore.ResolveProvider</c>
    /// (both in Strg.Infrastructure) deliberately keep their own <c>JsonDocument</c>-based
    /// parsers because they tolerate non-string values; folding them into this factory would
    /// be a behavior change, not a refactor.</para>
    /// </summary>
    public static DictionaryStorageProviderConfig FromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}")
        {
            return new DictionaryStorageProviderConfig(new Dictionary<string, string?>());
        }
        var raw = JsonSerializer.Deserialize<Dictionary<string, string?>>(json)
                  ?? new Dictionary<string, string?>();
        return new DictionaryStorageProviderConfig(raw);
    }

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
