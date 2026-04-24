using System.Net;
using FluentAssertions;
using Strg.Integration.Tests.Auth;
using Xunit;

namespace Strg.Integration.Tests.Middleware;

/// <summary>
/// STRG-010 TC-007 — a request to a protected endpoint without an <c>Authorization</c> header
/// returns 401. Exercises the end-to-end authentication middleware placement in
/// <c>Program.cs</c> (UseAuthentication / UseAuthorization after the rate limiter, with
/// <c>FallbackPolicy = RequireAuthenticatedUser</c>).
/// </summary>
public sealed class UnauthenticatedReturns401Tests(StrgWebApplicationFactory factory) : IClassFixture<StrgWebApplicationFactory>
{
    [Fact]
    public async Task Get_protected_endpoint_without_token_returns_401()
    {
        using var client = factory.CreateClient();

        // Explicit pin: no bearer, no cookie, no API key. The endpoint /api/v1/drives is mapped
        // under MapGroup("/api/v1/drives").RequireAuthorization(), and Program.cs sets the
        // global FallbackPolicy to RequireAuthenticatedUser — either mechanism alone would
        // reject; the layered contract is that BOTH produce 401 for anonymous callers.
        client.DefaultRequestHeaders.Authorization.Should().BeNull();

        using var response = await client.GetAsync("/api/v1/drives");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
