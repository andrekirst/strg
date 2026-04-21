namespace Strg.Core.Domain;

/// <summary>
/// Per-<see cref="FileVersion"/> Data Encryption Key (DEK) envelope. Contains the DEK wrapped
/// with the Key Encryption Key (KEK, held by <see cref="Storage.IKeyProvider"/>). Stored in a
/// separate table — NOT alongside the file on disk — so an attacker who exfiltrates the storage
/// volume does not also exfiltrate the keys needed to read it.
///
/// <para>Inherits <see cref="Entity"/>, not <see cref="TenantedEntity"/>. Tenant scoping is
/// transitive: FileKey → FileVersion → FileItem (tenanted). Callers MUST resolve the FileVersion
/// through the tenant-filtered path (<see cref="IFileRepository"/> → <see cref="IFileVersionRepository"/>)
/// before touching FileKey to avoid cross-tenant key disclosure.</para>
///
/// <para>Uniqueness on <see cref="FileVersionId"/> is load-bearing: enforcing it at the DB level
/// means re-writing a FileVersion can never produce two FileKey rows, which would otherwise
/// silently leave one DEK orphaned if a crash interrupted an overwrite.</para>
/// </summary>
public sealed class FileKey : Entity
{
    public Guid FileVersionId { get; init; }

    /// <summary>The DEK after encryption under the KEK. Format is defined by <see cref="Algorithm"/>.</summary>
    public required byte[] EncryptedDek { get; init; }

    /// <summary>
    /// Algorithm identifier — lets us rotate crypto without silently breaking old rows. The v0.1
    /// default is <c>"AES-256-GCM"</c> (nonce || ciphertext || tag layout in <see cref="EncryptedDek"/>).
    /// </summary>
    public required string Algorithm { get; init; } = "AES-256-GCM";

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
