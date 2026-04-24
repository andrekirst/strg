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
        // After the Phase-2 migration to CQRS, the 8192-char check lives in CreateDriveValidator
        // (FluentValidation) which emits "<PropertyName>: <message>". A Contains check keeps the
        // test focused on the user-visible assertion (the 8192-char guard still fires with a 422)
        // without coupling to ValidationBehavior's internal formatting.
        body.GetProperty("error").GetString()
            .Should().Contain("8192 characters");
    }

    [Fact]
    public async Task CreateDrive_LeadingDashName_returns_422()
    {
        // Phase-2 regex tightening: the unified name rule is ^[a-z0-9][a-z0-9-]{0,63}$ (must
        // start with an alphanumeric). The pre-migration REST endpoint accepted "-foo" via the
        // looser ^[a-z0-9\-]{1,64}$ pattern — this test pins the stricter behavior so a relapse
        // to the old regex fails the build. Leading-dash names surface as `-rm -rf /` lookalikes
        // in shell-driven admin tooling and as URL-escaping surprises; rejecting them removes
        // an entire class of minor operational papercuts.
        var tokenResponse = await factory.PostTokenAsync(
            StrgWebApplicationFactory.AdminEmail,
            StrgWebApplicationFactory.AdminPassword);
        var (accessToken, _) = await StrgWebApplicationFactory.ReadTokensAsync(tokenResponse);
        using var client = factory.CreateAuthenticatedClient(accessToken);

        using var response = await client.PostAsJsonAsync("/api/v1/drives/", new
        {
            name = "-leading-dash",
            providerType = "local",
            encryptionEnabled = false,
        });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Contain("alphanumeric");
    }

    [Fact]
    public async Task CreateDrive_name_of_soft_deleted_drive_is_rejected_with_409()
    {
        // Phase-2 uniqueness contract: a drive's name stays reserved across soft-delete so an
        // operator recreating the name can't silently clobber audit trails that reference drives
        // by name. CreateDriveHandler's uniqueness check uses IgnoreQueryFilters to span deleted
        // rows (the one legitimate call site in Strg.Application, allow-listed by
        // ApplicationDoesNotBypassTenantFiltersTests).
        var tokenResponse = await factory.PostTokenAsync(
            StrgWebApplicationFactory.AdminEmail,
            StrgWebApplicationFactory.AdminPassword);
        var (accessToken, _) = await StrgWebApplicationFactory.ReadTokensAsync(tokenResponse);
        using var client = factory.CreateAuthenticatedClient(accessToken);

        var name = $"reuse-test-{Guid.NewGuid():N}"[..32];
        var create = await client.PostAsJsonAsync("/api/v1/drives/", new
        {
            name,
            providerType = "local",
            encryptionEnabled = false,
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created);

        var body = await create.Content.ReadFromJsonAsync<JsonElement>();
        var driveId = body.GetProperty("id").GetGuid();

        var delete = await client.DeleteAsync($"/api/v1/drives/{driveId}");
        delete.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Recreate with the same name: must return 409 Conflict, NOT 201 Created.
        var recreate = await client.PostAsJsonAsync("/api/v1/drives/", new
        {
            name,
            providerType = "local",
            encryptionEnabled = false,
        });
        recreate.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "soft-deleted names remain reserved — CreateDriveHandler.IgnoreQueryFilters pins this");
    }
}
