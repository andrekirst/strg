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

    // STRG-073 fold-in #2 — an OidcBaseAddress option lived here in an earlier draft of the
    // bridge. It was REMOVED as a security-reviewer baseline: any config-bound base address for
    // the bridge's token-endpoint client is a credential-exfiltration vector — a deploy-time typo
    // on "WebDav:OidcBaseAddress" would redirect every WebDAV user's cleartext password to the
    // misconfigured URL. The bridge now resolves its target from IServer.Features at request time
    // (see WebDavServiceExtensions), which is the actual running Kestrel binding rather than any
    // settable value. Do NOT re-introduce an OidcBaseAddress option without pairing it with a
    // same-process-loopback validator.
}
