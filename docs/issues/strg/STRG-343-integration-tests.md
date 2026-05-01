---
id: STRG-343
title: Integration tests — happy path, bomb, encrypted-drive, prune, extension-point
milestone: v0.2
priority: high
status: open
type: testing
labels: [thumbnails, phase-15, integration-tests, testcontainers]
depends_on: [STRG-336, STRG-337, STRG-338, STRG-339, STRG-340, STRG-342]
blocks: []
assigned_agent_type: feature-dev:code-architect
estimated_complexity: large
---

# STRG-343: Integration tests — happy path, bomb, encrypted-drive, prune, extension-point

## Summary

Comprehensive integration tests for the thumbnail subsystem. Real binary fixtures, Testcontainers (PostgreSQL + RabbitMQ + InMemoryStorageProvider), `ITestHarness` + `IOutboxFlusher` orchestration. Zero `Thread.Sleep` / `Task.Delay`. Includes the extension-point sanity test that proves Phase 16 will be additive.

## Background / Context

Issue #52 says: "Real files (small PNG, WebP, animated GIF, orientation-tagged JPEG, corrupt JPEG, 100 MP PNG bomb)". The test suite must cover every TC the parent issue lists. Pattern follows `tests/Strg.Integration.Tests/Messaging/AuditLogConsumerTests.cs:33-53, 410-434` exactly — same Testcontainers setup, same harness build.

## Technical Specification

### Test class layout

| File | Coverage |
|---|---|
| `tests/Strg.Integration.Tests/Thumbnails/ThumbnailGenerationConsumerTests.cs` | TC-003, TC-004, TC-006, TC-012 (happy path, idempotency, encrypted-drive, timeout) |
| `tests/Strg.Integration.Tests/Thumbnails/ThumbnailEndpointTests.cs` | TC-009 (REST ETag/304/Cache-Control) |
| `tests/Strg.Integration.Tests/Thumbnails/ThumbnailCleanupTests.cs` | TC-007, TC-008 (file delete + version prune propagation) |
| `tests/Strg.Integration.Tests/Thumbnails/ImageGeneratorTests.cs` | TC-005, TC-011 (pixel bomb, EXIF orientation + metadata strip) |
| `tests/Strg.Integration.Tests/Thumbnails/ThumbnailBackfillTests.cs` | TC-006 (encrypted-drive backfill) + STRG-342 backfill cases |
| `tests/Strg.Integration.Tests/Thumbnails/ThumbnailGraphQlTests.cs` | TC-001, TC-010 (DataLoader batching, extension-point) |
| `tests/Strg.Integration.Tests/Thumbnails/ExtensionPointTests.cs` | TC-010 (fake `TxtThumbnailer` registers, backfill routes to it) |

### Harness pattern

```csharp
public sealed class ThumbnailGenerationConsumerTests(ITestOutputHelper output) : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private readonly RabbitMqContainer _rabbitMq = new RabbitMqBuilder("rabbitmq:3.13-management-alpine").Build();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await _rabbitMq.StartAsync();
    }

    private async Task<(IServiceProvider Provider, ITestHarness Harness, StrgDbContext Db, IOutboxFlusher Flusher)> BuildAsync(
        Action<IServiceCollection>? overrides = null)
    {
        var services = new ServiceCollection();
        services.AddDbContext<StrgDbContext>(o =>
            o.UseNpgsql(_postgres.GetConnectionString()));

        services.AddMassTransitTestHarness(bus =>
        {
            bus.AddConsumer<ThumbnailGenerationConsumer>();
            bus.AddConsumer<ThumbnailGenerationFaultObserver>();
            bus.AddConsumer<ThumbnailCleanupConsumer>();
            bus.AddEntityFrameworkOutbox<StrgDbContext>(outbox =>
            {
                outbox.UsePostgres();
                outbox.UseBusOutbox();
                outbox.QueryDelay = TimeSpan.FromSeconds(1);
            });
            bus.UsingRabbitMq((ctx, cfg) =>
            {
                cfg.Host(new Uri(_rabbitMq.GetConnectionString()));
                cfg.UseMessageRetry(r => r.Immediate(2));
                cfg.ConfigureEndpoints(ctx);
            });
        });

        services.AddSingleton<IThumbnailGenerator, MagickNetImageThumbnailer>();
        services.AddSingleton<IThumbnailGeneratorRegistry, ThumbnailGeneratorRegistry>();
        services.AddSingleton<IThumbnailRepository, ThumbnailRepository>();
        services.AddSingleton<IStorageProvider, InMemoryStorageProvider>();
        services.AddSingleton<StrgMetrics>();
        services.AddSingleton(TimeProvider.System);
        services.Configure<ThumbnailOptions>(o => { /* test defaults */ });

        overrides?.Invoke(services);

        var provider = services.BuildServiceProvider();
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var db = provider.GetRequiredService<StrgDbContext>();
        await db.Database.MigrateAsync();

        var flusher = provider.GetRequiredService<IOutboxFlusher>();
        return (provider, harness, db, flusher);
    }
}
```

### Test fixtures — `tests/Strg.Integration.Tests/Thumbnails/Fixtures/`

