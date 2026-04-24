using System.Text.Json;
using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Strg.Core.Auditing;
using Strg.Core.Domain;
using Strg.Core.Events;
using Strg.Infrastructure.Data;
using Strg.Infrastructure.Messaging.Consumers;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Xunit;
using Xunit.Abstractions;

namespace Strg.Integration.Tests.Messaging;

/// <summary>
/// STRG-062 TC-001..TC-005 + idempotency. Proves the <see cref="AuditLogConsumer"/> writes an
/// append-only <see cref="AuditEntry"/> row per domain event dispatched through the MassTransit
/// outbox and that at-least-once redelivery collapses on the <see cref="AuditEntry.EventId"/>
/// unique constraint.
///
/// <para>Same container shape as STRG-061's outbox tests: one <see cref="PostgreSqlContainer"/> +
/// one <see cref="RabbitMqContainer"/> per class, fresh database per fact.</para>
/// </summary>
public sealed class AuditLogConsumerTests(ITestOutputHelper output) : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .Build();

    private readonly RabbitMqContainer _rabbitMq = new RabbitMqBuilder("rabbitmq:3.13-management-alpine")
        .Build();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await _rabbitMq.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _rabbitMq.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task TC001_FileUploadedEvent_creates_audit_entry_with_file_uploaded_action()
    {
        var tenantId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var driveId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await using var provider = await BuildServiceProviderAsync(tenantId);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await PublishAsync(provider, tenantId, new FileUploadedEvent(
            tenantId, fileId, driveId, userId, Size: 2048, MimeType: "image/png"));

        (await harness.Consumed.Any<FileUploadedEvent>()).Should().BeTrue(
            "harness must observe the consumer pulling the event off the outbox");

        var entry = await GetSingleAuditEntryAsync(provider, fileId);
        entry.Action.Should().Be(AuditActions.FileUploaded);
        entry.TenantId.Should().Be(tenantId);
        entry.UserId.Should().Be(userId);
        entry.ResourceType.Should().Be("FileItem");
        entry.ResourceId.Should().Be(fileId);
        entry.EventId.Should().NotBeNull().And.NotBe(Guid.Empty);

        // Details is a camelCase JSON blob — the wire format the STRG-062 spec fixes so GraphQL
        // admin queries + audit-log exporters can rely on field names without a transform layer.
        var details = JsonDocument.Parse(entry.Details!).RootElement;
        details.GetProperty("driveId").GetGuid().Should().Be(driveId);
        details.GetProperty("size").GetInt64().Should().Be(2048);
        details.GetProperty("mimeType").GetString().Should().Be("image/png");
    }

    [Fact]
    public async Task TC002_FileDeletedEvent_creates_audit_entry_with_file_deleted_action()
    {
        var tenantId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var driveId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await using var provider = await BuildServiceProviderAsync(tenantId);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await PublishAsync(provider, tenantId, new FileDeletedEvent(tenantId, fileId, driveId, userId));

        (await harness.Consumed.Any<FileDeletedEvent>()).Should().BeTrue();

        var entry = await GetSingleAuditEntryAsync(provider, fileId);
        entry.Action.Should().Be(AuditActions.FileDeleted);
        entry.ResourceId.Should().Be(fileId);

        var details = JsonDocument.Parse(entry.Details!).RootElement;
        details.GetProperty("driveId").GetGuid().Should().Be(driveId);
    }

    [Fact]
    public async Task TC003_Transient_DbUpdateException_retries_and_eventually_persists_one_row()
    {
        // TC-003: non-idempotency DbUpdateException (e.g. deadlock, conflict on an unrelated
        // column) must rethrow so MassTransit's retry pipeline takes over. A ThrowOnceInterceptor
        // fails the first AuditEntry INSERT; the consumer rethrows, MassTransit retries, second
        // attempt has a fresh scope where the interceptor has already fired → succeeds.
        var tenantId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var interceptor = new ThrowOnceInterceptor();

        await using var provider = await BuildServiceProviderAsync(tenantId, interceptor);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await PublishAsync(provider, tenantId, new FileUploadedEvent(
            tenantId, fileId, Guid.NewGuid(), Guid.NewGuid(), Size: 512, MimeType: "text/plain"));

        // Poll for the audit row — the first attempt throws in the interceptor (AuditEntry
        // INSERT rolled back), MassTransit retries, second attempt commits. Polling rather than
        // harness.Consumed.Any<T>() because the harness flags as "consumed" on the first attempt
        // even if the consumer threw, and we specifically want the *successful* write to land.
        var entry = await WaitForAuditEntryAsync(provider, fileId, TimeSpan.FromSeconds(45));
        entry.Should().NotBeNull("retry must eventually persist the audit row");

        interceptor.Hits.Should().BeGreaterOrEqualTo(2,
            "the interceptor must have seen the failing attempt + at least one retry");

        // Exactly one audit row lands — the failed attempt rolled back, the retry committed.
        var entries = await GetAuditEntriesAsync(provider, fileId);
        entries.Should().ContainSingle(e => e.Action == AuditActions.FileUploaded);
    }

    [Fact]
    public async Task TC004_FileMovedEvent_details_json_contains_oldPath_and_newPath()
    {
        var tenantId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        const string oldPath = "/docs/draft.md";
        const string newPath = "/docs/archive/draft-2026.md";

        await using var provider = await BuildServiceProviderAsync(tenantId);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await PublishAsync(provider, tenantId, new FileMovedEvent(
            tenantId, fileId, DriveId: Guid.NewGuid(), oldPath, newPath, UserId: Guid.NewGuid()));

        (await harness.Consumed.Any<FileMovedEvent>()).Should().BeTrue();

        var entry = await GetSingleAuditEntryAsync(provider, fileId);
        entry.Action.Should().Be(AuditActions.FileMoved);

        var details = JsonDocument.Parse(entry.Details!).RootElement;
        details.GetProperty("oldPath").GetString().Should().Be(oldPath);
        details.GetProperty("newPath").GetString().Should().Be(newPath);
    }

    [Fact]
    public async Task TC005_Two_events_in_sequence_produce_two_audit_rows()
    {
        var tenantId = Guid.NewGuid();
        var fileId1 = Guid.NewGuid();
        var fileId2 = Guid.NewGuid();

        await using var provider = await BuildServiceProviderAsync(tenantId);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        using (var scope = provider.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<StrgDbContext>();
            var bus = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();
            ctx.Tenants.Add(new Tenant { Id = tenantId, Name = "audit-two" });

            await bus.Publish(new FileUploadedEvent(
                tenantId, fileId1, Guid.NewGuid(), Guid.NewGuid(), 1, "a/b"));
            await bus.Publish(new FileDeletedEvent(
                tenantId, fileId2, Guid.NewGuid(), Guid.NewGuid()));

            await ctx.SaveChangesAsync();
        }

        (await harness.Consumed.Any<FileUploadedEvent>()).Should().BeTrue();
        (await harness.Consumed.Any<FileDeletedEvent>()).Should().BeTrue();

        await using var assertScope = provider.CreateAsyncScope();
        var db = assertScope.ServiceProvider.GetRequiredService<StrgDbContext>();
        var rows = await db.AuditEntries
            .Where(e => e.ResourceId == fileId1 || e.ResourceId == fileId2)
            .ToListAsync();

        rows.Should().HaveCount(2);
        rows.Should().ContainSingle(e => e.ResourceId == fileId1 && e.Action == AuditActions.FileUploaded);
        rows.Should().ContainSingle(e => e.ResourceId == fileId2 && e.Action == AuditActions.FileDeleted);
    }

    [Fact]
    public async Task Idempotency_DbLevel_unique_constraint_rejects_duplicate_EventId()
    {
        // Direct DB-level test: the partial unique index must reject a second AuditEntry row with
        // the same EventId. This is the guard the consumer relies on — if it silently dropped,
        // at-least-once redelivery would produce N duplicate audit rows.
        var tenantId = Guid.NewGuid();
        var eventId = Guid.NewGuid();

        await using var provider = await BuildServiceProviderAsync(tenantId);

        using var scope1 = provider.CreateScope();
        var db1 = scope1.ServiceProvider.GetRequiredService<StrgDbContext>();
        db1.Tenants.Add(new Tenant { Id = tenantId, Name = "dupe-test" });
        db1.AuditEntries.Add(new AuditEntry
        {
            TenantId = tenantId,
            UserId = Guid.NewGuid(),
            Action = AuditActions.FileUploaded,
            ResourceType = "FileItem",
            ResourceId = Guid.NewGuid(),
            EventId = eventId,
        });
        await db1.SaveChangesAsync();

        using var scope2 = provider.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<StrgDbContext>();
        db2.AuditEntries.Add(new AuditEntry
        {
            TenantId = tenantId,
            UserId = Guid.NewGuid(),
            Action = AuditActions.FileDeleted,
            ResourceType = "FileItem",
            ResourceId = Guid.NewGuid(),
            EventId = eventId,
        });

        Func<Task> act = () => db2.SaveChangesAsync();
        var thrown = await act.Should().ThrowAsync<DbUpdateException>();
        thrown.Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be("23505");
    }

    [Fact]
    public async Task Idempotency_Consumer_swallows_duplicate_EventId_delivery()
    {
        // Consumer-level idempotency: seed an AuditEntry with EventId = X, then publish a domain
        // event whose MessageId is also X. The consumer attempts the insert, hits the partial
        // unique index, catches the PostgresException, and returns without rethrowing. The
        // outbox-state row is marked delivered so MassTransit does not loop on it.
        var tenantId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var messageId = Guid.NewGuid();

        await using var provider = await BuildServiceProviderAsync(tenantId);

        // Seed the "already persisted" row with the same EventId the next publish will use.
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();
            db.Tenants.Add(new Tenant { Id = tenantId, Name = "idem-test" });
            db.AuditEntries.Add(new AuditEntry
            {
                TenantId = tenantId,
                UserId = Guid.NewGuid(),
                Action = AuditActions.FileUploaded,
                ResourceType = "FileItem",
                ResourceId = fileId,
                Details = "{\"source\":\"seed\"}",
                EventId = messageId,
            });
            await db.SaveChangesAsync();
        }

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        using (var scope = provider.CreateScope())
        {
            var bus = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();
            var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();

            await bus.Publish(
                new FileUploadedEvent(tenantId, fileId, Guid.NewGuid(), Guid.NewGuid(), 99, "x/y"),
                ctx => ctx.MessageId = messageId);
            await db.SaveChangesAsync();
        }

        (await harness.Consumed.Any<FileUploadedEvent>()).Should().BeTrue(
            "consumer must observe the redelivered event and silently collapse");

        // Exactly one row with this EventId — the seed, untouched. The consumer's INSERT
        // rolled back on the unique-violation and did not throw further.
        var rows = await GetAuditEntriesAsync(provider, fileId);
        rows.Should().ContainSingle(e => e.EventId == messageId)
            .Which.Details.Should().Be("{\"source\":\"seed\"}");
    }

    [Fact]
    public async Task TenantId_on_audit_row_comes_from_event_payload_not_ambient_context()
    {
        // Regression test for STRG-061 audit INFO-2: MassTransit consumers execute in a
        // background-service scope where the ambient ITenantContext resolves to Guid.Empty
        // (no HTTP request). If a future refactor routes AuditEntry writes through a helper that
        // fills TenantId from ambient context, every consumed event would land in the zero-tenant
        // bucket. This test pins the contract by *explicitly* disagreeing — ambient Guid.Empty,
        // event TenantId = tenantX — and asserting the row uses the event's value.
        var eventTenantId = Guid.NewGuid();
        var fileId = Guid.NewGuid();

        // Build with ambient tenant = Guid.Empty, matching production consumer scope where no
        // HttpContext is present.
        await using var provider = await BuildServiceProviderAsync(Guid.Empty);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();
            var bus = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();

            // Seed the event's tenant so AuditEntry.TenantId FK resolves. Tenant is not
            // tenant-scoped (it's the root entity, not a TenantedEntity), so no filter bypass
            // is needed regardless of ambient context.
            db.Tenants.Add(new Tenant { Id = eventTenantId, Name = "info2-regression" });

            await bus.Publish(new FileUploadedEvent(
                TenantId: eventTenantId,
                FileId: fileId,
                DriveId: Guid.NewGuid(),
                UserId: Guid.NewGuid(),
                Size: 1,
                MimeType: "text/plain"));
            await db.SaveChangesAsync();
        }

        (await harness.Consumed.Any<FileUploadedEvent>()).Should().BeTrue();

        // Read back with IgnoreQueryFilters — the audit query filter is keyed on ambient
        // ITenantContext.TenantId = Guid.Empty, and a filtered query would return nothing even
        // if the row landed correctly under eventTenantId. We need the raw row to assert its
        // TenantId column value.
        await using var assertScope = provider.CreateAsyncScope();
        var auditDb = assertScope.ServiceProvider.GetRequiredService<StrgDbContext>();
        var row = await auditDb.AuditEntries
            .IgnoreQueryFilters()
            .SingleAsync(e => e.ResourceId == fileId);

        // Single assertion — comparing to a freshly-minted Guid.NewGuid() already rules out
        // Guid.Empty, so a separate NotBe(Guid.Empty) line would be logically redundant and
        // mislead readers into treating them as independent guards against different bug shapes.
        row.TenantId.Should().Be(eventTenantId,
            "AuditEntry.TenantId must come from the event payload, not ambient ITenantContext (which is Guid.Empty here)");
    }

    [Fact]
    public async Task DeadLetter_log_renders_ExceptionType_and_Message_via_explicit_projection()
    {
        // STRG-062 follow-up INFO-1. Pins the shape of the {Exceptions} binding after the
        // explicit `ExceptionType: Message` projection in AuditLogConsumer.ProjectExceptions.
        //
        // Background: the raw ExceptionInfo[] binding (pre-fix) empirically rendered as
        // ["MassTransit.Events.FaultExceptionInfo"] because ExceptionInfo is an interface with
        // no ToString override — Serilog's scalar converter falls back to Type.FullName on the
        // concrete impl type. That rendering carried no triage signal.
        //
        // Post-fix, the consumer projects each ExceptionInfo into "{ExceptionType}: {Message}"
        // strings before binding so Serilog's scalar pipeline has useful content. This test
        // forces a real Fault<FileUploadedEvent> through the Serilog render path and asserts:
        //
        //  (1) The rendered line contains the exception class name (forensic signal #1).
        //  (2) The rendered line contains the exception message (forensic signal #2).
        //  (3) The binding is NOT destructured — the log does NOT contain field names like
        //      `StackTrace` or `Data` that would appear under `{@Exceptions}`. This defends
        //      the narrow-projection choice against a future refactor that swaps `{Exceptions}`
        //      for `{@Exceptions}` "to get more detail" and re-opens the EF-parameter-leakage
        //      window the STRG-062 INFO-1 fix was originally protecting against.
        //
        // Test-output dump is kept for audit: a future reader investigating the Fault render
        // shape reads the captured string directly here instead of having to re-run the probe.
        var tenantId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var capturedEvents = new List<LogEvent>();

        // Serilog → in-memory capturing sink, then wrapped as MEL ILoggerFactory. Mirrors prod
        // (Program.cs UseSerilog) rendering semantics, which is the pipeline the {Exceptions}
        // template was specced against — MEL-only tests would measure a different render path.
        await using var serilog = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Sink(new CapturingSink(capturedEvents))
            .CreateLogger();

        var connectionString = await CreateFreshDatabaseAsync();
        var services = new ServiceCollection();
        services.AddLogging(lb => lb.AddSerilog(serilog, dispose: false));
        services.AddSingleton<ITenantContext>(new OutboxTenantContext(tenantId));
        services.AddDbContext<StrgDbContext>(options =>
        {
            options.UseNpgsql(connectionString);
            options.UseOpenIddict();
        });
        services.AddMassTransitTestHarness(bus =>
        {
            bus.AddConsumer<AuditLogConsumer>();
            bus.AddConsumer<DeadLetterProbeConsumer>();

            bus.AddEntityFrameworkOutbox<StrgDbContext>(outbox =>
            {
                outbox.UsePostgres();
                outbox.UseBusOutbox();
                outbox.QueryDelay = TimeSpan.FromSeconds(1);
            });

            bus.UsingRabbitMq((context, cfg) =>
            {
                cfg.Host(new Uri(_rabbitMq.GetConnectionString()));
                cfg.UseMessageRetry(r => r.Immediate(2));
                cfg.ConfigureEndpoints(context);
            });
        });
        services.Configure<MassTransitHostOptions>(o => o.WaitUntilStarted = true);
        services.AddOptions<TestHarnessOptions>().Configure(o =>
        {
            o.TestInactivityTimeout = TimeSpan.FromSeconds(30);
            o.TestTimeout = TimeSpan.FromMinutes(2);
        });

        await using var provider = services.BuildServiceProvider();
        using (var scope = provider.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<StrgDbContext>();
            await ctx.Database.MigrateAsync();
        }

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();
            var bus = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();
            db.Tenants.Add(new Tenant { Id = tenantId, Name = "dead-letter-probe" });
            await bus.Publish(new FileUploadedEvent(
                tenantId, fileId, Guid.NewGuid(), Guid.NewGuid(), Size: 1, MimeType: "text/plain"));
            await db.SaveChangesAsync();
        }

        // Wait for AuditLogConsumer's Fault<FileUploadedEvent> handler to fire — that's the log
        // call whose render shape we're measuring. DeadLetterProbeConsumer throws on every
        // attempt; Immediate(2) → Fault<FileUploadedEvent> published → AuditLogConsumer's Fault
        // endpoint picks it up.
        (await harness.Consumed.Any<Fault<FileUploadedEvent>>()).Should().BeTrue(
            "retry exhaustion on the probe consumer must drive MassTransit to publish Fault<T>");

        // Find the emitted dead-letter line. The AuditLogConsumer endpoint ALSO processes the
        // original FileUploadedEvent and writes its own log lines; we filter to the distinctive
        // "Dead-letter:" prefix.
        var deadLetter = capturedEvents.FirstOrDefault(e =>
            e.MessageTemplate.Text.StartsWith("Dead-letter: FileUploadedEvent", StringComparison.Ordinal));
        deadLetter.Should().NotBeNull(
            "AuditLogConsumer.Consume(Fault<FileUploadedEvent>) should have emitted a Dead-letter log line");

        var renderedMessage = deadLetter!.RenderMessage();

        // Empirical dump — human inspects this to decide Option A vs Option B shape. Keep in
        // test output permanently: a future reader diagnosing a production Fault log by searching
        // "what does {Exceptions} actually look like?" finds this test + its captured output.
        output.WriteLine("=== EMPIRICAL RENDER of {Exceptions} template on a real Fault<FileUploadedEvent> ===");
        output.WriteLine(renderedMessage);
        output.WriteLine("=== Exceptions property raw (Serilog scalar/sequence value) ===");
        if (deadLetter.Properties.TryGetValue("Exceptions", out var exProp))
        {
            output.WriteLine(exProp.ToString());
            output.WriteLine("  (property value type: " + exProp.GetType().Name + ")");
        }
        else
        {
            output.WriteLine("  <Exceptions property NOT BOUND>");
        }
        output.WriteLine("=== LogEvent.Exception (MEL-side .Exception, separate from template bindings) ===");
        output.WriteLine(deadLetter.Exception?.ToString() ?? "<null>");

        const string probeMarker = "DEAD_LETTER_PROBE";
        renderedMessage.Should().Contain("InvalidOperationException",
            "the ExceptionType: Message projection must carry the exception class name into the scalar render");
        renderedMessage.Should().Contain(probeMarker,
            "the ExceptionType: Message projection must carry the exception Message into the scalar render");

        // Defence against a future refactor that "improves" the Fault log by flipping
        // {Exceptions} → {@Exceptions}. Serilog's destructure path would render ExceptionInfo
        // as a structured object with StackTrace / Data properties, re-opening the EF
        // parameter leakage window. The absence of "StackTrace" in the rendered text is the
        // cheapest available tripwire.
        renderedMessage.Should().NotContain("StackTrace",
            "binding must remain scalar string projection — '@' destructure would leak StackTrace + Data fields");
    }

    [Fact]
    public async Task DeadLetter_log_does_not_leak_FK_DETAIL_when_inner_PostgresException_reaches_Fault_pipeline()
    {
        // STRG-062 C3 audit follow-up. Pins the *absence half* of the dead-letter render
        // contract: the sibling test above proves `ExceptionType: Message` carries into the
        // rendered log; this test proves `PostgresException.Detail` does NOT.
        //
        // Why this vector needs its own pin:
        //   - `AuditLogConsumer.IsEventIdUniqueViolation` already swallows 23505 on the EventId
        //     index, so those never reach Fault. But a *different* Postgres failure — FK violation
        //     on AuditEntries.TenantId (event arrives before Tenant row, Tenant-delete race),
        //     check-constraint violation on Details JSON shape, unique-index violation on a
        //     future second constraint — propagates out of Consume, MassTransit retries 2×
        //     (harness) / 5× (prod), then publishes Fault<T>.
        //   - Npgsql's `PostgresException.Detail` on FK violations contains the neighbouring-row
        //     primary key: `Key ("TenantId")=(<guid>) is not present in table "tenants".` That
        //     is cross-tenant state bleeding across the forensic log surface.
        //   - `ExceptionInfo.Message` (sourced from `Exception.Message`) EXCLUDES Detail — only
        //     `PostgresException.ToString()` concatenates it under "Exception data:". As long as
        //     `ProjectExceptions` uses `e.Message` and NOT `e.ToString()`, Detail stays out. A
        //     future "let's include more context" refactor that swaps `e.Message` → `e.ToString()`
        //     would silently open the leak — this test is the tripwire.
        //
        // Test shape mirrors the sibling render test. `FkViolationProbeConsumer` throws a
        // fabricated DbUpdateException wrapping a PostgresException with the Detail field
        // populated; after Immediate(2) retries the harness publishes Fault<FileUploadedEvent>;
        // AuditLogConsumer's Fault handler logs through the Serilog pipeline; CapturingSink
        // intercepts; the assertions hunt for the sentinel Detail strings and fail if any
        // leaked through.
        var tenantId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var capturedEvents = new List<LogEvent>();

        await using var serilog = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Sink(new CapturingSink(capturedEvents))
            .CreateLogger();

        var connectionString = await CreateFreshDatabaseAsync();
        var services = new ServiceCollection();
        services.AddLogging(lb => lb.AddSerilog(serilog, dispose: false));
        services.AddSingleton<ITenantContext>(new OutboxTenantContext(tenantId));
        services.AddDbContext<StrgDbContext>(options =>
        {
            options.UseNpgsql(connectionString);
            options.UseOpenIddict();
        });
        services.AddMassTransitTestHarness(bus =>
        {
            bus.AddConsumer<AuditLogConsumer>();
            bus.AddConsumer<FkViolationProbeConsumer>();

            bus.AddEntityFrameworkOutbox<StrgDbContext>(outbox =>
            {
                outbox.UsePostgres();
                outbox.UseBusOutbox();
                outbox.QueryDelay = TimeSpan.FromSeconds(1);
            });

            bus.UsingRabbitMq((context, cfg) =>
            {
                cfg.Host(new Uri(_rabbitMq.GetConnectionString()));
                cfg.UseMessageRetry(r => r.Immediate(2));
                cfg.ConfigureEndpoints(context);
            });
        });
        services.Configure<MassTransitHostOptions>(o => o.WaitUntilStarted = true);
        services.AddOptions<TestHarnessOptions>().Configure(o =>
        {
            o.TestInactivityTimeout = TimeSpan.FromSeconds(30);
            o.TestTimeout = TimeSpan.FromMinutes(2);
        });

        await using var provider = services.BuildServiceProvider();
        using (var scope = provider.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<StrgDbContext>();
            await ctx.Database.MigrateAsync();
        }

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();
            var bus = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();
            db.Tenants.Add(new Tenant { Id = tenantId, Name = "fk-leak-probe" });
            await bus.Publish(new FileUploadedEvent(
                tenantId, fileId, Guid.NewGuid(), Guid.NewGuid(), Size: 1, MimeType: "text/plain"));
            await db.SaveChangesAsync();
        }

        (await harness.Consumed.Any<Fault<FileUploadedEvent>>()).Should().BeTrue(
            "retry exhaustion on FkViolationProbeConsumer must drive MassTransit to publish Fault<T>");

        var deadLetter = capturedEvents.FirstOrDefault(e =>
            e.MessageTemplate.Text.StartsWith("Dead-letter: FileUploadedEvent", StringComparison.Ordinal));
        deadLetter.Should().NotBeNull(
            "AuditLogConsumer.Consume(Fault<FileUploadedEvent>) should have emitted a Dead-letter log line");

        var renderedMessage = deadLetter!.RenderMessage();

        // Empirical dump kept for audit — a future reader investigating the FK-leak vector
        // inspects this captured output to verify the projection shape without re-running the
        // probe. Same rationale as the sibling render test.
        output.WriteLine("=== EMPIRICAL RENDER of FK-violation Fault<FileUploadedEvent> ===");
        output.WriteLine(renderedMessage);
        output.WriteLine("=== Exceptions property raw (Serilog scalar/sequence value) ===");
        if (deadLetter.Properties.TryGetValue("Exceptions", out var exProp))
        {
            output.WriteLine(exProp.ToString());
        }
        else
        {
            output.WriteLine("  <Exceptions property NOT BOUND>");
        }

        // Positive signal: the projection still carries Type + Message forensic signal. The
        // Message we assert on is the outer DbUpdateException message (what ExceptionInfo.Message
        // captures — MassTransit's FaultExceptionInfo is constructed from the thrown exception's
        // top-level .Message). Without this baseline a regression that zeroes the entire
        // projection would pass the negative assertions below.
        renderedMessage.Should().Contain("DbUpdateException",
            "projection must still carry the outer exception type name — otherwise operators " +
            "have no signal at all for diagnosing the dead-letter");
        renderedMessage.Should().Contain("FK_VIOLATION_PROBE",
            "projection must carry the outer exception's Message so operators can triage; this " +
            "is the baseline without which the negative assertions would be vacuously true");

        // Negative signal: the sentinel Detail content must NOT appear anywhere in the rendered
        // line. Each assertion defends against a distinct regression shape:
        //
        //  (a) SensitiveTenantGuid — the cross-tenant GUID that Postgres writes into Detail on
        //      an FK violation. Its presence means cross-tenant PII bled through the log.
        //  (b) DetailPayload full string — the structured "Key (...)=(...) is not present..."
        //      shape Npgsql emits. Defends against a partial-render refactor that would still
        //      include the key name even if it stripped the guid.
        //  (c) "Detail:" prefix — appears in `PostgresException.ToString()` output under the
        //      "Exception data:" section. Its presence signals a ToString()-based projection,
        //      which is the canonical regression shape the C3 audit flagged.
        renderedMessage.Should().NotContain(FkViolationProbeConsumer.SensitiveTenantGuid,
            "PostgresException.Detail must NOT flow into the rendered log — leaking the FK " +
            "neighbouring-row tenant GUID is cross-tenant state bleeding across the forensic " +
            "log surface. If this fails, a refactor swapped e.Message → e.ToString() in " +
            "ProjectExceptions, re-opening the STRG-062 INFO-1 leak window.");
        renderedMessage.Should().NotContain(FkViolationProbeConsumer.DetailPayload,
            "the full Detail payload must not render even in partial form — defends against a " +
            "'render the key name but strip the value' refactor that would still leak schema shape");
        renderedMessage.Should().NotContain("Detail:",
            "the 'Detail:' prefix is the tell for `PostgresException.ToString()` output (under " +
            "its 'Exception data:' section). Its presence means the projection is calling " +
            "ToString() instead of .Message — canonical C3-audit regression shape.");
    }

    private static async Task PublishAsync<TEvent>(ServiceProvider provider, Guid tenantId, TEvent message)
        where TEvent : class
    {
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();
        var bus = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "audit-test" });
        await bus.Publish(message);
        await db.SaveChangesAsync();
    }

    private static async Task<AuditEntry> GetSingleAuditEntryAsync(ServiceProvider provider, Guid fileId)
    {
        var rows = await GetAuditEntriesAsync(provider, fileId);
        rows.Should().ContainSingle($"exactly one audit row must exist for file {fileId}");
        return rows[0];
    }

    private static async Task<List<AuditEntry>> GetAuditEntriesAsync(ServiceProvider provider, Guid fileId)
    {
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();
        return await db.AuditEntries
            .Where(e => e.ResourceId == fileId)
            .ToListAsync();
    }

    private static async Task<AuditEntry?> WaitForAuditEntryAsync(
        ServiceProvider provider, Guid fileId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            var rows = await GetAuditEntriesAsync(provider, fileId);
            if (rows.Count > 0)
            {
                return rows[0];
            }
            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }
        return null;
    }

    private async Task<ServiceProvider> BuildServiceProviderAsync(Guid tenantId, IInterceptor? saveInterceptor = null)
    {
        var connectionString = await CreateFreshDatabaseAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITenantContext>(new OutboxTenantContext(tenantId));

        services.AddDbContext<StrgDbContext>(options =>
        {
            options.UseNpgsql(connectionString);
            options.UseOpenIddict();
            if (saveInterceptor is not null)
            {
                options.AddInterceptors(saveInterceptor);
            }
        });

        services.AddMassTransitTestHarness(bus =>
        {
            bus.AddConsumer<AuditLogConsumer>();

            bus.AddEntityFrameworkOutbox<StrgDbContext>(outbox =>
            {
                outbox.UsePostgres();
                outbox.UseBusOutbox();
                outbox.QueryDelay = TimeSpan.FromSeconds(1);
            });

            bus.UsingRabbitMq((context, cfg) =>
            {
                var uri = new Uri(_rabbitMq.GetConnectionString());
                cfg.Host(uri);
                // Short retry cycle so TC-003 completes inside the harness timeout. Production
                // uses 5× exponential backoff (see MassTransitExtensions); the test only needs
                // to prove the retry *happens*, not match prod's full schedule.
                cfg.UseMessageRetry(r => r.Immediate(3));
                cfg.ConfigureEndpoints(context);
            });
        });

        services.Configure<MassTransitHostOptions>(o => o.WaitUntilStarted = true);
        services.AddOptions<TestHarnessOptions>().Configure(o =>
        {
            o.TestInactivityTimeout = TimeSpan.FromSeconds(30);
            o.TestTimeout = TimeSpan.FromMinutes(2);
        });

        var provider = services.BuildServiceProvider();

        using (var scope = provider.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<StrgDbContext>();
            await ctx.Database.MigrateAsync();
        }

        return provider;
    }

    private async Task<string> CreateFreshDatabaseAsync()
    {
        var dbName = $"strg_audit_{Guid.NewGuid():N}";
        var adminConnectionString = _postgres.GetConnectionString();

        await using (var connection = new NpgsqlConnection(adminConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE \"{dbName}\"";
            await command.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            Database = dbName,
        }.ConnectionString;
    }
}

