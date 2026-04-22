using MassTransit;
using Microsoft.Extensions.Logging;
using Strg.Core.Events;

namespace Strg.WebDav.Consumers;

/// <summary>
/// STRG-073 — bridges <see cref="UserPasswordChangedEvent"/> from the outbox to the in-process
/// <see cref="IWebDavJwtCache"/>, evicting all cached Basic-Auth → JWT exchanges keyed by the
/// user's email so a re-attempt with the old password sees a cache miss and the ROPC exchange
/// rejects the stale credential.
///
/// <para><b>Why this consumer lives in <c>Strg.WebDav</c>.</b> The cache surface is a WebDAV
/// feature abstraction; Strg.Core cannot reference it without inverting the layer dependency.
/// Register through the <c>configureConsumers</c> hook in <c>AddStrgMassTransit</c>'s caller so
/// the MassTransit wiring in <c>Strg.Infrastructure</c> stays ignorant of WebDAV types — same
/// shape as <c>GraphQLSubscriptionPublisher</c>.</para>
///
/// <para><b>Race-window semantics.</b> The cache key is
/// <c>webdav-jwt:{email}:{HEX(SHA256(password))}</c>. A password change produces a different key,
/// so the NEW password never has a stale hit — the window only exists for the OLD password's
/// cached entry. Event-driven invalidation bounds that window to outbox-dispatch latency (target:
/// seconds) vs the full 14-min JWT cache TTL. At-least-once delivery is fine: a duplicate
/// <see cref="IWebDavJwtCache.InvalidateUser"/> on an already-empty side-index is a no-op.</para>
///
/// <para><b>No MassTransit retry-policy override.</b> The global exponential-retry policy in
/// <c>MassTransitExtensions</c> (5 retries, 1s→30s backoff, then per-consumer DLX) is the correct
/// posture — <see cref="IWebDavJwtCache.InvalidateUser"/> is a pure in-memory operation that does
/// not fail under load; any exception would be a programming bug that should surface to the
/// dead-letter queue for investigation rather than being silently swallowed here.</para>
/// </summary>
public sealed class WebDavJwtCacheInvalidationConsumer(
    IWebDavJwtCache cache,
    ILogger<WebDavJwtCacheInvalidationConsumer> logger) : IConsumer<UserPasswordChangedEvent>
{
    public Task Consume(ConsumeContext<UserPasswordChangedEvent> context)
    {
        cache.InvalidateUser(context.Message.Email);
        logger.LogDebug(
            "Evicted WebDAV JWT cache entries for user {UserId} in tenant {TenantId} after password change.",
            context.Message.UserId,
            context.Message.TenantId);
        return Task.CompletedTask;
    }
}
