---
id: STRG-337
title: HEIC/HEIF + EXIF orientation + metadata stripping
milestone: v0.2
priority: medium
status: open
type: feature
labels: [thumbnails, phase-15, heic, exif, privacy]
depends_on: [STRG-336]
blocks: [STRG-343]
assigned_agent_type: feature-dev:code-architect
estimated_complexity: medium
---

# STRG-337: HEIC/HEIF + EXIF orientation + metadata stripping

## Summary

Extend `MagickNetImageThumbnailer` to support HEIC/HEIF input, apply EXIF orientation BEFORE resize, and strip EXIF/XMP/IPTC from the output (privacy: GPS, camera serial, timestamps).

## Background / Context

Issue #52's STRG-337 bundles three concerns that are tightly coupled:

1. **HEIC support** — modern iOS default for camera output. Magick.NET-Q8-x64 ships with native libheif; no extra package, but the Docker base image must carry the libheif system library.
2. **EXIF orientation** — JPEG/HEIC frequently carry an `Orientation` tag (1–8). If we resize WITHOUT first auto-orienting, a portrait phone photo lands upside-down or sideways at the thumbnail size. The fix is `image.AutoOrient()` BEFORE `Resize()`.
3. **Metadata stripping** — EXIF carries GPS coordinates, camera serial numbers, and timestamps. Embedding any of these into a thumbnail that's served to share-link recipients is a serious privacy leak. Strip via `image.Strip()` BEFORE writing the output.

These concerns are bundled because they all live in the same `GenerateAsync` flow, and the test fixtures overlap (orientation-tagged JPEG + GPS-tagged HEIC + EXIF-rich TIFF).

## Technical Specification

### HEIC support — STRG-336 update

In `MagickNetImageThumbnailer.SupportedMimes`:

```csharp
private static readonly HashSet<string> SupportedMimes = new(StringComparer.OrdinalIgnoreCase)
{
    "image/jpeg", "image/png", "image/webp",
    "image/gif", "image/bmp", "image/tiff",
    "image/heic", "image/heif",                // STRG-337
};
```

`MimeSniffer` already detects HEIC via the `ftyp` brand list (STRG-330).

### Docker base-image consequence

The Docker base image (defined in `Dockerfile`) MUST install:

```dockerfile
RUN apt-get update && apt-get install -y \
    libheif1 libheif-plugin-aomdec libheif-plugin-libde265 \
    && rm -rf /var/lib/apt/lists/*
```

(Or equivalent for Alpine if the project uses a slim base.)

If libheif is missing at runtime, Magick.NET HEIC reads throw `MagickMissingDelegateErrorException`. The thumbnailer's outer `catch (MagickException)` already maps this to `SourceCorrupt(...)`, so the failure mode is graceful — but the operator-facing log MUST surface the missing-delegate hint clearly.

### Orientation — order of operations matters

```csharp
image.Read(concatenated);

// 1. AutoOrient FIRST — must run before any geometry transform.
image.AutoOrient();          // applies the EXIF Orientation tag, rotates pixels, sets tag to 1.

// 2. Resize the orientation-corrected pixels.
image.Resize(new MagickGeometry(...) { Greater = true, IgnoreAspectRatio = false });

// 3. Letterbox.
image.Extent(...);

// 4. Strip metadata BEFORE write (so the orientation correction we just applied doesn't get
//    "undone" by a leftover Orientation tag in some viewer's interpretation).
image.Strip();

// 5. Write.
image.Format = MagickFormat.WebP;
image.Quality = ...;
image.Write(output);
```

### Metadata stripping — what's removed

`image.Strip()` removes:

- EXIF (GPS, camera serial, timestamps, exposure, lens info)
- XMP (any embedded metadata)
- IPTC (caption, copyright, keywords)
- ICC profiles (color management)

For the THUMBNAIL output, dropping the ICC profile is acceptable — viewers default to sRGB which is what we resize into anyway. For the SOURCE file, we never modify it; this strip is on the in-memory thumbnail copy only.

### What we do NOT do

**We do not extract EXIF for AI auto-tagging here.** That's a separate concern (issue IF-01, future). AI auto-tagging will run on a different consumer path that reads the unmodified source — it does NOT depend on the thumbnail subsystem and MUST NOT inherit our `Strip()` discipline.

## Acceptance Criteria

- [ ] `image/heic` and `image/heif` added to `SupportedMimes`.
- [ ] HEIC input produces a valid WebP thumbnail.
- [ ] `image.AutoOrient()` is called BEFORE `Resize()` and `Extent()`.
- [ ] `image.Strip()` is called BEFORE `Write()`.
- [ ] Output WebP has no EXIF, XMP, IPTC, or ICC chunks.
- [ ] Missing-delegate error (libheif unavailable) maps cleanly to `SourceCorrupt(...)` — does not crash.
- [ ] `Dockerfile` (if present) installs libheif and its plugins.

## Test Cases

- **TC-001**: Portrait-orientation JPEG (EXIF Orientation = 6, "rotate 90 CW") → output is correctly rotated; verify by content inspection (top-of-image pixel is in the expected position).
- **TC-002**: HEIC sample with GPS coordinates → output WebP has no EXIF chunk (re-decode and assert).
- **TC-003**: TIFF with full EXIF + IPTC + XMP → output WebP has none of these chunks.
- **TC-004**: HEIC on a system without libheif → `SourceCorrupt(...)` returned, no crash, error logged with hint text "libheif missing or HEIC delegate not available".
- **TC-005**: 24-bit RGB JPEG with embedded ICC profile → output renders correctly under sRGB (verify by visual diff against a reference).

## Implementation Tasks

- [ ] Update `SupportedMimes` to include HEIC/HEIF.
- [ ] Confirm `AutoOrient()` is invoked before `Resize()`/`Extent()` (STRG-336 already calls it; verify ordering).
- [ ] Confirm `Strip()` is invoked before `Write()`.
- [ ] Update `Dockerfile` to install libheif + plugins.
- [ ] Add test fixtures: portrait-orientation JPEG (EXIF=6), HEIC with GPS, EXIF-rich TIFF.
- [ ] Tests under `tests/Strg.Integration.Tests/Thumbnails/ImageGeneratorTests.cs`.

## Security Review Checklist

- [ ] `Strip()` runs unconditionally — no flag that lets EXIF survive into thumbnails.
- [ ] Missing-delegate logging does NOT leak file content or paths.
- [ ] HEIC processing is bounded by the same pixel-cap and timeout as other formats (STRG-338 — same `GenerateAsync` path).
- [ ] No re-encoding the source file's metadata into the thumbnail (re-orient changes pixels, not metadata).
- [ ] AI auto-tagging path (future) is documented as needing the unmodified source — this issue does NOT change that.

## Code Review Checklist

- [ ] `AutoOrient()` is BEFORE `Resize()` (test TC-001 catches this).
- [ ] `Strip()` is BEFORE `Write()`.
- [ ] No conditional metadata preservation.
- [ ] Docker base image documentation updated (if `Dockerfile` is present in the repo at this point).

## Definition of Done

- [ ] All acceptance criteria green.
- [ ] Test fixtures committed under `tests/Strg.Integration.Tests/Thumbnails/Fixtures/`.
- [ ] HEIC support verified on Linux x64 in CI (or documented as a known gap if CI base image lacks libheif).
