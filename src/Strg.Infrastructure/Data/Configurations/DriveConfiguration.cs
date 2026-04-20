using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Strg.Core.Domain;

namespace Strg.Infrastructure.Data.Configurations;

public sealed class DriveConfiguration : IEntityTypeConfiguration<Drive>
{
    public void Configure(EntityTypeBuilder<Drive> builder)
    {
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Name).IsRequired().HasMaxLength(64);
        builder.Property(d => d.ProviderType).IsRequired().HasMaxLength(50);
        builder.Property(d => d.ProviderConfig).IsRequired().HasColumnType("text");
        builder.Property(d => d.VersioningPolicy).IsRequired().HasColumnType("text");
        builder.HasIndex(d => new { d.TenantId, d.Name }).IsUnique();
        builder.Ignore(d => d.IsDeleted);
    }
}
