using FluentValidation;

namespace Strg.Application.Features.Folders.Create;

public sealed class CreateFolderValidator : AbstractValidator<CreateFolderCommand>
{
    public CreateFolderValidator()
    {
        RuleFor(c => c.Path).NotEmpty();
    }
}
