using MassTransit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Strg.Core.Events;
using Strg.Core.Services;
using Strg.Infrastructure.Thumbnails;

namespace Strg.Infrastructure.Messaging.Consumers;

/// <summary>
/// STRG-331 — generates thumbnails on upload (<see cref="FileUploadedEvent"/>) and on
/// admin-triggered backfill (<see cref="ThumbnailGenerationRequestedEvent"/>). Both event
/// types route through the same <see cref="IThumbnailService"/> orchestration so idempotency
/// + metrics + safeguard logic stay identical.
///
/// <para><b>Tenant from payload, not ambient.</b> Same load-bearing rule as
/// <c>AuditLogConsumer</c> — the consumer's DI scope has an empty <c>ITenantContext</c>.</para>
///
/// <para><b>Idempotency.</b> Delegated to <see cref="ThumbnailService"/>'s SQLSTATE-23505 catch
/// against the pinned <c>IX_ThumbnailEntries_FileVersionId_Variant_Format</c> index.</para>
///
/// <para><b>Dead-letter.</b> Sibling <see cref="ThumbnailGenerationFaultObserver"/> logs
/// <c>{TenantId, FileId, Exceptions}</c> after the 5× retry budget exhausts.</para>
/// </summary>
public sealed class ThumbnailGenerationConsumer(
    IThumbnailService thumbnailService,
    IOptions<ThumbnailOptions> options,
    ILogger<ThumbnailGenerationConsumer> logger) :
    IConsumer<FileUploadedEvent>,
    IConsumer<ThumbnailGenerationRequestedEvent>
{
    public Task Consume(ConsumeContext<FileUploadedEvent> context)
    {
        var msg = context.Message;
        return ProcessAsync(
            msg.TenantId, msg.FileId, fileVersionId: Guid.Empty, msg.DriveId,
            context.CancellationToken);
    }

    public Task Consume(ConsumeContext<ThumbnailGenerationRequestedEvent> context)
    {
        var msg = context.Message;
        return ProcessAsync(
            msg.TenantId, msg.FileId, msg.FileVersionId, msg.DriveId,
            context.CancellationToken);
    }

    private async Task ProcessAsync(
        Guid tenantId,
        Guid fileId,
        Guid fileVersionId,
        Guid driveId,
        CancellationToken cancellationToken)
    {
        var optionsValue = options.Value;

        // FileUploadedEvent doesn't carry the FileVersionId (the upload event predates this
        // tranche). Resolve the latest version inside the service when fileVersionId is empty.
        // Backfill events DO carry it explicitly so the admin mutation can target a specific
        // version (e.g., for a generator-version regen).
        if (fileVersionId == Guid.Empty)
        {
            // Service handles the resolution by querying the latest version for the file.
            // We could do it here but keeping the lookup inside the service centralises the
            // tenant-filter discipline.
        }

        try
        {
            await thumbnailService.GenerateAllAsync(
                tenantId, fileId, fileVersionId, driveId,
                optionsValue.Variants, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Re-throw so MassTransit retries. Logging here gives operators visibility before
            // the eventual dead-letter (which the fault observer also logs).
            logger.LogWarning(ex,
                "ThumbnailGenerationConsumer: error processing tenant={TenantId} file={FileId} version={FileVersionId}",
                tenantId, fileId, fileVersionId);
            throw;
        }
    }
}

/// <summary>
/// Dead-letter observer for the thumbnail generation pipeline. Logs only safe scalars —
/// <c>TenantId</c>, <c>FileId</c>, and the projected <c>{ExceptionType}: {Message}</c> array.
/// No PII, no MIME sniffing output, no path strings.
/// </summary>
public sealed class ThumbnailGenerationFaultObserver(ILogger<ThumbnailGenerationFaultObserver> logger) :
    IConsumer<Fault<FileUploadedEvent>>,
    IConsumer<Fault<ThumbnailGenerationRequestedEvent>>
{
    public Task Consume(ConsumeContext<Fault<FileUploadedEvent>> context)
    {
        logger.LogError(
            "Dead-letter: thumbnail FileUploadedEvent dispatch failed after retries. Tenant={TenantId} File={FileId} Exceptions={Exceptions}",
            context.Message.Message.TenantId,
            context.Message.Message.FileId,
            ProjectExceptions(context.Message.Exceptions));
        return Task.CompletedTask;
    }

    public Task Consume(ConsumeContext<Fault<ThumbnailGenerationRequestedEvent>> context)
    {
        logger.LogError(
            "Dead-letter: ThumbnailGenerationRequestedEvent dispatch failed after retries. Tenant={TenantId} File={FileId} Exceptions={Exceptions}",
            context.Message.Message.TenantId,
            context.Message.Message.FileId,
            ProjectExceptions(context.Message.Exceptions));
        return Task.CompletedTask;
    }

    // Same projection discipline as AuditLogConsumer.ProjectExceptions — exclude .Data, .StackTrace,
    // and .ToString() so FK DETAIL / parameter values never reach the structured-log surface.
    private static string[] ProjectExceptions(ExceptionInfo[] exceptions) =>
        exceptions.Select(e => $"{e.ExceptionType}: {e.Message}").ToArray();
}
