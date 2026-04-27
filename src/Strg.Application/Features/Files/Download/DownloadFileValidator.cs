using FluentValidation;

namespace Strg.Application.Features.Files.Download;

/// <summary>
/// Syntactic validation for <see cref="DownloadFileCommand"/>. Rejects malformed input before
/// the handler runs: empty Guids (route binding accepts <c>00000000-…</c>), negative range
/// bounds, and ranges where <c>From &gt; To</c>.
///
/// <para><b>What this does NOT validate</b> — semantics that depend on DB state (drive
/// existence, file existence, directory check, range satisfiability against file size) live
/// in the handler. Validators run before the DB is touched, so they can only catch
/// shape-level bugs.</para>
/// </summary>
public sealed class DownloadFileValidator : AbstractValidator<DownloadFileCommand>
{
    public DownloadFileValidator()
    {
        RuleFor(c => c.DriveId).NotEqual(Guid.Empty);
        RuleFor(c => c.FileId).NotEqual(Guid.Empty);

        When(c => c.Range is not null, () =>
        {
            RuleFor(c => c.Range!.From)
                .Must(from => from is null or >= 0)
                .WithMessage("Range.From must be non-negative when supplied.");

            RuleFor(c => c.Range!.To)
                .Must(to => to is null or >= 0)
                .WithMessage("Range.To must be non-negative when supplied.");

            RuleFor(c => c.Range!)
                .Must(r => !(r.From.HasValue && r.To.HasValue) || r.From!.Value <= r.To!.Value)
                .WithMessage("Range.From must not exceed Range.To when both are supplied.");

            RuleFor(c => c.Range!)
                .Must(r => r.From.HasValue || r.To.HasValue)
                .WithMessage("At least one of Range.From or Range.To must be supplied.");
        });
    }
}
