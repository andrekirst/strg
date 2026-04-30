using FluentValidation;

namespace Strg.Application.Features.Users.UpdateProfile;

/// <summary>
/// Syntactic validation for <see cref="UpdateProfileCommand"/>. The display name must be
/// non-empty and non-whitespace; the <c>User</c> entity carries no further length constraint,
/// so the validator deliberately does not impose one. When the validator fails, the validation
/// pipeline behavior short-circuits with <c>Result&lt;User&gt;.Failure("ValidationError", ...)</c>,
/// which the REST endpoint maps to HTTP 400.
/// </summary>
public sealed class UpdateProfileValidator : AbstractValidator<UpdateProfileCommand>
{
    public UpdateProfileValidator()
    {
        RuleFor(c => c.DisplayName)
            .NotEmpty()
            .Must(n => !string.IsNullOrWhiteSpace(n))
            .WithMessage("Display name must not be whitespace.");
    }
}
