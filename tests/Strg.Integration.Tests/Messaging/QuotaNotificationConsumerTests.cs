using System.Text.Json;
using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Strg.Core.Domain;
using Strg.Core.Events;
using Strg.Infrastructure.Data;
using Strg.Infrastructure.Messaging.Consumers;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Xunit;

namespace Strg.Integration.Tests.Messaging;

/// <summary>
/// STRG-064 TC-003/TC-004: end-to-end proof that a <see cref="QuotaWarningEvent"/> published
/// through the outbox lands as a <see cref="Notification"/> row, and that at-least-once
/// redelivery collapses on the partial unique index over <see cref="Notification.EventId"/>.
///
/// <para>Mirrors the TestContainers shape used by <see cref="AuditLogConsumerTests"/>: one
/// Postgres + one RabbitMQ per test class, fresh database per fact. The existence of the live
/// broker is the point — this is the only layer that proves the outbox → broker → consumer
/// path actually works.</para>
/// </summary>
public sealed class QuotaNotificationConsumerTests : IAsyncLifetime
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

    // TC-003
    [Fact]
    public async Task TC003_QuotaWarningEvent_creates_notification_row_with_warning_level_payload()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await using var provider = await BuildServiceProviderAsync(tenantId);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await PublishAsync(provider, tenantId, new QuotaWarningEvent(
            TenantId: tenantId,
            UserId: userId,
            UsedBytes: 810,
            QuotaBytes: 1_000));

        (await harness.Consumed.Any<QuotaWarningEvent>()).Should().BeTrue(
            "harness must observe the consumer pulling the event off the outbox");

        var notification = await GetSingleNotificationAsync(provider, userId);
        notification.TenantId.Should().Be(tenantId);
        notification.UserId.Should().Be(userId);
        notification.Type.Should().Be(QuotaThresholds.NotificationType);
        notification.ReadAt.Should().BeNull("freshly created notifications are unread");
        notification.EventId.Should().NotBeNull().And.NotBe(Guid.Empty);

        var payload = JsonDocument.Parse(notification.PayloadJson).RootElement;
        payload.GetProperty("level").GetString().Should().Be(QuotaThresholds.WarningLevel);
        payload.GetProperty("usedBytes").GetInt64().Should().Be(810);
        payload.GetProperty("quotaBytes").GetInt64().Should().Be(1_000);
    }

    [Fact]
    public async Task QuotaWarningEvent_at_critical_ratio_writes_critical_level_payload()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await using var provider = await BuildServiceProviderAsync(tenantId);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await PublishAsync(provider, tenantId, new QuotaWarningEvent(
            tenantId, userId, UsedBytes: 960, QuotaBytes: 1_000));

        (await harness.Consumed.Any<QuotaWarningEvent>()).Should().BeTrue();

        var notification = await GetSingleNotificationAsync(provider, userId);
        var payload = JsonDocument.Parse(notification.PayloadJson).RootElement;
        payload.GetProperty("level").GetString().Should().Be(QuotaThresholds.CriticalLevel,
            "96% usage crosses the 95% critical threshold — level discriminator must reflect that");
    }

    // TC-004
    [Fact]
    public async Task TC004_Duplicate_EventId_delivery_collapses_to_one_notification_row()
    {
        // Consumer-level idempotency, mirroring AuditLogConsumerTests.Idempotency_Consumer_swallows_duplicate.
        // Seed a Notification row with EventId = X, then publish a QuotaWarningEvent whose
        // MessageId is also X. The consumer's INSERT hits the partial unique index, catches the
        // PostgresException (SQLSTATE 23505), and returns without rethrowing. One row remains.
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var messageId = Guid.NewGuid();

        await using var provider = await BuildServiceProviderAsync(tenantId);

        // Seed the "already persisted" row with the same EventId the next publish will use.
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();
            db.Tenants.Add(new Tenant { Id = tenantId, Name = "quota-idem-test" });
            db.Notifications.Add(new Notification
            {
                TenantId = tenantId,
                UserId = userId,
                Type = QuotaThresholds.NotificationType,
                PayloadJson = "{\"source\":\"seed\"}",
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
                new QuotaWarningEvent(tenantId, userId, UsedBytes: 810, QuotaBytes: 1_000),
                ctx => ctx.MessageId = messageId);
            await db.SaveChangesAsync();
        }

        (await harness.Consumed.Any<QuotaWarningEvent>()).Should().BeTrue(
            "consumer must observe the redelivered event and silently collapse");

        var rows = await GetNotificationsAsync(provider, userId);
        rows.Should().ContainSingle(n => n.EventId == messageId)
            .Which.PayloadJson.Should().Be("{\"source\":\"seed\"}",
                "the seeded row must survive untouched — consumer insert rolled back on unique violation");
    }

    [Fact]
    public async Task Notification_TenantId_comes_from_event_payload_not_ambient_context()
    {
        // Regression defence mirroring the AuditLogConsumer pinning test: consumers run without
        // an HTTP request, so ambient ITenantContext.TenantId = Guid.Empty. The row must carry
        // the event's TenantId, not the empty ambient one. A refactor to a helper that fills
        // TenantId from ambient context would silently route every quota warning into the zero-
        // tenant bucket — this test fails loud.
        var eventTenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await using var provider = await BuildServiceProviderAsync(Guid.Empty);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();
            var bus = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();

            db.Tenants.Add(new Tenant { Id = eventTenantId, Name = "quota-tenant-pin" });
            await bus.Publish(new QuotaWarningEvent(eventTenantId, userId, 810, 1_000));
            await db.SaveChangesAsync();
        }

        (await harness.Consumed.Any<QuotaWarningEvent>()).Should().BeTrue();

        // IgnoreQueryFilters because the Notifications filter is keyed on ambient tenant = Empty.
        await using var assertScope = provider.CreateAsyncScope();
        var readDb = assertScope.ServiceProvider.GetRequiredService<StrgDbContext>();
        var row = await readDb.Notifications
            .IgnoreQueryFilters()
            .SingleAsync(n => n.UserId == userId);
        row.TenantId.Should().Be(eventTenantId).And.NotBe(Guid.Empty);
    }

    private static async Task PublishAsync<TEvent>(ServiceProvider provider, Guid tenantId, TEvent message)
        where TEvent : class
    {
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();
        var bus = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "quota-test" });
        await bus.Publish(message);
        await db.SaveChangesAsync();
    }

    private static async Task<Notification> GetSingleNotificationAsync(ServiceProvider provider, Guid userId)
    {
        var rows = await GetNotificationsAsync(provider, userId);
        rows.Should().ContainSingle($"exactly one notification row must exist for user {userId}");
        return rows[0];
    }

    private static async Task<List<Notification>> GetNotificationsAsync(ServiceProvider provider, Guid userId)
    {
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();
        return await db.Notifications
            .Where(n => n.UserId == userId)
            .ToListAsync();
    }

    private async Task<ServiceProvider> BuildServiceProviderAsync(Guid tenantId)
    {
        var connectionString = await CreateFreshDatabaseAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITenantContext>(new OutboxTenantContext(tenantId));

        services.AddDbContext<StrgDbContext>(options =>
        {
            options.UseNpgsql(connectionString);
            options.UseOpenIddict();
        });

        services.AddMassTransitTestHarness(bus =>
        {
            bus.AddConsumer<QuotaNotificationConsumer>();

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
        var dbName = $"strg_quota_{Guid.NewGuid():N}";
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
