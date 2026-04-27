using Strg.Core.Domain;

namespace Strg.Application.Features.Files.List;

/// <summary>
/// Page of <see cref="FileItem"/> rows plus the paging metadata the caller needs to fetch the
/// next page. Holding raw entities (rather than DTOs) keeps the projection-to-wire-shape concern
/// in the surface layer (REST endpoint, future GraphQL resolver), so each transport can shape
/// its own response without forcing the others to share a constraining DTO.
/// </summary>
public sealed record ListFilesResult(
    IReadOnlyList<FileItem> Items,
    int Page,
    int PageSize,
    int TotalCount);
