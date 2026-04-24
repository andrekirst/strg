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
/// <para><b>Publisher discipline.</b> Publish BEFORE <c>Microsoft.EntityFrameworkCore.DbContext.SaveChangesAsync(CancellationToken)</c>
/// so the outbox row commits atomically with the password-hash change. MassTransit's EF outbox
/// (wired via <c>UseBusOutbox()</c> in <c>AddStrgMassTransit</c>) buffers the <c>Publish</c> call
/// on the same <c>Microsoft.EntityFrameworkCore.DbContext</c> change tracker, so a single
/// <c>SaveChangesAsync</c> flushes BOTH the entity row and the outbox row in one transaction.
/// Publishing AFTER the save would require a second <c>SaveChangesAsync</c> to flush the outbox
/// row, reopening the dual-write race — a crash between the hash-commit and the outbox-row-commit
/// would leave stale cache entries serving the old password for up to the JWT cache TTL (≤14 min).
/// See <c>UserManager.ChangePasswordAsync</c> / <c>SetPasswordAsync</c> and
/// <c>UserMutationHandlers.ChangePasswordAsync</c> for the canonical publish-before-save shape.</para>
/// </summary>
public sealed record UserPasswordChangedEvent(
    Guid TenantId,
    Guid UserId,
    string Email
) : IDomainEvent;
