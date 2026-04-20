namespace Strg.Core.Tests.Domain;

using Strg.Core.Domain;
using FluentAssertions;
using Xunit;

// Concrete subclass for testing
file sealed class TestEntity : TenantedEntity { }

public sealed class TenantedEntityTests
{
    [Fact]
    public void Id_IsNonEmptyGuid_OnConstruction()
    {
        var entity = new TestEntity();
        entity.Id.Should().NotBeEmpty();
    }

    [Fact]
    public void CreatedAt_IsApproximatelyUtcNow_OnConstruction()
    {
        var before = DateTimeOffset.UtcNow;
        var entity = new TestEntity();
        var after = DateTimeOffset.UtcNow;

        entity.CreatedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    [Fact]
    public void UpdatedAt_IsApproximatelyUtcNow_OnConstruction()
    {
        var before = DateTimeOffset.UtcNow;
        var entity = new TestEntity();
        var after = DateTimeOffset.UtcNow;

        entity.UpdatedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    [Fact]
    public void IsDeleted_IsFalse_WhenDeletedAtIsNull()
    {
        var entity = new TestEntity();
        entity.IsDeleted.Should().BeFalse();
    }

    [Fact]
    public void IsDeleted_IsTrue_WhenDeletedAtIsSet()
    {
        var entity = new TestEntity { DeletedAt = DateTimeOffset.UtcNow };
        entity.IsDeleted.Should().BeTrue();
    }
}
