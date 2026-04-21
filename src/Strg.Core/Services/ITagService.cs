using Strg.Core.Domain;
using Strg.Core.Exceptions;

namespace Strg.Core.Services;

/// <summary>
/// User-scoped tag operations on files. A tag is a <c>(fileId, userId, key, value, valueType)</c>
/// quintuple; the same key on the same file for two different users is two independent records.
/// <b>Tenant visibility (tenant-wide shared tags) is deferred to v0.2</b> per the phase-6 design
/// decisions — every method here enforces (fileId, userId) scoping exclusively.
///
/// <para><b>Typed values.</b> <see cref="TagValueType"/> is metadata for the client's renderer
/// — the value is always stored as a string. This keeps the schema single-column while giving
/// GraphQL filters a type discriminator. Callers that need to filter by "number > 100" interpret
/// the string via ValueType; the DB does no type-aware comparison.</para>
///
/// <para><b>Key case-folding.</b> Keys are case-insensitive: <c>"Project"</c> and <c>"project"</c>
/// address the same tag. <see cref="Tag.Key"/>'s init-setter lowercases on entry, so the unique
/// index on <c>(FileId, UserId, Key)</c> is a plain B-tree — no functional LOWER() expression
/// required. Callers pass keys in any case and the service normalizes before comparison.</para>
///
/// <para><b>UserId provenance.</b> The <c>userId</c> parameter is sourced from the authenticated
/// JWT subject at the mutation layer — never from a client-supplied field. The service trusts
/// its callers on this point; it does NOT re-authorize the userId against the request context.
/// GraphQL resolvers / REST endpoints are responsible for that projection.</para>
/// </summary>
public interface ITagService
{
    /// <summary>
    /// Adds the tag if absent, otherwise overwrites its <see cref="Tag.Value"/> and
    /// <see cref="Tag.ValueType"/>. Returns the persisted entity (the newly-added row or the
    /// mutated existing one). Key is normalized to lowercase before comparison.
    /// </summary>
    /// <exception cref="ValidationException">Key fails format/length constraints, or value
    /// exceeds the 255-char storage ceiling.</exception>
    Task<Tag> UpsertAsync(
        Guid fileId,
        Guid userId,
        string key,
        string value,
        TagValueType valueType = TagValueType.String,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the (fileId, userId, key) tag. Returns <see langword="true"/> if a row was
    /// deleted, <see langword="false"/> if no matching tag existed (idempotent — no exception
    /// on missing). Emits an audit entry only when a row actually changed.
    /// </summary>
    Task<bool> RemoveAsync(
        Guid fileId,
        Guid userId,
        string key,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes every tag for (fileId, userId). Returns the number of rows deleted. Used by
    /// file-deletion and user-offboarding flows where tags are cleaned up in bulk.
    /// </summary>
    Task<int> RemoveAllAsync(
        Guid fileId,
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns every tag (fileId, userId) holds, ordered by key for stable presentation. Never
    /// returns tags owned by other users on the same file — user-scoping is the product-level
    /// invariant, not a filter callers can opt out of.
    /// </summary>
    Task<IReadOnlyList<Tag>> GetTagsAsync(
        Guid fileId,
        Guid userId,
        CancellationToken cancellationToken = default);
}
