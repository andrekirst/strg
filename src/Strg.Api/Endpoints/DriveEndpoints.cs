using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Strg.Api.Auth;
using Strg.Core.Domain;
using Strg.Core.Identity;
using Strg.Core.Storage;
using Strg.Infrastructure.Data;
using System.Security.Claims;

namespace Strg.Api.Endpoints;

public static class DriveEndpoints
{
    public static IEndpointRouteBuilder MapDriveEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/drives").RequireAuthorization();

        group.MapGet("/", ListDrives);
        group.MapGet("/{id:guid}", GetDrive);
        group.MapPost("/", CreateDrive).RequireAuthorization(AuthPolicies.Admin);
        group.MapDelete("/{id:guid}", DeleteDrive).RequireAuthorization(AuthPolicies.Admin);

        return app;
    }

    private static async Task<IResult> ListDrives(
        StrgDbContext db,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var tenantId = user.GetTenantId();
        var drives = await db.Drives.ToListAsync(cancellationToken);
        // Return drives without ProviderConfig — never expose storage credentials to clients
        var dtos = drives.Select(d => new DriveDto(d.Id, d.Name, d.ProviderType, d.EncryptionEnabled, d.CreatedAt));
        return Results.Ok(dtos);
    }

    private static async Task<IResult> GetDrive(
        Guid id,
        StrgDbContext db,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var drive = await db.Drives.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (drive is null)
        {
            return Results.NotFound();
        }

        return Results.Ok(new DriveDto(drive.Id, drive.Name, drive.ProviderType, drive.EncryptionEnabled, drive.CreatedAt));
    }

    private static async Task<IResult> CreateDrive(
        [FromBody] CreateDriveRequest request,
        StrgDbContext db,
        ClaimsPrincipal user,
        IStorageProviderRegistry registry,
        CancellationToken cancellationToken)
    {
        var tenantId = user.GetTenantId();

        // Validate name: lowercase alphanumeric + hyphens only, max 64 chars
        if (!System.Text.RegularExpressions.Regex.IsMatch(request.Name, @"^[a-z0-9\-]{1,64}$"))
        {
            return Results.UnprocessableEntity(new { error = "Drive name must be lowercase alphanumeric with hyphens, max 64 chars" });
        }

        // Validate provider type
        if (!registry.IsRegistered(request.ProviderType))
        {
            return Results.UnprocessableEntity(new { error = $"Unknown provider type: {request.ProviderType}" });
        }

        // Reject oversized ProviderConfig before it hits the DB — cheaper error, no tx rollback.
        // DB column is capped at varchar(8192) as defense-in-depth backstop.
        if ((request.ProviderConfigJson?.Length ?? 0) > 8192)
        {
            return Results.UnprocessableEntity(new { error = "ProviderConfig JSON cannot exceed 8192 characters" });
        }

        // Check name uniqueness — bypass global filter to also check soft-deleted names,
        // preventing re-use of a deleted drive name within the same tenant.
        var existing = await db.Drives.IgnoreQueryFilters()
            .AnyAsync(d => d.TenantId == tenantId && d.Name == request.Name && !d.IsDeleted, cancellationToken);
        if (existing)
        {
            return Results.Conflict(new { error = $"Drive '{request.Name}' already exists" });
        }

        var drive = new Drive
        {
            TenantId = tenantId,
            Name = request.Name,
            ProviderType = request.ProviderType,
            ProviderConfig = request.ProviderConfigJson ?? "{}",
            EncryptionEnabled = request.EncryptionEnabled
        };
        db.Drives.Add(drive);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/v1/drives/{drive.Id}", new DriveDto(drive.Id, drive.Name, drive.ProviderType, drive.EncryptionEnabled, drive.CreatedAt));
    }

    private static async Task<IResult> DeleteDrive(
        Guid id,
        StrgDbContext db,
        CancellationToken cancellationToken)
    {
        var drive = await db.Drives.FindAsync([id], cancellationToken);
        if (drive is null)
        {
            return Results.NotFound();
        }

        drive.DeletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }
}

public record DriveDto(Guid Id, string Name, string ProviderType, bool EncryptionEnabled, DateTimeOffset CreatedAt);

public record CreateDriveRequest(
    string Name,
    string ProviderType,
    string? ProviderConfigJson = null,
    bool EncryptionEnabled = false);
