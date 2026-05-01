using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Strg.Core.Domain;
using Strg.Core.Events;
using Strg.Core.Services;
using Strg.Infrastructure.Data;
using Strg.Infrastructure.Data.Configurations;
using Strg.Infrastructure.Observability;
using Strg.Plugin.Abstractions.Storage;

namespace Strg.Infrastructure.Thumbnails;

/// <summary>
/// Orchestration layer for the thumbnail subsystem (STRG-330/331). Two callers:
/// <list type="bullet">
///   <item><c>ThumbnailGenerationConsumer</c> on <c>FileUploadedEvent</c> (upload path).</item>
///   <item><c>ThumbnailGenerationConsumer</c> on <c>ThumbnailGenerationRequestedEvent</c> (admin backfill).</item>
/// </list>
/// Both share <see cref="GenerateAllAsync"/> so the upload and backfill paths exercise the
/// same idempotency / metrics / safeguard logic.
///
/// <para><b>Encrypted-drive carve-out (D17).</b> When <c>Drive.EncryptionEnabled</c> is true,
/// writes a single <see cref="ThumbnailStatus.Unsupported"/> row + <c>strg_thumbnails_skipped_total{reason=encrypted-drive}</c>
/// metric and exits. Decryption-aware reads land in a future phase (planned STRG-347).</para>
///
/// <para><b>Idempotency.</b> SQLSTATE 23505 + exact <c>ConstraintName</c> equality discriminates
/// at-least-once redelivery from unrelated unique violations — same triangulation as
/// <c>AuditLogConsumer.IsEventIdUniqueViolation</c>.</para>
/// </summary>
public sealed class ThumbnailService(
    StrgDbContext db,
    IThumbnailRepository repo,
    IThumbnailGeneratorRegistry registry,
    IStorageProviderRegistry storageRegistry,
    IPublishEndpoint bus,
    IOptions<ThumbnailOptions> options,
    StrgMetrics metrics,
    TimeProvider clock,
    ILogger<ThumbnailService> logger) : IThumbnailService
{
    private const string UniqueViolationSqlState = "23505";
    private const int MagicByteWindow = 64;

    public async Task GenerateAllAsync(
        Guid tenantId,
        Guid fileId,
        Guid fileVersionId,
        Guid driveId,
        IReadOnlyList<string> variants,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(variants);

        var optionsValue = options.Value;
        if (!optionsValue.Enabled)
        {
            logger.LogDebug("Thumbnails disabled by config; skipping FileId={FileId}", fileId);
            return;
        }

        // Consumer scope has empty ITenantContext (MassTransit dispatches outside any HTTP
        // request — same load-bearing rule as AuditLogConsumer). The global tenant + soft-delete
        // filters on TenantedEntity therefore evaluate to "TenantId == Guid.Empty AND DeletedAt
        // IS NULL", which excludes every legitimate row. We bypass via IgnoreQueryFilters and
        // re-apply BOTH predicates inline using the event-carried tenantId — the same carve-out
        // pattern as UserRepository.GetByEmailAsync (pre-auth lookup) and DriveRepository's
        // tenant-aware reads.
        var file = await db.Files
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(
                f => f.Id == fileId && f.TenantId == tenantId && !f.DeletedAt.HasValue,
                cancellationToken)
            .ConfigureAwait(false);
        if (file is null)
        {
            logger.LogDebug("File {FileId} not visible (deleted/cross-tenant); skipping thumbnails", fileId);
            return;
        }

        // FileVersion is Entity (not TenantedEntity) — no global filter applies. Tenant
        // isolation is transitive via FileVersion.FileId → FileItem.TenantId, already enforced
        // by the FileItem read above. If fileVersionId is Guid.Empty (FileUploadedEvent path),
        // pick the latest version of the file.
        var version = fileVersionId == Guid.Empty
            ? await db.FileVersions
                .AsNoTracking()
                .Where(v => v.FileId == fileId)
                .OrderByDescending(v => v.VersionNumber)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false)
            : await db.FileVersions
                .AsNoTracking()
                .FirstOrDefaultAsync(v => v.Id == fileVersionId && v.FileId == fileId, cancellationToken)
                .ConfigureAwait(false);
        if (version is null)
        {
            logger.LogDebug("FileVersion {FileVersionId} not found; skipping thumbnails", fileVersionId);
            return;
        }

        var drive = await db.Drives
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(
                d => d.Id == driveId && d.TenantId == tenantId && !d.DeletedAt.HasValue,
                cancellationToken)
            .ConfigureAwait(false);
        if (drive is null)
        {
            logger.LogDebug("Drive {DriveId} not visible; skipping thumbnails", driveId);
            return;
        }

        // Encrypted-drive carve-out (D17). One row only, deterministic variant/format so
        // re-delivery hits the unique-index idempotency path.
        if (drive.EncryptionEnabled)
        {
            await TryAddCarveOutRowAsync(
                tenantId, fileId, version.Id,
                variant: ThumbnailVariants.Thumb,
                reason: "encrypted-drive-not-yet-supported",
                cancellationToken).ConfigureAwait(false);
            metrics.IncrementThumbnailSkipped("encrypted-drive");
            return;
        }

        // Source-size cap (STRG-338) — rejects before any blob I/O.
        if (version.Size > optionsValue.MaxSourceSizeBytes)
        {
            foreach (var variant in variants)
            {
                await TryAddCarveOutRowAsync(
                    tenantId, fileId, version.Id, variant,
                    reason: "too-large", cancellationToken).ConfigureAwait(false);
            }
            metrics.IncrementThumbnailSkipped("too-large");
            return;
        }

        var provider = ResolveProvider(drive);

        // Sniff the source MIME from the first ~64 bytes (D12). FileItem.MimeType is
        // client-provided and untrusted; we trust the bytes.
        var headBuffer = new byte[MagicByteWindow];
        int read;
        await using (var headStream = await provider.ReadAsync(version.StorageKey, 0, cancellationToken).ConfigureAwait(false))
        {
            read = await ReadFullyAsync(headStream, headBuffer, cancellationToken).ConfigureAwait(false);
        }

        var sniffedMime = MimeSniffer.Detect(headBuffer.AsSpan(0, read));
        if (sniffedMime is null)
        {
            foreach (var variant in variants)
            {
                await TryAddCarveOutRowAsync(
                    tenantId, fileId, version.Id, variant,
                    reason: "unknown-mime", cancellationToken).ConfigureAwait(false);
            }
            metrics.IncrementThumbnailSkipped("unknown-mime");
            return;
        }

        var generator = registry.Resolve(sniffedMime, headBuffer.AsSpan(0, read));
        if (generator is null)
        {
            foreach (var variant in variants)
            {
                await TryAddCarveOutRowAsync(
                    tenantId, fileId, version.Id, variant,
                    reason: "no-generator", cancellationToken).ConfigureAwait(false);
            }
            metrics.IncrementThumbnailSkipped("no-generator");
            return;
        }

        // Per-variant fan-out. Each variant is its own atomic step (insert Pending → generate →
        // write blob → update Ready). Failure on variant N does not roll back N-1.
        foreach (var variant in variants)
        {
            await GenerateOneAsync(
                tenantId, fileId, version.Id, driveId, variant, generator,
                provider, version.StorageKey, version.Size, sniffedMime,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task GenerateOneAsync(
        Guid tenantId,
        Guid fileId,
        Guid fileVersionId,
        Guid driveId,
        string variant,
        IThumbnailGenerator generator,
        IStorageProvider provider,
        string sourceStorageKey,
        long sourceSize,
        string sourceMime,
        CancellationToken cancellationToken)
    {
        const string format = ThumbnailFormats.WebP;

        // Idempotency probe. A re-delivery may find a Ready/Unsupported/Failed row from a
        // prior attempt — short-circuit without re-running the generator. The unique-index
        // catch below remains as the race-safe defence.
        var existing = await repo.GetAsync(fileVersionId, variant, format, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null && existing.Status != ThumbnailStatus.Pending)
        {
            return;
        }

        // Insert (or update) the Pending row. Any 23505 means a concurrent worker landed the
        // row first; we no-op and let them finish.
        ThumbnailEntry pendingRow;
        if (existing is null)
        {
            pendingRow = new ThumbnailEntry
            {
                TenantId = tenantId,
                FileId = fileId,
                FileVersionId = fileVersionId,
                Variant = variant,
                Format = format,
                Status = ThumbnailStatus.Pending,
                GeneratorVersion = generator.Version,
            };
            repo.Add(pendingRow);

            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException ex) when (IsThumbnailUniqueViolation(ex))
            {
                db.ChangeTracker.Clear();
                logger.LogDebug(
                    "ThumbnailService: concurrent worker landed {Variant}/{Format} for {FileVersionId}; no-op",
                    variant, format, fileVersionId);
                return;
            }
        }
        else
        {
            pendingRow = existing;
        }

        ThumbnailGenerationOutcome outcome;
        try
        {
            await using var sourceStream = await provider
                .ReadAsync(sourceStorageKey, 0, cancellationToken)
                .ConfigureAwait(false);

            outcome = await generator.GenerateAsync(
                sourceStream,
                new ThumbnailRequest(
                    Variant: variant,
                    TargetEdgePixels: ThumbnailVariants.EdgePixelsFor(variant),
                    TargetFormat: format,
                    SourceSizeBytes: sourceSize,
                    SourceMimeType: sourceMime),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Caller cancellation — let MassTransit re-queue. Pending row stays.
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "ThumbnailService: generator threw for {FileVersionId}/{Variant}; marking Failed",
                fileVersionId, variant);
            pendingRow.Status = ThumbnailStatus.Failed;
            pendingRow.ErrorReason = $"generator-error: {ex.GetType().Name}";
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            metrics.IncrementThumbnailGenerated(format, variant, "error");
            return;
        }

        switch (outcome)
        {
            case ThumbnailGenerationOutcome.Success success:
                await PromoteToReadyAsync(
                    pendingRow, success, tenantId, fileId, fileVersionId, driveId, variant, format,
                    provider, cancellationToken).ConfigureAwait(false);
                break;

            case ThumbnailGenerationOutcome.Unsupported u:
                pendingRow.Status = ThumbnailStatus.Unsupported;
                pendingRow.ErrorReason = u.Reason;
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                metrics.IncrementThumbnailSkipped("unknown-mime");
                break;

            case ThumbnailGenerationOutcome.SourceCorrupt corrupt:
                pendingRow.Status = ThumbnailStatus.Failed;
                pendingRow.ErrorReason = corrupt.Reason;
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                metrics.IncrementThumbnailGenerated(format, variant, "source-corrupt");
                break;

            case ThumbnailGenerationOutcome.ResourceLimitExceeded rle:
                pendingRow.Status = ThumbnailStatus.Unsupported;
                pendingRow.ErrorReason = rle.Reason;
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                metrics.IncrementThumbnailSkipped("pixel-cap");
                break;

            case ThumbnailGenerationOutcome.TimedOut t:
                pendingRow.Status = ThumbnailStatus.Failed;
                pendingRow.ErrorReason = $"timeout ({t.Limit.TotalSeconds:F0}s)";
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                metrics.IncrementThumbnailGenerated(format, variant, "timed-out");
                break;
        }
    }

    private async Task PromoteToReadyAsync(
        ThumbnailEntry row,
        ThumbnailGenerationOutcome.Success success,
        Guid tenantId,
        Guid fileId,
        Guid fileVersionId,
        Guid driveId,
        string variant,
        string format,
        IStorageProvider provider,
        CancellationToken cancellationToken)
    {
        await using var output = success.Output;

        var key = ThumbnailStorageKeyBuilder.Build(driveId, fileVersionId, variant, format);
        var path = StoragePath.Parse(key);

        await provider.WriteAsync(path.Value, output, cancellationToken).ConfigureAwait(false);

        row.Status = ThumbnailStatus.Ready;
        row.StorageKey = path.Value;
        row.Width = success.Width;
        row.Height = success.Height;
        row.SizeBytes = output.Length;
        row.GeneratedAt = clock.GetUtcNow();
        row.ErrorReason = null;

        // Outbox publish BEFORE SaveChanges — the event and the row update commit atomically
        // through MassTransit's EF outbox.
        await bus.Publish(
            new ThumbnailReadyEvent(tenantId, fileId, fileVersionId, variant, format, success.Width, success.Height),
            cancellationToken).ConfigureAwait(false);

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        metrics.IncrementThumbnailGenerated(format, variant, "ready");
    }

    private async Task TryAddCarveOutRowAsync(
        Guid tenantId,
        Guid fileId,
        Guid fileVersionId,
        string variant,
        string reason,
        CancellationToken cancellationToken)
    {
        var row = new ThumbnailEntry
        {
            TenantId = tenantId,
            FileId = fileId,
            FileVersionId = fileVersionId,
            Variant = variant,
            Format = ThumbnailFormats.WebP,
            Status = ThumbnailStatus.Unsupported,
            ErrorReason = reason,
            GeneratorVersion = "carve-out/v1",
        };
        repo.Add(row);

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsThumbnailUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
        }
    }

    private static async Task<int> ReadFullyAsync(Stream source, byte[] buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await source.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            total += read;
        }
        return total;
    }

    private IStorageProvider ResolveProvider(Drive drive)
    {
        // Mirror FileVersionStore.ResolveProvider — same JSON-config contract.
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        using var json = System.Text.Json.JsonDocument.Parse(drive.ProviderConfig);
        foreach (var property in json.RootElement.EnumerateObject())
        {
            values[property.Name] = property.Value.ValueKind switch
            {
                System.Text.Json.JsonValueKind.String => property.Value.GetString(),
                System.Text.Json.JsonValueKind.Null => null,
                _ => property.Value.GetRawText(),
            };
        }
        var config = new DictionaryStorageProviderConfig(values);
        return storageRegistry.Resolve(drive.ProviderType, config);
    }

    // internal so unit tests in Strg.Api.Tests can verify the discrimination invariant without
    // booting Postgres — same precedent as AuditLogConsumer.IsEventIdUniqueViolation.
    internal static bool IsThumbnailUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException pg
        && pg.SqlState == UniqueViolationSqlState
        && pg.ConstraintName == ThumbnailEntryConstraintNames.UniqueIndex;
}
