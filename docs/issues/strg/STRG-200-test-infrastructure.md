---
id: STRG-200
title: Set up integration test infrastructure (TestContainers + Respawn)
milestone: v0.1
priority: critical
status: open
type: testing
labels: [testing, infrastructure]
depends_on: [STRG-001, STRG-004, STRG-030, STRG-061]
blocks: [STRG-201, STRG-202, STRG-203]
assigned_agent_type: feature-dev:code-architect
estimated_complexity: medium
---

# STRG-200: Set up integration test infrastructure

## Summary

Create the shared integration test infrastructure used by all test projects: `StrgWebApplicationFactory` backed by **TestContainers** (PostgreSQL + RabbitMQ), **Respawn** for fast DB reset between tests, `IOutboxFlusher` for deterministic outbox testing, and JWT helpers for authenticated requests. One factory and container set per test class.

## Technical Specification

### File: `tests/Strg.Integration.Tests/StrgWebApplicationFactory.cs`

```csharp
// One factory + one container set per test class
public sealed class StrgWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private PostgreSqlContainer _postgres = null!;
    private RabbitMqContainer _rabbitmq = null!;
    private Respawner _respawner = null!;

    public async Task InitializeAsync()
    {
        _postgres = new PostgreSqlBuilder().Build();
        _rabbitmq = new RabbitMqBuilder().Build();
        await Task.WhenAll(_postgres.StartAsync(), _rabbitmq.StartAsync());
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            // Use TestContainers PostgreSQL (real Postgres, not SQLite)
            services.RemoveDbContext<StrgDbContext>();
            services.AddDbContext<StrgDbContext>(options =>
                options.UseNpgsql(_postgres.GetConnectionString()));

            // Use TestContainers RabbitMQ (real transport, matches prod)
            services.AddMassTransit(x =>
            {
                x.AddConsumer<AuditLogConsumer>();
                x.AddConsumer<QuotaNotificationConsumer>();
                x.AddConsumer<GraphQLSubscriptionPublisher>();
                x.UsingRabbitMq((ctx, cfg) =>
                {
                    cfg.Host(_rabbitmq.GetConnectionString());
                    cfg.ConfigureEndpoints(ctx);
                });
            });

            // Test-only IOutboxFlusher for deterministic outbox assertions
            services.AddSingleton<IOutboxFlusher, TestOutboxFlusher>();

            // Replace storage with in-memory provider (no disk I/O in tests)
            services.AddSingleton<IStorageProvider, InMemoryStorageProvider>();
        });

        builder.UseEnvironment("Testing");
    }

    public async Task ResetDatabaseAsync()
    {
        // Respawn: truncates all tables in ~5ms — called between test methods in InitializeAsync
        _respawner ??= await Respawner.CreateAsync(_postgres.GetConnectionString(),
            new RespawnerOptions { DbAdapter = DbAdapter.Postgres });
        await _respawner.ResetAsync(_postgres.GetConnectionString());
    }

    public new async Task DisposeAsync()
    {
        await _postgres.DisposeAsync();
        await _rabbitmq.DisposeAsync();
        await base.DisposeAsync();
    }

    public async Task<(string accessToken, Guid userId)> LoginAsync(
        string email = "admin@test.com",
        string password = "Admin123!")
    {
        var client = CreateClient();
        var response = await client.PostAsync("/connect/token", new FormUrlEncodedContent([
            new("grant_type", "password"),
            new("username", email),
            new("password", password),
            new("client_id", "strg-api"),
            new("scope", "files.read files.write tags.write")
        ]));
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (json.GetProperty("access_token").GetString()!, /* parse sub claim */);
    }
}
```

### File: `tests/Strg.Integration.Tests/TestFixtures/DatabaseFixture.cs`

```csharp
public sealed class DatabaseFixture : IAsyncLifetime
{
    public StrgWebApplicationFactory Factory { get; } = new();

    public async Task InitializeAsync()
    {
        // Run migrations
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StrgDbContext>();
        await db.Database.EnsureCreatedAsync();

        // Seed test data: tenant, admin user, test drive
        await SeedAsync(db);
    }

    private static async Task SeedAsync(StrgDbContext db)
    {
        var tenant = new Tenant { Name = "Test Tenant" };
        var admin = new User
        {
            TenantId = tenant.Id,
            Email = "admin@test.com",
            PasswordHash = /* PBKDF2("Admin123!") */,
            DisplayName = "Admin",
            Role = UserRole.Admin,
            QuotaBytes = 100 * 1024 * 1024 // 100MB
        };
        db.Tenants.Add(tenant);
        db.Users.Add(admin);
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Factory.DisposeAsync().AsTask();
}
```

### JWT helper for test requests:

```csharp
public static class TestAuthExtensions
{
    public static HttpClient WithAuth(this HttpClient client, string token)
    {
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
```

### `xunit.runner.json` (in test project root):

```json
{
  "parallelizeTestCollections": false,
  "methodDisplay": "ClassAndMethod"
}
```

Integration tests should NOT run in parallel (shared SQLite database).

## Acceptance Criteria

- [ ] `StrgWebApplicationFactory` bootstraps the full pipeline with TestContainers PostgreSQL + RabbitMQ
- [ ] One factory and container set per test class (`IClassFixture<StrgWebApplicationFactory>`)
- [ ] `ResetDatabaseAsync()` uses Respawn to truncate all tables between test methods (~5ms)
- [ ] `IOutboxFlusher.FlushAsync()` forces immediate outbox dispatch for deterministic event assertions
- [ ] `LoginAsync` returns a valid JWT for authenticated test requests
- [ ] Tests run sequentially (parallel disabled in `xunit.runner.json`)
- [ ] Docker required on dev machine and CI for TestContainers

## Test Cases

- **TC-001**: `LoginAsync` returns valid JWT
- **TC-002**: `GET /health/ready` against factory → `200 OK`
- **TC-003**: MassTransit test harness captures published messages

## Implementation Tasks

- [ ] Create `StrgWebApplicationFactory.cs`
- [ ] Create `DatabaseFixture.cs` with seed data
- [ ] Create `TestAuthExtensions.cs`
- [ ] Configure `xunit.runner.json` to disable parallel test execution
- [ ] Add NuGet: `Microsoft.AspNetCore.Mvc.Testing`, `Testcontainers.PostgreSql`, `Testcontainers.RabbitMq`, `Respawn`
- [ ] Create `IOutboxFlusher` interface in test project
- [ ] Create `TestOutboxFlusher` that calls the MassTransit outbox delivery service directly

## Testing Tasks

- [ ] Smoke test: factory starts without errors
- [ ] Smoke test: login returns JWT

## Definition of Done

- [ ] All integration tests can use `StrgWebApplicationFactory`
- [ ] Seeded DB contains admin user, test drive
