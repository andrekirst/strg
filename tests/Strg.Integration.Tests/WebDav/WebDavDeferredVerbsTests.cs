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
/// STRG-074 gap-fill — pins the 501 Not Implemented response on WebDAV write verbs deliberately
/// deferred to STRG-071 (DELETE, COPY, MOVE, PROPPATCH). The STRG-067 middleware comment at
/// <c>StrgWebDavMiddleware.cs:270</c> and the <c>OPTIONS</c> <c>Allow</c> header both advertise
/// these verbs, but the dispatch tail intentionally short-circuits with 501 until each handler
/// ships under STRG-071.
///
/// <para><b>Why a dedicated pin.</b> Three drift shapes this test defends against:
/// <list type="bullet">
///   <item><description><b>Silent flip to 200/201/204.</b> When STRG-071 lands a real DELETE/COPY/
///     MOVE handler, this test fails loudly — forcing an explicit decision to delete the pin
///     alongside the new handler, rather than the pin silently turning vacuous as the status code
///     changes. That's the signal the v0.1 → v0.1+ boundary needs.</description></item>
///   <item><description><b>Silent flip to 405 Method Not Allowed.</b> A well-meaning refactor that
///     "cleans up the unhandled-verb tail" by switching 501 → 405 would be wrong:
///     <c>Allow: … DELETE, COPY, MOVE, PROPPATCH …</c> still advertises these methods, so a 405
///     contradicts the OPTIONS surface. 501 is the honest "server understands the verb but hasn't
///     implemented it" status per RFC 7231 §6.6.2.</description></item>
///   <item><description><b>Silent flip to 500.</b> If a future dispatch reorder lets one of these
///     verbs reach a handler that throws before completing (e.g., <c>NotImplementedException</c>
///     bubbling through a generic exception filter), the status would flip to 500 and the
///     middleware's explicit deferral would be bypassed — operators would see "broken" instead of
///     "not yet implemented."</description></item>
/// </list></para>
///
/// <para><b>Dispatch-order artefact — MKCOL is intentionally omitted.</b> The middleware's
/// <c>store.GetItemAsync</c> null-check at line 234 short-circuits with 404 <i>before</i> the
/// deferral tail at line 272. MKCOL inherently targets a non-existing URL (RFC 4918 §9.3 is
/// exactly "create a new collection at this URL"), so in the current dispatch ordering MKCOL
/// always returns 404, never 501. That's a known artefact tracked separately for STRG-071 —
/// pinning it here would embed the 404 behaviour as if it were intentional, and a future dispatch
/// reorder that moves MKCOL before the GetItem check would make this test fail for the wrong
/// reason. Each verb covered below DOES reach the 501 tail because we pre-seed a file via PUT so
/// GetItemAsync returns non-null.</para>
///
/// <para><b>Why this suite, not an extension of <see cref="WebDavMiddlewareTests"/>.</b> The
/// existing middleware suite pins drive resolution + auth (TC-001..TC-005). Conflating those with
/// the deferred-verb shape means a regression in either direction produces a single confusing
/// "something in WebDavMiddlewareTests broke" signal. Separate classes → separate diagnosis.</para>
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
    public async Task DELETE_on_existing_file_returns_501_NotImplemented_until_STRG071_lands()
    {
        var client = await CreateAuthenticatedClientAsync();

        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/dav/{DriveName}/{SeededFilePath}");
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotImplemented,
            because: "STRG-067 middleware line ~272 explicitly defers DELETE to STRG-071 — a silent " +
                     "flip to 200/204/405/500 would mean the handler shipped (or regressed) without " +
                     "the operator-visible deferral signal this pin enforces");
    }

    [Fact]
    public async Task COPY_on_existing_file_returns_501_NotImplemented_until_STRG071_lands()
    {
        var client = await CreateAuthenticatedClientAsync();

        using var request = new HttpRequestMessage(new HttpMethod("COPY"), $"/dav/{DriveName}/{SeededFilePath}");
        // RFC 4918 §9.8 requires a Destination header on COPY; we include it so a future handler
        // that validates shape-before-dispatch doesn't 400 out before reaching the deferral tail.
        // Under the current middleware the header is ignored — the 501 pin holds regardless — but
        // the defensive shape keeps this test meaningful after STRG-071 lands partial validation.
        request.Headers.Add("Destination", $"/dav/{DriveName}/{SeededFilePath}.copy");
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotImplemented,
            because: "COPY dispatch is deferred to STRG-071; 501 is the honest status while the verb " +
                     "is advertised in OPTIONS Allow but has no handler wired");
    }

    [Fact]
    public async Task MOVE_on_existing_file_returns_501_NotImplemented_until_STRG071_lands()
    {
        var client = await CreateAuthenticatedClientAsync();

        using var request = new HttpRequestMessage(new HttpMethod("MOVE"), $"/dav/{DriveName}/{SeededFilePath}");
        request.Headers.Add("Destination", $"/dav/{DriveName}/{SeededFilePath}.moved");
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotImplemented,
            because: "MOVE dispatch is deferred to STRG-071; pinning 501 here catches a silent " +
                     "partial-implementation slip (e.g., a PR that wires MOVE but forgets to remove " +
                     "this pin — the failing test is the enforcement signal)");
    }

    [Fact]
    public async Task PROPPATCH_on_existing_file_returns_501_NotImplemented_until_STRG071_lands()
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
            because: "PROPPATCH is also deferred to STRG-071; the watch-tracker task #135 separately " +
                     "pins future auth + scope + strg:* allowlist invariants on the real handler, " +
                     "but today the honest status is 501");
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
        services.AddDbContext<StrgDbContext>(opts => opts.UseNpgsql(factory.ConnectionString).UseOpenIddict());
        return services.BuildServiceProvider();
    }

    private sealed class FixtureTenantContext(Guid tenantId) : ITenantContext
    {
        public Guid TenantId { get; } = tenantId;
    }
}
