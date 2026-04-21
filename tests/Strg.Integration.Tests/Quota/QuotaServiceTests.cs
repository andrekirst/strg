using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Strg.Core.Domain;
using Strg.Core.Exceptions;
using Strg.Core.Services;
using Strg.Infrastructure.Data;
using Strg.Infrastructure.Services;
using Testcontainers.PostgreSql;
using Xunit;

namespace Strg.Integration.Tests.Quota;

internal sealed class FixedTenantContext(Guid id) : ITenantContext
{
    public Guid TenantId => id;
}

/// <summary>
/// Integration tests for <see cref="QuotaService"/> — every test below runs against a real
/// Postgres database (TestContainers) because the value the service adds over a tracked-entity
/// update is the <i>atomic</i> nature of the SQL UPDATE. An in-memory provider would happily
/// serialise two "concurrent" ExecuteUpdateAsync calls and every race test would pass trivially
/// while production races through the floor.
/// </summary>
public sealed class QuotaServiceTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    // TC-001
    [Fact]
    public async Task CheckAsync_returns_allowed_when_upload_fits_within_remaining_quota()
    {
        var fx = await CreateFixtureAsync();
        var user = await fx.CreateUserAsync(quotaBytes: 100 * 1024 * 1024, usedBytes: 90 * 1024 * 1024);
        var service = fx.BuildService();

        var result = await service.CheckAsync(user.Id, 5 * 1024 * 1024);

        result.IsAllowed.Should().BeTrue();
        result.Available.Should().Be(10 * 1024 * 1024);
        result.Quota.Should().Be(100 * 1024 * 1024);
        result.Used.Should().Be(90 * 1024 * 1024);
    }

    // TC-002
    [Fact]
    public async Task CheckAsync_returns_rejected_when_upload_exceeds_remaining_quota()
    {
        var fx = await CreateFixtureAsync();
        var user = await fx.CreateUserAsync(quotaBytes: 100 * 1024 * 1024, usedBytes: 90 * 1024 * 1024);
        var service = fx.BuildService();

        var result = await service.CheckAsync(user.Id, 15 * 1024 * 1024);

        result.IsAllowed.Should().BeFalse();
        result.Available.Should().Be(10 * 1024 * 1024);
    }

    [Fact]
    public async Task CheckAsync_for_missing_user_throws_NotFound()
    {
        var fx = await CreateFixtureAsync();
        var service = fx.BuildService();

        var act = () => service.CheckAsync(Guid.NewGuid(), 1);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    // TC-004
    [Fact]
    public async Task CommitAsync_increments_UsedBytes_atomically()
    {
        var fx = await CreateFixtureAsync();
        var user = await fx.CreateUserAsync(quotaBytes: 1_000_000, usedBytes: 200_000);
        var service = fx.BuildService();

        await service.CommitAsync(user.Id, 50_000);

        var reloaded = await fx.ReloadUserAsync(user.Id);
        reloaded.UsedBytes.Should().Be(250_000);
    }

    [Fact]
    public async Task CommitAsync_throws_QuotaExceeded_when_new_total_would_exceed_quota()
    {
        var fx = await CreateFixtureAsync();
        var user = await fx.CreateUserAsync(quotaBytes: 1_000_000, usedBytes: 950_000);
        var service = fx.BuildService();

        var act = () => service.CommitAsync(user.Id, 100_000);

        await act.Should().ThrowAsync<QuotaExceededException>();

        // Critical: used_bytes must NOT have moved. A partial-commit-then-rollback would leave
        // phantom usage that the next upload would then have to work around.
        var reloaded = await fx.ReloadUserAsync(user.Id);
        reloaded.UsedBytes.Should().Be(950_000);
    }

    [Fact]
    public async Task CommitAsync_zero_bytes_is_a_no_op()
    {
        var fx = await CreateFixtureAsync();
        var user = await fx.CreateUserAsync(quotaBytes: 1_000_000, usedBytes: 100_000);
        var service = fx.BuildService();

        await service.CommitAsync(user.Id, 0);

        var reloaded = await fx.ReloadUserAsync(user.Id);
        reloaded.UsedBytes.Should().Be(100_000);
    }

    [Fact]
    public async Task CommitAsync_throws_QuotaExceeded_when_user_does_not_exist()
    {
        // rows-affected = 0 for a non-existent userId collapses into "quota exceeded" — see the
        // code comment in QuotaService.CommitAsync for the rationale. A cross-tenant attempt
        // fails the same way because the global query filter excludes the foreign-tenant user.
        var fx = await CreateFixtureAsync();
        var service = fx.BuildService();

        var act = () => service.CommitAsync(Guid.NewGuid(), 1);

        await act.Should().ThrowAsync<QuotaExceededException>();
    }

    [Fact]
    public async Task CommitAsync_rejects_negative_bytes()
    {
        var fx = await CreateFixtureAsync();
        var user = await fx.CreateUserAsync(quotaBytes: 1_000, usedBytes: 0);
        var service = fx.BuildService();

        var act = () => service.CommitAsync(user.Id, -1);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    // TC-005
    [Fact]
    public async Task ReleaseAsync_decrements_UsedBytes()
    {
        var fx = await CreateFixtureAsync();
        var user = await fx.CreateUserAsync(quotaBytes: 1_000_000, usedBytes: 500_000);
        var service = fx.BuildService();

        await service.ReleaseAsync(user.Id, 200_000);

        var reloaded = await fx.ReloadUserAsync(user.Id);
        reloaded.UsedBytes.Should().Be(300_000);
    }

    [Fact]
    public async Task ReleaseAsync_clamps_at_zero_when_releasing_more_than_was_committed()
    {
        // Double-release, stale client size, or a miscounted orphan-reaper could request more
        // bytes than the user has committed. Production safety demands UsedBytes never goes
        // negative — a negative usage silently disables all subsequent quota enforcement.
        var fx = await CreateFixtureAsync();
        var user = await fx.CreateUserAsync(quotaBytes: 1_000_000, usedBytes: 100);
        var service = fx.BuildService();

        await service.ReleaseAsync(user.Id, 500);

        var reloaded = await fx.ReloadUserAsync(user.Id);
        reloaded.UsedBytes.Should().Be(0);
    }

    [Fact]
    public async Task ReleaseAsync_rejects_negative_bytes()
    {
        var fx = await CreateFixtureAsync();
        var user = await fx.CreateUserAsync(quotaBytes: 1_000, usedBytes: 100);
        var service = fx.BuildService();

        var act = () => service.ReleaseAsync(user.Id, -1);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task GetInfoAsync_returns_real_time_snapshot_with_clamped_usage_percent()
    {
        var fx = await CreateFixtureAsync();
        var user = await fx.CreateUserAsync(quotaBytes: 1_000, usedBytes: 250);
        var service = fx.BuildService();

        var info = await service.GetInfoAsync(user.Id);

        info.QuotaBytes.Should().Be(1_000);
        info.UsedBytes.Should().Be(250);
        info.FreeBytes.Should().Be(750);
        info.UsagePercent.Should().BeApproximately(25d, 0.0001);
    }

    [Fact]
    public async Task GetInfoAsync_clamps_UsagePercent_to_100_when_used_exceeds_quota()
    {
        // Can happen legitimately: admin lowers quota below current usage, or a race slipped a
        // commit past a stale Check. Dashboards rendering "121%" used confuse more than help.
        var fx = await CreateFixtureAsync();
        var user = await fx.CreateUserAsync(quotaBytes: 1_000, usedBytes: 1_210);
        var service = fx.BuildService();

        var info = await service.GetInfoAsync(user.Id);

        info.UsagePercent.Should().Be(100d);
        info.FreeBytes.Should().Be(0);
    }

    [Fact]
    public async Task GetInfoAsync_zero_quota_returns_zero_percent()
    {
        var fx = await CreateFixtureAsync();
        var user = await fx.CreateUserAsync(quotaBytes: 0, usedBytes: 0);
        var service = fx.BuildService();

        var info = await service.GetInfoAsync(user.Id);

        info.UsagePercent.Should().Be(0d);
        info.FreeBytes.Should().Be(0);
    }

    [Fact]
    public async Task GetInfoAsync_for_missing_user_throws_NotFound()
    {
        var fx = await CreateFixtureAsync();
        var service = fx.BuildService();

        var act = () => service.GetInfoAsync(Guid.NewGuid());

        await act.Should().ThrowAsync<NotFoundException>();
    }

    // TC-003 — the load-bearing concurrency test. If the SQL UPDATE is not atomic (or a refactor
    // later drops it in favour of a read-then-write), two parallel Commits that each pass their
    // own Check would both succeed, racing UsedBytes past QuotaBytes. The atomic
    // ExecuteUpdateAsync + WHERE clause guarantees exactly one survives.
    [Fact]
    public async Task Concurrent_commits_summing_past_quota_result_in_exactly_one_success()
    {
        var fx = await CreateFixtureAsync();
        // Quota 100 MiB, each commit 55 MiB — together would need 110 MiB, so one must fail.
        var user = await fx.CreateUserAsync(quotaBytes: 100 * 1024 * 1024, usedBytes: 0);

        // Each task gets its own DbContext so we're really racing at the DB connection layer,
        // not just contending on a single tracked-entity snapshot.
        var serviceA = fx.BuildService();
        var serviceB = fx.BuildService();

        var taskA = Task.Run(() => CommitOutcomeAsync(() => serviceA.CommitAsync(user.Id, 55 * 1024 * 1024)));
        var taskB = Task.Run(() => CommitOutcomeAsync(() => serviceB.CommitAsync(user.Id, 55 * 1024 * 1024)));

        var results = await Task.WhenAll(taskA, taskB);

        results.Count(ok => ok).Should().Be(1,
            "exactly one commit must survive — the atomic UPDATE ... WHERE used_bytes + @delta <= quota_bytes gate is the entire point of the service");
        results.Count(ok => !ok).Should().Be(1);

        var reloaded = await fx.ReloadUserAsync(user.Id);
        reloaded.UsedBytes.Should().Be(55 * 1024 * 1024);
    }

    private static async Task<bool> CommitOutcomeAsync(Func<Task> commit)
    {
        try
        {
            await commit();
            return true;
        }
        catch (QuotaExceededException)
        {
            return false;
        }
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

        var options = new DbContextOptionsBuilder<StrgDbContext>()
            .UseNpgsql(testDbConnectionString)
            .Options;

        var tenantId = Guid.NewGuid();
        var tenantContext = new FixedTenantContext(tenantId);

        await using (var bootstrap = new StrgDbContext(options, tenantContext))
        {
            await bootstrap.Database.EnsureCreatedAsync();
            bootstrap.Tenants.Add(new Tenant { Id = tenantId, Name = $"test-{tenantId:N}" });
            await bootstrap.SaveChangesAsync();
        }

        return new Fixture(options, tenantContext, tenantId);
    }

    private sealed class Fixture(
        DbContextOptions<StrgDbContext> options,
        ITenantContext tenantContext,
        Guid tenantId)
    {
        public Guid TenantId { get; } = tenantId;

        public StrgDbContext NewDbContext() => new(options, tenantContext);

        public IQuotaService BuildService() => new QuotaService(NewDbContext());

        public async Task<User> CreateUserAsync(long quotaBytes, long usedBytes)
        {
            await using var ctx = NewDbContext();
            var user = new User
            {
                TenantId = TenantId,
                Email = $"quota-{Guid.NewGuid():N}@example.com",
                DisplayName = "Quota Test",
                PasswordHash = "not-a-real-hash-tests-only",
                QuotaBytes = quotaBytes,
                UsedBytes = usedBytes,
            };
            ctx.Users.Add(user);
            await ctx.SaveChangesAsync();
            return user;
        }

        public async Task<User> ReloadUserAsync(Guid userId)
        {
            await using var ctx = NewDbContext();
            var user = await ctx.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == userId);
            user.Should().NotBeNull($"user {userId} should exist for reload");
            return user!;
        }
    }
}
