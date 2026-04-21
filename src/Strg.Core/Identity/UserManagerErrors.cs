namespace Strg.Core.Identity;

/// <summary>
/// Error codes returned by <see cref="IUserManager"/> in <see cref="Result{T}"/> and
/// <see cref="Result"/>. Stable strings — callers may branch on them.
/// </summary>
public static class UserManagerErrors
{
    /// <summary>Minimum password length (characters) enforced by create/change/set flows.</summary>
    public const int MinimumPasswordLength = 12;

    public const string EmailAlreadyExists = "EmailAlreadyExists";
    public const string InvalidEmail = "InvalidEmail";
    public const string InvalidPassword = "InvalidPassword";
    public const string InvalidQuota = "InvalidQuota";
    public const string PasswordTooShort = "PasswordTooShort";
    public const string UserNotFound = "UserNotFound";
}
