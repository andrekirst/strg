using FluentAssertions;
using MassTransit;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Strg.Core.Events;
using Strg.Infrastructure.Messaging.Consumers;
using Xunit;

namespace Strg.Api.Tests.Messaging;

/// <summary>
/// STRG-064 INFO-2: pins that <see cref="QuotaNotificationConsumer"/>'s Fault&lt;T&gt; handler
/// binds exceptions via the scalar <c>{Exceptions}</c> template (paired with the explicit
/// <c>ExceptionType: Message</c> projection in the consumer body) rather than the destructured
/// <c>{@Exceptions}</c>. Mirrors the STRG-062 defense captured by
/// <c>AuditLogConsumerTests.DeadLetter_log_renders_ExceptionType_and_Message_via_explicit_projection</c>.
///
/// <para><b>Regression shape defended.</b> A future "let's get more detail" refactor that flips
/// <c>{Exceptions}</c> → <c>{@Exceptions}</c> would push Serilog's destructure path over
/// <see cref="ExceptionInfo"/>, flowing <c>StackTrace</c> + the <c>Data</c> dictionary
/// (EF parameter values, neighbouring-row tenant IDs from FK-violation <c>DETAIL</c>, etc.)
/// into the structured payload — re-opening the exact cross-tenant leakage window STRG-062
/// INFO-1 closed. Pinning the template text is the cheapest tripwire.</para>
///
/// <para><b>Why a template-text scan, not an empirical render.</b> The AuditLogConsumer mirror
/// test (<c>DeadLetter_log_renders_ExceptionType_and_Message_via_explicit_projection</c>) runs
/// a full Postgres + RabbitMQ + probe-consumer + Serilog sink pipeline because it was written
/// alongside an empirical uncertainty about the raw <c>ExceptionInfo[]</c> render. That
/// uncertainty is resolved. For this invariant, the template text IS the property under test —
/// a change to <c>{@Exceptions}</c> is a template-text edit. MEL's <c>FormattedLogValues</c>
/// state exposes the original template via the <c>{OriginalFormat}</c> key, so the scan runs
/// unit-level without containers.</para>
/// </summary>
public sealed class QuotaNotificationConsumerTemplateTests
{
    [Fact]
    public async Task Fault_handler_template_binds_Exceptions_scalar_not_AtExceptions_destructured()
    {
        var logger = new TemplateCapturingLogger<QuotaNotificationConsumer>();
        var consumer = new QuotaNotificationConsumer(db: null!, logger: logger);

        var exceptionInfo = Substitute.For<ExceptionInfo>();
        exceptionInfo.ExceptionType.Returns("System.InvalidOperationException");
        exceptionInfo.Message.Returns("PROBE_MARKER");

        var fault = Substitute.For<Fault<QuotaWarningEvent>>();
        fault.Message.Returns(new QuotaWarningEvent(
            TenantId: Guid.NewGuid(),
            UserId: Guid.NewGuid(),
            UsedBytes: 810,
            QuotaBytes: 1_000));
        fault.Exceptions.Returns(new[] { exceptionInfo });

        var context = Substitute.For<ConsumeContext<Fault<QuotaWarningEvent>>>();
        context.Message.Returns(fault);

        await consumer.Consume(context);

        var template = logger.Entries.Should().ContainSingle().Subject.Template;

        template.Should().Contain("{Exceptions}",
            "the Fault handler must bind the projected ExceptionType: Message strings via the scalar template");
        template.Should().NotContain("{@Exceptions}",
            "@-destructure would flow StackTrace + Data dictionary contents into the structured payload — " +
            "re-opening the EF-parameter / FK-DETAIL leakage window that STRG-062 INFO-1 closed");
    }

    private sealed record CapturedEntry(LogLevel Level, string Template, string Rendered);

    private sealed class TemplateCapturingLogger<T> : ILogger<T>
    {
        public List<CapturedEntry> Entries { get; } = [];

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            // MEL wraps the template + args in FormattedLogValues, which implements
            // IReadOnlyList<KeyValuePair<string, object?>>. The final entry with key
            // "{OriginalFormat}" carries the raw unrendered template — that's what we pin.
            var template = state is IReadOnlyList<KeyValuePair<string, object?>> pairs
                ? pairs.FirstOrDefault(kv => kv.Key == "{OriginalFormat}").Value?.ToString() ?? string.Empty
                : string.Empty;
            Entries.Add(new CapturedEntry(logLevel, template, formatter(state, exception)));
        }
    }
}
