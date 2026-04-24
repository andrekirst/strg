using FluentValidation;

namespace Strg.Application.Features.Drives.Create;

public sealed class CreateDriveValidator : AbstractValidator<CreateDriveCommand>
{
    public CreateDriveValidator()
    {
        RuleFor(c => c.Name)
            .NotEmpty()
            .Matches(@"^[a-z0-9][a-z0-9-]{0,63}$")
            .WithMessage(
                "Drive name must start with an alphanumeric and may contain only lowercase letters, digits, and hyphens (max 64 chars).");
        RuleFor(c => c.ProviderType).NotEmpty();
        RuleFor(c => c.ProviderConfigJson)
            .Must(json => json is null || json.Length <= 8192)
            .WithMessage("ProviderConfig JSON cannot exceed 8192 characters.");
    }
}
