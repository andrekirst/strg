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
