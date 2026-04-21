using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Strg.Integration.Tests.Auth;
using Xunit;

namespace Strg.Integration.Tests.Drives;

/// <summary>
/// REST-layer pin for the STRG-043 L3 oversized-<c>ProviderConfig</c> guard at
/// <c>DriveEndpoints.CreateDrive</c> lines 74-79. The GraphQL surface already pins the same
/// behaviour in <c>DriveMutationsTests.CreateDrive_ProviderConfigOver8192Chars_ReturnsValidationError</c>;
/// this closes the REST side.
///
/// <para><b>Regression this catches.</b> A future consolidation of the length-cap check into a
/// shared validator that only runs on the GraphQL input-type silently loses the REST gate. The
/// DB <c>varchar(8192)</c> backstop would then surface as an Npgsql 22001 exception that maps
/// through the problem-details middleware to a 500 — admins see an opaque server error instead
/// of a structured 422. No data loss, but admin tooling loses the ability to distinguish
/// "your JSON is too long" from "the server is broken".</para>
/// </summary>
public sealed class DriveEndpointsTests(StrgWebApplicationFactory factory)
    : IClassFixture<StrgWebApplicationFactory>
{
    [Fact]
    public async Task CreateDrive_ProviderConfigOver8192Chars_returns_422_with_length_cap_message()
    {
        var tokenResponse = await factory.PostTokenAsync(
            StrgWebApplicationFactory.AdminEmail,
            StrgWebApplicationFactory.AdminPassword);
        tokenResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            "the pre-seeded admin token must succeed before the endpoint can be exercised");
        var (accessToken, _) = await StrgWebApplicationFactory.ReadTokensAsync(tokenResponse);

        using var client = factory.CreateAuthenticatedClient(accessToken);

        // 8193 x's — one byte over the service-layer guard (and the DB varchar(8192) backstop).
        // Name + providerType are deliberately valid so the length check is the one that fires;
        // the endpoint short-circuits on name-regex and provider-registration mismatches first,
        // so an invalid choice for either would mask the assertion target.
        var request = new
        {
            name = "rest-oversize-test",
            providerType = "local",
            providerConfigJson = new string('x', 8193),
            encryptionEnabled = false,
        };

        using var response = await client.PostAsJsonAsync("/api/v1/drives/", request);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        // Match against the literal string DriveEndpoints emits — if the message is reworded,
        // this assertion fails with a clear diff rather than hiding behind a loose Contains check.
        body.GetProperty("error").GetString()
            .Should().Be("ProviderConfig JSON cannot exceed 8192 characters");
    }
}
