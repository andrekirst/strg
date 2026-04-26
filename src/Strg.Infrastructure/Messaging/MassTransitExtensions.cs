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
        bool isDevelopment,
        Action<IBusRegistrationConfigurator>? configureConsumers = null)
    {
        var pollingSeconds = configuration.GetValue("MassTransit:OutboxPollingIntervalSeconds", 5);

        // Credentials must be resolved up-front: MassTransit captures the closure over cfg
        // callback, and the throw needs to happen at startup (fail-fast) rather than at first
        // broker connection. A missing-creds-in-prod config mistake should crash Kestrel, not
        // silently publish with dev defaults.
        var username = configuration["RabbitMQ:Username"];
        var password = configuration["RabbitMQ:Password"];

        // IsNullOrWhiteSpace not IsNullOrEmpty: whitespace-only values ("   ") are a common
        // copy-paste artefact from secret managers — they'd pass an IsNullOrEmpty guard and
        // then fail at the first broker publish with an opaque RabbitMQ ACCESS_REFUSED that
        // sends operators chasing vault/env-vars/k8s-secrets before suspecting whitespace
        // padding. Strong guard up-front turns a deep-pipeline failure into a startup crash.
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
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
            username = "guest";
            password = "guest";
        }

        services.AddMassTransit(bus =>
        {
            bus.AddEntityFrameworkOutbox<StrgDbContext>(outbox =>
            {
                outbox.UsePostgres();
                outbox.UseBusOutbox();
                outbox.QueryDelay = TimeSpan.FromSeconds(pollingSeconds);
                outbox.DuplicateDetectionWindow = TimeSpan.FromMinutes(30);

                // Optional: disable the InboxCleanupService background loop entirely. Used by
                // integration tests where the short test lifetime races the cleanup loop's
                // mid-query DbContext disposal — the in-flight query gets an
                // EndOfStreamException-wrapped "transient failure" logged as
                // `[ERR] CleanUpInboxState faulted` even though tests still pass. Production
                // keeps the default (cleanup enabled) so old inbox-state rows don't accumulate.
                if (configuration.GetValue<bool?>("RabbitMQ:DisableInboxCleanup") == true)
                {
                    outbox.DisableInboxCleanupService();
                }
            });

            bus.AddConsumer<AuditLogConsumer>();
            bus.AddConsumer<QuotaNotificationConsumer>();
            bus.AddConsumer<SearchIndexConsumer>();

            // GraphQlSubscriptionPublisher lives in Strg.GraphQl (so it can see ITopicEventSender),
            // which Strg.Infrastructure cannot reference without inverting the layer dependency.
            // Strg.Api wires it in via this hook — see AddStrgMassTransit caller in Program.cs.
            configureConsumers?.Invoke(bus);

            bus.SetKebabCaseEndpointNameFormatter();

            bus.UsingRabbitMq((context, cfg) =>
            {
                var host = configuration["RabbitMQ:Host"] ?? "localhost";
                var virtualHost = configuration["RabbitMQ:VirtualHost"] ?? "/";
                // Port is optional — production uses the AMQP default (5672) and the config key
                // is a test-only escape hatch for Testcontainers, which exposes RabbitMQ on a
                // random host port.
                var port = configuration.GetValue<ushort?>("RabbitMQ:Port");

                // Optional opt-out of RabbitMQ publisher confirmations. Default is true (prod
                // safety). Set RabbitMQ:PublisherConfirmation=false to disable — used in
                // integration tests to dodge a known SemaphoreSlim disposal race in
                // RabbitMQ.Client 7.x's `Channel.MaybeHandlePublisherConfirmationTcsOnChannelShutdownAsync`
                // that crashes the test host when the bus is disposed mid-run.
                //
                // Safe to disable in tests because the EF Core outbox (UseBusOutbox above) is
                // the durability boundary: messages are persisted with the DB transaction
                // BEFORE dispatch is attempted, so a publish that doesn't get a broker ack is
                // re-dispatched on the next outbox poll. Publisher confirmations are a redundant
                // ack-on-publish that doesn't add semantic guarantees beyond what the outbox
                // already provides. PublisherConfirmation is a host-level setting (lives on
                // IRabbitMqHostConfigurator), so it's applied inside the host callback below.
                var publisherConfirmation =
                    configuration.GetValue<bool?>("RabbitMQ:PublisherConfirmation") ?? true;

                void ConfigureHost(IRabbitMqHostConfigurator h)
                {
                    h.Username(username);
                    h.Password(password);
                    h.PublisherConfirmation = publisherConfirmation;
                }

                if (port.HasValue)
                {
                    cfg.Host(host, port.Value, virtualHost, ConfigureHost);
                }
                else
                {
                    cfg.Host(host, virtualHost, ConfigureHost);
                }

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
