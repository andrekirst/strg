using System.Diagnostics;
using FluentAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Strg.Core;
using Strg.Core.Domain;
using Strg.Core.Identity;
using Strg.Core.Services;
using Strg.Infrastructure.Data;
using Strg.Infrastructure.Identity;
using Strg.Infrastructure.Services;
using Testcontainers.PostgreSql;
using Xunit;

namespace Strg.Integration.Tests.Identity;

internal sealed class FixedTenantContext(Guid id) : ITenantContext
{
    public Guid TenantId => id;
}

/// <summary>
/// No-op <see cref="IPublishEndpoint"/> for UserManager tests that are not asserting on the
/// event-publish behavior — the event-driven cache invalidation is covered by a dedicated
/// integration test that spins the real MassTransit pipeline and flushes via
/// <see cref="IOutboxFlusher"/>. These tests care about password business logic only.
/// </summary>
internal sealed class NoopPublishEndpoint : IPublishEndpoint
{
    public Task Publish<T>(T message, CancellationToken cancellationToken = default) where T : class
        => Task.CompletedTask;
    public Task Publish<T>(T message, IPipe<PublishContext<T>> pipe, CancellationToken cancellationToken = default) where T : class
        => Task.CompletedTask;
    public Task Publish<T>(T message, IPipe<PublishContext> pipe, CancellationToken cancellationToken = default) where T : class
        => Task.CompletedTask;
    public Task Publish(object message, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
    public Task Publish(object message, IPipe<PublishContext> pipe, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
    public Task Publish(object message, Type messageType, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
    public Task Publish(object message, Type messageType, IPipe<PublishContext> pipe, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
    public Task Publish<T>(object values, CancellationToken cancellationToken = default) where T : class
        => Task.CompletedTask;
    public Task Publish<T>(object values, IPipe<PublishContext<T>> pipe, CancellationToken cancellationToken = default) where T : class
        => Task.CompletedTask;
    public Task Publish<T>(object values, IPipe<PublishContext> pipe, CancellationToken cancellationToken = default) where T : class
        => Task.CompletedTask;
    public ConnectHandle ConnectPublishObserver(IPublishObserver observer)
        => throw new NotSupportedException();
}

public sealed class UserManagerTests : IAsyncLifetime
{
    // PBKDF2 verify with 310k iterations is ~50-150ms on typical hardware. The floor proves the
    // dummy verify ran (vs a short-circuit that would complete in microseconds). If this flakes on
    // slow CI, lower the floor — never raise it, since raising makes the timing oracle test
    // stricter than the code guarantees.
    private const long DummyVerifyMinElapsedMs = 30;
    private const string ValidPassword = "correct-horse-battery";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    // TC-001
    [Fact]
    public async Task CreateUserAsync_persists_user_with_hashed_password_when_input_is_valid()
    {
        var fx = await CreateFixtureAsync();
        var request = new CreateUserRequest(
            TenantId: fx.TenantId,
            Email: "alice@example.com",
            DisplayName: "Alice",
            Password: ValidPassword);

        var result = await fx.Manager.CreateUserAsync(request);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.Email.Should().Be("alice@example.com");
        result.Value.DisplayName.Should().Be("Alice");
        result.Value.PasswordHash.Should().NotBe(ValidPassword);
        result.Value.PasswordHash.Should().NotBeNullOrWhiteSpace();

        await using var verifyDb = fx.NewDbContext();
        var persisted = await verifyDb.Users.FirstOrDefaultAsync(u => u.Id == result.Value.Id);
        persisted.Should().NotBeNull();
        persisted!.PasswordHash.Should().Be(result.Value.PasswordHash);
        persisted.Role.Should().Be(UserRole.User);
    }

    // TC-002
    [Fact]
    public async Task CreateUserAsync_returns_EmailAlreadyExists_when_email_duplicates_in_same_tenant()
    {
        var fx = await CreateFixtureAsync();
        var first = await fx.Manager.CreateUserAsync(new CreateUserRequest(
            fx.TenantId, "dup@example.com", "First", ValidPassword));
        first.IsSuccess.Should().BeTrue();

        var second = await fx.Manager.CreateUserAsync(new CreateUserRequest(
            fx.TenantId, "dup@example.com", "Second", ValidPassword));

        second.IsFailure.Should().BeTrue();
        second.ErrorCode.Should().Be(UserManagerErrors.EmailAlreadyExists);
    }

    // TC-003
    [Fact]
    public async Task ValidatePasswordAsync_returns_true_when_password_is_correct()
    {
        var fx = await CreateFixtureAsync();
        var user = await CreateUserOrThrowAsync(fx, "bob@example.com");

        var ok = await fx.Manager.ValidatePasswordAsync(user.Id, ValidPassword);

        ok.Should().BeTrue();
    }

    // TC-004
    [Fact]
    public async Task ValidateCredentialsAsync_returns_null_and_increments_failures_when_password_is_wrong()
    {
        var fx = await CreateFixtureAsync();
        var user = await CreateUserOrThrowAsync(fx, "carol@example.com");

        var outcome = await fx.Manager.ValidateCredentialsAsync(user.Email, "wrong-password!!");

        outcome.Should().BeNull();
        var reloaded = await fx.ReloadUserAsync(user.Id);
        reloaded.FailedLoginAttempts.Should().Be(1);
        reloaded.LockedUntil.Should().BeNull();
    }

    // TC-005
    [Fact]
    public async Task RecordFailedLoginAsync_locks_account_for_fifteen_minutes_after_five_consecutive_failures()
    {
        var fx = await CreateFixtureAsync();
        var user = await CreateUserOrThrowAsync(fx, "dave@example.com");

        var expectedLockStart = DateTimeOffset.UtcNow;
        for (var i = 0; i < 5; i++)
        {
            await fx.Manager.RecordFailedLoginAsync(user.Id);
        }

        var reloaded = await fx.ReloadUserAsync(user.Id);
        reloaded.FailedLoginAttempts.Should().Be(5);
        reloaded.LockedUntil.Should().NotBeNull();
        reloaded.IsLocked.Should().BeTrue();
        reloaded.LockedUntil!.Value.Should()
            .BeCloseTo(expectedLockStart + TimeSpan.FromMinutes(15), TimeSpan.FromSeconds(5));
    }

    // TC-006
    [Fact]
    public async Task ValidatePasswordAsync_returns_false_when_account_is_locked_even_with_correct_password()
    {
        var fx = await CreateFixtureAsync();
        var user = await CreateUserOrThrowAsync(fx, "eve@example.com");

        for (var i = 0; i < 5; i++)
        {
            await fx.Manager.RecordFailedLoginAsync(user.Id);
        }

        var ok = await fx.Manager.ValidatePasswordAsync(user.Id, ValidPassword);

        ok.Should().BeFalse();
    }

    // TC-007
    [Fact]
    public async Task CreateUserAsync_returns_PasswordTooShort_when_password_below_minimum_length()
    {
        var fx = await CreateFixtureAsync();
        var shortPassword = new string('a', UserManagerErrors.MinimumPasswordLength - 1);

        var result = await fx.Manager.CreateUserAsync(new CreateUserRequest(
            fx.TenantId, "short@example.com", "Short", shortPassword));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(UserManagerErrors.PasswordTooShort);

        await using var verifyDb = fx.NewDbContext();
        var existsAnyway = await verifyDb.Users.AnyAsync(u => u.Email == "short@example.com");
        existsAnyway.Should().BeFalse();
    }

    // #8 — counter increments while locked (so the long tier is reachable in one attack burst),
    // but lock-EXTENSION is capped: only the EXACT threshold transitions (count == 5, count == 10)
    // set LockedUntil. Past 10, the counter keeps growing but LockedUntil is not re-applied —
    // that's how we prevent the indefinite-DoS-via-lock-extension vector while still allowing the
    // 10-failure cumulative tier to fire.
    [Fact]
    public async Task RecordFailedLoginAsync_increments_counter_while_locked_but_caps_lock_at_one_hour()
    {
        var fx = await CreateFixtureAsync();
        var user = await CreateUserOrThrowAsync(fx, "frank@example.com");

        for (var i = 0; i < 5; i++)
        {
            await fx.Manager.RecordFailedLoginAsync(user.Id);
        }

        var beforeLongTier = DateTimeOffset.UtcNow;
        for (var i = 0; i < 5; i++)
        {
            await fx.Manager.RecordFailedLoginAsync(user.Id);
        }
        var atLongTier = await fx.ReloadUserAsync(user.Id);
        var lockedUntilAtLongTier = atLongTier.LockedUntil;
        atLongTier.FailedLoginAttempts.Should().Be(10);
        lockedUntilAtLongTier.Should().NotBeNull();
        lockedUntilAtLongTier!.Value.Should()
            .BeCloseTo(beforeLongTier + TimeSpan.FromHours(1), TimeSpan.FromSeconds(5));

        for (var i = 0; i < 5; i++)
        {
            await fx.Manager.RecordFailedLoginAsync(user.Id);
        }

        var reloaded = await fx.ReloadUserAsync(user.Id);
        reloaded.FailedLoginAttempts.Should().Be(15);
        reloaded.LockedUntil.Should().Be(lockedUntilAtLongTier,
            "lock window must not extend past the 1h tier — that would be indefinite-DoS");
    }

    // #9 — long-tier transition: 10 consecutive failures → 1h lock. The counter increments past
    // the short tier's lock so we can walk straight from a fresh user to count=10 via 10
    // RecordFailedLoginAsync calls; no need to seed state.
    [Fact]
    public async Task RecordFailedLoginAsync_locks_account_for_one_hour_after_ten_consecutive_failures()
    {
        var fx = await CreateFixtureAsync();
        var user = await CreateUserOrThrowAsync(fx, "grace@example.com");

        var expectedLockStart = DateTimeOffset.UtcNow;
        for (var i = 0; i < 10; i++)
        {
            await fx.Manager.RecordFailedLoginAsync(user.Id);
        }

        var reloaded = await fx.ReloadUserAsync(user.Id);
        reloaded.FailedLoginAttempts.Should().Be(10);
        reloaded.LockedUntil.Should().NotBeNull();
        reloaded.LockedUntil!.Value.Should()
            .BeCloseTo(expectedLockStart + TimeSpan.FromHours(1), TimeSpan.FromSeconds(5));
    }

    // #10 — counter past 10 does NOT re-apply the lock. Seed at count=10 with an existing 1h
    // lock, take one more failure: count goes to 11, but LockedUntil stays at the original value
    // — the `==` threshold check in ApplyFailedLoginAsync only fires at exactly 5 and 10, not at
    // any count above. Proves there is no rolling extension of the 1h window per failure, which
    // would be the indefinite-DoS vector.
    [Fact]
    public async Task RecordFailedLoginAsync_does_not_extend_lock_when_counter_is_already_past_long_tier()
    {
        var fx = await CreateFixtureAsync();
        var user = await CreateUserOrThrowAsync(fx, "henry@example.com");
        var existingLock = DateTimeOffset.UtcNow + TimeSpan.FromHours(1);
        await fx.SetLockoutStateAsync(user.Id, failedAttempts: 10, lockedUntil: existingLock);

        await fx.Manager.RecordFailedLoginAsync(user.Id);

        var reloaded = await fx.ReloadUserAsync(user.Id);
        reloaded.FailedLoginAttempts.Should().Be(11);
        reloaded.LockedUntil.Should().NotBeNull();
        reloaded.LockedUntil!.Value.Should()
            .BeCloseTo(existingLock, TimeSpan.FromMilliseconds(100),
                "fail #11 must not bump the lock window — 1h is the cap");
    }

    // #11 — natural expiry resets counter then lets the new failure increment to 1
    [Fact]
    public async Task ValidateCredentialsAsync_resets_counter_on_natural_lock_expiry_before_counting_new_failure()
    {
        var fx = await CreateFixtureAsync();
        var user = await CreateUserOrThrowAsync(fx, "ivy@example.com");
        await fx.SetLockoutStateAsync(user.Id, failedAttempts: 5, lockedUntil: DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1));

        var outcome = await fx.Manager.ValidateCredentialsAsync(user.Email, "wrong-password!!");

        outcome.Should().BeNull();
        var reloaded = await fx.ReloadUserAsync(user.Id);
        // Expired lock → counter reset to 0 → wrong password increments to 1. Lock stays null
        // because 1 is below both the 5- and 10-failure thresholds.
        reloaded.FailedLoginAttempts.Should().Be(1);
        reloaded.LockedUntil.Should().BeNull();
    }

    // #12 — InvalidQuota
    [Fact]
    public async Task CreateUserAsync_returns_InvalidQuota_when_quota_is_negative()
    {
        var fx = await CreateFixtureAsync();

        var result = await fx.Manager.CreateUserAsync(new CreateUserRequest(
            fx.TenantId, "neg@example.com", "Neg", ValidPassword, QuotaBytes: -1));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(UserManagerErrors.InvalidQuota);
    }

    // #13 — concurrent race on unique (tenant, email)
    [Fact]
    public async Task CreateUserAsync_returns_EmailAlreadyExists_when_concurrent_inserts_race_on_same_email()
    {
        var fx = await CreateFixtureAsync();
        var secondManager = fx.NewManager();

        var request1 = new CreateUserRequest(fx.TenantId, "race@example.com", "One", ValidPassword);
        var request2 = new CreateUserRequest(fx.TenantId, "race@example.com", "Two", ValidPassword);

        var task1 = Task.Run(() => fx.Manager.CreateUserAsync(request1));
        var task2 = Task.Run(() => secondManager.CreateUserAsync(request2));

        var results = await Task.WhenAll(task1, task2);

        results.Count(r => r.IsSuccess).Should().Be(1,
            "exactly one insert survives the UNIQUE(tenant_id, email) index");
        var failure = results.Single(r => r.IsFailure);
        failure.ErrorCode.Should().Be(UserManagerErrors.EmailAlreadyExists,
            "the pre-check + DbUpdateException 23505 handler must translate the race into a Result, not bubble a 500");
    }

    // #14 — unknown email path triggers the dummy PBKDF2 verify (timing equalisation)
    [Fact]
    public async Task ValidateCredentialsAsync_with_unknown_email_returns_null_and_runs_dummy_verify()
    {
        var fx = await CreateFixtureAsync();
        // Prime the Lazy<string> dummy hash so the first call's one-time hash computation is not
        // conflated with the verify elapsed time we care about.
        await fx.Manager.ValidateCredentialsAsync("primer@example.com", "any-password-11");

        var stopwatch = Stopwatch.StartNew();
        var outcome = await fx.Manager.ValidateCredentialsAsync("unknown@example.com", "anything-goes");
        stopwatch.Stop();

        outcome.Should().BeNull();
        stopwatch.ElapsedMilliseconds.Should().BeGreaterThan(DummyVerifyMinElapsedMs,
            $"the missing-user path must run a real PBKDF2 verify against the dummy hash to defeat timing-based account enumeration (measured {stopwatch.ElapsedMilliseconds}ms)");
    }

    // #15 — locked account with the correct password also runs the dummy verify
    [Fact]
    public async Task ValidateCredentialsAsync_with_locked_account_and_correct_password_runs_dummy_verify()
    {
        var fx = await CreateFixtureAsync();
        var user = await CreateUserOrThrowAsync(fx, "locked@example.com");
        await fx.SetLockoutStateAsync(user.Id, failedAttempts: 5, lockedUntil: DateTimeOffset.UtcNow + TimeSpan.FromMinutes(15));
        // Prime the dummy hash (same reason as #14).
        await fx.Manager.ValidateCredentialsAsync("primer@example.com", "any-password-11");

        var stopwatch = Stopwatch.StartNew();
        var outcome = await fx.Manager.ValidateCredentialsAsync(user.Email, ValidPassword);
        stopwatch.Stop();

        outcome.Should().BeNull();
        stopwatch.ElapsedMilliseconds.Should().BeGreaterThan(DummyVerifyMinElapsedMs,
            $"the locked-branch must run a dummy PBKDF2 verify so lock state cannot be probed via wall-clock timing (measured {stopwatch.ElapsedMilliseconds}ms)");
    }

    // #16 — first-run seed on empty DB
    [Fact]
    public async Task FirstRunInitializationService_seeds_default_tenant_and_super_admin_and_prints_password_once()
    {
        var (options, connectionString) = await CreateFreshDatabaseWithConnectionStringAsync();
        var services = BuildFirstRunServiceProvider(connectionString);
        await using var _ = services;

        using var capturedStdout = new StringWriter();
        var originalOut = Console.Out;
        Console.SetOut(capturedStdout);
        try
        {
            var seeder = new FirstRunInitializationService(services);
            await seeder.StartAsync(CancellationToken.None);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        await using var ctx = new StrgDbContext(options, new FixedTenantContext(Guid.Empty));
        var tenants = await ctx.Tenants.IgnoreQueryFilters().ToListAsync();
        tenants.Should().ContainSingle().Which.Name.Should().Be("default");

        var admins = await ctx.Users.IgnoreQueryFilters().ToListAsync();
        admins.Should().ContainSingle();
        var admin = admins.Single();
        admin.Email.Should().Be("admin@strg.local");
        admin.Role.Should().Be(UserRole.SuperAdmin);
        admin.TenantId.Should().Be(tenants.Single().Id);
        admin.PasswordHash.Should().NotBeNullOrWhiteSpace();

        var stdout = capturedStdout.ToString();
        stdout.Should().Contain("SuperAdmin account created");
        stdout.Should().Contain("admin@strg.local");
        stdout.Should().Contain("Password:");
        // "shown ONCE" — assert the header line appears exactly once, not repeated.
        CountSubstring(stdout, "SuperAdmin account created").Should().Be(1);
    }

    // #17 — first-run is a no-op when any user already exists
    [Fact]
    public async Task FirstRunInitializationService_is_noop_when_users_already_exist()
    {
        var (options, connectionString) = await CreateFreshDatabaseWithConnectionStringAsync();
        var preexistingTenantId = Guid.NewGuid();
        await using (var ctx = new StrgDbContext(options, new FixedTenantContext(preexistingTenantId)))
        {
            ctx.Tenants.Add(new Tenant { Id = preexistingTenantId, Name = "preexisting" });
            ctx.Users.Add(new User
            {
                TenantId = preexistingTenantId,
                Email = "existing@example.com",
                DisplayName = "Existing",
                PasswordHash = new Pbkdf2PasswordHasher().Hash(ValidPassword),
                Role = UserRole.User,
            });
            await ctx.SaveChangesAsync();
        }

        var services = BuildFirstRunServiceProvider(connectionString);
        await using var _ = services;

        using var capturedStdout = new StringWriter();
        var originalOut = Console.Out;
        Console.SetOut(capturedStdout);
        try
        {
            var seeder = new FirstRunInitializationService(services);
            await seeder.StartAsync(CancellationToken.None);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        await using var verifyDb = new StrgDbContext(options, new FixedTenantContext(preexistingTenantId));
        var users = await verifyDb.Users.IgnoreQueryFilters().ToListAsync();
        users.Should().ContainSingle();
        users[0].Email.Should().Be("existing@example.com");

        var tenants = await verifyDb.Tenants.IgnoreQueryFilters().ToListAsync();
        tenants.Should().ContainSingle();

        capturedStdout.ToString().Should().NotContain("SuperAdmin account created");
    }

    // ── fixture helpers ────────────────────────────────────────────────────────

    private async Task<(DbContextOptions<StrgDbContext> Options, string ConnectionString)>
        CreateFreshDatabaseWithConnectionStringAsync()
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

        await using (var bootstrap = new StrgDbContext(options, new FixedTenantContext(Guid.Empty)))
        {
            await bootstrap.Database.EnsureCreatedAsync();
        }

        return (options, testDbConnectionString);
    }

    private async Task<Fixture> CreateFixtureAsync()
    {
        var (options, _) = await CreateFreshDatabaseWithConnectionStringAsync();
        var tenantId = Guid.NewGuid();
        var tenantContext = new FixedTenantContext(tenantId);

        await using (var bootstrap = new StrgDbContext(options, tenantContext))
        {
            bootstrap.Tenants.Add(new Tenant { Id = tenantId, Name = $"test-{tenantId:N}" });
            await bootstrap.SaveChangesAsync();
        }

        return new Fixture(options, tenantContext, tenantId);
    }

    private static async Task<User> CreateUserOrThrowAsync(Fixture fx, string email)
    {
        var result = await fx.Manager.CreateUserAsync(new CreateUserRequest(
            fx.TenantId, email, "Test User", ValidPassword));
        result.IsSuccess.Should().BeTrue($"test setup requires user {email} to be created");
        return result.Value!;
    }

    private static ServiceProvider BuildFirstRunServiceProvider(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext>(new FixedTenantContext(Guid.Empty));
        services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
        services.AddDbContext<StrgDbContext>(opts => opts.UseNpgsql(connectionString));
        return services.BuildServiceProvider();
    }

    private static int CountSubstring(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    private sealed class Fixture(
        DbContextOptions<StrgDbContext> options,
        ITenantContext tenantContext,
        Guid tenantId)
    {
        private readonly IPasswordHasher _hasher = new Pbkdf2PasswordHasher();

        public Guid TenantId { get; } = tenantId;

        // Each property access returns a UserManager bound to a fresh DbContext. A shared
        // DbContext would keep the first-loaded User entity in its identity map; subsequent reads
        // return that tracked instance regardless of the row's current state in the database, so
        // mutations applied via a side DbContext (SetLockoutStateAsync) would be invisible to
        // later UserManager calls. Building per-access keeps reads authoritative.
        public UserManager Manager => Build(NewDbContext());

        public StrgDbContext NewDbContext() => new(options, tenantContext);

        public UserManager NewManager() => Build(NewDbContext());

        public async Task<User> ReloadUserAsync(Guid userId)
        {
            await using var ctx = NewDbContext();
            var user = await ctx.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == userId);
            user.Should().NotBeNull($"user {userId} should exist for reload");
            return user!;
        }

        public async Task SetLockoutStateAsync(Guid userId, int failedAttempts, DateTimeOffset? lockedUntil)
        {
            await using var ctx = NewDbContext();
            var user = await ctx.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == userId);
            user.FailedLoginAttempts = failedAttempts;
            user.LockedUntil = lockedUntil;
            await ctx.SaveChangesAsync();
        }

        private UserManager Build(StrgDbContext db)
        {
            var repo = new UserRepository(db);
            return new UserManager(repo, _hasher, db, new NoopPublishEndpoint());
        }
    }
}
