using FluentValidation;

namespace Strg.Application.Features.Files.Copy;

/// <summary>
/// Syntactic validation for <see cref="CopyFileCommand"/>. Mirrors
/// <see cref="Move.MoveFileValidator"/>'s shape — rejects empty Guids and an empty
/// <c>TargetPath</c>. Path-shape semantics (traversal, reserved names, null bytes) are checked
/// inside the handler via <c>StoragePath.Parse</c>; this validator only catches shape-level bugs
/// that would otherwise reach EF Core.
/// </summary>
public sealed class CopyFileValidator : AbstractValidator<CopyFileCommand>
{
    public CopyFileValidator()
    {
        RuleFor(c => c.DriveId).NotEqual(Guid.Empty);
        RuleFor(c => c.FileId).NotEqual(Guid.Empty);
        RuleFor(c => c.TargetPath).NotEmpty();
    }
}
