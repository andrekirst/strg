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
using Xunit;

namespace Strg.Integration.Tests.Auth;

/// <summary>
/// STRG-016 follow-up — TokenController.HandleRefreshTokenGrantAsync MUST re-read the user row
/// and fail-closed on soft-delete / lockout / missing user. Without this, a refresh token keeps
/// minting access tokens up to natural expiry even after admin revocation signals.
///
/// <para>Also verifies identity claims are rebuilt from the fresh row (role downgrade propagates
/// on the next refresh, not at refresh-token expiry) while scopes are preserved (they are a
/// client/server contract, not identity state).</para>
/// </summary>
public sealed class RefreshTokenRevalidationTests(StrgWebApplicationFactory factory)
    : IClassFixture<StrgWebApplicationFactory>
{
    private const string ScopesWithRefresh = "files.read offline_access";

    [Fact]
    public async Task Refresh_with_live_user_returns_new_token_pair()
    {
        var (user, password) = await CreateUserAsync("refresh-happy@strg.test", UserRole.User);
        var initial = await GetTokensAsync(user.Email, password);

        using var response = await factory.PostRefreshAsync(initial.RefreshToken!);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var refreshed = await StrgWebApplicationFactory.ReadTokensAsync(response);
        refreshed.AccessToken.Should().NotBeNullOrWhiteSpace().And.NotBe(initial.AccessToken);
        refreshed.RefreshToken.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Refresh_fails_closed_when_user_is_soft_deleted()
    {
        var (user, password) = await CreateUserAsync("refresh-softdelete@strg.test", UserRole.User);
        var initial = await GetTokensAsync(user.Email, password);

        await MutateUserAsync(user.Id, u => u.DeletedAt = DateTimeOffset.UtcNow);

        using var response = await factory.PostRefreshAsync(initial.RefreshToken!);

        await AssertInvalidGrantAsync(response);
    }

    [Fact]
    public async Task Refresh_fails_closed_when_user_is_currently_locked()
    {
        var (user, password) = await CreateUserAsync("refresh-locked@strg.test", UserRole.User);
        var initial = await GetTokensAsync(user.Email, password);

        await MutateUserAsync(user.Id, u =>
        {
            u.FailedLoginAttempts = 5;
            u.LockedUntil = DateTimeOffset.UtcNow.AddMinutes(15);
        });

        using var response = await factory.PostRefreshAsync(initial.RefreshToken!);

        await AssertInvalidGrantAsync(response);
    }

    [Fact]
    public async Task Refresh_after_role_downgrade_issues_token_with_updated_role_claim()
    {
        // Starting role carries into the initial access token; after downgrade the refreshed
        // access token must reflect the *current* DB row, not the cached role claim from the
        // refresh-token principal.
        var (user, password) = await CreateUserAsync("refresh-downgrade@strg.test", UserRole.Admin);
        var initial = await GetTokensAsync(user.Email, password);

        DecodeClaim(initial.AccessToken, "role").Should().Be(UserRole.Admin.ToString());

        await MutateUserAsync(user.Id, u => u.Role = UserRole.User);

        using var response = await factory.PostRefreshAsync(initial.RefreshToken!);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var refreshed = await StrgWebApplicationFactory.ReadTokensAsync(response);

        DecodeClaim(refreshed.AccessToken, "role").Should().Be(UserRole.User.ToString());
    }

    [Fact]
    public async Task Refresh_preserves_scopes_from_initial_grant()
    {
        var (user, password) = await CreateUserAsync("refresh-scopes@strg.test", UserRole.User);
        var initial = await GetTokensAsync(user.Email, password);

        using var response = await factory.PostRefreshAsync(initial.RefreshToken!);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var refreshed = await StrgWebApplicationFactory.ReadTokensAsync(response);

        DecodeScopes(refreshed.AccessToken).Should().Contain("files.read");
    }

    private static async Task AssertInvalidGrantAsync(HttpResponseMessage response)
    {
        // OpenIddict maps [Forbid(...)] with InvalidGrant to HTTP 400 + JSON body {error:"invalid_grant"}.
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("invalid_grant");
    }

    private async Task<(User User, string Password)> CreateUserAsync(string email, UserRole role)
    {
        const string password = "integration-refresh-password-42";
        await using var sp = BuildScopedServices(includeHasher: true);
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        var user = new User
        {
            TenantId = factory.AdminTenantId,
            Email = email,
            DisplayName = email,
            PasswordHash = hasher.Hash(password),
            Role = role,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return (user, password);
    }

    private async Task MutateUserAsync(Guid userId, Action<User> mutate)
    {
        await using var sp = BuildScopedServices(includeHasher: false);
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();
        var user = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == userId);
        mutate(user);
        await db.SaveChangesAsync();
    }

    private ServiceProvider BuildScopedServices(bool includeHasher)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext>(new EmptyTenantContext());
        if (includeHasher)
        {
            services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
        }
        services.AddDbContext<StrgDbContext>(opts => opts.UseNpgsql(factory.ConnectionString).UseOpenIddict());
        return services.BuildServiceProvider();
    }

    private async Task<(string AccessToken, string? RefreshToken)> GetTokensAsync(string email, string password)
    {
        using var response = await factory.PostTokenAsync(email, password, scopes: ScopesWithRefresh);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await StrgWebApplicationFactory.ReadTokensAsync(response);
    }

    private static string? DecodeClaim(string jwt, string claimName)
    {
        using var payload = DecodePayload(jwt);
        return payload.RootElement.TryGetProperty(claimName, out var value) ? value.GetString() : null;
    }

    private static string[] DecodeScopes(string jwt)
    {
        using var payload = DecodePayload(jwt);
        if (!payload.RootElement.TryGetProperty("scope", out var scope))
        {
            // OpenIddict sometimes splits scopes into an oi_scp array — check both shapes.
            if (!payload.RootElement.TryGetProperty("oi_scp", out scope))
            {
                return Array.Empty<string>();
            }
        }

        return scope.ValueKind switch
        {
            JsonValueKind.String => scope.GetString()!.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            JsonValueKind.Array => scope.EnumerateArray().Select(e => e.GetString()!).ToArray(),
            _ => Array.Empty<string>(),
        };
    }

    private static JsonDocument DecodePayload(string jwt)
    {
        // DisableAccessTokenEncryption is set in OpenIddictConfiguration, so the access token is
        // a plain signed JWT whose middle segment is base64url-encoded JSON.
        var segment = jwt.Split('.')[1];
        var padded = segment.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - padded.Length % 4) % 4), '=');
        return JsonDocument.Parse(Convert.FromBase64String(padded));
    }

    private sealed class EmptyTenantContext : ITenantContext
    {
        public Guid TenantId => Guid.Empty;
    }
}
