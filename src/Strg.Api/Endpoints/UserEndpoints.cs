using Mediator;
using Strg.Api.Auth;
using Strg.Application.Features.Users.GetCurrentUser;
using Strg.Application.Features.Users.GetUserById;
using Strg.Application.Features.Users.List;
using Strg.Application.Features.Users.Lock;
using Strg.Application.Features.Users.Unlock;
using Strg.Application.Features.Users.UpdateProfile;
using Strg.Application.Features.Users.UpdateQuota;
using Strg.Core.Domain;

namespace Strg.Api.Endpoints;

/// <summary>
/// STRG-059 — REST surface for user-profile self-service and admin user management. Complements
/// the GraphQL user mutations (STRG-054); REST is the canonical interface for tooling and
/// automation, GraphQL is for interactive clients.
///
/// <para><b>Routing layout.</b> The group's default authorization is the fallback policy
/// (<c>RequireAuthenticatedUser</c>) so <c>/me</c> works for any authenticated user. Admin-only
/// routes individually opt in to <see cref="AuthPolicies.Admin"/>, which requires the
/// <c>admin</c> scope on the access token. A scope-deficient call is rejected with HTTP 403 by
/// ASP.NET Core's authorization middleware before the endpoint runs — same enforcement model
/// as <c>DriveEndpoints</c>.</para>
///
/// <para><b>Tenant isolation</b> is the global query filter on <c>StrgDbContext.Users</c>.
/// Every handler in <c>Strg.Application.Features.Users</c> reads through
/// <c>IStrgDbContext.Users</c> without an explicit tenant predicate; cross-tenant ids collapse
/// to <see langword="null"/> → HTTP 404, which doubles as anti-enumeration: the wire response
/// cannot distinguish "user does not exist" from "user exists but in another tenant".</para>
///
/// <para><b><see cref="UserDto"/> never carries the password hash.</b> The projection is a
/// positional record with explicit fields — adding <c>PasswordHash</c> would require a
/// deliberate code change visible in review. <see cref="User.LockedUntil"/> is also
/// deliberately omitted; the computed <see cref="UserDto.IsLocked"/> boolean is the only lock
/// state the wire exposes (per the issue's Security Review checklist).</para>
/// </summary>
public static class UserEndpoints
{
    private const int MaxPageSize = 200;
    private const int DefaultPageSize = 50;

    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/users").RequireAuthorization();

        group.MapGet("/me", GetCurrentUserAsync)
            .WithName("GetCurrentUser")
            .WithTags("Users")
            .WithSummary("Return the current authenticated user's profile.");

        group.MapPut("/me", UpdateCurrentProfileAsync)
            .WithName("UpdateCurrentProfile")
            .WithTags("Users")
            .WithSummary("Update the current user's display name.");

        group.MapGet("/", ListUsersAsync)
            .RequireAuthorization(AuthPolicies.Admin)
            .WithName("ListUsers")
            .WithTags("Users")
            .WithSummary("Paginated list of every user in the current tenant. Admin only.");

        group.MapGet("/{userId:guid}", GetUserByIdAsync)
            .RequireAuthorization(AuthPolicies.Admin)
            .WithName("GetUserById")
            .WithTags("Users")
            .WithSummary("Return a single user by id. Admin only.");

        group.MapPut("/{userId:guid}/quota", UpdateUserQuotaAsync)
            .RequireAuthorization(AuthPolicies.Admin)
            .WithName("UpdateUserQuota")
            .WithTags("Users")
            .WithSummary("Update a user's quota in bytes. Admin only.");

        group.MapPost("/{userId:guid}/lock", LockUserAsync)
            .RequireAuthorization(AuthPolicies.Admin)
            .WithName("LockUser")
            .WithTags("Users")
            .WithSummary("Lock a user account. Admin only.");

        group.MapDelete("/{userId:guid}/lock", UnlockUserAsync)
            .RequireAuthorization(AuthPolicies.Admin)
            .WithName("UnlockUser")
            .WithTags("Users")
            .WithSummary("Unlock a user account. Admin only.");

