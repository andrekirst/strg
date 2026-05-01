using Microsoft.Extensions.Options;
using Strg.Core.Services;

namespace Strg.Infrastructure.Thumbnails;

/// <summary>
/// Fail-fast validator for <see cref="ThumbnailOptions"/>. Wired via
/// <c>services.AddOptions&lt;ThumbnailOptions&gt;().ValidateOnStart()</c> so misconfiguration
/// crashes the host at boot rather than at first thumbnail generation.
/// </summary>
internal sealed class ThumbnailOptionsValidator : IValidateOptions<ThumbnailOptions>
{
    public ValidateOptionsResult Validate(string? name, ThumbnailOptions options)
    {
        var failures = new List<string>();

        if (options.Variants is null || options.Variants.Count == 0)
        {
            failures.Add("Thumbnails:Variants must be non-empty.");
        }
        else
        {
            foreach (var variant in options.Variants)
            {
                if (!ThumbnailVariants.IsKnown(variant))
                {
                    failures.Add($"Thumbnails:Variants contains unknown variant '{variant}'. Allowed: thumb, small, medium.");
                }
            }
        }

        if (options.MaxSourceSizeBytes <= 0)
        {
            failures.Add($"Thumbnails:MaxSourceSizeBytes must be > 0 (got {options.MaxSourceSizeBytes}).");
        }
        if (options.MaxPixelArea <= 0)
        {
            failures.Add($"Thumbnails:MaxPixelArea must be > 0 (got {options.MaxPixelArea}).");
        }
        if (options.GenerationTimeoutSeconds <= 0 || options.GenerationTimeoutSeconds > 600)
        {
            failures.Add($"Thumbnails:GenerationTimeoutSeconds must be in (0, 600] (got {options.GenerationTimeoutSeconds}).");
        }
        if (options.WebPQuality < 1 || options.WebPQuality > 100)
        {
            failures.Add($"Thumbnails:WebPQuality must be in [1, 100] (got {options.WebPQuality}).");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
