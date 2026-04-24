using FluentAssertions;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Strg.Application.Abstractions;
using Strg.Application.DependencyInjection;
using Strg.Application.Features.Tags.AddTag;
using Strg.Application.Features.Tags.ListTagsForFile;
using Strg.Application.Features.Tags.RemoveAllTags;
using Strg.Application.Features.Tags.RemoveTag;
using Strg.Application.Features.Tags.UpdateTag;
using Strg.Core.Auditing;
using Strg.Core.Domain;
using Strg.Core.Exceptions;
using Strg.Infrastructure.Auditing;
using Strg.Infrastructure.Data;
using Testcontainers.PostgreSql;
using Xunit;

namespace Strg.Integration.Tests.Application.Tags;

internal sealed class FixedTenantContext(Guid id) : ITenantContext
{
    public Guid TenantId => id;
}

internal sealed class MutableCurrentUser : ICurrentUser
{
    public Guid UserId { get; set; }
}

/// <summary>
/// Integration tests for the Tags CQRS slice. Ports every invariant from the now-deleted
/// TagServiceTests onto IMediator dispatch — each handler runs inside the real Mediator pipeline
/// (LoggingBehavior, ValidationBehavior, TenantScopeBehavior, AuditBehavior) against
/// TestContainers Postgres. Equivalence with the old suite is the proof that the handler port
/// preserves the TagService contract verbatim.
/// </summary>
public sealed class TagHandlerTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    // TC-001
    [Fact]
    public async Task AddTag_creates_tag_with_normalized_key_and_typed_value()
    {
        var fx = await CreateFixtureAsync();
        var (user, file) = await fx.CreateUserAndFileAsync();
        var mediator = fx.BuildMediator(user.Id);

        var result = await mediator.Send(new AddTagCommand(file.Id, "Project", "acme", TagValueType.String));

        result.IsSuccess.Should().BeTrue();
        result.Value!.Key.Should().Be("project", "handler normalizes key to lowercase");
        result.Value.Value.Should().Be("acme");
        result.Value.ValueType.Should().Be(TagValueType.String);
        result.Value.FileId.Should().Be(file.Id);
        result.Value.UserId.Should().Be(user.Id);
        result.Value.TenantId.Should().Be(fx.TenantId);
    }

    // TC-002
    [Fact]
    public async Task AddTag_same_key_twice_updates_value_and_does_not_duplicate()
    {
        var fx = await CreateFixtureAsync();
        var (user, file) = await fx.CreateUserAndFileAsync();
        var mediator = fx.BuildMediator(user.Id);

        var first = await mediator.Send(new AddTagCommand(file.Id, "project", "acme", TagValueType.String));
        var second = await mediator.Send(new AddTagCommand(file.Id, "project", "globex", TagValueType.String));

        second.Value!.Id.Should().Be(first.Value!.Id, "upsert must reuse the existing row, not create a second");
        second.Value.Value.Should().Be("globex");

        var all = await mediator.Send(new ListTagsForFileQuery(file.Id));
        all.Should().HaveCount(1);
    }

    // TC-003
    [Fact]
    public async Task RemoveTag_by_id_on_existing_tag_emits_audit_and_hard_deletes()
    {
        var fx = await CreateFixtureAsync();
        var (user, file) = await fx.CreateUserAndFileAsync();
        var mediator = fx.BuildMediator(user.Id);

        var created = await mediator.Send(new AddTagCommand(file.Id, "project", "acme", TagValueType.String));
        var removed = await mediator.Send(new RemoveTagCommand(created.Value!.Id));

        removed.IsSuccess.Should().BeTrue();

        // Hard-delete (not soft-delete) is the canonical behavior — the previous GraphQL quirk
        // that set DeletedAt is intentionally not replicated. ListTagsForFile must return empty.
        var after = await mediator.Send(new ListTagsForFileQuery(file.Id));
        after.Should().BeEmpty();

        (await fx.CountAuditEntriesAsync(AuditActions.TagRemoved)).Should().Be(1);
    }

    // TC-004
    [Fact]
    public async Task AddTag_key_exceeding_255_chars_short_circuits_to_validation_failure()
    {
        var fx = await CreateFixtureAsync();
        var (user, file) = await fx.CreateUserAndFileAsync();
        var mediator = fx.BuildMediator(user.Id);

        var oversizedKey = new string('a', 256);
        var result = await mediator.Send(new AddTagCommand(file.Id, oversizedKey, "x", TagValueType.String));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("ValidationError");
    }

    [Fact]
    public async Task AddTag_key_with_disallowed_characters_short_circuits_to_validation_failure()
    {
        var fx = await CreateFixtureAsync();
        var (user, file) = await fx.CreateUserAndFileAsync();
        var mediator = fx.BuildMediator(user.Id);

        foreach (var badKey in new[] { "has space", "has/slash", "has;semi", "has=equals" })
        {
            var result = await mediator.Send(new AddTagCommand(file.Id, badKey, "x", TagValueType.String));
            result.IsFailure.Should().BeTrue($"key '{badKey}' should be rejected");
            result.ErrorCode.Should().Be("ValidationError");
        }
    }

    [Fact]
    public async Task AddTag_empty_key_short_circuits_to_validation_failure()
    {
        var fx = await CreateFixtureAsync();
        var (user, file) = await fx.CreateUserAndFileAsync();
        var mediator = fx.BuildMediator(user.Id);

        var empty = await mediator.Send(new AddTagCommand(file.Id, string.Empty, "x", TagValueType.String));
        empty.IsFailure.Should().BeTrue();
        empty.ErrorCode.Should().Be("ValidationError");
    }

    [Fact]
    public async Task AddTag_value_exceeding_255_chars_short_circuits_to_validation_failure()
    {
        var fx = await CreateFixtureAsync();
        var (user, file) = await fx.CreateUserAndFileAsync();
        var mediator = fx.BuildMediator(user.Id);

        var oversizedValue = new string('v', 256);
        var result = await mediator.Send(new AddTagCommand(file.Id, "project", oversizedValue, TagValueType.String));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("ValidationError");
    }

    // TC-005
    [Fact]
    public async Task AddTag_case_mixed_key_matches_lowercase_on_subsequent_read()
    {
        var fx = await CreateFixtureAsync();
        var (user, file) = await fx.CreateUserAndFileAsync();
        var mediator = fx.BuildMediator(user.Id);

        await mediator.Send(new AddTagCommand(file.Id, "Project", "acme", TagValueType.String));

        var tags = await mediator.Send(new ListTagsForFileQuery(file.Id));
        tags.Should().HaveCount(1);
        tags[0].Key.Should().Be("project");

        await mediator.Send(new AddTagCommand(file.Id, "PROJECT", "globex", TagValueType.String));
        var after = await mediator.Send(new ListTagsForFileQuery(file.Id));
        after.Should().HaveCount(1);
        after[0].Value.Should().Be("globex");
    }

    [Fact]
    public async Task Two_users_on_same_file_have_independent_tag_records()
    {
        var fx = await CreateFixtureAsync();
        var (userA, file) = await fx.CreateUserAndFileAsync();
        var userB = await fx.CreateUserAsync();

        var mediatorA = fx.BuildMediator(userA.Id);
        var mediatorB = fx.BuildMediator(userB.Id);

        await mediatorA.Send(new AddTagCommand(file.Id, "project", "acme", TagValueType.String));
        await mediatorB.Send(new AddTagCommand(file.Id, "project", "globex", TagValueType.String));

        var a = await mediatorA.Send(new ListTagsForFileQuery(file.Id));
        var b = await mediatorB.Send(new ListTagsForFileQuery(file.Id));

        a.Should().HaveCount(1);
        a[0].Value.Should().Be("acme");
        b.Should().HaveCount(1);
        b[0].Value.Should().Be("globex");
    }

    [Fact]
    public async Task ListTagsForFile_does_not_return_other_users_tags_on_same_file()
    {
        var fx = await CreateFixtureAsync();
        var (userA, file) = await fx.CreateUserAndFileAsync();
        var userB = await fx.CreateUserAsync();

        var mediatorA = fx.BuildMediator(userA.Id);
        var mediatorB = fx.BuildMediator(userB.Id);

        await mediatorA.Send(new AddTagCommand(file.Id, "project", "acme", TagValueType.String));
        await mediatorB.Send(new AddTagCommand(file.Id, "secret", "value", TagValueType.String));

        var bTags = await mediatorB.Send(new ListTagsForFileQuery(file.Id));

        bTags.Should().HaveCount(1);
        bTags[0].Key.Should().Be("secret", "user B must not see user A's 'project' tag");
    }

    [Fact]
    public async Task AddTag_emits_tag_assigned_audit_row()
    {
        var fx = await CreateFixtureAsync();
        var (user, file) = await fx.CreateUserAndFileAsync();
        var mediator = fx.BuildMediator(user.Id);

        await mediator.Send(new AddTagCommand(file.Id, "project", "acme", TagValueType.String));

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
    public async Task RemoveTag_emits_tag_removed_audit_row()
    {
        var fx = await CreateFixtureAsync();
        var (user, file) = await fx.CreateUserAndFileAsync();
        var mediator = fx.BuildMediator(user.Id);

        var created = await mediator.Send(new AddTagCommand(file.Id, "Project", "acme", TagValueType.String));
        var removed = await mediator.Send(new RemoveTagCommand(created.Value!.Id));

        removed.IsSuccess.Should().BeTrue();
        var audits = await fx.LoadAuditEntriesAsync(AuditActions.TagRemoved);
        audits.Should().HaveCount(1);
        audits[0].Details.Should().Contain("key=project");
    }

    [Fact]
    public async Task RemoveAllTags_removes_every_tag_for_user_and_returns_count()
    {
        var fx = await CreateFixtureAsync();
        var (user, file) = await fx.CreateUserAndFileAsync();
        var mediator = fx.BuildMediator(user.Id);

        await mediator.Send(new AddTagCommand(file.Id, "project", "acme", TagValueType.String));
        await mediator.Send(new AddTagCommand(file.Id, "priority", "3", TagValueType.Number));
        await mediator.Send(new AddTagCommand(file.Id, "done", "false", TagValueType.Boolean));

        var removed = await mediator.Send(new RemoveAllTagsCommand(file.Id));

        removed.IsSuccess.Should().BeTrue();
        removed.Value.Should().Be(3);
        (await mediator.Send(new ListTagsForFileQuery(file.Id))).Should().BeEmpty();

        var audits = await fx.LoadAuditEntriesAsync(AuditActions.TagRemoved);
        audits.Should().HaveCount(1);
        audits[0].Details.Should().Contain("bulk=true").And.Contain("count=3");
    }

    [Fact]
    public async Task RemoveAllTags_on_file_with_no_tags_returns_zero_and_emits_no_audit()
    {
        var fx = await CreateFixtureAsync();
        var (user, file) = await fx.CreateUserAndFileAsync();
        var mediator = fx.BuildMediator(user.Id);

        var removed = await mediator.Send(new RemoveAllTagsCommand(file.Id));

        removed.IsSuccess.Should().BeTrue();
        removed.Value.Should().Be(0);
        (await fx.CountAuditEntriesAsync(AuditActions.TagRemoved)).Should().Be(0);
    }

    [Fact]
    public async Task AddTag_persists_all_three_TagValueTypes()
    {
        var fx = await CreateFixtureAsync();
        var (user, file) = await fx.CreateUserAndFileAsync();
        var mediator = fx.BuildMediator(user.Id);

        await mediator.Send(new AddTagCommand(file.Id, "s", "v", TagValueType.String));
        await mediator.Send(new AddTagCommand(file.Id, "n", "42", TagValueType.Number));
        await mediator.Send(new AddTagCommand(file.Id, "b", "true", TagValueType.Boolean));

        var tags = await mediator.Send(new ListTagsForFileQuery(file.Id));
        tags.Select(t => t.ValueType).Should().BeEquivalentTo(
            new[] { TagValueType.Boolean, TagValueType.Number, TagValueType.String });
    }

    [Fact]
    public async Task AddTag_cross_tenant_throws_NotFound_when_file_not_visible_to_caller()
    {
        var fxA = await CreateFixtureAsync();
        var (userA, fileA) = await fxA.CreateUserAndFileAsync();

        var fxB = await fxA.WithNewTenantAsync();
        var mediatorB = fxB.BuildMediator(userA.Id);

        var act = async () => await mediatorB.Send(
            new AddTagCommand(fileA.Id, "project", "stolen", TagValueType.String));

        await act.Should().ThrowAsync<NotFoundException>();

        // Belt-and-braces: confirm no Tag row was created under either tenant.
        var mediatorA = fxA.BuildMediator(userA.Id);
        (await mediatorA.Send(new ListTagsForFileQuery(fileA.Id))).Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateTag_updates_value_and_value_type_by_id()
    {
        var fx = await CreateFixtureAsync();
        var (user, file) = await fx.CreateUserAndFileAsync();
        var mediator = fx.BuildMediator(user.Id);

        var created = await mediator.Send(new AddTagCommand(file.Id, "priority", "1", TagValueType.Number));
        var updated = await mediator.Send(new UpdateTagCommand(created.Value!.Id, "2", TagValueType.Number));

        updated.IsSuccess.Should().BeTrue();
        updated.Value!.Value.Should().Be("2");
        updated.Value.ValueType.Should().Be(TagValueType.Number);
    }

    [Fact]
    public async Task UpdateTag_with_missing_id_returns_NotFound()
    {
        var fx = await CreateFixtureAsync();
        var (user, _) = await fx.CreateUserAndFileAsync();
        var mediator = fx.BuildMediator(user.Id);

        var result = await mediator.Send(new UpdateTagCommand(Guid.NewGuid(), "x", TagValueType.String));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("NotFound");
    }

    [Fact]
    public async Task RemoveTag_with_missing_id_returns_NotFound()
    {
        var fx = await CreateFixtureAsync();
        var (user, _) = await fx.CreateUserAndFileAsync();
        var mediator = fx.BuildMediator(user.Id);

        var result = await mediator.Send(new RemoveTagCommand(Guid.NewGuid()));

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("NotFound");
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

        await using (var bootstrap = new StrgDbContext(options, tenantContext))
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
            tenantContext);

        public IMediator BuildMediator(Guid currentUserId)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(tenantContext);
            services.AddSingleton<ICurrentUser>(new MutableCurrentUser { UserId = currentUserId });
            services.AddDbContext<StrgDbContext>(o => o.UseNpgsql(connectionString));
            services.AddScoped<IStrgDbContext>(sp => sp.GetRequiredService<StrgDbContext>());
            services.AddScoped<ITagRepository, TagRepository>();
            services.AddScoped<IFileRepository, FileRepository>();
            services.AddScoped<IAuditService, AuditService>();
            services.AddStrgApplication();

            return services.BuildServiceProvider().GetRequiredService<IMediator>();
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
            var options = new DbContextOptionsBuilder<StrgDbContext>().UseNpgsql(connectionString).Options;
            await using var ctx = new StrgDbContext(options, newTenantContext);
            ctx.Tenants.Add(new Tenant { Id = newTenantId, Name = $"test-{newTenantId:N}" });
            await ctx.SaveChangesAsync();
            return new Fixture(connectionString, newTenantContext, newTenantId);
        }
    }
}
