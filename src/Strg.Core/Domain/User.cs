namespace Strg.Core.Domain;

public sealed class User : TenantedEntity
{
    private string _email = string.Empty;

    public required string Email
    {
        get => _email;
        set => _email = value.ToLowerInvariant();
    }

    public required string DisplayName { get; set; }
    public required string PasswordHash { get; set; }
    public long QuotaBytes { get; set; } = 10L * 1024 * 1024 * 1024; // 10 GB default
    public long UsedBytes { get; set; }
    public UserRole Role { get; set; } = UserRole.User;
    public DateTimeOffset? LockedUntil { get; set; }
    public int FailedLoginAttempts { get; set; }

    public bool IsLocked => LockedUntil.HasValue && LockedUntil > DateTimeOffset.UtcNow;
    public long FreeBytes => Math.Max(0, QuotaBytes - UsedBytes);
    public double UsagePercent => QuotaBytes == 0 ? 0 : (double)UsedBytes / QuotaBytes * 100;
}

public enum UserRole
{
    Readonly = 0,
    User = 1,
    Admin = 2,
    SuperAdmin = 3
}
