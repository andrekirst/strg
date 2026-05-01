using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Net.Http.Headers;
using Strg.Integration.Tests.Auth;
using Xunit;

namespace Strg.Integration.Tests.Middleware;

/// <summary>
/// STRG-010 TC-005 / TC-006 + STRG-082 TC-001 / TC-002 / TC-003 / TC-004, plus the Security
/// Review Checklist pin for "rate limit before auth".
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

    /// <summary>
    /// STRG-082 TC-001 + AC1 + AC3 — bursts past the auth budget on <c>/connect/token</c> trip
    /// the named "auth" policy and produce 429s with a <c>Retry-After</c> header and a JSON
    /// error body. Runs with Auth=3 / Global=100000 so the auth limiter fires deterministically
    /// before the global one in an 8-request burst. The first three requests reach the
    /// OpenIddict token handler (returning 400 because the credentials are invalid — the
    /// limiter is BEFORE auth, so credential validity doesn't gate budget consumption); from
    /// the fourth onwards every response is a 429 with the rejection envelope.
    /// </summary>
    [Fact]
    public async Task Auth_burst_exceeds_auth_budget_and_returns_429_with_retry_after()
    {
        const int permitLimit = 3;
        const int burstSize = 8;
        using var rateFactory = CreateFactoryWithAuthLimit(permitLimit, windowSeconds: 60);
        using var client = rateFactory.CreateClient();

        var statusCodes = new List<HttpStatusCode>();
        HttpResponseMessage? lastRejection = null;
        for (var i = 0; i < burstSize; i++)
        {
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["username"] = StrgWebApplicationFactory.AdminEmail,
                ["password"] = "deliberately-wrong-password",
                ["client_id"] = StrgWebApplicationFactory.DefaultClientId,
                ["scope"] = StrgWebApplicationFactory.AdminScopes,
            });
            var response = await client.PostAsync("/connect/token", form);
            statusCodes.Add(response.StatusCode);
            if (response.StatusCode == HttpStatusCode.TooManyRequests && lastRejection is null)
            {
                lastRejection = response;
            }
            else
            {
                response.Dispose();
            }
        }

        statusCodes.Should().Contain(HttpStatusCode.TooManyRequests,
            $"sending {burstSize} token requests against an Auth={permitLimit}/min budget must overflow into 429");
        statusCodes.Take(permitLimit).Should().NotContain(HttpStatusCode.TooManyRequests,
            $"the first {permitLimit} requests within budget must reach the OpenIddict handler, not the limiter");

        try
        {
            lastRejection.Should().NotBeNull("at least one request must have produced a 429 response");
            lastRejection!.Headers.TryGetValues(HeaderNames.RetryAfter, out var retryValues).Should().BeTrue(
                "every 429 must carry a Retry-After header per the Security Review Checklist");
            var retryAfter = int.Parse(retryValues!.Single());
            retryAfter.Should().BeGreaterThan(0, "Retry-After must be a positive integer second-count");

            var body = await lastRejection.Content.ReadAsStringAsync();
            body.Should().Contain("\"error\"",
                "the rejection envelope is the {\"error\":\"...\"} JSON shape used by the rest of the API");
        }
        finally
        {
            lastRejection?.Dispose();
        }
    }

    /// <summary>
    /// STRG-082 TC-002 + AC2 — the global limiter rejects the request immediately following the
    /// configured budget. Uses a small, deterministic budget rather than literally 300/301 so
    /// the test runs sub-second; the boundary semantics are identical (N succeed-or-401, N+1th
    /// is 429). Auth budget is left at the factory default (100000) so the named auth policy
    /// can't shadow the global limit during the burst.
    /// </summary>
    [Fact]
    public async Task Global_burst_returns_429_immediately_after_configured_budget()
    {
        const int permitLimit = 5;
        const int burstSize = permitLimit + 3;
        using var rateFactory = CreateFactoryWithGlobalLimit(permitLimit, windowSeconds: 60);
        using var client = rateFactory.CreateClient();

        var statusCodes = new List<HttpStatusCode>();
        for (var i = 0; i < burstSize; i++)
        {
            using var response = await client.GetAsync("/api/v1/drives");
            statusCodes.Add(response.StatusCode);
        }

        statusCodes.Take(permitLimit).Should().NotContain(HttpStatusCode.TooManyRequests,
            $"the first {permitLimit} requests must reach the auth middleware (returning 401) — "
            + "any 429 inside the budget means the limiter is rejecting under-budget traffic");
        statusCodes.Skip(permitLimit).Should().Contain(HttpStatusCode.TooManyRequests,
            $"requests {permitLimit + 1}..{burstSize} exhaust the global budget and must produce at least one 429");
    }

    /// <summary>
    /// STRG-082 TC-003 + AC5 — <c>/health/live</c> is exempt from rate limiting. The K8s
    /// liveness probe must never be starved of budget by other traffic; a 429 here would mark
    /// the pod unhealthy and force a restart. Hammering 1000 times against a clamped budget
    /// (3 permits / 1s window) proves the <c>.DisableRateLimiting()</c> wiring on the endpoint
    /// keeps the limiter from ever firing on the probe.
    /// </summary>
    [Fact]
    public async Task Liveness_probe_is_never_rate_limited_under_sustained_burst()
    {
        const int requestCount = 1000;
        using var rateFactory = CreateFactoryWithGlobalLimit(permitLimit: 3, windowSeconds: 1);
        using var client = rateFactory.CreateClient();

        var statusCodes = new List<HttpStatusCode>();
        for (var i = 0; i < requestCount; i++)
        {
            using var response = await client.GetAsync("/health/live");
            statusCodes.Add(response.StatusCode);
        }

        statusCodes.Should().HaveCount(requestCount);
        statusCodes.Should().AllSatisfy(s => s.Should().Be(HttpStatusCode.OK),
            $"/health/live is mapped with .DisableRateLimiting() and must return 200 across all {requestCount} probes");
    }

    /// <summary>
    /// STRG-082 TC-004 + AC6 — <c>appsettings.json</c> values flow through to the limiter
    /// behavior. The same call shape with two different configured budgets must produce two
    /// different breaking points. Verifies that the configured permit limit is the one the
    /// limiter actually enforces (not a hardcoded constant), which is the audit thrust of the
    /// Code Review Checklist's "Limits are configurable (not hardcoded)" item.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(7)]
    public async Task Configured_global_limit_drives_observed_rejection_threshold(int permitLimit)
    {
        const int burstSize = 12;
        using var rateFactory = CreateFactoryWithGlobalLimit(permitLimit, windowSeconds: 60);
        using var client = rateFactory.CreateClient();

        var rejectedAtIndex = -1;
        for (var i = 0; i < burstSize; i++)
        {
            using var response = await client.GetAsync("/api/v1/drives");
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                rejectedAtIndex = i;
                break;
            }
        }

        rejectedAtIndex.Should().BeGreaterOrEqualTo(permitLimit,
            $"the first 429 must appear at request index >= {permitLimit} (zero-based) when PermitLimit={permitLimit}");
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

    private WebApplicationFactory<Program> CreateFactoryWithAuthLimit(int permitLimit, int windowSeconds) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["RateLimiting:Auth:PermitLimit"] = permitLimit.ToString(),
                    ["RateLimiting:Auth:WindowSeconds"] = windowSeconds.ToString(),
                    ["RateLimiting:Auth:QueueLimit"] = "0",
                });
            });
        });
}
