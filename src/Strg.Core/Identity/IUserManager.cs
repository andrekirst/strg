using Strg.Core.Domain;

namespace Strg.Core.Identity;

/// <summary>
/// Application-level user lifecycle operations: create, change password, validate credentials,
/// and brute-force lockout tracking. Distinct from <see cref="IUserRepository"/>, which only
/// handles persistence concerns.
/// </summary>
public interface IUserManager
{
    /// <summary>
    /// Creates a new user with a hashed password. Returns <c>EmailAlreadyExists</c> when the
    /// email is taken within the tenant, or <c>PasswordTooShort</c> when below the minimum length.
    /// </summary>
    Task<Result<User>> CreateUserAsync(CreateUserRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the user's password without verifying the current one. Intended for admin-initiated
    /// resets and the first-run seed flow.
    /// </summary>
    Task<Result> SetPasswordAsync(Guid userId, string newPassword, CancellationToken cancellationToken = default);

    /// <summary>
    /// Changes the user's password after verifying the current one. Returns <c>InvalidPassword</c>
    /// when the current password does not match.
    /// </summary>
    Task<Result> ChangePasswordAsync(
        Guid userId,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns <c>true</c> when <paramref name="password"/> matches the stored hash AND the user
    /// is not currently locked out. Returns <c>false</c> for unknown users (no enumeration).
    /// </summary>
    Task<bool> ValidatePasswordAsync(Guid userId, string password, CancellationToken cancellationToken = default);

    /// <summary>
    /// Increments the failed-login counter and applies the lockout schedule:
    /// 5 failures → 15 min lock; 10 failures → 1 hour lock.
    /// </summary>
    Task RecordFailedLoginAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resets the failed-login counter to zero and clears any active lockout. Called after a
    /// successful login.
    /// </summary>
    Task ResetFailedLoginsAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cross-tenant email lookup used by the password-grant token endpoint to resolve a user
    /// before the request has any tenant context. Returns <c>null</c> when the email is unknown
    /// or the user is soft-deleted. Never leak existence to clients — callers must respond with
    /// the same generic "invalid credentials" error whether this returns null or
    /// <see cref="ValidatePasswordAsync"/> returns false.
    ///
    /// Prefer <see cref="ValidateCredentialsAsync"/> for authentication flows; this method
    /// exists for callers that need the user object for reasons other than authentication.
    /// </summary>
    Task<User?> FindForLoginAsync(string email, CancellationToken cancellationToken = default);

    /// <summary>
    /// Authenticates a set of credentials in one call. Returns the <see cref="User"/> on success,
    /// or <c>null</c> for ANY failure mode (missing email, unknown email, wrong password, locked
    /// account). All failure paths spend the same wall-clock time (one full PBKDF2 verify), so
    /// observers cannot distinguish failure modes from request latency — this is the designated
    /// single-timing-envelope entry point for the password-grant token endpoint.
    ///
    /// Internally manages the failed-login counter and lockout state: do NOT additionally call
    /// <see cref="RecordFailedLoginAsync"/> or <see cref="ResetFailedLoginsAsync"/> on the result.
    /// </summary>
    Task<User?> ValidateCredentialsAsync(string email, string password, CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-resolves a user from the current database row for the refresh-token grant flow. Returns
    /// <c>null</c> when the user is missing, soft-deleted, or currently locked out — the caller
    /// MUST fail-closed with <c>invalid_grant</c> in that case. A non-null result means the
    /// holder of a valid refresh token is still allowed to obtain new access tokens; the caller
    /// rebuilds claims from this fresh row so role downgrades, email changes, and tenant moves
    /// propagate on the next refresh instead of waiting for refresh-token expiry.
    ///
    /// <para>
    /// Uses the pre-auth lookup path: the refresh endpoint is anonymous, OpenIddict has validated
    /// the refresh token but ASP.NET Core's tenant context is not populated for this request, so
    /// a tenant-scoped query would always resolve to <c>Guid.Empty</c> and return null.
    /// </para>
    /// </summary>
    Task<User?> FindForRefreshAsync(Guid userId, CancellationToken cancellationToken = default);
}
