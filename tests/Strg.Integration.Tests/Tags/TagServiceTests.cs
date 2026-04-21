using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Strg.Core.Auditing;
using Strg.Core.Domain;
using Strg.Core.Exceptions;
using Strg.Core.Services;
using Strg.Infrastructure.Auditing;
using Strg.Infrastructure.Data;
using Strg.Infrastructure.Services;
using Testcontainers.PostgreSql;
using Xunit;

namespace Strg.Integration.Tests.Tags;

internal sealed class FixedTenantContext(Guid id) : ITenantContext
{
    public Guid TenantId => id;
}

/// <summary>
/// Integration tests for <see cref="TagService"/>. TestContainers Postgres because the case-fold
/// unique index, tenant-filter propagation, and audit-trail durability are things an in-memory
/// provider fakes faithfully enough to hide regressions. The key invariants pinned here:
/// upsert-on-same-key mutates instead of duplicating, missing-user/foreign-tenant collapses to
/// an empty result set (no cross-tenant leak), audit rows are emitted per state change, key
/// validation rejects disallowed characters, and RemoveAllAsync returns the affected count.
/// </summary>
public sealed class TagServiceTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    // TC-001
    [Fact]
    public async Task UpsertAsync_creates_tag_with_normalized_key_and_typed_value()
    {
        var fx = await CreateFixtureAsync();
        var (user, file) = await fx.CreateUserAndFileAsync();
        var service = fx.BuildService();

        var tag = await service.UpsertAsync(file.Id, user.Id, "Project", "acme", TagValueType.String);

        tag.Key.Should().Be("project", "the entity setter lowercases on init");
        tag.Value.Should().Be("acme");
        tag.ValueType.Should().Be(TagValueType.String);
        tag.FileId.Should().Be(file.Id);
        tag.UserId.Should().Be(user.Id);
        tag.TenantId.Should().Be(fx.TenantId);
    }

    // TC-002
    [Fact]
    public async Task UpsertAsync_same_key_twice_updates_value_and_does_not_duplicate()
    {
        var fx = await CreateFixtureAsync();
        var (user, file) = await fx.CreateUserAndFileAsync();
        var service = fx.BuildService();

        var first = await service.UpsertAsync(file.Id, user.Id, "project", "acme");
        var second = await service.UpsertAsync(file.Id, user.Id, "project", "globex");

        second.Id.Should().Be(first.Id, "upsert must reuse the existing row, not create a second");
        second.Value.Should().Be("globex");

        var all = await service.GetTagsAsync(file.Id, user.Id);
        all.Should().HaveCount(1);
    }

    // TC-003
    [Fact]
    public async Task RemoveAsync_on_missing_key_returns_false_and_emits_no_audit_row()
    {
        var fx = await CreateFixtureAsync();
        var (user, file) = await fx.CreateUserAndFileAsync();
        var service = fx.BuildService();

        var removed = await service.RemoveAsync(file.Id, user.Id, "nonexistent");

        removed.Should().BeFalse();

        // The state-change-only audit contract means a no-op Remove doesn't pollute the audit
        // trail. If a future refactor switches to "log intent, not effect" this test fails and
        // forces the discussion.
        (await fx.CountAuditEntriesAsync(AuditActions.TagRemoved)).Should().Be(0);
    }

    // TC-004
    [Fact]
    public async Task UpsertAsync_key_exceeding_255_chars_throws_ValidationException()
    {
        var fx = await CreateFixtureAsync();
        var (user, file) = await fx.CreateUserAndFileAsync();
        var service = fx.BuildService();

        var oversizedKey = new string('a', 256);
        var act = () => service.UpsertAsync(file.Id, user.Id, oversizedKey, "x");

        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task UpsertAsync_key_with_disallowed_characters_throws_ValidationException()
    {
        var fx = await CreateFixtureAsync();
        var (user, file) = await fx.CreateUserAndFileAsync();
        var service = fx.BuildService();

        // Path separator rules out filesystem-alike keys; space rules out log-injection payloads.
        foreach (var badKey in new[] { "has space", "has/slash", "has;semi", "has=equals" })
        {
            var act = () => service.UpsertAsync(file.Id, user.Id, badKey, "x");
            await act.Should().ThrowAsync<ValidationException>($"key '{badKey}' should be rejected");
        }
    }

    [Fact]
    public async Task UpsertAsync_empty_key_throws_ValidationException()
    {
        var fx = await CreateFixtureAsync();
        var (user, file) = await fx.CreateUserAndFileAsync();
        var service = fx.BuildService();

        var actEmpty = () => service.UpsertAsync(file.Id, user.Id, string.Empty, "x");
        await actEmpty.Should().ThrowAsync<ValidationException>();

        var actWhitespace = () => service.UpsertAsync(file.Id, user.Id, "   ", "x");
        await actWhitespace.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task UpsertAsync_value_exceeding_255_chars_throws_ValidationException()
    {
        var fx = await CreateFixtureAsync();
        var (user, file) = await fx.CreateUserAndFileAsync();
        var service = fx.BuildService();

        var oversizedValue = new string('v', 256);
        var act = () => service.UpsertAsync(file.Id, user.Id, "project", oversizedValue);

        await act.Should().ThrowAsync<ValidationException>();
    }

    // TC-005
    [Fact]
    public async Task UpsertAsync_case_mixed_key_matches_lowercase_on_subsequent_read()
    {
        var fx = await CreateFixtureAsync();
        var (user, file) = await fx.CreateUserAndFileAsync();
        var service = fx.BuildService();

        await service.UpsertAsync(file.Id, user.Id, "Project", "acme");

        var tags = await service.GetTagsAsync(file.Id, user.Id);
        tags.Should().HaveCount(1);
        tags[0].Key.Should().Be("project");

        // And re-upsert with a different casing collapses onto the same row.
        await service.UpsertAsync(file.Id, user.Id, "PROJECT", "globex");
        var after = await service.GetTagsAsync(file.Id, user.Id);
        after.Should().HaveCount(1);
        after[0].Value.Should().Be("globex");
    }

    [Fact]
    public async Task Two_users_on_same_file_have_independent_tag_records()
    {
        // User-scoping is a product invariant per STRG-046 — user A's "project=acme" does NOT
        // block user B from tagging the same file "project=foo". Without this test the unique
        // index could be silently changed to (FileId, Key) and every per-user tag would collide.
        var fx = await CreateFixtureAsync();
        var (userA, file) = await fx.CreateUserAndFileAsync();
        var userB = await fx.CreateUserAsync();
        var service = fx.BuildService();

        await service.UpsertAsync(file.Id, userA.Id, "project", "acme");
        await service.UpsertAsync(file.Id, userB.Id, "project", "globex");

        var a = await service.GetTagsAsync(file.Id, userA.Id);
        var b = await service.GetTagsAsync(file.Id, userB.Id);

        a.Should().HaveCount(1);
        a[0].Value.Should().Be("acme");
        b.Should().HaveCount(1);
        b[0].Value.Should().Be("globex");
    }

    [Fact]
    public async Task GetTagsAsync_does_not_return_other_users_tags_on_same_file()
    {
        var fx = await CreateFixtureAsync();
        var (userA, file) = await fx.CreateUserAndFileAsync();
        var userB = await fx.CreateUserAsync();
        var service = fx.BuildService();

        await service.UpsertAsync(file.Id, userA.Id, "project", "acme");
        await service.UpsertAsync(file.Id, userB.Id, "secret", "value");

        var bTags = await service.GetTagsAsync(file.Id, userB.Id);

        bTags.Should().HaveCount(1);
        bTags[0].Key.Should().Be("secret", "user B must not see user A's 'project' tag");
    }

    [Fact]
    public async Task UpsertAsync_emits_tag_assigned_audit_row()
    {
        var fx = await CreateFixtureAsync();
        var (user, file) = await fx.CreateUserAndFileAsync();
        var service = fx.BuildService();

        await service.UpsertAsync(file.Id, user.Id, "project", "acme", TagValueType.String);

        var audits = await fx.LoadAuditEntriesAsync(AuditActions.TagAssigned);
        audits.Should().HaveCount(1);
        var entry = audits[0];
        entry.ResourceType.Should().Be("FileItem");
        entry.ResourceId.Should().Be(file.Id);
        entry.UserId.Should().Be(user.Id);
        entry.TenantId.Should().Be(fx.TenantId);
        entry.Details.Should().Contain("key=project").And.Contain("value_type=string");
    }

    [Fact]
    public async Task RemoveAsync_emits_tag_removed_audit_row()
    {
        var fx = await CreateFixtureAsync();
        var (user, file) = await fx.CreateUserAndFileAsync();
        var service = fx.BuildService();
        await service.UpsertAsync(file.Id, user.Id, "project", "acme");

        var removed = await service.RemoveAsync(file.Id, user.Id, "Project");

        removed.Should().BeTrue();
        var audits = await fx.LoadAuditEntriesAsync(AuditActions.TagRemoved);
        audits.Should().HaveCount(1);
        audits[0].Details.Should().Contain("key=project");
    }

    [Fact]
    public async Task RemoveAllAsync_removes_every_tag_for_user_and_returns_count()
    {
        var fx = await CreateFixtureAsync();
        var (user, file) = await fx.CreateUserAndFileAsync();
        var service = fx.BuildService();

        await service.UpsertAsync(file.Id, user.Id, "project", "acme", TagValueType.String);
        await service.UpsertAsync(file.Id, user.Id, "priority", "3", TagValueType.Number);
        await service.UpsertAsync(file.Id, user.Id, "done", "false", TagValueType.Boolean);

        var removed = await service.RemoveAllAsync(file.Id, user.Id);

        removed.Should().Be(3);
        (await service.GetTagsAsync(file.Id, user.Id)).Should().BeEmpty();

        // Bulk remove emits a single audit row — the alternative (one row per tag) would flood
        // the audit stream on large files and obscure the product-level "clear tags" intent.
        var audits = await fx.LoadAuditEntriesAsync(AuditActions.TagRemoved);
        audits.Should().HaveCount(1);
        audits[0].Details.Should().Contain("bulk=true").And.Contain("count=3");
    }

    [Fact]
    public async Task RemoveAllAsync_on_file_with_no_tags_returns_zero_and_emits_no_audit()
    {
        var fx = await CreateFixtureAsync();
        var (user, file) = await fx.CreateUserAndFileAsync();
        var service = fx.BuildService();

        var removed = await service.RemoveAllAsync(file.Id, user.Id);

        removed.Should().Be(0);
        (await fx.CountAuditEntriesAsync(AuditActions.TagRemoved)).Should().Be(0);
    }

    [Fact]
    public async Task UpsertAsync_persists_all_three_TagValueTypes()
    {
        // CHECK constraint on ValueType column enforces `'string' | 'number' | 'boolean'` —
        // the round-trip through EF Core's value converter must agree with the constraint for
        // every enum value. Regression gate in case the converter's casing drifts.
        var fx = await CreateFixtureAsync();
        var (user, file) = await fx.CreateUserAndFileAsync();
        var service = fx.BuildService();

        await service.UpsertAsync(file.Id, user.Id, "s", "v", TagValueType.String);
        await service.UpsertAsync(file.Id, user.Id, "n", "42", TagValueType.Number);
        await service.UpsertAsync(file.Id, user.Id, "b", "true", TagValueType.Boolean);

        var tags = await service.GetTagsAsync(file.Id, user.Id);
        tags.Select(t => t.ValueType).Should().BeEquivalentTo(
            new[] { TagValueType.Boolean, TagValueType.Number, TagValueType.String });
    }

    [Fact]
    public async Task UpsertAsync_cross_tenant_does_not_mutate_foreign_tag()
    {
        // Cross-tenant security pin: even if a caller guessed a foreign fileId, the global query
        // filter hides the existing tag so UpsertAsync takes the Add branch, which fails the
        // FileItem foreign key because the foreign file is not visible. The invariant under test:
        // tenant-A's tag state is unchanged after tenant-B's attempt.
        var fxA = await CreateFixtureAsync();
        var (userA, fileA) = await fxA.CreateUserAndFileAsync();
        var serviceA = fxA.BuildService();
        await serviceA.UpsertAsync(fileA.Id, userA.Id, "project", "acme");

        var fxB = await fxA.WithNewTenantAsync();
        var serviceB = fxB.BuildService();

        var act = () => serviceB.UpsertAsync(fileA.Id, userA.Id, "project", "stolen");
        // Either the FK constraint rejects the Add or EF Core raises before the DB call —
        // either way, tenant-A's tag is unchanged. The shape of the exception is secondary to
        // the state-invariance assertion below.
        await act.Should().ThrowAsync<Exception>();

        var aTags = await serviceA.GetTagsAsync(fileA.Id, userA.Id);
        aTags.Should().HaveCount(1);
        aTags[0].Value.Should().Be("acme", "cross-tenant upsert must not mutate foreign-tenant tag state");
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

        public ITagService BuildService()
        {
            var db = NewDbContext();
            var repo = new TagRepository(db);
            var audit = new AuditService(db);
            return new TagService(db, repo, tenantContext, audit, NullLogger<TagService>.Instance);
        }

        public async Task<User> CreateUserAsync()
        {
            await using var ctx = NewDbContext();
            var user = new User
            {
                TenantId = TenantId,
                Email = $"tag-{Guid.NewGuid():N}@example.com",
                DisplayName = "Tag Test",
                PasswordHash = "not-a-real-hash-tests-only",
                QuotaBytes = 1_000_000,
            };
            ctx.Users.Add(user);
            await ctx.SaveChangesAsync();
            return user;
        }

        public async Task<(User user, FileItem file)> CreateUserAndFileAsync()
        {
            var user = await CreateUserAsync();
            await using var ctx = NewDbContext();
            var drive = new Drive
            {
                TenantId = TenantId,
                Name = $"drive-{Guid.NewGuid():N}",
                ProviderType = "local",
                ProviderConfig = "{}",
            };
            ctx.Drives.Add(drive);
            var file = new FileItem
            {
                TenantId = TenantId,
                DriveId = drive.Id,
                Name = "doc.txt",
                Path = "/doc.txt",
                IsDirectory = false,
                CreatedBy = user.Id,
            };
            ctx.Files.Add(file);
            await ctx.SaveChangesAsync();
            return (user, file);
        }

        public async Task<IReadOnlyList<AuditEntry>> LoadAuditEntriesAsync(string action)
        {
            await using var ctx = NewDbContext();
            return await ctx.AuditEntries
                .Where(a => a.Action == action)
                .OrderBy(a => a.PerformedAt)
                .ToListAsync();
        }

        public async Task<int> CountAuditEntriesAsync(string action)
        {
            await using var ctx = NewDbContext();
            return await ctx.AuditEntries.CountAsync(a => a.Action == action);
        }

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
