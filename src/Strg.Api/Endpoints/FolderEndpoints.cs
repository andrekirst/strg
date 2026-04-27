using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Strg.Api.Auth;
using Strg.Core.Domain;
using Strg.Core.Identity;
using Strg.Core.Storage;
using Strg.Infrastructure.Data;

namespace Strg.Api.Endpoints;

/// <summary>
/// STRG-042 — REST endpoint that creates a virtual folder hierarchy in a drive. The endpoint is
/// pure metadata: it never asks the storage backend to materialise a directory because
/// <c>IStorageProvider</c> in strg works against virtual paths only — directories are
/// <see cref="FileItem"/> rows with <see cref="FileItem.IsDirectory"/>=<see langword="true"/>,
/// not OS-level directories.
///
/// <para><b>Auto-parent semantics.</b> A request for <c>"docs/2024/reports"</c> walks every
/// path segment in order. For each segment, the handler queries
/// <see cref="StrgDbContext.Files"/> by (driveId, currentPath). If the row is missing, the
/// handler inserts a new directory row with <see cref="FileItem.ParentId"/> pointing at the
/// previous segment's row, then commits. The per-segment commit is deliberate — without it the
/// next segment's <see cref="FileItem.ParentId"/> would reference a transient
/// <see cref="Guid.Empty"/> until the entire chain saves. Per the issue's Code Review Checklist,
/// this is exactly the contract: "SaveChangesAsync called once per segment (to get IDs for
/// ParentId)". The endpoint <i>is</i> the caller in CLAUDE.md's "caller commits" rule, and the
/// repository pattern is preserved (we route through <see cref="StrgDbContext"/> directly here
/// because per-segment id-resolution is the reason the rule exempts the caller).</para>
///
/// <para><b>Idempotency.</b> A re-request for an existing folder returns 200 with the existing
/// row, never 409. The 409 path fires only when a path segment <i>collides with a non-directory
/// file</i>: <c>"file.txt/subdir"</c> when <c>"file.txt"</c> exists as a file. Re-creating a
/// folder is a safe no-op for clients that keep retrying after a transient failure.</para>
///
/// <para><b>Tenant isolation</b> rides on the global query filter on <see cref="StrgDbContext"/>'s
/// file set — the endpoint cannot bypass it. <c>TenantId</c> on every new row is pinned from
/// the JWT <c>tenant_id</c> claim, never from the request body. Cross-tenant request for
/// someone else's drive collapses to 404 because the tenant filter excludes the drive row.</para>
///
/// <para><b>Path safety</b> is enforced by <see cref="StoragePath.Parse"/>: traversal
/// (<c>"../etc"</c>), null bytes, encoded variants, Windows-reserved names — all surface as
/// <see cref="StoragePathException"/>, which the endpoint catches and translates to a 400. The
/// <c>Path</c> on every persisted <see cref="FileItem"/> row is therefore guaranteed normalised.
/// </para>
/// </summary>
public static class FolderEndpoints
{
    public static IEndpointRouteBuilder MapFolderEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/drives/{driveId:guid}/folders", CreateFolderAsync)
            .RequireAuthorization(AuthPolicies.FilesWrite)
            .WithName("CreateFolder")
            .WithTags("Folders")
            .WithSummary("Create a virtual folder (and any missing parent segments) in a drive.")
            .WithDescription(
                "Creates the folder at the given path, auto-creating any missing parent path " +
                "segments as virtual directory rows. Returns 200 OK with the leaf folder; the " +
                "endpoint is idempotent — re-requesting an existing folder yields 200 OK and no " +
                "duplicate row. Returns 409 Conflict when a path segment collides with an " +
                "existing non-directory file. Returns 400 Bad Request when StoragePath.Parse " +
                "rejects the input (traversal, null bytes, reserved names). No physical " +
                "directory is created in the storage backend — strg uses virtual paths only.");

        return app;
    }

    private static async Task<IResult> CreateFolderAsync(
        Guid driveId,
        CreateFolderRequest request,
        StrgDbContext db,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        // Path validation BEFORE the drive existence check. A malformed path is the cheapest
        // failure mode (no DB round-trip) and StoragePath.Parse is the project-wide gate against
        // traversal/null-byte/reserved-name attacks. The catch surfaces as 400 because there is
        // no central RFC 7807 mapping for StoragePathException on the REST surface (the GraphQL
        // surface routes through StrgErrorFilter; REST endpoints translate inline).
        StoragePath path;
        try
        {
            path = StoragePath.Parse(request.Path);
        }
        catch (StoragePathException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        // Drive existence check rides the tenant filter on db.Drives — a drive owned by a
        // different tenant collapses to 404, never 403, to prevent cross-tenant enumeration.
        // Mirrors the FileListEndpoints "UnknownDrive_Returns404" contract.
        var driveExists = await db.Drives
            .AnyAsync(d => d.Id == driveId, cancellationToken)
            .ConfigureAwait(false);
        if (!driveExists)
        {
            return Results.NotFound();
        }

        var tenantId = user.GetTenantId();
        var userId = user.GetUserId();

        // Split on '/' against the normalised path. StoragePath.Normalize trims leading/trailing
        // separators and collapses '\\' to '/', so a single split here yields exactly the
        // segments — no empty entries to filter (Normalize already rejects "//" via
        // ContainsTraversal). The struct's lack of a .Segments property is the reason for the
        // inline split; we'd rather not promote the convenience accessor onto StoragePath until
        // a second consumer needs it (folder creation is the first).
        var segments = path.Value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var currentPath = string.Empty;
        FileItem? parent = null;

        foreach (var segment in segments)
        {
            currentPath = currentPath.Length == 0 ? segment : $"{currentPath}/{segment}";

            // Per-segment lookup — the unique (DriveId, Path) index makes this an index seek.
            // The tenant + soft-delete filters on db.Files apply, so a row from another tenant
            // (or a soft-deleted directory at the same path) is invisible here. That is the
            // intended semantic: a re-created folder after a soft-delete is a fresh row.
            var existing = await db.Files
                .FirstOrDefaultAsync(f => f.DriveId == driveId && f.Path == currentPath, cancellationToken)
                .ConfigureAwait(false);

            if (existing is null)
            {
                var dir = new FileItem
                {
                    TenantId = tenantId,
                    DriveId = driveId,
                    ParentId = parent?.Id,
                    Name = segment,
                    Path = currentPath,
                    IsDirectory = true,
                    Size = 0,
                    CreatedBy = userId,
                };
                db.Files.Add(dir);

                // Per-segment SaveChangesAsync. Without this, dir.Id is Guid.Empty when the next
                // iteration reads it as parent.Id — every newly-inserted child would point at a
                // null parent. The issue's Code Review Checklist explicitly pins this contract.
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                parent = dir;
            }
            else if (!existing.IsDirectory)
            {
                // 409 ONLY when the existing row is a file. A pre-existing directory is the
                // idempotent path — the loop continues with the existing row as the new parent.
                return Results.Conflict(new
                {
                    error = $"Path segment '{currentPath}' exists as a file.",
                });
            }
            else
            {
                parent = existing;
            }
        }

        // parent is guaranteed non-null because StoragePath.Parse rejects empty/whitespace input;
        // segments.Length is therefore at least 1, and the loop body always assigns parent.
        return Results.Ok(FileItemDto.From(parent!));
    }
}
