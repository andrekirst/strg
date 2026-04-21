using System.Text.Json;
using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Strg.Core.Auditing;
using Strg.Core.Domain;
using Strg.Core.Events;
using Strg.Infrastructure.Data;
using Strg.Infrastructure.Messaging.Consumers;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Xunit;

namespace Strg.Integration.Tests.Messaging;

/// <summary>
/// STRG-062 TC-001..TC-005 + idempotency. Proves the <see cref="AuditLogConsumer"/> writes an
/// append-only <see cref="AuditEntry"/> row per domain event dispatched through the MassTransit
/// outbox and that at-least-once redelivery collapses on the <see cref="AuditEntry.EventId"/>
/// unique constraint.
///
/// <para>Same container shape as STRG-061's outbox tests: one <see cref="PostgreSqlContainer"/> +
/// one <see cref="RabbitMqContainer"/> per class, fresh database per fact.</para>
/// </summary>
public sealed class AuditLogConsumerTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .Build();

    private readonly RabbitMqContainer _rabbitMq = new RabbitMqBuilder("rabbitmq:3.13-management-alpine")
        .Build();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await _rabbitMq.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _rabbitMq.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task TC001_FileUploadedEvent_creates_audit_entry_with_file_uploaded_action()
    {
        var tenantId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var driveId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await using var provider = await BuildServiceProviderAsync(tenantId);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await PublishAsync(provider, tenantId, new FileUploadedEvent(
            tenantId, fileId, driveId, userId, Size: 2048, MimeType: "image/png"));

        (await harness.Consumed.Any<FileUploadedEvent>()).Should().BeTrue(
            "harness must observe the consumer pulling the event off the outbox");

        var entry = await GetSingleAuditEntryAsync(provider, fileId);
        entry.Action.Should().Be(AuditActions.FileUploaded);
        entry.TenantId.Should().Be(tenantId);
        entry.UserId.Should().Be(userId);
        entry.ResourceType.Should().Be("FileItem");
        entry.ResourceId.Should().Be(fileId);
        entry.EventId.Should().NotBeNull().And.NotBe(Guid.Empty);

        // Details is a camelCase JSON blob — the wire format the STRG-062 spec fixes so GraphQL
        // admin queries + audit-log exporters can rely on field names without a transform layer.
        var details = JsonDocument.Parse(entry.Details!).RootElement;
        details.GetProperty("driveId").GetGuid().Should().Be(driveId);
        details.GetProperty("size").GetInt64().Should().Be(2048);
        details.GetProperty("mimeType").GetString().Should().Be("image/png");
    }

    [Fact]
    public async Task TC002_FileDeletedEvent_creates_audit_entry_with_file_deleted_action()
    {
        var tenantId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var driveId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await using var provider = await BuildServiceProviderAsync(tenantId);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await PublishAsync(provider, tenantId, new FileDeletedEvent(tenantId, fileId, driveId, userId));

        (await harness.Consumed.Any<FileDeletedEvent>()).Should().BeTrue();

        var entry = await GetSingleAuditEntryAsync(provider, fileId);
        entry.Action.Should().Be(AuditActions.FileDeleted);
        entry.ResourceId.Should().Be(fileId);

        var details = JsonDocument.Parse(entry.Details!).RootElement;
        details.GetProperty("driveId").GetGuid().Should().Be(driveId);
    }

    [Fact]
    public async Task TC003_Transient_DbUpdateException_retries_and_eventually_persists_one_row()
    {
        // TC-003: non-idempotency DbUpdateException (e.g. deadlock, conflict on an unrelated
        // column) must rethrow so MassTransit's retry pipeline takes over. A ThrowOnceInterceptor
        // fails the first AuditEntry INSERT; the consumer rethrows, MassTransit retries, second
        // attempt has a fresh scope where the interceptor has already fired → succeeds.
        var tenantId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var interceptor = new ThrowOnceInterceptor();

        await using var provider = await BuildServiceProviderAsync(tenantId, interceptor);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await PublishAsync(provider, tenantId, new FileUploadedEvent(
            tenantId, fileId, Guid.NewGuid(), Guid.NewGuid(), Size: 512, MimeType: "text/plain"));

        // Poll for the audit row — the first attempt throws in the interceptor (AuditEntry
        // INSERT rolled back), MassTransit retries, second attempt commits. Polling rather than
        // harness.Consumed.Any<T>() because the harness flags as "consumed" on the first attempt
        // even if the consumer threw, and we specifically want the *successful* write to land.
        var entry = await WaitForAuditEntryAsync(provider, fileId, TimeSpan.FromSeconds(45));
        entry.Should().NotBeNull("retry must eventually persist the audit row");

        interceptor.Hits.Should().BeGreaterOrEqualTo(2,
            "the interceptor must have seen the failing attempt + at least one retry");

        // Exactly one audit row lands — the failed attempt rolled back, the retry committed.
        var entries = await GetAuditEntriesAsync(provider, fileId);
        entries.Should().ContainSingle(e => e.Action == AuditActions.FileUploaded);
    }

    [Fact]
    public async Task TC004_FileMovedEvent_details_json_contains_oldPath_and_newPath()
    {
        var tenantId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        const string OldPath = "/docs/draft.md";
        const string NewPath = "/docs/archive/draft-2026.md";

        await using var provider = await BuildServiceProviderAsync(tenantId);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await PublishAsync(provider, tenantId, new FileMovedEvent(
            tenantId, fileId, DriveId: Guid.NewGuid(), OldPath, NewPath, UserId: Guid.NewGuid()));

        (await harness.Consumed.Any<FileMovedEvent>()).Should().BeTrue();

        var entry = await GetSingleAuditEntryAsync(provider, fileId);
        entry.Action.Should().Be(AuditActions.FileMoved);

        var details = JsonDocument.Parse(entry.Details!).RootElement;
        details.GetProperty("oldPath").GetString().Should().Be(OldPath);
        details.GetProperty("newPath").GetString().Should().Be(NewPath);
    }

    [Fact]
    public async Task TC005_Two_events_in_sequence_produce_two_audit_rows()
    {
        var tenantId = Guid.NewGuid();
        var fileId1 = Guid.NewGuid();
        var fileId2 = Guid.NewGuid();

        await using var provider = await BuildServiceProviderAsync(tenantId);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        using (var scope = provider.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<StrgDbContext>();
            var bus = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();
            ctx.Tenants.Add(new Tenant { Id = tenantId, Name = "audit-two" });

            await bus.Publish(new FileUploadedEvent(
                tenantId, fileId1, Guid.NewGuid(), Guid.NewGuid(), 1, "a/b"));
            await bus.Publish(new FileDeletedEvent(
                tenantId, fileId2, Guid.NewGuid(), Guid.NewGuid()));

            await ctx.SaveChangesAsync();
        }

        (await harness.Consumed.Any<FileUploadedEvent>()).Should().BeTrue();
        (await harness.Consumed.Any<FileDeletedEvent>()).Should().BeTrue();

        await using var assertScope = provider.CreateAsyncScope();
        var db = assertScope.ServiceProvider.GetRequiredService<StrgDbContext>();
        var rows = await db.AuditEntries
            .Where(e => e.ResourceId == fileId1 || e.ResourceId == fileId2)
            .ToListAsync();

        rows.Should().HaveCount(2);
        rows.Should().ContainSingle(e => e.ResourceId == fileId1 && e.Action == AuditActions.FileUploaded);
        rows.Should().ContainSingle(e => e.ResourceId == fileId2 && e.Action == AuditActions.FileDeleted);
    }

    [Fact]
    public async Task Idempotency_DbLevel_unique_constraint_rejects_duplicate_EventId()
    {
        // Direct DB-level test: the partial unique index must reject a second AuditEntry row with
        // the same EventId. This is the guard the consumer relies on — if it silently dropped,
        // at-least-once redelivery would produce N duplicate audit rows.
        var tenantId = Guid.NewGuid();
        var eventId = Guid.NewGuid();

        await using var provider = await BuildServiceProviderAsync(tenantId);

        using var scope1 = provider.CreateScope();
        var db1 = scope1.ServiceProvider.GetRequiredService<StrgDbContext>();
        db1.Tenants.Add(new Tenant { Id = tenantId, Name = "dupe-test" });
        db1.AuditEntries.Add(new AuditEntry
        {
            TenantId = tenantId,
            UserId = Guid.NewGuid(),
            Action = AuditActions.FileUploaded,
            ResourceType = "FileItem",
            ResourceId = Guid.NewGuid(),
            EventId = eventId,
        });
        await db1.SaveChangesAsync();

        using var scope2 = provider.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<StrgDbContext>();
        db2.AuditEntries.Add(new AuditEntry
        {
            TenantId = tenantId,
            UserId = Guid.NewGuid(),
            Action = AuditActions.FileDeleted,
            ResourceType = "FileItem",
            ResourceId = Guid.NewGuid(),
            EventId = eventId,
        });

        Func<Task> act = () => db2.SaveChangesAsync();
        var thrown = await act.Should().ThrowAsync<DbUpdateException>();
        thrown.Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be("23505");
    }

    [Fact]
    public async Task Idempotency_Consumer_swallows_duplicate_EventId_delivery()
    {
        // Consumer-level idempotency: seed an AuditEntry with EventId = X, then publish a domain
        // event whose MessageId is also X. The consumer attempts the insert, hits the partial
        // unique index, catches the PostgresException, and returns without rethrowing. The
        // outbox-state row is marked delivered so MassTransit does not loop on it.
        var tenantId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var messageId = Guid.NewGuid();

        await using var provider = await BuildServiceProviderAsync(tenantId);

        // Seed the "already persisted" row with the same EventId the next publish will use.
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();
            db.Tenants.Add(new Tenant { Id = tenantId, Name = "idem-test" });
            db.AuditEntries.Add(new AuditEntry
            {
                TenantId = tenantId,
                UserId = Guid.NewGuid(),
                Action = AuditActions.FileUploaded,
                ResourceType = "FileItem",
                ResourceId = fileId,
                Details = "{\"source\":\"seed\"}",
                EventId = messageId,
            });
            await db.SaveChangesAsync();
        }

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        using (var scope = provider.CreateScope())
        {
            var bus = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();
            var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();

            await bus.Publish(
                new FileUploadedEvent(tenantId, fileId, Guid.NewGuid(), Guid.NewGuid(), 99, "x/y"),
                ctx => ctx.MessageId = messageId);
            await db.SaveChangesAsync();
        }

        (await harness.Consumed.Any<FileUploadedEvent>()).Should().BeTrue(
            "consumer must observe the redelivered event and silently collapse");

        // Exactly one row with this EventId — the seed, untouched. The consumer's INSERT
        // rolled back on the unique-violation and did not throw further.
        var rows = await GetAuditEntriesAsync(provider, fileId);
        rows.Should().ContainSingle(e => e.EventId == messageId)
            .Which.Details.Should().Be("{\"source\":\"seed\"}");
    }

    private static async Task PublishAsync<TEvent>(ServiceProvider provider, Guid tenantId, TEvent message)
        where TEvent : class
    {
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();
        var bus = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "audit-test" });
        await bus.Publish(message);
        await db.SaveChangesAsync();
    }

    private static async Task<AuditEntry> GetSingleAuditEntryAsync(ServiceProvider provider, Guid fileId)
    {
        var rows = await GetAuditEntriesAsync(provider, fileId);
        rows.Should().ContainSingle($"exactly one audit row must exist for file {fileId}");
        return rows[0];
    }

    private static async Task<List<AuditEntry>> GetAuditEntriesAsync(ServiceProvider provider, Guid fileId)
    {
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();
        return await db.AuditEntries
            .Where(e => e.ResourceId == fileId)
            .ToListAsync();
    }

    private static async Task<AuditEntry?> WaitForAuditEntryAsync(
        ServiceProvider provider, Guid fileId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            var rows = await GetAuditEntriesAsync(provider, fileId);
            if (rows.Count > 0)
            {
                return rows[0];
            }
            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }
        return null;
    }

    private async Task<ServiceProvider> BuildServiceProviderAsync(Guid tenantId, IInterceptor? saveInterceptor = null)
    {
        var connectionString = await CreateFreshDatabaseAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITenantContext>(new OutboxTenantContext(tenantId));

        services.AddDbContext<StrgDbContext>(options =>
        {
            options.UseNpgsql(connectionString);
            options.UseOpenIddict();
            if (saveInterceptor is not null)
            {
                options.AddInterceptors(saveInterceptor);
            }
        });

        services.AddMassTransitTestHarness(bus =>
        {
            bus.AddConsumer<AuditLogConsumer>();

            bus.AddEntityFrameworkOutbox<StrgDbContext>(outbox =>
            {
                outbox.UsePostgres();
                outbox.UseBusOutbox();
                outbox.QueryDelay = TimeSpan.FromSeconds(1);
            });

            bus.UsingRabbitMq((context, cfg) =>
            {
                var uri = new Uri(_rabbitMq.GetConnectionString());
                cfg.Host(uri);
                // Short retry cycle so TC-003 completes inside the harness timeout. Production
                // uses 5× exponential backoff (see MassTransitExtensions); the test only needs
                // to prove the retry *happens*, not match prod's full schedule.
                cfg.UseMessageRetry(r => r.Immediate(3));
                cfg.ConfigureEndpoints(context);
            });
        });

        services.Configure<MassTransitHostOptions>(o => o.WaitUntilStarted = true);
        services.AddOptions<TestHarnessOptions>().Configure(o =>
        {
            o.TestInactivityTimeout = TimeSpan.FromSeconds(30);
            o.TestTimeout = TimeSpan.FromMinutes(2);
        });

        var provider = services.BuildServiceProvider();

        using (var scope = provider.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<StrgDbContext>();
            await ctx.Database.MigrateAsync();
        }

        return provider;
    }

    private async Task<string> CreateFreshDatabaseAsync()
    {
        var dbName = $"strg_audit_{Guid.NewGuid():N}";
        var adminConnectionString = _postgres.GetConnectionString();

        await using (var connection = new NpgsqlConnection(adminConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE \"{dbName}\"";
            await command.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            Database = dbName,
        }.ConnectionString;
    }
}

/// <summary>
/// Simulates a transient DB failure on the first <c>SaveChangesAsync</c> that touches an
/// <see cref="AuditEntry"/> row — subsequent saves pass through. Used by TC-003 to prove the
/// consumer rethrows non-idempotency DbUpdateExceptions so MassTransit can retry.
/// </summary>
internal sealed class ThrowOnceInterceptor : SaveChangesInterceptor
{
    private int _hits;

    public int Hits => Volatile.Read(ref _hits);

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        // Only throw when an AuditEntry is being inserted — the outbox dispatcher also uses this
        // DbContext for its own bookkeeping saves, and throwing there would break the harness.
        var hasAuditInsert = eventData.Context?.ChangeTracker
            .Entries<AuditEntry>()
            .Any(e => e.State == EntityState.Added) ?? false;

        if (hasAuditInsert && Interlocked.Increment(ref _hits) == 1)
        {
            throw new DbUpdateException(
                "ThrowOnceInterceptor: simulated transient failure",
                new InvalidOperationException("first-attempt-failure"));
        }

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}
