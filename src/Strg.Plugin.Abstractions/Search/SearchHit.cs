namespace Strg.Plugin.Abstractions.Search;

/// <summary>
/// A single search hit. <see cref="Snippet"/> is a short context excerpt with the match
/// highlighted (provider-defined markup); <see cref="Score"/> is the provider's relevance value
/// (range and scale are provider-specific — callers SHOULD treat it as ordinal, not absolute).
/// </summary>
public sealed record SearchHit(Guid FileId, string Snippet, float Score);
