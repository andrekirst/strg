using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Strg.Core.Auditing;
using Strg.Core.Domain;
using Strg.Core.Services;
using Strg.Infrastructure.Data;
using Strg.Infrastructure.Services;
using Xunit;

namespace Strg.Integration.Tests.Auth;

/// <summary>
/// STRG-015 TC-006/TC-007: pins the contract that every <c>/connect/token</c> exchange produces
/// exactly one <see cref="AuditEntry"/> with the right action + tenant shape. Without these
/// tests, silent regressions in <c>TokenController</c> wiring (e.g. removing the audit call
/// during a refactor) would not surface until an operator noticed missing rows in prod.
/// </summary>
public sealed class AuditLoggingTests(StrgWebApplicationFactory factory) : IClassFixture<StrgWebApplicationFactory>
{
    [Fact]
    public async Task Successful_password_grant_writes_one_login_success_entry_under_user_tenant()
    {
        await factory.ResetAdminLockoutAsync();
        var before = await CountAuditEntriesAsync(AuditActions.LoginSuccess);

        using var response = await factory.PostTokenAsync(
            StrgWebApplicationFactory.AdminEmail,
            StrgWebApplicationFactory.AdminPassword);
        response.EnsureSuccessStatusCode();

        var after = await CountAuditEntriesAsync(AuditActions.LoginSuccess);
        after.Should().Be(before + 1);

        var latest = await LatestAuditEntryAsync(AuditActions.LoginSuccess);
        latest.Should().NotBeNull();
        latest!.UserId.Should().Be(factory.AdminUserId);
        latest.TenantId.Should().Be(factory.AdminTenantId);
        latest.ResourceType.Should().Be("User");
        latest.ResourceId.Should().Be(factory.AdminUserId);
    }

    [Fact]
    public async Task Wrong_password_writes_one_login_failure_entry_with_empty_user_and_tenant()
    {
        await factory.ResetAdminLockoutAsync();
        var before = await CountAuditEntriesAsync(AuditActions.LoginFailure);

        using var response = await factory.PostTokenAsync(
            StrgWebApplicationFactory.AdminEmail,
            "definitely-not-the-admin-password");
        response.IsSuccessStatusCode.Should().BeFalse();

        var after = await CountAuditEntriesAsync(AuditActions.LoginFailure);
        after.Should().Be(before + 1);

        var latest = await LatestAuditEntryAsync(AuditActions.LoginFailure);
        latest.Should().NotBeNull();
        // UserId/TenantId stay Guid.Empty on purpose — resolving either from the submitted email
        // would leak whether the email exists via the audit row itself.
        latest!.UserId.Should().Be(Guid.Empty);
        latest.TenantId.Should().Be(Guid.Empty);
    }

    [Fact]
    public async Task Unknown_email_writes_login_failure_entry_indistinguishable_from_wrong_password()
    {
        await factory.ResetAdminLockoutAsync();
        var before = await CountAuditEntriesAsync(AuditActions.LoginFailure);

        using var response = await factory.PostTokenAsync(
            "nobody-by-this-email@strg.test",
            "also-wrong");
        response.IsSuccessStatusCode.Should().BeFalse();

        var after = await CountAuditEntriesAsync(AuditActions.LoginFailure);
        after.Should().Be(before + 1);

        var latest = await LatestAuditEntryAsync(AuditActions.LoginFailure);
        latest.Should().NotBeNull();
        latest!.UserId.Should().Be(Guid.Empty);
        latest.TenantId.Should().Be(Guid.Empty);
        latest.Action.Should().Be(AuditActions.LoginFailure);
    }

    // Reverse-proxy topology: real deployments terminate TLS at nginx/traefik and forward the
    // original client IP via X-Forwarded-For. The audit row must record that first-hop IP, not
    // the loopback address Connection.RemoteIpAddress returns inside the proxy tier. This test
    // also pins the success Details format: `ip=<first-hop>` with no email.
    [Fact]
    public async Task Login_success_audit_records_xforwarded_for_first_hop_as_ip()
    {
        await factory.ResetAdminLockoutAsync();
        const string firstHop = "203.0.113.7";
        const string chain = $"{firstHop}, 198.51.100.5, 192.0.2.1";

        using var client = factory.CreateClient();
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = StrgWebApplicationFactory.AdminEmail,
            ["password"] = StrgWebApplicationFactory.AdminPassword,
            ["client_id"] = StrgWebApplicationFactory.DefaultClientId,
            ["scope"] = StrgWebApplicationFactory.AdminScopes,
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/connect/token") { Content = form };
        request.Headers.Add("X-Forwarded-For", chain);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var latest = await LatestAuditEntryAsync(AuditActions.LoginSuccess);
        latest.Should().NotBeNull();
        latest!.Details.Should().Be($"ip={firstHop}",
            "the first XFF hop wins and success details format is 'ip=<value>' (no email)");
    }

