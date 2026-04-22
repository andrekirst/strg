using System.Net;
using System.Net.Http;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Strg.Core.Domain;
using Strg.Infrastructure.Data;
using Strg.Integration.Tests.Auth;
using Xunit;

namespace Strg.Integration.Tests.WebDav;

/// <summary>
/// STRG-067 — WebDAV middleware + route registration acceptance tests (TC-001..TC-005).
///
/// <para>Uses the same <see cref="StrgWebApplicationFactory"/> that the auth suite relies on so
/// the host boots with real OpenIddict + real Postgres — the WebDAV auth check path is real,
/// not mocked. A single seeded drive (<c>test-drive</c>) is enough for the happy-path assertions;
/// the failure-path tests deliberately probe names the fixture has NOT seeded.</para>
/// </summary>
public sealed class WebDavMiddlewareTests(StrgWebApplicationFactory factory) : IClassFixture<StrgWebApplicationFactory>
{
    private const string SeededDriveName = "test-drive";

    [Fact]
    public async Task TC001_options_returns_200_with_dav_class_1_and_2_header()
    {
        await EnsureSeededDriveAsync();
        var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Options, $"/dav/{SeededDriveName}/");
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.TryGetValues("DAV", out var davValues).Should().BeTrue("RFC 4918 §10.1 requires the DAV response header on OPTIONS");
        string.Join(",", davValues!).Should().Contain("1").And.Contain("2",
            because: "class 1 is base WebDAV; class 2 is locks (STRG-070)");
    }

    [Fact]
    public async Task TC002_graphql_route_is_not_intercepted_by_webdav_middleware()
    {
        var client = factory.CreateClient();

        // Anonymous GET on /graphql would be a Banana Cake Pop / introspection landing in dev.
        // The assertion is negative: whatever the response, it's NOT the 501 Not Implemented that
        // the WebDAV middleware returns for non-OPTIONS verbs on an unknown drive, i.e. the
        // /dav branch did not intercept this request.
        var response = await client.GetAsync("/graphql");

        response.StatusCode.Should().NotBe(HttpStatusCode.NotImplemented,
            because: "the WebDAV middleware must only run under /dav; /graphql stays on its own pipeline");
    }

    [Fact]
    public async Task TC003_propfind_on_unknown_drive_returns_404()
    {
        var accessToken = await AcquireAccessTokenAsync();
        var client = factory.CreateAuthenticatedClient(accessToken);

        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), "/dav/nonexistent-drive/");
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            because: "drive resolution must fail closed — unknown driveName is indistinguishable from not-authorised, and returning 404 matches the STRG-067 acceptance criterion");
    }

    [Fact]
    public async Task TC004_get_without_bearer_token_returns_401()
    {
        await EnsureSeededDriveAsync();
        var client = factory.CreateClient();

        var response = await client.GetAsync($"/dav/{SeededDriveName}/");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            because: "the WebDAV branch re-runs UseAuthentication/UseAuthorization; an anonymous GET on any non-OPTIONS verb must 401 before drive resolution");
    }

    [Fact]
    public async Task TC005_map_prefix_captures_nested_paths_for_options()
    {
        await EnsureSeededDriveAsync();
        var client = factory.CreateClient();

        // OPTIONS on a nested collection path still hits the WebDAV middleware — proves the
        // app.Map("/dav", ...) branching captures everything under the prefix, not just the
        // drive-root. Without the prefix capture, /dav/test-drive/folder/sub/ would fall through
        // to the default pipeline and return 404 (or worse, route to an unrelated endpoint).
        using var request = new HttpRequestMessage(HttpMethod.Options, $"/dav/{SeededDriveName}/folder/sub/file.txt");
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Contains("DAV").Should().BeTrue();
    }

    private async Task<string> AcquireAccessTokenAsync()
    {
        using var tokenResponse = await factory.PostTokenAsync(
            StrgWebApplicationFactory.AdminEmail,
            StrgWebApplicationFactory.AdminPassword);
        tokenResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var (accessToken, _) = await StrgWebApplicationFactory.ReadTokensAsync(tokenResponse);
        return accessToken;
    }

    private async Task EnsureSeededDriveAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext>(new FixtureTenantContext(factory.AdminTenantId));
        services.AddDbContext<StrgDbContext>(opts => opts.UseNpgsql(factory.ConnectionString).UseOpenIddict());
        await using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();

        var existing = await db.Drives.FirstOrDefaultAsync(d => d.Name == SeededDriveName);
        if (existing is not null)
        {
            return;
        }

        db.Drives.Add(new Drive
        {
            TenantId = factory.AdminTenantId,
            Name = SeededDriveName,
            ProviderType = "local",
        });
        await db.SaveChangesAsync();
    }

    private sealed class FixtureTenantContext(Guid tenantId) : ITenantContext
    {
        public Guid TenantId { get; } = tenantId;
    }
}
