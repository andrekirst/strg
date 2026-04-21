using MassTransit;
using Microsoft.Extensions.Logging;
using Strg.Core.Events;

namespace Strg.Infrastructure.Messaging.Consumers;

/// <summary>
/// Placeholder audit-log consumer wired up for STRG-061 so the outbox dispatch path is exercised
/// end-to-end. Real implementation lands in STRG-062 (persist Notification row, write AuditEntry,
/// etc.). Today: log-only fallback so we can prove retry + dead-letter semantics without blocking
/// the Tranche-5 consumers on the audit write-through path.
/// </summary>
public sealed class AuditLogConsumer :
    IConsumer<FileUploadedEvent>,
    IConsumer<FileDeletedEvent>,
    IConsumer<FileMovedEvent>,
    IConsumer<Fault<FileUploadedEvent>>,
    IConsumer<Fault<FileDeletedEvent>>,
    IConsumer<Fault<FileMovedEvent>>
{
    private readonly ILogger<AuditLogConsumer> _logger;

    public AuditLogConsumer(ILogger<AuditLogConsumer> logger) => _logger = logger;

    public Task Consume(ConsumeContext<FileUploadedEvent> context)
    {
        // TODO STRG-062: persist AuditEntry row (EventId from context.MessageId for idempotency).
        _logger.LogInformation(
            "AuditLogConsumer received FileUploadedEvent: Tenant={TenantId} File={FileId} Drive={DriveId} User={UserId} Size={Size}",
            context.Message.TenantId, context.Message.FileId, context.Message.DriveId,
            context.Message.UserId, context.Message.Size);
        return Task.CompletedTask;
    }

    public Task Consume(ConsumeContext<FileDeletedEvent> context)
    {
        // TODO STRG-062: persist AuditEntry row.
        _logger.LogInformation(
            "AuditLogConsumer received FileDeletedEvent: Tenant={TenantId} File={FileId} Drive={DriveId} User={UserId}",
            context.Message.TenantId, context.Message.FileId, context.Message.DriveId, context.Message.UserId);
        return Task.CompletedTask;
    }

    public Task Consume(ConsumeContext<FileMovedEvent> context)
    {
        // TODO STRG-062: persist AuditEntry row.
        _logger.LogInformation(
            "AuditLogConsumer received FileMovedEvent: Tenant={TenantId} File={FileId} Drive={DriveId} {OldPath} -> {NewPath}",
            context.Message.TenantId, context.Message.FileId, context.Message.DriveId,
            context.Message.OldPath, context.Message.NewPath);
        return Task.CompletedTask;
    }

    public Task Consume(ConsumeContext<Fault<FileUploadedEvent>> context)
    {
        // TODO STRG-062: write Notification row (admin-only visibility) + structured error log.
        _logger.LogError(
            "Dead-letter: FileUploadedEvent dispatch failed after retries. Tenant={TenantId} File={FileId} Exceptions={@Exceptions}",
            context.Message.Message.TenantId, context.Message.Message.FileId, context.Message.Exceptions);
        return Task.CompletedTask;
    }

    public Task Consume(ConsumeContext<Fault<FileDeletedEvent>> context)
    {
        _logger.LogError(
            "Dead-letter: FileDeletedEvent dispatch failed. Tenant={TenantId} File={FileId} Exceptions={@Exceptions}",
            context.Message.Message.TenantId, context.Message.Message.FileId, context.Message.Exceptions);
        return Task.CompletedTask;
    }

    public Task Consume(ConsumeContext<Fault<FileMovedEvent>> context)
    {
        _logger.LogError(
            "Dead-letter: FileMovedEvent dispatch failed. Tenant={TenantId} File={FileId} Exceptions={@Exceptions}",
            context.Message.Message.TenantId, context.Message.Message.FileId, context.Message.Exceptions);
        return Task.CompletedTask;
    }
}
