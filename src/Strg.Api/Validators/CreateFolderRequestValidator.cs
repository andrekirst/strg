using FluentValidation;
using Strg.Api.Endpoints;

namespace Strg.Api.Validators;

/// <summary>
/// STRG-085 — request-body validator for <see cref="CreateFolderRequest"/>. Runs as a minimal-API
/// endpoint filter (<see cref="ValidationProblemDetailsFilter{TRequest}"/>) BEFORE the mediator
/// dispatch; on failure the response is RFC 7807 <c>ValidationProblemDetails</c> (HTTP 400).
///
/// <para>Path traversal (<c>..</c>) is blocked here AND inside the handler via
/// <see cref="Strg.Core.Storage.StoragePath.Parse"/> — see the belt-and-suspenders rationale on
/// <see cref="ValidationProblemDetailsFilter{TRequest}"/>. The 4096-character cap mirrors the
/// issue spec and prevents oversized request bodies from reaching EF Core.</para>
/// </summary>
public sealed class CreateFolderRequestValidator : AbstractValidator<CreateFolderRequest>
{
    public CreateFolderRequestValidator()
    {
        RuleFor(x => x.Path)
            .NotEmpty()
            .WithMessage("Path is required.")
            .MaximumLength(4096)
            .Must(p => p is null || !p.Contains("..", StringComparison.Ordinal))
            .WithMessage("Path must not contain '..'.");
    }
}
