using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Strg.Core.Domain;
using Strg.Core.Services;
using Strg.Infrastructure.Data;
using Strg.Infrastructure.Services;
using Strg.Integration.Tests.Auth;
using Xunit;

namespace Strg.Integration.Tests.Users;

/// <summary>
/// STRG-059 HTTP contract — covers the full <c>/api/v1/users</c> surface: self-service /me
/// reads/writes for any authenticated user, admin-only list / single-user / quota / lock /
/// unlock for callers holding the admin scope.
///
/// <para><b>Test isolation strategy.</b> The fixture is class-scoped (<c>IClassFixture</c>),
/// so admin state bleeds across tests. Tests that mutate the admin (display name, lock state)
/// reload by id and assert the post-mutation state without depending on prior values; tests
/// that need a non-admin target user create one fresh through a direct DbContext insert under
/// the admin's tenant — the public registration endpoint targets a different tenant
/// (<c>default</c>) by design, so reuse via that endpoint would not produce a row visible to
/// the admin's tenant-filtered queries.</para>
/// </summary>
public sealed class UserEndpointsTests(StrgWebApplicationFactory factory)
    : IClassFixture<StrgWebApplicationFactory>
{
    // -- TC-001 -----------------------------------------------------------------------------------
    [Fact]
    public async Task GetMe_returns_current_user_profile()
    {
        var token = await GetAdminTokenAsync();
        using var client = factory.CreateAuthenticatedClient(token);

        using var response = await client.GetAsync("/api/v1/users/me");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("id").GetGuid().Should().Be(factory.AdminUserId);
        body.GetProperty("email").GetString().Should().Be(StrgWebApplicationFactory.AdminEmail);

        // Security pin — UserDto must NEVER carry the password hash.
        body.TryGetProperty("passwordHash", out _).Should().BeFalse(
            "UserDto must never expose PasswordHash");
        // Security pin — raw LockedUntil is hidden; only the computed isLocked boolean is exposed.
        body.TryGetProperty("lockedUntil", out _).Should().BeFalse(
            "UserDto must never expose the raw LockedUntil timestamp");
    }

    // -- TC-002 -----------------------------------------------------------------------------------
    [Fact]
    public async Task PutMe_with_new_displayName_persists_change()
    {
        var token = await GetAdminTokenAsync();
        using var client = factory.CreateAuthenticatedClient(token);

        var newName = $"Renamed-{Guid.NewGuid():N}"[..16];
        using var response = await client.PutAsJsonAsync("/api/v1/users/me", new { displayName = newName });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("displayName").GetString().Should().Be(newName);

        // Direct DB read confirms persistence — protects against a regression where the endpoint
        // returns a synthesized DTO without saving.
        var reloaded = await LoadUserByIdAsync(factory.AdminUserId);
        reloaded!.DisplayName.Should().Be(newName);
    }

    [Fact]
    public async Task PutMe_with_empty_displayName_returns_400()
    {
        // Pins that the validator runs through the pipeline and the endpoint maps
        // ValidationError to HTTP 400 — without a Result<T> shape on the command this would
        // throw and surface as 500.
        var token = await GetAdminTokenAsync();
        using var client = factory.CreateAuthenticatedClient(token);

        using var response = await client.PutAsJsonAsync("/api/v1/users/me", new { displayName = "" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // -- TC-003 -----------------------------------------------------------------------------------
    [Fact]
    public async Task GetUsers_without_admin_scope_returns_403()
    {
        // Token issued WITHOUT the admin scope — only files.read. AuthPolicies.Admin requires
        // the admin scope claim, so the authorization middleware rejects this with 403 before
        // the endpoint runs.
        using var tokenResponse = await factory.PostTokenAsync(
            StrgWebApplicationFactory.AdminEmail,
            StrgWebApplicationFactory.AdminPassword,
            scopes: "files.read");
        tokenResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var (token, _) = await StrgWebApplicationFactory.ReadTokensAsync(tokenResponse);

        using var client = factory.CreateAuthenticatedClient(token);
        using var response = await client.GetAsync("/api/v1/users");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetUsers_with_admin_scope_returns_paginated_tenant_users()
    {
        // Seed an extra user in the admin's tenant so the list has at least 2 rows.
        var extra = await SeedTenantUserAsync($"list-target-{Guid.NewGuid():N}@strg.test");

        var token = await GetAdminTokenAsync();
        using var client = factory.CreateAuthenticatedClient(token);
        using var response = await client.GetAsync("/api/v1/users?page=1&pageSize=200");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("items").GetArrayLength().Should().BeGreaterThanOrEqualTo(2);
        body.GetProperty("page").GetInt32().Should().Be(1);
        body.GetProperty("pageSize").GetInt32().Should().Be(200);
        body.GetProperty("totalCount").GetInt32().Should().BeGreaterThanOrEqualTo(2);

        var ids = body.GetProperty("items")
            .EnumerateArray()
            .Select(i => i.GetProperty("id").GetGuid())
            .ToHashSet();
        ids.Should().Contain(factory.AdminUserId).And.Contain(extra.Id);
    }

    // -- TC-004 -----------------------------------------------------------------------------------
    [Fact]
    public async Task PutUserQuota_updates_QuotaBytes()
    {
        var target = await SeedTenantUserAsync($"quota-target-{Guid.NewGuid():N}@strg.test");
        var token = await GetAdminTokenAsync();
        using var client = factory.CreateAuthenticatedClient(token);

        const long newQuota = 21_474_836_480L; // 20 GiB
        using var response = await client.PutAsJsonAsync(
            $"/api/v1/users/{target.Id}/quota",
            new { quotaBytes = newQuota });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("quotaBytes").GetInt64().Should().Be(newQuota);

        var reloaded = await LoadUserByIdAsync(target.Id);
        reloaded!.QuotaBytes.Should().Be(newQuota);
    }

    [Fact]
    public async Task PutUserQuota_with_negative_value_returns_400()
    {
        var target = await SeedTenantUserAsync($"quota-negative-{Guid.NewGuid():N}@strg.test");
        var token = await GetAdminTokenAsync();
        using var client = factory.CreateAuthenticatedClient(token);

        using var response = await client.PutAsJsonAsync(
            $"/api/v1/users/{target.Id}/quota",
            new { quotaBytes = -1L });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PutUserQuota_for_unknown_user_returns_404()
    {
        var token = await GetAdminTokenAsync();
        using var client = factory.CreateAuthenticatedClient(token);

        using var response = await client.PutAsJsonAsync(
            $"/api/v1/users/{Guid.NewGuid()}/quota",
            new { quotaBytes = 1024L });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // -- TC-005 -----------------------------------------------------------------------------------
    [Fact]
    public async Task PostLock_then_DeleteLock_toggles_account_lock_state()
    {
        var target = await SeedTenantUserAsync($"lock-target-{Guid.NewGuid():N}@strg.test");
        var token = await GetAdminTokenAsync();
        using var client = factory.CreateAuthenticatedClient(token);

        // Lock — POST returns the updated DTO with isLocked=true; DB confirms LockedUntil set
        // to a far-future timestamp.
        using var lockResp = await client.PostAsync($"/api/v1/users/{target.Id}/lock", content: null);
        lockResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var lockedBody = await lockResp.Content.ReadFromJsonAsync<JsonElement>();
        lockedBody.GetProperty("isLocked").GetBoolean().Should().BeTrue();

        var afterLock = await LoadUserByIdAsync(target.Id);
        afterLock!.LockedUntil.Should().NotBeNull();
        afterLock.LockedUntil!.Value.Should().BeAfter(
            DateTimeOffset.UtcNow.AddYears(50),
            "+100 years matches the existing GraphQL admin handler convention");

        // Unlock — DELETE clears LockedUntil; DTO reports isLocked=false; DB confirms null.
        using var unlockResp = await client.DeleteAsync($"/api/v1/users/{target.Id}/lock");
        unlockResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var unlockedBody = await unlockResp.Content.ReadFromJsonAsync<JsonElement>();
        unlockedBody.GetProperty("isLocked").GetBoolean().Should().BeFalse();

        var afterUnlock = await LoadUserByIdAsync(target.Id);
        afterUnlock!.LockedUntil.Should().BeNull();
    }

    [Fact]
    public async Task PostLock_for_unknown_user_returns_404()
    {
        var token = await GetAdminTokenAsync();
        using var client = factory.CreateAuthenticatedClient(token);

        using var response = await client.PostAsync($"/api/v1/users/{Guid.NewGuid()}/lock", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // -- helpers ----------------------------------------------------------------------------------
    private async Task<string> GetAdminTokenAsync()
    {
        using var response = await factory.PostTokenAsync(
            StrgWebApplicationFactory.AdminEmail,
            StrgWebApplicationFactory.AdminPassword);
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "the pre-seeded admin token must succeed before exercising user endpoints");
        var (token, _) = await StrgWebApplicationFactory.ReadTokensAsync(response);
        return token;
    }

    /// <summary>
    /// Inserts a non-admin user directly into <c>StrgDbContext</c> under
    /// <see cref="StrgWebApplicationFactory.AdminTenantId"/>. The public registration endpoint
    /// targets the <c>default</c> tenant by design, which the fixture deliberately does NOT seed
    /// — a registered user would land in a different tenant and the admin's tenant filter would
    /// hide it. Inserting directly into the admin's tenant is what the admin-listing test needs.
    /// </summary>
    private async Task<User> SeedTenantUserAsync(string email)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext>(new FixedTenantContext(factory.AdminTenantId));
        services.AddSingleton<ICurrentUser>(new FixedCurrentUser());
        services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
        services.AddDbContext<StrgDbContext>(opts => opts.UseNpgsql(factory.ConnectionString).UseOpenIddict());
        await using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        var user = new User
        {
            TenantId = factory.AdminTenantId,
            Email = email,
            DisplayName = "Test Target",
            PasswordHash = hasher.Hash("integration-test-password-42"),
            Role = UserRole.User,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private async Task<User?> LoadUserByIdAsync(Guid id)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext>(new FixedTenantContext(factory.AdminTenantId));
        services.AddSingleton<ICurrentUser>(new FixedCurrentUser());
        services.AddDbContext<StrgDbContext>(opts => opts.UseNpgsql(factory.ConnectionString).UseOpenIddict());
        await using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();
        return await db.Users.FirstOrDefaultAsync(u => u.Id == id);
    }
}
