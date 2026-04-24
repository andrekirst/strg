using FluentValidation;

namespace Strg.Application.Features.Drives.Update;

public sealed class UpdateDriveValidator : AbstractValidator<UpdateDriveCommand>
{
    public UpdateDriveValidator()
    {
        RuleFor(c => c.Name!)
            .Matches(@"^[a-z0-9][a-z0-9-]{0,63}$")
            .When(c => c.Name is not null)
            .WithMessage(
                "Drive name must start with an alphanumeric and may contain only lowercase letters, digits, and hyphens (max 64 chars).");
    }
}
