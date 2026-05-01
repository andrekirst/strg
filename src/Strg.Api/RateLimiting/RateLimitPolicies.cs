namespace Strg.Api.RateLimiting;

/// <summary>
/// Named rate-limit policies for STRG-010. Each constant pairs with the matching subsection
/// under <c>RateLimiting:{PolicyName}</c> bound by <see cref="RateLimitOptions"/>.
/// </summary>
internal static class RateLimitPolicies
{
    /// <summary>
    /// Credential-exchange policy (<c>/connect/token</c>). Tighter than the global limiter —
    /// legitimate clients rarely issue more than a handful of token requests per minute, so a
    /// low per-window cap is cheap to overshoot credential-stuffing / password-spraying.
    /// Bound from <c>RateLimiting:Auth</c>.
    /// </summary>
    public const string Auth = "auth";

    /// <summary>
    /// Per-user upload policy. Partitioned on the JWT <c>sub</c> claim with an IP fallback so
    /// unauthenticated probes can't pool a user's budget. v0.1 does NOT attach this policy to
    /// TUS — chunk uploads are explicitly exempted (STRG-082) because they are high-volume by
    /// nature and not a brute-force target. Registered for future chunked-upload endpoints
    /// that opt in via <c>RequireRateLimiting</c>. Bound from <c>RateLimiting:Upload</c>.
    /// </summary>
    public const string Upload = "upload";
}
