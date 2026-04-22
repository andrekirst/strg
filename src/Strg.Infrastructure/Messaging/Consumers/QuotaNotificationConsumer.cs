using System.Text.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Strg.Core.Domain;
using Strg.Core.Events;
using Strg.Infrastructure.Data;
using Strg.Infrastructure.Data.Configurations;

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
        // Binding the raw ExceptionInfo[] to {Exceptions} renders as the sequence of concrete
        // impl type names (e.g. ["MassTransit.Events.FaultExceptionInfo"]) because ExceptionInfo
        // is an interface and Serilog's scalar converter falls back to Type.FullName via default
        // ToString. Verified empirically in STRG-062 follow-up INFO-1. Projecting to
        // "{Type}: {Message}" strings restores the forensic signal without the @-destructure
        // path that would flow StackTrace + Data dictionary contents (EF parameter values,
        // neighbouring-row tenant IDs from FK-violation DETAIL) into the structured payload.
        var projected = context.Message.Exceptions
            .Select(e => $"{e.ExceptionType}: {e.Message}")
            .ToArray();
        _logger.LogError(
            "Dead-letter: QuotaWarningEvent dispatch failed. Tenant={TenantId} User={UserId} Exceptions={Exceptions}",
            context.Message.Message.TenantId, context.Message.Message.UserId, projected);
        return Task.CompletedTask;
    }

    // Exact equality against the EF-pinned index name, not substring — mirrors the
    // AuditLogConsumer triangulation (STRG-062 INFO-2). A future rename like
    // UQ_Notifications_Idempotency or an unrelated unique index whose name contains
    // "EventId" would previously have been mis-classified. The MigrationTests schema
    // pin + EF HasDatabaseName + this equality check are the three anchors.
    private static bool IsDuplicateEventId(DbUpdateException ex) =>
        ex.InnerException is PostgresException pg
        && pg.SqlState == "23505"
        && pg.ConstraintName == NotificationConstraintNames.EventIdUniqueIndex;
}
