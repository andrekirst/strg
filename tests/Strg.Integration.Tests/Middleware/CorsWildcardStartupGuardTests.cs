using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Strg.Api.Cors;
using Xunit;

namespace Strg.Integration.Tests.Middleware;

/// <summary>
/// STRG-010 — Security Review Checklist: "CORS does not allow <c>*</c> origin in any
/// configuration". Pins the fail-fast startup guard in
/// <see cref="StrgCorsServiceCollectionExtensions.AddStrgCors"/> so a misconfiguration surfaces
/// as a host-start exception, NOT as a browser-side "blocked by CORS policy" at runtime.
/// </summary>
public sealed class CorsWildcardStartupGuardTests
{
    [Fact]
    public void AddStrgCors_throws_when_allowed_origins_contains_bare_wildcard()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Cors:AllowedOrigins:0"] = "https://allowed.example.com",
            ["Cors:AllowedOrigins:1"] = "*",
        });

        var act = () => services.AddStrgCors(configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*wildcard*", "the exception must call out the forbidden pattern to guide the operator")
            .WithMessage($"*{StrgCorsServiceCollectionExtensions.AllowedOriginsConfigurationKey}*");
    }

    [Fact]
    public void AddStrgCors_throws_when_origin_contains_embedded_wildcard()
    {
        // Embedded wildcards ("https://*.example.com") are also rejected: they collapse to the
        // same spec violation (AllowCredentials + wildcard) at request time, and the guard is
        // deliberately strict — explicit allow-listing is the STRG-010 contract.
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Cors:AllowedOrigins:0"] = "https://*.example.com",
        });

        var act = () => services.AddStrgCors(configuration);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void AddStrgCors_succeeds_with_empty_or_explicit_origins()
    {
        // Empty origins is NOT a startup failure — a strg instance with no browser UI is a
        // legitimate topology (CLI/WebDAV-only). The production risk (no wildcards) is what the
        // guard covers; the empty case is "CORS preflight will always fail, browsers can't use
        // this host" which is correct-by-configuration, not a misconfiguration.
        var emptyServices = new ServiceCollection();
        var emptyConfig = BuildConfiguration(new Dictionary<string, string?>());
        var emptyAct = () => emptyServices.AddStrgCors(emptyConfig);
        emptyAct.Should().NotThrow();

        var explicitServices = new ServiceCollection();
        var explicitConfig = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Cors:AllowedOrigins:0"] = "https://app.example.com",
            ["Cors:AllowedOrigins:1"] = "https://admin.example.com",
        });
        var explicitAct = () => explicitServices.AddStrgCors(explicitConfig);
        explicitAct.Should().NotThrow();
    }

    private static IConfiguration BuildConfiguration(IDictionary<string, string?> data) =>
        new ConfigurationBuilder().AddInMemoryCollection(data).Build();
}
