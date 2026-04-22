using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Strg.Core.Domain;
using Strg.Infrastructure.Data;
using Strg.Integration.Tests.Auth;
using Strg.WebDav;
using Xunit;

namespace Strg.Integration.Tests.WebDav;

/// <summary>
/// STRG-073 fold-in #3 — pins the two DB-layer invariants of <see cref="DriveResolver.GetDriveTenantIdAsync"/>
/// against a real Postgres: <c>IgnoreQueryFilters()</c> bypasses the tenant scope (so the pre-auth
/// bridge lookup finds the drive regardless of the caller's claimed tenant), AND the inline
/// <c>!d.IsDeleted</c> predicate keeps soft-delete enforcement intact (a soft-deleted drive must
/// not surface its tenant via this path — that would reintroduce the tenant-leakage vector
/// <c>IgnoreQueryFilters</c> is opened for).
///
/// <para>The middleware-level tests in <c>BasicAuthJwtBridgeMiddlewareTests</c> use a stubbed
/// <see cref="IDriveResolver"/>, so they cover the bridge's branching on the resolver's outcome
/// but cannot catch a regression in the DB query shape itself — e.g. a refactor that drops the
/// <c>IgnoreQueryFilters()</c> call (silently returns <c>null</c> when the pre-auth tenant context
/// is <see cref="Guid.Empty"/>) or that replaces the inline predicate with reliance on the global
/// soft-delete filter (which would silently leak soft-deleted drives as soon as the method also
/// dropped the tenant filter).</para>
/// </summary>
public sealed class DriveResolverTests(StrgWebApplicationFactory factory)
    : IClassFixture<StrgWebApplicationFactory>
{
    [Fact]
    public async Task GetDriveTenantIdAsync_returns_tenant_id_when_caller_has_no_tenant_context()
    {
        var driveName = $"resolver-it-{Guid.NewGuid():N}".ToLowerInvariant()[..20];
        var driveTenantId = await SeedDriveInFreshTenantAsync(driveName, isDeleted: false);

        await using var sp = BuildScopedDbWithEmptyTenantContext();
        using var scope = sp.CreateScope();
        var resolver = ActivatorUtilities.CreateInstance<DriveResolver>(scope.ServiceProvider);

        var resolved = await resolver.GetDriveTenantIdAsync(driveName);

        resolved.Should().Be(driveTenantId,
            because: "IgnoreQueryFilters() must bypass the tenant global filter; otherwise the "
                   + "pre-auth bridge lookup (tenant context is Guid.Empty at that point) would "
                   + "always return null and the cross-tenant oracle defense would silently fail open.");
    }

    [Fact]
    public async Task GetDriveTenantIdAsync_returns_null_for_soft_deleted_drive()
    {
        var driveName = $"resolver-it-{Guid.NewGuid():N}".ToLowerInvariant()[..20];
        _ = await SeedDriveInFreshTenantAsync(driveName, isDeleted: true);

        await using var sp = BuildScopedDbWithEmptyTenantContext();
        using var scope = sp.CreateScope();
        var resolver = ActivatorUtilities.CreateInstance<DriveResolver>(scope.ServiceProvider);

        var resolved = await resolver.GetDriveTenantIdAsync(driveName);

        resolved.Should().BeNull(
            because: "the inline !d.IsDeleted predicate is the only soft-delete guard on this path "
                   + "(IgnoreQueryFilters disables the global one); a soft-deleted drive leaking "
                   + "its tenant would reintroduce the cross-tenant oracle this method exists to close.");
    }

    [Fact]
    public async Task GetDriveTenantIdAsync_returns_null_for_unknown_drive_name()
    {
        await using var sp = BuildScopedDbWithEmptyTenantContext();
        using var scope = sp.CreateScope();
        var resolver = ActivatorUtilities.CreateInstance<DriveResolver>(scope.ServiceProvider);

        var resolved = await resolver.GetDriveTenantIdAsync($"nonexistent-{Guid.NewGuid():N}"[..24]);

        resolved.Should().BeNull();
    }

    private async Task<Guid> SeedDriveInFreshTenantAsync(string driveName, bool isDeleted)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext>(new FixtureTenantContext(factory.AdminTenantId));
        services.AddDbContext<StrgDbContext>(opts => opts.UseNpgsql(factory.ConnectionString).UseOpenIddict());
        await using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();

        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = $"resolver-it-tenant-{tenantId:N}" });

        var drive = new Drive
        {
            TenantId = tenantId,
            Name = driveName,
            ProviderType = "local",
            ProviderConfig = "{}",
            // TenantedEntity.IsDeleted is computed from DeletedAt.HasValue — setting DeletedAt here
            // is the only way to produce a soft-deleted row, and it matches the shape the soft-delete
            // shadow property ("DeletedAt" → "IsDeleted") infrastructure expects at the DB layer.
            DeletedAt = isDeleted ? DateTimeOffset.UtcNow : null,
        };
        db.Drives.Add(drive);
        await db.SaveChangesAsync();
        return tenantId;
    }

    private ServiceProvider BuildScopedDbWithEmptyTenantContext()
    {
        // Guid.Empty mimics the pre-auth scenario the bridge runs under: the request has NOT yet
        // been authenticated, so no JWT has populated ITenantContext. This is exactly the state
        // in which IgnoreQueryFilters() must save the lookup — a tenant-scoped query with
        // TenantId == Guid.Empty would return nothing regardless of what's in the table.
        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext>(new FixtureTenantContext(Guid.Empty));
        services.AddDbContext<StrgDbContext>(opts => opts.UseNpgsql(factory.ConnectionString).UseOpenIddict());
        return services.BuildServiceProvider();
    }

    private sealed class FixtureTenantContext(Guid tenantId) : ITenantContext
    {
        public Guid TenantId { get; } = tenantId;
    }
}
