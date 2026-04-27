namespace Strg.Api.Endpoints;

/// <summary>
/// Wire projection of <see cref="Strg.Core.Domain.FileVersion"/> for the STRG-044 versions list
/// endpoint. Deliberately drops <c>StorageKey</c> (provider-internal addressing — security
/// checklist) and <c>FileId</c> (redundant in a list scoped by file id in the route).
/// <c>BlobSizeBytes</c> is also omitted: the on-disk envelope size is a storage-planning metric
/// and not part of the user-facing version contract.
/// </summary>
public sealed record FileVersionDto(
    int VersionNumber,
    long Size,
    string ContentHash,
    DateTimeOffset CreatedAt,
    Guid CreatedBy);