/// <summary>
/// Simulates a transient DB failure on the first <c>SaveChangesAsync</c> that touches an
/// <see cref="AuditEntry"/> row — subsequent saves pass through. Used by TC-003 to prove the
/// consumer rethrows non-idempotency DbUpdateExceptions so MassTransit can retry.
/// </summary>
internal sealed class ThrowOnceInterceptor : SaveChangesInterceptor
{
    private int _hits;

    public int Hits => Volatile.Read(ref _hits);

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        // Only throw when an AuditEntry is being inserted — the outbox dispatcher also uses this
        // DbContext for its own bookkeeping saves, and throwing there would break the harness.
        var hasAuditInsert = eventData.Context?.ChangeTracker
            .Entries<AuditEntry>()
            .Any(e => e.State == EntityState.Added) ?? false;

        if (hasAuditInsert && Interlocked.Increment(ref _hits) == 1)
        {
            throw new DbUpdateException(
                "ThrowOnceInterceptor: simulated transient failure",
                new InvalidOperationException("first-attempt-failure"));
        }

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}

/// <summary>
/// Forces a <see cref="Fault{T}"/> publish for <see cref="FileUploadedEvent"/>. Every attempt
/// throws the same distinctive <see cref="InvalidOperationException"/>; after
/// <c>Immediate(2)</c> retries the harness publishes <c>Fault&lt;FileUploadedEvent&gt;</c> which
/// the <see cref="AuditLogConsumer"/> picks up via its <c>IConsumer&lt;Fault&lt;FileUploadedEvent&gt;&gt;</c>
/// implementation. Used only by
/// <c>DeadLetter_log_renders_ExceptionType_and_Message_via_explicit_projection</c>.
/// </summary>
internal sealed class DeadLetterProbeConsumer : IConsumer<FileUploadedEvent>
{
    public Task Consume(ConsumeContext<FileUploadedEvent> context) =>
        throw new InvalidOperationException("DEAD_LETTER_PROBE — forced failure to exercise Fault<T> log render");
}

