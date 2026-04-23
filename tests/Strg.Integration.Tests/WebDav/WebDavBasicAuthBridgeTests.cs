using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Net.Http.Headers;
using Strg.Core.Domain;
using Strg.Infrastructure.Data;
using Strg.Integration.Tests.Auth;
using Strg.WebDav;
using Xunit;

namespace Strg.Integration.Tests.WebDav;

/// <summary>
/// STRG-074 gap-fill — pins the <b>integration-level</b> Basic Auth → JWT bridge contract at
/// the live <c>/dav</c> surface. The AC line reads "Basic Auth → JWT bridge tested (correct
/// and wrong credentials)"; this closes the gap that the STRG-073 unit tests leave open.
///
/// <para><b>Why this is not redundant with <c>BasicAuthJwtBridgeMiddlewareTests</c>.</b> The
/// STRG-073 unit tests in <c>Strg.Api.Tests</c> drive the middleware directly with a stubbed
/// <see cref="IHttpClientFactory"/> and a stub token handler that returns hard-coded JSON.
/// They pin the middleware's own branching but cannot catch these integration-level drift
/// shapes:
/// <list type="bullet">
///   <item><description><b>Pipeline-order drift.</b> If a refactor moves
///     <c>UseMiddleware&lt;BasicAuthJwtBridgeMiddleware&gt;()</c> out of the
///     <c>app.Map("/dav", …)</c> branch (or reorders it past the branch's
///     <c>UseAuthentication</c>), the unit tests still pass but real Basic Auth requests 401
///     because the outer pipeline's Bearer-only authentication rejects them before the bridge
///     can rewrite the header. Only a live-host test catches this.</description></item>
///   <item><description><b>Client-secret / scope config drift.</b> The bridge has to present
///     credentials OpenIddict accepts: correct grant type (password), correct client_id
///     (<c>strg-default</c>), correct scope set. A config typo that makes /connect/token
///     respond with <c>invalid_client</c> would pass the stubbed unit tests but fail here
///     against the real OpenIddict endpoint.</description></item>
///   <item><description><b>Password-grant pathway drift.</b> OpenIddict's
///     <c>AuthorizationController</c> calls <c>UserManager.AuthenticatePassword</c> which
///     hashes the incoming password via <c>Pbkdf2PasswordHasher</c> and compares against the
///     stored hash. A regression in the hasher's Verify path (salt-handling, constant-time
///     comparator, PBKDF2 params) would pass all unit tests individually but surface here as
///     "correct password, still 401".</description></item>
/// </list>
/// Taken together: the unit suite pins the middleware's internal contract in isolation; this
/// suite pins the whole chain <c>Basic header → pipeline routing → real token endpoint →
/// real password hasher → JWT → WebDAV dispatch</c> — the surface an actual WebDAV client
/// (Windows Explorer, Finder, DAVx5) exercises.</para>
///
/// <para><b>TestServer handler override — why this test builds its own factory.</b> The
/// production <c>"oidc"</c> named HttpClient in <see cref="WebDavServiceExtensions.AddStrgWebDav"/>
/// resolves its <see cref="HttpClient.BaseAddress"/> from <c>IServer.Features.Get&lt;IServerAddressesFeature&gt;()</c>
/// — a deliberate STRG-073 fold-in against config-bound credential-exfiltration
/// (a config-driven base address would let a deploy-time typo redirect every WebDAV user's
/// cleartext password to an attacker-controlled URL). <see cref="TestServer"/> registers the
/// feature but its <c>Addresses</c> collection is empty, so the production callback throws
/// <see cref="InvalidOperationException"/> and the request 500s.
///
/// <para>The fix used here: rebuild the named client in <see cref="IWebHostBuilder.ConfigureServices"/>
/// with a <see cref="HttpClientFactoryOptions.HttpClientActions"/> list cleared and repopulated
/// with a loopback BaseAddress, and a primary handler that routes through
/// <see cref="TestServer.CreateHandler"/>. This exercises the SAME middleware and the SAME
/// /connect/token endpoint via the in-memory pipeline — the only thing swapped is the transport
/// the bridge uses to reach it. The "don't trust config for BaseAddress" production invariant
/// is preserved because we're not reading from config either — we're injecting the TestServer
/// handler directly.</para>
/// </summary>
public sealed class WebDavBasicAuthBridgeTests(StrgWebApplicationFactory factory)
    : IClassFixture<StrgWebApplicationFactory>, IAsyncLifetime
{
    private const string DriveName = "basic-auth-bridge-test-drive";

    private string _rootPath = string.Empty;
    private WebApplicationFactory<Program>? _bridgeFactory;

    async Task IAsyncLifetime.InitializeAsync()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), $"strg-basicauth-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_rootPath);
        await EnsureDriveAsync();

        _bridgeFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                // Step 1 — wipe the production HttpClientActions for the "oidc" named client.
                // The production callback reads IServerAddressesFeature.Addresses and throws when
                // the list is empty (STRG-073 fold-in #2, guards against credential exfiltration
                // via misconfigured BaseAddress). TestServer registers the feature but doesn't
                // populate it, so the production callback throws → 500.
                //
                // Clearing in a Configure<Options> delegate registered AFTER AddStrgWebDav runs is
                // deterministic: HttpClientFactoryOptions.HttpClientActions is a List<Action>
                // applied in registration order at HttpClient resolve-time, and Configure
                // delegates run in registration order too. So this Clear executes on the options
                // instance BEFORE the list is walked to configure the client.
                services.Configure<HttpClientFactoryOptions>(
                    BasicAuthJwtBridgeMiddleware.OidcHttpClientName,
                    options =>
                    {
                        options.HttpClientActions.Clear();
                        options.HttpClientActions.Add(client =>
                        {
                            // Any valid absolute URI works — the primary handler below rewrites
                            // the transport to TestServer's in-memory pipeline, so what matters
                            // is that HttpClient can construct the target URL. Loopback is the
                            // honest choice because that's conceptually where we're talking to.
                            client.BaseAddress = new Uri("http://localhost");
                        });
                    });

                // Step 2 — replace the primary handler with TestServer's in-memory handler.
                // This is the piece that makes the request actually reach /connect/token in the
                // same process: TestServer.CreateHandler() returns an HttpMessageHandler that
                // dispatches requests directly into the ASP.NET Core pipeline without a socket.
                //
                // sp.GetRequiredService<IServer>() returns the TestServer instance in WAF hosts
                // (the production IServer is Kestrel; TestServer replaces it). The callback fires
                // at HttpClient resolve-time, after the server is built, so the cast is safe.
                services.AddHttpClient(BasicAuthJwtBridgeMiddleware.OidcHttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(sp =>
                    {
                        var testServer = (TestServer)sp.GetRequiredService<IServer>();
                        return testServer.CreateHandler();
                    });
            });
        });
    }

    Task IAsyncLifetime.DisposeAsync()
    {
        _bridgeFactory?.Dispose();
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Correct_basic_credentials_reach_webdav_and_return_207_multistatus()
    {
        // The bridge is expected to: (a) intercept in the /dav branch, (b) POST the creds to the
        // live OpenIddict /connect/token endpoint (via TestServer handler), (c) receive a real
        // JWT, (d) rewrite Authorization to Bearer {jwt}, (e) let UseAuthentication + the
        // StrgWebDavMiddleware run the PROPFIND. A failure anywhere in that chain manifests as
        // a non-207 status — the assertion is deliberately PROPFIND-shaped rather than just
        // "not 401" because the WebDAV-specific 207 proves dispatch reached StrgWebDavMiddleware,
        // not that the request merely passed auth and then failed at routing.
        //
        // This test is also the regression pin for the pipeline-ordering invariant documented in
        // Program.cs: an outer UseAuthentication() BEFORE the /dav Map would cache NoResult
        // against the original Basic header, and the branch's UseAuthentication() would return
        // that stale NoResult post-rewrite. A silent flip of that ordering resurfaces here as a
        // 401 on credentials that are demonstrably correct (the unit tests still pass because
        // they drive the middleware in isolation without a second UseAuthentication).
        var client = _bridgeFactory!.CreateClient();
        client.DefaultRequestHeaders.Authorization = BuildBasicHeader(
            StrgWebApplicationFactory.AdminEmail,
            StrgWebApplicationFactory.AdminPassword);

        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), $"/dav/{DriveName}/");
        request.Headers.Add("Depth", "0");
        using var response = await client.SendAsync(request);

        var respHeaders = string.Join("; ", response.Headers.Select(h => $"{h.Key}={string.Join(",", h.Value)}"));
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.MultiStatus,
            because: $"the bridge must exchange the Basic credentials against the live /connect/token " +
                     $"endpoint and hand StrgWebDavMiddleware a Bearer JWT — any drift in pipeline " +
                     $"wiring (including a regression that adds UseAuthentication BEFORE the /dav Map), " +
                     $"client_id/scope config, or password hasher Verify will surface here as a non-207 " +
                     $"status the stubbed unit tests cannot detect. ResponseHeaders: [{respHeaders}]. Body: [{body}]");
    }

    [Fact]
    public async Task Wrong_basic_credentials_return_401_with_webdav_client_actionable_challenge()
    {
        // Deliberately-wrong password. The integration-level contract: 401 + WWW-Authenticate:
        // Basic realm="strg". Windows Explorer, Finder, and DAVx5 all key off WWW-Authenticate
        // to re-prompt the user — a regression that dropped the challenge or renamed the realm
        // would make a wrong-password case look like "service broken" and break the user's
        // re-prompt flow. The unit test pins this for stubbed /connect/token; this test pins it
        // end-to-end so a live-token-endpoint 400 response flows through the bridge to the same
        // shape a WebDAV client sees.
        var client = _bridgeFactory!.CreateClient();
        client.DefaultRequestHeaders.Authorization = BuildBasicHeader(
            StrgWebApplicationFactory.AdminEmail,
            "deliberately-wrong-password-for-integration-test");

        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), $"/dav/{DriveName}/");
        request.Headers.Add("Depth", "0");
        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            because: "wrong password at /connect/token must surface as 401 at the /dav surface — " +
                     "a silent flip to 502 would lie about the failure mode (upstream failure, not " +
                     "credential failure) and prevent WebDAV clients from re-prompting the user");

        response.Headers.TryGetValues(HeaderNames.WWWAuthenticate, out var challenges)
            .Should().BeTrue(because: "WebDAV clients need WWW-Authenticate to re-prompt; absence turns a " +
                                      "wrong-password case into a silent stuck state");
        challenges!.Should().ContainSingle()
            .Which.Should().Be("Basic realm=\"strg\"",
                because: "the realm string is the identity the client caches credentials against; a rename " +
                         "would invalidate saved creds on every WebDAV client simultaneously");
    }

    // ---- helpers ----

    private static AuthenticationHeaderValue BuildBasicHeader(string username, string password)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
        return new AuthenticationHeaderValue("Basic", encoded);
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
