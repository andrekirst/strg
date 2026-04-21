using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Strg.Core.Auditing;
using Strg.Core.Domain;
using Strg.Core.Identity;
using Strg.Infrastructure.Data;
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

    private async Task<int> CountAuditEntriesAsync(string action)
    {
        await using var db = CreateDbContext();
        return await db.AuditEntries.IgnoreQueryFilters().CountAsync(e => e.Action == action);
    }

    private async Task<AuditEntry?> LatestAuditEntryAsync(string action)
    {
        await using var db = CreateDbContext();
        return await db.AuditEntries.IgnoreQueryFilters()
            .Where(e => e.Action == action)
            .OrderByDescending(e => e.PerformedAt)
            .FirstOrDefaultAsync();
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
