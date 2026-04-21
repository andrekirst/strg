using System.Net.Mail;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Strg.Core;
using Strg.Core.Domain;
using Strg.Core.Identity;
using Strg.Core.Services;
using Strg.Infrastructure.Data;

namespace Strg.Infrastructure.Identity;

public sealed class UserManager(
    IUserRepository userRepository,
    IPasswordHasher passwordHasher,
    StrgDbContext db) : IUserManager
{
    private const int ShortLockoutThreshold = 5;
    private const int LongLockoutThreshold = 10;
    private const string PostgresUniqueViolation = "23505";
    private static readonly TimeSpan ShortLockout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan LongLockout = TimeSpan.FromHours(1);

    public async Task<Result<User>> CreateUserAsync(CreateUserRequest request, CancellationToken cancellationToken = default)
    {
        if (!IsValidEmail(request.Email))
        {
            return Result<User>.Failure(UserManagerErrors.InvalidEmail, "Email is not a valid email address.");
        }

        if (!IsPasswordLongEnough(request.Password))
        {
            return Result<User>.Failure(
                UserManagerErrors.PasswordTooShort,
                $"Password must be at least {UserManagerErrors.MinimumPasswordLength} characters.");
        }

        if (request.QuotaBytes is < 0)
        {
            return Result<User>.Failure(UserManagerErrors.InvalidQuota, "Quota must be non-negative.");
        }

        var normalizedEmail = request.Email.ToLowerInvariant();

        var existing = await userRepository.GetByEmailAsync(request.TenantId, normalizedEmail, cancellationToken);
        if (existing is not null)
        {
            return Result<User>.Failure(UserManagerErrors.EmailAlreadyExists, "An account with that email already exists.");
        }

        var user = new User
        {
            TenantId = request.TenantId,
            Email = normalizedEmail,
            DisplayName = request.DisplayName,
            PasswordHash = passwordHasher.Hash(request.Password),
            Role = request.Role,
        };

        if (request.QuotaBytes.HasValue)
        {
            user.QuotaBytes = request.QuotaBytes.Value;
        }

        await userRepository.AddAsync(user, cancellationToken);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg && pg.SqlState == PostgresUniqueViolation)
        {
            // Authoritative defence against the pre-check + insert race: two concurrent
            // registrations with the same email can both pass GetByEmailAsync, and only the
            // UNIQUE(tenant_id, email) index catches the second one at commit time.
            return Result<User>.Failure(UserManagerErrors.EmailAlreadyExists, "An account with that email already exists.");
        }

        return Result<User>.Success(user);
    }

    /// <summary>
    /// Replaces the user's password without verifying the current one. Intended for admin-initiated
    /// resets and the first-run seed flow.
    ///
    /// CALLER RESPONSIBILITY: this method does NOT enforce that the caller is an admin. The endpoint
    /// or GraphQL mutation invoking it MUST gate the call with an authorization policy (e.g.
    /// [Authorize(Policy = AuthPolicies.Admin)]). A future call site that forgets this is a
    /// privilege-escalation bug.
    /// </summary>
    public async Task<Result> SetPasswordAsync(Guid userId, string newPassword, CancellationToken cancellationToken = default)
    {
        if (!IsPasswordLongEnough(newPassword))
        {
            return Result.Failure(
                UserManagerErrors.PasswordTooShort,
                $"Password must be at least {UserManagerErrors.MinimumPasswordLength} characters.");
        }

        // Post-auth caller (admin reset endpoint): the global tenant filter scopes the lookup to
        // the admin's own tenant, so an admin in tenant A cannot SetPassword on a user in tenant B.
        var user = await FindByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return Result.Failure(UserManagerErrors.UserNotFound, "User not found.");
        }

        user.PasswordHash = passwordHasher.Hash(newPassword);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result> ChangePasswordAsync(
        Guid userId,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        // Length check before the 100ms PBKDF2 verify — fail fast on cheap validation.
        if (!IsPasswordLongEnough(newPassword))
        {
            return Result.Failure(
                UserManagerErrors.PasswordTooShort,
                $"Password must be at least {UserManagerErrors.MinimumPasswordLength} characters.");
        }

        // Post-auth caller (user changing their own password): tenant filter applies.
        var user = await FindByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return Result.Failure(UserManagerErrors.UserNotFound, "User not found.");
        }

        if (!passwordHasher.Verify(currentPassword, user.PasswordHash))
        {
            return Result.Failure(UserManagerErrors.InvalidPassword, "Current password is incorrect.");
        }

        user.PasswordHash = passwordHasher.Hash(newPassword);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<bool> ValidatePasswordAsync(Guid userId, string password, CancellationToken cancellationToken = default)
    {
        var user = await FindByIdPreAuthAsync(userId, cancellationToken);
        if (user is null)
        {
            // Run a real PBKDF2 verify against a dummy hash and discard the result, so the wall-
            // clock time for the unknown-user path matches the existing-user wrong-password path.
            _ = passwordHasher.Verify(password, passwordHasher.CanaryHash);
            return false;
        }

        // A naturally-expired lock must not leave the counter above the 5-failure threshold, or
        // the very next failure would re-lock at the 1h mark. Treat expiry as a fresh start.
        if (user.LockedUntil.HasValue && user.LockedUntil <= DateTimeOffset.UtcNow)
        {
            user.FailedLoginAttempts = 0;
            user.LockedUntil = null;
            await db.SaveChangesAsync(cancellationToken);
        }

        if (user.IsLocked)
        {
            // Same equalization as the missing-user path — don't reveal lock state via wall clock.
            _ = passwordHasher.Verify(password, passwordHasher.CanaryHash);
            return false;
        }

        return passwordHasher.Verify(password, user.PasswordHash);
    }

    public async Task RecordFailedLoginAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        // Pre-auth: token endpoint records failures before any JWT exists.
        var user = await FindByIdPreAuthAsync(userId, cancellationToken);
        if (user is null)
        {
            return;
        }

        // The counter still increments while the account is locked — that's how 10 cumulative
        // failures (during a single attack burst) reach the 1h tier. Lock-EXTENSION is what we
        // prevent (the indefinite-DoS vector), via the `==` threshold checks in
        // ApplyFailedLoginAsync — only the EXACT counts of 5 and 10 set LockedUntil; anything
        // beyond that increments the counter but leaves LockedUntil at the most recent
        // threshold transition. So the maximum lock per attack cycle is 1h, after which expiry
        // resets the counter (in ValidateCredentialsAsync) and the cycle restarts.
        await ApplyFailedLoginAsync(user, cancellationToken);
    }

    public async Task ResetFailedLoginsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        // Pre-auth: token endpoint resets the counter on successful login before any JWT exists.
        var user = await FindByIdPreAuthAsync(userId, cancellationToken);
        if (user is null)
        {
            return;
        }

        await ClearFailedLoginsAsync(user, cancellationToken);
    }

    public Task<User?> FindForLoginAsync(string email, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return Task.FromResult<User?>(null);
        }

        var normalized = email.ToLowerInvariant();
        // Cross-tenant: callers do not yet have a tenant context at token-exchange time.
        return db.Users.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Email == normalized && !u.DeletedAt.HasValue, cancellationToken);
    }

    /// <summary>
    /// Single timing-envelope credentials check for the password-grant token endpoint. Every
    /// failure path — empty input, unknown email, locked account, wrong password — costs exactly
    /// one PBKDF2 verify (against <see cref="IPasswordHasher.CanaryHash"/> when there is no real
    /// hash to check), so an observer cannot distinguish failure modes from request latency.
    /// Internally manages the lockout counter; callers MUST NOT layer
    /// <see cref="RecordFailedLoginAsync"/> or <see cref="ResetFailedLoginsAsync"/> on top of
    /// the result.
    /// </summary>
    public async Task<User?> ValidateCredentialsAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
        {
            _ = passwordHasher.Verify(password ?? string.Empty, passwordHasher.CanaryHash);
            return null;
        }

        var user = await FindForLoginAsync(email, cancellationToken);
        if (user is null)
        {
            _ = passwordHasher.Verify(password, passwordHasher.CanaryHash);
            return null;
        }

        // A naturally-expired lock must not leave the counter above the 5-failure threshold, or
        // the very next failure would re-lock at the 1h mark. Treat expiry as a fresh start.
        if (user.LockedUntil.HasValue && user.LockedUntil <= DateTimeOffset.UtcNow)
        {
            await ClearFailedLoginsAsync(user, cancellationToken);
        }

        if (user.IsLocked)
        {
            // Equalize wall-clock with the wrong-password path so lock state isn't probeable.
            _ = passwordHasher.Verify(password, passwordHasher.CanaryHash);
            return null;
        }

        if (!passwordHasher.Verify(password, user.PasswordHash))
        {
            await ApplyFailedLoginAsync(user, cancellationToken);
            return null;
        }

        await ClearFailedLoginsAsync(user, cancellationToken);
        return user;
    }

    // Threshold checks use `==` so the lock is set EXACTLY at the tier transitions (5 and 10)
    // and not re-applied on every subsequent failure. Combined with RecordFailedLoginAsync NOT
    // no-op'ing on locked accounts, this gives the desired shape: counter grows while locked,
    // 10 cumulative failures escalate to 1h, but failures past 10 do not extend the lock window
    // (no indefinite-DoS via lock extension). Concurrency: under EF Core without optimistic
    // tokens, two parallel increments from N to N+1 both write N+1 last-write-wins; both
    // observe FailedLoginAttempts == N+1 in their local context, so a threshold transition is
    // not missed — the threshold is only "missed" if the same fail-count is incremented from
    // two different start points, which cannot happen since both transactions read the same row.
    private async Task ApplyFailedLoginAsync(User user, CancellationToken cancellationToken)
    {
        user.FailedLoginAttempts++;
        var now = DateTimeOffset.UtcNow;
        if (user.FailedLoginAttempts == LongLockoutThreshold)
        {
            user.LockedUntil = now + LongLockout;
        }
        else if (user.FailedLoginAttempts == ShortLockoutThreshold)
        {
            user.LockedUntil = now + ShortLockout;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task ClearFailedLoginsAsync(User user, CancellationToken cancellationToken)
    {
        if (user.FailedLoginAttempts == 0 && user.LockedUntil is null)
        {
            return;
        }

        user.FailedLoginAttempts = 0;
        user.LockedUntil = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    // IgnoreQueryFilters carve-out per CLAUDE.md §Security Rules #1: callers that run before
    // ITenantContext is populated (login validation, lockout bookkeeping during the password
    // grant) need to look up users without the tenant filter resolving to Guid.Empty. IsDeleted
    // is re-applied inline so the bypass does not widen the search to soft-deleted rows.
    private Task<User?> FindByIdPreAuthAsync(Guid userId, CancellationToken cancellationToken)
        => db.Users.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == userId && !u.DeletedAt.HasValue, cancellationToken);

    // Post-auth caller: relies on the global tenant + soft-delete filters in StrgDbContext.
    // Returns null when the user is in a different tenant than the current ITenantContext, which
    // is the desired isolation behavior for admin/user-self operations.
    private Task<User?> FindByIdAsync(Guid userId, CancellationToken cancellationToken)
        => db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

    private static bool IsPasswordLongEnough(string? password)
        => !string.IsNullOrEmpty(password) && password.Length >= UserManagerErrors.MinimumPasswordLength;

    private static bool IsValidEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return false;
        }

        // MailAddress accepts `Name <addr>` forms — require the raw address to equal the input.
        return MailAddress.TryCreate(email, out var parsed) && parsed.Address == email;
    }
}