| File | Purpose |
|---|---|
| `small.jpg` | Baseline JPEG (e.g., 600×400, ~50 KiB) |
| `small.png` | Baseline PNG with transparency |
| `small.webp` | Baseline WebP |
| `animated.gif` | 3-frame GIF |
| `corrupt.jpg` | Truncated JPEG (header only) |
| `bomb.png` | 100 MP-class image header (palette PNG; the actual pixel data can be small after compression — only the header dimensions matter) |
| `portrait-orientation.jpg` | EXIF Orientation = 6 |
| `gps-tagged.heic` | HEIC with embedded GPS |
| `metadata-rich.tiff` | TIFF with EXIF + IPTC + XMP |

**Fixture generation note**: the `bomb.png` cannot legitimately be 100 MP of pixel data in source control (~400 MB). It can be a crafted PNG header that ADVERTISES 10000×10000 dimensions but has minimal IDAT — the `MagickImageInfo` probe reads dimensions from the header, which is what we're testing. Document this in a `Fixtures/README.md`.

### Extension-point test — `ExtensionPointTests.cs`

```csharp
[Fact]
public async Task BackfillRoutesToFakeGenerator_ProvesPhase16IsAdditive()
{
    // Arrange: register a fake "txt" generator
    var (provider, harness, db, flusher) = await BuildAsync(overrides: services =>
    {
        services.AddSingleton<IThumbnailGenerator, FakeTxtThumbnailer>();
    });

    var version = await SeedFileVersionAsync(db, mimeType: "text/plain", contents: "hello");

    // Act: publish backfill event (the same event the admin mutation would publish)
    await harness.Bus.Publish(new ThumbnailGenerationRequestedEvent(
        tenantId, version.FileId, version.Id, version.DriveId));

    await flusher.FlushAsync();
    await harness.WaitForConsumed<ThumbnailGenerationRequestedEvent>();

    // Assert: 3 Ready rows exist (one per variant), Format = "txt-icon" (fake's output)
    var rows = await db.ThumbnailEntries.Where(t => t.FileVersionId == version.Id).ToListAsync();
    Assert.Equal(3, rows.Count);
    Assert.All(rows, r => Assert.Equal(ThumbnailStatus.Ready, r.Status));
    Assert.All(rows, r => Assert.Equal("txt-icon", r.Format));
}

private sealed class FakeTxtThumbnailer : IThumbnailGenerator
{
    public bool CanHandle(string mimeType, ReadOnlySpan<byte> magicBytes) =>
        mimeType == "text/plain";
    public Task<ThumbnailGenerationOutcome> GenerateAsync(Stream s, ThumbnailRequest r, CancellationToken ct) =>
        Task.FromResult<ThumbnailGenerationOutcome>(
            new ThumbnailGenerationOutcome.Success(new MemoryStream(new byte[]{1,2,3}), 1, 1, "txt-icon"));
}
```

This proves the extension-point contract: NO consumer change, NO API change, NO DB change to support a new generator.

### Zero-sleep guarantee

All waits go through `IOutboxFlusher.FlushAsync()` and `harness.WaitForConsumed<T>()`. Document in test class doc-comment: "Adding `Thread.Sleep` / `Task.Delay` is a CI-flake bug; flag in review."

## Acceptance Criteria

- [ ] All seven test classes exist with the coverage matrix above.
- [ ] Test fixtures committed under `Fixtures/`; fixture README documents how each was generated.
- [ ] Zero `Thread.Sleep` / `Task.Delay` in test code (architecture test or grep enforces).
- [ ] Testcontainers PostgreSQL + RabbitMQ; storage uses `InMemoryStorageProvider` per CLAUDE.md.
- [ ] Extension-point test (`FakeTxtThumbnailer`) proves the Phase 16 contract.
- [ ] Each TC-001…TC-012 from issue #52 has a named test method (mapping documented in the PR description).

## Test Cases

(All TC-001 through TC-012 from issue #52 are covered. See the test-class layout table above for which file each lives in.)

## Implementation Tasks

- [ ] Build the Testcontainers-based harness factory (one per test class, per existing pattern).
- [ ] Add fixture binaries; document generation steps.
- [ ] Implement all seven test classes.
- [ ] `FakeTxtThumbnailer` for the extension-point test.
- [ ] Verify zero-sleep via grep / architecture test.

## Security Review Checklist

- [ ] No credentials in test code or fixtures.
- [ ] Fixture binaries don't contain real GPS coordinates / camera serials (synthetic test data only).
- [ ] `bomb.png` fixture is documented as a crafted-header file — won't accidentally consume 400 MB of disk if a contributor decompresses it.
- [ ] Tests don't write to the production-like blob path; `InMemoryStorageProvider` keeps everything in-memory.

## Code Review Checklist

- [ ] `IAsyncLifetime` (xunit) for container lifecycle.
- [ ] One `PostgreSqlContainer` + one `RabbitMqContainer` per test class (existing project convention — NOT shared across classes for isolation).
- [ ] `IOutboxFlusher.FlushAsync()` + `harness.WaitForConsumed<T>()` for sync points.
- [ ] No `Task.Delay` anywhere.

## Definition of Done

- [ ] All acceptance criteria green.
- [ ] Per CLAUDE.md S1 trigger: full integration suite handed to user (Claude does NOT auto-run).
- [ ] Targeted run: `dotnet test tests/Strg.Integration.Tests --filter "FullyQualifiedName~Thumbnail"` passes locally.
- [ ] PR description maps each TC to its test method.
