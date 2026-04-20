namespace Strg.Api.Tests.Storage;

using FluentAssertions;
using NSubstitute;
using Strg.Core.Storage;
using Strg.Infrastructure.Storage;
using Xunit;

public sealed class StorageProviderRegistryTests
{
    private readonly StorageProviderRegistry _registry = new();

    [Fact]
    public void Register_ThenIsRegistered_ReturnsTrue()
    {
        _registry.Register("local", _ => Substitute.For<IStorageProvider>());
        _registry.IsRegistered("local").Should().BeTrue();
    }

    [Fact]
    public void IsRegistered_UnknownType_ReturnsFalse()
    {
        _registry.IsRegistered("unknown").Should().BeFalse();
    }

    [Fact]
    public void Resolve_RegisteredType_ReturnsProviderInstance()
    {
        var provider = Substitute.For<IStorageProvider>();
        _registry.Register("local", _ => provider);
        var config = Substitute.For<IStorageProviderConfig>();

        var resolved = _registry.Resolve("local", config);

        resolved.Should().BeSameAs(provider);
    }

    [Fact]
    public void Resolve_UnregisteredType_ThrowsInvalidOperationException()
    {
        var config = Substitute.For<IStorageProviderConfig>();
        var act = () => _registry.Resolve("nonexistent", config);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*nonexistent*");
    }

    [Fact]
    public void Register_SameTypeTwice_SecondFactoryOverwritesFirst()
    {
        var firstProvider = Substitute.For<IStorageProvider>();
        var secondProvider = Substitute.For<IStorageProvider>();
        var config = Substitute.For<IStorageProviderConfig>();

        _registry.Register("local", _ => firstProvider);
        _registry.Register("local", _ => secondProvider);

        var resolved = _registry.Resolve("local", config);
        resolved.Should().BeSameAs(secondProvider);
    }

    [Fact]
    public void GetRegisteredTypes_ReturnsAllRegisteredTypes()
    {
        _registry.Register("local", _ => Substitute.For<IStorageProvider>());
        _registry.Register("s3", _ => Substitute.For<IStorageProvider>());

        var types = _registry.GetRegisteredTypes();
        types.Should().Contain("local").And.Contain("s3");
    }
}
