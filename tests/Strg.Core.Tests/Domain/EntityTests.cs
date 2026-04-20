namespace Strg.Core.Tests.Domain;

using Strg.Core.Domain;
using FluentAssertions;
using Xunit;

file sealed class ConcreteEntity : Entity { }

public sealed class EntityTests
{
    [Fact]
    public void Id_DefaultsToNewGuid()
    {
        var entity = new ConcreteEntity();
        entity.Id.Should().NotBeEmpty();
    }

    [Fact]
    public void TwoEntities_HaveDifferentIds()
    {
        var e1 = new ConcreteEntity();
        var e2 = new ConcreteEntity();
        e1.Id.Should().NotBe(e2.Id);
    }
}
