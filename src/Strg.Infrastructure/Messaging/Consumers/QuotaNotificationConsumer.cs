using System.Text.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Strg.Core.Domain;
using Strg.Core.Events;
using Strg.Infrastructure.Data;

namespace Strg.Infrastructure.Messaging.Consumers;

/// <summary>
/// Persists a <see cref="Notification"/> row for every <see cref="QuotaWarningEvent"/> so the
/// user can read the warning history via the notification centre even when they weren't
/// connected to a GraphQL subscription at the moment of the threshold crossing. The live
/// subscription push happens in the Strg.GraphQL-layer consumer; this consumer is the durable
/// surface.
///
/// <para><b>Idempotency:</b> <see cref="Notification.EventId"/> carries the MassTransit
/// <c>MessageId</c>. The partial unique index on that column collapses at-least-once redelivery
/// to one row — duplicate inserts fail with Postgres SQLSTATE <c>23505</c>, which we swallow
/// as "already handled". This is the "ON CONFLICT DO NOTHING" guard MassTransit's outbox
/// assumes the consumer-side will provide.</para>
///
/// <para><b>Tenant source:</b> <c>TenantId</c> is taken from the event payload, not the ambient
/// <see cref="ITenantContext"/>. Consumer scope is initialised by MassTransit without a tenant
/// context — the event is the only trustworthy source. Same contract as
/// <see cref="AuditLogConsumer"/>; a regression test pins it.</para>
///
/// <para><b>Level in payload:</b> the warning/critical discriminator is derived from the
/// <c>UsedBytes/QuotaBytes</c> ratio at the time of publish. Stored as JSON so future
/// notification types reuse the same column without a migration per variant.</para>
/// </summary>
public sealed class QuotaNotificationConsumer :
    IConsumer<QuotaWarningEvent>,
    IConsumer<Fault<QuotaWarningEvent>>
{
    private readonly StrgDbContext _db;
    private readonly ILogger<QuotaNotificationConsumer> _logger;

    public QuotaNotificationConsumer(StrgDbContext db, ILogger<QuotaNotificationConsumer> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<QuotaWarningEvent> context)
    {
        var msg = context.Message;
        var ratio = msg.QuotaBytes <= 0 ? 0d : (double)msg.UsedBytes / msg.QuotaBytes;
        var level = ratio >= QuotaThresholds.Critical
            ? QuotaThresholds.CriticalLevel
            : QuotaThresholds.WarningLevel;

        var payload = JsonSerializer.Serialize(new
        {
            level,
            usedBytes = msg.UsedBytes,
            quotaBytes = msg.QuotaBytes,
        });

        var notification = new Notification
        {
            TenantId = msg.TenantId,
            UserId = msg.UserId,
            Type = QuotaThresholds.NotificationType,
            PayloadJson = payload,
            EventId = context.MessageId,
        };

        _db.Notifications.Add(notification);

        try
        {
            await _db.SaveChangesAsync(context.CancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "QuotaNotificationConsumer: wrote {Level} notification for tenant={TenantId} user={UserId} (ratio={Ratio:P0})",
                level, msg.TenantId, msg.UserId, ratio);
        }
        catch (DbUpdateException ex) when (IsDuplicateEventId(ex))
        {
            // Duplicate EventId → redelivery of an already-persisted warning. Swallow at Debug:
            // idempotent redelivery is expected under at-least-once; louder levels would drown
            // the log on broker replays.
            _logger.LogDebug(
                "QuotaNotificationConsumer: duplicate EventId {EventId} for tenant={TenantId} user={UserId} — row already persisted",
                context.MessageId, msg.TenantId, msg.UserId);
        }
    }

    public Task Consume(ConsumeContext<Fault<QuotaWarningEvent>> context)
    {
        _logger.LogError(
            // {Exceptions} not {@Exceptions}: destructuring the Fault exception graph flows
            // EF parameter values (path strings, neighbouring-row tenant IDs from FK-violation
            // messages) into the structured payload. Plain ToString keeps type + top stack
            // frame without cross-tenant leakage.
            "Dead-letter: QuotaWarningEvent dispatch failed. Tenant={TenantId} User={UserId} Exceptions={Exceptions}",
            context.Message.Message.TenantId, context.Message.Message.UserId, context.Message.Exceptions);
        return Task.CompletedTask;
    }

    private static bool IsDuplicateEventId(DbUpdateException ex) =>
        ex.InnerException is PostgresException pg
        && pg.SqlState == "23505"
        && pg.ConstraintName is not null
        && pg.ConstraintName.Contains("EventId", StringComparison.Ordinal);
}
