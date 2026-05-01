namespace Strg.Core.Services;

/// <summary>
/// Generator-agnostic request envelope passed to <see cref="IThumbnailGenerator.GenerateAsync"/>.
/// Contains everything the generator needs to size + format the output without re-resolving
/// configuration.
/// </summary>
public sealed record ThumbnailRequest(
    string Variant,
    int TargetEdgePixels,
    string TargetFormat,
    long SourceSizeBytes,
    string SourceMimeType);

/// <summary>
/// Sum-type result from <see cref="IThumbnailGenerator.GenerateAsync"/>. Each shape maps to a
/// different <see cref="Domain.ThumbnailStatus"/> + metric reason in the consumer. Failure
/// modes are NOT exceptions — consumer code branches deterministically on the case.
/// </summary>
public abstract record ThumbnailGenerationOutcome
{
    /// <summary>
    /// Generation succeeded. <see cref="Output"/> is owned by the caller (consumer disposes via
    /// <c>await using</c> after writing to <c>IStorageProvider</c>).
    /// </summary>
    public sealed record Success(Stream Output, int Width, int Height, string Format)
        : ThumbnailGenerationOutcome;

    /// <summary>The generator does not handle this MIME / extension after a closer look.</summary>
    public sealed record Unsupported(string Reason) : ThumbnailGenerationOutcome;

    /// <summary>The source bytes failed to decode (truncated, malformed, or library-bug shape).</summary>
    public sealed record SourceCorrupt(string Reason) : ThumbnailGenerationOutcome;

    /// <summary>The source exceeded a configured limit (e.g. pixel-area cap).</summary>
    public sealed record ResourceLimitExceeded(string Reason) : ThumbnailGenerationOutcome;

    /// <summary>The per-call timeout fired before the generator finished.</summary>
    public sealed record TimedOut(TimeSpan Limit) : ThumbnailGenerationOutcome;
}

/// <summary>
/// A thumbnail-format-specific generator (image, PDF, Office). Generators self-declare via
/// <see cref="CanHandle"/> so the consumer never branches on MIME — new generators (Phase 16)
/// plug into the registry without consumer/API/DB changes.
///
/// <para>v1 ships exactly one generator (Magick.NET for raster images). Phase 16 adds PDFium
/// and LibreOffice generators behind the same contract.</para>
/// </summary>
public interface IThumbnailGenerator
{
    /// <summary>
    /// Stable identifier for the generator (e.g. <c>"magick-net-q8"</c>) — written into
    /// <c>ThumbnailEntry.GeneratorVersion</c> so a future bump-and-regen admin action can target
    /// rows by generator without inspecting blob bytes.
    /// </summary>
    string Version { get; }

    /// <summary>
    /// True when this generator can produce a thumbnail for the source described by
    /// <paramref name="mimeType"/> + <paramref name="magicBytes"/> (first ~16 bytes of the
    /// source). Implementations are encouraged to consult both — <see cref="MimeSniffer"/>
    /// is conservative and may return a generic MIME for some formats.
    /// </summary>
    bool CanHandle(string mimeType, ReadOnlySpan<byte> magicBytes);

    /// <summary>
    /// Generate one thumbnail for <paramref name="request"/>. <paramref name="source"/> is read
    /// from start; the implementation MUST stream end-to-end where the underlying library
    /// permits (no full <c>byte[]</c> buffering of the source). The returned
    /// <see cref="ThumbnailGenerationOutcome.Success"/> stream is owned by the caller.
    /// </summary>
    Task<ThumbnailGenerationOutcome> GenerateAsync(
        Stream source,
        ThumbnailRequest request,
        CancellationToken cancellationToken);
}
