using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using Strg.Infrastructure.Data;

namespace Strg.WebDav;

/// <summary>
/// STRG-067 — WebDAV foundation middleware. Intercepts WebDAV verbs on the <c>/dav</c> branch,
/// resolves the target <see cref="Core.Domain.Drive"/>, and hands off to the per-verb handlers
/// (STRG-068+). Non-WebDAV HTTP methods fall through to <c>_next</c> so GraphQL, REST controllers,
/// and any other pipeline segments keep working unchanged.
///
/// <para><b>Verb dispatch surface.</b> The full RFC 4918 + RFC 3253 method set — <c>OPTIONS</c>,
/// <c>HEAD</c>, <c>GET</c>, <c>PUT</c>, <c>DELETE</c>, <c>PROPFIND</c>, <c>PROPPATCH</c>,
/// <c>MKCOL</c>, <c>COPY</c>, <c>MOVE</c>, <c>LOCK</c>, <c>UNLOCK</c>. A missing entry here means
/// that verb will be treated as non-WebDAV and leak into the rest of the pipeline, so the list is
/// a pin, not a default.</para>
///
/// <para><b>OPTIONS is pre-auth and pre-resolve.</b> RFC 4918 §10.1 allows clients to probe
/// server capabilities without credentials; enforcing auth here would break every WebDAV client's
/// initial handshake. The response advertises <c>DAV: 1, 2</c> — class-2 because STRG-070's lock
/// manager ships Locks/Unlocks.</para>
///
/// <para><b>Auth enforcement.</b> <c>UseAuthentication()</c> in the branch populates
/// <see cref="HttpContext.User"/> but does not reject anonymous requests on its own — the
/// <c>FallbackPolicy</c> on the app's <c>AddAuthorization</c> applies to endpoint-routed targets,
/// not to raw middleware terminals. The explicit 401 below is what TC-004 pins.</para>
///
/// <para><b>Drive resolution.</b> Unknown drive → 404 <i>before</i> any per-verb handler runs, so
/// an invalid URL never reaches the storage provider. The <see cref="IDriveResolver"/> also
/// filters malformed <c>driveName</c> values (path-traversal defence) by returning <c>null</c>
/// for anything that doesn't match <c>[a-z0-9-]</c>.</para>
/// </summary>
public sealed class StrgWebDavMiddleware
{
    private static readonly HashSet<string> WebDavMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "OPTIONS", "HEAD", "GET", "PUT", "DELETE",
        "PROPFIND", "PROPPATCH", "MKCOL", "COPY", "MOVE", "LOCK", "UNLOCK",
    };

    private readonly RequestDelegate _next;
    private readonly ILogger<StrgWebDavMiddleware> _logger;

    public StrgWebDavMiddleware(RequestDelegate next, ILogger<StrgWebDavMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, IDriveResolver resolver, ITenantContext tenantContext)
    {
        var method = context.Request.Method;

        if (!WebDavMethods.Contains(method))
        {
            await _next(context);
            return;
        }

        if (HttpMethods.IsOptions(method))
        {
            // class 1 (base) + class 2 (locks). STRG-069/070/071 add the verb bodies; the
            // advertisement here is truthful because the middleware already refuses to fall
            // through on LOCK/UNLOCK.
            context.Response.Headers[HeaderNames.Allow] =
                "OPTIONS, HEAD, GET, PUT, DELETE, PROPFIND, PROPPATCH, MKCOL, COPY, MOVE, LOCK, UNLOCK";
            context.Response.Headers["DAV"] = "1, 2";
            context.Response.StatusCode = StatusCodes.Status200OK;
            return;
        }

        if (context.User.Identity?.IsAuthenticated != true)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var driveName = ExtractDriveName(context.Request.Path);
        if (driveName is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var drive = await resolver.ResolveAsync(driveName, tenantContext.TenantId, context.RequestAborted);
        if (drive is null)
        {
            _logger.LogDebug(
                "WebDAV: drive {DriveName} not resolvable for tenant {TenantId} — returning 404",
                driveName, tenantContext.TenantId);
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // STRG-068 will inject IWebDavDispatcher here and hand off (context, drive) to the per-verb
        // handlers. The foundation slice terminates with 501 so clients get a truthful "not yet
        // implemented" instead of a silent 404 or, worse, a pipeline fall-through that would
        // invite unauthenticated access to other endpoints.
        context.Response.StatusCode = StatusCodes.Status501NotImplemented;
    }

    // `app.Map("/dav", ...)` strips the prefix before the middleware runs, so Request.Path starts
    // with `/{driveName}[/remainder]`. The driveName is just the first segment; everything after
    // is the in-drive resource path (consumed by STRG-068's WebDAV store).
    private static string? ExtractDriveName(PathString path)
    {
        if (!path.HasValue)
        {
            return null;
        }

        var value = path.Value!.AsSpan();
        if (value.Length == 0 || value[0] != '/')
        {
            return null;
        }

        value = value[1..];
        var slashIndex = value.IndexOf('/');
        var segment = slashIndex < 0 ? value : value[..slashIndex];
        return segment.IsEmpty ? null : segment.ToString();
    }
}
