using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Strg.Core.Domain;

namespace Strg.Infrastructure.Data.Configurations;

public sealed class TagConfiguration : IEntityTypeConfiguration<Tag>
{
    public void Configure(EntityTypeBuilder<Tag> builder)
    {
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Key).IsRequired().HasMaxLength(255);
        builder.Property(t => t.Value).IsRequired().HasMaxLength(255);
        builder.Property(t => t.ValueType).HasConversion<string>().HasMaxLength(10);
        // Case-insensitive uniqueness: in SQL it'll be enforced via raw SQL index in migration
        // For SQLite/InMemory testing: use HasIndex(t => new { t.FileId, t.UserId, t.Key })
        builder.HasIndex(t => new { t.FileId, t.UserId, t.Key }).IsUnique();
        builder.Ignore(t => t.IsDeleted);
    }
}
