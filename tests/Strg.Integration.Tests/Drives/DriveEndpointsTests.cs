using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Strg.Core.Domain;
using Strg.Infrastructure.Data;
using Strg.Integration.Tests.Auth;
using Xunit;

namespace Strg.Integration.Tests.Drives;

/// <summary>
/// REST-layer pins for <c>DriveEndpoints</c> (STRG-060). Each test method pins a specific
/// regression — see the per-method docs for the failure mode each one catches. Together they
/// cover the four CRUD verbs plus the soft-delete name-reservation invariant inherited from
/// STRG-043.
/// </summary>
public sealed class DriveEndpointsTests(StrgWebApplicationFactory factory)
    : IClassFixture<StrgWebApplicationFactory>
{
    /// <summary>
    /// STRG-043 L3 oversized-<c>ProviderConfig</c> guard at <c>CreateDriveValidator</c>. The
    /// GraphQL surface pins the same behaviour in
    /// <c>DriveMutationsTests.CreateDrive_ProviderConfigOver8192Chars_ReturnsValidationError</c>;
    /// this closes the REST side. Regression: a future consolidation of the length-cap check
    /// into a GraphQL-only validator silently drops the REST gate, surfacing as an Npgsql 22001
    /// 500 instead of a structured 422.
    /// </summary>
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

    /// <summary>
    /// STRG-060 TC-001 — pins tenant isolation at the REST surface. A drive seeded in a foreign
    /// tenant must not appear in the admin's GET /drives response. The global tenant filter in
    /// <c>StrgDbContext.OnModelCreating</c> is what makes this work; this test catches a future
    /// refactor that disables it for <c>Drive</c> (e.g. an .IgnoreQueryFilters() landing in
    /// <c>ListDrivesHandler</c>).
    /// </summary>
    [Fact]
    public async Task ListDrives_returns_only_current_tenant_drives()
    {
        var foreignName = $"foreign-list-{Guid.NewGuid():N}"[..24];
        await SeedDriveInOtherTenantAsync(foreignName);

        using var tokenResponse = await factory.PostTokenAsync(
            StrgWebApplicationFactory.AdminEmail,
            StrgWebApplicationFactory.AdminPassword);
        var (token, _) = await StrgWebApplicationFactory.ReadTokensAsync(tokenResponse);
        using var client = factory.CreateAuthenticatedClient(token);

        using var response = await client.GetAsync("/api/v1/drives/");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = body.EnumerateArray().ToList();
        var names = items.Select(d => d.GetProperty("name").GetString()).ToList();
        names.Should().NotContain(foreignName,
            "the global tenant filter must hide drives owned by other tenants");

        // Definition-of-Done pin from STRG-060: providerConfig must NEVER appear on the wire.
        // The positional DriveDto record at DriveEndpoints.cs makes adding a field impossible
        // without a code-review-visible signature change, but a computed property could slip
        // through. This is the wire-level backstop.
        foreach (var item in items)
        {
            item.TryGetProperty("providerConfig", out _).Should().BeFalse(
                "DriveDto must never expose ProviderConfig — it may contain storage credentials");
        }
    }

    /// <summary>
    /// STRG-060 TC-002 — pins the enumeration-oracle defence: a foreign-tenant drive id must
    /// surface as 404, never 403 or 200. Returning 403 would leak existence of cross-tenant
    /// rows; the global tenant filter's hide-from-FirstOrDefaultAsync behaviour is what
    /// produces the 404. A future refactor that swaps the filter for an explicit
    /// <c>if (drive.TenantId != current) return Forbidden()</c> would fail this test.
    /// </summary>
    [Fact]
    public async Task GetDrive_with_foreign_tenant_id_returns_404()
    {
        var foreignDriveId = await SeedDriveInOtherTenantAsync(
            $"foreign-get-{Guid.NewGuid():N}"[..24]);

        using var tokenResponse = await factory.PostTokenAsync(
            StrgWebApplicationFactory.AdminEmail,
            StrgWebApplicationFactory.AdminPassword);
        var (token, _) = await StrgWebApplicationFactory.ReadTokensAsync(tokenResponse);
        using var client = factory.CreateAuthenticatedClient(token);

        using var response = await client.GetAsync($"/api/v1/drives/{foreignDriveId}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "cross-tenant lookup is hidden behind 404 — never 403 — to avoid leaking existence");
    }

    /// <summary>
    /// STRG-060 TC-003 — pins the <c>AuthPolicies.Admin</c> guard on POST /drives. SuperAdmin
    /// can request a narrower token (files.read only); without the Admin policy on the route, a
    /// leaked files.read token would let an attacker mint drives. This test fails if the
    /// <c>.RequireAuthorization(AuthPolicies.Admin)</c> line is dropped from
    /// <c>DriveEndpoints.MapDriveEndpoints</c>.
    /// </summary>
    [Fact]
    public async Task CreateDrive_without_admin_scope_returns_403()
    {
        using var tokenResponse = await factory.PostTokenAsync(
            StrgWebApplicationFactory.AdminEmail,
            StrgWebApplicationFactory.AdminPassword,
            scopes: "files.read");
        tokenResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var (token, _) = await StrgWebApplicationFactory.ReadTokensAsync(tokenResponse);

        using var client = factory.CreateAuthenticatedClient(token);
        using var response = await client.PostAsJsonAsync("/api/v1/drives/", new
        {
            name = $"non-admin-{Guid.NewGuid():N}"[..20],
            providerType = "local",
            encryptionEnabled = false,
        });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "files.read scope alone must NOT mint drives — Admin policy is the only guard");
    }

    /// <summary>
    /// STRG-060 TC-005 — pins the post-delete read behaviour. A soft-deleted drive must vanish
    /// from BOTH GET /drives/{id} (404) and GET /drives (omitted from the array). The
    /// <c>CreateDrive_name_of_soft_deleted_drive_is_rejected_with_409</c> test pins the 204
    /// status and the name-reservation behaviour; this one closes the gap on the
    /// post-delete-read path, catching a future refactor that disables the IsDeleted global
    /// filter for <c>Drive</c>.
    /// </summary>
    [Fact]
    public async Task DeleteDrive_then_drive_is_absent_from_subsequent_GETs()
    {
        using var tokenResponse = await factory.PostTokenAsync(
            StrgWebApplicationFactory.AdminEmail,
            StrgWebApplicationFactory.AdminPassword);
        var (token, _) = await StrgWebApplicationFactory.ReadTokensAsync(tokenResponse);
        using var client = factory.CreateAuthenticatedClient(token);

        var name = $"lifecycle-{Guid.NewGuid():N}"[..24];
        using var create = await client.PostAsJsonAsync("/api/v1/drives/", new
        {
            name,
            providerType = "local",
            encryptionEnabled = false,
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var createBody = await create.Content.ReadFromJsonAsync<JsonElement>();
        var driveId = createBody.GetProperty("id").GetGuid();

        using var delete = await client.DeleteAsync($"/api/v1/drives/{driveId}");
        delete.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var getById = await client.GetAsync($"/api/v1/drives/{driveId}");
        getById.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "soft-deleted drives must not be retrievable by id");

        using var list = await client.GetAsync("/api/v1/drives/");
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        var listBody = await list.Content.ReadFromJsonAsync<JsonElement>();
        var ids = listBody.EnumerateArray().Select(d => d.GetProperty("id").GetGuid()).ToList();
        ids.Should().NotContain(driveId,
            "soft-deleted drives must not appear in the list response");
    }

    // Mints a tenant + drive directly in the same database the WebApplicationFactory uses, but
    // OUTSIDE the admin's tenant scope. Same throwaway-service-container shape as
    // RegistrationTests.BuildDbServiceProvider and StrgWebApplicationFactory.SeedDefaultTenantAsync:
    // FixedTenantContext(Guid.Empty) bypasses the global tenant filter for the INSERT, while the
    // explicit Drive.TenantId field anchors the row in the foreign tenant so the SAME global
    // filter hides it from admin-scoped HTTP queries — which is exactly the property the caller
    // tests want to pin.
    private async Task<Guid> SeedDriveInOtherTenantAsync(string driveName)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext>(new SeedTenantContext(Guid.Empty));
        services.AddSingleton<ICurrentUser>(new SeedCurrentUser());
        services.AddDbContext<StrgDbContext>(opts =>
            opts.UseNpgsql(factory.ConnectionString).UseOpenIddict());
        await using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();

        var foreignTenantId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = foreignTenantId, Name = $"foreign-{foreignTenantId:N}" });
        var drive = new Drive
        {
            TenantId = foreignTenantId,
            Name = driveName,
            ProviderType = "local",
            ProviderConfig = "{}",
            EncryptionEnabled = false,
            IsDefault = false,
        };
        db.Drives.Add(drive);
        await db.SaveChangesAsync();
        return drive.Id;
    }

    private sealed class SeedTenantContext(Guid id) : ITenantContext
    {
        public Guid TenantId => id;
    }

    private sealed class SeedCurrentUser : ICurrentUser
    {
        public Guid UserId => Guid.Empty;
    }
}
