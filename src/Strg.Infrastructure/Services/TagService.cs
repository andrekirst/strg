using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Strg.Core.Auditing;
using Strg.Core.Domain;
using Strg.Core.Exceptions;
using Strg.Core.Services;
using Strg.Infrastructure.Data;

namespace Strg.Infrastructure.Services;

/// <summary>
/// EF Core-backed <see cref="ITagService"/>. Validates key/value inputs, normalizes keys to
/// lowercase, delegates state changes to <see cref="ITagRepository"/>, commits via the shared
/// <see cref="StrgDbContext"/>, and emits an audit entry on each state-changing operation.
///
/// <para><b>Tenant-ownership guard (STRG-047 M1).</b> Every method accepting a
/// <c>fileId</c> first resolves it through <see cref="IFileRepository.GetByIdAsync"/>, which
/// routes through the global tenant filter. A fileId belonging to a foreign tenant (or a
/// soft-deleted file) returns null and the method throws <see cref="NotFoundException"/>
/// before any DB state is touched. <b>This is the sole defense against cross-tenant ghost-row
/// writes</b> — <see cref="Tag"/> has no EF FK onto <see cref="FileItem"/> (plain Guid column
/// plus the unique index on <c>(FileId, UserId, Key)</c>), so without this guard a caller in
/// tenant B guessing a tenant-A fileId and a non-colliding key would successfully INSERT a
/// ghost Tag row pointing at a foreign file. v0.2 will retire this obligation by adding a
/// composite <c>(FileId, TenantId) → FileItem(Id, TenantId)</c> FK.</para>
///
/// <para><b>Audit-durability scope.</b> Tag.assigned / tag.removed are logged via
/// <see cref="IAuditService.LogAsync"/> after the tag-op <c>SaveChangesAsync</c>, so an
/// audit-store failure never rolls back the primary op — we swallow and warn. Durability
/// applies only within THIS service's transaction scope (no ambient <c>BeginTransactionAsync</c>
/// opened by a higher-level caller). Inside an ambient transaction opened upstream, both
/// <c>SaveChangesAsync</c> flushes enlist into that transaction and audit rows roll back with
/// the rest of the batch on upstream abort — the expected behaviour for multi-step units of
/// work, worth stating explicitly because the separate <c>SaveChangesAsync</c> pair suggests
/// independence that does not actually hold.</para>
///
/// <para><b>TenantId provenance.</b> New tags inherit <see cref="ITenantContext.TenantId"/>
/// from the ambient request context. Updates of existing tags never mutate TenantId — the
/// entity is init-only on that field. Combined with the tenant-ownership guard above, a caller
/// in tenant B cannot observe, mutate, or create Tag rows against any tenant-A file.</para>
/// </summary>
public sealed partial class TagService(
    StrgDbContext db,
    ITagRepository tagRepository,
    IFileRepository fileRepository,
    ITenantContext tenantContext,
    IAuditService auditService,
    ILogger<TagService> logger) : ITagService
{
    // Conservative character class: alphanumeric + hyphen + dot + underscore. Rules out control
    // chars, path separators, shell meta, and whitespace — rules collectively defeat log-injection
    // in audit Details and prevent keys that would confuse operator tooling.
    [GeneratedRegex(@"^[a-zA-Z0-9._-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyFormatRegex();

    private const int MaxKeyLength = 255;
    private const int MaxValueLength = 255;

    public async Task<Tag> UpsertAsync(
        Guid fileId,
        Guid userId,
        string key,
        string value,
        TagValueType valueType = TagValueType.String,
        CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        ValidateValue(value);
        await EnsureFileVisibleAsync(fileId, cancellationToken).ConfigureAwait(false);

        // Tag.Key's setter lowercases on assignment, but we normalize here too for the audit
        // Details payload + GetByKey lookup so the repository call and the logged row agree.
        var normalizedKey = key.ToLowerInvariant();

        var tag = new Tag
        {
            TenantId = tenantContext.TenantId,
            FileId = fileId,
            UserId = userId,
            Key = normalizedKey,
            Value = value,
            ValueType = valueType,
        };

        await tagRepository.UpsertAsync(tag, cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Re-query to return the canonical persisted entity. If UpsertAsync collapsed onto an
        // existing row, `tag` above was never added — its Id is the stale ctor-assigned one and
        // would mislead callers. The round-trip is one indexed lookup on a unique key; negligible
        // next to the write it follows.
        var persisted = await tagRepository
            .GetByKeyAsync(fileId, userId, normalizedKey, cancellationToken)
            .ConfigureAwait(false);
        if (persisted is null)
        {
            // Cannot happen on a correctly-configured unique index + successful SaveChanges.
            // If it does, the repo and the DB disagree about the index definition — surface
            // loudly rather than returning a phantom.
            throw new InvalidOperationException(
                $"Tag ({fileId}, {userId}, {normalizedKey}) disappeared immediately after upsert.");
        }

        await SafeAuditAsync(
            AuditActions.TagAssigned,
            fileId,
            userId,
            $"key={normalizedKey}; value_type={valueType.ToString().ToLowerInvariant()}",
            cancellationToken).ConfigureAwait(false);

        return persisted;
    }

    public async Task<bool> RemoveAsync(
        Guid fileId,
        Guid userId,
        string key,
        CancellationToken cancellationToken = default)
    {
        await EnsureFileVisibleAsync(fileId, cancellationToken).ConfigureAwait(false);

        var normalizedKey = key.ToLowerInvariant();

        // Probe first so we can return the correct bool and suppress the audit row on a no-op
        // remove. We could infer presence from the db.SaveChangesAsync rows-affected count, but
        // the repo method is designed to not surface that and adding a plumbing path just for
        // this bool would bleed implementation detail into the interface.
        var existing = await tagRepository
            .GetByKeyAsync(fileId, userId, normalizedKey, cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
        {
            return false;
        }

        await tagRepository.RemoveAsync(fileId, userId, normalizedKey, cancellationToken)
            .ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await SafeAuditAsync(
            AuditActions.TagRemoved,
            fileId,
            userId,
            $"key={normalizedKey}",
            cancellationToken).ConfigureAwait(false);

        return true;
    }

    public async Task<int> RemoveAllAsync(
        Guid fileId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        await EnsureFileVisibleAsync(fileId, cancellationToken).ConfigureAwait(false);

        var existing = await tagRepository.GetByFileAsync(fileId, userId, cancellationToken)
            .ConfigureAwait(false);
        if (existing.Count == 0)
        {
            return 0;
        }

        await tagRepository.RemoveAllAsync(fileId, userId, cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Emit a single bulk-remove audit entry rather than N per-key rows. The Details payload
        // records the count so the audit trail captures scale without losing intent; per-key
        // drill-down belongs in a dedicated tag-history table, not the audit stream.
        await SafeAuditAsync(
            AuditActions.TagRemoved,
            fileId,
            userId,
            $"bulk=true; count={existing.Count}",
            cancellationToken).ConfigureAwait(false);

        return existing.Count;
    }

    public Task<IReadOnlyList<Tag>> GetTagsAsync(
        Guid fileId,
        Guid userId,
        CancellationToken cancellationToken = default)
        => tagRepository.GetByFileAsync(fileId, userId, cancellationToken);

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ValidationException("Tag key must not be empty.", nameof(key));
        }
        if (key.Length > MaxKeyLength)
        {
            throw new ValidationException(
                $"Tag key must not exceed {MaxKeyLength} characters.", nameof(key));
        }
        if (!KeyFormatRegex().IsMatch(key))
        {
            throw new ValidationException(
                "Tag key may contain only letters, digits, '.', '-', and '_'.", nameof(key));
        }
    }

    private static void ValidateValue(string value)
    {
        // Empty value is allowed — a boolean-valued tag may legitimately store "" as a sentinel.
        // Only length is enforced; the content is opaque to this layer.
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > MaxValueLength)
        {
            throw new ValidationException(
                $"Tag value must not exceed {MaxValueLength} characters.", nameof(value));
        }
    }

    private async Task EnsureFileVisibleAsync(Guid fileId, CancellationToken cancellationToken)
    {
        // Tenant-ownership guard. `IFileRepository.GetByIdAsync` routes through the global
        // tenant filter and returns null for (a) foreign-tenant fileIds, (b) soft-deleted files,
        // (c) non-existent files. All three collapse to NotFoundException so the caller cannot
        // distinguish them — if a later feature needs to, it goes through an admin-only service
        // with its own AuthPolicy gate, never this user-facing surface.
        var file = await fileRepository.GetByIdAsync(fileId, cancellationToken).ConfigureAwait(false);
        if (file is null)
        {
            throw new NotFoundException($"File '{fileId}' not found.");
        }
    }

    private async Task SafeAuditAsync(
        string action,
        Guid fileId,
        Guid userId,
        string details,
        CancellationToken cancellationToken)
    {
        try
        {
            await auditService.LogAsync(
                new AuditEntry
                {
                    TenantId = tenantContext.TenantId,
                    UserId = userId,
                    Action = action,
                    ResourceType = "FileItem",
                    ResourceId = fileId,
                    Details = details,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Audit-store outage is a logging concern. Per IAuditService xmldoc, we must not
            // fail the user's primary op — log and move on. CancellationToken-driven aborts
            // re-throw so the task respects cooperative cancellation.
            if (ex is OperationCanceledException)
            {
                throw;
            }
            logger.LogWarning(
                ex,
                "TagService: audit write failed for {Action} on file {FileId} by user {UserId}; tag op succeeded",
                action,
                fileId,
                userId);
        }
    }
}
