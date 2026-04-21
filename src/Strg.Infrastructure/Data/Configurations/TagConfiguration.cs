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
        builder.Property(t => t.ValueType)
            .HasConversion(
                v => v.ToString().ToLowerInvariant(),
                v => Enum.Parse<TagValueType>(v, ignoreCase: true))
            .HasMaxLength(10);

        // Tag.Key is normalized to lowercase on init, so a normal unique index gives
        // case-insensitive uniqueness without needing a functional LOWER() index.
        builder.HasIndex(t => new { t.FileId, t.UserId, t.Key }).IsUnique();

        builder.ToTable("Tags", t => t.HasCheckConstraint(
            "CK_Tags_ValueType",
            "\"ValueType\" IN ('string', 'number', 'boolean')"));

        builder.Ignore(t => t.IsDeleted);
    }
}
