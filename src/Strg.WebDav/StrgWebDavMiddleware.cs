using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Strg.Core.Exceptions;
using Strg.Core.Identity;
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
/// <para><b>PROPFIND / GET / HEAD / PUT (STRG-068 + STRG-070).</b> PROPFIND and GET/HEAD are the
/// STRG-068 reads (PROPFIND XML + byte streaming, Range support added with STRG-070). PUT is
/// the STRG-070 upload path — <c>files.write</c> scope-gated, Commit-first quota-gated, returns
/// 201/204/409/507 per RFC 4918 §9.7. Remaining write verbs (MKCOL, DELETE, COPY, MOVE,
/// PROPPATCH, LOCK, UNLOCK) still return 501 — STRG-071/072 replace each in turn.</para>
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
        IStrgWebDavLockManager lockManager,
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

        if (HttpMethods.IsPut(method))
        {
            // Scope gate: WebDAV has no endpoint-routing metadata so the FilesWrite policy doesn't
            // fire automatically. Enforcing here matches what [Authorize(Policy="FilesWrite")] does
            // on every GraphQL/REST write surface. Short-circuit with 403 (authenticated but
            // lacking the scope) — 401 would lie about the auth state.
            if (!context.User.HasScope("files.write"))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            Guid userId;
            try
            {
                userId = context.User.GetUserId();
            }
            catch (InvalidOperationException)
            {
                // sub claim missing: the token is malformed rather than unauthorized. 401 is the
                // honest status — the client should re-authenticate rather than retry with
                // different scopes.
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            // STRG-072 lock gate. If someone else holds an exclusive lock on this resource, or
            // we hold one but didn't present its token via If:, RFC 4918 §9.10.6 requires 423
            // Locked. CanWriteAsync returns true when either (a) no active lock exists or (b) the
            // caller owns the lock AND presented its token.
            var putIfToken = WebDavIfHeader.ExtractFirstLockToken(
                context.Request.Headers["If"].ToString());
            var putResourceUri = BuildResourceUri(drive.Name, itemPath);
            if (!await lockManager.CanWriteAsync(putResourceUri, userId, putIfToken, context.RequestAborted))
            {
                context.Response.StatusCode = StatusCodes.Status423Locked;
                return;
            }

            try
            {
                var (document, created) = await store.PutDocumentAsync(
                    drive,
                    itemPath,
                    context.Request.Body,
                    context.Request.ContentType,
                    userId,
                    context.RequestAborted);

                // 201 Created for new resources, 204 No Content for overwrites — the RFC 4918 §9.7
                // response shape clients like Windows Explorer and macOS Finder key off.
                context.Response.StatusCode = created
                    ? StatusCodes.Status201Created
                    : StatusCodes.Status204NoContent;
                if (!string.IsNullOrEmpty(document.ContentHash))
                {
                    context.Response.Headers[HeaderNames.ETag] = $"\"{document.ContentHash}\"";
                }
            }
            catch (QuotaExceededException)
            {
                // RFC 4918 §9.7.3 — 507 Insufficient Storage is the exact status WebDAV defines for
                // quota-denied writes, so no need for a JSON error body here.
                context.Response.StatusCode = StatusCodes.Status507InsufficientStorage;
            }
            catch (InvalidOperationException ex)
            {
                // Store raises this for "parent folder missing", "overwriting a folder", and
                // "PUT on root" — all RFC 4918 §9.7.1 / §9.7.2 "409 Conflict" territory. The
                // message is diagnostic; we log it but don't echo to the client.
                _logger.LogInformation(
                    "WebDAV PUT refused on drive {DriveName} path {Path}: {Reason}",
                    drive.Name, itemPath, ex.Message);
                context.Response.StatusCode = StatusCodes.Status409Conflict;
            }
            return;
        }

        // LOCK / UNLOCK must NOT require the target to exist. RFC 4918 §9.10.4 defines "lock-null
        // resources": a client may LOCK a URL where the resource does not yet exist to reserve it
        // before the PUT that populates it. Gating LOCK behind GetItemAsync would make the common
        // "lock, then upload" flow unreachable — clients like Cadaver and Microsoft Office rely on
        // this ordering. UNLOCK is handled the same way; we authenticate the lock by token, not
        // by whether a file currently exists at the URL.
        if (string.Equals(method, "LOCK", StringComparison.OrdinalIgnoreCase))
        {
            await HandleLockAsync(context, drive, itemPath, lockManager, options.Value);
            return;
        }

        if (string.Equals(method, "UNLOCK", StringComparison.OrdinalIgnoreCase))
        {
            await HandleUnlockAsync(context, drive, itemPath, lockManager);
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

        // Remaining write-side verbs deferred to STRG-071 (MKCOL/DELETE/COPY/MOVE/PROPPATCH). 501
        // is the truthful status — dispatch reached the verb but no handler is wired yet.
        context.Response.StatusCode = StatusCodes.Status501NotImplemented;
    }

    private static async Task HandleLockAsync(
        HttpContext context,
        Core.Domain.Drive drive,
        string itemPath,
        IStrgWebDavLockManager lockManager,
        WebDavOptions options)
    {
        // Scope gate: LOCK is a write-surface even without a body — granting a lock affects who
        // can write the file next. Enforcing files.write here mirrors the PUT gate and closes the
        // "I can't PUT, but I can DoS the file by locking it" loophole.
        if (!context.User.HasScope("files.write"))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        Guid ownerId;
        try
        {
            ownerId = context.User.GetUserId();
        }
        catch (InvalidOperationException)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var resourceUri = BuildResourceUri(drive.Name, itemPath);
        var timeout = WebDavTimeoutParser.Parse(
            context.Request.Headers["Timeout"],
            options.DefaultLockTimeoutSeconds,
            options.MaxLockTimeoutSeconds);

        // Refresh path: empty body + If header with the token. RFC 4918 §9.10.2 — no owner
        // element on the wire, we only bump ExpiresAt.
        var ifToken = WebDavIfHeader.ExtractFirstLockToken(context.Request.Headers["If"].ToString());
        if (ifToken is not null && (context.Request.ContentLength ?? 0) == 0)
        {
            var refreshed = await lockManager.RefreshAsync(resourceUri, ifToken, ownerId, timeout, context.RequestAborted);
            if (refreshed is null)
            {
                // Precondition failed — the token doesn't match an active lock the caller owns.
                // 412 is the RFC-correct status for "your If: condition was false".
                context.Response.StatusCode = StatusCodes.Status412PreconditionFailed;
                return;
            }
            await WebDavResponseWriter.WriteLockAsync(
                context, refreshed, StatusCodes.Status200OK, context.RequestAborted);
            return;
        }

        var ownerInfo = await WebDavResponseWriter.ReadLockOwnerAsync(
            context.Request.Body, context.RequestAborted);

        var result = await lockManager.LockAsync(
            resourceUri, ownerId, ownerInfo, timeout, cancellationToken: context.RequestAborted);

        if (result.Status == LockStatus.Conflict)
        {
            context.Response.StatusCode = StatusCodes.Status423Locked;
            return;
        }

        await WebDavResponseWriter.WriteLockAsync(
            context, result.Lock!, StatusCodes.Status201Created, context.RequestAborted);
    }

    private static async Task HandleUnlockAsync(
        HttpContext context,
        Core.Domain.Drive drive,
        string itemPath,
        IStrgWebDavLockManager lockManager)
    {
        if (!context.User.HasScope("files.write"))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        Guid ownerId;
        try
        {
            ownerId = context.User.GetUserId();
        }
        catch (InvalidOperationException)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var token = WebDavIfHeader.ExtractLockTokenHeader(context.Request.Headers["Lock-Token"].ToString());
        if (string.IsNullOrEmpty(token))
        {
            // RFC 4918 §9.11 — missing Lock-Token is 400 Bad Request, not 401. The client sent a
            // malformed request; no amount of re-authing fixes it.
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var resourceUri = BuildResourceUri(drive.Name, itemPath);
        var unlocked = await lockManager.UnlockAsync(resourceUri, token, ownerId, context.RequestAborted);
        // RFC 4918 §9.11 specifies 204 No Content on success, 409 Conflict when the token doesn't
        // match an active lock. 409 rather than 404 because the resource exists — the lock
        // assertion the client made is what's wrong.
        context.Response.StatusCode = unlocked
            ? StatusCodes.Status204NoContent
            : StatusCodes.Status409Conflict;
    }

    private static string BuildResourceUri(string driveName, string itemPath)
    {
        // Drive-rooted URI: "{driveName}" or "{driveName}/{path}". Not the raw /dav/... request
        // path — the /dav prefix is a routing concern and prefix changes shouldn't strand locks.
        return string.IsNullOrEmpty(itemPath) ? driveName : $"{driveName}/{itemPath}";
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
