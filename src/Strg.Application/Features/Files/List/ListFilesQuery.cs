using Mediator;

namespace Strg.Application.Features.Files.List;

/// <summary>
/// STRG-038 — paginated list of files under a drive, optionally filtered by path prefix and/or
/// recursively descended. A <see langword="null"/> result means the drive does not exist (or is
/// owned by another tenant — the global query filter on <c>StrgDbContext.Drives</c> hides
/// foreign-tenant rows). The REST endpoint maps null to HTTP 404; future GraphQL surfaces would
/// shape it to their own missing-resource convention.
/// </summary>
public sealed record ListFilesQuery(
    Guid DriveId,
    string Path,
    bool Recursive,
    int Page,
    int PageSize,
    string? TagKey = null,
    string? TagValue = null) : IQuery<ListFilesResult?>;
