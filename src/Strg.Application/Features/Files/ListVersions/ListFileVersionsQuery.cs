using Mediator;

namespace Strg.Application.Features.Files.ListVersions;

/// <summary>
/// STRG-044 — list every <see cref="Strg.Core.Domain.FileVersion"/> for a file, ordered
/// descending by <see cref="Strg.Core.Domain.FileVersion.VersionNumber"/> (latest first). A
/// <see langword="null"/> result means the addressed file does not exist on
/// <see cref="DriveId"/>, is owned by another tenant (the global query filter on
/// <c>StrgDbContext.Files</c> hides foreign-tenant rows), or is a directory (directories have
/// no versions). The REST endpoint maps null to HTTP 404.
/// </summary>
public sealed record ListFileVersionsQuery(Guid DriveId, Guid FileId)
    : IQuery<IReadOnlyList<FileVersionView>?>;

/// <summary>
/// Application-layer projection of <see cref="Strg.Core.Domain.FileVersion"/>. Excludes
/// <c>StorageKey</c> deliberately: the storage-backend locator is an implementation detail of
/// the storage providers and must not leak into transport-layer responses (Security Review
/// checklist item).
/// </summary>
public sealed record FileVersionView(
    int VersionNumber,
    long Size,
    string ContentHash,
    DateTimeOffset CreatedAt,
    Guid CreatedBy);
