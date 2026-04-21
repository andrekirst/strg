using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Strg.Core.Domain;
using Strg.Core.Identity;

namespace Strg.Api.Endpoints;

/// <summary>
/// Public self-registration endpoint (STRG-086). Enabled by default in v0.1; quota is the
/// abuse guard rather than rate limiting, per Phase 10 design decision.
///
/// <para>
/// Anti-enumeration hardening: the endpoint ALWAYS returns <c>204 No Content</c>, regardless of
/// whether the request succeeded, failed validation, or collided with an existing email. A
/// response code that discriminates between "duplicate email" and "new email" hands an attacker
/// a free email-existence oracle — 204-on-everything closes that oracle. The real outcome is
/// logged server-side so operators retain visibility.
/// </para>
/// </summary>
public static class UserRegistrationEndpoints
{
    public static IEndpointRouteBuilder MapUserRegistrationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/users/register", RegisterUserAsync)
            .AllowAnonymous()
            .WithName("RegisterUser");

        return app;
    }

    private static async Task<IResult> RegisterUserAsync(
        [FromBody] RegisterUserRequest request,
        IValidator<RegisterUserRequest> validator,
        ITenantRepository tenants,
        IUserManager userManager,
        ILogger<RegisterUserRequestLogCategory> logger,
        CancellationToken cancellationToken)
    {
        var validation = await validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            // Log the specific failure for ops visibility, but do not surface it to the caller.
            logger.LogInformation(
                "Self-registration rejected (validation): {Errors}",
                string.Join("; ", validation.Errors.Select(e => $"{e.PropertyName}:{e.ErrorCode}")));
            return Results.NoContent();
        }

        var tenant = await tenants.GetByNameAsync(DefaultTenantName, cancellationToken);
        if (tenant is null)
        {
            // FirstRunInitializationService seeds the default tenant at host start. Reaching
            // this branch means the seed worker hasn't completed (or was skipped in a broken
            // deployment) — log loudly but still return 204 to avoid leaking state.
            logger.LogError(
                "Self-registration cannot proceed: default tenant '{TenantName}' not found",
                DefaultTenantName);
            return Results.NoContent();
        }

        var result = await userManager.CreateUserAsync(
            new CreateUserRequest(tenant.Id, request.Email, request.DisplayName, request.Password),
            cancellationToken);

        if (result.IsSuccess)
        {
            logger.LogInformation(
                "Self-registration succeeded for user {UserId} in tenant {TenantId}",
                result.Value!.Id,
                tenant.Id);
        }
        else
        {
            logger.LogInformation(
                "Self-registration rejected ({ErrorCode}) in tenant {TenantId}",
                result.ErrorCode,
                tenant.Id);
        }

        return Results.NoContent();
    }

    private const string DefaultTenantName = "default";

    /// <summary>
    /// Marker type for <see cref="ILogger{TCategoryName}"/> so registration log events group
    /// under a stable category name without requiring a real service class.
    /// </summary>
    public sealed class RegisterUserRequestLogCategory;
}

public sealed record RegisterUserRequest(string Email, string DisplayName, string Password);

public sealed class RegisterUserRequestValidator : AbstractValidator<RegisterUserRequest>
{
    public RegisterUserRequestValidator()
    {
        RuleFor(r => r.Email)
            .NotEmpty()
            .EmailAddress()
            .MaximumLength(256);

        RuleFor(r => r.DisplayName)
            .NotEmpty()
            .Must(n => !string.IsNullOrWhiteSpace(n))
            .WithMessage("Display name must not be whitespace.")
            .MaximumLength(200);

        // Mirror UserManager's floor so validation rejects short passwords with a clean log
        // line; without this, the manager returns PasswordTooShort which would still land as
        // a 204 but muddles the ops telemetry.
        RuleFor(r => r.Password)
            .NotEmpty()
            .MinimumLength(UserManagerErrors.MinimumPasswordLength);
    }
}
