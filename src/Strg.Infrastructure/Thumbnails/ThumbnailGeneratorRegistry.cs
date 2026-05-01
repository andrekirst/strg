using Strg.Core.Services;

namespace Strg.Infrastructure.Thumbnails;

/// <summary>
/// First-registered-wins resolver. The DI container injects every <see cref="IThumbnailGenerator"/>
/// registration in registration order; the registry walks them and returns the first whose
/// <see cref="IThumbnailGenerator.CanHandle"/> matches.
///
/// <para>Mirrors <c>StorageProviderRegistry</c>'s shape — same first-match-wins discipline so
/// future test-only generators registered LATER don't accidentally shadow production ones.</para>
/// </summary>
public sealed class ThumbnailGeneratorRegistry : IThumbnailGeneratorRegistry
{
    private readonly IReadOnlyList<IThumbnailGenerator> _generators;

    public ThumbnailGeneratorRegistry(IEnumerable<IThumbnailGenerator> generators)
    {
        ArgumentNullException.ThrowIfNull(generators);
        _generators = generators.ToArray();
    }

    public IThumbnailGenerator? Resolve(string mimeType, ReadOnlySpan<byte> magicBytes)
    {
        // Manual loop because IEnumerable<>.FirstOrDefault doesn't accept a ref-struct (Span<>)
        // closure. Bounded N (1–3 generators in v1, ≤5 even with Phase 16 + future plugins).
        foreach (var generator in _generators)
        {
            if (generator.CanHandle(mimeType, magicBytes))
            {
                return generator;
            }
        }
        return null;
    }
}
