---
id: STRG-336
title: MagickNetImageThumbnailer — IThumbnailGenerator implementation for images
milestone: v0.2
priority: high
status: open
type: feature
labels: [thumbnails, phase-15, image-processing, magick-net]
depends_on: [STRG-330, STRG-334, STRG-335]
blocks: [STRG-337, STRG-338, STRG-343]
assigned_agent_type: feature-dev:code-architect
estimated_complexity: large
---

# STRG-336: MagickNetImageThumbnailer — IThumbnailGenerator implementation for images

## Summary

Implement the JPEG/PNG/WebP/GIF/BMP/TIFF image thumbnailer using `Magick.NET-Q8-x64` (Apache 2.0). Outputs WebP at the configured quality (default 82). Letterbox-to-fit on white. Streaming end-to-end — no full-buffer in memory. Resource safeguards (pixel cap + timeout + max-source-size) integrated.

## Background / Context

Decision **D5** (issue #52, ADR STRG-335) selected Magick.NET as the v1 image library:
- Apache 2.0 license — compatible with strg's license. ImageSharp's license change was the ship-stopper.
- `Magick.NET-Q8-x64` includes native libheif for HEIC support (STRG-337 builds on this).
- `MagickImageInfo` allows pixel-area probes BEFORE full decode — load-bearing for the bomb defence.

The thumbnailer sits behind `IThumbnailGenerator` so the consumer never branches on MIME and the choice stays swappable.

## Technical Specification

### Project reference

Add to `src/Strg.Infrastructure/Strg.Infrastructure.csproj`:

```xml
<PackageReference Include="Magick.NET-Q8-x64" Version="14.*" />
```

(Q8 sufficient for thumbnails; Q16 doubles memory for no perceptual gain at 256/512/1024 px.)

### File — `src/Strg.Infrastructure/Thumbnails/Generators/MagickNetImageThumbnailer.cs`

```csharp
public sealed class MagickNetImageThumbnailer(
    IOptions<ThumbnailOptions> options,
    StrgMetrics metrics,
    TimeProvider clock,
    ILogger<MagickNetImageThumbnailer> logger)
    : IThumbnailGenerator
{
    private static readonly HashSet<string> SupportedMimes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/webp",
        "image/gif", "image/bmp", "image/tiff",
        // HEIC handled in STRG-337 — same generator
    };

    public bool CanHandle(string mimeType, ReadOnlySpan<byte> magicBytes) =>
        SupportedMimes.Contains(mimeType);

    public async Task<ThumbnailGenerationOutcome> GenerateAsync(
        Stream source,
        ThumbnailRequest request,
        CancellationToken cancellationToken) { ... }
}
```

### Algorithm — `GenerateAsync`

```csharp
metrics.ThumbnailsInflight.Add(1);
var sw = Stopwatch.StartNew();
try
{
    using var timeoutCts = new CancellationTokenSource(
        TimeSpan.FromSeconds(options.Value.GenerationTimeoutSeconds));
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

    // 1. Probe header BEFORE full decode (D14 — pixel-cap pre-check).
    //    MagickImageInfo reads only the header; cheap.
    var probeBuffer = new MemoryStream();
    var probeBytes = Math.Min(64 * 1024, request.SourceSizeBytes);  // 64 KiB header probe
    await CopyHeadAsync(source, probeBuffer, probeBytes, linked.Token);
    probeBuffer.Position = 0;

    MagickImageInfo info;
    try { info = new MagickImageInfo(probeBuffer); }
    catch (MagickException ex)
    {
        return new ThumbnailGenerationOutcome.SourceCorrupt($"header-parse: {ex.GetType().Name}");
    }

    var pixelArea = (long)info.Width * info.Height;
    if (pixelArea > options.Value.MaxPixelArea)
    {
        return new ThumbnailGenerationOutcome.ResourceLimitExceeded(
            $"pixel-cap ({pixelArea} > {options.Value.MaxPixelArea})");
    }

    // 2. Rewind: probeBuffer holds the first N bytes; rest of `source` still has the tail.
    //    Concatenate them so MagickImage gets the full file.
    probeBuffer.Position = 0;
    using var concatenated = new ConcatenatingStream(probeBuffer, source);
    using var image = new MagickImage();
    await Task.Run(() => image.Read(concatenated), linked.Token);

    // 3. EXIF auto-orient + resize + strip metadata (full sequence in STRG-337).
    image.AutoOrient();
    image.Resize(new MagickGeometry((uint)request.TargetEdgePixels, (uint)request.TargetEdgePixels)
    {
        Greater = true,                    // shrink only, never enlarge
        IgnoreAspectRatio = false,
    });

    // 4. Letterbox to a square canvas with white background (D10).
    image.Extent(
        new MagickGeometry((uint)request.TargetEdgePixels, (uint)request.TargetEdgePixels)
        {
            X = 0, Y = 0,
        },
        Gravity.Center,
        new MagickColor("#ffffff"));

    image.Strip();                          // remove EXIF/XMP/IPTC (privacy — STRG-337)
    image.Format = MagickFormat.WebP;
    image.Quality = (uint)options.Value.WebPQuality;

    var output = new MemoryStream();
    await Task.Run(() => image.Write(output), linked.Token);
    output.Position = 0;

    return new ThumbnailGenerationOutcome.Success(
        output, (int)image.Width, (int)image.Height, "webp");
}
catch (OperationCanceledException) when (timeoutFired)
{
    return new ThumbnailGenerationOutcome.TimedOut(
        TimeSpan.FromSeconds(options.Value.GenerationTimeoutSeconds));
}
catch (MagickException ex)
{
    return new ThumbnailGenerationOutcome.SourceCorrupt($"decode: {ex.GetType().Name}");
}
finally
{
    metrics.ThumbnailsInflight.Add(-1);
    metrics.RecordThumbnailDuration("webp", sw.Elapsed.TotalSeconds);
}
```

### `ConcatenatingStream` helper

A small `Strg.Infrastructure/Internal/ConcatenatingStream.cs` that reads from stream A then stream B without buffering both fully. Read-only, no seek required by Magick.NET's `Read(Stream)`.

### `MemoryStream` for output

Acceptable for v1 — output thumbnails are bounded by `WebPQuality * area` (typically <500 KiB at 1024 px). The cost is dwarfed by the source image's decode buffer. Future optimization could pipe through `IStorageProvider.WriteAsync` directly, but the current `IStorageProvider` contract takes a `Stream`, which a `MemoryStream` satisfies.

## Acceptance Criteria

- [ ] `Magick.NET-Q8-x64` package added; `Strg.Architecture.Tests` confirms it lives in `Strg.Infrastructure`, NOT `Strg.Core`.
- [ ] `CanHandle` returns true for `image/jpeg`, `image/png`, `image/webp`, `image/gif`, `image/bmp`, `image/tiff`.
- [ ] Output is WebP at the configured quality.
- [ ] Letterbox to a square canvas on white background.
- [ ] EXIF/XMP/IPTC stripped from output (verify via output-image inspection in test).
- [ ] Pixel-area probe happens BEFORE full decode (`MagickImageInfo` on the first 64 KiB of the stream).
- [ ] Pixel-cap exceeded → `ResourceLimitExceeded` returned, no decode performed.
- [ ] Generation timeout → `TimedOut` returned (typed result, not an exception leaking out).
- [ ] Streaming end-to-end except for the bounded-size output `MemoryStream`.
- [ ] No `Thread.Sleep`, no `byte[] = await stream.ReadAllBytes()`.

## Test Cases

- **TC-001**: 600×400 JPEG → output is square 256×256 (or 512/1024 per variant), WebP, letterboxed white.
- **TC-002**: 100 MP PNG bomb header → `ResourceLimitExceeded("pixel-cap ...")` returned WITHOUT decoding the body.
- **TC-003**: Corrupt JPEG (truncated body) → `SourceCorrupt(...)` returned, not an exception bubbling up.
- **TC-004**: 1×1 transparent PNG → `Success(...)` with white background visible (transparency flattened).
- **TC-005**: Animated GIF (3 frames) → first frame only, `Success(...)`.
- **TC-006**: TIFF with embedded EXIF → output WebP has no EXIF/XMP/IPTC chunks (verified by re-decoding output and checking metadata).

## Implementation Tasks

- [ ] Add `Magick.NET-Q8-x64` package reference.
- [ ] Implement `MagickNetImageThumbnailer`.
- [ ] Implement `ConcatenatingStream` helper (or use an existing one if `Strg.Infrastructure` has one).
- [ ] Register in `Program.cs`: `services.AddSingleton<IThumbnailGenerator, MagickNetImageThumbnailer>()`.
- [ ] Unit/integration tests under `tests/Strg.Integration.Tests/Thumbnails/ImageGeneratorTests.cs`.

## Security Review Checklist

- [ ] Pixel-cap check uses `MagickImageInfo` (header-only probe) BEFORE decode — bomb-resistant.
- [ ] Output `Strip()` removes EXIF/XMP/IPTC including GPS / camera serial / timestamps (privacy).
- [ ] Generator runs in `Task.Run` with a linked CTS — caller cancellation AND timeout both honored.
- [ ] No `MagickImage.Read(string path)` — always `Read(Stream)` so no path injection vector.
- [ ] `MagickException.Message` is NOT exposed to callers — only `ex.GetType().Name` reaches the typed result.
- [ ] No `MagickReadSettings.Density` from user input (would allow PDF rasterization at attacker-chosen DPI; not relevant here since this is the IMAGE generator, but verify).

## Code Review Checklist

- [ ] `using` for every `IDisposable` (`MagickImage`, `MemoryStream`, `CancellationTokenSource`).
- [ ] Output `MemoryStream` ownership transferred to the caller (consumer owns disposal via `await using` in STRG-331).
- [ ] No `dynamic`, no `unsafe`.
- [ ] CancellationToken parameter named `cancellationToken`.

## Definition of Done

- [ ] All acceptance criteria green.
- [ ] Architecture test (`Strg.Architecture.Tests`) confirms Magick.NET is NOT referenced from `Strg.Core`.
- [ ] Test image fixtures (small PNG, JPEG, animated GIF, TIFF) present under `tests/Strg.Integration.Tests/Thumbnails/Fixtures/`.
- [ ] Linux-x64 + Linux-arm64 verified (Magick.NET-Q8-x64 has separate native bundles — confirm Docker base image carries libheif/libwebp).
