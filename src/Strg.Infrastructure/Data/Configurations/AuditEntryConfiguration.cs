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
        //
        // HasDatabaseName pins the name explicitly (matches EF's convention output today, but
        // the convention could shift on an EF major version). AuditLogConsumer.IsEventIdUniqueViolation
        // equality-checks this exact string via a shared const to discriminate the unique-violation
        // it silently swallows from any future unrelated unique index. MigrationTests asserts
        // the index exists under this name. Three-point triangulation is deliberate — substring
        // matching would silently accept a renamed constraint and trigger a duplicate-event
        // storm through the DLQ after a routine migration.
        builder.HasIndex(e => e.EventId)
            .IsUnique()
            .HasDatabaseName(AuditEntryConstraintNames.EventIdUniqueIndex)
            .HasFilter("\"EventId\" IS NOT NULL");
    }
}
