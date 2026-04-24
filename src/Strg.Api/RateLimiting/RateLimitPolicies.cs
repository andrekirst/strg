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
}
