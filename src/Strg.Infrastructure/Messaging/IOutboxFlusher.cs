namespace Strg.Infrastructure.Messaging;

/// <summary>
/// Deterministic trigger for the EF Core outbox dispatch loop. Production uses MassTransit's
/// background polling (<c>QueryDelay</c>, default 5s); integration tests would otherwise need to
/// sleep past that interval to observe a published event.
/// </summary>
/// <remarks>
/// Phase-12 test-infrastructure memory: prefer this over <c>Task.Delay(PollingInterval * 2)</c>
/// in tests. The flusher bypasses the delay and pumps the outbox once.
/// </remarks>
public interface IOutboxFlusher
{
    /// <summary>
    /// Pump the outbox once: pick up any pending OutboxMessage rows for this context and hand them
    /// off to the transport. Safe to call multiple times; idempotent per dispatched message.
    /// </summary>
    Task FlushAsync(CancellationToken cancellationToken = default);
}
