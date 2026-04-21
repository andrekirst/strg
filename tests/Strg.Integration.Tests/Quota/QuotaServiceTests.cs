using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
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
    public async Task CheckAsync_for_missing_user_throws_QuotaExceeded()
    {
        // Missing user collapses into QuotaExceededException to avoid turning CheckAsync into
        // an enumeration oracle across tenants — admin paths use IQuotaAdminService.TryCommitAsync
        // when they legitimately need to distinguish "no such user" from "over quota".
        var fx = await CreateFixtureAsync();
        var service = fx.BuildService();

        var act = () => service.CheckAsync(Guid.NewGuid(), 1);

        await act.Should().ThrowAsync<QuotaExceededException>();
    }

    [Fact]
    public async Task CheckAsync_returns_false_for_long_MaxValue_request()
    {
        // Security-reviewer L1: additive form `UsedBytes + requiredBytes <= QuotaBytes` wraps to
        // a negative value on long.MaxValue and silently returns IsAllowed = true — a bypass of
        // the entire quota check. Subtractive form `requiredBytes <= QuotaBytes - UsedBytes` is
        // wrap-safe because both operands are non-negative. This test is the regression gate.
        var fx = await CreateFixtureAsync();
        var user = await fx.CreateUserAsync(quotaBytes: 1_000_000, usedBytes: 0);
        var service = fx.BuildService();

        var result = await service.CheckAsync(user.Id, long.MaxValue);

        result.IsAllowed.Should().BeFalse("no finite quota can accommodate long.MaxValue bytes");
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
    public async Task GetInfoAsync_for_missing_user_throws_QuotaExceeded()
    {
        // Same enumeration-oracle rationale as CheckAsync — missing user collapses into
        // QuotaExceededException uniformly across the IQuotaService surface.
        var fx = await CreateFixtureAsync();
        var service = fx.BuildService();

        var act = () => service.GetInfoAsync(Guid.NewGuid());

        await act.Should().ThrowAsync<QuotaExceededException>();
    }

    [Fact]
    public async Task GetInfoAsync_reports_overage_when_used_exceeds_quota()
    {
        // OverageBytes surfaces the unclamped gap so dashboards can render a red "over by N"
        // indicator without having to re-derive the math. UsagePercent stays clamped to [0, 100]
        // for pie-chart sanity — the two fields serve different UI surfaces.
        var fx = await CreateFixtureAsync();
        var user = await fx.CreateUserAsync(quotaBytes: 1_000, usedBytes: 1_210);
        var service = fx.BuildService();

        var info = await service.GetInfoAsync(user.Id);

        info.OverageBytes.Should().Be(210);
        info.FreeBytes.Should().Be(0);
        info.UsagePercent.Should().Be(100d);
    }

    [Fact]
    public async Task GetInfoAsync_reports_zero_overage_when_within_quota()
    {
        var fx = await CreateFixtureAsync();
        var user = await fx.CreateUserAsync(quotaBytes: 1_000, usedBytes: 250);
        var service = fx.BuildService();

        var info = await service.GetInfoAsync(user.Id);

        info.OverageBytes.Should().Be(0);
    }

    [Fact]
    public async Task Commit_then_simulated_write_failure_then_release_rolls_back_quota()
    {
        // Devils-advocate #1: pin the Commit-first contract. The correct upload sequence is
        // CommitAsync(n) → write blob → on write-failure, ReleaseAsync(n). This test simulates
        // the write failure and asserts the Release restores UsedBytes to its pre-Commit value.
        // If a future refactor reorders this to Check → write → Commit, the race-safe budget gate
        // disappears — this test won't catch that directly, but it pins the rollback invariant
        // that the Commit-first pattern relies on.
        var fx = await CreateFixtureAsync();
        var user = await fx.CreateUserAsync(quotaBytes: 1_000_000, usedBytes: 200_000);
        var service = fx.BuildService();

        await service.CommitAsync(user.Id, 50_000);
        (await fx.ReloadUserAsync(user.Id)).UsedBytes.Should().Be(250_000, "Commit reserves atomically");

        // Simulated write-failure path: caller rolls back by releasing the reserved bytes.
        await service.ReleaseAsync(user.Id, 50_000);

        (await fx.ReloadUserAsync(user.Id)).UsedBytes.Should().Be(200_000,
            "Release after simulated write-failure must restore pre-Commit usage exactly");
    }

    [Fact]
    public async Task TryCommitAsync_returns_Success_when_user_exists_and_quota_suffices()
    {
        var fx = await CreateFixtureAsync();
        var user = await fx.CreateUserAsync(quotaBytes: 1_000_000, usedBytes: 0);
        var admin = fx.BuildAdminService();

        var outcome = await admin.TryCommitAsync(user.Id, 100);

        outcome.Should().Be(CommitOutcome.Success);
        (await fx.ReloadUserAsync(user.Id)).UsedBytes.Should().Be(100);
    }

    [Fact]
    public async Task TryCommitAsync_distinguishes_QuotaExceeded_from_UserNotInTenant()
    {
        // The entire purpose of IQuotaAdminService: a distinction that the main IQuotaService
        // cannot safely expose. Admin diagnostic tooling needs to tell "typo'd userId" from
        // "real over-quota" — here we pin that the follow-up existence probe works.
        var fx = await CreateFixtureAsync();
        var existingUser = await fx.CreateUserAsync(quotaBytes: 1_000, usedBytes: 900);

        var admin = fx.BuildAdminService();

        var overQuota = await admin.TryCommitAsync(existingUser.Id, 500);
        overQuota.Should().Be(CommitOutcome.QuotaExceeded);

        var missing = await admin.TryCommitAsync(Guid.NewGuid(), 1);
        missing.Should().Be(CommitOutcome.UserNotInTenant);
    }

    [Fact]
    public async Task TryCommitAsync_zero_bytes_probes_existence()
    {
        // Zero-byte Commits are no-ops on the storage path but retain the admin-distinguishing
        // value: tell existing from missing without charging any bytes.
        var fx = await CreateFixtureAsync();
        var user = await fx.CreateUserAsync(quotaBytes: 1_000, usedBytes: 0);
        var admin = fx.BuildAdminService();

        (await admin.TryCommitAsync(user.Id, 0)).Should().Be(CommitOutcome.Success);
        (await admin.TryCommitAsync(Guid.NewGuid(), 0)).Should().Be(CommitOutcome.UserNotInTenant);
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

    // Security-reviewer L2: pin tenant-filter propagation through ExecuteUpdateAsync. The global
    // filter adds an implicit `tenant_id = @currentTenant` clause to the WHERE, which collapses
    // cross-tenant Commit into "rows-affected = 0 → QuotaExceededException" (security-positive:
    // indistinguishable from legitimate quota-shortfall, no enumeration oracle) and cross-tenant
    // Release into a silent no-op. A future refactor that replaces ExecuteUpdateAsync with a
    // read-then-write service-layer loop would lose the filter binding without failing any
    // existing test — these two tests are the regression gate.
    [Fact]
    public async Task CommitAsync_from_tenant_B_on_user_in_tenant_A_throws_QuotaExceeded()
    {
        var fxA = await CreateFixtureAsync();
        var fxB = await fxA.WithNewTenantAsync();
        var userInA = await fxA.CreateUserAsync(quotaBytes: 1_000_000, usedBytes: 0);
        var serviceInB = fxB.BuildService();

        var act = () => serviceInB.CommitAsync(userInA.Id, 100);
        await act.Should().ThrowAsync<QuotaExceededException>();

        // The real test isn't the exception — it's that the foreign user's bytes didn't move.
        // A filter bypass would typically still return rows-affected >= 1, so the exception
        // alone is insufficient to catch it.
        var reloaded = await fxA.ReloadUserAsync(userInA.Id);
        reloaded.UsedBytes.Should().Be(0, "cross-tenant commit must not mutate the foreign-tenant user");
    }

    [Fact]
    public async Task ReleaseAsync_from_tenant_B_on_user_in_tenant_A_is_silent_noop()
    {
        var fxA = await CreateFixtureAsync();
        var fxB = await fxA.WithNewTenantAsync();
        var userInA = await fxA.CreateUserAsync(quotaBytes: 1_000_000, usedBytes: 500_000);
        var serviceInB = fxB.BuildService();

        // Release contract: rows-affected=0 is NOT an error — a legitimate orphan-reaper path
        // may call Release against a soft-deleted user and must not panic. That same silent-noop
        // contract extends to cross-tenant callers. The invariant under test is that the call
        // neither throws nor mutates state outside its tenant.
        await serviceInB.ReleaseAsync(userInA.Id, 100);

        var reloaded = await fxA.ReloadUserAsync(userInA.Id);
        reloaded.UsedBytes.Should().Be(500_000, "cross-tenant release must not mutate the foreign-tenant user");
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

        public IQuotaService BuildService() =>
            new QuotaService(NewDbContext(), tenantContext, NullLogger<QuotaService>.Instance);

        // QuotaService implements both IQuotaService and IQuotaAdminService. Admin callers
        // bind through the admin interface so the enumeration-oracle-unsafe outcomes never leak
        // to untrusted callers via the main interface — same instance, narrower surface.
        public IQuotaAdminService BuildAdminService() =>
            new QuotaService(NewDbContext(), tenantContext, NullLogger<QuotaService>.Instance);

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

        // Mint a SECOND tenant in the SAME database. Cross-tenant tests demand that service-B and
        // the foreign user live under one Postgres connection + schema, so the row is present but
        // filtered-out by the global tenant filter that piggybacks onto ExecuteUpdateAsync. Using
        // CreateFixtureAsync twice would put the two tenants in different databases, exercising
        // database isolation instead — a strictly stronger guarantee that hides the property we
        // actually want to pin (query-filter propagation through EF Core's bulk-update path).
        public async Task<Fixture> WithNewTenantAsync()
        {
            var newTenantId = Guid.NewGuid();
            var newTenantContext = new FixedTenantContext(newTenantId);
            await using var ctx = new StrgDbContext(options, newTenantContext);
            ctx.Tenants.Add(new Tenant { Id = newTenantId, Name = $"test-{newTenantId:N}" });
            await ctx.SaveChangesAsync();
            return new Fixture(options, newTenantContext, newTenantId);
        }
    }
}
