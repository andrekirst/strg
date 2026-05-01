using FluentAssertions;
using Strg.Infrastructure.Storage;
using Strg.Plugin.Abstractions.Storage;
using Xunit;

namespace Strg.Architecture.Tests.Layering;

/// <summary>
/// TC-002 from STRG-088. Pins both the assembly origin (the relocated <see cref="IStorageProvider"/>
/// lives in Strg.Plugin.Abstractions) AND that <see cref="LocalFileSystemProvider"/> still
/// implements it after the move. Without the assembly-origin guard, a stray duplicate interface
/// in Strg.Core could let LocalFileSystemProvider satisfy an obsolete contract while the plugin
/// surface bit-rots silently.
/// </summary>
public sealed class LocalFileSystemProviderImplementsRelocatedContractTests
{
    [Fact]
    public void IStorageProvider_lives_in_Strg_Plugin_Abstractions()
    {
        typeof(IStorageProvider).Assembly.GetName().Name
            .Should().Be("Strg.Plugin.Abstractions",
                because: "the contract has actually moved out of Strg.Core; a duplicate " +
                         "interface in Strg.Core would let implementations satisfy an " +
                         "obsolete contract while the plugin surface bit-rots silently.");
    }

    [Fact]
    public void LocalFileSystemProvider_implements_IStorageProvider_from_Plugin_Abstractions()
    {
        typeof(IStorageProvider).IsAssignableFrom(typeof(LocalFileSystemProvider))
            .Should().BeTrue(
                because: "LocalFileSystemProvider is the canonical reference implementation " +
                         "of the storage plugin contract — if it stops satisfying the " +
                         "interface, every external storage plugin is similarly broken and " +
                         "the upload/download paths fail to bind a provider at request time.");
    }
}
