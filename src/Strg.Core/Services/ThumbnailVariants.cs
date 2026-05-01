namespace Strg.Core.Services;

/// <summary>
/// Static catalogue of thumbnail variant identifiers + their pixel-edge sizes. String constants
/// (not <c>enum</c>) so future operator-tunable variants (config-driven via <c>ThumbnailOptions</c>)
/// don't force a code change; the database column is also <c>varchar</c> so the wire format
/// stays stable across deploys.
/// </summary>
public static class ThumbnailVariants
{
    public const string Thumb = "thumb";
    public const string Small = "small";
    public const string Medium = "medium";

    /// <summary>
    /// Square edge length in pixels for each variant. The image generator resizes the longest
    /// edge to this and letterboxes the result onto a square canvas.
    /// </summary>
    public static int EdgePixelsFor(string variant) => variant switch
    {
        Thumb => 256,
        Small => 512,
        Medium => 1024,
        _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, "Unknown thumbnail variant"),
    };

    /// <summary>The default variant set used by the generation consumer when no override is configured.</summary>
    public static IReadOnlyList<string> All { get; } = [Thumb, Small, Medium];

    /// <summary>True when <paramref name="variant"/> is in the <see cref="All"/> whitelist.</summary>
    public static bool IsKnown(string variant) =>
        variant == Thumb || variant == Small || variant == Medium;
}

/// <summary>
/// Output formats the thumbnail subsystem can write. WebP is the v1 default (D9). JPEG is the
/// reserved fallback for clients that cannot render WebP — not implemented in v1.
/// </summary>
public static class ThumbnailFormats
{
    public const string WebP = "webp";
    public const string Jpeg = "jpeg";

    public static bool IsKnown(string format) => format == WebP || format == Jpeg;
}
