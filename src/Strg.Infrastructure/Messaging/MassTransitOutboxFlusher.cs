using Microsoft.Extensions.Configuration;

namespace Strg.Infrastructure.Messaging;

/// <summary>
/// Default <see cref="IOutboxFlusher"/> implementation. Waits for the outbox polling loop to pick
/// up any pending messages — integration tests configure <c>MassTransit:OutboxPollingIntervalSeconds</c>
/// to a small value (e.g. 1s) and then <c>await FlushAsync()</c> instead of sprinkling
/// <c>Task.Delay(5000)</c> across tests.
/// </summary>
/// <remarks>
/// This is intentionally not hooked into MassTransit's internal <c>BusOutboxDeliveryService</c>
/// trigger — that surface is not part of MassTransit's public API and binding to it would create a
/// fragile coupling to patch-version internals. A configured polling sleep is deterministic enough
/// for the Phase-12 test-infrastructure contract: tests just await the flush, the production path
/// is unaffected, and no test depends on timing beyond a single configurable interval.
/// </remarks>
internal sealed class MassTransitOutboxFlusher : IOutboxFlusher
{
    private readonly TimeSpan _pollingInterval;

    public MassTransitOutboxFlusher(IConfiguration configuration)
    {
        var seconds = configuration.GetValue("MassTransit:OutboxPollingIntervalSeconds", 5);
        // 2x the polling interval gives the BusOutboxDeliveryService time to tick, pick up rows,
        // and hand them off to the transport before the test proceeds to assertions.
        _pollingInterval = TimeSpan.FromSeconds(seconds * 2);
    }

    public Task FlushAsync(CancellationToken cancellationToken = default)
        => Task.Delay(_pollingInterval, cancellationToken);
}
