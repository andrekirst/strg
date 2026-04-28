using FluentValidation;

namespace Strg.Application.Features.Files.Move;

/// <summary>
/// Syntactic validation for <see cref="MoveFileCommand"/>. Rejects the <c>00000000-…</c>
/// pseudo-Guid that route binding accepts and an empty <c>TargetPath</c>. Path-shape semantics
/// (traversal, reserved names, null bytes) are checked inside the handler via
/// <c>StoragePath.Parse</c> — the validator only catches shape-level bugs that would otherwise
/// reach EF Core. Mirrors <c>DeleteFileValidator</c>'s split between syntactic guard here vs.
/// semantic validation in the handler.
/// </summary>
public sealed class MoveFileValidator : AbstractValidator<MoveFileCommand>
{
    public MoveFileValidator()
    {
        RuleFor(c => c.DriveId).NotEqual(Guid.Empty);
        RuleFor(c => c.FileId).NotEqual(Guid.Empty);
        RuleFor(c => c.TargetPath).NotEmpty();
    }
}
