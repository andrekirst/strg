using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Strg.Core.Domain;

namespace Strg.Infrastructure.Data.Configurations;

public sealed class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntry>
{
    public void Configure(EntityTypeBuilder<AuditEntry> builder)
    {
        builder.HasKey(e => e.Id);

        // Read-path indexes from STRG-062 spec — admin audit queries hit (tenant, time),
        // user-activity queries hit (user, time), and resource-history queries hit (resource, type).
        builder.HasIndex(e => new { e.TenantId, e.PerformedAt });
        builder.HasIndex(e => new { e.UserId, e.PerformedAt });
        builder.HasIndex(e => new { e.ResourceId, e.ResourceType });

        // Partial unique index: scopes the constraint to outbox-delivered rows where
        // EventId is populated with the MassTransit MessageId. Legacy writers (auth,
        // tag, prune) emit rows with EventId = null, and those must not collide with
        // each other — NULLs are excluded from the unique scope by the filter clause.
        // This is the "ON CONFLICT DO NOTHING" guard for at-least-once redelivery.
        builder.HasIndex(e => e.EventId)
            .IsUnique()
            .HasFilter("\"EventId\" IS NOT NULL");
    }
}
