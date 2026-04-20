---
id: STRG-309
title: X-Strg-Wait-For-Rules middleware
milestone: v0.1
priority: medium
status: open
type: implementation
labels: [inbox, api, middleware]
depends_on: [STRG-308]
blocks: []
assigned_agent_type: feature-dev
estimated_complexity: small
---

# STRG-309: X-Strg-Wait-For-Rules middleware

## Summary

Implement the `WaitForRulesMiddleware` and `IInboxWaitService` that allow upload clients to optionally block until inbox rule processing completes. When a client sends `X-Strg-Wait-For-Rules: true`, the HTTP connection is held open until `InboxProcessingConsumer` finishes and publishes `InboxFileProcessedEvent` (or a configurable timeout elapses). This enables synchronous "upload and get final path" workflows.

## Technical Specification

### Wait service interface (`src/Strg.Core/Inbox/IInboxWaitService.cs`)

```csharp
public interface IInboxWaitService
{
    /// <summary>Register interest in the processed event for the given file.</summary>
    void Register(Guid fileId);

    /// <summary>Signal that processing for fileId is complete.</summary>
    void Notify(Guid fileId, InboxFileProcessedEvent result);

    /// <summary>
    /// Wait until Notify is called for fileId, or until timeout.
    /// Returns null on timeout.
    /// </summary>
    Task<InboxFileProcessedEvent?> WaitAsync(Guid fileId, TimeSpan timeout, CancellationToken ct = default);
}
```

### Wait service implementation (`src/Strg.Infrastructure/Inbox/InboxWaitService.cs`)

```csharp
public sealed class InboxWaitService : IInboxWaitService
{
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<InboxFileProcessedEvent>> _waiters = new();

    public void Register(Guid fileId) =>
        _waiters.TryAdd(fileId, new TaskCompletionSource<InboxFileProcessedEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously));

    public void Notify(Guid fileId, InboxFileProcessedEvent result)
    {
        if (_waiters.TryRemove(fileId, out var tcs))
            tcs.TrySetResult(result);
    }

    public async Task<InboxFileProcessedEvent?> WaitAsync(Guid fileId, TimeSpan timeout, CancellationToken ct)
    {
        if (!_waiters.TryGetValue(fileId, out var tcs))
            return null;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            return await tcs.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            _waiters.TryRemove(fileId, out _);
            return null;
        }
    }
}
```

### Consumer change (in STRG-308)

`InboxProcessingConsumer` calls `IInboxWaitService.Notify` after publishing `InboxFileProcessedEvent`:

```csharp
_waitService.Notify(file.Id, processedEvent);
```

### Middleware (`src/Strg.Api/Middleware/WaitForRulesMiddleware.cs`)

```csharp
public class WaitForRulesMiddleware(RequestDelegate next, IInboxWaitService waitService,
    IOptions<InboxOptions> options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var waitHeader = context.Request.Headers["X-Strg-Wait-For-Rules"].FirstOrDefault();
        if (!string.Equals(waitHeader, "true", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        // Pre-register before calling next (upload may complete very fast)
        // FileId is not yet known; we'll register after the upload handler writes the file.
        // Use HttpContext.Items to pass fileId from the upload handler to this middleware.
        context.Items["WaitForRules"] = true;

        await next(context); // upload handler runs here

        if (context.Items.TryGetValue("UploadedFileId", out var fileIdObj) &&
            fileIdObj is Guid fileId)
        {
            waitService.Register(fileId);
            var timeout = TimeSpan.FromSeconds(options.Value.WaitForRulesTimeoutSeconds);
            var result = await waitService.WaitAsync(fileId, timeout, context.RequestAborted);

            if (result != null)
                context.Response.Headers["X-Strg-Inbox-Status"] = result.Status.ToString();
            else
                context.Response.Headers["X-Strg-Inbox-Status"] = "Timeout";
        }
    }
}
```

Upload handlers must set `context.Items["UploadedFileId"] = file.Id` when `WaitForRules` is in `Items`.

### Configuration (`src/Strg.Infrastructure/Options/InboxOptions.cs`)

```csharp
public class InboxOptions
{
    public const string SectionName = "Inbox";
    public int WaitForRulesTimeoutSeconds { get; set; } = 30;
}
```

```json
// appsettings.json
{
  "Inbox": {
    "WaitForRulesTimeoutSeconds": 30
  }
}
```

### DI + middleware registration (`src/Strg.Api/Program.cs`)

```csharp
services.AddSingleton<IInboxWaitService, InboxWaitService>(); // singleton — state shared across requests
services.Configure<InboxOptions>(configuration.GetSection(InboxOptions.SectionName));
app.UseMiddleware<WaitForRulesMiddleware>();
```

## Acceptance Criteria

- [ ] `IInboxWaitService` with `Register`, `Notify`, `WaitAsync` exists in `Strg.Core`
- [ ] `InboxWaitService` is a singleton (shared state across requests)
- [ ] `WaitForRulesMiddleware` checks `X-Strg-Wait-For-Rules: true` header
- [ ] Middleware adds `X-Strg-Inbox-Status` response header with final status or `Timeout`
- [ ] `WaitForRulesTimeoutSeconds` is configurable in `appsettings.json` (default 30s)
- [ ] When header is absent, middleware is a no-op (no performance impact)
- [ ] Upload handlers set `context.Items["UploadedFileId"]` when wait is requested

## Test Cases

- TC-001: Request without `X-Strg-Wait-For-Rules` → middleware is transparent; no extra headers
- TC-002: Request with `X-Strg-Wait-For-Rules: true` → response includes `X-Strg-Inbox-Status: Processed`
- TC-003: Consumer slower than timeout → response includes `X-Strg-Inbox-Status: Timeout`
- TC-004: Two concurrent uploads for different files → each waiter resolved independently

## Implementation Tasks

- [ ] Create `src/Strg.Core/Inbox/IInboxWaitService.cs`
- [ ] Create `src/Strg.Infrastructure/Inbox/InboxWaitService.cs` (singleton)
- [ ] Create `src/Strg.Infrastructure/Options/InboxOptions.cs`
- [ ] Create `src/Strg.Api/Middleware/WaitForRulesMiddleware.cs`
- [ ] Update `InboxProcessingConsumer` to call `IInboxWaitService.Notify`
- [ ] Register in DI and middleware pipeline
- [ ] Update upload handlers to set `context.Items["UploadedFileId"]`
- [ ] Write integration tests

## Definition of Done

- [ ] `dotnet build` passes with zero warnings
- [ ] All TC-001 through TC-004 tests pass
- [ ] `IInboxWaitService` has zero external NuGet dependencies in `Strg.Core`
