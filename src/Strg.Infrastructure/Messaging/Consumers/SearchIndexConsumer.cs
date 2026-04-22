using MassTransit;
using Microsoft.Extensions.Logging;
using Strg.Core.Events;

namespace Strg.Infrastructure.Messaging.Consumers;

/// <summary>
/// STRG-063 v0.1 no-op placeholder. Wired into the bus so the search-index message topology
/// exists from day one — the real indexing body lands in v0.2 when the <c>ISearchProvider</c>
/// plugin interface ships. Receiving events as a no-op now ensures the broker topology +
/// dead-letter wiring are production-shaped before the indexing path is switched on, and
/// spares downstream v0.2 from needing a migration to introduce a new consumer queue.
///
/// <para><b>Log level:</b> <see cref="LogLevel.Debug"/>. Not Information because the volume on a
/// busy tenant would drown the default appender; not Trace because the "event routed to search
/// consumer" signal is useful during v0.2 rollout verification. Flip to Debug via
/// <c>Logging:LogLevel:Strg.Infrastructure.Messaging.Consumers.SearchIndexConsumer</c>.</para>
///
/// <para><b>Log contents:</b> only the <c>FileId</c> from the event payload is logged — never
/// paths, MIME types, sizes, or user identifiers. Search-index logs have a different retention
/// shape than audit logs, so the principle here is "no PII-adjacent data in search logs, ever".</para>
///
/// <para><b>Also consumes <see cref="FileMovedEvent"/>:</b> STRG-061 wired the event to this
/// consumer because a v0.2 search index needs to update the path on move. Retained as a no-op
/// in v0.1 so the queue binding stays in place — dropping it would mean a broker-topology
/// migration when the v0.2 body lands.</para>
/// </summary>
public sealed class SearchIndexConsumer :
    IConsumer<FileUploadedEvent>,
    IConsumer<FileDeletedEvent>,
    IConsumer<FileMovedEvent>
{
    private readonly ILogger<SearchIndexConsumer> _logger;

    public SearchIndexConsumer(ILogger<SearchIndexConsumer> logger) => _logger = logger;

    public Task Consume(ConsumeContext<FileUploadedEvent> context)
    {
        // TODO v0.2: resolve ISearchProvider from the scope and call
        // provider.IndexAsync(context.Message.FileId, context.CancellationToken).
        _logger.LogDebug(
            "SearchIndexConsumer: file.uploaded fileId={FileId} (indexing deferred to v0.2)",
            context.Message.FileId);
        return Task.CompletedTask;
    }

    public Task Consume(ConsumeContext<FileDeletedEvent> context)
    {
        // TODO v0.2: resolve ISearchProvider + call provider.RemoveAsync(fileId).
        _logger.LogDebug(
            "SearchIndexConsumer: file.deleted fileId={FileId} (indexing deferred to v0.2)",
            context.Message.FileId);
        return Task.CompletedTask;
    }

    public Task Consume(ConsumeContext<FileMovedEvent> context)
    {
        // TODO v0.2: resolve ISearchProvider + call provider.UpdatePathAsync(fileId, newPath).
        _logger.LogDebug(
            "SearchIndexConsumer: file.moved fileId={FileId} (indexing deferred to v0.2)",
            context.Message.FileId);
        return Task.CompletedTask;
    }
}
