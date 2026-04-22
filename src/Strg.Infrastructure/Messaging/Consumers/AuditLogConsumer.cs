using System.Text.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Strg.Core.Auditing;
using Strg.Core.Domain;
using Strg.Core.Events;
using Strg.Infrastructure.Data;

namespace Strg.Infrastructure.Messaging.Consumers;

/// <summary>
/// STRG-062 — persists an <see cref="AuditEntry"/> row for every file-domain event the outbox
/// dispatches. The consumer is the single write-through point for <c>file.*</c> audit actions
/// so the forensic trail lives alongside auth/tag/prune entries in the same <c>AuditEntries</c>
/// table.
///
/// <para><b>Tenant sourcing — load-bearing.</b> Every <see cref="AuditEntry.TenantId"/> here is
/// taken directly from the event payload (<c>msg.TenantId</c>), NOT from the ambient
/// <c>ITenantContext</c>. MassTransit consumers execute in a background-service DI scope outside
/// any HTTP request, so the ambient <c>HttpTenantContext</c> resolves to <see cref="Guid.Empty"/>.
/// Any refactor that routes writes through an ambient-tenant helper (e.g. <c>IAuditService.LogAsync</c>
/// today fills TenantId from scope in other call sites) will silently land every consumed event
/// in the zero-tenant bucket and defeat per-tenant admin queries. Pinned by a regression test in
/// <c>AuditLogConsumerTests</c>.</para>
///
/// <para><b>Reads inside the consumer scope must use explicit tenant filters.</b> The global
/// EF query filter on <see cref="AuditEntry"/> binds to the same ambient-empty tenant context, so
/// a naive <c>db.AuditEntries.Where(...)</c> inside <c>Consume</c> would return zero rows. The
/// idempotency path here avoids that by relying on the DB-level unique constraint + Npgsql
/// exception inspection instead of a pre-insert existence check.</para>
///
/// <para><b>Idempotency via EventId.</b> The outbox guarantees at-least-once delivery — on retry
/// or after a dispatcher restart the same message can arrive twice. Each row is tagged with
/// <see cref="ConsumeContext.MessageId"/> into <see cref="AuditEntry.EventId"/>, and a partial
/// unique index at the DB layer collapses duplicates. A <see cref="DbUpdateException"/> whose
/// inner Npgsql exception is a unique-violation on that index is the "already-persisted" signal
/// — logged and swallowed. Any other <see cref="DbUpdateException"/> rethrows so MassTransit's
/// retry pipeline (5× exponential backoff per <see cref="MassTransitExtensions"/>) takes over.</para>
///
/// <para><b>Fault observability.</b> When retries are exhausted MassTransit publishes
/// <see cref="Fault{T}"/> messages. We keep log-only handlers here for v0.1; the Notification
/// entity + admin-visible DLQ surface is STRG-064's territory. Dead-letter logs use the plain
/// <c>{Exceptions}</c> template (not <c>{@Exceptions}</c>) to avoid flowing EF parameter values
/// — path strings, neighbouring-row tenant IDs from FK violations — through Serilog's
/// destructuring into the structured log payload.</para>
/// </summary>
public sealed class AuditLogConsumer :
    IConsumer<FileUploadedEvent>,
    IConsumer<FileDeletedEvent>,
    IConsumer<FileMovedEvent>,
    IConsumer<Fault<FileUploadedEvent>>,
    IConsumer<Fault<FileDeletedEvent>>,
    IConsumer<Fault<FileMovedEvent>>
{
    private const string FileItemResource = "FileItem";
    private const string UniqueViolationSqlState = "23505";

    // camelCase for field names in the Details JSON blob so downstream audit-log readers
    // (GraphQL admin queries, JSON-path filters) see the wire format the spec prescribes.
    private static readonly JsonSerializerOptions DetailsJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly StrgDbContext _db;
    private readonly ILogger<AuditLogConsumer> _logger;

    public AuditLogConsumer(StrgDbContext db, ILogger<AuditLogConsumer> logger)
    {
        _db = db;
        _logger = logger;
    }

    public Task Consume(ConsumeContext<FileUploadedEvent> context)
    {
        var msg = context.Message;
        var details = JsonSerializer.Serialize(
            new { driveId = msg.DriveId, size = msg.Size, mimeType = msg.MimeType },
            DetailsJsonOptions);
        return WriteAuditEntryAsync(
            context,
            AuditActions.FileUploaded,
            msg.TenantId,
            msg.UserId,
            msg.FileId,
            details);
    }

    public Task Consume(ConsumeContext<FileDeletedEvent> context)
    {
        var msg = context.Message;
        var details = JsonSerializer.Serialize(
            new { driveId = msg.DriveId },
            DetailsJsonOptions);
        return WriteAuditEntryAsync(
            context,
            AuditActions.FileDeleted,
            msg.TenantId,
            msg.UserId,
            msg.FileId,
            details);
    }

    public Task Consume(ConsumeContext<FileMovedEvent> context)
    {
        var msg = context.Message;
        var details = JsonSerializer.Serialize(
            new { driveId = msg.DriveId, oldPath = msg.OldPath, newPath = msg.NewPath },
            DetailsJsonOptions);
        return WriteAuditEntryAsync(
            context,
            AuditActions.FileMoved,
            msg.TenantId,
            msg.UserId,
            msg.FileId,
            details);
    }

    public Task Consume(ConsumeContext<Fault<FileUploadedEvent>> context)
    {
        _logger.LogError(
            "Dead-letter: FileUploadedEvent dispatch failed after retries. Tenant={TenantId} File={FileId} Exceptions={Exceptions}",
            context.Message.Message.TenantId, context.Message.Message.FileId, context.Message.Exceptions);
        return Task.CompletedTask;
    }

    public Task Consume(ConsumeContext<Fault<FileDeletedEvent>> context)
    {
        _logger.LogError(
            "Dead-letter: FileDeletedEvent dispatch failed after retries. Tenant={TenantId} File={FileId} Exceptions={Exceptions}",
            context.Message.Message.TenantId, context.Message.Message.FileId, context.Message.Exceptions);
        return Task.CompletedTask;
    }

    public Task Consume(ConsumeContext<Fault<FileMovedEvent>> context)
    {
        _logger.LogError(
            "Dead-letter: FileMovedEvent dispatch failed after retries. Tenant={TenantId} File={FileId} Exceptions={Exceptions}",
            context.Message.Message.TenantId, context.Message.Message.FileId, context.Message.Exceptions);
        return Task.CompletedTask;
    }

    private async Task WriteAuditEntryAsync<TEvent>(
        ConsumeContext<TEvent> context,
        string action,
        Guid tenantId,
        Guid userId,
        Guid resourceId,
        string details)
        where TEvent : class
    {
        // MassTransit always populates MessageId; the ?? fallback is defensive against
        // misconfigured test doubles. New GUID there would defeat idempotency, so it's a
        // deliberate loud-but-correct failure rather than a silent hole — logging flags
        // the condition if it ever appears in production.
        if (!context.MessageId.HasValue)
        {
            _logger.LogWarning(
                "AuditLogConsumer: {EventType} arrived with no MessageId — writing non-idempotent audit row",
                typeof(TEvent).Name);
        }

        var entry = new AuditEntry
        {
            TenantId = tenantId,
            UserId = userId,
            Action = action,
            ResourceType = FileItemResource,
            ResourceId = resourceId,
            Details = details,
            EventId = context.MessageId,
        };

        _db.AuditEntries.Add(entry);

        try
        {
            await _db.SaveChangesAsync(context.CancellationToken);
        }
        catch (DbUpdateException ex) when (IsEventIdUniqueViolation(ex))
        {
            // At-least-once redelivery — the row already landed on an earlier attempt. Detach
            // the tracked entity so a retry within the same scope doesn't re-attempt the INSERT.
            _db.Entry(entry).State = EntityState.Detached;
            _logger.LogInformation(
                "AuditLogConsumer: swallowing duplicate {Action} for EventId={EventId} (at-least-once redelivery)",
                action, context.MessageId);
        }
    }

    private static bool IsEventIdUniqueViolation(DbUpdateException ex)
    {
        // Npgsql wraps the PG error; SqlState 23505 is unique_violation. ConstraintName check
        // pins the match to our partial index — any other unique-violation (e.g. a future
        // column) rethrows so the retry pipeline takes over instead of being quietly dropped.
        return ex.InnerException is PostgresException pg
            && pg.SqlState == UniqueViolationSqlState
            && pg.ConstraintName is { } name
            && name.Contains("EventId", StringComparison.Ordinal);
    }
}
