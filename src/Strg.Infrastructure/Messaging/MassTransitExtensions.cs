using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Strg.Infrastructure.Data;
using Strg.Infrastructure.Messaging.Consumers;

namespace Strg.Infrastructure.Messaging;

public static class MassTransitExtensions
{
    /// <summary>
    /// Wires MassTransit with the EF Core outbox (send-side, per project Phase-8 memory) over the
    /// RabbitMQ transport. Registers all Tranche-5 placeholder consumers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Transport:</b> RabbitMQ from v0.1. Connection settings come from the <c>RabbitMQ</c>
    /// configuration section (<c>Host</c>, <c>VirtualHost</c>, <c>Username</c>, <c>Password</c>).
    /// <c>Username</c> and <c>Password</c> are required in non-Development environments —
    /// startup throws if either is missing. Development has a guest/guest fallback applied
    /// in code (not in appsettings.json) so a prod overlay cannot silently inherit it.
    /// </para>
    /// <para>
    /// <b>Outbox polling:</b> default 5 seconds. Override via
    /// <c>MassTransit:OutboxPollingIntervalSeconds</c>. Tests drive dispatch deterministically via
    /// <see cref="IOutboxFlusher"/> rather than sleeping past this interval.
    /// </para>
    /// <para>
    /// <b>Retries + dead-letter:</b> 5 retries with exponential backoff before a message is sent
    /// to the per-consumer dead-letter exchange. <c>IConsumer&lt;Fault&lt;TEvent&gt;&gt;</c> handlers
    /// observe dead-letter traffic.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddStrgMassTransit(
        this IServiceCollection services,
        IConfiguration configuration,
        bool isDevelopment)
    {
        var pollingSeconds = configuration.GetValue("MassTransit:OutboxPollingIntervalSeconds", 5);

        // Credentials must be resolved up-front: MassTransit captures the closure over cfg
        // callback, and the throw needs to happen at startup (fail-fast) rather than at first
        // broker connection. A missing-creds-in-prod config mistake should crash Kestrel, not
        // silently publish with dev defaults.
        var username = configuration["RabbitMQ:Username"];
        var password = configuration["RabbitMQ:Password"];

        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            if (!isDevelopment)
            {
                throw new InvalidOperationException(
                    "RabbitMQ:Username and RabbitMQ:Password are required outside Development. " +
                    "Configure both via appsettings, environment variables, or secret store — " +
                    "no guest/guest fallback is applied in non-Development environments.");
            }

            // Development-only fallback. The literal lives here (not in appsettings.json) so
            // a prod config overlay cannot silently inherit it.
            username ??= "guest";
            password ??= "guest";
        }

        services.AddMassTransit(bus =>
        {
            bus.AddEntityFrameworkOutbox<StrgDbContext>(outbox =>
            {
                outbox.UsePostgres();
                outbox.UseBusOutbox();
                outbox.QueryDelay = TimeSpan.FromSeconds(pollingSeconds);
                outbox.DuplicateDetectionWindow = TimeSpan.FromMinutes(30);
            });

            bus.AddConsumer<AuditLogConsumer>();
            bus.AddConsumer<QuotaNotificationConsumer>();
            bus.AddConsumer<GraphQLSubscriptionPublisher>();
            bus.AddConsumer<SearchIndexConsumer>();

            bus.SetKebabCaseEndpointNameFormatter();

            bus.UsingRabbitMq((context, cfg) =>
            {
                var host = configuration["RabbitMQ:Host"] ?? "localhost";
                var virtualHost = configuration["RabbitMQ:VirtualHost"] ?? "/";

                cfg.Host(host, virtualHost, h =>
                {
                    h.Username(username);
                    h.Password(password);
                });

                // 5 retries exponential backoff before dead-letter (per STRG-061 spec).
                cfg.UseMessageRetry(r => r.Exponential(
                    retryLimit: 5,
                    minInterval: TimeSpan.FromSeconds(1),
                    maxInterval: TimeSpan.FromSeconds(30),
                    intervalDelta: TimeSpan.FromSeconds(2)));

                cfg.ConfigureEndpoints(context);
            });
        });

        services.AddScoped<IOutboxFlusher, MassTransitOutboxFlusher>();

        return services;
    }
}
