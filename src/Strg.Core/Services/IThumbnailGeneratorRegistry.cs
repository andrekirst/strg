namespace Strg.Core.Services;

/// <summary>
/// First-registered-match-wins resolver over all <see cref="IThumbnailGenerator"/> registrations.
/// Mirrors the <c>IStorageProviderRegistry</c> shape so consumers stay generator-agnostic.
/// </summary>
public interface IThumbnailGeneratorRegistry
{
    /// <summary>
    /// Returns the first registered generator whose <see cref="IThumbnailGenerator.CanHandle"/>
    /// returns true for <paramref name="mimeType"/> + <paramref name="magicBytes"/>, or
    /// <c>null</c> when no generator self-declared support.
    /// </summary>
    IThumbnailGenerator? Resolve(string mimeType, ReadOnlySpan<byte> magicBytes);
}
