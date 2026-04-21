using Strg.Core.Domain;

namespace Strg.Core.Identity;

/// <summary>
/// Input DTO for <see cref="IUserManager.CreateUserAsync"/>. Callers are responsible for
/// resolving the target tenant before invoking — <see cref="TenantId"/> must reference an
/// existing <see cref="Tenant"/>. v0.1 keeps tenant routing client-side; multi-tenant routing
/// (host/subdomain → tenant) is a v0.2 concern. <see cref="QuotaBytes"/> is optional; when
/// omitted, the <see cref="User"/> default quota applies. Must be non-negative.
/// </summary>
public sealed record CreateUserRequest(
    Guid TenantId,
    string Email,
    string DisplayName,
    string Password,
    UserRole Role = UserRole.User,
    long? QuotaBytes = null);
