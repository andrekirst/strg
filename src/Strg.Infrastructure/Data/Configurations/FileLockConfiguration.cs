using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Strg.Core.Domain;

namespace Strg.Infrastructure.Data.Configurations;

public sealed class FileLockConfiguration : IEntityTypeConfiguration<FileLock>
{
    public void Configure(EntityTypeBuilder<FileLock> builder)
    {
        builder.HasKey(e => e.Id);

        builder.Property(e => e.ResourceUri).IsRequired().HasMaxLength(2048);
        builder.Property(e => e.Token).IsRequired().HasMaxLength(64);
        builder.Property(e => e.OwnerId).IsRequired();
        builder.Property(e => e.OwnerInfo).HasMaxLength(512);
        builder.Property(e => e.ExpiresAt).IsRequired();

        // Token lookup on UNLOCK / If-header validation. Not unique — a pathologically well-aimed
        // collision on 128-bit CSPRNG output is ~2^-64 after a billion locks, so the DB constraint
        // would be a waste of an index page. The uniqueness that matters is "only one active lock
        // per resource", enforced below.
        builder.HasIndex(e => e.Token).HasDatabaseName(FileLockConstraintNames.TokenIndex);

        // The race-safe bit: a FULL unique index on (TenantId, ResourceUri). An earlier draft
        // used a partial index filtered by `"ExpiresAt" > NOW()` but Postgres rejects that at
        // index-build time with `42P17: functions in index predicate must be marked IMMUTABLE` —
        // NOW() is STABLE, not IMMUTABLE, and the predicate must be referentially transparent for
        // index maintenance.
        //
        // Without the partial filter, any existing (active OR expired) row blocks a new LOCK.
        // <see cref="Strg.WebDav.DbLockManager.LockAsync"/> closes that gap by running a DELETE
        // of expired rows BEFORE the INSERT inside the same logical operation. Concurrent LOCKs
        // on the same resource still race-safely serialize on the unique index: the second
        // INSERT raises 23505, DbLockManager catches on constraint-name equality, and returns
        // Conflict — same race semantics, no volatile predicate required.
        builder.HasIndex(e => new { e.TenantId, e.ResourceUri })
            .IsUnique()
            .HasDatabaseName(FileLockConstraintNames.ActiveLockUniqueIndex);
    }
}
