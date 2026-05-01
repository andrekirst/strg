using System.Collections.ObjectModel;

namespace Strg.Infrastructure.Thumbnails;

/// <summary>
/// Strongly-typed configuration for the thumbnail subsystem. Bound to the <c>Thumbnails</c>
/// section in <c>appsettings.json</c>; validated at startup via <see cref="ThumbnailOptionsValidator"/>.
///
/// <para><b>Why <c>PdfEnabled</c> / <c>OfficeEnabled</c> exist in v1.</b> Issue #52 mandates the
/// config gates ship from day 1 even though Phase 16 ships the actual generators. Adding them
/// later would force a config-schema migration on operators (every appsettings.json across the
/// fleet would need to grow new keys). Reading the gates is a no-op in v1 — the registry just
/// has no PDF generator.</para>
/// </summary>
public sealed class ThumbnailOptions
{
    public const string SectionName = "Thumbnails";

    public bool Enabled { get; init; } = true;

    /// <summary>Variant whitelist; defaults to all three. Each entry must be in <c>ThumbnailVariants.All</c>.</summary>
    public IReadOnlyList<string> Variants { get; init; } =
        new ReadOnlyCollection<string>(["thumb", "small", "medium"]);

    /// <summary>Reject sources larger than this without reading. Default 256 MiB.</summary>
    public long MaxSourceSizeBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>Reject sources whose decoded pixel area exceeds this without full decode. Default 100 MP.</summary>
    public long MaxPixelArea { get; init; } = 100_000_000;

    /// <summary>Per-call generation timeout. Default 30 s. Validator caps at 600 s.</summary>
    public int GenerationTimeoutSeconds { get; init; } = 30;

    /// <summary>WebP quality (1–100). Default 82.</summary>
    public int WebPQuality { get; init; } = 82;

    /// <summary>Phase 16 gate — no effect in v1 (no PDF generator registered).</summary>
    public bool PdfEnabled { get; init; } = true;

    /// <summary>Phase 16 gate — no effect in v1 (no Office generator registered).</summary>
    public bool OfficeEnabled { get; init; }
}
