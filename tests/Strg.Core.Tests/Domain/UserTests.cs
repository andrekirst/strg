using FluentAssertions;
using Strg.Core.Domain;
using Xunit;

namespace Strg.Core.Tests.Domain;

public sealed class UserTests
{
    private static User NewUser(Action<User>? configure = null)
    {
        var user = new User
        {
            Email = "a@example.com",
            DisplayName = "A",
            PasswordHash = "hash",
            TenantId = Guid.NewGuid()
        };
        configure?.Invoke(user);
        return user;
    }

    [Fact]
    public void FreeBytes_ReturnsZero_WhenUsedBytesExceedsQuota()
    {
        var user = NewUser(u => { u.QuotaBytes = 100; u.UsedBytes = 150; });

        user.FreeBytes.Should().Be(0);
    }

    [Fact]
    public void IsLocked_ReturnsFalse_WhenLockedUntilIsPast()
    {
        var user = NewUser(u => u.LockedUntil = DateTimeOffset.UtcNow.AddMinutes(-1));

        user.IsLocked.Should().BeFalse();
    }

    [Fact]
    public void IsLocked_ReturnsTrue_WhenLockedUntilIsInFuture()
    {
        var user = NewUser(u => u.LockedUntil = DateTimeOffset.UtcNow.AddMinutes(30));

        user.IsLocked.Should().BeTrue();
    }

    [Fact]
    public void UsagePercent_ReturnsZero_WhenQuotaIsZero()
    {
        var user = NewUser(u => { u.QuotaBytes = 0; u.UsedBytes = 0; });

        user.UsagePercent.Should().Be(0);
    }

    [Fact]
    public void Email_IsNormalizedToLowercase_OnSet()
    {
        var user = NewUser(u => u.Email = "MIXED.Case@Example.COM");

        user.Email.Should().Be("mixed.case@example.com");
    }
}
