using FluentValidation;

namespace Strg.Application.Features.Folders.Create;

/// <summary>
/// Syntactic validation for <see cref="CreateFolderCommand"/>. Mirrors
/// <c>MoveFileValidator</c>'s split between syntactic guard here vs. semantic validation in the
/// handler — path-shape semantics (traversal, reserved names, null bytes) are checked inside
/// <c>CreateFolderHandler</c> via <see cref="Strg.Core.Storage.StoragePath.Parse"/>, this
/// validator only catches shape-level bugs that would otherwise reach EF Core.
/// </summary>
public sealed class CreateFolderValidator : AbstractValidator<CreateFolderCommand>
{
    public CreateFolderValidator()
    {
        RuleFor(c => c.DriveId).NotEqual(Guid.Empty);
        RuleFor(c => c.Path).NotEmpty();
    }
}
