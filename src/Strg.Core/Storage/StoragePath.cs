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

    private static bool IsReservedName(string p)
    {
        var segment = p.Split('/')[^1]; // check only the last segment
        var nameWithoutExt = segment.Split('.')[0]; // strip extension
        return WindowsReserved.Contains(nameWithoutExt);
    }

    public override string ToString() => Value;
    public bool Equals(StoragePath other) => string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);
    public override bool Equals(object? obj) => obj is StoragePath other && Equals(other);
    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Value);
    public static bool operator ==(StoragePath left, StoragePath right) => left.Equals(right);
    public static bool operator !=(StoragePath left, StoragePath right) => !left.Equals(right);
}
