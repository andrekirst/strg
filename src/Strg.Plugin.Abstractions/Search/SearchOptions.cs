namespace Strg.Plugin.Abstractions.Search;

/// <summary>
/// Tunables for <see cref="ISearchProvider.SearchAsync"/>. v0.1 ships the minimum needed for
/// pagination and provider-specific filtering; highlighting, faceting, and sort order are
/// reserved for v0.2 once the in-tree provider exercises the surface end-to-end.
/// </summary>
/// <param name="Limit">Maximum number of hits to return; the provider MUST cap silently.</param>
/// <param name="Offset">Zero-based offset for pagination.</param>
/// <param name="Filters">Opaque key/value filters interpreted by the provider; <c>null</c> means none.</param>
public sealed record SearchOptions(
    int Limit,
    int Offset,
    IReadOnlyDictionary<string, string>? Filters);
