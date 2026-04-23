---
id: STRG-067
title: Implement StrgWebDavMiddleware and WebDAV route registration
milestone: v0.1
priority: high
status: done
type: implementation
labels: [webdav, api]
depends_on: [STRG-013, STRG-025]
blocks: [STRG-068, STRG-069, STRG-070, STRG-071, STRG-072]
assigned_agent_type: feature-dev:code-architect
estimated_complexity: medium
---

# STRG-067: Implement StrgWebDavMiddleware and WebDAV route registration

## Summary

Set up the WebDAV server middleware using the `WebDav.Server` NuGet package (NWebDav). Register the WebDAV route at `/dav/{driveName}/` and wire it to the storage abstraction layer. This is the foundation all WebDAV method handlers build on.

## Technical Specification

### Packages: `NWebDav.Server`, `NWebDav.Server.AspNetCore`

### Registration in `Program.cs`:

```csharp
// Mount WebDAV at /dav/{driveName}/
app.Map("/dav/{driveName}", webdavApp =>
{
    webdavApp.UseAuthentication();
    webdavApp.UseAuthorization();
    webdavApp.UseMiddleware<StrgWebDavMiddleware>();
});
```

### File: `src/Strg.WebDav/StrgWebDavMiddleware.cs`

```csharp
public sealed class StrgWebDavMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IWebDavDispatcher _dispatcher;

    public StrgWebDavMiddleware(RequestDelegate next, IWebDavDispatcher dispatcher)
    {
        _next = next;
        _dispatcher = dispatcher;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Only handle WebDAV verbs; pass everything else down
        if (!IsWebDavRequest(context.Request.Method))
        {
            await _next(context);
            return;
        }

        await _dispatcher.DispatchRequestAsync(context);
    }

    private static bool IsWebDavRequest(string method) =>
        method is "PROPFIND" or "PROPPATCH" or "MKCOL" or "COPY" or "MOVE"
            or "LOCK" or "UNLOCK" or "GET" or "PUT" or "DELETE" or "HEAD" or "OPTIONS";
}
```

### DI registration in `Strg.WebDav/WebDavServiceExtensions.cs`:

```csharp
public static IServiceCollection AddStrgWebDav(this IServiceCollection services)
{
    services.AddSingleton<IWebDavDispatcher, WebDavDispatcher>();
    services.AddScoped<IStrgWebDavStore, StrgWebDavStore>();
    services.AddScoped<ILockManager, DbLockManager>(); // DB-backed locks (file_locks table)
    return services;
}
```

### Drive resolution from URL:

The `driveName` route value maps to `Drive.Name` (URL-safe name: `[a-z0-9-]`). If the drive does not exist → `404 Not Found`. If the user lacks access → `403 Forbidden`.

## Acceptance Criteria

- [ ] `OPTIONS /dav/{driveName}/` → `200 OK` with `DAV: 1, 2` header
- [ ] `GET /dav/{driveName}/` without auth → `401 Unauthorized`
- [ ] Non-WebDAV methods (POST, GraphQL) are NOT intercepted by middleware
- [ ] Drive name case-insensitive lookup
- [ ] Unknown drive name → `404 Not Found`
- [ ] `AddStrgWebDav()` extension method registers all required services

## Test Cases

- **TC-001**: `OPTIONS /dav/my-drive/` → `200` with `DAV: 1, 2` response header
- **TC-002**: `GET /graphql` through the same app → middleware passes through
- **TC-003**: `PROPFIND /dav/nonexistent/` → `404 Not Found`
- **TC-004**: `GET /dav/my-drive/` without Bearer token → `401`
- **TC-005**: Map `/dav/{driveName}` prefix captured correctly for nested paths

## Implementation Tasks

- [ ] Add `Strg.WebDav` project to solution
- [ ] Install `NWebDav.Server`, `NWebDav.Server.AspNetCore` packages
- [ ] Create `StrgWebDavMiddleware.cs`
- [ ] Create `WebDavServiceExtensions.cs` with `AddStrgWebDav()`
- [ ] Register WebDAV route in `Program.cs`
- [ ] Drive resolver helper: `IDriveResolver.ResolveAsync(string driveName, Guid tenantId)`

## Testing Tasks

- [ ] Integration test: `OPTIONS /dav/test-drive/` → `200`
- [ ] Integration test: middleware does not intercept GraphQL route
- [ ] Integration test: unknown drive name → `404`

## Security Review Checklist

- [ ] WebDAV route behind `UseAuthentication()` and `UseAuthorization()`
- [ ] `driveName` path value validated against `[a-z0-9-]` pattern (no path traversal)
- [ ] `OPTIONS` does not reveal server internals

## Code Review Checklist

- [ ] Middleware is `sealed`
- [ ] WebDAV verb list is exhaustive (OPTIONS, HEAD, GET, PUT, DELETE, PROPFIND, PROPPATCH, MKCOL, COPY, MOVE, LOCK, UNLOCK)
- [ ] `AddStrgWebDav()` called from `Program.cs`, not from within the middleware

## Definition of Done

- [ ] `OPTIONS` request returns `DAV: 1, 2`
- [ ] Auth enforced
- [ ] Drive resolution working
