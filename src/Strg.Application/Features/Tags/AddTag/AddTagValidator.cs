using FluentValidation;

namespace Strg.Application.Features.Tags.AddTag;

public sealed class AddTagValidator : AbstractValidator<AddTagCommand>
{
    public AddTagValidator()
    {
        RuleFor(c => c.Key)
            .NotEmpty().WithMessage("Tag key must not be empty.")
            .MaximumLength(255).WithMessage("Tag key must not exceed 255 characters.")
            .Matches(@"^[a-zA-Z0-9._-]+$")
            .WithMessage("Tag key may contain only letters, digits, '.', '-', and '_'.");
        RuleFor(c => c.Value)
            .NotNull()
            .MaximumLength(255).WithMessage("Tag value must not exceed 255 characters.");
    }
}
