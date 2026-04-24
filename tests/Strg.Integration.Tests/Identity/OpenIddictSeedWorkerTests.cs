using System.Collections.Immutable;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OpenIddict.Abstractions;
using Strg.Core.Domain;
using Strg.Core.Constants;
using Strg.Infrastructure.Data;
using Strg.Infrastructure.Identity;
using Testcontainers.PostgreSql;
using Xunit;

namespace Strg.Integration.Tests.Identity;

/// <summary>
/// Pins the invariants documented on <see cref="OpenIddictSeedWorker"/>: a fresh boot creates the
/// <c>strg-default</c> row with Public client type, PKCE requirement, and the expected grant /
/// scope permission set — and a second boot is a no-op. Uses a real Postgres container because
/// the assertions observe the OpenIddict row shape as persisted by EF Core, not the descriptor
/// passed to <see cref="IOpenIddictApplicationManager.CreateAsync(OpenIddictApplicationDescriptor, System.Threading.CancellationToken)"/>.
/// </summary>
public sealed class OpenIddictSeedWorkerTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task StartAsync_creates_strg_default_client_on_empty_database()
    {
        await using var services = await BuildServicesAsync();
        var worker = new OpenIddictSeedWorker(services);

        await worker.StartAsync(CancellationToken.None);

        await using var scope = services.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var client = await manager.FindByClientIdAsync(OpenIddictSeedWorker.DefaultClientId);
        client.Should().NotBeNull("the seed worker must create the default client on first boot");
    }

    [Fact]
    public async Task StartAsync_pins_client_type_public_on_seeded_row()
    {
        await using var services = await BuildServicesAsync();
        await new OpenIddictSeedWorker(services).StartAsync(CancellationToken.None);

        await using var scope = services.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var client = await manager.FindByClientIdAsync(OpenIddictSeedWorker.DefaultClientId);
        var clientType = await manager.GetClientTypeAsync(client!);

        clientType.Should().Be(OpenIddictConstants.ClientTypes.Public,
            "first-party CLI / SPA callers cannot keep a secret; pinning the type prevents a future edit from silently flipping the registration to confidential");
    }

    [Fact]
    public async Task StartAsync_pins_pkce_requirement_at_client_level()
    {
        await using var services = await BuildServicesAsync();
        await new OpenIddictSeedWorker(services).StartAsync(CancellationToken.None);

        await using var scope = services.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var client = await manager.FindByClientIdAsync(OpenIddictSeedWorker.DefaultClientId);
        var requirements = await manager.GetRequirementsAsync(client!);

        requirements.Should().Contain(OpenIddictConstants.Requirements.Features.ProofKeyForCodeExchange,
            "PKCE must be enforced on this client even if a future refactor removes the server-wide RequireProofKeyForCodeExchange");
    }

    [Fact]
    public async Task StartAsync_grants_password_refresh_and_first_party_scope_permissions()
    {
        await using var services = await BuildServicesAsync();
        await new OpenIddictSeedWorker(services).StartAsync(CancellationToken.None);

        await using var scope = services.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var client = await manager.FindByClientIdAsync(OpenIddictSeedWorker.DefaultClientId);
        var permissions = await manager.GetPermissionsAsync(client!);

        var expected = ImmutableArray.Create(
            OpenIddictConstants.Permissions.Endpoints.Token,
            OpenIddictConstants.Permissions.GrantTypes.Password,
            OpenIddictConstants.Permissions.GrantTypes.RefreshToken,
            OpenIddictConstants.Permissions.Prefixes.Scope + "files.read",
            OpenIddictConstants.Permissions.Prefixes.Scope + "files.write",
            OpenIddictConstants.Permissions.Prefixes.Scope + "files.share",
            OpenIddictConstants.Permissions.Prefixes.Scope + "tags.write",
            OpenIddictConstants.Permissions.Prefixes.Scope + "admin",
            OpenIddictConstants.Permissions.Prefixes.Scope + OpenIddictConstants.Scopes.OpenId,
            OpenIddictConstants.Permissions.Prefixes.Scope + OpenIddictConstants.Scopes.Email,
            OpenIddictConstants.Permissions.Prefixes.Scope + OpenIddictConstants.Scopes.Profile,
            OpenIddictConstants.Permissions.Prefixes.Scope + OpenIddictConstants.Scopes.OfflineAccess);

        permissions.Should().BeEquivalentTo(expected,
            "the seeded permission set is the contract this worker pins — new scopes or grant types must be added here, not hand-edited on the row");
    }

    [Fact]
    public async Task StartAsync_is_noop_on_second_run_when_client_already_exists()
    {
        await using var services = await BuildServicesAsync();
        var worker = new OpenIddictSeedWorker(services);
        await worker.StartAsync(CancellationToken.None);
        await worker.StartAsync(CancellationToken.None);

        await using var scope = services.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var count = await manager.CountAsync(CancellationToken.None);

        // Both strg-default (STRG-012) and webdav-internal (STRG-073) are seeded on first boot;
        // the FindByClientIdAsync short-circuit on each must prevent duplicate rows on second boot.
        count.Should().Be(2L, "the FindByClientIdAsync short-circuit must prevent duplicate inserts across both seeded clients");
    }

    [Fact]
    public async Task StartAsync_creates_webdav_internal_client_with_narrow_permissions()
    {
        // STRG-073 — the bridge client exists exclusively to serve Basic-Auth → JWT exchanges from
        // BasicAuthJwtBridgeMiddleware. Its permission set MUST stay narrower than strg-default
        // (no RefreshToken grant, no admin scope, no offline_access, no identity scopes) so that
        // a stolen WebDAV Basic-Auth credential cannot mint a refresh-token-backed or
        // admin-scoped access token via this client. If future edits broaden the permissions,
        // this pin fires and forces a deliberate review of the bridge's blast radius.
        await using var services = await BuildServicesAsync();
        await new OpenIddictSeedWorker(services).StartAsync(CancellationToken.None);

        await using var scope = services.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var client = await manager.FindByClientIdAsync(OpenIddictSeedWorker.WebDavInternalClientId);
        client.Should().NotBeNull("the seed worker must create the webdav-internal client alongside strg-default");

        var clientType = await manager.GetClientTypeAsync(client!);
        clientType.Should().Be(OpenIddictConstants.ClientTypes.Public,
            "the bridge has no secret-storage surface — public client matches reality");

        var permissions = await manager.GetPermissionsAsync(client!);

        // STRG-073 fold-in #1 — the expected scope permissions are sourced from
        // Strg.Core.Constants.WebDavScopes.All, NOT hardcoded here. This is the load-bearing
        // drift guard: if a future edit widens WebDavScopes.All (e.g., adds "files.share"),
        // the bridge will silently request the wider scope AND the seed will silently grant it.
        // Sourcing both sides from the same array means the test only fires on the intended
        // security invariant — "nothing beyond the three WebDavScopes gets granted" — rather
        // than on innocuous scope-set evolution.
        var expectedBuilder = ImmutableArray.CreateBuilder<string>();
        expectedBuilder.Add(OpenIddictConstants.Permissions.Endpoints.Token);
        expectedBuilder.Add(OpenIddictConstants.Permissions.GrantTypes.Password);
        foreach (var scopeName in WebDavScopes.All)
        {
            expectedBuilder.Add(OpenIddictConstants.Permissions.Prefixes.Scope + scopeName);
        }
        permissions.Should().BeEquivalentTo(expectedBuilder.ToImmutable(),
            "webdav-internal scopes deliberately exclude RefreshToken, admin, offline_access, and the OIDC identity scopes — widening requires a deliberate review, not a silent edit");
    }

    [Fact]
    public void WebDavScopes_does_not_grant_elevated_or_identity_scopes()
    {
        // STRG-073 fold-in #1 — pins the CONTENTS of WebDavScopes.All, not just its shape. The
        // whole-scope-set invariant ("nothing beyond the three") from the seed test above is only
        // meaningful if WebDavScopes itself stays narrow. If a future edit adds "admin",
        // "files.share", or "offline_access" here, the bridge would silently request them AND
        // the seed would silently grant them — the seed test wouldn't catch it because both sides
        // move together. This test is the fence around WebDavScopes itself.
        WebDavScopes.All.Should().NotContain("admin",
            "a stolen Basic-Auth credential must not be able to mint admin-scoped tokens through the bridge");
        WebDavScopes.All.Should().NotContain("files.share",
            "WebDAV clients cannot usefully share via the protocol — granting files.share would let a stolen WebDAV credential mint shareable links");
        WebDavScopes.All.Should().NotContain("offline_access",
            "refresh tokens would outlive the 14-minute cache window that is the intended revocation surface");
        WebDavScopes.All.Should().NotContain("openid",
            "the bridge issues access tokens only; identity tokens have no WebDAV use case and would expose identity claims to cache layers");
        WebDavScopes.All.Should().NotContain("profile",
            "profile-claim tokens have no WebDAV use case");
    }

    // Regression test for the documented "no mutation on subsequent boots" contract: an operator
    // who hand-edits the client (here: adds a display-name suffix) must not be overwritten by the
    // next deploy. If this test starts failing, the seed worker has grown an upsert — that needs
    // an explicit migration story, not a silent overwrite.
    [Fact]
    public async Task StartAsync_does_not_overwrite_an_operator_edit_on_second_run()
    {
        await using var services = await BuildServicesAsync();
        var worker = new OpenIddictSeedWorker(services);
        await worker.StartAsync(CancellationToken.None);

        const string editedDisplayName = "strg Default Client (operator-edited)";
        await using (var scope = services.CreateAsyncScope())
        {
            var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
            var client = await manager.FindByClientIdAsync(OpenIddictSeedWorker.DefaultClientId);
            var descriptor = new OpenIddictApplicationDescriptor();
            await manager.PopulateAsync(descriptor, client!);
            descriptor.DisplayName = editedDisplayName;
            await manager.UpdateAsync(client!, descriptor);
        }

        await worker.StartAsync(CancellationToken.None);

        await using var verifyScope = services.CreateAsyncScope();
        var verifyManager = verifyScope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var verifyClient = await verifyManager.FindByClientIdAsync(OpenIddictSeedWorker.DefaultClientId);
        var displayName = await verifyManager.GetDisplayNameAsync(verifyClient!);

        displayName.Should().Be(editedDisplayName,
            "the seed worker must not overwrite operator edits on subsequent boots");
    }

    private async Task<ServiceProvider> BuildServicesAsync()
    {
        var dbName = $"strg_openiddict_seed_{Guid.NewGuid():N}";
        var adminConnectionString = _postgres.GetConnectionString();

        await using (var connection = new NpgsqlConnection(adminConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE \"{dbName}\"";
            await command.ExecuteNonQueryAsync();
        }

        var testConnectionString = new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            Database = dbName,
        }.ConnectionString;

        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext>(new FixedTenantContext(Guid.Empty));
        services.AddDbContext<StrgDbContext>(opts => opts.UseNpgsql(testConnectionString).UseOpenIddict());
        services.AddOpenIddict()
            .AddCore(o => o.UseEntityFrameworkCore().UseDbContext<StrgDbContext>());

        var provider = services.BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();
            await db.Database.EnsureCreatedAsync();
        }

        return provider;
    }
}
