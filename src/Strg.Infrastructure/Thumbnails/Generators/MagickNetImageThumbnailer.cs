using System.Diagnostics;
using ImageMagick;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Strg.Core.Services;
using Strg.Infrastructure.Observability;

namespace Strg.Infrastructure.Thumbnails.Generators;

/// <summary>
/// Magick.NET-backed image thumbnailer (STRG-336/337/338). Handles JPEG/PNG/WebP/GIF/BMP/TIFF
/// + HEIC/HEIF (libheif system dep), outputs WebP at the configured quality, letterboxes to a
/// square white canvas. Pixel-cap probe via <see cref="MagickImageInfo"/> BEFORE full decode.
/// EXIF auto-orient + metadata strip applied to the output.
///
/// <para><b>Resource safeguards (D14).</b> Three layered checks:
/// <list type="number">
///   <item>Source-size cap — checked in the consumer BEFORE this generator runs.</item>
///   <item>Pixel-area cap — header-only probe via <see cref="MagickImageInfo"/>; rejects bombs without decoding the body.</item>
///   <item>Per-call timeout — linked <see cref="CancellationTokenSource"/> with the operator-tunable budget.</item>
/// </list>
/// </para>
///
/// <para><b>Privacy (STRG-337).</b> <c>Strip()</c> runs unconditionally before <c>Write()</c> so
/// the output WebP carries no EXIF/XMP/IPTC/ICC. AI auto-tagging (future) reads the unmodified
/// source on a separate consumer path; this generator does not affect that.</para>
/// </summary>
public sealed class MagickNetImageThumbnailer(
    IOptions<ThumbnailOptions> options,
    StrgMetrics metrics,
    ILogger<MagickNetImageThumbnailer> logger) : IThumbnailGenerator
{
    private const string GeneratorVersion = "magick-net-q8/v1";

    // Closed whitelist. CanHandle returns false for anything else so the registry's
    // first-match-wins routing degrades to "no generator → write Unsupported{no-generator}".
    private static readonly HashSet<string> SupportedMimes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/png",
        "image/webp",
        "image/gif",
        "image/bmp",
        "image/tiff",
        "image/heic",
        "image/heif",
    };

    public string Version => GeneratorVersion;

    public bool CanHandle(string mimeType, ReadOnlySpan<byte> magicBytes) =>
        !string.IsNullOrEmpty(mimeType) && SupportedMimes.Contains(mimeType);

    public async Task<ThumbnailGenerationOutcome> GenerateAsync(
        Stream source,
        ThumbnailRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);

        var optionsValue = options.Value;
        metrics.ThumbnailsInflight.Add(1);
        var sw = Stopwatch.StartNew();

        using var timeoutCts = new CancellationTokenSource(
            TimeSpan.FromSeconds(optionsValue.GenerationTimeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutCts.Token);

        try
        {
            // Read the entire source into a MemoryStream. Magick.NET's MagickImageInfo + MagickImage
            // both need seekable access — the libraries' streaming variants are unreliable across
            // formats (HEIC in particular requires a fully buffered input on libheif's side). The
            // SourceSizeBytes cap (STRG-338) bounds memory at MaxSourceSizeBytes (default 256 MiB),
            // which the consumer enforces BEFORE invoking this generator.
            await using var bufferedSource = new MemoryStream(
                capacity: (int)Math.Min(request.SourceSizeBytes, int.MaxValue));
            await source.CopyToAsync(bufferedSource, linkedCts.Token).ConfigureAwait(false);
            bufferedSource.Position = 0;

            // Pixel-cap probe BEFORE full decode (D14). MagickImageInfo reads only the header.
            MagickImageInfo info;
            try
            {
                info = new MagickImageInfo(bufferedSource);
            }
            catch (MagickException ex)
            {
                logger.LogDebug("Header-parse failure: {Type}", ex.GetType().Name);
                return new ThumbnailGenerationOutcome.SourceCorrupt($"header-parse: {ex.GetType().Name}");
            }

            var pixelArea = (long)info.Width * info.Height;
            if (pixelArea > optionsValue.MaxPixelArea)
            {
                return new ThumbnailGenerationOutcome.ResourceLimitExceeded(
                    $"pixel-cap ({pixelArea} > {optionsValue.MaxPixelArea})");
            }

            bufferedSource.Position = 0;

            // Heavy decode + resize on the thread pool so the consumer's await chain stays cheap.
            // The linked CT propagates both caller cancellation AND the per-call timeout into the
            // synchronous Magick.NET work via Task.Run + the cancellationToken parameter.
            return await Task.Run(() => RunMagick(bufferedSource, request, optionsValue, linkedCts.Token), linkedCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Timeout fired (not caller-initiated cancellation). Distinguishing matters: a caller
            // cancellation should propagate so MassTransit re-queues; a timeout becomes a Failed
            // row + "timed-out" metric.
            return new ThumbnailGenerationOutcome.TimedOut(
                TimeSpan.FromSeconds(optionsValue.GenerationTimeoutSeconds));
        }
        finally
        {
            metrics.ThumbnailsInflight.Add(-1);
            metrics.RecordThumbnailDuration("webp", sw.Elapsed.TotalSeconds);
        }
    }

    private static ThumbnailGenerationOutcome RunMagick(
        Stream input,
        ThumbnailRequest request,
        ThumbnailOptions optionsValue,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var image = new MagickImage();
        try
        {
            image.Read(input);
        }
        catch (MagickException ex)
        {
            return new ThumbnailGenerationOutcome.SourceCorrupt($"decode: {ex.GetType().Name}");
        }

        cancellationToken.ThrowIfCancellationRequested();

        // 1. AutoOrient FIRST (STRG-337) — applies the EXIF Orientation tag and resets it to 1.
        //    Doing this BEFORE Resize ensures the resized pixels are upright.
        image.AutoOrient();

        // 2. Resize the longest edge to the target size; Greater = shrink-only (never enlarge).
        var edge = (uint)request.TargetEdgePixels;
        image.Resize(new MagickGeometry(edge, edge)
        {
            Greater = true,
            IgnoreAspectRatio = false,
        });

        cancellationToken.ThrowIfCancellationRequested();

        // 3. Letterbox onto a square white canvas (D10).
        image.BackgroundColor = MagickColors.White;
        image.Extent(
            new MagickGeometry(edge, edge),
            Gravity.Center,
            MagickColors.White);

        // Flatten any remaining transparency layers onto the white background. PNG inputs with
        // alpha would otherwise emit a transparent WebP, which defeats the letterbox-on-white
        // contract.
        image.Alpha(AlphaOption.Remove);

        // 4. Strip metadata (STRG-337) — privacy: GPS, camera serial, timestamps.
        image.Strip();

        // 5. Output as WebP at the configured quality.
        image.Format = MagickFormat.WebP;
        image.Quality = (uint)optionsValue.WebPQuality;

        var output = new MemoryStream();
        try
        {
            image.Write(output);
        }
        catch (MagickException ex)
        {
            output.Dispose();
            return new ThumbnailGenerationOutcome.SourceCorrupt($"encode: {ex.GetType().Name}");
        }

        output.Position = 0;

        return new ThumbnailGenerationOutcome.Success(
            output, (int)image.Width, (int)image.Height, "webp");
    }
}
