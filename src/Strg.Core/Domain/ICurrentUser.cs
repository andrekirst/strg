namespace Strg.Core.Domain;

/// <summary>
/// Surfaces the current request's authenticated user identity. Mirrors <see cref="ITenantContext"/>:
/// implementations live in the outer layers (Strg.Infrastructure's HttpCurrentUser for the HTTP
/// stack, test doubles in test projects). Application-layer handlers inject this port instead of
/// carrying <c>UserId</c> as a command field, so wire clients can't spoof the field.
/// </summary>
public interface ICurrentUser
{
    Guid UserId { get; }
}
