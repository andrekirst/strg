using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Strg.Core.Domain;

namespace Strg.Infrastructure.Data.Configurations;

public sealed class UserDriveDefaultConfiguration : IEntityTypeConfiguration<UserDriveDefault>
{
    public void Configure(EntityTypeBuilder<UserDriveDefault> builder)
    {
        builder.HasKey(d => d.Id);
        // Unique per (TenantId, UserId): a user has at most one default drive per tenant. The
        // global tenant filter handles cross-tenant reads; this index is the DB-level backstop
        // against duplicate-row races. SetDefaultDriveHandler upserts on this exact key tuple.
        builder.HasIndex(d => new { d.TenantId, d.UserId }).IsUnique();
        builder.HasIndex(d => d.DriveId);
        builder.Ignore(d => d.IsDeleted);
    }
}
