namespace Strg.Core.Constants;

/// <summary>
/// STRG-073 — the canonical scope set granted to the <c>webdav-internal</c> OpenIddict client
/// and requested by <c>BasicAuthJwtBridgeMiddleware</c>. Centralizing both consumers on one
/// array is the <c>C2-ext</c> "hardcode the NAME, not the literal" pattern applied to OAuth
/// scopes: drift between the middleware's <c>scope=</c> form param and the seed descriptor's
/// <c>Prefixes.Scope + "..."</c> permissions would create a silent-failure surface where the
/// bridge requests something the client isn't allowed to issue (→ 400 on every WebDAV call) OR
/// the client grants more than the bridge requests (→ over-privileged cached tokens).
///
/// <para><b>Why these three and no more.</b>
/// <list type="bullet">
///   <item><description><c>files.read</c> — PROPFIND, GET, HEAD.</description></item>
///   <item><description><c>files.write</c> — PUT, DELETE, MOVE, COPY, MKCOL, LOCK, UNLOCK.</description></item>
///   <item><description><c>tags.write</c> — PROPPATCH dead-property surface (deferred, but the
///     scope is preemptively granted so enabling PROPPATCH doesn't require a seed-migration).</description></item>
/// </list>
/// <c>files.share</c> is deliberately omitted: WebDAV clients cannot usefully share via the
/// protocol, and a stolen Basic-Auth credential must not be able to mint shareable links.
/// <c>admin</c> is omitted for the same reason. <c>offline_access</c> is omitted because the
/// 14-minute cache window is the intended revocation surface — refresh tokens would outlive it.</para>
/// </summary>
public static class WebDavScopes
{
    public const string FilesRead = "files.read";
    public const string FilesWrite = "files.write";
    public const string TagsWrite = "tags.write";

    /// <summary>
    /// Ordered array consumed by both the bridge's token request and the seed descriptor.
    /// Stable ordering is not required by OAuth (scope is a set), but preserving it makes
    /// diffs and log messages deterministic.
    /// </summary>
    public static readonly string[] All = [FilesRead, FilesWrite, TagsWrite];

    /// <summary>
    /// Space-separated form of <see cref="All"/> — the wire format for the <c>scope=</c>
    /// parameter on the OAuth2 token request (RFC 6749 §3.3).
    /// </summary>
    public static readonly string SpaceSeparated = string.Join(' ', All);
}
