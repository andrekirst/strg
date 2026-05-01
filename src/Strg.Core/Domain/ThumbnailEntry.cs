namespace Strg.Core.Domain;

/// <summary>
/// One row per generated thumbnail blob. Carries the variant/format coordinate, the storage
/// key the blob is at, and a lifecycle <see cref="ThumbnailStatus"/>.
///
/// <para><b>Idempotency.</b> The unique constraint <c>(FileVersionId, Variant, Format)</c>
/// (pinned name <c>ThumbnailConstraintNames.UniqueIndex</c> in Strg.Infrastructure) is the
/// load-bearing key for at-least-once redelivery. <c>ThumbnailGenerationConsumer</c> catches
/// the SQLSTATE 23505 + exact <c>ConstraintName</c> equality and treats it as "row already
/// landed on a prior delivery — no-op".</para>
///
/// <para><b>Tenant scoping.</b> Inherits <see cref="TenantedEntity"/>'s global query filter on
/// <c>TenantId</c> + <c>DeletedAt</c>. The consumer must populate <see cref="TenantedEntity.TenantId"/>
/// from the event payload — never from the (empty) ambient <c>ITenantContext</c> in consumer
/// scope. Cleanup soft-deletes via <see cref="TenantedEntity.DeletedAt"/>; the global filter
/// hides soft-deleted rows from regular queries.</para>
///
/// <para><b>Cascade.</b> The EF configuration sets <c>OnDelete(Cascade)</c> from
/// <see cref="FileVersion"/> — pruning a version row physically removes its thumbnail rows.
/// Blob cleanup is explicit (handled by <c>ThumbnailCleanupConsumer</c> on <c>FileDeletedEvent</c>
/// and by <c>FileVersionStore.PruneVersionsAsync</c>'s extended per-version loop).</para>
/// </summary>
public sealed class ThumbnailEntry : TenantedEntity
{
    /// <summary>FK to <see cref="FileVersion.Id"/>. The only durable link from a thumbnail to its source bytes.</summary>
    public required Guid FileVersionId { get; init; }

    /// <summary>
    /// FK to <see cref="FileItem.Id"/>. Denormalised from <c>FileVersion.FileId</c> so the cleanup
    /// consumer can soft-delete every thumbnail for a file in one query without a join — the
    /// <c>FileDeletedEvent</c> only carries the file id, and the per-version chain would force a
    /// nested lookup that adds an avoidable round-trip on every delete.
    /// </summary>
    public required Guid FileId { get; init; }

    /// <summary>One of the whitelisted strings in <c>ThumbnailVariants.All</c> (currently <c>thumb</c>, <c>small</c>, <c>medium</c>).</summary>
    public required string Variant { get; init; }

    /// <summary>Output format (<c>webp</c> in v1; <c>jpeg</c> reserved for fallback).</summary>
    public required string Format { get; init; }

    /// <summary>
    /// Storage key for the blob (e.g. <c>thumbnails/{driveId}/{fileVersionId}/small.webp</c>).
    /// Empty until <see cref="Status"/> reaches <see cref="ThumbnailStatus.Ready"/>; never
    /// concatenated by callers — always built via <c>ThumbnailStorageKeyBuilder.Build</c> and
    /// wrapped in <c>StoragePath.Parse</c> before reaching <c>IStorageProvider</c>.
    /// </summary>
    public string StorageKey { get; set; } = string.Empty;

    public long SizeBytes { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    public ThumbnailStatus Status { get; set; } = ThumbnailStatus.Pending;

    /// <summary>
    /// Bounded human-readable reason for non-<c>Ready</c> states. Capped at 256 chars in EF
    /// configuration so adversarial inputs cannot blow up the audit/log surface.
    /// </summary>
    public string? ErrorReason { get; set; }

    public DateTimeOffset? GeneratedAt { get; set; }

    /// <summary>
    /// Stable identifier for the generator (and its tunable knobs) that produced this row.
    /// Not wired to any auto-regeneration trigger today — exists so a future bump-and-regen
    /// admin action can target rows by version without scanning blob bytes.
    /// </summary>
    public required string GeneratorVersion { get; init; }
}
