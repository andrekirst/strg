using FluentValidation;

namespace Strg.Application.Features.Drives.SetDefault;

public sealed class SetDefaultDriveValidator : AbstractValidator<SetDefaultDriveCommand>
{
    public SetDefaultDriveValidator()
    {
        RuleFor(c => c.DriveId).NotEmpty();
    }
}
