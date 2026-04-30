using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Strg.Core.Domain;
using Strg.Infrastructure.Data;
using Strg.Integration.Tests.Auth;
using Xunit;

namespace Strg.Integration.Tests.WebDav;

/// <summary>
/// Status pin for the WebDAV write verb that still returns 501 Not Implemented — PROPPATCH.
/// The middleware's OPTIONS <c>Allow</c> header advertises PROPPATCH, but the dispatch tail
/// short-circuits with 501 until a handler is wired.
///
/// <para>The pin defends against three silent regressions: (a) a flip to 200/201/204/207 when a
/// real handler ships without removing this test, (b) a flip to 405 from a "cleanup" of the
/// unhandled-verb tail (would contradict the OPTIONS Allow header — 501 is the honest "verb
/// understood, not implemented" status per RFC 7231 §6.6.2), (c) a flip to 500 from a dispatch
/// reorder that lets PROPPATCH reach a throwing handler.</para>
/// </summary>
public sealed class WebDavDeferredVerbsTests(StrgWebApplicationFactory factory)
    : IClassFixture<StrgWebApplicationFactory>, IAsyncLifetime
{
    private const string DriveName = "deferred-verbs-test-drive";
    private const string SeededFilePath = "seeded.txt";

    private string _rootPath = string.Empty;

    async Task IAsyncLifetime.InitializeAsync()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), $"strg-webdav-deferred-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_rootPath);
        await EnsureDriveAsync();
        await SeedFileAsync();
    }

    Task IAsyncLifetime.DisposeAsync()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task PROPPATCH_on_existing_file_returns_501_NotImplemented_until_handler_lands()
    {
        var client = await CreateAuthenticatedClientAsync();

        // Minimal well-formed PROPPATCH body so a future handler that XML-validates before dispatch
        // doesn't 400 out. RFC 4918 §9.2 — removes a nonexistent custom dead property, which a real
        // handler would either 207-multistatus or 424 on; the pin here is strictly about 501.
        const string body =
            """
            <?xml version="1.0" encoding="utf-8" ?>
            <D:propertyupdate xmlns:D="DAV:" xmlns:Z="http://example.com/ns/">
              <D:remove><D:prop><Z:author/></D:prop></D:remove>
            </D:propertyupdate>
            """;
        using var request = new HttpRequestMessage(new HttpMethod("PROPPATCH"), $"/dav/{DriveName}/{SeededFilePath}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/xml"),
        };
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotImplemented,
            because: "PROPPATCH has no handler wired yet; 501 is the honest 'verb understood, " +
                     "not implemented' status. A real handler will pin its own auth + scope + " +
                     "strg:* allowlist invariants when it ships.");
    }

    // ---- helpers ----

    private async Task<HttpClient> CreateAuthenticatedClientAsync()
    {
        using var tokenResponse = await factory.PostTokenAsync(
            StrgWebApplicationFactory.AdminEmail,
            StrgWebApplicationFactory.AdminPassword);
        tokenResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var (accessToken, _) = await StrgWebApplicationFactory.ReadTokensAsync(tokenResponse);
        return factory.CreateAuthenticatedClient(accessToken);
    }

    private async Task EnsureDriveAsync()
    {
        await using var sp = BuildScopedDb();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();

        var providerConfig = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["rootPath"] = _rootPath,
        });

        var existing = await db.Drives.FirstOrDefaultAsync(d => d.Name == DriveName);
        if (existing is not null)
        {
            existing.ProviderConfig = providerConfig;
            await db.SaveChangesAsync();
            return;
        }

        db.Drives.Add(new Drive
        {
            TenantId = factory.AdminTenantId,
            Name = DriveName,
            ProviderType = "local",
            ProviderConfig = providerConfig,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedFileAsync()
    {
        // Seed via the real PUT handler so FileItem + FileVersion land with the schema the middleware
        // reads back via store.GetItemAsync. Writing directly to the DB would bypass the hashing +
        // quota path and risk drift with what the production code expects to see at GetItemAsync time.
        var client = await CreateAuthenticatedClientAsync();
        using var put = new HttpRequestMessage(HttpMethod.Put, $"/dav/{DriveName}/{SeededFilePath}")
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("seeded-for-deferred-verb-pin")),
        };
        using var response = await client.SendAsync(put);
        // 201 on first seed of the class fixture, 204 on subsequent test-instance setups (xUnit
        // reinstantiates the test class per [Fact]; the FileItem row from the previous instance
        // survives in the shared DB and makes the second PUT an overwrite). Either shape satisfies
        // the "file exists at SeededFilePath" precondition the deferred-verb tests depend on.
        response.StatusCode.Should().BeOneOf(
            new[] { HttpStatusCode.Created, HttpStatusCode.NoContent },
            because: "the deferred-verb tests rely on GetItemAsync returning non-null so dispatch " +
                     "reaches the 501 tail — a failing seed would cause every verb test to 404 and " +
                     "silently vacate the pin; 201 (new) and 204 (overwrite) both satisfy the " +
                     "precondition");
    }

    private ServiceProvider BuildScopedDb()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext>(new FixtureTenantContext(factory.AdminTenantId));
        // StrgDbContext ctor depends on ICurrentUser; this throw-away DI
        // container needs an explicit registration since it doesn't share the factory's
        // ServiceProvider. Mirrors StrgWebApplicationFactory.BootstrapSchemaAndSeedAsync.
        services.AddSingleton<ICurrentUser>(new FixtureCurrentUser(factory.AdminUserId));
        services.AddDbContext<StrgDbContext>(opts => opts.UseNpgsql(factory.ConnectionString).UseOpenIddict());
        return services.BuildServiceProvider();
    }

    private sealed class FixtureTenantContext(Guid tenantId) : ITenantContext
    {
        public Guid TenantId { get; } = tenantId;
    }

    private sealed class FixtureCurrentUser(Guid userId) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
    }
}
