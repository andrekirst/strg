namespace Strg.Plugin.Abstractions.Tagging;

/// <summary>
/// A single tag suggestion. <see cref="Confidence"/> is in the range <c>[0, 1]</c>; the host
/// applies a configurable threshold (per-tenant) before persisting the suggestion as a tag.
/// </summary>
public sealed record TagSuggestion(string Key, string Value, float Confidence);
