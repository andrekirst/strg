using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Strg.Core.Storage;
using Strg.Infrastructure.Data;

namespace Strg.WebDav;

/// <summary>
/// STRG-067 / STRG-068 — WebDAV middleware. Intercepts WebDAV verbs on the <c>/dav</c> branch,
/// resolves the target <see cref="Core.Domain.Drive"/>, and dispatches per-verb handlers.
/// Non-WebDAV HTTP methods fall through to <c>_next</c> so GraphQL, REST controllers, and any
/// other pipeline segments keep working unchanged.
///
/// <para><b>Verb dispatch surface.</b> The RFC 4918 method set — <c>OPTIONS</c>, <c>HEAD</c>,
/// <c>GET</c>, <c>PUT</c>, <c>DELETE</c>, <c>PROPFIND</c>, <c>PROPPATCH</c>, <c>MKCOL</c>,
/// <c>COPY</c>, <c>MOVE</c>, <c>LOCK</c>, <c>UNLOCK</c>. RFC 3253 versioning verbs (<c>REPORT</c>,
/// <c>VERSION-CONTROL</c>, <c>CHECKIN</c>/<c>CHECKOUT</c>, <c>MKWORKSPACE</c>, <c>UPDATE</c>,
/// <c>LABEL</c>, <c>MERGE</c>, <c>BASELINE-CONTROL</c>, <c>MKACTIVITY</c>) are out of scope for
/// v0.1 and MUST be added to <see cref="WebDavMethods"/> before any per-verb handler is
/// registered — a missing entry here means that verb will fall through to <c>_next</c> and leak
/// into the rest of the pipeline, so the list is a pin, not a default.</para>
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
/// for anything that doesn't match <c>[a-z0-9-]</c>. Cross-tenant drives fall into the same
/// bucket by construction (the resolver runs through the tenant-scoped query filter) — STRG-068
/// TC-005 expected 403 but the STRG-067 TC-003 pin established 404 as the not-found-or-not-yours
/// shape, and the enumeration-oracle argument there still holds: distinguishing "wrong tenant"
/// from "no such drive" would leak drive existence across tenant boundaries.</para>
///
/// <para><b>PROPFIND / GET / HEAD (STRG-068).</b> These three verbs get real handling because
/// the STRG-068 tests (TC-001 PROPFIND XML, TC-003 GET stream) pin them. Write verbs (PUT,
/// MKCOL, DELETE, COPY, MOVE, PROPPATCH, LOCK, UNLOCK) still return 501 — STRG-069/070/071/072
/// replace each in turn.</para>
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

    public async Task InvokeAsync(
        HttpContext context,
        IDriveResolver resolver,
        ITenantContext tenantContext,
        IStrgWebDavStore store,
        IOptions<WebDavOptions> options)
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

        string itemPath;
        try
        {
            itemPath = WebDavUriParser.ExtractValidatedPath(context.Request.Path.Value ?? "/");
        }
        catch (StoragePathException ex)
        {
            // TC-004 pin — `..`, `%00`, UNC-style backslash, reserved names all fail fast at the
            // URL boundary, not at the storage provider. The 400 tells the client the request
            // was malformed; no information about whether the target existed is leaked.
            _logger.LogDebug(ex,
                "WebDAV: rejected unsafe path {Path} on drive {DriveName}",
                context.Request.Path.Value, driveName);
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var item = await store.GetItemAsync(drive, itemPath, context.RequestAborted);
        if (item is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method))
        {
            if (item is not IStrgWebDavStoreDocument document)
            {
                // GET on a collection is legal per RFC 4918 §9.4 but the response body is
                // implementation-defined (often an HTML listing). For v0.1 we decline — clients
                // that understand WebDAV use PROPFIND, and non-WebDAV browsers landing on a
                // collection URL shouldn't leak directory contents.
                context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                return;
            }

            await WebDavResponseWriter.WriteGetAsync(
                context,
                document,
                includeBody: HttpMethods.IsGet(method),
                context.RequestAborted);
            return;
        }

        if (string.Equals(method, "PROPFIND", StringComparison.OrdinalIgnoreCase))
        {
            await WebDavResponseWriter.WritePropFindAsync(
                context,
                item,
                options.Value.PropfindInfinityMaxItems,
                context.RequestAborted);
            return;
        }

        // Write-side verbs deferred to STRG-069/070/071/072. 501 is the truthful status —
        // dispatch reached the verb but no handler is wired yet. This keeps the foundation
        // honest instead of returning a silent 200 or a misleading 404.
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
