using Mediator;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Strg.Application.Features.Files.Copy;
using Strg.Application.Features.Files.Delete;
using Strg.Application.Features.Files.Move;
using Strg.Application.Features.Folders.Create;
using Strg.Core;
using Strg.Core.Constants;
using Strg.Core.Domain;
using Strg.Core.Exceptions;
using Strg.Core.Identity;
using Strg.Core.Storage;

namespace Strg.WebDav;

/// <summary>
/// WebDAV middleware. Intercepts WebDAV verbs on the <c>/dav</c> branch, resolves the target
/// <see cref="Core.Domain.Drive"/>, and dispatches per-verb handlers. Non-WebDAV HTTP methods
/// fall through to <c>_next</c> so GraphQL, REST controllers, and any other pipeline segments
/// keep working unchanged.
///
/// <para><b>Verb registration is a pin, not a default.</b> RFC 3253 versioning verbs
/// (<c>REPORT</c>, <c>VERSION-CONTROL</c>, <c>CHECKIN</c>/<c>CHECKOUT</c>, <c>MKWORKSPACE</c>,
/// <c>UPDATE</c>, <c>LABEL</c>, <c>MERGE</c>, <c>BASELINE-CONTROL</c>, <c>MKACTIVITY</c>) are
/// deliberately not in <c>KnownMethods</c>. A missing entry there means the verb falls through
/// to <c>_next</c> and leaks into the rest of the pipeline, so the set must be expanded
/// alongside any new handler — a silent fall-through would be a security regression.</para>
///
/// <para><b>OPTIONS is pre-auth and pre-resolve</b> per RFC 4918 §10.1 — clients probe server
/// capabilities without credentials, and gating it would break every WebDAV client's initial
/// handshake. The response advertises <c>DAV: 1, 2</c>.</para>
///
/// <para><b>Auth enforcement.</b> <c>UseAuthentication()</c> in the branch populates
/// <see cref="HttpContext.User"/> but does not reject anonymous requests on its own — the
/// <c>FallbackPolicy</c> on the app's <c>AddAuthorization</c> applies to endpoint-routed targets,
/// not to raw middleware terminals. The explicit 401 below is the load-bearing gate.</para>
///
/// <para><b>Drive resolution: unknown or wrong-tenant drives → 404, not 403.</b> Distinguishing
/// "wrong tenant" from "no such drive" would leak drive existence across tenant boundaries; the
/// 404 collapse is an enumeration-oracle defence, not a UX nicety. <see cref="IDriveResolver"/>
/// also filters malformed <c>driveName</c> values (path-traversal) by returning <c>null</c> for
/// anything that doesn't match <c>[a-z0-9-]</c>.</para>
///
/// <para><b>Write verbs are thin protocol shims.</b> MKCOL/DELETE/COPY/MOVE validate WebDAV
/// headers (Destination, Overwrite), gate on <c>files.write</c> scope + active locks, then
/// dispatch to the existing Mediator command. Heavy lifting — recursive soft-delete, path-rebase
/// under directory MOVE, encryption + quota for COPY, outbox publishes — lives in
/// <see cref="DeleteFileHandler"/> / <see cref="MoveFileHandler"/> / <see cref="CopyFileHandler"/>
/// / <see cref="CreateFolderHandler"/>. Re-implementing here would duplicate the encryption +
/// quota + event flow and risk drift between WebDAV and REST surfaces.</para>
///
/// <para><b>Cross-drive / cross-host MOVE/COPY are refused with 502.</b> WebDAV clients use the
/// REST API for cross-drive operations. <see cref="WebDavUriParser.ParseDestination"/> is the
/// gate; the host comparison is host:port only because TLS-termination at a reverse proxy can
/// rewrite https → http on the wire to Kestrel, which would otherwise produce false-positive
/// cross-host rejects.</para>
///
/// <para><b>Overwrite semantics.</b> <c>Overwrite: F</c> + existing destination → 412.
/// <c>Overwrite: T</c> + existing destination is refused with 409 + <c>Strg-Reason:
/// OverwriteCopyDeferred</c> / <c>OverwriteMoveDeferred</c> — implementing it as delete-then-copy
/// in middleware would open a transaction-spanning race window the existing handler does not
/// cover.</para>
/// </summary>
public sealed class StrgWebDavMiddleware(RequestDelegate next, ILogger<StrgWebDavMiddleware> logger)
{
    private static readonly HashSet<string> KnownMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        HttpMethods.Options, HttpMethods.Head, HttpMethods.Get, HttpMethods.Put, HttpMethods.Delete,
        WebDavMethods.PropFind, WebDavMethods.PropPatch, WebDavMethods.Mkcol,
        WebDavMethods.Copy, WebDavMethods.Move, WebDavMethods.Lock, WebDavMethods.Unlock,
    };

    private const string AllowHeaderForResource =
        "OPTIONS, HEAD, GET, PUT, DELETE, PROPFIND, PROPPATCH, COPY, MOVE, LOCK, UNLOCK";

    private const string AllowHeaderForCollection =
        "OPTIONS, HEAD, GET, PROPFIND";

    public async Task InvokeAsync(
        HttpContext context,
        IDriveResolver resolver,
        ITenantContext tenantContext,
        IStrgWebDavStore store,
        IStrgWebDavLockManager lockManager,
        IMediator mediator,
        IOptions<WebDavOptions> options)
    {
        var method = context.Request.Method;

        if (!KnownMethods.Contains(method))
        {
            await next(context);
            return;
        }

        if (HttpMethods.IsOptions(method))
        {
            // class 1 (base) + class 2 (locks). The advertisement is truthful because every
            // verb in the Allow header has a registered handler below.
            // MKCOL is advertised on the wildcard OPTIONS response because it's a legal verb
            // SOMEWHERE on the server; per-resource Allow headers (the 405 paths below) restrict
            // it appropriately for collections vs documents.
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
            logger.LogDebug(
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
            logger.LogDebug(ex,
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
            if (!context.User.HasScope(WebDavScopes.FilesWrite))
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

            // Lock gate. If someone else holds an exclusive lock on this resource, or we hold
            // one but didn't present its token via If:, RFC 4918 §9.10.6 requires 423 Locked.
            // CanWriteAsync returns true when either (a) no active lock exists or (b) the caller
            // owns the lock AND presented its token.
            var putIfToken = WebDavIfHeader.ExtractFirstLockToken(
                context.Request.Headers[WebDavHeaderNames.If].ToString());
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
                logger.LogInformation(
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
        if (string.Equals(method, WebDavMethods.Lock, StringComparison.OrdinalIgnoreCase))
        {
            await HandleLockAsync(context, drive, itemPath, lockManager, options.Value);
            return;
        }

        if (string.Equals(method, WebDavMethods.Unlock, StringComparison.OrdinalIgnoreCase))
        {
            await HandleUnlockAsync(context, drive, itemPath, lockManager);
            return;
        }

        // MKCOL targets a NON-existing URL by construction (RFC 4918 §9.3 — "create a new
        // collection at this URL"), so it MUST dispatch BEFORE the GetItemAsync null-check below;
        // otherwise every MKCOL would 404 before reaching its handler. The dispatch ordering is
        // the artefact previously pinned by WebDavDeferredVerbsTests' "MKCOL omitted" carve-out.
        if (string.Equals(method, WebDavMethods.Mkcol, StringComparison.OrdinalIgnoreCase))
        {
            await HandleMkcolAsync(context, drive, itemPath, store, mediator, lockManager);
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

        if (string.Equals(method, WebDavMethods.PropFind, StringComparison.OrdinalIgnoreCase))
        {
            await WebDavResponseWriter.WritePropFindAsync(
                context,
                item,
                options.Value.PropfindInfinityMaxItems,
                context.RequestAborted);
            return;
        }

        if (HttpMethods.IsDelete(method))
        {
            await HandleDeleteAsync(context, drive, itemPath, item, mediator, lockManager);
            return;
        }

        if (string.Equals(method, WebDavMethods.Copy, StringComparison.OrdinalIgnoreCase))
        {
            await HandleCopyAsync(context, drive, itemPath, item, store, mediator, lockManager);
            return;
        }

        if (string.Equals(method, WebDavMethods.Move, StringComparison.OrdinalIgnoreCase))
        {
            await HandleMoveAsync(context, drive, itemPath, item, store, mediator, lockManager);
            return;
        }

        // PROPPATCH still deferred. 501 is the truthful "verb understood, handler not wired"
        // status per RFC 7231 §6.6.2 — flipping to 405 would contradict the OPTIONS Allow
        // header which still advertises PROPPATCH.
        context.Response.StatusCode = StatusCodes.Status501NotImplemented;
    }

    /// <summary>
    /// MKCOL — RFC 4918 §9.3. The handler dispatches <see cref="CreateFolderCommand"/>, which
    /// auto-creates missing parent segments (a strg-wide convention shared with REST
    /// <c>POST /folders</c>; RFC 4918 §9.3.1 specifies 409 for missing parents but the project's
    /// auto-create policy satisfies the AC's "directory visible in subsequent PROPFIND" pin).
    /// </summary>
    private async Task HandleMkcolAsync(
        HttpContext context,
        Drive drive,
        string itemPath,
        IStrgWebDavStore store,
        IMediator mediator,
        IStrgWebDavLockManager lockManager)
    {
        if (!context.User.HasScope(WebDavScopes.FilesWrite))
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
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        // RFC 4918 §9.3.1: MKCOL on the drive root cannot succeed because the drive itself is
        // the synthetic root collection (no FileItem to create). 405 over 403 because the
        // resource exists — the verb is just illegal for it.
        if (string.IsNullOrEmpty(itemPath))
        {
            context.Response.Headers[HeaderNames.Allow] = AllowHeaderForCollection;
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        // RFC 4918 §9.3.1: the request body is reserved. Refusing non-empty bodies prevents a
        // future extension drift where a body shape gets silently ignored.
        if ((context.Request.ContentLength ?? 0) > 0)
        {
            context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
            return;
        }

        // Pre-existence check. RFC 4918 §9.3.1 specifies 405 Method Not Allowed when the target
        // already exists — Allow header tells the client which verbs ARE legal on it.
        var existing = await store.GetItemAsync(drive, itemPath, context.RequestAborted).ConfigureAwait(false);
        if (existing is not null)
        {
            context.Response.Headers[HeaderNames.Allow] = AllowHeaderForResource;
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        var resourceUri = BuildResourceUri(drive.Name, itemPath);
        var ifToken = WebDavIfHeader.ExtractFirstLockToken(context.Request.Headers[WebDavHeaderNames.If].ToString());
        if (!await lockManager.CanWriteAsync(resourceUri, userId, ifToken, context.RequestAborted).ConfigureAwait(false))
        {
            context.Response.StatusCode = StatusCodes.Status423Locked;
            return;
        }

        var result = await mediator.Send(
            new CreateFolderCommand(drive.Id, itemPath),
            context.RequestAborted).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            context.Response.StatusCode = StatusCodes.Status201Created;
            return;
        }

        // Map handler errors to RFC 4918 §9.3.1 statuses. "Conflict" fires when an ancestor
        // segment exists as a FILE (handler doc-comment) — that's exactly RFC's 409 case.
        // "InvalidPath" should be unreachable given ExtractValidatedPath ran upstream, but we
        // map for defence-in-depth.
        logger.LogInformation(
            "WebDAV MKCOL refused on drive {DriveName} path {Path}: {ErrorCode} {ErrorMessage}",
            drive.Name, itemPath, result.ErrorCode, result.ErrorMessage);
        context.Response.StatusCode = result.ErrorCode switch
        {
            "InvalidPath" => StatusCodes.Status400BadRequest,
            "Conflict" => StatusCodes.Status409Conflict,
            "NotFound" => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status500InternalServerError,
        };
    }

    /// <summary>
    /// DELETE — RFC 4918 §9.6. Soft-delete only; storage blobs are NOT removed (orphan-reaper
    /// is the authoritative sweep). Recursive directory delete is handled inside
    /// <see cref="DeleteFileHandler"/> via a single async-stream pass over descendants — the
    /// handler also pins the trailing-slash anchor that prevents the <c>docs</c> vs.
    /// <c>docsbackup</c> prefix-collision bug.
    /// </summary>
    private async Task HandleDeleteAsync(
        HttpContext context,
        Drive drive,
        string itemPath,
        IStrgWebDavStoreItem item,
        IMediator mediator,
        IStrgWebDavLockManager lockManager)
    {
        if (!context.User.HasScope(WebDavScopes.FilesWrite))
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
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        // Drive-root collection has Id == Guid.Empty (synthetic — no backing FileItem). DELETE
        // on the drive itself is an admin operation, not a WebDAV operation; refuse with 403.
        if (item.Id == Guid.Empty)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var resourceUri = BuildResourceUri(drive.Name, itemPath);
        var ifToken = WebDavIfHeader.ExtractFirstLockToken(context.Request.Headers[WebDavHeaderNames.If].ToString());
        if (!await lockManager.CanWriteAsync(resourceUri, userId, ifToken, context.RequestAborted).ConfigureAwait(false))
        {
            context.Response.StatusCode = StatusCodes.Status423Locked;
            return;
        }

        var result = await mediator.Send(
            new DeleteFileCommand(drive.Id, item.Id),
            context.RequestAborted).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        logger.LogInformation(
            "WebDAV DELETE refused on drive {DriveName} path {Path}: {ErrorCode} {ErrorMessage}",
            drive.Name, itemPath, result.ErrorCode, result.ErrorMessage);
        context.Response.StatusCode = result.ErrorCode switch
        {
            // Race with another deleter — file vanished between GetItem and Send.
            "NotFound" => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status500InternalServerError,
        };
    }

    /// <summary>
    /// COPY — RFC 4918 §9.8. Cross-drive and cross-host destinations are refused with 502.
    /// <c>Overwrite: F</c> + existing destination → 412; <c>Overwrite: T</c> + existing
    /// destination is REFUSED with 409 + <c>Strg-Reason: OverwriteCopyDeferred</c> —
    /// delete-then-copy in middleware would open a transaction-spanning race window the
    /// existing handler doesn't cover.
    /// </summary>
    private async Task HandleCopyAsync(
        HttpContext context,
        Drive drive,
        string itemPath,
        IStrgWebDavStoreItem item,
        IStrgWebDavStore store,
        IMediator mediator,
        IStrgWebDavLockManager lockManager)
    {
        await HandleCopyOrMoveAsync(
            context, drive, itemPath, item, store, mediator, lockManager,
            isMove: false).ConfigureAwait(false);
    }

    /// <summary>
    /// MOVE — RFC 4918 §9.9. Same Destination/Overwrite parsing and lock-gate logic as COPY,
    /// but the lock gate covers BOTH source and destination resources (RFC §9.9.4 — the source
    /// is being mutated, the destination is being created). Within-drive directory MOVE rebases
    /// every descendant under the new prefix inside <see cref="MoveFileHandler"/>.
    /// </summary>
    private async Task HandleMoveAsync(
        HttpContext context,
        Drive drive,
        string itemPath,
        IStrgWebDavStoreItem item,
        IStrgWebDavStore store,
        IMediator mediator,
        IStrgWebDavLockManager lockManager)
    {
        await HandleCopyOrMoveAsync(
            context, drive, itemPath, item, store, mediator, lockManager,
            isMove: true).ConfigureAwait(false);
    }

    /// <summary>
    /// Shared body for COPY/MOVE because they only differ in:
    /// (1) which lock URIs to gate (source for MOVE; both COPY and MOVE gate the destination);
    /// (2) which Mediator command to dispatch;
    /// (3) the deferred-overwrite reason code in the 409 response.
    /// Inlining both would duplicate ~70 lines of header parsing + status mapping; the
    /// <paramref name="isMove"/> flag is the single inflection point and keeps both verbs in
    /// lockstep when the protocol surface evolves.
    /// </summary>
    private async Task HandleCopyOrMoveAsync(
        HttpContext context,
        Drive drive,
        string itemPath,
        IStrgWebDavStoreItem item,
        IStrgWebDavStore store,
        IMediator mediator,
        IStrgWebDavLockManager lockManager,
        bool isMove)
    {
        var verb = isMove ? WebDavMethods.Move : WebDavMethods.Copy;

        if (!context.User.HasScope(WebDavScopes.FilesWrite))
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
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        // Drive-root: id is Guid.Empty (synthetic collection). MOVE/COPY on the drive itself is
        // outside the WebDAV verb's scope; refuse with 403 — same shape as DELETE.
        if (item.Id == Guid.Empty)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var destinationHeader = context.Request.Headers[WebDavHeaderNames.Destination].ToString();
        var parseResult = WebDavUriParser.ParseDestination(
            destinationHeader, context.Request.Host, drive.Name);

        if (!parseResult.IsOk)
        {
            context.Response.StatusCode = parseResult.Status switch
            {
                // RFC 4918 §9.8.5 / §9.9.4 — destination on another server is 502 Bad Gateway.
                // The strg spec extends the same shape to cross-drive destinations because a
                // WebDAV client can't usefully relocate across drives without re-authenticating
                // to a different drive context (REST API is the right surface for that).
                DestinationParseStatus.CrossHost or DestinationParseStatus.CrossDrive
                    => StatusCodes.Status502BadGateway,
                _ => StatusCodes.Status400BadRequest,
            };
            return;
        }

        // [MemberNotNullWhen] on IsOk flow-narrows Path to non-null here — no `!` suppression.
        var destPath = parseResult.Path;

        // Destination drive root is not a legal target. Trailing-slash on the destination ends up
        // here (parser returned empty path).
        if (string.IsNullOrEmpty(destPath))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        // Overwrite header. RFC 4918 §10.6: valid values "T" / "F", default "T". Anything else
        // is malformed → 400 (honest over silent fallback).
        var overwriteHeader = context.Request.Headers[WebDavHeaderNames.Overwrite].ToString();
        bool overwrite;
        if (string.IsNullOrWhiteSpace(overwriteHeader))
        {
            overwrite = true;
        }
        else if (string.Equals(overwriteHeader.Trim(), "T", StringComparison.OrdinalIgnoreCase))
        {
            overwrite = true;
        }
        else if (string.Equals(overwriteHeader.Trim(), "F", StringComparison.OrdinalIgnoreCase))
        {
            overwrite = false;
        }
        else
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        // Pre-existence check on the destination. The handler also returns Conflict on collision
        // (race-window safety), but pre-checking lets us distinguish 412 (Overwrite: F) from
        // 409 (Overwrite: T deferred) — both share the "destination exists" precondition but
        // the wire shape is different.
        var existingDest = await store.GetItemAsync(drive, destPath, context.RequestAborted).ConfigureAwait(false);
        if (existingDest is not null)
        {
            if (!overwrite)
            {
                context.Response.StatusCode = StatusCodes.Status412PreconditionFailed;
                return;
            }

            // Overwrite: T + existing destination is deferred. delete-then-copy in middleware would
            // open a transaction-spanning race window outside the handler's atomicity guarantees;
            // refusing is the honest behaviour. Tracked as a follow-up issue.
            context.Response.Headers[WebDavHeaderNames.StrgReason] =
                isMove ? "OverwriteMoveDeferred" : "OverwriteCopyDeferred";
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            return;
        }

        // Lock gates. RFC 4918 §9.8.5 — destination must not be locked by another owner. RFC
        // 4918 §9.9.4 — additionally, MOVE source must not be locked by another owner (COPY
        // doesn't mutate the source).
        var ifToken = WebDavIfHeader.ExtractFirstLockToken(context.Request.Headers[WebDavHeaderNames.If].ToString());
        var destResourceUri = BuildResourceUri(drive.Name, destPath);
        if (!await lockManager.CanWriteAsync(destResourceUri, userId, ifToken, context.RequestAborted).ConfigureAwait(false))
        {
            context.Response.StatusCode = StatusCodes.Status423Locked;
            return;
        }
        if (isMove)
        {
            var sourceResourceUri = BuildResourceUri(drive.Name, itemPath);
            if (!await lockManager.CanWriteAsync(sourceResourceUri, userId, ifToken, context.RequestAborted).ConfigureAwait(false))
            {
                context.Response.StatusCode = StatusCodes.Status423Locked;
                return;
            }
        }

        try
        {
            Result<FileItem> result;
            if (isMove)
            {
                result = await mediator.Send(
                    new MoveFileCommand(drive.Id, item.Id, destPath, TargetDriveId: null),
                    context.RequestAborted).ConfigureAwait(false);
            }
            else
            {
                result = await mediator.Send(
                    new CopyFileCommand(drive.Id, item.Id, destPath, TargetDriveId: null),
                    context.RequestAborted).ConfigureAwait(false);
            }

            if (result.IsSuccess)
            {
                // 201 Created over 204 No Content because we already refused the existing-dest
                // case above; reaching success here means the destination didn't previously
                // exist (RFC 4918 §9.8.5 / §9.9.4 — 201 for "created", 204 for "replaced").
                context.Response.Headers[HeaderNames.Location] = $"/dav/{drive.Name}/{destPath}";
                context.Response.StatusCode = StatusCodes.Status201Created;
                return;
            }

            logger.LogInformation(
                "WebDAV {Verb} refused on drive {DriveName} path {Path} → {DestPath}: {ErrorCode} {ErrorMessage}",
                verb, drive.Name, itemPath, destPath, result.ErrorCode, result.ErrorMessage);
            context.Response.StatusCode = result.ErrorCode switch
            {
                "NotFound" => StatusCodes.Status404NotFound,
                "InvalidPath" => StatusCodes.Status400BadRequest,
                // Race-window: another writer landed at destPath between the existence check
                // above and Send. 409 differentiates from the 412 (Overwrite: F precondition)
                // path — operators can grep logs to tell the two apart.
                "Conflict" => StatusCodes.Status409Conflict,
                // Directory copy is unsupported in v1.5 (handler limitation, see
                // CopyFileHandler.cs:104-109). MOVE handles directories within-drive.
                "DirectoryCopyUnsupported" => StatusCodes.Status403Forbidden,
                "CrossDriveUnsupported" => StatusCodes.Status502BadGateway,
                "CrossDriveDirectoryUnsupported" => StatusCodes.Status502BadGateway,
                _ => StatusCodes.Status500InternalServerError,
            };
        }
        catch (QuotaExceededException)
        {
            // RFC 4918 §9.7.3 / §9.8 — 507 Insufficient Storage covers quota-denied COPY too.
            // MOVE within-drive doesn't move quota; the catch is a no-op for MOVE in practice
            // but stays here for symmetry with the COPY path.
            context.Response.StatusCode = StatusCodes.Status507InsufficientStorage;
        }
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
        if (!context.User.HasScope(WebDavScopes.FilesWrite))
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
            context.Request.Headers[WebDavHeaderNames.Timeout],
            options.DefaultLockTimeoutSeconds,
            options.MaxLockTimeoutSeconds);

        // Refresh path: empty body + If header with the token. RFC 4918 §9.10.2 — no owner
        // element on the wire, we only bump ExpiresAt.
        var ifToken = WebDavIfHeader.ExtractFirstLockToken(context.Request.Headers[WebDavHeaderNames.If].ToString());
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
        if (!context.User.HasScope(WebDavScopes.FilesWrite))
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

        var token = WebDavIfHeader.ExtractLockTokenHeader(context.Request.Headers[WebDavHeaderNames.LockToken].ToString());
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
    // is the in-drive resource path (consumed by the WebDAV store).
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
