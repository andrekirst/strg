namespace Strg.Core.Services;

/// <summary>
/// Whitelist-only magic-byte sniffer that maps the first few bytes of a file to a canonical
/// IANA MIME type. NOT a libmagic substitute — the whitelist is intentionally narrow:
/// formats we generate thumbnails for in v1 (JPEG, PNG, GIF, WebP, HEIC) plus PDF (no v1
/// generator, but the registry self-declares miss → <c>Unsupported</c>; Phase 16 ships the
/// PDF generator without re-touching this file).
///
/// <para><b>Why sniff at thumbnail time.</b> <see cref="Domain.FileItem.MimeType"/> is
/// client-provided at upload — the client can lie. Sniffing here ensures the generator we
/// resolve is actually appropriate for the bytes we're about to feed it (D12).</para>
///
/// <para>Truncated input (less than the format's signature length) returns <c>null</c>;
/// callers handle this as "unknown MIME → write Unsupported row".</para>
/// </summary>
public static class MimeSniffer
{
    /// <summary>Inspect <paramref name="head"/> (first ~16 bytes is sufficient) and return the canonical MIME, or <c>null</c>.</summary>
    public static string? Detect(ReadOnlySpan<byte> head)
    {
        // JPEG: FF D8 FF
        if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF)
        {
            return "image/jpeg";
        }

        // PNG: 89 50 4E 47 0D 0A 1A 0A
        if (head.Length >= 8 &&
            head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47 &&
            head[4] == 0x0D && head[5] == 0x0A && head[6] == 0x1A && head[7] == 0x0A)
        {
            return "image/png";
        }

        // GIF: "GIF87a" or "GIF89a"
        if (head.Length >= 6 &&
            head[0] == 0x47 && head[1] == 0x49 && head[2] == 0x46 && head[3] == 0x38 &&
            (head[4] == 0x37 || head[4] == 0x39) && head[5] == 0x61)
        {
            return "image/gif";
        }

        // WebP: "RIFF" .... "WEBP" (offset 8)
        if (head.Length >= 12 &&
            head[0] == 0x52 && head[1] == 0x49 && head[2] == 0x46 && head[3] == 0x46 &&
            head[8] == 0x57 && head[9] == 0x45 && head[10] == 0x42 && head[11] == 0x50)
        {
            return "image/webp";
        }

        // PDF: "%PDF-"
        if (head.Length >= 5 &&
            head[0] == 0x25 && head[1] == 0x50 && head[2] == 0x44 && head[3] == 0x46 && head[4] == 0x2D)
        {
            return "application/pdf";
        }

        // HEIC/HEIF: bytes 4..7 = "ftyp", brand at 8..11 in the recognised set.
        if (head.Length >= 12 &&
            head[4] == 0x66 && head[5] == 0x74 && head[6] == 0x79 && head[7] == 0x70)
        {
            // ASCII brand at offset 8, 4 chars.
            var brand = (char)head[8] + "" + (char)head[9] + (char)head[10] + (char)head[11];
            return brand switch
            {
                "heic" or "heix" or "heim" or "heis" or "hevc" or "hevx" => "image/heic",
                "mif1" or "msf1" => "image/heif",
                _ => null,
            };
        }

        return null;
    }
}
