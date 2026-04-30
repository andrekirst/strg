namespace Strg.Core.Constants;

/// <summary>
/// String constants for the WebDAV request headers that <c>Microsoft.Net.Http.Headers.HeaderNames</c>
/// does not pre-define. Same drift-defence rationale as <see cref="WebDavMethods"/>: a typo in
/// <c>"Destinaton"</c> would land in a <c>Headers[...]</c> indexer that returns an empty
/// StringValues rather than throwing, and the middleware would silently treat the COPY/MOVE as
/// missing-Destination → 400 even though the client sent a valid request.
/// </summary>
public static class WebDavHeaderNames
{
    /// <summary>RFC 4918 §10.3 — destination URI for COPY and MOVE.</summary>
    public const string Destination = "Destination";

    /// <summary>RFC 4918 §10.6 — <c>"T"</c>/<c>"F"</c> precondition for COPY and MOVE.</summary>
    public const string Overwrite = "Overwrite";

    /// <summary>RFC 4918 §10.2 — <c>0</c>/<c>1</c>/<c>infinity</c> traversal scope for PROPFIND.</summary>
    public const string Depth = "Depth";

    /// <summary>RFC 4918 §10.4 — conditional precondition carrying a lock token.</summary>
    public const string If = "If";

    /// <summary>RFC 4918 §10.5 — lock token returned on LOCK and required on UNLOCK.</summary>
    public const string LockToken = "Lock-Token";

    /// <summary>RFC 4918 §10.7 — requested lock duration (LOCK requests).</summary>
    public const string Timeout = "Timeout";

    /// <summary>
    /// Diagnostic header used by the WebDAV middleware to disambiguate 409 Conflict shapes
    /// (race-window collision vs. deferred-overwrite refusal). Unlike the RFC 4918 headers
    /// above, this is an strg extension — operators rely on it to triage why an overwrite
    /// COPY/MOVE was refused without spelunking server logs.
    /// </summary>
    public const string StrgReason = "Strg-Reason";
}
