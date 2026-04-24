using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Strg.Core.Domain;

namespace Strg.Application.Abstractions;

/// <summary>
/// Application-layer seam over Strg.Infrastructure.Data.StrgDbContext. Handlers depend on this
/// interface so their persistence dependency is a plain port — not an EF DbContext subclass —
/// which keeps Strg.Application compilable without referencing Strg.Infrastructure and makes
/// handlers trivial to unit-test against an in-memory implementation.
/// </summary>
public interface IStrgDbContext
{
    DbSet<Tenant> Tenants { get; }
    DbSet<User> Users { get; }
    DbSet<Drive> Drives { get; }
    DbSet<FileItem> Files { get; }
    DbSet<FileVersion> FileVersions { get; }
    DbSet<FileKey> FileKeys { get; }
    DbSet<Tag> Tags { get; }
    DbSet<AuditEntry> AuditEntries { get; }
    DbSet<Notification> Notifications { get; }
    DbSet<InboxRule> InboxRules { get; }
    DbSet<FileLock> FileLocks { get; }

    DatabaseFacade Database { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