/// <summary>
/// Forces a <see cref="Fault{T}"/> publish for <see cref="FileUploadedEvent"/> carrying an inner
/// <see cref="PostgresException"/> whose <c>Detail</c> field is populated with a foreign-key
/// leak payload. Used only by
/// <c>DeadLetter_log_does_not_leak_FK_DETAIL_when_inner_PostgresException_reaches_Fault_pipeline</c>
/// to empirically confirm that the <c>{Exceptions}</c> template binding on the AuditLogConsumer
/// Fault handler does NOT flow <c>Detail</c> content into the rendered log line.
///
/// <para>The fabricated <see cref="PostgresException"/> mimics the shape Npgsql/EF Core would
/// emit when an <c>AuditEntry</c> INSERT fails the <c>TenantId</c> foreign key — the scenario
/// the AuditLogConsumer commentary warns against (event arriving before the Tenant row, or a
/// Tenant-delete race). The FK constraint name and the sentinel tenant GUID in <c>Detail</c>
/// are synthetic — the probe never interacts with the real database — so the test is
/// deterministic across Postgres versions and FK naming conventions.</para>
/// </summary>
internal sealed class FkViolationProbeConsumer : IConsumer<FileUploadedEvent>
{
    // Sentinel values: test asserts these strings are absent from the rendered dead-letter log.
    // Exposed as consts so the test can reference them without duplicating the literals.
    public const string SensitiveTenantGuid = "deadbeef-1111-2222-3333-444455556666";
    public const string DetailPayload =
        "Key (\"TenantId\")=(" + SensitiveTenantGuid + ") is not present in table \"tenants\".";
    public const string ConstraintName = "FK_AuditEntries_Tenants_TenantId";

