using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Strg.Core.Domain;

namespace Strg.Infrastructure.Data.Configurations;

public sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Type).HasMaxLength(128).IsRequired();
        builder.Property(e => e.PayloadJson).IsRequired();

        // (TenantId, UserId, CreatedAt) — TenantId leads because the global tenant filter
        // generates WHERE TenantId = @p1 as the first predicate; leftmost-prefix matching
        // then serves the per-user listing and the CreatedAt DESC sort in one index seek.
        builder.HasIndex(e => new { e.TenantId, e.UserId, e.CreatedAt });

        // Unread-badge queries filter by (UserId, ReadAt IS NULL) — covered by the above
        // composite prefix plus an EF-level filter. A dedicated partial index is deferred until
        // we measure actual unread-query cost.

        // Partial unique on EventId: outbox-delivered rows carry the MassTransit MessageId for
        // at-least-once idempotency. Manual/admin writes leave EventId = null; the HasFilter
        // clause excludes those from the unique scope so they don't collide with each other.
        // Mirrors AuditEntryConfiguration exactly — same rationale, including the three-point
        // triangulation (HasDatabaseName pin + QuotaNotificationConsumer equality-match +
        // MigrationTests schema pin) that keeps substring-drift from silently re-opening
        // the duplicate-event storm vector.
        builder.HasIndex(e => e.EventId)
            .IsUnique()
            .HasDatabaseName(NotificationConstraintNames.EventIdUniqueIndex)
            .HasFilter("\"EventId\" IS NOT NULL");
    }
}
