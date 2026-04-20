using FluentAssertions;
using Strg.Core.Domain;
using Xunit;

namespace Strg.Core.Tests.Domain;

public sealed class UserTests
{
    [Fact]
    public void FreeBytes_ReturnsZero_WhenUsedBytesExceedsQuota()
    {
        var user = new User
        {
            Email = "a@example.com",
            DisplayName = "A",
            TenantId = Guid.NewGuid(),
            QuotaBytes = 100,
            UsedBytes = 150
        };

        user.FreeBytes.Should().Be(0);
    }

    [Fact]
    public void IsLocked_ReturnsFalse_WhenLockedUntilIsPast()
    {
        var user = new User
        {
            Email = "a@example.com",
            DisplayName = "A",
            TenantId = Guid.NewGuid(),
            LockedUntil = DateTimeOffset.UtcNow.AddMinutes(-1)
        };

        user.IsLocked.Should().BeFalse();
    }

    [Fact]
    public void IsLocked_ReturnsTrue_WhenLockedUntilIsInFuture()
    {
        var user = new User
        {
            Email = "a@example.com",
            DisplayName = "A",
            TenantId = Guid.NewGuid(),
            LockedUntil = DateTimeOffset.UtcNow.AddMinutes(30)
        };

        user.IsLocked.Should().BeTrue();
    }

    [Fact]
    public void UsagePercent_ReturnsZero_WhenQuotaIsZero()
    {
        var user = new User
        {
            Email = "a@example.com",
            DisplayName = "A",
            TenantId = Guid.NewGuid(),
            QuotaBytes = 0,
            UsedBytes = 0
        };

        user.UsagePercent.Should().Be(0);
    }
}
