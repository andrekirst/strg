using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Strg.Integration.Tests.Auth;
using Xunit;

namespace Strg.Integration.Tests.Middleware;

/// <summary>
/// STRG-010 TC-005 / TC-006 and the Security Review Checklist pin for "rate limit before auth".
///
/// <para>
/// All tests use a per-test <see cref="WebApplicationFactory{TEntryPoint}.WithWebHostBuilder"/>
/// that clamps the global rate-limit budget to 3 requests per 1-second window so assertions
/// are deterministic within a test method. Per-test factories (rather than a class-shared
/// one) keep the rate-limiter state fresh across tests — a fixed-window limiter carries
/// state across request boundaries and a shared factory would couple test outcomes through
/// that state.
/// </para>
/// </summary>
public sealed class RateLimitingTests(StrgWebApplicationFactory factory) : IClassFixture<StrgWebApplicationFactory>
{
    /// <summary>
    /// TC-005 + AC6 — sending more requests than the global budget trips the limiter and
    /// produces 429 responses. The assertion is "at least one 429 appears in a rapid-fire
    /// burst" (not "the exact 4th request is 429") to stay robust against partitioning order,
    /// background hosted-service traffic, and test-runner scheduling jitter.
    /// </summary>
    [Fact]
    public async Task Rapid_requests_exceed_global_budget_and_return_429()
    {
        using var rateFactory = CreateFactoryWithGlobalLimit(permitLimit: 3, windowSeconds: 1);
        using var client = rateFactory.CreateClient();

        var tokenResponse = await factory.PostTokenAsync(
            StrgWebApplicationFactory.AdminEmail,
            StrgWebApplicationFactory.AdminPassword);
        tokenResponse.EnsureSuccessStatusCode();
        var (accessToken, _) = await StrgWebApplicationFactory.ReadTokensAsync(tokenResponse);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var statusCodes = new List<HttpStatusCode>();
        for (var i = 0; i < 20; i++)
        {
            using var response = await client.GetAsync("/api/v1/drives");
            statusCodes.Add(response.StatusCode);
        }

        statusCodes.Should().Contain(HttpStatusCode.TooManyRequests,
            "sending 20 rapid requests against a 3-permit/1s global limiter must overflow and produce at least one 429");
    }

    /// <summary>
    /// TC-006 + AC8/AC9 — the health-check and metrics endpoints are exempt from the global
    /// limiter because they chain <c>.DisableRateLimiting()</c> on their endpoint mappings.
    /// Even with a clamped budget, these endpoints must return 200 on every request — K8s
    /// probes burst at a steady cadence and must never be starved of budget by other traffic.
    /// </summary>
    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    [InlineData("/metrics")]
    public async Task Exempt_endpoints_bypass_rate_limiter(string path)
    {
        using var rateFactory = CreateFactoryWithGlobalLimit(permitLimit: 3, windowSeconds: 1);
        using var client = rateFactory.CreateClient();

        var statusCodes = new List<HttpStatusCode>();
        for (var i = 0; i < 15; i++)
        {
            using var response = await client.GetAsync(path);
            statusCodes.Add(response.StatusCode);
        }

        statusCodes.Should().AllSatisfy(s => s.Should().NotBe(HttpStatusCode.TooManyRequests),
            $"{path} is mapped with .DisableRateLimiting() and must never return 429");
        statusCodes.Should().OnlyContain(s => s == HttpStatusCode.OK,
            $"{path} must return 200 on every probe regardless of global budget state");
    }

    /// <summary>
    /// Security Review Checklist pin: "Middleware order is verified: rate limit before auth".
    /// Sending unauthenticated requests against a protected endpoint produces 401 for as long
    /// as the limiter allows the request to reach <c>UseAuthentication</c>, and 429 once the
    /// budget is exhausted. A pipeline that placed rate-limiting AFTER auth would produce
    /// exclusively 401 (auth rejects every request; the limiter never fires) — the presence
    /// of at least one 429 response in the sequence therefore proves the ordering invariant.
    /// </summary>
    [Fact]
    public async Task Unauthenticated_requests_hit_rate_limit_not_just_401()
    {
        using var rateFactory = CreateFactoryWithGlobalLimit(permitLimit: 3, windowSeconds: 1);
        using var client = rateFactory.CreateClient();

        client.DefaultRequestHeaders.Authorization.Should().BeNull();

        var statusCodes = new List<HttpStatusCode>();
        for (var i = 0; i < 20; i++)
        {
            using var response = await client.GetAsync("/api/v1/drives");
            statusCodes.Add(response.StatusCode);
        }

        statusCodes.Should().Contain(HttpStatusCode.Unauthorized,
            "the first N requests within budget reach the authentication middleware and return 401 — "
            + "if no 401s appear at all the limiter is incorrectly rejecting all traffic");
        statusCodes.Should().Contain(HttpStatusCode.TooManyRequests,
            "once the budget is exhausted the limiter rejects with 429 before auth runs — "
            + "if no 429s appear, rate limiting is wired AFTER authentication which breaks the "
            + "security-review ordering invariant (auth bypass via rate-limit exploit)");
    }

    private WebApplicationFactory<Program> CreateFactoryWithGlobalLimit(int permitLimit, int windowSeconds) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["RateLimiting:Global:PermitLimit"] = permitLimit.ToString(),
                    ["RateLimiting:Global:WindowSeconds"] = windowSeconds.ToString(),
                    ["RateLimiting:Global:QueueLimit"] = "0",
                });
            });
        });
}
