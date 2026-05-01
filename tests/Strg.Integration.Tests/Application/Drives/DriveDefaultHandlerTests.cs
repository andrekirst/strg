using FluentAssertions;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Strg.Application.Abstractions;
using Strg.Application.DependencyInjection;
using Strg.Application.Features.Drives.Create;
using Strg.Application.Features.Drives.GetDefault;
using Strg.Application.Features.Drives.SetDefault;
using Strg.Core.Auditing;
using Strg.Core.Domain;
using Strg.Core.Exceptions;
using Strg.Core.Storage;
using Strg.Infrastructure.Auditing;
using Strg.Infrastructure.Data;
using Strg.Infrastructure.Storage;
using Testcontainers.PostgreSql;
using Xunit;

namespace Strg.Integration.Tests.Application.Drives;

internal sealed class FixedTenantContext(Guid id) : ITenantContext
{
    public Guid TenantId => id;
}

internal sealed class MutableCurrentUser : ICurrentUser
{
    public Guid UserId { get; set; }
}

/// <summary>
/// STRG-300 integration tests for the per-user default drive feature. Each TC-* method pins one
/// row of the issue's Test Cases section. Mediator is built the same way <c>TagHandlerTests</c>
/// does — Testcontainers Postgres, real EF, real query filters — so a refactor that disables
/// the tenant filter for <c>UserDriveDefault</c> or <c>Drive</c> trips the cross-tenant test.
/// </summary>
public sealed class DriveDefaultHandlerTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    // TC-001
    [Fact]
    public async Task CreateDrive_first_drive_in_tenant_is_marked_default_automatically()
    {
        var fx = await CreateFixtureAsync();
        var user = await fx.CreateUserAsync();
        var mediator = fx.BuildMediator(user.Id);

        var result = await mediator.Send(new CreateDriveCommand("first", "local"));

        result.IsSuccess.Should().BeTrue();
        result.Value!.IsDefault.Should().BeTrue("the first drive in a tenant becomes the bootstrap default");
    }

    // TC-002
    [Fact]
    public async Task CreateDrive_second_drive_does_not_steal_default_from_first()
    {
        var fx = await CreateFixtureAsync();
        var user = await fx.CreateUserAsync();
        var mediator = fx.BuildMediator(user.Id);

        var first = await mediator.Send(new CreateDriveCommand("first", "local"));
        var second = await mediator.Send(new CreateDriveCommand("second", "local"));

        first.Value!.IsDefault.Should().BeTrue();
        second.Value!.IsDefault.Should().BeFalse("only the first drive auto-defaults; subsequent creates start non-default");

        // Re-read first from DB to confirm the persisted state still has it as default.
        await using var db = fx.NewDbContext();
        var firstFromDb = await db.Drives.FirstAsync(d => d.Id == first.Value.Id);
        firstFromDb.IsDefault.Should().BeTrue();
    }

    [Fact]
    public async Task CreateDrive_explicit_isDefault_true_overrides_first_drive_heuristic()
    {
        // Admin override path: when IsDefault is passed explicitly, the auto-default heuristic
        // is bypassed in both directions. Prevents a future change to the heuristic from
        // silently flipping the admin-supplied value.
        var fx = await CreateFixtureAsync();
        var user = await fx.CreateUserAsync();
        var mediator = fx.BuildMediator(user.Id);

        var explicitFalse = await mediator.Send(new CreateDriveCommand("first", "local", IsDefault: false));
        explicitFalse.Value!.IsDefault.Should().BeFalse(
            "explicit false on the first drive must NOT be auto-flipped to true");
    }

    // TC-003
    [Fact]
    public async Task SetDefaultDrive_creates_user_drive_default_row_and_updates_on_re_invoke()
    {
        var fx = await CreateFixtureAsync();
        var user = await fx.CreateUserAsync();
        var mediator = fx.BuildMediator(user.Id);

        var first = await mediator.Send(new CreateDriveCommand("first", "local"));
        var second = await mediator.Send(new CreateDriveCommand("second", "local"));

        var pickFirst = await mediator.Send(new SetDefaultDriveCommand(first.Value!.Id));
        pickFirst.IsSuccess.Should().BeTrue();
        pickFirst.Value!.Id.Should().Be(first.Value.Id);

        await using (var db = fx.NewDbContext())
        {
            var rows = await db.UserDriveDefaults.Where(u => u.UserId == user.Id).ToListAsync();
            rows.Should().HaveCount(1);
            rows[0].DriveId.Should().Be(first.Value.Id);
        }

        // Switch to the second drive — must reuse the same row, never create a duplicate.
        var pickSecond = await mediator.Send(new SetDefaultDriveCommand(second.Value!.Id));
        pickSecond.IsSuccess.Should().BeTrue();

        await using (var db = fx.NewDbContext())
        {
            var rows = await db.UserDriveDefaults.Where(u => u.UserId == user.Id).ToListAsync();
            rows.Should().HaveCount(1, "upsert must reuse the existing (TenantId, UserId) row");
            rows[0].DriveId.Should().Be(second.Value.Id);
        }
    }

    [Fact]
    public async Task SetDefaultDrive_same_drive_twice_is_a_noop_and_does_not_double_audit()
    {
        var fx = await CreateFixtureAsync();
        var user = await fx.CreateUserAsync();
        var mediator = fx.BuildMediator(user.Id);

        var first = await mediator.Send(new CreateDriveCommand("first", "local"));

        await mediator.Send(new SetDefaultDriveCommand(first.Value!.Id));
        await mediator.Send(new SetDefaultDriveCommand(first.Value.Id));

        var audits = await fx.LoadAuditEntriesAsync(AuditActions.DriveDefaultChanged);
        audits.Should().HaveCount(1, "second invocation with same drive id is a no-op; no second audit row");
    }

    // TC-004
    [Fact]
    public async Task GetDefaultDrive_returns_null_when_tenant_has_no_drives()
    {
        var fx = await CreateFixtureAsync();
        var user = await fx.CreateUserAsync();
        var mediator = fx.BuildMediator(user.Id);

        var result = await mediator.Send(new GetDefaultDriveQuery());

        result.Should().BeNull("no drives, no user preference — fallback chain produces null");
    }

    [Fact]
    public async Task GetDefaultDrive_falls_back_to_tenant_default_when_user_has_no_preference()
    {
        var fx = await CreateFixtureAsync();
        var user = await fx.CreateUserAsync();
        var mediator = fx.BuildMediator(user.Id);

        var first = await mediator.Send(new CreateDriveCommand("first", "local"));

        var result = await mediator.Send(new GetDefaultDriveQuery());

        result.Should().NotBeNull();
        result!.Id.Should().Be(first.Value!.Id, "tenant default is the fallback when no UserDriveDefault row exists");
    }

    [Fact]
    public async Task GetDefaultDrive_returns_user_preference_when_set()
    {
        var fx = await CreateFixtureAsync();
        var user = await fx.CreateUserAsync();
        var mediator = fx.BuildMediator(user.Id);

        // First drive is auto-tenant-default; user picks the SECOND drive as their personal default.
        var first = await mediator.Send(new CreateDriveCommand("first", "local"));
        var second = await mediator.Send(new CreateDriveCommand("second", "local"));
        await mediator.Send(new SetDefaultDriveCommand(second.Value!.Id));

        var result = await mediator.Send(new GetDefaultDriveQuery());

        result.Should().NotBeNull();
        result!.Id.Should().Be(second.Value.Id, "user preference takes precedence over tenant default");
        first.Value!.IsDefault.Should().BeTrue("tenant default flag is unchanged by per-user preference selection");
    }

    // TC-005
    [Fact]
    public async Task SetDefaultDrive_cross_tenant_drive_id_throws_NotFound()
    {
        var fxA = await CreateFixtureAsync();
        var userA = await fxA.CreateUserAsync();
        var mediatorA = fxA.BuildMediator(userA.Id);

        var driveA = await mediatorA.Send(new CreateDriveCommand("first", "local"));

        // Tenant B tries to set tenant A's drive as their default. The global tenant filter
        // hides driveA from B's query, so the handler observes "drive not found" and throws
        // NotFoundException — which the GraphQL error filter renders as NOT_FOUND.
        var fxB = await fxA.WithNewTenantAsync();
        var userB = await fxB.CreateUserAsync();
        var mediatorB = fxB.BuildMediator(userB.Id);

        var act = async () => await mediatorB.Send(new SetDefaultDriveCommand(driveA.Value!.Id));

        await act.Should().ThrowAsync<NotFoundException>();

        // Belt-and-braces: confirm no UserDriveDefault row was created under either tenant for
        // tenant A's drive id.
        await using var db = fxA.NewDbContext();
        var leakedRows = await db.UserDriveDefaults
            .IgnoreQueryFilters()
            .Where(u => u.DriveId == driveA.Value.Id)
            .ToListAsync();
        leakedRows.Should().BeEmpty("a NotFound throw must abort the SaveChanges before any row hits the DB");
    }

    // ── fixture helpers ────────────────────────────────────────────────────────

    private async Task<Fixture> CreateFixtureAsync()
    {
        var dbName = $"strg_test_{Guid.NewGuid():N}";
        var adminConnectionString = _postgres.GetConnectionString();

        await using (var connection = new NpgsqlConnection(adminConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE \"{dbName}\"";
            await command.ExecuteNonQueryAsync();
        }

        var testDbConnectionString = new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            Database = dbName,
        }.ConnectionString;

        var tenantId = Guid.NewGuid();
        var tenantContext = new FixedTenantContext(tenantId);

        var options = new DbContextOptionsBuilder<StrgDbContext>()
            .UseNpgsql(testDbConnectionString)
            .Options;

        await using (var bootstrap = new StrgDbContext(options, tenantContext, new MutableCurrentUser { UserId = Guid.Empty }))
        {
            await bootstrap.Database.EnsureCreatedAsync();
            bootstrap.Tenants.Add(new Tenant { Id = tenantId, Name = $"test-{tenantId:N}" });
            await bootstrap.SaveChangesAsync();
        }

        return new Fixture(testDbConnectionString, tenantContext, tenantId);
    }

    private sealed class Fixture(
        string connectionString,
        ITenantContext tenantContext,
        Guid tenantId)
    {
        public Guid TenantId { get; } = tenantId;

        public StrgDbContext NewDbContext() => new(
            new DbContextOptionsBuilder<StrgDbContext>().UseNpgsql(connectionString).Options,
            tenantContext,
            new MutableCurrentUser { UserId = Guid.Empty });

        public IMediator BuildMediator(Guid currentUserId)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(tenantContext);
            services.AddSingleton<ICurrentUser>(new MutableCurrentUser { UserId = currentUserId });
            services.AddDbContext<StrgDbContext>(o => o.UseNpgsql(connectionString));
            services.AddScoped<IStrgDbContext>(sp => sp.GetRequiredService<StrgDbContext>());
            services.AddScoped<IAuditService, AuditService>();
            services.AddStrgStorageProviders();
            services.AddStrgApplication();

            return services.BuildServiceProvider().GetRequiredService<IMediator>();
        }

        public async Task<User> CreateUserAsync()
        {
            await using var ctx = NewDbContext();
            var user = new User
            {
                TenantId = TenantId,
                Email = $"drive-{Guid.NewGuid():N}@example.com",
                DisplayName = "Drive Test",
                PasswordHash = "not-a-real-hash-tests-only",
                QuotaBytes = 1_000_000,
            };
            ctx.Users.Add(user);
            await ctx.SaveChangesAsync();
            return user;
        }

        public async Task<IReadOnlyList<AuditEntry>> LoadAuditEntriesAsync(string action)
        {
            await using var ctx = NewDbContext();
            return await ctx.AuditEntries
                .Where(a => a.Action == action)
                .OrderBy(a => a.PerformedAt)
                .ToListAsync();
        }

        public async Task<Fixture> WithNewTenantAsync()
        {
            var newTenantId = Guid.NewGuid();
            var newTenantContext = new FixedTenantContext(newTenantId);
            var options = new DbContextOptionsBuilder<StrgDbContext>().UseNpgsql(connectionString).Options;
            await using var ctx = new StrgDbContext(options, newTenantContext, new MutableCurrentUser { UserId = Guid.Empty });
            ctx.Tenants.Add(new Tenant { Id = newTenantId, Name = $"test-{newTenantId:N}" });
            await ctx.SaveChangesAsync();
            return new Fixture(connectionString, newTenantContext, newTenantId);
        }
    }
}
