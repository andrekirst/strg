using Strg.Core.Domain;

namespace Strg.Application.Features.Users.List;

/// <summary>
/// Page of <see cref="User"/> rows plus paging metadata. Holding raw entities (rather than a
/// transport DTO) keeps wire-shape projection in the surface layer — REST projects to UserDto,
/// future GraphQL surfaces resolve through a schema type — so each transport shapes its own
/// response without forcing the others to share a constraining DTO. Mirrors
/// <see cref="Strg.Application.Features.Files.List.ListFilesResult"/>'s split.
/// </summary>
public sealed record ListUsersResult(
    IReadOnlyList<User> Items,
    int Page,
    int PageSize,
    int TotalCount);
