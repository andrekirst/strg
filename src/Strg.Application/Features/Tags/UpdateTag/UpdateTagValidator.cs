using FluentValidation;

namespace Strg.Application.Features.Tags.UpdateTag;

public sealed class UpdateTagValidator : AbstractValidator<UpdateTagCommand>
{
    public UpdateTagValidator()
    {
        RuleFor(c => c.Value)
            .NotNull()
            .MaximumLength(255).WithMessage("Tag value must not exceed 255 characters.");
    }
}
