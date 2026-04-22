using Strg.Core.Domain;

namespace Strg.Core.Events;

/// <summary>
/// Published after a user's password hash is committed to the database. The sole consumer in v0.1
/// is <c>WebDavJwtCacheInvalidationConsumer</c>, which evicts cached Basic-Auth → JWT exchanges
/// keyed by the user's email so an old-password re-attempt sees a cache miss and the ROPC exchange
/// rejects the stale credential.
///
/// <para><b>Why Email and not UserId in the wire shape?</b> The WebDAV JWT cache keys on the login
/// identifier the Basic-Auth client presents, which IS the email in this codebase (see
/// <c>IUserManager.FindForLoginAsync</c>). Keying the consumer's <c>InvalidateUser</c> call on
/// UserId would force the consumer to round-trip the DB just to translate Id → Email. <c>UserId</c>
/// is still carried for audit/observability and any future consumer that prefers the opaque Id.</para>
///
/// <para><b>Publisher discipline.</b> Publish AFTER <see cref="Microsoft.EntityFrameworkCore.DbContext.SaveChangesAsync(System.Threading.CancellationToken)"/>
/// so the outbox row commits atomically with the password-hash change — a crash between the
/// hash-commit and the outbox-row-commit would leave stale cache entries serving the old password
/// for up to the JWT cache TTL (≤14 min). MassTransit's EF outbox makes this atomic when the
/// <c>Publish</c> call is buffered on the same <see cref="Microsoft.EntityFrameworkCore.DbContext"/>.</para>
/// </summary>
public sealed record UserPasswordChangedEvent(
    Guid TenantId,
    Guid UserId,
    string Email
) : IDomainEvent;
