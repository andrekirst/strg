using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Strg.Infrastructure.Data;
using Strg.Integration.Tests.Auth;
using Xunit;

namespace Strg.Integration.Tests.Users;

internal sealed class FixedTenantContext(Guid id) : ITenantContext
{
    public Guid TenantId => id;
}

/// <summary>
/// STRG-086 HTTP contract: <c>POST /api/v1/users/register</c> returns
/// <see cref="HttpStatusCode.NoContent"/> regardless of outcome. The anti-enumeration guarantee
/// lives at the status-code level — a response that distinguishes "duplicate email" from "new
/// email" hands an attacker a free oracle. These tests assert both halves of the contract: the
/// wire code is uniform, and the DB reflects the real outcome.
///
/// <para>
/// The factory seeds a bootstrap tenant named <c>integration-test-tenant</c> on purpose; the
/// registration endpoint resolves by the hard-coded name <c>default</c>. Tests that want a
/// successful registration path must call <see cref="StrgWebApplicationFactory.SeedDefaultTenantAsync"/>
/// first; tests covering the "default tenant missing" branch deliberately do not.
/// </para>
/// </summary>
public sealed class RegistrationTests(StrgWebApplicationFactory factory) : IClassFixture<StrgWebApplicationFactory>
{
    private const string ValidPassword = "integration-test-password-42";

    // Happy path — new email, long-enough password, tenant exists. Status is 204 AND the user
    // is actually persisted. If persistence ever silently fails under a 204, the status-code
    // uniformity would hide the regression completely.
    [Fact]
    public async Task Register_with_new_email_returns_204_and_persists_user()
    {
        var tenantId = await factory.SeedDefaultTenantAsync();
        var email = $"happy-{Guid.NewGuid():N}@strg.test";

        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/v1/users/register", new
        {
            Email = email,
            DisplayName = "Happy Path",
            Password = ValidPassword,
        });

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var user = await LoadUserByEmailAsync(email);
        user.Should().NotBeNull();
        user!.TenantId.Should().Be(tenantId);
        user.PasswordHash.Should().NotBeNullOrEmpty();
        user.PasswordHash.Should().NotBe(ValidPassword, "password must be hashed, never stored plaintext");
    }

    // Duplicate email — the endpoint must still return 204 (no enumeration oracle) AND must not
    // create a second row. Two successive calls with the same email must leave exactly one user.
    [Fact]
    public async Task Register_with_duplicate_email_returns_204_and_leaves_one_user()
    {
        await factory.SeedDefaultTenantAsync();
        var email = $"duplicate-{Guid.NewGuid():N}@strg.test";

        using var client = factory.CreateClient();
        using var first = await client.PostAsJsonAsync("/api/v1/users/register", new
        {
            Email = email,
            DisplayName = "Original",
            Password = ValidPassword,
        });
        first.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var second = await client.PostAsJsonAsync("/api/v1/users/register", new
        {
            Email = email,
            DisplayName = "Impersonator",
            Password = ValidPassword,
        });
        second.StatusCode.Should().Be(HttpStatusCode.NoContent,
            "a 409/422 here would be a free email-existence oracle for attackers");

        var count = await CountUsersByEmailAsync(email);
        count.Should().Be(1);
    }

    // Validator-level rejection (password too short) must land as 204 — the validator logs the
    // cause for ops but the caller sees no discrimination. Assertion mirrors the duplicate case:
    // status uniform, DB unchanged.
    [Fact]
    public async Task Register_with_short_password_returns_204_and_creates_no_user()
    {
        await factory.SeedDefaultTenantAsync();
        var email = $"short-pw-{Guid.NewGuid():N}@strg.test";

        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/v1/users/register", new
        {
            Email = email,
            DisplayName = "Short Password",
            Password = "short", // < 12 chars, below UserManagerErrors.MinimumPasswordLength
        });

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var user = await LoadUserByEmailAsync(email);
        user.Should().BeNull("validation rejection must not persist anything");
    }

    private async Task<Core.Domain.User?> LoadUserByEmailAsync(string email)
    {
        await using var sp = BuildDbServiceProvider();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();
        return await db.Users.IgnoreQueryFilters()
            .SingleOrDefaultAsync(u => u.Email == email.ToLowerInvariant());
    }

    private async Task<int> CountUsersByEmailAsync(string email)
    {
        await using var sp = BuildDbServiceProvider();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();
        return await db.Users.IgnoreQueryFilters()
            .CountAsync(u => u.Email == email.ToLowerInvariant());
    }

    private ServiceProvider BuildDbServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext>(new FixedTenantContext(Guid.Empty));
        services.AddDbContext<StrgDbContext>(opts => opts.UseNpgsql(factory.ConnectionString).UseOpenIddict());
        return services.BuildServiceProvider();
    }
}

/// <summary>
/// Separate fixture so the bootstrap tenant <c>integration-test-tenant</c> is the ONLY tenant
/// in the DB — the registration endpoint's <c>"default"</c> lookup must miss. A misconfigured
/// deployment (seed worker crashed, DB restored without seeds) must still return 204 without
/// leaking the state via a 500 or a 503.
/// </summary>
public sealed class RegistrationWithoutDefaultTenantTests(StrgWebApplicationFactory factory)
    : IClassFixture<StrgWebApplicationFactory>
{
    [Fact]
    public async Task Register_when_default_tenant_missing_returns_204_and_creates_no_user()
    {
        var email = $"no-tenant-{Guid.NewGuid():N}@strg.test";

        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/v1/users/register", new
        {
            Email = email,
            DisplayName = "No Tenant",
            Password = "integration-test-password-42",
        });

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext>(new FixedTenantContext(Guid.Empty));
        services.AddDbContext<StrgDbContext>(opts => opts.UseNpgsql(factory.ConnectionString).UseOpenIddict());
        await using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();
        var count = await db.Users.IgnoreQueryFilters()
            .CountAsync(u => u.Email == email.ToLowerInvariant());
        count.Should().Be(0, "no default tenant → registration cannot persist, but status must still be 204");
    }
}
