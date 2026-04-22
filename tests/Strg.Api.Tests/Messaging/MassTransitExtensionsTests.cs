using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Strg.Infrastructure.Messaging;
using Xunit;

namespace Strg.Api.Tests.Messaging;

/// <summary>
/// STRG-061 HIGH-1 regression: the RabbitMQ guest/guest baseline was removed from
/// appsettings.json and the <c>?? "guest"</c> silent fallbacks dropped from
/// <see cref="MassTransitExtensions.AddStrgMassTransit"/>. The guard now lives in code:
/// missing creds outside Development must throw at startup, so a prod config mistake
/// crashes Kestrel rather than silently publishing with dev defaults.
/// </summary>
public sealed class MassTransitExtensionsTests
{
    [Fact]
    public void Throws_when_rabbitmq_credentials_missing_in_non_development()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(username: null, password: null);

        var act = () => services.AddStrgMassTransit(configuration, isDevelopment: false);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*RabbitMQ:Username and RabbitMQ:Password are required*");
    }

    [Fact]
    public void Throws_when_only_password_missing_in_non_development()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(username: "strg-prod", password: null);

        var act = () => services.AddStrgMassTransit(configuration, isDevelopment: false);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Throws_when_only_username_missing_in_non_development()
    {
        // Symmetric mirror of Throws_when_only_password_missing. A future refactor that drops
        // the username half of the guard (typo turning `IsNullOrWhiteSpace(username) ||` into a
        // single-branch check on password) would pass the missing-both and missing-password
        // tests but leak a blank username into RabbitMQ auth. This test closes the hole.
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(username: null, password: "rotated-secret");

        var act = () => services.AddStrgMassTransit(configuration, isDevelopment: false);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Throws_when_credentials_are_whitespace_only_in_non_development()
    {
        // Pins STRG-061 INFO-1 — the guard is IsNullOrWhiteSpace, not IsNullOrEmpty. Whitespace
        // values are a common copy-paste artefact from secret managers; letting them through
        // surfaces as an opaque RabbitMQ ACCESS_REFUSED at first publish instead of a fast-fail
        // startup crash. A revert to IsNullOrEmpty would silently pass this config and fail
        // this test.
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(username: "   ", password: "   ");

        var act = () => services.AddStrgMassTransit(configuration, isDevelopment: false);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*RabbitMQ:Username and RabbitMQ:Password are required*");
    }

    [Fact]
    public void Allows_missing_credentials_in_development_with_guest_fallback()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(username: null, password: null);

        var act = () => services.AddStrgMassTransit(configuration, isDevelopment: true);

        act.Should().NotThrow();
    }

    [Fact]
    public void Allows_configured_credentials_in_non_development()
    {
        var services = new ServiceCollection();
        var configuration = BuildConfiguration(username: "strg-prod", password: "rotated-secret");

        var act = () => services.AddStrgMassTransit(configuration, isDevelopment: false);

        act.Should().NotThrow();
    }

    private static IConfiguration BuildConfiguration(string? username, string? password)
    {
        var builder = new ConfigurationBuilder();
        var values = new Dictionary<string, string?>
        {
            ["RabbitMQ:Host"] = "localhost",
            ["RabbitMQ:VirtualHost"] = "/",
            ["RabbitMQ:Username"] = username,
            ["RabbitMQ:Password"] = password,
        };
        builder.AddInMemoryCollection(values);
        return builder.Build();
    }
}
