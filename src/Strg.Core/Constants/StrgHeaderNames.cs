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
}