    // Failure path pins the inverse Details contract: `email=<submitted-verbatim>; ip=<value>`.
    // The email is intentionally NOT lowercased so an operator sees exactly what the caller
    // sent — useful for correlating with client-side bugs or attack signatures.
    [Fact]
    public async Task Login_failure_audit_records_submitted_email_verbatim_in_details()
    {
        await factory.ResetAdminLockoutAsync();
        const string submittedEmail = "Typo-Casing@STRG.test";

        using var response = await factory.PostTokenAsync(submittedEmail, "definitely-wrong");
        response.IsSuccessStatusCode.Should().BeFalse();

        var latest = await LatestAuditEntryAsync(AuditActions.LoginFailure);
        latest.Should().NotBeNull();
        latest!.Details.Should().StartWith($"email={submittedEmail};",
            "failure details preserve the caller's email verbatim (no lowercase normalization)");
        latest.Details.Should().Contain("ip=",
            "failure details always include an ip= segment (value may be 'unknown' under the test harness)");
    }

    // Refresh grant is the second door into the token endpoint. Its audit coverage is symmetric
    // with password grant: a successful rotation writes a login.success row bound to the current
    // user/tenant. Without this test, a future refactor that forgot to call TryLogLoginSuccessAsync
    // in HandleRefreshTokenGrantAsync would silently lose half the auth audit trail.
    [Fact]
    public async Task Refresh_success_writes_one_login_success_entry_for_current_user()
    {
        var (user, password) = await CreateAuditTestUserAsync("refresh-audit-happy@strg.test");
        using var initial = await factory.PostTokenAsync(user.Email, password, scopes: "files.read offline_access");
        var (_, refreshToken) = await StrgWebApplicationFactory.ReadTokensAsync(initial);
        refreshToken.Should().NotBeNullOrWhiteSpace();

        var before = await CountAuditEntriesForUserAsync(user.Id, AuditActions.LoginSuccess);

        using var response = await factory.PostRefreshAsync(refreshToken!);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        var after = await CountAuditEntriesForUserAsync(user.Id, AuditActions.LoginSuccess);
        after.Should().Be(before + 1, "refresh success must write exactly one login.success row for the refreshed user");
    }

    // Fail-closed path: a refresh token that still decodes cleanly but whose subject user was
    // soft-deleted since the token was issued must produce a login.failure audit row, not a
    // silent 400. Pairs with RefreshTokenRevalidationTests — that file asserts the 400, this one
    // asserts the observability trail behind it.
    [Fact]
    public async Task Refresh_against_soft_deleted_user_writes_login_failure_entry()
    {
        var (user, password) = await CreateAuditTestUserAsync("refresh-audit-softdelete@strg.test");
        using var initial = await factory.PostTokenAsync(user.Email, password, scopes: "files.read offline_access");
        var (_, refreshToken) = await StrgWebApplicationFactory.ReadTokensAsync(initial);
        refreshToken.Should().NotBeNullOrWhiteSpace();

        await SoftDeleteUserAsync(user.Id);

        var before = await CountAuditEntriesAsync(AuditActions.LoginFailure);

        using var response = await factory.PostRefreshAsync(refreshToken!);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());

        var after = await CountAuditEntriesAsync(AuditActions.LoginFailure);
        after.Should().Be(before + 1,
            "refresh revalidation failing closed on a soft-deleted user must still leave an audit trail");

        var latest = await LatestAuditEntryAsync(AuditActions.LoginFailure);
        latest.Should().NotBeNull();
        latest!.UserId.Should().Be(Guid.Empty,
            "failure rows never resolve UserId — matches the enumeration-safe pattern from the password-grant path");
    }

    private async Task<int> CountAuditEntriesAsync(string action)
    {
        await using var db = CreateDbContext();
        return await db.AuditEntries.IgnoreQueryFilters().CountAsync(e => e.Action == action);
    }

    private async Task<int> CountAuditEntriesForUserAsync(Guid userId, string action)
    {
        await using var db = CreateDbContext();
        return await db.AuditEntries.IgnoreQueryFilters()
            .CountAsync(e => e.Action == action && e.UserId == userId);
    }

    private async Task<AuditEntry?> LatestAuditEntryAsync(string action)
    {
        await using var db = CreateDbContext();
        return await db.AuditEntries.IgnoreQueryFilters()
            .Where(e => e.Action == action)
            .OrderByDescending(e => e.PerformedAt)
            .FirstOrDefaultAsync();
    }

    private async Task<(User User, string Password)> CreateAuditTestUserAsync(string email)
    {
        const string password = "integration-audit-password-42";
        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext>(new UnusedTenantContext());
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
            DisplayName = email,
            PasswordHash = hasher.Hash(password),
            Role = UserRole.User,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return (user, password);
    }

    private async Task SoftDeleteUserAsync(Guid userId)
    {
        await using var db = CreateDbContext();
        var user = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == userId);
        user.DeletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    private StrgDbContext CreateDbContext()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext>(new UnusedTenantContext());
        services.AddDbContext<StrgDbContext>(opts => opts.UseNpgsql(factory.ConnectionString).UseOpenIddict());
        var sp = services.BuildServiceProvider();
        return sp.GetRequiredService<StrgDbContext>();
    }

    private sealed class UnusedTenantContext : ITenantContext
    {
        // Tests use IgnoreQueryFilters(), so the tenant id is never consulted — match
        // StrgWebApplicationFactory's BootstrapSchemaAndSeedAsync which uses Guid.Empty here.
        public Guid TenantId => Guid.Empty;
    }
}
