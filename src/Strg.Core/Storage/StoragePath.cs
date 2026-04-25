namespace Strg.Core.Storage;

public readonly struct StoragePath : IEquatable<StoragePath>
{
    public string Value { get; }

    private StoragePath(string value) => Value = value;

    public static StoragePath Parse(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        // Decode BEFORE the null-byte check so `%00` cannot smuggle a NUL past the filter. A
        // raw check first would accept "legal%00.txt" as clean, then materialize the null
        // after decoding — classic check-then-decode ordering bug.
        var decoded = Uri.UnescapeDataString(raw);
        if (decoded.Contains('\0'))
        {
            throw new StoragePathException("Null byte in path");
        }

        // Collapse backslashes to forward slashes BEFORE traversal detection so UNC-style
        // inputs (`\\server\share`) and Windows-style separators can't route around the
        // `//` / leading-slash rules. Without this, `ContainsTraversal` sees backslashes as
        // literal characters and the traversal check passes.
        var normalized = decoded.Replace('\\', '/');

        // Reject empty / whitespace-only paths AFTER decoding so encoded variants like
        // `%20` (single space) are caught alongside literal "" and "   ". Without this gate,
        // the rest of the pipeline accepts the input and returns a StoragePath with
        // Value = "" or " ", which downstream providers interpret ambiguously (root-of-drive
        // vs. error) — the late-binding failure mode the parse-time contract exists to prevent.
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new StoragePathException("Path must not be empty or whitespace");
        }

        if (ContainsTraversal(normalized))
        {
            throw new StoragePathException($"Path traversal detected: {raw}");
        }

        if (IsReservedName(normalized))
        {
            throw new StoragePathException($"Reserved path name: {raw}");
        }

        return new StoragePath(Normalize(normalized));
    }

    public static bool TryParse(string raw, out StoragePath path)
    {
        try
        {
            path = Parse(raw);
            return true;
        }
        catch (StoragePathException)
        {
            path = default;
            return false;
        }
    }

    private static bool ContainsTraversal(string p) =>
        p.Contains("..") || p.Contains("//") || p.StartsWith('/');

    private static string Normalize(string p) =>
        p.Replace('\\', '/').TrimStart('/').TrimEnd('/');

    private static readonly HashSet<string> WindowsReserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9",
        "LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9"
    };

    // Span-based lookup so IsReservedName can probe the segment without materialising
    // substrings. Without this, p.Split('/')[^1].Split('.')[0] allocates two arrays and
    // their substrings on every successful Parse — ~30+ bytes per call on a 2-segment
    // path, which is forbidden on the hot path that storage providers, WebDAV, and the
    // CQRS handlers all funnel through. Pinned by Parse_HotPath_DoesNotAllocate.
    private static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> WindowsReservedLookup =
        WindowsReserved.GetAlternateLookup<ReadOnlySpan<char>>();

    private static bool IsReservedName(string p)
    {
        // Walk EVERY segment, not just the last. Windows treats CON/PRN/AUX/NUL/COMn/LPTn
        // as reserved at any path level — `archives/CON/file.txt` is an OS-invalid path on
        // a Windows host even though `file.txt` is fine. The earlier last-segment-only
        // implementation let the directory variant slip through. Manual index walk over
        // ReadOnlySpan<char> avoids the per-call allocation that Split would reintroduce
        // (pinned by Parse_HotPath_DoesNotAllocate).
        var span = p.AsSpan();
        var start = 0;
        for (var i = 0; i <= span.Length; i++)
        {
            if (i != span.Length && span[i] != '/')
            {
                continue;
            }
            if (i > start)
            {
                var segment = span[start..i];
                var dotIdx = segment.IndexOf('.');
                var nameWithoutExt = dotIdx >= 0 ? segment[..dotIdx] : segment;
                if (WindowsReservedLookup.Contains(nameWithoutExt))
                {
                    return true;
                }
            }
            start = i + 1;
        }
        return false;
    }

    // `default(StoragePath)` has `Value == null` because the private constructor is never
    // called on the zero-initialised struct. ToString / GetHashCode must guard against
    // that — otherwise a failed `TryParse` whose `out` value lands in a HashSet key,
    // Dictionary key, log formatter, or JSON serializer would NRE far from the parse site.
    public override string ToString() => Value ?? string.Empty;
    public bool Equals(StoragePath other) => string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);
    public override bool Equals(object? obj) => obj is StoragePath other && Equals(other);
    public override int GetHashCode() => Value is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(Value);
    public static bool operator ==(StoragePath left, StoragePath right) => left.Equals(right);
    public static bool operator !=(StoragePath left, StoragePath right) => !left.Equals(right);
}
