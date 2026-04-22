using System.Reflection;
using FluentAssertions;
using MassTransit;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Strg.Core.Events;
using Strg.Infrastructure.Messaging.Consumers;
using Xunit;

namespace Strg.Api.Tests.Messaging;

/// <summary>
/// STRG-063 TC-001..TC-003: asserts the v0.1 placeholder is a silent no-op (no throws,
/// no external calls) and pins the invariant that no <c>ISearchProvider</c> seam is wired
/// yet. TC-003 is a structural assertion — if anyone adds an <c>ISearchProvider</c>
/// constructor dependency before v0.2, this test fails loud, forcing the v0.2 plugin
/// lifecycle (registration, fault handling, test harness) to land together rather than
/// as a stealth merge.
///
/// <para>Consumer is invoked directly with a substituted <see cref="ConsumeContext{T}"/>
/// rather than via <c>InMemoryTestHarness</c> — a no-op logger-only consumer does not
/// need the harness's message-routing scaffolding, and keeping this in Api.Tests avoids
/// pulling MassTransit.Testing into the unit-test project.</para>
/// </summary>
public sealed class SearchIndexConsumerTests
{
    [Fact]
    public async Task TC001_Consume_FileUploadedEvent_does_not_throw_and_logs_fileId_at_debug()
    {
        var logger = new CapturingLogger<SearchIndexConsumer>();
        var consumer = new SearchIndexConsumer(logger);

        var message = new FileUploadedEvent(
            TenantId: Guid.NewGuid(),
            FileId: Guid.NewGuid(),
            DriveId: Guid.NewGuid(),
            UserId: Guid.NewGuid(),
            Size: 123,
            MimeType: "text/plain");
        var context = Substitute.For<ConsumeContext<FileUploadedEvent>>();
        context.Message.Returns(message);

        await consumer.Invoking(c => c.Consume(context)).Should().NotThrowAsync();

        logger.Entries.Should().ContainSingle(e =>
            e.Level == LogLevel.Debug
            && e.Message.Contains(message.FileId.ToString())
            && e.Message.Contains("file.uploaded"));
    }

    [Fact]
    public async Task TC002_Consume_FileDeletedEvent_does_not_throw_and_logs_fileId_at_debug()
    {
        var logger = new CapturingLogger<SearchIndexConsumer>();
        var consumer = new SearchIndexConsumer(logger);

        var message = new FileDeletedEvent(
            TenantId: Guid.NewGuid(),
            FileId: Guid.NewGuid(),
            DriveId: Guid.NewGuid(),
            UserId: Guid.NewGuid());
        var context = Substitute.For<ConsumeContext<FileDeletedEvent>>();
        context.Message.Returns(message);

        await consumer.Invoking(c => c.Consume(context)).Should().NotThrowAsync();

        logger.Entries.Should().ContainSingle(e =>
            e.Level == LogLevel.Debug
            && e.Message.Contains(message.FileId.ToString())
            && e.Message.Contains("file.deleted"));
    }

    [Fact]
    public async Task Consume_log_message_contains_only_fileId_and_omits_path_and_mimetype()
    {
        // Security checklist: consumer does not log file paths or content metadata (only IDs).
        // Feeds the Uploaded payload (which carries MimeType + Size) and the Moved payload
        // (which carries OldPath + NewPath) through the consumer, then scans the captured
        // log line for any trace of those values. A regression that changes the log template
        // to include the richer payload would fail here.
        var logger = new CapturingLogger<SearchIndexConsumer>();
        var consumer = new SearchIndexConsumer(logger);

        var uploaded = new FileUploadedEvent(
            TenantId: Guid.NewGuid(),
            FileId: Guid.NewGuid(),
            DriveId: Guid.NewGuid(),
            UserId: Guid.NewGuid(),
            Size: 424242,
            MimeType: "application/x-forbidden-in-search-logs");
        var uploadedCtx = Substitute.For<ConsumeContext<FileUploadedEvent>>();
        uploadedCtx.Message.Returns(uploaded);
        await consumer.Consume(uploadedCtx);

        var moved = new FileMovedEvent(
            TenantId: Guid.NewGuid(),
            FileId: Guid.NewGuid(),
            DriveId: Guid.NewGuid(),
            OldPath: "/forbidden/old/path.txt",
            NewPath: "/forbidden/new/path.txt",
            UserId: Guid.NewGuid());
        var movedCtx = Substitute.For<ConsumeContext<FileMovedEvent>>();
        movedCtx.Message.Returns(moved);
        await consumer.Consume(movedCtx);

        logger.Entries.Should().HaveCount(2);
        foreach (var entry in logger.Entries)
        {
            entry.Message.Should().NotContain("forbidden");
            entry.Message.Should().NotContain("424242");
            entry.Message.Should().NotContain("/old/");
            entry.Message.Should().NotContain("/new/");
        }
    }

    [Fact]
    public void TC003_SearchIndexConsumer_has_no_ISearchProvider_dependency_in_v01()
    {
        // Structural guard: v0.1 wires SearchIndexConsumer with ILogger only. Any
        // constructor parameter whose type name contains "SearchProvider" would indicate
        // the v0.2 plugin seam has leaked into the v0.1 consumer ahead of the plugin
        // lifecycle landing. Also verifies the type does not exist in the Strg.*
        // assemblies — defensive against a future type being introduced without the
        // paired consumer-wiring change.
        var ctor = typeof(SearchIndexConsumer)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Should().ContainSingle().Subject;

        ctor.GetParameters().Should().AllSatisfy(p =>
            p.ParameterType.Name.Should().NotContain("SearchProvider"));

        var strgSearchProviders = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.FullName is string fn && fn.StartsWith("Strg.", StringComparison.Ordinal))
            .SelectMany(SafeGetTypes)
            .Where(t => t.Name.Contains("SearchProvider", StringComparison.Ordinal))
            .ToArray();

        strgSearchProviders.Should().BeEmpty(
            "ISearchProvider is a v0.2 seam — introducing the type in v0.1 requires a " +
            "paired plugin lifecycle (registration, fault handling, test harness) that is " +
            "deliberately deferred until STRG-065.");
    }

    private static Type[] SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
        }
    }
}