        return app;
    }

    private static async Task<IResult> GetCurrentUserAsync(
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var user = await mediator.Send(new GetCurrentUserQuery(), cancellationToken).ConfigureAwait(false);
        return user is null ? Results.NotFound() : Results.Ok(UserDto.From(user));
    }

    private static async Task<IResult> UpdateCurrentProfileAsync(
        UpdateProfileRequest request,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var command = new UpdateProfileCommand(request.DisplayName);
        var result = await mediator.Send(command, cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            return Results.Ok(UserDto.From(result.Value!));
        }

        return result.ErrorCode switch
        {
            "NotFound" => Results.NotFound(),
            "ValidationError" => Results.BadRequest(new { code = result.ErrorCode, message = result.ErrorMessage }),
            _ => Results.Problem(statusCode: 500, detail: result.ErrorMessage ?? "Profile update failed."),
        };
    }

    private static async Task<IResult> ListUsersAsync(
        IMediator mediator,
        CancellationToken cancellationToken,
        int page = 1,
        int pageSize = DefaultPageSize)
    {
        // Cap pageSize at the wire layer so a 999-item request returns 200 even on the routing
        // layer's log surface. The handler re-applies the same clamp as defence-in-depth.
        var cappedPageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        var clampedPage = Math.Max(page, 1);

        var result = await mediator.Send(new ListUsersQuery(clampedPage, cappedPageSize), cancellationToken)
            .ConfigureAwait(false);
        var items = result.Items.Select(UserDto.From).ToArray();
        return Results.Ok(new UserListResponse(items, result.Page, result.PageSize, result.TotalCount));
    }

    private static async Task<IResult> GetUserByIdAsync(
        Guid userId,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var user = await mediator.Send(new GetUserByIdQuery(userId), cancellationToken).ConfigureAwait(false);
        return user is null ? Results.NotFound() : Results.Ok(UserDto.From(user));
    }

    private static async Task<IResult> UpdateUserQuotaAsync(
        Guid userId,
        UpdateQuotaRequest request,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var command = new UpdateUserQuotaCommand(userId, request.QuotaBytes);
        var result = await mediator.Send(command, cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            return Results.Ok(UserDto.From(result.Value!));
        }

        return result.ErrorCode switch
        {
            "NotFound" => Results.NotFound(),
            "ValidationError" => Results.BadRequest(new { code = result.ErrorCode, message = result.ErrorMessage }),
            _ => Results.Problem(statusCode: 500, detail: result.ErrorMessage ?? "Quota update failed."),
        };
    }

    private static async Task<IResult> LockUserAsync(
        Guid userId,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var user = await mediator.Send(new LockUserCommand(userId), cancellationToken).ConfigureAwait(false);
        return user is null ? Results.NotFound() : Results.Ok(UserDto.From(user));
    }

    private static async Task<IResult> UnlockUserAsync(
        Guid userId,
        IMediator mediator,
        CancellationToken cancellationToken)
    {
        var user = await mediator.Send(new UnlockUserCommand(userId), cancellationToken).ConfigureAwait(false);
        return user is null ? Results.NotFound() : Results.Ok(UserDto.From(user));
    }
}

/// <summary>
/// Wire-shape projection of <see cref="User"/>. Deliberately omits <see cref="User.PasswordHash"/>
/// (security boundary — never on the wire) and the raw <see cref="User.LockedUntil"/> timestamp
/// (the computed <see cref="IsLocked"/> boolean is the only lock state the wire exposes — per
/// the STRG-059 Security Review checklist). <see cref="From(User)"/> uses
/// <c>u.Role.ToString()</c> to surface the enum as a stable string ("User", "Admin",
/// "SuperAdmin", "Readonly"); the issue spec body literally says <c>u.UserRole.ToString()</c>,
/// but the entity property is <see cref="User.Role"/> — code matches the entity, not the spec
/// typo.
/// </summary>
public record UserDto(
    Guid Id,
    string Email,
    string DisplayName,
    string Role,
    long QuotaBytes,
    long UsedBytes,
    long FreeBytes,
    double UsagePercent,
    bool IsLocked,
    DateTimeOffset CreatedAt)
{
    public static UserDto From(User u) => new(
        u.Id,
        u.Email,
        u.DisplayName,
        u.Role.ToString(),
        u.QuotaBytes,
        u.UsedBytes,
        u.FreeBytes,
        u.UsagePercent,
        u.IsLocked,
        u.CreatedAt);
}

/// <summary>Paged response for <c>GET /api/v1/users</c>.</summary>
public record UserListResponse(
    IReadOnlyList<UserDto> Items,
    int Page,
    int PageSize,
    int TotalCount);

/// <summary>Body of <c>PUT /api/v1/users/me</c>.</summary>
public sealed record UpdateProfileRequest(string DisplayName);

/// <summary>Body of <c>PUT /api/v1/users/{userId}/quota</c>.</summary>
public sealed record UpdateQuotaRequest(long QuotaBytes);
