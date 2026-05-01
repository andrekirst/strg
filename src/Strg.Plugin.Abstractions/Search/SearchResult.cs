namespace Strg.Plugin.Abstractions.Search;

/// <summary>
/// Result of a search query. <see cref="TotalCount"/> is the unpaged total — UIs use it to render
/// pagination controls.
/// </summary>
public sealed record SearchResult(
    IReadOnlyList<SearchHit> Hits,
    int TotalCount);
