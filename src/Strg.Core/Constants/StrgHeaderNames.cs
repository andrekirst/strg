namespace Strg.Core.Constants;

/// <summary>
/// Header names we reference from project code but which are absent from
/// <c>Microsoft.Net.Http.Headers.HeaderNames</c>. Prefer the framework constants; add
/// entries here only when the header is not exposed by ASP.NET Core's static list.
/// </summary>
public static class StrgHeaderNames
{
    /// <summary>
    /// De-facto standard hop-by-hop header for the original client IP when the request arrives
    /// via a reverse proxy. Not IANA-registered — see RFC 7239's <c>Forwarded</c> header for the
    /// standardized equivalent. We honour <c>X-Forwarded-For</c> because it remains the format
    /// nginx, traefik, and cloud load balancers emit out-of-the-box.
    /// </summary>
    public const string XForwardedFor = "X-Forwarded-For";

    /// <summary>
    /// <c>Referrer-Policy</c> response header (STRG-010). Controls how much of the origin
    /// URL is sent as the <c>Referer</c> in outbound navigations. Absent from
    /// <c>Microsoft.Net.Http.Headers.HeaderNames</c> as of ASP.NET Core 10.
    /// </summary>
    public const string ReferrerPolicy = "Referrer-Policy";

    /// <summary>
    /// <c>Permissions-Policy</c> response header (STRG-010). Successor to <c>Feature-Policy</c>;
    /// restricts which browser-feature APIs (geolocation, camera, microphone, …) the served
    /// document may invoke. Not exposed by <c>Microsoft.Net.Http.Headers.HeaderNames</c>.
    /// </summary>
    public const string PermissionsPolicy = "Permissions-Policy";
}
