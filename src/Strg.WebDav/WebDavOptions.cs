namespace Strg.WebDav;

/// <summary>
/// STRG-069 — WebDAV tunables bound from the <c>WebDav</c> configuration section. Kept in its own
/// options class (not inlined as primitives) so new knobs for STRG-070+ (lock manager TTL defaults,
/// MKCOL collection-depth caps, etc.) land on this type rather than inflating constructors.
/// </summary>
public sealed class WebDavOptions
{
    public const string SectionName = "WebDav";

    /// <summary>
    /// Hard ceiling on how many items a <c>Depth: infinity</c> PROPFIND may enumerate. The cap is
    /// load-bearing DoS defence: without it a client opening the root of a drive with millions of
    /// files would either OOM the host or hold a request thread open for minutes while the XML
    /// stream drains. The spec default of <c>10000</c> is roughly two orders of magnitude above
    /// what real Finder / Explorer windows render, leaving headroom for scripted clients but still
    /// bounded. Tests override to a small value to exercise the <c>507 Insufficient Storage</c>
    /// branch without seeding thousands of rows.
    /// </summary>
    public int PropfindInfinityMaxItems { get; set; } = 10_000;
}
