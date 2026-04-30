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

        // Inverse of FileItem.Tags collection navigation. The relationship lets Hot Chocolate's
        // [UseFiltering] traverse `where: { tags: { some: ... } }` against the FileItem aggregate
        // and lets EF apply the Tag global query filter to navigation-driven sub-queries.
        // OnDelete(Cascade) ensures a future hard-delete of a FileItem also clears its tags;
        // soft-delete sets DeletedAt and is filtered out via the global SoftDelete filter.
        builder.HasOne<FileItem>()
            .WithMany(f => f.Tags)
            .HasForeignKey(t => t.FileId)
            .OnDelete(DeleteBehavior.Cascade);

        // Tag.Key is normalized to lowercase on init, so a normal unique index gives
        // case-insensitive uniqueness without needing a functional LOWER() index.
        builder.HasIndex(t => new { t.FileId, t.UserId, t.Key }).IsUnique();

        builder.ToTable("Tags", t => t.HasCheckConstraint(
            "CK_Tags_ValueType",
            "\"ValueType\" IN ('string', 'number', 'boolean')"));

        builder.Ignore(t => t.IsDeleted);
    }
}
