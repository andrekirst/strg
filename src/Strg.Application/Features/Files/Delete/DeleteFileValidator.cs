using FluentValidation;

namespace Strg.Application.Features.Files.Delete;

/// <summary>
/// Syntactic validation for <see cref="DeleteFileCommand"/>. Rejects the
/// <c>00000000-…</c> pseudo-Guid that route binding accepts. Semantic validation (drive
/// ownership, file-on-other-drive collapse to 404) lives in the handler — the validator only
/// catches shape-level bugs before the DB is touched.
/// </summary>
public sealed class DeleteFileValidator : AbstractValidator<DeleteFileCommand>
{
    public DeleteFileValidator()
    {
        RuleFor(c => c.DriveId).NotEqual(Guid.Empty);
        RuleFor(c => c.FileId).NotEqual(Guid.Empty);
    }
}