    public Task Consume(ConsumeContext<FileUploadedEvent> context) =>
        throw new DbUpdateException(
            "FK_VIOLATION_PROBE — fabricated DbUpdateException to exercise non-EventId 23503 render path",
            new PostgresException(
                messageText:
                    "insert or update on table \"AuditEntries\" violates foreign key " +
                    "constraint \"" + ConstraintName + "\"",
                severity: "ERROR",
                invariantSeverity: "ERROR",
                sqlState: "23503",
                detail: DetailPayload,
                constraintName: ConstraintName));
}

/// <summary>
/// Serilog sink that appends every incoming <see cref="LogEvent"/> to a caller-provided list.
/// Used by the empirical dead-letter probe to capture and inspect the rendered shape of the
/// <c>{Exceptions}</c> template binding against a real <c>ExceptionInfo[]</c> produced by
/// MassTransit's fault publication pipeline.
/// </summary>
internal sealed class CapturingSink(List<LogEvent> events) : ILogEventSink
{
    private readonly object _gate = new();

    public void Emit(LogEvent logEvent)
    {
        // MassTransit's log pipeline is multi-threaded; lock to keep the captured list
        // consistent when the probe's published event triggers concurrent consumer logs.
        lock (_gate)
        {
            events.Add(logEvent);
        }
    }
}
