using MassTransit;
using Microsoft.Extensions.Logging;
using Strg.Core.Events;

namespace Strg.Infrastructure.Messaging.Consumers;

/// <summary>
/// No-op consumer for v0.1 per STRG-061 spec. Wired up so the search-index message topology exists
/// from day one; real indexing logic arrives in STRG-065 (search feature). Receiving events as a
/// no-op now ensures the broker topology + dead-letter wiring is production-shaped before the
/// indexing path is turned on.
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
        // TODO STRG-065: index file content + metadata.
        _logger.LogTrace("SearchIndexConsumer (noop v0.1): FileUploaded {FileId}", context.Message.FileId);
        return Task.CompletedTask;
    }

    public Task Consume(ConsumeContext<FileDeletedEvent> context)
    {
        _logger.LogTrace("SearchIndexConsumer (noop v0.1): FileDeleted {FileId}", context.Message.FileId);
        return Task.CompletedTask;
    }

    public Task Consume(ConsumeContext<FileMovedEvent> context)
    {
        _logger.LogTrace("SearchIndexConsumer (noop v0.1): FileMoved {FileId}", context.Message.FileId);
        return Task.CompletedTask;
    }
}
