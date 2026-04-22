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

    /// <summary>
    /// Default lock duration when the client doesn't send a <c>Timeout:</c> header, in seconds.
    /// RFC 4918 §10.7 leaves this implementation-defined; 10 minutes matches what Office, Finder,
    /// and Explorer send when editing documents and long enough that a user can save-pause-save
    /// without the lock expiring mid-session.
    /// </summary>
    public int DefaultLockTimeoutSeconds { get; set; } = 600;

    /// <summary>
    /// Hard cap on requested lock timeout. Clients can ask for <c>Second-Infinite</c>; we refuse
    /// to honor that because a misbehaving or abandoned client would otherwise hold a resource
    /// indefinitely. 1 hour is the ceiling — anyone editing for longer is expected to refresh the
    /// lock via the RFC 4918 §7.6 LOCK-with-If-header dance.
    /// </summary>
    public int MaxLockTimeoutSeconds { get; set; } = 3600;

    /// <summary>
    /// STRG-073 — base address the Basic-Auth bridge uses to reach <c>/connect/token</c>. In the
    /// default single-process deployment this is a loopback URL on the same Kestrel instance; a
    /// future split that moves the token endpoint to a sidecar would flip this to the sidecar's
    /// URL with no code change on the bridge. Kept on <see cref="WebDavOptions"/> rather than a
    /// top-level <c>Oidc</c> section so the WebDAV-only coupling stays visible in config.
    /// </summary>
    public string OidcBaseAddress { get; set; } = "http://127.0.0.1:5000";
}
