using Mediator;
using Strg.Core.Domain;

namespace Strg.Application.Features.Tags.ListTagsForFile;

/// <summary>
/// Returns every tag the current user owns on the given file, ordered by key. Queries are not
/// Result-wrapped — a missing file surfaces as an empty list (read-path convention: "file absent"
/// and "file present but untagged" are indistinguishable by design, preventing existence probes).
/// </summary>
public sealed record ListTagsForFileQuery(Guid FileId) : IQuery<IReadOnlyList<Tag>>;
