using Mediator;
using Strg.Api.Auth;
using Strg.Application.Features.Drives.Create;
using Strg.Application.Features.Drives.Delete;
using Strg.Application.Features.Drives.Get;
using Strg.Application.Features.Drives.List;
using Strg.Core.Domain;

namespace Strg.Api.Endpoints;

/// <summary>
/// Drive management endpoints for the current tenant. A <c>Drive</c> is a named mount point
/// that binds a storage provider configuration; create/delete require the <c>admin</c> scope.
/// All four verbs dispatch through <see cref="IMediator"/> — business logic, tenant scoping,
/// and audit emission live in <c>Strg.Application.Features.Drives</c>.
/// </summary>
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

    /// <summary>
    /// Returns every non-deleted drive visible to the current tenant. Storage credentials
    /// (<c>ProviderConfig</c>) are stripped from the response.
    /// </summary>
    private static async Task<IResult> ListDrives(IMediator mediator, CancellationToken cancellationToken)
    {
        var drives = await mediator.Send(new ListDrivesQuery(), cancellationToken);
        var dtos = drives.Select(ToDto);
        return Results.Ok(dtos);
    }

    /// <summary>
    /// Returns a single drive by id, or 404 if the caller's tenant does not own it.
    /// </summary>
    private static async Task<IResult> GetDrive(Guid id, IMediator mediator, CancellationToken cancellationToken)
    {
        var drive = await mediator.Send(new GetDriveQuery(id), cancellationToken);
        return drive is null ? Results.NotFound() : Results.Ok(ToDto(drive));
    }

    /// <summary>
    /// Creates a new drive in the current tenant. Requires the <c>admin</c> scope.
    /// Returns 422 on invalid input and 409 if the drive name already exists (soft-deleted
    /// names count — names are reserved for the lifetime of the tenant).
    /// </summary>
    private static async Task<IResult> CreateDrive(
        CreateDriveCommand command,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(command, cancellationToken);
        if (result.IsSuccess)
        {
            var drive = result.Value!;
            return Results.Created($"/api/v1/drives/{drive.Id}", ToDto(drive));
        }
        return result.ErrorCode switch
        {
            "DuplicateName" => Results.Conflict(new { error = result.ErrorMessage }),
            _ => Results.UnprocessableEntity(new { error = result.ErrorMessage }),
        };
    }

    /// <summary>
    /// Soft-deletes a drive. The record is retained so its name remains reserved in the tenant.
    /// Requires the <c>admin</c> scope.
    /// </summary>
    private static async Task<IResult> DeleteDrive(Guid id, IMediator mediator, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new DeleteDriveCommand(id), cancellationToken);
        return result.IsSuccess ? Results.NoContent() : Results.NotFound();
    }

    private static DriveDto ToDto(Drive d) =>
        new(d.Id, d.Name, d.ProviderType, d.EncryptionEnabled, d.CreatedAt);
}

public record DriveDto(Guid Id, string Name, string ProviderType, bool EncryptionEnabled, DateTimeOffset CreatedAt);
