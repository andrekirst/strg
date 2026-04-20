using System.Security.Claims;
using FluentAssertions;
using Strg.Core.Identity;
using Xunit;

namespace Strg.Core.Tests.Identity;

public sealed class ClaimsPrincipalExtensionsTests
{
    private static ClaimsPrincipal MakePrincipal(IEnumerable<Claim> claims)
    {
        var identity = new ClaimsIdentity(claims, authenticationType: "test");
        return new ClaimsPrincipal(identity);
    }

    // ---- GetUserId ----

    [Fact]
    public void GetUserId_ReturnsExpectedGuid_WhenSubClaimPresent()
    {
        var id = Guid.NewGuid();
        var principal = MakePrincipal([new Claim("sub", id.ToString())]);

        principal.GetUserId().Should().Be(id);
    }

    [Fact]
    public void GetUserId_Throws_WhenSubClaimMissing()
    {
        var principal = MakePrincipal([]);

        var act = () => principal.GetUserId();

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*'sub'*");
    }

    // ---- GetTenantId ----

    [Fact]
    public void GetTenantId_ReturnsExpectedGuid_WhenTenantIdClaimPresent()
    {
        var tenantId = Guid.NewGuid();
        var principal = MakePrincipal([new Claim("tenant_id", tenantId.ToString())]);

        principal.GetTenantId().Should().Be(tenantId);
    }

    [Fact]
    public void GetTenantId_Throws_WhenTenantIdClaimMissing()
    {
        var principal = MakePrincipal([]);

        var act = () => principal.GetTenantId();

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*'tenant_id'*");
    }

    // ---- HasScope ----

    [Fact]
    public void HasScope_ReturnsTrue_WhenScopeIsExactSingleValue()
    {
        var principal = MakePrincipal([new Claim("scope", "files.read")]);

        principal.HasScope("files.read").Should().BeTrue();
    }

    [Fact]
    public void HasScope_ReturnsTrue_WhenScopeIsInSpaceSeparatedString()
    {
        var principal = MakePrincipal([new Claim("scope", "files.read files.write admin")]);

        principal.HasScope("files.write").Should().BeTrue();
    }

    [Fact]
    public void HasScope_ReturnsFalse_WhenScopeNotPresent()
    {
        var principal = MakePrincipal([new Claim("scope", "files.read")]);

        principal.HasScope("admin").Should().BeFalse();
    }

    [Fact]
    public void HasScope_ReturnsFalse_WhenNoScopeClaimsExist()
    {
        var principal = MakePrincipal([]);

        principal.HasScope("files.read").Should().BeFalse();
    }

    [Fact]
    public void HasScope_ReturnsTrue_WhenMultipleIndividualScopeClaims()
    {
        var principal = MakePrincipal([
            new Claim("scope", "files.read"),
            new Claim("scope", "admin"),
        ]);

        principal.HasScope("admin").Should().BeTrue();
    }

    [Fact]
    public void HasScope_IsCaseSensitive()
    {
        var principal = MakePrincipal([new Claim("scope", "files.read")]);

        principal.HasScope("Files.Read").Should().BeFalse();
    }
}
